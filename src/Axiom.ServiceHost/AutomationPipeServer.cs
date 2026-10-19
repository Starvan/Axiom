using System.Collections.Immutable;
using System.IO.Pipes;
using System.Security.Principal;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;
using Axiom.Infrastructure;

namespace Axiom.ServiceHost;

internal sealed class AutomationPipeServer(ImmutableArray<IAutomationTaskBackend> backends,
    IAutomationRuntimeLifetime lifetime, string sid, CancellationTokenSource exit, LoginStartupRegistration startup, ShortcutCoordinator shortcuts,
    EngineHost engine, WorkbenchUtilities utilities, UsageObservationController usage)
{
    private readonly AutomationTaskRouter router = new(backends);
    private readonly HashSet<Task> clients = [];
    public async Task RunAsync()
    {
        while (!exit.IsCancellationRequested)
        {
            var pipe = LocalPipeFactory.Create(AutomationProtocol.PipeName(sid), sid);
            try { await pipe.WaitForConnectionAsync(exit.Token); }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            clients.RemoveWhere(t => t.IsCompleted);
            if (clients.Count >= 8) { pipe.Dispose(); continue; }
            clients.Add(HandleAsync(pipe));
        }
        await Task.WhenAll(clients);
    }
    private async Task<AutomationSnapshot> ReadAsync(CancellationToken token)
    {
        var snapshots = new List<AutomationSnapshot>();
        foreach (var backend in backends) snapshots.Add(await backend.ReadAsync(token));
        var latest = snapshots.MaxBy(x => x.AtUtc)!;
        return latest with
        {
            Tasks = [.. snapshots.SelectMany(x => x.Tasks)],
            Capabilities = [.. snapshots.SelectMany(x => x.Capabilities).DistinctBy(x => (x.Kind, x.Id))],
            Combinations = [.. snapshots.SelectMany(x => x.Combinations)],
            Observations = [.. snapshots.SelectMany(x => x.Observations)],
            DetectionAvailable = snapshots.All(x => x.DetectionAvailable),
            CompositeDetectionVersion = snapshots.Max(x => x.CompositeDetectionVersion)
        };
    }
    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(exit.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            AutomationMessage? request = null;
            var authorized = false;
            try
            {
                request = await AutomationProtocol.ReadAsync(pipe, deadline.Token);
                string? actualSid = null;
                pipe.RunAsClient(() => actualSid = WindowsIdentity.GetCurrent().User?.Value);
                if (actualSid != sid) throw new UnauthorizedAccessException("访问被拒绝。");
                GameHdrPipePeer.RequireSameSession(pipe, request.Type == "restore-shortcuts" ? "Axiom.SessionAgent.exe" : null);
                AutomationProtocol.ValidateRequest(request, DateTimeOffset.UtcNow);
                authorized = true;
                if (request.Type is "read-brightness-wheel" or "save-brightness-wheel" or "resume-brightness-wheel")
                {
                    var runtime = backends.OfType<PluginTaskRuntime>().Single();
                    var wheel = request.Type == "save-brightness-wheel" ? await runtime.SaveBrightnessWheelAsync(request.WheelSettings!, deadline.Token) :
                        request.Type == "resume-brightness-wheel" ? await runtime.ResumeAutomaticBrightnessAsync(request.ExpectedVersion!.Value, deadline.Token) : runtime.ReadBrightnessWheel();
                    await AutomationProtocol.WriteAsync(pipe, new(1, "brightness-wheel", request.RequestId, Wheel: wheel), deadline.Token); return;
                }
                if (request.Type == "shutdown") deadline.CancelAfter(request.DeadlineUtc!.Value - DateTimeOffset.UtcNow);
                if (request.Type == "usage")
                {
                    var result = await usage.HandleAsync(request, await ReadAsync(deadline.Token), deadline.Token);
                    if (request.Usage!.Operation == "start" && result.UsageSnapshot?.ActiveId is { } active)
                    {
                        // This explicit UI lease prevents invisible observation after the window exits.
                        try
                        {
                            await AutomationProtocol.WriteAsync(pipe, result, deadline.Token);
                            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                            var disconnected = pipe.ReadExactlyAsync(new byte[1], deadline.Token).AsTask();
                            var ended = usage.WaitForEndAsync(active, deadline.Token);
                            if (await Task.WhenAny(disconnected, ended) == ended && !exit.IsCancellationRequested)
                            {
                                await ended;
                                using var stoppedWrite = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                                await AutomationProtocol.WriteAsync(pipe, new(1, "usage-ended", request.RequestId), stoppedWrite.Token);
                            }
                            deadline.Cancel();
                            try { await disconnected; } catch (Exception error) when (error is IOException or OperationCanceledException) { }
                        }
                        finally { await usage.EndLeaseAsync(active); }
                    }
                    else await AutomationProtocol.WriteAsync(pipe, result, deadline.Token);
                    return;
                }
                if (request.Type is "read-tray-tool" or "save-tray-tool" or "run-tray-tool")
                {
                    var runtime = backends.OfType<PluginTaskRuntime>().Single();
                    var tool = request.Type == "save-tray-tool" ? await runtime.SaveTrayToolAsync(request.TraySettings!, deadline.Token) :
                        request.Type == "run-tray-tool" ? await runtime.RunTrayToolAsync(request.ExpectedVersion!.Value, deadline.Token) : runtime.ReadTrayTool();
                    await AutomationProtocol.WriteAsync(pipe, new(1, "tray-tool", request.RequestId, TrayTool: tool), deadline.Token); return;
                }
                if (request.Type == "read-tray")
                {
                    var catalog = await TrayCatalogClient.ReadAsync(sid, deadline.Token);
                    await AutomationProtocol.WriteAsync(pipe, new(1, "tray", request.RequestId, Tray: catalog), deadline.Token); return;
                }
                if (request.Type is "read-groups" or "save-group" or "delete-group" or "scan-steam" or "read-steam-tool" or "save-steam-settings" or "refresh-steam-tool")
                {
                    var result = await utilities.HandleAsync(request, deadline.Token);
                    if (request.Type is "save-group" or "delete-group") engine.RecordActivity(new(DateTimeOffset.UtcNow, "进程组",
                        request.Type == "save-group" ? "进程组已保存" : "进程组已删除", request.Group?.Name ?? "组已移除，已有任务保持原进程名单。", ActivityLevel.Success));
                    await AutomationProtocol.WriteAsync(pipe, result, deadline.Token); return;
                }
                if (request.Type == "read-activity")
                {
                    var activity = await engine.ReadActivityAsync(actualSid!, request.ActivityBeforeId, request.ActivityPageSize ?? SqliteActivityStore.DefaultPageSize, deadline.Token);
                    await AutomationProtocol.WriteAsync(pipe, new(1, "activity", request.RequestId, Activity: activity), deadline.Token);
                    return;
                }
                if (request.Type == "clear-old-activity")
                {
                    var cleared = await engine.ClearOldActivityAsync(actualSid!, DateTimeOffset.UtcNow, deadline.Token);
                    if (cleared.DeletedCount > 0) engine.RecordActivity(new(DateTimeOffset.UtcNow, "活动记录", "旧记录已清理",
                        $"已清除 {cleared.DeletedCount} 条七天前的活动，最近七天的记录已保留。", ActivityLevel.Success));
                    await AutomationProtocol.WriteAsync(pipe, new(1, "activity-cleared", request.RequestId, ActivityCleanup: cleared), deadline.Token);
                    return;
                }
                if (request.Type is "read-shortcuts" or "restore-shortcuts" or "save-shortcut" or "delete-shortcut")
                {
                    var settings = await shortcuts.HandleAsync(request, deadline.Token);
                    if (request.Type is "save-shortcut" or "delete-shortcut")
                        engine.RecordActivity(new(DateTimeOffset.UtcNow, "设置", request.Type == "delete-shortcut" ? "快捷键已删除" : "快捷键已保存",
                            "全局快捷键配置已更新。", ActivityLevel.Success));
                    await AutomationProtocol.WriteAsync(pipe, new(1, "shortcuts", request.RequestId, Shortcuts: settings), deadline.Token);
                    return;
                }
                if (request.Type is "read-startup" or "set-startup")
                {
                    var setting = request.Type == "set-startup" ? startup.Set(request.StartupEnabled!.Value) : startup.Read();
                    if (request.Type == "set-startup") engine.RecordActivity(new(DateTimeOffset.UtcNow, "设置",
                        setting.Error is null ? request.StartupEnabled == true ? "登录启动已开启" : "登录启动已关闭" : "登录启动设置失败",
                        setting.Error ?? "当前用户的登录启动登记已更新。", setting.Error is null ? ActivityLevel.Success : ActivityLevel.Error));
                    await AutomationProtocol.WriteAsync(pipe, new(1, "startup", request.RequestId, Startup: setting), deadline.Token);
                    return;
                }
                AutomationTask? saved = null;
                if (request.Type == "save")
                {
                    var backend = router.Resolve(request.Task!);
                    var existing = (await ReadAsync(deadline.Token)).Tasks.SingleOrDefault(x => x.Id == request.Task!.Id);
                    if (existing is not null && !ReferenceEquals(router.Resolve(existing), backend))
                        throw new InvalidOperationException("更换执行适配器需要新建任务，原任务不会被覆盖。");
                    saved = await backend.SaveAsync(request.Task!, deadline.Token);
                }
                if (request.Type == "delete")
                {
                    var snapshot = await ReadAsync(deadline.Token);
                    var task = snapshot.Tasks.SingleOrDefault(x => x.Id == request.Id) ?? throw new InvalidOperationException("任务已不存在。");
                    await router.Resolve(task).DeleteAsync(task.Id, request.ExpectedVersion!.Value, deadline.Token);
                }
                if (request.Type == "shutdown")
                {
                    await lifetime.EnsureSafeToStopAsync(deadline.Token);
                    await AutomationProtocol.WriteAsync(pipe, new(1, "stopping", request.RequestId), deadline.Token);
                    exit.Cancel(); return;
                }
                var type = request.Type == "save" ? "saved" : request.Type == "delete" ? "deleted" : "snapshot";
                await AutomationProtocol.WriteAsync(pipe, new(1, type, request.RequestId, Task: saved, Snapshot: await ReadAsync(deadline.Token)), deadline.Token);
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                if (authorized && request?.Type is "save" or "delete" or "save-shortcut" or "delete-shortcut" or "set-startup" or "shutdown" or "save-group" or "delete-group" or "save-steam-settings" or "refresh-steam-tool" or "save-tray-tool" or "run-tray-tool")
                    engine.RecordActivity(new(DateTimeOffset.UtcNow, "操作", "操作未完成", error.Message, ActivityLevel.Error,
                        request.Task?.Title));
                using var errorTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await AutomationProtocol.WriteAsync(pipe, new(1, "error", request?.RequestId ?? "rejected", Error: error.Message), errorTimeout.Token); }
                catch (Exception writeError) when (writeError is IOException or OperationCanceledException) { }
            }
        }
    }
}
