using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Axiom.Contracts;

namespace Axiom.Infrastructure;

// Explicit upstream queries never use the system hosts file for target domains.
public sealed class HostsDnsResolver
{
    public async Task<IReadOnlyDictionary<string, IPAddress[]>> ResolveAsync(HostsSettings settings, CancellationToken token)
    {
        var useProxy = settings.Transport == "doh" && settings.UseSystemProxy;
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = useProxy, ConnectTimeout = TimeSpan.FromSeconds(5) };
        // Proxy routing and DNS belong to the system HTTP transport. Direct mode uses only the explicit bootstrap.
        if (!useProxy) handler.ConnectCallback = async (context, cancellation) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] :
                await ResolveDomainAsync(host, "both", (name, type, ct) => QueryDnsAsync(HostsCapabilities.Endpoint(settings.Bootstrap), name, type, ct), cancellation);
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellation); return new NetworkStream(socket, ownsSocket: true); }
                catch (SocketException) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new IOException("无法连接DoH服务器。");
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        async Task<byte[]> Query(string name, ushort type, CancellationToken ct)
        {
            if (settings.Transport == "dns") return await QueryDnsAsync(HostsCapabilities.Endpoint(settings.Server), name, type, ct);
            var query = CreateQuery(name, type);
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.DohEndpoint) { Content = new ByteArrayContent(query) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType != "application/dns-message" || response.Content.Headers.ContentLength > 65535)
                throw new InvalidDataException("DoH响应格式或大小无效。");
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var data = new byte[65536]; var count = 0;
            while (count < data.Length)
            {
                var read = await body.ReadAsync(data.AsMemory(count), timeout.Token); if (read == 0) break; count += read;
            }
            if (count == data.Length) throw new InvalidDataException("DoH响应过大。");
            var result = data[..count]; ValidateHeader(query, result, allowTruncated: false); return result;
        }
        using var workers = new SemaphoreSlim(4);
        var resolved = await Task.WhenAll(settings.Domains.Select(async domain =>
        {
            await workers.WaitAsync(token);
            try { return new KeyValuePair<string, IPAddress[]>(domain, await ResolveDomainAsync(domain, settings.Family, Query, token)); }
            catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            { throw new IOException($"{domain} 解析失败，hosts保持原样：{error.Message}", error); }
            finally { workers.Release(); }
        }));
        return resolved.ToDictionary(p => p.Key, p => p.Value);
    }

    private static async Task<IPAddress[]> ResolveDomainAsync(string domain, string family, Func<string, ushort, CancellationToken, Task<byte[]>> query, CancellationToken token)
    {
        var results = new List<IPAddress>();
        foreach (ushort type in family == "ipv4" ? new ushort[] { 1 } : family == "ipv6" ? [28] : [1, 28])
        {
            var name = HostsCapabilities.Domain(domain); var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var hop = 0; hop < 8; hop++)
            {
                if (!visited.Add(name)) throw new InvalidDataException("DNS别名循环。");
                var packet = await query(name, type, token);
                var answer = ReadAnswer(packet, name, type); results.AddRange(answer.Addresses);
                if (answer.Addresses.Length != 0 || answer.Canonical == name) break;
                name = answer.Canonical;
                if (hop == 7) throw new InvalidDataException("DNS别名链过长。");
            }
        }
        if (results.Count == 0) throw new IOException("DNS未返回所选类型的IP地址。");
        return [.. results.Distinct().OrderBy(a => a.AddressFamily).ThenBy(a => a.ToString(), StringComparer.Ordinal)];
    }
    public static byte[] CreateQuery(string domain, ushort type)
    {
        if (type is not (1 or 28)) throw new ArgumentException("不支持的DNS查询类型。");
        using var buffer = new MemoryStream(); var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)RandomNumberGenerator.GetInt32(65536)); header[2] = 1; header[5] = 1; buffer.Write(header);
        foreach (var label in HostsCapabilities.Domain(domain).Split('.')) { buffer.WriteByte((byte)label.Length); buffer.Write(Encoding.ASCII.GetBytes(label)); }
        buffer.WriteByte(0); buffer.WriteByte(0); buffer.WriteByte((byte)type); buffer.WriteByte(0); buffer.WriteByte(1); return buffer.ToArray();
    }
    private static void ValidateHeader(byte[] query, byte[] data, bool allowTruncated)
    {
        if (data.Length < 12 || data[0] != query[0] || data[1] != query[1] || (data[2] & 0xf8) != 0x80 || (data[3] & 15) != 0 ||
            (!allowTruncated && (data[2] & 2) != 0)) throw new InvalidDataException("DNS返回错误、截断或不匹配的响应。");
        var offset = 12; var question = ReadName(data, ref offset); var queryOffset = 12; var expected = ReadName(query, ref queryOffset);
        if (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2)) != 1 || question != expected || offset + 4 > data.Length || !data.AsSpan(offset, 4).SequenceEqual(query.AsSpan(queryOffset, 4)))
            throw new InvalidDataException("DNS响应问题不匹配。");
    }
    private static async Task<byte[]> QueryDnsAsync(IPEndPoint server, string name, ushort type, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var query = CreateQuery(name, type);
        using var udp = new UdpClient(server.AddressFamily); udp.Connect(server);
        await udp.SendAsync(query, timeout.Token); var received = await udp.ReceiveAsync(timeout.Token); var data = received.Buffer;
        ValidateHeader(query, data, allowTruncated: true);
        if ((data[2] & 2) == 0) return data;
        using var tcp = new TcpClient(server.AddressFamily); await tcp.ConnectAsync(server.Address, server.Port, timeout.Token);
        await using var stream = tcp.GetStream(); var size = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)query.Length);
        await stream.WriteAsync(size, timeout.Token); await stream.WriteAsync(query, timeout.Token);
        await stream.ReadExactlyAsync(size, timeout.Token); data = new byte[BinaryPrimitives.ReadUInt16BigEndian(size)];
        await stream.ReadExactlyAsync(data, timeout.Token); ValidateHeader(query, data, allowTruncated: false); return data;
    }
    public static (IPAddress[] Addresses, string Canonical) ReadAnswer(byte[] data, string domain, ushort type)
    {
        if (data.Length < 12 || (data[2] & 0xfa) != 0x80 || (data[3] & 15) != 0 || BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4, 2)) != 1)
            throw new InvalidDataException("DNS响应无效。");
        var offset = 12; var question = ReadName(data, ref offset);
        if (offset + 4 > data.Length || question != HostsCapabilities.Domain(domain) || BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2)) != type || data[offset + 2] != 0 || data[offset + 3] != 1)
            throw new InvalidDataException("DNS响应问题无效。");
        offset += 4;
        var count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(6, 2));
        var addresses = new List<(string Name, IPAddress Address)>(); var aliases = new Dictionary<string, string>();
        for (var i = 0; i < count; i++)
        {
            var owner = ReadName(data, ref offset);
            if (offset + 10 > data.Length) throw new InvalidDataException("DNS记录不完整。");
            var kind = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2)); var cls = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2, 2));
            var size = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 8, 2)); offset += 10;
            if (offset + size > data.Length) throw new InvalidDataException("DNS记录长度无效。");
            if (cls == 1 && kind == type && size == (type == 1 ? 4 : 16)) addresses.Add((owner, new(data.AsSpan(offset, size))));
            if (cls == 1 && kind == 5)
            {
                var pointer = offset; var alias = ReadName(data, ref pointer);
                if (pointer != offset + size || aliases.TryGetValue(owner, out var old) && old != alias) throw new InvalidDataException("DNS别名记录无效。");
                aliases[owner] = alias;
            }
            offset += size;
        }
        var canonical = question; var seen = new HashSet<string>();
        while (aliases.TryGetValue(canonical, out var next))
        {
            if (!seen.Add(canonical) || seen.Count > 8) throw new InvalidDataException("DNS别名循环或过长。");
            canonical = next;
        }
        return ([.. addresses.Where(a => a.Name == canonical).Select(a => a.Address).Distinct()], canonical);
    }
    private static string ReadName(byte[] data, ref int offset)
    {
        var cursor = offset; var jumped = false; var labels = new List<string>(); var visited = new HashSet<int>(); var length = 0;
        while (true)
        {
            if (cursor >= data.Length || !visited.Add(cursor)) throw new InvalidDataException("DNS名称不完整或指针循环。");
            var count = data[cursor++];
            if ((count & 0xc0) == 0xc0)
            {
                if (cursor >= data.Length) throw new InvalidDataException("DNS名称指针不完整。");
                var target = ((count & 63) << 8) | data[cursor++]; if (!jumped) offset = cursor; jumped = true; cursor = target; continue;
            }
            if (count == 0) { if (!jumped) offset = cursor; break; }
            if (count > 63 || cursor + count > data.Length || (length += count + 1) > 254) throw new InvalidDataException("DNS名称长度无效。");
            labels.Add(Encoding.ASCII.GetString(data, cursor, count).ToLowerInvariant()); cursor += count;
        }
        return string.Join('.', labels);
    }
}
