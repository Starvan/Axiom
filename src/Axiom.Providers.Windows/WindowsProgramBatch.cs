using System.ComponentModel;

namespace Axiom.Providers.Windows;

public static class WindowsProgramBatch
{
    public static async Task<ProgramControlResult> ExecuteAsync(IReadOnlyList<string> paths,
        Func<string, CancellationToken, Task<ProgramControlResult>> execute, CancellationToken token)
    {
        if (paths.Count is < 1 or > 32) throw new ArgumentException("请选择 1–32 个目标程序。");
        token.ThrowIfCancellationRequested();
        async Task<ProgramControlResult> Run(string path)
        {
            try { token.ThrowIfCancellationRequested(); return await execute(path, token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
            { return new(false, error.Message); }
        }
        // All targets start their bounded wait together, staying within the command deadline.
        var results = await Task.WhenAll(paths.Select(Run));
        token.ThrowIfCancellationRequested();
        if (results.Length == 1) return results[0];
        var skipped = results.Count(r => r.Success && r.Skipped);
        var completed = results.Count(r => r.Success && !r.Skipped);
        var failed = results.Count(r => !r.Success);
        var summary = skipped == results.Length ? $"无需处理 {skipped} 个目标。" :
            $"完成 {completed} 个" + (skipped > 0 ? $"，无需处理 {skipped} 个" : "") + (failed > 0 ? $"，失败 {failed} 个" : "") + "。";
        var errors = results.Select((result, index) => (Result: result, Path: paths[index])).Where(item => !item.Result.Success)
            .GroupBy(item => item.Result.Reason, StringComparer.Ordinal)
            .Select(group => string.Join("、", group.Select(item => item.Path)) + "：" + group.Key);
        return new(failed == 0, summary + (failed > 0 ? "\n" + string.Join("\n", errors) : "")) { Skipped = skipped == results.Length };
    }
}
