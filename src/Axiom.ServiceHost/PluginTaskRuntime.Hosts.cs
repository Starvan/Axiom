using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;

namespace Axiom.ServiceHost;

internal sealed partial class PluginTaskRuntime
{
    private static async Task<Outcome> ExecuteHostsAsync(TaskInvocation invocation, Guid commandId, CancellationToken token, Action beforeCommit)
    {
        IReadOnlyDictionary<string, System.Net.IPAddress[]> resolved;
        try { resolved = await new HostsDnsResolver().ResolveAsync(HostsCapabilities.Validate(invocation), token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { return new(OutcomeStatus.Failed, "解析未完成，hosts保持原样：" + (error is OperationCanceledException ? "已取消或DNS超时。" : error.Message), commandId); }
        var committing = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
            var changed = await HostsFileUpdater.UpdateAsync(path, resolved, token, () => { beforeCommit(); committing = true; });
            return new(OutcomeStatus.Succeeded, $"{resolved.Count}个域名、{resolved.Sum(p => p.Value.Length)}条IP记录" +
                (changed ? "已写入系统hosts并回读确认。" : "与系统hosts管理记录一致，无需写入。"), commandId);
        }
        catch (OperationCanceledException) when (!committing) { throw; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { return new(committing ? OutcomeStatus.Unknown : OutcomeStatus.Failed, (committing ? "hosts更新未确认：" : "hosts尚未写入：") + error.Message + " 请检查活动记录和文件。", commandId); }
    }
}
