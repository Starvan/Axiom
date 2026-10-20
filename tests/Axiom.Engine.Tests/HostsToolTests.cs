using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.ServiceHost;

namespace Axiom.Engine.Tests;

public sealed class HostsToolTests
{
    private static TaskInvocation Invocation(string domains = "example.test", string family = "ipv4", string transport = "dns", string server = "127.0.0.1") =>
        new(HostsCapabilities.Update, 1, HostsCapabilities.Descriptor.Fields.ToImmutableDictionary(f => f.Id,
            f => f.Id switch { "domains" => domains, "family" => family, "transport" => transport, "server" => server, _ => f.DefaultValue! }));
    private static Dictionary<string, IPAddress[]> Addresses(string domain = "example.test", string address = "192.0.2.1") => new() { [domain] = [IPAddress.Parse(address)] };
    [Fact]
    public void SystemProxyDefaultsOffForLegacyAndNewSettingsAndRequiresAnExplicitBoolean()
    {
        var action = Invocation(transport: "doh");
        Assert.False(HostsCapabilities.Validate(action).UseSystemProxy);
        Assert.False(HostsCapabilities.Validate(action with { Parameters = action.Parameters.Remove("useSystemProxy") }).UseSystemProxy);
        Assert.True(HostsCapabilities.Validate(action with { Parameters = action.Parameters.SetItem("useSystemProxy", "true") }).UseSystemProxy);
        foreach (var value in new[] { "yes", "1", "TRUE" })
            Assert.Throws<ArgumentException>(() => HostsCapabilities.Validate(action with { Parameters = action.Parameters.SetItem("useSystemProxy", value) }));
        Assert.Throws<ArgumentException>(() => HostsCapabilities.Validate(action with { Parameters = action.Parameters.SetItem("proxyUrl", "http://proxy.test") }));
    }
    [Fact]
    public async Task ProductionCatalogAndNineHostsTasksFitTheExistingPipeLimitWithUnicodeRoundTrip()
    {
        var modules = BuiltInAutomationPlugins.Create(); var compiler = new Axiom.Engine.AutomationTaskCompiler(supportsCompositeDetection: true, supportsRunConditions: true);
        foreach (var module in modules) { foreach (var d in module.Detections) compiler.Register(d); foreach (var a in module.Actions) compiler.Register(a); }
        compiler.Register(new HdrTaskAction());
        var detection = new TaskInvocation("process.running", 2, ImmutableDictionary<string, string>.Empty.Add("processNames",
            string.Join('\n', Enumerable.Range(1, 128).Select(i => $"SyntheticApplication{i}.exe"))));
        var tasks = Enumerable.Range(1, 9).Select(i => new AutomationTask(Guid.NewGuid().ToString("D"), 1, new string('界', 120), true,
            detection, Invocation("example.test\nother.test", transport: "doh"))).ToImmutableArray();
        var snapshot = new AutomationSnapshot(tasks, compiler.Catalog, [.. modules.SelectMany(m => m.Combinations)], true, "等待条件", "", DateTimeOffset.UtcNow, []);
        using var wire = new MemoryStream();
        await AutomationProtocol.WriteAsync(wire, new(1, "snapshot", "synthetic", Snapshot: snapshot), default);
        Assert.True(wire.Length - 4 <= AutomationProtocol.MaximumFrame);
        var payload = Encoding.UTF8.GetString(wire.ToArray()[4..]); Assert.Contains("使用系统代理", payload);
        wire.Position = 0; var read = await AutomationProtocol.ReadAsync(wire, default);
        Assert.Equal(9, read.Snapshot!.Tasks.Length); Assert.Equal(new string('界', 120), read.Snapshot.Tasks[0].Title);
        Assert.Equal(detection.Parameters["processNames"], read.Snapshot.Tasks[0].Detection.Parameters["processNames"]);
        Assert.Equal(snapshot.Capabilities.Length, read.Snapshot.Capabilities.Length);
        Assert.Equal(snapshot.Combinations.Length, read.Snapshot.Combinations.Length);
    }
    [Fact(Timeout = 15000)]
    public async Task SystemProxyUsesConnectForDohAndDirectModeIgnoresIt()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var proxyListener = new TcpListener(IPAddress.Loopback, 0); proxyListener.Start();
        using var directListener = new TcpListener(IPAddress.Loopback, 0); directListener.Start();
        var proxyEndpoint = (IPEndPoint)proxyListener.LocalEndpoint; var directEndpoint = (IPEndPoint)directListener.LocalEndpoint;
        var previous = HttpClient.DefaultProxy;
        try
        {
            HttpClient.DefaultProxy = new WebProxy(new Uri($"http://127.0.0.1:{proxyEndpoint.Port}"));
            var proxyServer = Task.Run(async () =>
            {
                using var client = await proxyListener.AcceptTcpClientAsync(limit.Token); await using var stream = client.GetStream();
                var bytes = new List<byte>(); var buffer = new byte[1];
                while (bytes.Count < 4096)
                {
                    await stream.ReadExactlyAsync(buffer, limit.Token); bytes.Add(buffer[0]);
                    if (bytes.Count >= 4 && bytes.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                var header = Encoding.ASCII.GetString(bytes.ToArray());
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Synthetic proxy\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), limit.Token);
                return header;
            }, limit.Token);
            var action = Invocation(transport: "doh");
            action = action with { Parameters = action.Parameters.SetItem("useSystemProxy", "true").SetItem("doh", "https://doh.synthetic.test/dns-query").SetItem("bootstrap", "192.0.2.53") };
            await Assert.ThrowsAsync<IOException>(() => new HostsDnsResolver().ResolveAsync(HostsCapabilities.Validate(action), limit.Token));
            Assert.StartsWith("CONNECT doh.synthetic.test:443 HTTP/1.1\r\n", await proxyServer);
            var directServer = Task.Run(async () =>
            {
                using var client = await directListener.AcceptTcpClientAsync(limit.Token); await using var stream = client.GetStream();
                var first = new byte[1]; await stream.ReadExactlyAsync(first, limit.Token); return first[0];
            }, limit.Token);
            action = action with { Parameters = action.Parameters.SetItem("useSystemProxy", "false").SetItem("doh", $"https://127.0.0.1:{directEndpoint.Port}/dns-query") };
            await Assert.ThrowsAsync<IOException>(() => new HostsDnsResolver().ResolveAsync(HostsCapabilities.Validate(action), limit.Token));
            Assert.Equal(22, await directServer); Assert.False(proxyListener.Pending());
        }
        finally { HttpClient.DefaultProxy = previous; }
    }
    private static byte[] Answer(byte[] query, string address = "192.0.2.1")
    {
        var ip = IPAddress.Parse(address).GetAddressBytes(); var result = new byte[query.Length + 12 + ip.Length]; query.CopyTo(result, 0);
        result[2] = 0x81; result[3] = 0x80; result[7] = 1; var n = query.Length;
        result[n] = 0xc0; result[n + 1] = 12; result[n + 3] = (byte)(ip.Length == 4 ? 1 : 28); result[n + 5] = 1;
        result[n + 11] = (byte)ip.Length; ip.CopyTo(result, n + 12); return result;
    }
    [Fact]
    public void ValidationCanonicalizesInternationalDomainsAndRejectsInjectionAndUnknownFields()
    {
        var settings = HostsCapabilities.Validate(Invocation("EXAMPLE.TEST.\nexample.test\n例子.测试"));
        Assert.Equal(["example.test", "xn--fsqu00a.xn--0zwm56d"], settings.Domains);
        foreach (var bad in new[] { "https://example.test", "example.test/path", "*.example.test", "127.0.0.1", "example.test:80", "a\t.test", "-a.test", "a..test" })
            Assert.Throws<ArgumentException>(() => HostsCapabilities.Validate(Invocation(bad)));
        foreach (var (key, value) in new[] { ("extra", "x"), ("transport", "shell"), ("server", "example.test"), ("bootstrap", "bad"), ("family", "any"), ("doh", "http://example.test/dns-query"), ("doh", "https://user@example.test/dns-query") })
            Assert.Throws<ArgumentException>(() => HostsCapabilities.Validate(Invocation() with { Parameters = Invocation().Parameters.SetItem(key, value) }));
        Assert.Equal(5300, HostsCapabilities.Endpoint("[::1]:5300").Port);
        Assert.Equal(53, HostsCapabilities.Endpoint("::1").Port);
        Assert.Throws<ArgumentException>(() => HostsCapabilities.Endpoint("127.0.0.1:0"));
        Assert.Throws<ArgumentException>(() => HostsCapabilities.Validate(Invocation(string.Join('\n', Enumerable.Range(0, 17).Select(i => $"a{i}.test")))));
    }
    [Fact]
    public void EveryRegisteredDetectionIncludingMonthlyCanDriveHostsAndTargetsAreSerialized()
    {
        var modules = BuiltInAutomationPlugins.Create(); var compiler = new Axiom.Engine.AutomationTaskCompiler(supportsCompositeDetection: true, supportsRunConditions: true);
        foreach (var d in modules.SelectMany(m => m.Detections)) compiler.Register(d);
        foreach (var a in modules.SelectMany(m => m.Actions)) compiler.Register(a);
        foreach (var c in modules.SelectMany(m => m.Combinations)) compiler.RegisterCombination(c);
        Assert.All(modules.SelectMany(m => m.Detections), d => Assert.Contains(new(d.Descriptor.Id, HostsCapabilities.Update), modules.SelectMany(m => m.Combinations)));
        var action = new HostsTaskAction(); var key = action.Compile(Invocation(), ResourceScope.Session("synthetic", 1)).Key;
        Assert.Equal("system-hosts", key.ResourceId);
        var binding = Assert.Single(new HostsPlugin().Bindings); Assert.Equal(ExecutionLocation.Service, binding.Descriptor.Location);
        Assert.Equal(ConcurrencyScope.Capability, binding.Descriptor.Concurrency); Assert.Equal(RetryCategory.Never, binding.Descriptor.Retry);
    }
    [Fact]
    public void ManagedUpdatesAreIdempotentPreserveOtherContentAndReplaceOnlyRequestedDomains()
    {
        const string original = "# comment\r\n127.0.0.1\tlocalhost # local\r\n";
        var first = HostsFileUpdater.Merge(original, Addresses());
        Assert.StartsWith(original, first); Assert.Equal(first, HostsFileUpdater.Merge(first, Addresses()));
        var second = HostsFileUpdater.Merge(first, Addresses(address: "192.0.2.2"));
        Assert.DoesNotContain("192.0.2.1", second); Assert.Contains("192.0.2.2\texample.test", second);
        var third = HostsFileUpdater.Merge(second, Addresses("other.test", "2001:db8::1"));
        Assert.Contains("192.0.2.2\texample.test", third); Assert.Contains("2001:db8::1\tother.test", third);
        Assert.Equal(1, third.Split(HostsFileUpdater.Begin).Length - 1); Assert.StartsWith(original, third);
        Assert.EndsWith("# trailing\n", HostsFileUpdater.Merge(first + "# trailing\n", Addresses(address: "192.0.2.3")));
    }
    [Fact]
    public void ManualAliasesAndDamagedManagedBlocksFailBeforeChangingAnything()
    {
        Assert.Throws<IOException>(() => HostsFileUpdater.Merge("192.0.2.5 alias.test EXAMPLE.TEST. # note\n", Addresses()));
        foreach (var text in new[] { HostsFileUpdater.Begin, HostsFileUpdater.End, HostsFileUpdater.Begin + "\n" + HostsFileUpdater.Begin + "\n" + HostsFileUpdater.End,
            HostsFileUpdater.Begin + "\nuser text\n" + HostsFileUpdater.End })
            Assert.Throws<InvalidDataException>(() => HostsFileUpdater.Merge(text, Addresses()));
    }
    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("ansi")]
    public async Task FileWritePreservesEncodingAndReadbackAndCancelledUpdateHasNoSideEffect(string format)
    {
        var path = Path.Combine(Path.GetTempPath(), "axiom-hosts-" + Guid.NewGuid().ToString("N"));
        var encoding = format == "utf16" ? Encoding.Unicode : format == "utf8" ? Encoding.UTF8 : Encoding.Latin1;
        var original = encoding.GetPreamble().Concat(encoding.GetBytes("# synthetic é\r\n127.0.0.1 localhost\r\n")).ToArray();
        try
        {
            await File.WriteAllBytesAsync(path, original);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HostsFileUpdater.UpdateAsync(path, Addresses(), cancelled.Token));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            var checkedBeforeWrite = false;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HostsFileUpdater.UpdateAsync(path, Addresses(), default, () =>
            { checkedBeforeWrite = true; throw new OperationCanceledException("synthetic expired time window"); }));
            Assert.True(checkedBeforeWrite); Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.True(await HostsFileUpdater.UpdateAsync(path, Addresses(), default));
            var updated = await File.ReadAllBytesAsync(path); Assert.True(updated.AsSpan(0, original.Length).SequenceEqual(original));
            Assert.False(await HostsFileUpdater.UpdateAsync(path, Addresses(), default));
            Assert.Contains("192.0.2.1", encoding.GetString(updated));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void DnsParserRejectsTruncationMismatchedQuestionsAndCompressionCycles()
    {
        var query = HostsDnsResolver.CreateQuery("example.test", 1); var data = Answer(query);
        Assert.Equal(IPAddress.Parse("192.0.2.1"), Assert.Single(HostsDnsResolver.ReadAnswer(data, "example.test", 1).Addresses));
        Assert.Throws<InvalidDataException>(() => HostsDnsResolver.ReadAnswer(data[..^1], "example.test", 1));
        Assert.Throws<InvalidDataException>(() => HostsDnsResolver.ReadAnswer(data, "different.test", 1));
        Assert.Throws<InvalidDataException>(() => HostsDnsResolver.ReadAnswer(data, "example.test", 28));
        data[query.Length] = 0xc0; data[query.Length + 1] = (byte)query.Length;
        Assert.Throws<InvalidDataException>(() => HostsDnsResolver.ReadAnswer(data, "example.test", 1));
    }
    [Fact]
    public void DnsAliasesOnlyAcceptAddressesOwnedByTheCanonicalTarget()
    {
        var query = HostsDnsResolver.CreateQuery("example.test", 1);
        var other = HostsDnsResolver.CreateQuery("other.test", 1)[12..^4];
        using var bytes = new MemoryStream(); bytes.Write(query); bytes.Write(new byte[] { 0xc0, 12, 0, 5, 0, 1, 0, 0, 0, 10, 0, (byte)other.Length }); bytes.Write(other);
        bytes.Write(other); bytes.Write(new byte[] { 0, 1, 0, 1, 0, 0, 0, 10, 0, 4, 192, 0, 2, 9 });
        var packet = bytes.ToArray(); packet[2] = 0x81; packet[3] = 0x80; packet[7] = 2;
        var answer = HostsDnsResolver.ReadAnswer(packet, "example.test", 1);
        Assert.Equal("other.test", answer.Canonical); Assert.Equal(IPAddress.Parse("192.0.2.9"), Assert.Single(answer.Addresses));
        // Preserve valid encoding while changing only the alias's label text.
        packet = bytes.ToArray(); packet[2] = 0x81; packet[3] = 0x80; packet[7] = 2; packet[query.Length + 13] = (byte)'x';
        Assert.Empty(HostsDnsResolver.ReadAnswer(packet, "example.test", 1).Addresses);
    }
    [Fact(Timeout = 15000)]
    public async Task ExplicitDnsUsesConfiguredUpstreamAndTcpAfterTruncationForBothFamilies()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); var endpoint = (IPEndPoint)udp.Client.LocalEndPoint!;
        using var tcp = new TcpListener(endpoint); tcp.Start();
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var packet = await udp.ReceiveAsync(limit.Token); var truncated = packet.Buffer.ToArray(); truncated[2] = 0x83; truncated[3] = 0x80;
                await udp.SendAsync(truncated, packet.RemoteEndPoint, limit.Token);
                using var client = await tcp.AcceptTcpClientAsync(limit.Token); await using var stream = client.GetStream();
                var length = new byte[2]; await stream.ReadExactlyAsync(length, limit.Token);
                var query = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)]; await stream.ReadExactlyAsync(query, limit.Token);
                var answer = Answer(query, query[^3] == 1 ? "192.0.2.8" : "2001:db8::8");
                BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)answer.Length); await stream.WriteAsync(length, limit.Token); await stream.WriteAsync(answer, limit.Token);
            }
        }, limit.Token);
        var result = await new HostsDnsResolver().ResolveAsync(HostsCapabilities.Validate(Invocation(family: "both", server: endpoint.ToString())), limit.Token);
        await server; Assert.Equal(2, result["example.test"].Length);
        Assert.Contains(IPAddress.Parse("192.0.2.8"), result["example.test"]); Assert.Contains(IPAddress.Parse("2001:db8::8"), result["example.test"]);
    }
    [Fact(Timeout = 15000)]
    public async Task FailedOrSpoofedDnsNeverProducesAnUpdateBatch()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); var endpoint = (IPEndPoint)udp.Client.LocalEndPoint!;
        var server = Task.Run(async () =>
        {
            var packet = await udp.ReceiveAsync(limit.Token); var answer = Answer(packet.Buffer); answer[0] ^= 1;
            await udp.SendAsync(answer, packet.RemoteEndPoint, limit.Token);
        }, limit.Token);
        await Assert.ThrowsAsync<IOException>(() => new HostsDnsResolver().ResolveAsync(HostsCapabilities.Validate(Invocation(server: endpoint.ToString())), limit.Token)); await server;
    }
    [Fact(Timeout = 15000)]
    public async Task DohResolvesItsEndpointWithBootstrapAndPreservesTlsCertificateValidation()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); var bootstrap = (IPEndPoint)udp.Client.LocalEndPoint!;
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var rsa = RSA.Create(2048); var request = new CertificateRequest("CN=doh.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("doh.test"); request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        var bootstrapQueries = 0; var tlsConnections = 0;
        var dnsServer = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var packet = await udp.ReceiveAsync(limit.Token); var ipv4 = packet.Buffer[^3] == 1;
                var answer = Answer(packet.Buffer, ipv4 ? "127.0.0.1" : "::1");
                _ = HostsDnsResolver.ReadAnswer(answer, "doh.test", (ushort)(ipv4 ? 1 : 28)); bootstrapQueries++;
                await udp.SendAsync(answer, packet.RemoteEndPoint, limit.Token);
            }
        }, limit.Token);
        var tlsServer = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(limit.Token); tlsConnections++;
            using var stream = new SslStream(client.GetStream());
            try { await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, limit.Token); }
            catch (AuthenticationException) { }
            catch (IOException) { }
        }, limit.Token);
        var invocation = Invocation(transport: "doh") with { Parameters = Invocation(transport: "doh").Parameters
            .SetItem("doh", $"https://doh.test:{endpoint.Port}/dns-query").SetItem("bootstrap", bootstrap.ToString()) };
        await Assert.ThrowsAsync<IOException>(() => new HostsDnsResolver().ResolveAsync(HostsCapabilities.Validate(invocation), limit.Token));
        await Task.WhenAll(dnsServer, tlsServer); Assert.Equal(2, bootstrapQueries); Assert.Equal(1, tlsConnections);
    }
    [Fact(Timeout = 15000)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task DohSaveNamesInvalidBootstrapAndKeepsPersistedConfigurationUnchanged()
    {
        var directory = Path.Combine(Path.GetTempPath(), "axiom-hosts-validation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using var activity = new AutomationFixture(); using var store = new SqliteAutomationTaskStore(Path.Combine(directory, "tasks.db"));
            await using var runtime = new PluginTaskRuntime(BuiltInAutomationPlugins.Create(), store, "synthetic-hosts", activity.Engine, () => 1);
            await runtime.InitializeAsync();
            var action = Invocation(transport: "doh");
            var task = new AutomationTask(HostsCapabilities.TaskId, 0, "synthetic Hosts", true,
                new("process.running", 2, ImmutableDictionary<string, string>.Empty.Add("processNames", "Synthetic.exe")),
                action with { Parameters = action.Parameters.SetItem("bootstrap", "resolver.example.test") });
            var error = await Assert.ThrowsAsync<ArgumentException>(() => runtime.SaveAsync(task, default));
            Assert.StartsWith("请将“Bootstrap DNS”", error.Message); Assert.Contains("单个DNS服务器IP", error.Message); Assert.Empty(await store.ListAsync());
            var saved = await runtime.SaveAsync(task with { Action = action }, default); Assert.Equal(1, saved.Version);
            var invalidEdit = saved with { Action = saved.Action with { Parameters = saved.Action.Parameters.SetItem("server", "https://dns.example.test/dns-query") } };
            error = await Assert.ThrowsAsync<ArgumentException>(() => runtime.SaveAsync(invalidEdit, default));
            Assert.StartsWith("请将“普通 DNS 服务器”", error.Message);
            var persisted = Assert.Single(await store.ListAsync()); Assert.Equal(1, persisted.Version); Assert.Equal(action.Parameters["server"], persisted.Action.Parameters["server"]);
            await runtime.EnsureSafeToStopAsync(default);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact(Timeout = 15000)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task HostsToolConfigurationPersistsAndPauseUsesExistingTaskVersionProtection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "axiom-hosts-store-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using var activity = new AutomationFixture(); using var store = new SqliteAutomationTaskStore(Path.Combine(directory, "tasks.db"));
            await using var runtime = new PluginTaskRuntime(BuiltInAutomationPlugins.Create(), store, "synthetic-hosts", activity.Engine, () => 1);
            await runtime.InitializeAsync();
            var task = new AutomationTask(HostsCapabilities.TaskId, 0, "定时Hosts", true,
                new(IntervalCapabilities.Elapsed, 1, ImmutableDictionary<string, string>.Empty.Add("minutes", "60")), Invocation() with { Parameters = Invocation().Parameters.Remove("useSystemProxy") });
            var saved = await runtime.SaveAsync(task, default); Assert.Equal(1, saved.Version);
            var stored = Assert.Single((await runtime.ReadAsync(default)).Tasks); Assert.True(stored.Paused); Assert.Equal("example.test", stored.Action.Parameters["domains"]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SaveAsync(task, default));
            Assert.False(HostsCapabilities.Validate(stored.Action).UseSystemProxy);
            var pausedAgain = await runtime.SaveAsync(stored with { Action = stored.Action with { Parameters = stored.Action.Parameters.Add("useSystemProxy", "true") } }, default);
            Assert.Equal(2, pausedAgain.Version); Assert.True(Assert.Single(await store.ListAsync()).Paused);
            Assert.True(HostsCapabilities.Validate(Assert.Single(await store.ListAsync()).Action).UseSystemProxy);
            await runtime.EnsureSafeToStopAsync(default);
        }
        finally { Directory.Delete(directory, true); }
    }
}
