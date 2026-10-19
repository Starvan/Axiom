using System.Collections.Immutable;
using System.Globalization;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;
using Axiom.Infrastructure;

namespace Axiom.ServiceHost;

internal sealed partial class PluginTaskRuntime
{
    private sealed class StartupInput(SourceSession source, ResourceKey[] keys)
    {
        internal SourceSession Source = source;
        internal ResourceKey[] Keys = keys;
        internal long Sequence;
        internal bool Ready;
        internal bool PreserveOnRebind;
    }
    private StartupInput? startupInput;
    private readonly SemaphoreSlim trayToolGate = new(1, 1);
    private readonly CancellationTokenSource trayLifetime = new();
    private CancellationTokenSource? trayMonitorCancel;
    private Task? trayMonitor, trayFlush;
    private long? trayPendingVersion;
    private bool trayPendingAutomatic, trayMonitoring;
    private string? trayState, trayResult;
    private DateTimeOffset? trayLastRun;
    private OutcomeStatus? trayLastStatus;
    private long trayRunVersion;
    private bool trayRunning;

    internal static bool IsTrayToolTask(AutomationTask task) => task.Id == TrayTool.TaskId && task.Title == TrayTool.Name &&
        task.SchemaVersion == 1 && task.TimeWindow is null && task.DetectionExpression is null &&
        task.Detection.CapabilityId == TrayTool.Trigger && task.Detection.SchemaVersion == 1 && task.Detection.Parameters.Count == 2 &&
        task.Detection.Parameters.GetValueOrDefault("frequency") == "every" && task.Detection.Parameters.GetValueOrDefault("mode") is TrayTool.Startup or TrayTool.Continuous &&
        task.Action.CapabilityId == TrayCapabilities.Hide && task.Action.SchemaVersion == 1 && task.Action.Parameters.Count == 3 &&
        int.TryParse(task.Action.Parameters.GetValueOrDefault("waitSeconds"), NumberStyles.None, CultureInfo.InvariantCulture, out var waitSeconds) &&
        waitSeconds is >= 1 and <= TrayCapabilities.MaximumWaitSeconds && task.Action.Parameters.GetValueOrDefault("delaySeconds") == "0";

    private AutomationTask? TrayTask()
    {
        var task = tasks.SingleOrDefault(t => t.Id == TrayTool.TaskId);
        if (task is not null && !IsTrayToolTask(task)) throw new InvalidOperationException("托盘工具标识与已有任务冲突；原任务保留，未覆盖。");
        return task;
    }
    public TrayToolSnapshot ReadTrayTool()
    {
        lock (statusGate)
        {
            var task = TrayTask();
            var settings = task is null ? new TrayToolSettings(0, false, TrayTool.Startup, []) : new(task.Version, !task.Paused,
                task.Detection.Parameters["mode"], TrayCapabilities.ValidateTargets(task.Action),
                int.Parse(task.Action.Parameters["waitSeconds"], CultureInfo.InvariantCulture));
            var state = task is null ? "尚未配置；先选择需要隐藏托盘图标的程序。" : task.Paused ? "已关闭；名单保留，不恢复已隐藏图标。" :
                trayState ?? "等待后台和会话程序准备好，再处理当前图标。";
            return new(settings, state, trayRunning, trayLastRun, trayLastStatus, trayResult);
        }
    }
    public async Task<TrayToolSnapshot> SaveTrayToolAsync(TrayToolSettings settings, CancellationToken token)
    {
        TrayTool.Validate(settings);
        await trayToolGate.WaitAsync(token);
        try
        {
            AutomationTask? current;
            lock (statusGate) current = TrayTask();
            if ((current?.Version ?? 0) != settings.Version) throw new InvalidOperationException("托盘工具已在别处更改，请刷新后重试。");
            var targets = ExecutablePaths.Parse(string.Join("\n", settings.Executables));
            var task = new AutomationTask(TrayTool.TaskId, settings.Version, TrayTool.Name, !settings.Enabled,
                new(TrayTool.Trigger, 1, ImmutableDictionary<string, string>.Empty.Add("frequency", "every").Add("mode", settings.Mode)),
                new(TrayCapabilities.Hide, 1, ImmutableDictionary<string, string>.Empty.Add("executable", string.Join("\n", targets))
                    .Add("waitSeconds", settings.WaitSeconds.ToString(CultureInfo.InvariantCulture)).Add("delaySeconds", "0")));
            var saved = await SaveTaskAsync(task, token);
            await RefreshTrayMonitorAsync(saved, token);
            lock (statusGate)
            {
                trayPendingVersion = null;
                trayRunning = false;
                if (!saved.Paused) QueueTrayTrigger(saved.Version, false);
            }
            return ReadTrayTool();
        }
        finally { trayToolGate.Release(); }
    }
    public async Task<TrayToolSnapshot> RunTrayToolAsync(long version, CancellationToken token)
    {
        await trayToolGate.WaitAsync(token);
        try
        {
            lock (statusGate)
            {
                var task = TrayTask();
                if (task is null || task.Version != version) throw new InvalidOperationException("托盘工具已更改，请刷新后重试。");
                if (task.Paused || stopping) throw new InvalidOperationException("请先启用托盘工具，再立即隐藏。");
                QueueTrayTrigger(version, false);
            }
            return ReadTrayTool();
        }
        finally { trayToolGate.Release(); }
    }
    private void QueueTrayTrigger(long version, bool automatic)
    {
        if (stopping) return;
        var task = TrayTask();
        if (task is null || task.Paused || task.Version != version) return;
        // An explicit request survives a later removal-only notification; repeated automatic opportunities merge.
        trayPendingAutomatic = trayPendingVersion is null ? automatic : trayPendingAutomatic && automatic;
        trayPendingVersion = version;
        trayState = "已请求处理当前图标；等待会话就绪或当前动作结束，结果尚未确认。";
        FlushTrayTrigger();
    }
    private void FlushTrayTrigger()
    {
        if (stopping || trayPendingVersion is not { } version || startupInput is not { Ready: true } input) return;
        var task = TrayTask();
        if (task is null || task.Paused || task.Version != version) { trayPendingVersion = null; return; }
        var key = new ResourceKey(AxiomStartupPlugin.CapabilityId, task.Id, scope);
        if (!input.Keys.Contains(key)) return;
        var actionKey = compiler.Compile(task, sid, scope).Action.Key;
        var decision = engine.GetDecision(actionKey);
        if (decision.Status is DecisionStatus.Running or DecisionStatus.Queued)
        {
            trayFlush ??= FlushTrayWhenIdleAsync(actionKey);
            return;
        }
        trayPendingVersion = null;
        BeginTrayRun(task);
        engine.Update(input.Source, input.Sequence++, new(key, StateQuality.Known, StateValue.From(new("axiom.bool", 1), false), DateTimeOffset.UtcNow));
        engine.Update(input.Source, input.Sequence++, new(key, StateQuality.Known, StateValue.From(new("axiom.bool", 1), true), DateTimeOffset.UtcNow));
        firedStartupTasks.Add(task.Id);
        CheckTrayDecision(task);
    }
    private void CheckTrayDecision(AutomationTask task)
    {
        if (!IsTrayToolTask(task)) return;
        var decision = engine.GetDecision(compiler.Compile(task, sid, scope).Action.Key);
        if (decision.Status is not (DecisionStatus.Running or DecisionStatus.Queued))
        {
            trayRunning = false;
            trayState = decision.Status == DecisionStatus.Conflict ? "动作冲突，尚未隐藏；请检查使用同一名单的任务。" :
                decision.Status == DecisionStatus.Overridden ? "此资源已被手动接管，尚未隐藏。" : "当前不能执行隐藏：" + decision.Reason;
        }
    }
    private async Task FlushTrayWhenIdleAsync(ResourceKey key)
    {
        // Defer out of the current input callback, then await this resource only (no timer or scan).
        await Task.Yield();
        try { await engine.WaitForResourceIdleAsync(key, trayLifetime.Token); }
        catch (OperationCanceledException) { return; }
        lock (statusGate) { trayFlush = null; FlushTrayTrigger(); }
    }
    private void BeginTrayRun(AutomationTask task)
    {
        if (!IsTrayToolTask(task)) return;
        trayRunVersion = task.Version; trayRunning = true;
        trayState = "正在通过后台处理当前图标；启用不代表动作已成功。";
    }
    private Outcome TrayOutcome(Command command, Outcome outcome)
    {
        lock (statusGate)
        {
            var origin = command.Origins.FirstOrDefault(o => o.RuleId == TrayTool.TaskId);
            var task = tasks.SingleOrDefault(t => t.Id == TrayTool.TaskId);
            if (origin is not null && task is not null && !task.Paused && IsTrayToolTask(task) && task.Version == origin.RuleVersion && trayRunVersion == task.Version)
            {
                trayRunning = false; trayLastRun = DateTimeOffset.UtcNow; trayLastStatus = outcome.Status; trayResult = outcome.Reason;
                trayState = outcome.Status == OutcomeStatus.Succeeded ? task.Detection.Parameters["mode"] == TrayTool.Continuous
                    ? trayMonitoring ? "本次隐藏已完成；正在监听图标重新出现。" : "本次隐藏已完成；持续监听尚未就绪。"
                    : "本次隐藏已完成；等待下次 Axiom 后台启动。" : "本次隐藏未完成或未确认；可查看结果并重试。";
            }
        }
        return outcome;
    }
    private async Task RefreshTrayMonitorAsync(AutomationTask? task, CancellationToken token)
    {
        trayMonitorCancel?.Cancel();
        if (trayMonitor is not null) await trayMonitor;
        trayMonitorCancel?.Dispose(); trayMonitorCancel = null; trayMonitor = null;
        lock (statusGate) trayMonitoring = false;
        if (stopping || task is null || !IsTrayToolTask(task) || task.Paused || task.Detection.Parameters["mode"] != TrayTool.Continuous) return;
        trayMonitorCancel = CancellationTokenSource.CreateLinkedTokenSource(trayLifetime.Token);
        trayMonitor = MonitorTrayAsync(task, trayMonitorCancel.Token);
    }
    private async Task MonitorTrayAsync(AutomationTask task, CancellationToken token)
    {
        var connectedOnce = false;
        var paths = TrayCapabilities.ValidateTargets(task.Action);
        var resource = new ProviderResource(TrayTool.Trigger, ExecutablePaths.ResourceId(paths));
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = Client(); await pipe.ConnectForUserAsync(2000, token);
                var id = Guid.NewGuid().ToString("N");
                await ProviderProtocol.WriteAsync(pipe, new(1, "observe-tray", id, DateTimeOffset.UtcNow.AddSeconds(5), Invocation: task.Action), token);
                long sequence = 0, counter = 0;
                while (!token.IsCancellationRequested)
                {
                    var response = await ProviderProtocol.ReadAsync(pipe, token);
                    if (response.Version != 1 || response.RequestId != id || response.Sequence != sequence || response.Type != (sequence == 0 ? "snapshot" : "update") ||
                        response.Samples is not { Length: 1 } samples || samples[0] is not { Quality: StateQuality.Known, SchemaId: "axiom.event-count", SchemaVersion: 1, Boolean: not null, Number: not null } sample ||
                        sample.Resource != resource || sample.Number < counter || sample.Number < 0 || sample.Number != decimal.Truncate(sample.Number.Value) || sample.Number > long.MaxValue)
                        throw new InvalidDataException(response.Reason ?? "持续托盘通知无效，未根据未知数据隐藏。");
                    lock (statusGate)
                    {
                        if (token.IsCancellationRequested || stopping || TrayTask()?.Version != task.Version) return;
                        trayMonitoring = true;
                        if (sequence == 0 && !trayRunning && trayPendingVersion is null && trayLastStatus == OutcomeStatus.Succeeded)
                            trayState = "本次隐藏已完成；正在监听图标重新出现。";
                        if (sample.Boolean == false && trayPendingVersion == task.Version && trayPendingAutomatic) trayPendingVersion = null;
                        if (sequence == 0 ? connectedOnce && sample.Boolean == true : sample.Number > counter && sample.Boolean == true)
                            QueueTrayTrigger(task.Version, true);
                    }
                    counter = (long)sample.Number.Value; sequence++; connectedOnce = true;
                }
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                if (token.IsCancellationRequested) break;
                lock (statusGate)
                {
                    trayMonitoring = false;
                    trayState = "持续监听中断：" + error.Message + " 后台会尝试重新连接；尚未确认持续隐藏正常。";
                }
                Record("托盘持续监听中断", error.Message, ActivityLevel.Warning);
            }
            // Existing provider reconnect policy; this retries transport only, never enumerates or hides on a timer.
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); } catch (OperationCanceledException) { break; }
        }
    }
}
