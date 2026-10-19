using System.IO.Pipes;
using System.Security.Principal;
using Axiom.Contracts;

namespace Axiom.WinUI;

internal static class WorkbenchClient
{
    public static async IAsyncEnumerable<Axiom.Domain.TaskRunStatus[]> WatchRunStatesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException();
        using var pipe = new NamedPipeClientStream(".", AutomationProtocol.PipeName(sid), PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(token); connect.CancelAfter(TimeSpan.FromSeconds(12));
        await pipe.ConnectForUserAsync(3000, connect.Token);
        var request = new AutomationMessage(1, "watch-run-states", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddSeconds(10));
        await AutomationProtocol.WriteAsync(pipe, request, connect.Token);
        long sequence = 0; var first = true;
        while (!token.IsCancellationRequested)
        {
            var response = await AutomationProtocol.ReadAsync(pipe, first ? connect.Token : token);
            if (response.Version != 1 || response.Type != "run-states" || response.RequestId != request.RequestId ||
                response.RunStatesSequence != sequence++ || response.RunStates is not { } states || states.Length > Axiom.Domain.AutomationTaskLimits.MaximumTasks ||
                states.Select(state => state.TaskId).Distinct(StringComparer.Ordinal).Count() != states.Length)
                throw new InvalidDataException("自动暂停状态流未确认。");
            first = false; yield return states;
        }
    }
    public static async Task<AutomationMessage> RequestAsync(AutomationMessage request, CancellationToken token = default)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException();
        using var pipe = new NamedPipeClientStream(".", AutomationProtocol.PipeName(sid), PipeDirection.InOut,
            PipeOptions.Asynchronous);
        var shutdown = request.Type == "shutdown";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(shutdown ? 40 : 13));
        await pipe.ConnectForUserAsync(3000, timeout.Token);
        await AutomationProtocol.WriteAsync(pipe, request with { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(shutdown ? 40 : 12) }, timeout.Token);
        var response = await AutomationProtocol.ReadAsync(pipe, timeout.Token);
        if (response.Version != 1 || response.RequestId != request.RequestId) throw new InvalidDataException();
        return response;
    }
}
