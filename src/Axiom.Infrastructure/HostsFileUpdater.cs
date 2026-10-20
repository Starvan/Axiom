using System.Net;
using System.Text;

namespace Axiom.Infrastructure;

// The Host supplies the fixed Windows path; tests supply only a synthetic file.
public static class HostsFileUpdater
{
    public const string Begin = "# BEGIN AXIOM HOSTS";
    public const string End = "# END AXIOM HOSTS";
    private static readonly SemaphoreSlim WriteGate = new(1);

    public static async Task<bool> UpdateAsync(string path, IReadOnlyDictionary<string, IPAddress[]> resolved, CancellationToken token, Action? beforeWrite = null)
    {
        if (resolved.Count is < 1 or > 32 || resolved.Any(p => Axiom.Contracts.HostsCapabilities.Domain(p.Key) != p.Key || p.Value.Length is < 1 or > 64))
            throw new ArgumentException("Hosts解析结果无效。");
        await WriteGate.WaitAsync(token);
        try
        {
            // No replace/backup: retain the original file's ACL and ownership. Deny concurrent writers.
            await using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            if (file.Length > 4 * 1024 * 1024) throw new IOException("hosts文件超过4MB，未更新。");
            var original = new byte[(int)file.Length]; await file.ReadExactlyAsync(original, token);
            var (encoding, prefix) = DetectEncoding(original);
            var content = encoding.GetString(original, prefix, original.Length - prefix);
            var updated = Merge(content, resolved);
            if (content == updated) return false;
            var text = encoding.GetBytes(updated); var output = new byte[prefix + text.Length]; original.AsSpan(0, prefix).CopyTo(output); text.CopyTo(output, prefix);
            token.ThrowIfCancellationRequested();
            beforeWrite?.Invoke();
            // Once committing, finish a bounded local write even when the originating rule is paused.
            // Cancellation before this point has no file side effect; later failures remain Unknown.
            file.Position = 0; await file.WriteAsync(output, CancellationToken.None); file.SetLength(output.Length); file.Flush(flushToDisk: true);
            file.Position = 0; var readback = new byte[output.Length]; await file.ReadExactlyAsync(readback, CancellationToken.None);
            if (!readback.AsSpan().SequenceEqual(output)) throw new IOException("hosts写入回读不一致，结果未确认。");
            return true;
        }
        finally { WriteGate.Release(); }
    }
    private static (Encoding Encoding, int Prefix) DetectEncoding(byte[] data)
    {
        if (data.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return (new UnicodeEncoding(false, false, true), 2);
        if (data.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return (new UnicodeEncoding(true, false, true), 2);
        if (data.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return (new UTF8Encoding(false, true), 3);
        // An ASCII-only addition with a one-to-one byte mapping preserves UTF8/ANSI bytes outside our block.
        if (data.Contains((byte)0)) throw new InvalidDataException("hosts编码无法识别，未更新。");
        return (Encoding.Latin1, 0);
    }
    public static string Merge(string content, IReadOnlyDictionary<string, IPAddress[]> resolved)
    {
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Split('\n'); var start = -1; var end = -1; var offset = 0; var startOffset = -1; var endOffset = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line == Begin) { if (start >= 0 || end >= 0) throw new InvalidDataException("Axiom hosts管理区重复，未更新。"); start = i; startOffset = offset; }
            if (line == End) { if (start < 0 || end >= 0) throw new InvalidDataException("Axiom hosts管理区不完整，未更新。"); end = i; endOffset = offset + lines[i].Length + (i < lines.Length - 1 ? 1 : 0); }
            offset += lines[i].Length + 1;
        }
        if (start >= 0 && end < 0) throw new InvalidDataException("Axiom hosts管理区不完整，未更新。");
        var managed = new SortedDictionary<string, IPAddress[]>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Split('#')[0].Trim(); if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (i > start && i < end)
            {
                if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || Axiom.Contracts.HostsCapabilities.Domain(parts[1]) != parts[1])
                    throw new InvalidDataException("Axiom hosts管理区含无法识别的记录，未更新。");
                managed[parts[1]] = [.. managed.GetValueOrDefault(parts[1], []), ip];
            }
            else if (parts.Length > 1 && IPAddress.TryParse(parts[0], out _) && parts.Skip(1).Any(d => resolved.ContainsKey(d.TrimEnd('.').ToLowerInvariant())))
                throw new IOException("hosts中已有同名手工记录；请先处理冲突，Axiom未修改文件。");
        }
        foreach (var pair in resolved) managed[pair.Key] = pair.Value;
        var block = Begin + newline + string.Join(newline, managed.SelectMany(p => p.Value.Distinct().OrderBy(a => a.ToString(), StringComparer.Ordinal).Select(a => a + "\t" + p.Key))) + newline + End + newline;
        return startOffset < 0 ? content + (content.Length > 0 && !content.EndsWith('\n') ? newline : "") + block : content[..startOffset] + block + content[endOffset..];
    }
}
