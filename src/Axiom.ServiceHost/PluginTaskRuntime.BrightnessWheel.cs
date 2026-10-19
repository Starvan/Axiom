using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;
using Axiom.Infrastructure;

namespace Axiom.ServiceHost;

internal sealed partial class PluginTaskRuntime
{
    private readonly SemaphoreSlim wheelToolGate = new(1, 1);
    private CancellationTokenSource? wheelCancel;
    private Task? wheelMonitor, wheelWork;
    private TaskCompletionSource<Outcome>? wheelReceipt;
    private string? wheelSubscription, wheelLastResult;
    private long wheelObservedSteps, wheelConsumedSteps;
    private bool wheelListening, wheelRunning, wheelHandoffReady;
    private volatile bool wheelTakenOver;
    private ResourceKey WheelKey => new(BrightnessWheelTool.Command, "manual-wheel", scope);
    private static bool IsBrightness(string id) => id is BuiltInBrightnessCapabilities.Set or BuiltInBrightnessAdjustCapabilities.Adjust or BuiltInBrightnessRestoreCapabilities.Set
        or HardwareBrightnessCapabilities.Set or HardwareBrightnessAdjustCapabilities.Adjust or HardwareBrightnessRestoreCapabilities.Set;
    internal static bool IsWheelToolTask(AutomationTask task) => task.Id == BrightnessWheelTool.TaskId && task.Title == BrightnessWheelTool.Name &&
        task.SchemaVersion == 1 && task.Detection.CapabilityId == BrightnessWheelTool.Trigger && task.Action.CapabilityId == BrightnessWheelTool.Command;
    private AutomationTask? WheelTask()
    {
        var task = tasks.SingleOrDefault(t => t.Id == BrightnessWheelTool.TaskId);
        if (task is not null && !IsWheelToolTask(task)) throw new InvalidDataException("亮度滚轮工具标识冲突；原任务保留。");
        return task;
    }
    private BrightnessWheelSettings WheelSettings()
    {
        var task = WheelTask();
        var settings = task is null ? new() : JsonSerializer.Deserialize<BrightnessWheelSettings>(task.Detection.Parameters["settings"])! with { Version = task.Version };
        BrightnessWheelTool.Validate(settings); return settings;
    }
    public BrightnessWheelSnapshot ReadBrightnessWheel()
    {
        lock (statusGate) return new(WheelSettings(), stopping ? "后台正在停止，未来滚轮写入已取消。" : wheelRunning ? "正在通过Engine调节亮度。" :
            wheelListening ? wheelTakenOver ? "正在监听；自动亮度已由手动接管暂停。" : "正在监听，命中组合后才接管和调节。" : "尚未监听；请启用或检查后台连接。",
            wheelListening, wheelTakenOver, wheelRunning, wheelLastResult);
    }
    private ResourceKey[] BrightnessKeys() => new[]
    {
        new ResourceKey(BuiltInBrightnessCapabilities.Set, "supported-built-in-panels", scope), new(BuiltInBrightnessAdjustCapabilities.Adjust, "supported-built-in-panels", scope),
        new(BuiltInBrightnessRestoreCapabilities.Set, "brightness", scope), new(HardwareBrightnessCapabilities.Set, "hardware-brightness-screens", scope),
        new(HardwareBrightnessAdjustCapabilities.Adjust, "hardware-brightness-screens", scope), new(HardwareBrightnessRestoreCapabilities.Set, "brightness", scope)
    }.Where(key => bindings.ContainsKey(key.CapabilityId)).ToArray();
    private void RestoreBrightnessWheelOverride()
    {
        var task = WheelTask();
        wheelTakenOver = task?.Detection.Parameters.GetValueOrDefault("manual") == "true";
        if (wheelTakenOver) foreach (var key in BrightnessKeys()) engine.TakeOverride(key, sid, "持久化的滚轮手动亮度接管");
    }
    private async Task PersistWheelAsync(BrightnessWheelSettings settings, bool manual, CancellationToken token)
    {
        BrightnessWheelTool.Validate(settings);
        await gate.WaitAsync(token);
        try
        {
            lock (statusGate) if ((WheelTask()?.Version ?? 0) != settings.Version) throw new InvalidOperationException("亮度工具已在别处更改，请刷新后重试。");
            var task = new AutomationTask(BrightnessWheelTool.TaskId, settings.Version, BrightnessWheelTool.Name, !settings.Enabled,
                new(BrightnessWheelTool.Trigger, 1, ImmutableDictionary<string, string>.Empty.Add("settings", JsonSerializer.Serialize(settings)).Add("manual", manual ? "true" : "false")),
                new(BrightnessWheelTool.Command, 1, ImmutableDictionary<string, string>.Empty));
            var saved = await database.SaveAsync(task, token);
            lock (statusGate) { tasks = [.. tasks.Where(t => t.Id != saved.Id), saved]; wheelTakenOver = manual; }
        }
        finally { gate.Release(); }
    }
    public async Task<BrightnessWheelSnapshot> SaveBrightnessWheelAsync(BrightnessWheelSettings settings, CancellationToken token)
    {
        BrightnessWheelTool.Validate(settings); await wheelToolGate.WaitAsync(token);
        try
        {
            if (stopping) throw new InvalidOperationException("后台正在停止，未启用滚轮工具。");
            await StopWheelMonitorAsync(token);
            await PersistWheelAsync(settings, wheelTakenOver, token);
            StartWheelMonitor(); return ReadBrightnessWheel();
        }
        finally { wheelToolGate.Release(); }
    }
    public async Task<BrightnessWheelSnapshot> ResumeAutomaticBrightnessAsync(long version, CancellationToken token)
    {
        await wheelToolGate.WaitAsync(token);
        try
        {
            if (WheelSettings().Version != version) throw new InvalidOperationException("亮度工具已变化，请刷新后重试。");
            foreach (var key in BrightnessKeys()) if (!Authorize(sid, key, CapabilityOperation.ReleaseOverride).Allowed) throw new UnauthorizedAccessException();
            await StopWheelMonitorAsync(token);
            using var pipe = Client(); await pipe.ConnectForUserAsync(2000, token);
            var id = Guid.NewGuid().ToString("N");
            await ProviderProtocol.WriteAsync(pipe, new(1, "resume-brightness", id, DateTimeOffset.UtcNow.AddSeconds(12)), token);
            var result = await ProviderProtocol.ReadAsync(pipe, token);
            if (result.RequestId != id || result.Type != "brightness-resumed" || result.Status != OutcomeStatus.Succeeded) throw new IOException("亮度资源释放未确认，自动亮度仍保持接管。");
            await PersistWheelAsync(WheelSettings(), false, token);
            wheelHandoffReady = false;
            foreach (var key in BrightnessKeys().Append(WheelKey)) engine.ReleaseOverride(key, sid);
            wheelLastResult = "已恢复自动亮度；按当前有效规则重新判断，不回放旧动作。";
            StartWheelMonitor(); return ReadBrightnessWheel();
        }
        finally { wheelToolGate.Release(); }
    }
    private void StartWheelMonitor()
    {
        if (stopping || !WheelSettings().Enabled) return;
        wheelCancel = new(); wheelMonitor = MonitorWheelAsync(WheelSettings(), wheelCancel.Token);
    }
    private async Task StopWheelMonitorAsync(CancellationToken token)
    {
        wheelCancel?.Cancel();
        if (wheelMonitor is not null) await wheelMonitor.WaitAsync(token);
        if (wheelWork is not null) await wheelWork.WaitAsync(token);
        wheelCancel?.Dispose(); wheelCancel = null; wheelMonitor = null; wheelWork = null;
        lock (statusGate) { wheelListening = false; wheelRunning = false; wheelSubscription = null; }
    }
    private async Task MonitorWheelAsync(BrightnessWheelSettings settings, CancellationToken token)
    {
        while (!token.IsCancellationRequested && !stopping)
        {
            try
            {
                using var pipe = Client(); await pipe.ConnectForUserAsync(2000, token);
                var id = Guid.NewGuid().ToString("N");
                lock (statusGate) { wheelSubscription = id; wheelObservedSteps = wheelConsumedSteps = 0; wheelHandoffReady = false; }
                await ProviderProtocol.WriteAsync(pipe, new(1, "observe-brightness-wheel", id, DateTimeOffset.UtcNow.AddSeconds(5), WheelSettings: settings), token);
                long sequence = 0;
                while (!token.IsCancellationRequested)
                {
                    var message = await ProviderProtocol.ReadAsync(pipe, token);
                    if (message.Type != "brightness-wheel-state" || message.RequestId != id || message.Sequence != sequence++ || message.Status != OutcomeStatus.Succeeded ||
                        message.WheelSteps is not { } steps || Math.Abs((decimal)steps) > 1_000_000_000) throw new IOException("滚轮监听状态未确认。");
                    lock (statusGate)
                    {
                        wheelListening = true; wheelObservedSteps = steps;
                        if (steps != wheelConsumedSteps && !wheelRunning)
                        { wheelRunning = true; wheelWork = RunWheelCommandsAsync(id, token); }
                    }
                }
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                lock (statusGate) { wheelListening = false; if (!token.IsCancellationRequested) wheelLastResult = "滚轮监听未就绪或中断：" + error.Message; }
                if (token.IsCancellationRequested) return;
                if (wheelWork is not null) await wheelWork;
            }
            // Retry transport only while disconnected. Connected idle waits solely on the pipe.
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); } catch (OperationCanceledException) { return; }
        }
    }
    private async Task EnsureWheelHandoffAsync(CancellationToken token)
    {
        if (wheelHandoffReady) return;
        foreach (var key in BrightnessKeys().Append(WheelKey)) if (!Authorize(sid, key, CapabilityOperation.Override).Allowed) throw new UnauthorizedAccessException();
        if (!wheelTakenOver) await PersistWheelAsync(WheelSettings(), true, token);
        using var pipe = Client(); await pipe.ConnectForUserAsync(2000, token);
        var id = Guid.NewGuid().ToString("N");
        await ProviderProtocol.WriteAsync(pipe, new(1, "abandon-brightness", id, DateTimeOffset.UtcNow.AddSeconds(40)), token);
        var marked = await ProviderProtocol.ReadAsync(pipe, token);
        if (marked.RequestId != id || marked.Type != "brightness-abandon-marked" || marked.Status != OutcomeStatus.Succeeded) throw new IOException("旧亮度放弃恢复尚未确认；未启动手势。");
        foreach (var key in BrightnessKeys()) engine.TakeOverride(key, sid, "快捷键+滚轮明确手动接管，保留当前亮度");
        var complete = await ProviderProtocol.ReadAsync(pipe, token);
        if (complete.RequestId != id || complete.Type != "brightness-abandon-complete" || complete.Status != OutcomeStatus.Succeeded) throw new IOException("旧亮度原生资源清理未确认；未启动手势。");
        foreach (var key in BrightnessKeys()) await engine.WaitForResourceIdleAsync(key, token);
        await DrainRestorableSettingsAsync(token);
        wheelHandoffReady = true;
    }
    private async Task RunWheelCommandsAsync(string subscription, CancellationToken token)
    {
        await Task.Yield();
        try
        {
            using var handoff = CancellationTokenSource.CreateLinkedTokenSource(token); handoff.CancelAfter(TimeSpan.FromSeconds(40));
            await EnsureWheelHandoffAsync(handoff.Token);
            while (!token.IsCancellationRequested)
            {
                long from; lock (statusGate) { if (wheelObservedSteps == wheelConsumedSteps) break; from = wheelConsumedSteps; }
                var invocation = BrightnessWheelTool.Gesture(subscription, from);
                var command = new Command(Guid.NewGuid(), WheelKey, StateValue.From(TaskInvocationCommands.Schema, JsonSerializer.Serialize(invocation)), sid, DateTimeOffset.UtcNow.AddSeconds(30), false);
                var completion = new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (statusGate) wheelReceipt = completion;
                var accepted = engine.SubmitManualCommand(command);
                if (accepted.Status != OutcomeStatus.Accepted) throw new IOException("手势未获Engine准入：" + accepted.Reason);
                var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(32), token);
                await engine.WaitForResourceIdleAsync(WheelKey, token);
                if (result.Status != OutcomeStatus.Succeeded) throw new IOException(result.Reason);
            }
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            lock (statusGate) wheelLastResult = "手动亮度已停止或未确认，不自动重试：" + error.Message;
            wheelCancel?.Cancel();
        }
        finally
        {
            lock (statusGate)
            {
                wheelRunning = false; wheelReceipt = null;
                if (!token.IsCancellationRequested && !stopping && wheelSubscription == subscription && wheelObservedSteps != wheelConsumedSteps)
                { wheelRunning = true; wheelWork = RunWheelCommandsAsync(subscription, token); }
            }
        }
    }
    private async ValueTask<Outcome> ExecuteWheelCommandAsync(Command command, TaskInvocation invocation, CancellationToken token)
    {
        Outcome outcome;
        try
        {
            var gesture = BrightnessWheelTool.ValidateGesture(invocation);
            if (!wheelTakenOver || !wheelHandoffReady || gesture.Subscription != wheelSubscription || gesture.FromSteps != wheelConsumedSteps)
                throw new UnauthorizedAccessException("手势没有有效的手动接管与监听授权。");
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, wheelCancel?.Token ?? new CancellationToken(true));
            lifetime.CancelAfter(command.Deadline - DateTimeOffset.UtcNow);
            using var pipe = Client(); await pipe.ConnectForUserAsync(2000, lifetime.Token);
            var id = Guid.NewGuid().ToString("N");
            await ProviderProtocol.WriteAsync(pipe, new(1, "invoke", id, command.Deadline, Invocation: invocation, CommandId: command.CommandId), lifetime.Token);
            var receipt = await ProviderProtocol.ReadAsync(pipe, lifetime.Token);
            if (receipt.Version != 1 || receipt.Type != "result" || receipt.RequestId != id || receipt.CommandId != command.CommandId || receipt.Status is not (OutcomeStatus.Succeeded or OutcomeStatus.Failed) || receipt.WheelSteps is null)
                throw new IOException("亮度手势回执未确认；没有重试。");
            lock (statusGate) { wheelConsumedSteps = receipt.WheelSteps.Value; wheelLastResult = receipt.Reason; }
            outcome = new(receipt.Status.Value, receipt.Reason ?? "手势结束。", command.CommandId);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { outcome = new(OutcomeStatus.Failed, error.Message, command.CommandId, FailureKind.Permanent); }
        lock (statusGate) wheelReceipt?.TrySetResult(outcome);
        return outcome;
    }
    private static CapabilityBinding WheelBinding() => new(new(BrightnessWheelTool.Command, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(30), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "explicit wheel gesture", "same user, manually owned brightness", []), ScalarKind.Text, ScalarKind.Boolean);
}
