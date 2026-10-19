using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;
using Axiom.Infrastructure;

namespace Axiom.ServiceHost;

// Shared runtime for explicitly registered plugins: no per-capability storage or pipe branches.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed partial class PluginTaskRuntime : IAutomationTaskBackend, ICapabilityExecutor, ILeasedDesiredExecutor, IAuthorizationPolicy, ICommandIdSource, IAutomationRuntimeLifetime, IAsyncDisposable
{
    private readonly EngineHost engine;
    private readonly DeadlineScheduler scheduler;
    private readonly SqliteAutomationTaskStore database;
    private readonly EngineHost activity;
    private readonly string sid;
    private readonly ResourceScope scope;
    private readonly AutomationTaskCompiler compiler = new(supportsCompositeDetection: true, supportsRunConditions: true);
    private readonly RuntimeClock clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ImmutableDictionary<string, CapabilityBinding> bindings;
    private readonly Func<uint>? bootIdentity;
    private ImmutableDictionary<string, long> startupTasks = ImmutableDictionary<string, long>.Empty;
    private readonly HashSet<string> firedStartupTasks = [];
    private bool startupDispatched;
    private bool? firstBoot;
    private AutomationTask[] tasks = [];
    private CancellationTokenSource? watchCancel;
    private Task? watcher;
    private ResourceKey[] watching = [];
    private string? lastWatchError;
    private readonly Dictionary<ResourceKey, string> observationReasons = [];
    private volatile bool connected;
    private volatile bool stopping;
    private readonly object statusGate = new();
    private string state = "等待条件", reason = "程序任务等待下一次状态变化。";
    private DateTimeOffset at = DateTimeOffset.MinValue;
    public ImmutableArray<TaskCombination> Combinations { get; }
    public bool SupportsCompositeDetection => true;
    public PluginTaskRuntime(IEnumerable<IAutomationPlugin> plugins, SqliteAutomationTaskStore database, string sid, EngineHost activity, Func<uint>? bootIdentity = null, TimeProvider? windowTime = null,
        Func<Action<bool?>, CancellationToken, Task>? observeRunHdr = null)
    {
        this.database = database; this.sid = sid; this.activity = activity; this.bootIdentity = bootIdentity; this.windowTime = windowTime ?? TimeProvider.System;
        this.observeRunHdr = observeRunHdr;
        scope = ResourceScope.Session(sid, Process.GetCurrentProcess().SessionId);
        var modules = plugins.ToArray();
        if (modules.Select(p => p.Id).Distinct().Count() != modules.Length) throw new ArgumentException("插件重复注册。");
        foreach (var detection in modules.SelectMany(p => p.Detections).DistinctBy(d => d.Descriptor.Id)) compiler.Register(detection);
        foreach (var action in modules.SelectMany(p => p.Actions)) compiler.Register(action);
        Combinations = [.. modules.SelectMany(p => p.Combinations)];
        foreach (var combination in Combinations) compiler.RegisterCombination(combination);
        bindings = modules.SelectMany(p => p.Bindings).Append(WheelBinding()).DistinctBy(b => b.Descriptor.Id).ToImmutableDictionary(b => b.Descriptor.Id);
        if (Combinations.Distinct().Count() != Combinations.Length || bindings.Values.Any(b => b.Descriptor.DesiredState && b.Descriptor.Id != KeepAwakeCapabilities.Hold && !RestorableSettingInvocations.IsRestorable(b.Descriptor.Id) ||
            b.Descriptor.Command && (b.Descriptor.Parameters != TaskInvocationCommands.Schema || b.ParametersKind != ScalarKind.Text)))
            throw new ArgumentException("插件组合重复，或未采用当前会话命令/保持唤醒/可恢复设置租约contract。");
        clock = new RuntimeClock(TimeProvider.System); scheduler = new(clock); engine = new(clock, scheduler, this); engine.Start();
        engine.ConfigureAutomation(this, this);
        foreach (var binding in bindings.Values) engine.RegisterCapability(binding);
    }
    public async Task InitializeAsync()
    {
        tasks = await database.ListAsync();
        RestoreBrightnessWheelOverride();
        foreach (var task in tasks.Where(t => !IsWheelToolTask(t)))
        {
            try { RequireSupportedTask(task); SetTaskRule(task); }
            catch (ArgumentException error) { Record("任务暂不可用", task.Title + "：" + error.Message, ActivityLevel.Warning); }
        }
        startupTasks = tasks.Where(t => !t.Paused && t.Detection.CapabilityId is AxiomStartupPlugin.CapabilityId or TrayTool.Trigger).ToImmutableDictionary(t => t.Id, t => t.Version);
        if (bindings.ContainsKey(AxiomStartupPlugin.CapabilityId))
        {
            try { firstBoot = await database.ClaimBootStartAsync((bootIdentity ?? WindowsBootIdentity.Read)()); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException or Microsoft.Data.Sqlite.SqliteException)
            { Record("首次启动状态未确认", error.Message + " 首次启动任务不会执行；每次启动任务仍可执行。", ActivityLevel.Warning); }
        }
        // Every supported rule can await independently: an async worker owns no waiting thread.
        // Match the persisted task limit, while retaining resource/capability single-flight fences.
        engine.StartExecution(new(concurrency: AutomationTaskLimits.MaximumTasks, maximumAttempts: 1));
        startupDispatched = engine.GetObservationPlan().References.Count == 0;
        await RefreshObservationAsync();
        await RefreshTrayMonitorAsync(tasks.SingleOrDefault(IsTrayToolTask), default);
        StartWheelMonitor();
        runMonitor = MonitorRunConditionsAsync(runLifetime.Token);
    }
    public AuthorizationDecision Authorize(string principal, ResourceKey key, CapabilityOperation operation) => new(
        principal == sid && key.Scope == scope && bindings.TryGetValue(key.CapabilityId, out var b) &&
        (operation == CapabilityOperation.Observe && b.Descriptor.Observe || operation == CapabilityOperation.ExecuteCommand && b.Descriptor.Command ||
            operation == CapabilityOperation.ApplyDesired && (b.Descriptor.Id == KeepAwakeCapabilities.Hold || RestorableSettingInvocations.IsRestorable(b.Descriptor.Id)) && b.Descriptor.DesiredState ||
            operation is CapabilityOperation.Override or CapabilityOperation.ReleaseOverride && (IsBrightness(key.CapabilityId) || key.CapabilityId == BrightnessWheelTool.Command)),
        "same-user-session registered plugin");
    public Guid NextId() => Guid.NewGuid();
    public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken token) =>
        RestorableSettingInvocations.IsRestorable(desired.Key.CapabilityId) ? ApplyRestorableAsync(desired, token) : ApplyKeepAwakeAsync(desired, token);
    public async ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken token)
    {
        var attempts = new List<WindowAttempt>(); var sendStarted = false;
        try
        {
            if (stopping || !Authorize(command.Principal, command.Key, CapabilityOperation.ExecuteCommand).Allowed || command.Arguments.Text is null)
                return new(OutcomeStatus.Rejected, "程序命令权限不匹配。", command.CommandId);
            var invocation = JsonSerializer.Deserialize<TaskInvocation>(command.Arguments.Text) ?? throw new InvalidDataException("能力参数无效。");
            if (invocation.CapabilityId == BrightnessWheelTool.Command && command.Key == WheelKey)
                return await ExecuteWheelCommandAsync(command, invocation, token);
            if (wheelTakenOver && IsBrightness(invocation.CapabilityId)) throw new UnauthorizedAccessException("亮度已由滚轮手动接管；请在工具箱恢复自动亮度。");
            var compiled = compiler.Catalog.Single(c => c.Kind == TaskCapabilityKind.Action && c.Id == invocation.CapabilityId);
            AutomationTaskCompiler.ValidateInvocation(invocation, compiled);
            if (invocation.CapabilityId != command.Key.CapabilityId) throw new InvalidDataException("能力参数与命令不匹配。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(command.Deadline - DateTimeOffset.UtcNow);
            if (!await BeginWindowAttemptsAsync(command, attempts, timeout.Token))
                return new(OutcomeStatus.Rejected, "此时段已完成、结果未确认或尚不满足重试条件；没有再次执行动作。", command.CommandId);
            ValidateWindowAttemptsBeforeSend(attempts, timeout.Token);
            using var pipe = Client(); await pipe.ConnectForUserAsync(2000, timeout.Token);
            ValidateWindowAttemptsBeforeSend(attempts, timeout.Token);
            // The durable attempt exists before the first possible side effect. Any failure
            // from beginning this write onward is uncertain unless a valid final receipt arrives.
            var id = Guid.NewGuid().ToString("N");
            sendStarted = true;
            await ProviderProtocol.WriteAsync(pipe, new(1, "invoke", id, command.Deadline, Invocation: invocation, CommandId: command.CommandId), timeout.Token);
            var response = await ProviderProtocol.ReadAsync(pipe, timeout.Token);
            if (response.Version != 1 || response.Type != "result" || response.RequestId != id || response.CommandId != command.CommandId ||
                response.Status is not (OutcomeStatus.Succeeded or OutcomeStatus.Failed or OutcomeStatus.Rejected) || string.IsNullOrWhiteSpace(response.Reason))
                throw new InvalidDataException("Provider 回执无效。");
            await CompleteWindowAttemptsAsync(attempts, response.Status.Value);
            Record(response.Status == OutcomeStatus.Succeeded ? "自动化动作已完成" : "自动化动作未完成", response.Reason,
                response.Status == OutcomeStatus.Succeeded ? ActivityLevel.Success : ActivityLevel.Error);
            return TrayOutcome(command, new(response.Status.Value, response.Reason, command.CommandId));
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            try
            {
                if (!sendStarted && error is OperationCanceledException)
                    foreach (var attempt in attempts)
                        await database.AbandonUnsentWindowOnceAttemptAsync(attempt.TaskId, attempt.Date, attempt.AttemptId, windowTime.GetUtcNow());
                else await CompleteWindowAttemptsAsync(attempts, sendStarted ? OutcomeStatus.Unknown : OutcomeStatus.Rejected);
            }
            catch (Exception persistence) when (persistence is not (OutOfMemoryException or StackOverflowException))
            { Record("时段执行记录未确认", persistence.Message + " 为避免重复操作，不会自动重发。", ActivityLevel.Warning); }
            var message = sendStarted ? error is OperationCanceledException ? "动作已取消或通信超时；无法确认程序是否已执行，不会自动重试。" : error.Message
                : error is OperationCanceledException ? "动作尚未发出，已取消；未消耗本时段的执行或重试次数。" : "动作尚未发出：" + error.Message + " 将按任务的失败重试设置处理。";
            Record("自动化动作未确认", message, ActivityLevel.Warning);
            return TrayOutcome(command, new(sendStarted ? OutcomeStatus.Unknown : OutcomeStatus.Rejected, message, command.CommandId));
        }
    }
    private bool IsSupportedTask(AutomationTask task)
    {
        try
        {
            compiler.Validate(task);
            return TaskDetectionExpressions.ValidateTask(task).All(leaf => Combinations.Contains(new(leaf.CapabilityId, task.Action.CapabilityId)));
        }
        catch (ArgumentException) { return false; }
    }
    private void RequireSupportedTask(AutomationTask task)
    {
        if (!IsSupportedTask(task)) throw new ArgumentException("插件不支持所有条件与同一动作的组合。");
    }
    public Task<AutomationTask> SaveAsync(AutomationTask task, CancellationToken token)
    {
        if (task.Id == TrayTool.TaskId || task.Detection.CapabilityId == TrayTool.Trigger || task.Id == BrightnessWheelTool.TaskId || task.Detection.CapabilityId == BrightnessWheelTool.Trigger) throw new InvalidOperationException("请在工具箱中管理此工具。");
        return SaveTaskAsync(task, token);
    }
    private async Task<AutomationTask> SaveTaskAsync(AutomationTask task, CancellationToken token)
    {
        RequireSupportedTask(task);
        _ = compiler.Compile(task, sid, scope);
        await gate.WaitAsync(token);
        try
        {
            if (stopping) throw new InvalidOperationException("后台正在停止，请重新打开 Axiom。");
            var candidate = tasks.Where(t => t.Id != task.Id).Append(task).Where(t => !t.Paused && IsSupportedTask(t))
                .SelectMany(t => { var rule = compiler.Compile(t, sid, scope); return rule.Condition.Dependencies.Append(rule.Action.Key).Where(k => bindings[k.CapabilityId].Descriptor.Observe); }).Distinct().Count();
            if (candidate > 128) throw new ArgumentException("启用的插件任务最多检测 128 个资源。");
            var saved = await database.SaveAsync(task, token);
            lock (statusGate)
            { SetTaskRule(saved); tasks = tasks.Where(t => t.Id != saved.Id).Append(saved).ToArray(); PublishRunStates(); }
            WakeRunConditions();
            await RefreshObservationAsync();
            await RefreshWindowTaskAsync(saved, token);
            Record(saved.Paused ? "任务已暂停" : "任务已保存", saved.Title + (saved.Paused ? "：尚未执行的动作已取消。" : "：等待下一次条件成立。"), ActivityLevel.Success);
            return saved;
        }
        finally { gate.Release(); }
    }
    public async Task DeleteAsync(string id, long expectedVersion, CancellationToken token)
    {
        if (id is TrayTool.TaskId or BrightnessWheelTool.TaskId) throw new InvalidOperationException("请在工具箱中管理此工具。");
        await gate.WaitAsync(token);
        try
        {
            await database.DeleteAsync(id, expectedVersion, token);
            lock (statusGate) { engine.RemoveRule(id); tasks = tasks.Where(t => t.Id != id).ToArray(); runPauses.Remove(id); PublishRunStates(); }
            WakeRunConditions();
            await RefreshObservationAsync(); Record("任务已删除", "尚未执行的动作已取消。", ActivityLevel.Success);
        }
        finally { gate.Release(); }
    }
    public async Task<AutomationSnapshot> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            lock (statusGate) return new([.. tasks.Where(t => !IsTrayToolTask(t) && !IsWheelToolTask(t))], [.. compiler.Catalog.Where(c => c.Id != TrayTool.Trigger)], [.. Combinations.Where(c => c.DetectionId != TrayTool.Trigger)], watching.Length == 0 || connected &&
                (firstBoot is not null || !tasks.Any(t => !t.Paused && t.Detection.CapabilityId == AxiomStartupPlugin.CapabilityId && t.Detection.Parameters["frequency"] == "firstBoot")), state, reason, at,
                [.. engine.ReadSnapshot().States.Where(p => watching.Contains(p.Key)).Select(p => new TaskObservation(
                    p.Key.CapabilityId == HdrStateCapabilities.State ? "主屏HDR状态" : p.Key.CapabilityId == ScreenContentCapabilities.Bright ? "主屏画面明亮面积" : p.Key.CapabilityId == KeepAwakeCapabilities.Hold ? p.Key.ResourceId == "display" ? "系统及屏幕保持唤醒" : "系统保持唤醒" : p.Key.CapabilityId == AxiomStartupPlugin.CapabilityId ? tasks.SingleOrDefault(t => t.Id == p.Key.ResourceId)?.Title ?? "Axiom 启动" : p.Key.CapabilityId == SystemEventCapabilities.Event ? SystemEventName(p.Key.ResourceId) : p.Key.CapabilityId == AudioCapabilities.Changed ? p.Key.ResourceId == "output" ? "默认播放设备变化" : "默认通信麦克风变化" : p.Key.CapabilityId == OnceTimeCapabilities.Due ? OnceTimeCapabilities.Label(p.Key.ResourceId) : p.Key.CapabilityId == FileArrivalCapabilities.Arrived ? FileArrivalCapabilities.Label(p.Key.ResourceId) : p.Key.CapabilityId == WindowOnceCapabilities.Due ? WindowOnceCapabilities.Label(p.Key.ResourceId) : p.Key.CapabilityId == WindowedIntervalCapabilities.Due ? WindowedIntervalCapabilities.Label(p.Key.ResourceId) : p.Key.CapabilityId == DailyTimeCapabilities.Due ? DailyTimeCapabilities.Label(p.Key.ResourceId) : p.Key.CapabilityId == IntervalCapabilities.Elapsed ? "每隔 " + p.Key.ResourceId + " 分钟" : p.Key.ResourceId,
                    p.Key.CapabilityId == ScreenContentCapabilities.Bright ? observationReasons.GetValueOrDefault(p.Key, "正在等待主屏画面检测。") : p.Value.Observation.Quality != StateQuality.Known ? observationReasons.GetValueOrDefault(p.Key, "状态未知") : p.Key.CapabilityId == WindowOnceCapabilities.Due ? observationReasons.GetValueOrDefault(p.Key, "等待选定时段，成功后本时段不再重复。") : p.Key.CapabilityId == OnceTimeCapabilities.Due ? observationReasons.GetValueOrDefault(p.Key, "等待指定日期和时刻") : p.Key.CapabilityId == HotkeyCapabilities.Pressed
                        ? "等待快捷键" : p.Value.Observation.Value?.Number is not null ? "等待下一次事件" : p.Key.CapabilityId == AxiomStartupPlugin.CapabilityId
                        ? p.Value.Observation.Value?.Boolean == true ? "已触发启动条件" : "等待下次启动"
                        : p.Key.CapabilityId == HdrStateCapabilities.State ? p.Value.Observation.Value?.Boolean == true ? "已开启" : "已关闭" : p.Key.CapabilityId == KeepAwakeCapabilities.Hold ? p.Value.Observation.Value?.Boolean == true ? "Axiom 正持有唤醒请求" : "未持有唤醒请求" : p.Key.CapabilityId == WindowPresenceCapabilities.Visible ? p.Value.Observation.Value?.Boolean == true ? "窗口已显示" : "没有可见主窗口" : p.Key.CapabilityId == SessionLockStateCapabilities.State ? p.Value.Observation.Value?.Boolean == true ? "已锁定" : "未锁定" : p.Key.CapabilityId == ForegroundCapabilities.Active ? p.Value.Observation.Value?.Boolean == true ? "正在前台" : "不在前台" : p.Value.Observation.Value?.Boolean == true ? "正在运行" : "未运行")),
                 .. tasks.Where(t => t.TimeWindow is not null).Select(t => new TaskObservation(t.Title + " · 时间限制",
                    t.Paused ? "已暂停" : engine.GetRuleEvaluation(t.Id).Reason.StartsWith("OutsideTimeWindow", StringComparison.Ordinal)
                        ? "上次触发已跳过：不在允许时段，进入时段不会补执行。"
                        : t.TimeWindow!.Contains(clock.UtcNow, clock.LocalTimeZone) ? "读取时在允许时段；等待新的条件边沿。" : "读取时在时段外；新的触发将跳过。", t.Id))], CompositeDetectionVersion: 1, RunConditionsVersion: 1, RunStates: CurrentRunStates());
        }
        finally { gate.Release(); }
    }
    private async Task RefreshObservationAsync(bool freshBaseline = false)
    {
        var plan = engine.GetObservationPlan(); var resources = plan.References.Keys.ToArray();
        if (!freshBaseline && watching.ToHashSet().SetEquals(resources))
        {
            if (connected || resources.Length == 0) engine.AcknowledgeObservationPlan(plan.Version, resources, guarantees: plan.Requirements);
            return;
        }
        if (watchCancel is not null)
        {
            watchCancel.Cancel(); if (watcher is not null) await watcher; watchCancel.Dispose();
            watchCancel = null; watcher = null;
        }
        watching = resources; connected = false;
        lock (statusGate) foreach (var key in observationReasons.Keys.Where(k => !resources.Contains(k)).ToArray()) observationReasons.Remove(key);
        if (resources.Length == 0) { engine.AcknowledgeObservationPlan(plan.Version, [], guarantees: plan.Requirements); return; }
        watchCancel = new(); watcher = WatchAsync(resources, watchCancel.Token);
    }
    private NamedPipeClientStream Client() => LocalUserPipe.Create(ProviderProtocol.PipeName(sid));
    private async Task WatchAsync(ResourceKey[] held, CancellationToken token)
    {
        var keys = held.Where(k => bindings[k.CapabilityId].Descriptor.Observe && bindings[k.CapabilityId].Descriptor.Location == ExecutionLocation.SessionAgent).ToArray();
        var local = held.Where(k => k.CapabilityId == AxiomStartupPlugin.CapabilityId).ToArray();
        var wire = keys.Select(k => new ProviderResource(k.CapabilityId, k.ResourceId)).ToArray();
        var mapping = wire.Zip(keys).ToDictionary(p => p.First, p => p.Second);
        while (!token.IsCancellationRequested)
        {
            var source = keys.Length == 0 ? null : engine.RegisterSource("session-plugin-observation", keys);
            var startupSource = local.Length == 0 ? null : engine.RegisterSource("host-startup", local);
            var currentWatch = BeginWindowWatch(source, keys, token);
            var input = startupSource is null ? null : new StartupInput(startupSource, local);
            long expected = 0;
            try
            {
                if (startupSource is not null)
                    lock (statusGate) { startupInput = input; engine.ApplySnapshot(startupSource, input!.Sequence++, local.Select(StartupBaseline).ToArray()); }
                using var pipe = Client(); await pipe.ConnectForUserAsync(2000, token);
                var id = Guid.NewGuid().ToString("N");
                await ProviderProtocol.WriteAsync(pipe, new(1, "observe", id, DateTimeOffset.UtcNow.AddSeconds(5), Resources: wire,
                    RequiredCapabilities: held.Where(k => bindings[k.CapabilityId].Descriptor.Location == ExecutionLocation.SessionAgent).Select(k => k.CapabilityId).Distinct().ToArray()), token);
                while (!token.IsCancellationRequested)
                {
                    var response = await ProviderProtocol.ReadAsync(pipe, token);
                    if (response.Type == "result") throw new InvalidDataException(response.Reason ?? "Provider 订阅失败。");
                    if (response.Version != 1 || response.RequestId != id || response.Sequence != expected++ || response.Samples is not { } samples ||
                        response.Type is not ("snapshot" or "update")) throw new InvalidDataException("Provider 检测流中断或缺失事件。");
                    if (samples.Any(s => !mapping.ContainsKey(s.Resource)) || samples.Select(s => s.Resource).Distinct().Count() != samples.Length)
                        throw new InvalidDataException("Provider 返回未请求的检测资源。");
                    lock (statusGate)
                        foreach (var sample in samples)
                        {
                            var key = mapping[sample.Resource];
                            if (sample.Reason is { Length: > 300 }) throw new InvalidDataException("检测原因过长。");
                            if (sample.Quality != StateQuality.Known && !string.IsNullOrWhiteSpace(sample.Reason))
                            {
                                if (observationReasons.GetValueOrDefault(key) != sample.Reason) Record("检测暂不可用", (key.CapabilityId == ScreenContentCapabilities.Bright ? "主屏画面明亮面积" : key.ResourceId) + "：" + sample.Reason, ActivityLevel.Warning);
                                observationReasons[key] = sample.Reason;
                            }
                            else if (key.CapabilityId is OnceTimeCapabilities.Due or ScreenContentCapabilities.Bright && !string.IsNullOrWhiteSpace(sample.Reason)) observationReasons[key] = sample.Reason;
                            else observationReasons.Remove(key);
                        }
                    var observations = samples.Select(s => new Observation(mapping[s.Resource], s.Quality,
                        s.Quality == StateQuality.Known ? s.Boolean is { } b ? StateValue.From(new(s.SchemaId, s.SchemaVersion), b) :
                            s.Number is { } n ? StateValue.From(new(s.SchemaId, s.SchemaVersion), n) :
                            s.Text is { } t ? StateValue.From(new(s.SchemaId, s.SchemaVersion), t) : throw new InvalidDataException("检测值无效。") : null, DateTimeOffset.UtcNow)).ToArray();
                    if (response.Type == "snapshot")
                    {
                        if (observations.Length != keys.Length) throw new InvalidDataException("Provider 快照不完整。");
                        if (source is not null)
                            lock (statusGate) engine.ApplySnapshot(source, currentWatch!.Sequence++, WindowSnapshot(currentWatch, observations));
                        // A live subscription can legitimately contain an unavailable individual resource.
                        // Keep unrelated rules working; Engine retains Unknown for that resource.
                        connected = true;
                        lastWatchError = null;
                        var plan = engine.GetObservationPlan();
                        if (engine.AcknowledgeObservationPlan(plan.Version, held, guarantees: plan.Requirements))
                        {
                            token.ThrowIfCancellationRequested();
                            lock (statusGate)
                            {
                                if (input is not null) input.Ready = true;
                                if (!startupDispatched)
                                {
                                    startupDispatched = true;
                                    foreach (var key in local)
                                    {
                                        var task = tasks.SingleOrDefault(t => t.Id == key.ResourceId);
                                        if (task is null || task.Paused || !startupTasks.TryGetValue(task.Id, out var version) || version != task.Version) continue;
                                        if (task.Detection.Parameters["frequency"] == "firstBoot" && firstBoot != true)
                                        {
                                            if (firstBoot == false) Record("首次启动已跳过", task.Title + "：本次开机已启动过 Axiom 后台，等待下次开机。", ActivityLevel.Information);
                                            continue;
                                        }
                                        BeginTrayRun(task);
                                        firedStartupTasks.Add(task.Id);
                                        engine.Update(startupSource!, input!.Sequence++, new(key, StateQuality.Known, StateValue.From(new("axiom.bool", 1), true), DateTimeOffset.UtcNow));
                                        CheckTrayDecision(task);
                                        Record("启动条件已触发", task.Title + "：" + (task.Detection.Parameters["frequency"] == "firstBoot" ? "本次开机首次启动" : "Axiom 后台启动") + "，按已保存参数执行动作。", ActivityLevel.Information);
                                    }
                                }
                            }
                            lock (statusGate) FlushTrayTrigger();
                            await RefreshKnownWindowTasksAsync(currentWatch, token);
                        }
                    }
                    else
                    {
                        if (!connected || observations.Length != 1) throw new InvalidDataException("检测事件无效。");
                        lock (statusGate)
                        {
                            if (observations[0].Key.CapabilityId == WindowOnceCapabilities.Due && observations[0].Quality == StateQuality.Known)
                            {
                                var key = observations[0].Key;
                                var wasKnown = currentWatch!.ProviderKnown.Contains(key);
                                SetWindowProviderQuality(currentWatch, key, true);
                                if (!wasKnown)
                                    engine.Update(source!, currentWatch.Sequence++, new(key, StateQuality.Known,
                                        StateValue.From(new("axiom.event-count", 1), currentWatch.Counters[key]), clock.UtcNow));
                                OfferWindowOpportunity(currentWatch, key);
                            }
                            else
                            {
                                if (observations[0].Key.CapabilityId == WindowOnceCapabilities.Due)
                                    SetWindowProviderQuality(currentWatch!, observations[0].Key, false);
                                engine.Update(source!, currentWatch!.Sequence++, observations[0]);
                            }
                        }
                        if (observations[0].Key.CapabilityId == HotkeyCapabilities.Pressed && observations[0].Quality == StateQuality.Known)
                            Record("快捷键已按下", (samples[0].Reason ?? observations[0].Key.ResourceId) + "：已交给规则判断。", ActivityLevel.Information);
                        if (observations[0].Key.CapabilityId == SystemEventCapabilities.Event && observations[0].Quality == StateQuality.Known)
                            Record("系统通知已到达", SystemEventName(observations[0].Key.ResourceId) + "：已交给规则判断。", ActivityLevel.Information);
                    }
                }
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                if (!token.IsCancellationRequested && (connected || lastWatchError != error.Message))
                { lastWatchError = error.Message; Record("自动化检测中断", error.Message + " 尚未执行的动作已取消，后台会尝试重新连接。", ActivityLevel.Warning); }
            }
            finally { lock (statusGate) { if (ReferenceEquals(startupInput, input)) startupInput = null; } connected = false; EndWindowWatch(currentWatch); if (source is not null) engine.ReleaseSource(source); if (startupSource is not null) engine.ReleaseSource(startupSource); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); } catch (OperationCanceledException) { break; }
        }
    }
    private static string SystemEventName(string kind) => SystemEventCapabilities.Descriptor.Fields[0].Choices.FirstOrDefault(c => c.Value == kind)?.Label ?? "系统事件";
    private Observation StartupBaseline(ResourceKey key)
    {
        var task = tasks.Single(t => t.Id == key.ResourceId);
        var unknown = task.Detection.Parameters["frequency"] == "firstBoot" && firstBoot is null;
        return new(key, unknown ? StateQuality.Unknown : StateQuality.Known,
            unknown ? null : StateValue.From(new("axiom.bool", 1), firedStartupTasks.Contains(task.Id)), DateTimeOffset.UtcNow);
    }
    private void Record(string title, string detail, ActivityLevel level)
    {
        var now = DateTimeOffset.UtcNow; lock (statusGate) { state = title; reason = detail; at = now; }
        activity.RecordActivity(new(now, "自动化任务", title, detail, level));
    }
    public async Task EnsureSafeToStopAsync(CancellationToken token)
    {
        stopping = true;
        runLifetime.Cancel(); if (runMonitor is not null) await runMonitor.WaitAsync(token);
        await StopWheelMonitorAsync(token);
        await gate.WaitAsync(token);
        try
        {
            stopping = true;
            trayLifetime.Cancel();
            await RefreshTrayMonitorAsync(null, token);
            foreach (var task in tasks) engine.RemoveRule(task.Id);
            watchCancel?.Cancel(); if (watcher is not null) await watcher.WaitAsync(token);
            await engine.WaitForIdleAsync(token);
            await DrainPowerLeasesAsync(token);
            await DrainRestorableSettingsAsync(token);
            await DrainWindowRetriesAsync(token);
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        stopping = true; runLifetime.Cancel(); if (runMonitor is not null) await runMonitor;
        await StopWheelMonitorAsync(default);
        trayLifetime.Cancel(); await RefreshTrayMonitorAsync(null, default);
        watchCancel?.Cancel(); if (watcher is not null) await watcher;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await engine.StopAndWaitAsync(deadline.Token);
        await DrainPowerLeasesAsync(deadline.Token);
        await DrainRestorableSettingsAsync(deadline.Token);
        await DrainWindowRetriesAsync(deadline.Token);
        if (trayFlush is not null) try { await trayFlush; } catch (OperationCanceledException) { }
        engine.Dispose(); scheduler.Dispose(); watchCancel?.Dispose(); gate.Dispose(); trayToolGate.Dispose(); trayLifetime.Dispose(); runLifetime.Dispose();
    }
}

internal sealed class AutomationRuntimeLifetime(params IAutomationRuntimeLifetime[] runtimes) : IAutomationRuntimeLifetime
{
    public async Task EnsureSafeToStopAsync(CancellationToken token)
    { foreach (var runtime in runtimes) await runtime.EnsureSafeToStopAsync(token); }
}
