using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;

namespace Axiom.ServiceHost;

internal sealed partial class PluginTaskRuntime
{
    private readonly CancellationTokenSource runLifetime = new();
    private readonly Channel<byte> runWake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<string, string?> runPauses = [];
    private readonly Func<Action<bool?>, CancellationToken, Task>? observeRunHdr;
    private readonly HashSet<Channel<byte>> runStateSubscribers = [];
    private Task? runMonitor;
    private bool? runHdr;
    private bool runHdrWatching;
    private string? AutomaticPauseReason(AutomationTask task) => task.Paused ? null : task.RunConditions?.PauseReason(runHdrWatching ? runHdr : null, windowTime.GetUtcNow(), LocalZone());
    private static TimeZoneInfo LocalZone() { TimeZoneInfo.ClearCachedData(); return TimeZoneInfo.Local; }
    private void SetTaskRule(AutomationTask task)
    {
        var pause = AutomaticPauseReason(task);
        engine.SetRule(compiler.Compile(task, sid, scope), pause); runPauses[task.Id] = pause;
    }
    private ImmutableArray<TaskRunStatus> CurrentRunStates() => [.. tasks.Where(t => !IsTrayToolTask(t) && !IsWheelToolTask(t))
        .Select(t => new TaskRunStatus(t.Id, !t.Paused && runPauses.GetValueOrDefault(t.Id) is not null, t.Paused ? null : runPauses.GetValueOrDefault(t.Id)))];
    private void WakeRunConditions() => runWake.Writer.TryWrite(1);
    private void PublishRunStates()
    { foreach (var subscriber in runStateSubscribers) subscriber.Writer.TryWrite(1); }
    internal async IAsyncEnumerable<TaskRunStatus[]> WatchRunStatesAsync([EnumeratorCancellation] CancellationToken token)
    {
        var signal = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        TaskRunStatus[] initial;
        lock (statusGate) { runStateSubscribers.Add(signal); initial = [.. CurrentRunStates()]; }
        try
        {
            yield return initial;
            await foreach (var _ in signal.Reader.ReadAllAsync(token))
            {
                TaskRunStatus[] states;
                lock (statusGate) states = [.. CurrentRunStates()];
                yield return states;
            }
        }
        finally { lock (statusGate) runStateSubscribers.Remove(signal); signal.Writer.TryComplete(); }
    }
    private async Task MonitorRunConditionsAsync(CancellationToken token)
    {
        CancellationTokenSource? hdrStop = null, clockStop = null;
        Task? hdrPump = null, clockPump = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                DateTimeOffset? next = null; long planVersion;
                await gate.WaitAsync(token);
                try
                {
                    if (stopping) return;
                    var hdrNeeded = tasks.Any(t => !t.Paused && t.RunConditions is { Hdr: not "any" });
                    if (hdrNeeded != runHdrWatching)
                    {
                        if (hdrStop is not null) { hdrStop.Cancel(); if (hdrPump is not null) await hdrPump; hdrStop.Dispose(); hdrStop = null; }
                        lock (statusGate) { runHdr = null; runHdrWatching = hdrNeeded; }
                        if (hdrNeeded)
                        {
                            hdrStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                            hdrPump = ObserveRunHdrAsync(hdrStop.Token);
                        }
                    }
                    var timeNeeded = tasks.Any(t => !t.Paused && t.RunConditions?.TimeWindow is not null);
                    if (timeNeeded && clockStop is null)
                    {
                        clockStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                        clockPump = ObserveRunClockAsync(clockStop.Token);
                    }
                    else if (!timeNeeded && clockStop is not null)
                    {
                        clockStop.Cancel(); if (clockPump is not null) await clockPump;
                        clockStop.Dispose(); clockStop = null; clockPump = null;
                    }
                    var zone = LocalZone(); var now = windowTime.GetUtcNow(); var changed = false;
                    lock (statusGate)
                    {
                        foreach (var task in tasks.Where(t => !IsTrayToolTask(t) && !IsWheelToolTask(t) && IsSupportedTask(t)))
                        {
                            var pause = task.Paused ? null : task.RunConditions?.PauseReason(runHdr, now, zone);
                            if (runPauses.GetValueOrDefault(task.Id) != pause)
                            {
                                engine.SetRuleAutomaticPause(task.Id, pause); runPauses[task.Id] = pause; changed = true;
                            }
                            if (!task.Paused && task.RunConditions?.TimeWindow?.NextBoundary(now, zone) is { } boundary && (next is null || boundary < next)) next = boundary;
                        }
                        if (changed) PublishRunStates();
                    }
                    await RefreshObservationAsync();
                    planVersion = engine.GetObservationPlan().Version;
                }
                finally { gate.Release(); }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                var wake = runWake.Reader.ReadAsync(wait.Token).AsTask();
                var plan = engine.WaitForObservationPlanAsync(planVersion, wait.Token);
                var delay = next.HasValue ? next.Value - windowTime.GetUtcNow() : Timeout.InfiniteTimeSpan;
                if (delay != Timeout.InfiniteTimeSpan && delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                var due = Task.Delay(delay, windowTime, wait.Token);
                try { await await Task.WhenAny(wake, plan, due); }
                finally { wait.Cancel(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            hdrStop?.Cancel(); clockStop?.Cancel();
            if (hdrPump is not null) await hdrPump;
            if (clockPump is not null) await clockPump;
            hdrStop?.Dispose(); clockStop?.Dispose();
        }
    }
    private Task ObserveRunClockAsync(CancellationToken token) =>
        ObserveRunResourceAsync(new(SystemEventCapabilities.ClockEvents, "local"), "axiom.event-count", _ => WakeRunConditions(), token);
    private async Task ObserveRunHdrAsync(CancellationToken token)
    {
        void Publish(bool? value) { lock (statusGate) runHdr = value; WakeRunConditions(); }
        if (observeRunHdr is not null)
        {
            try { await observeRunHdr(Publish, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally { Publish(null); }
            return;
        }
        await ObserveRunResourceAsync(new(HdrStateCapabilities.State, "primary"), "axiom.bool",
            sample => Publish(sample?.Quality == StateQuality.Known ? sample.Boolean : null), token);
    }
    private async Task ObserveRunResourceAsync(ProviderResource resource, string schema, Action<ProviderSample?> publish, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = Client(); await pipe.ConnectForUserAsync(2000, token);
                var id = Guid.NewGuid().ToString("N"); long sequence = 0;
                await ProviderProtocol.WriteAsync(pipe, new(1, "observe", id, DateTimeOffset.UtcNow.AddSeconds(5), Resources: [resource], RequiredCapabilities: [resource.CapabilityId]), token);
                while (!token.IsCancellationRequested)
                {
                    var response = await ProviderProtocol.ReadAsync(pipe, token);
                    if (response.Version != 1 || response.RequestId != id || response.Sequence != sequence++ || response.Type is not ("snapshot" or "update") ||
                        response.Samples is not { Length: 1 } samples || samples[0].Resource != resource || samples[0].SchemaId != schema || samples[0].SchemaVersion != 1 ||
                        samples[0].Quality == StateQuality.Known && (schema == "axiom.bool" ? samples[0].Boolean is null : samples[0].Number is null or < 0))
                        throw new InvalidDataException("外层运行条件检测流无效。");
                    publish(samples[0]);
                }
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { publish(null); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); } catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }
}
