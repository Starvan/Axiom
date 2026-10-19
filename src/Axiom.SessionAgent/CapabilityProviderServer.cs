using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

// The transport knows only registered capability IDs, never individual program parameters.
internal sealed class CapabilityProviderServer(string sid, IEnumerable<ISessionCapabilityProvider> providers, BrightnessWheelAgent? wheel = null)
{
    // One invocation per supported task, plus observation and short-lived catalog reads.
    private const int MaximumClients = AutomationTaskLimits.MaximumTasks + 8;
    private readonly Dictionary<string, ISessionCapabilityProvider> registry = providers
        .SelectMany(p => p.Capabilities.Select(id => (id, p))).ToDictionary(x => x.id, x => x.p, StringComparer.Ordinal);
    private readonly HashSet<Task> clients = [];
    public async Task RunAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            var pipe = GameHdrLocalPipeFactory.Create(ProviderProtocol.PipeName(sid), sid, MaximumClients + 1);
            try { await pipe.WaitForConnectionAsync(stopping); }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            clients.RemoveWhere(t => t.IsCompleted);
            if (clients.Count >= MaximumClients) { pipe.Dispose(); continue; }
            clients.Add(HandleAsync(pipe, stopping));
        }
        await Task.WhenAll(clients);
    }
    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stopping)
    {
        using (pipe)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping))
        {
            lifetime.CancelAfter(TimeSpan.FromSeconds(10));
            ProviderMessage? request = null;
            try
            {
                request = await ProviderProtocol.ReadAsync(pipe, lifetime.Token);
                string? caller = null; pipe.RunAsClient(() => caller = WindowsIdentity.GetCurrent().User?.Value);
                if (caller != sid) throw new UnauthorizedAccessException("Provider 用户不匹配。");
                GameHdrPipePeer.RequireHost(pipe);
                ProviderProtocol.Validate(request, DateTimeOffset.UtcNow);
                if (request.Type == "observe-brightness-wheel")
                {
                    lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
                    await (wheel ?? throw new IOException("没有滚轮监听服务。")).ObserveAsync(pipe, request, lifetime.Token); return;
                }
                if (request.Type == "abandon-brightness")
                {
                    lifetime.CancelAfter(request.DeadlineUtc!.Value - DateTimeOffset.UtcNow);
                    var drained = WindowsHardwareBrightness.BeginManualTakeover();
                    await ProviderProtocol.WriteAsync(pipe, new(1, "brightness-abandon-marked", request.RequestId, Status: OutcomeStatus.Succeeded, Reason: "已阻止新准入并放弃旧亮度恢复。"), lifetime.Token);
                    await drained.WaitAsync(lifetime.Token);
                    await ProviderProtocol.WriteAsync(pipe, new(1, "brightness-abandon-complete", request.RequestId, Status: OutcomeStatus.Succeeded, Reason: "旧亮度原生工作和资源已结束。"), lifetime.Token); return;
                }
                if (request.Type == "resume-brightness")
                {
                    lifetime.CancelAfter(request.DeadlineUtc!.Value - DateTimeOffset.UtcNow);
                    await WindowsHardwareBrightness.EndManualTakeoverAsync().WaitAsync(lifetime.Token);
                    await ProviderProtocol.WriteAsync(pipe, new(1, "brightness-resumed", request.RequestId, Status: OutcomeStatus.Succeeded, Reason: "已释放手动亮度准入门禁。"), lifetime.Token); return;
                }
                if (request.Type == "observe-tray")
                {
                    if (!registry.ContainsKey(TrayCapabilities.Hide)) throw new ArgumentException("会话程序缺少托盘能力。");
                    lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
                    await TrayToolObservationStream.RunAsync(pipe, request, lifetime.Token); return;
                }
                if (request.Type == "observe-usage")
                {
                    lifetime.CancelAfter(TimeSpan.FromMinutes(request.UsageOptions!.Minutes));
                    await UsageObservationStream.RunAsync(pipe, request, lifetime.Token); return;
                }
                if (request.Type == "read-tray")
                {
                    lifetime.CancelAfter(request.DeadlineUtc!.Value - DateTimeOffset.UtcNow);
                    var native = await Task.Run(() => WindowsTrayActions.ReadCatalog(lifetime.Token), lifetime.Token);
                    var entries = new List<TrayCatalogEntry>(); var skipped = 0;
                    foreach (var entry in native.Entries)
                    {
                        try { ExecutablePaths.ValidatePath(entry.ExecutablePath); }
                        catch (ArgumentException) { skipped++; continue; }
                        entries.Add(new(entry.ExecutablePath, entry.DisplayName, entry.IconCount));
                    }
                    var notice = native.Notice;
                    if (skipped > 0) notice = (notice is null ? "" : notice + " ") + $"{skipped} 个系统或受保护程序不支持此动作。";
                    var catalog = new TrayCatalog([.. entries], DateTimeOffset.UtcNow, notice);
                    TrayCatalogValidation.Validate(catalog);
                    await ProviderProtocol.WriteAsync(pipe, new(1, "tray", request.RequestId, Tray: catalog), lifetime.Token); return;
                }
                if (request.Type == "lease-setting")
                {
                    var provider = registry.GetValueOrDefault(request.Invocation!.CapabilityId) as ISessionRestorableCapabilityProvider
                        ?? throw new ArgumentException("未注册此可恢复设置租约。");
                    lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
                    await SessionRestorableLeaseStream.RunAsync(pipe, request, provider, lifetime.Token);
                    return;
                }
                if (request.Type == "lease")
                {
                    var provider = registry.GetValueOrDefault(request.Invocation!.CapabilityId) as ISessionDesiredCapabilityProvider
                        ?? throw new ArgumentException("未注册此条件租约能力。");
                    lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
                    await SessionDesiredLeaseStream.RunAsync(pipe, request, provider, lifetime.Token);
                    return;
                }
                if (request.Type == "invoke")
                {
                    lifetime.CancelAfter(request.DeadlineUtc!.Value - DateTimeOffset.UtcNow);
                    if (request.Invocation!.CapabilityId == BrightnessWheelTool.Command)
                    {
                        var operation = (wheel ?? throw new IOException("没有滚轮监听服务。")).InvokeAsync(request.Invocation, lifetime.Token);
                        var closed = ProviderProtocol.ReadAsync(pipe, lifetime.Token);
                        if (await Task.WhenAny(operation, closed) != operation) lifetime.Cancel();
                        var receipt = await operation;
                        await ProviderProtocol.WriteAsync(pipe, new(1, "result", request.RequestId, CommandId: request.CommandId,
                            Status: receipt.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, Reason: receipt.Reason, WheelSteps: receipt.ConsumedSteps), lifetime.Token);
                        lifetime.Cancel(); try { await closed; } catch (Exception error) when (error is IOException or OperationCanceledException) { }
                        return;
                    }
                    var provider = registry.GetValueOrDefault(request.Invocation!.CapabilityId) ?? throw new ArgumentException("未注册此能力。");
                    UsageObservationStream.MarkAction(request.Invocation);
                    var invocation = provider.InvokeAsync(request.Invocation, lifetime.Token);
                    var disconnected = ProviderProtocol.ReadAsync(pipe, lifetime.Token);
                    if (await Task.WhenAny(invocation, disconnected) != invocation)
                    {
                        lifetime.Cancel();
                        try { await invocation; } catch (OperationCanceledException) { }
                        try { await disconnected; } catch (Exception error) when (error is IOException or OperationCanceledException) { }
                        return;
                    }
                    var result = await invocation;
                    await ProviderProtocol.WriteAsync(pipe, new(1, "result", request.RequestId, CommandId: request.CommandId,
                        Status: result.Status, Reason: result.Reason), lifetime.Token);
                    lifetime.Cancel();
                    try { await disconnected; } catch (Exception error) when (error is IOException or OperationCanceledException) { }
                }
                else
                {
                    if (request.RequiredCapabilities!.Any(id => !registry.ContainsKey(id))) throw new ArgumentException("会话程序缺少任务需要的能力。");
                    var groups = request.Resources!.GroupBy(r => registry.GetValueOrDefault(r.CapabilityId)
                        ?? throw new ArgumentException("未注册此检测能力。")).ToArray();
                    lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
                    // Host-only detections still need the session actions to be ready.
                    if (groups.Length == 0)
                    {
                        using var write = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); write.CancelAfter(TimeSpan.FromSeconds(5));
                        await ProviderProtocol.WriteAsync(pipe, new(1, "snapshot", request.RequestId, Samples: []), write.Token);
                        try { await ProviderProtocol.ReadAsync(pipe, lifetime.Token); } // hold readiness until the peer disconnects
                        catch (Exception error) when (error is EndOfStreamException or OperationCanceledException) { }
                        return;
                    }
                    var queue = Channel.CreateBounded<(ISessionCapabilityProvider Provider, ProviderUpdate Update)>(256);
                    // Overflow aborts the subscription; host treats disconnect as Unknown and reacquires a baseline.
                    var watchers = groups.Select(g => g.Key.ObserveAsync(g.ToArray(), update =>
                    { if (!queue.Writer.TryWrite((g.Key, update))) lifetime.Cancel(); }, lifetime.Token)).ToArray();
                    var pending = groups.Select(g => g.Key).ToHashSet(); var initial = new List<ProviderSample>();
                    var watcherEnded = Task.WhenAny(watchers);
                    var buffered = new List<ProviderUpdate>();
                    var latest = new Dictionary<ProviderResource, ProviderSample>();
                    var disconnected = ProviderProtocol.ReadAsync(pipe, lifetime.Token);
                    long sequence = 0;
                    try
                    {
                        while (!lifetime.IsCancellationRequested)
                        {
                            var next = queue.Reader.ReadAsync(lifetime.Token).AsTask();
                            if (await Task.WhenAny(next, disconnected, watcherEnded) != next) break;
                            var item = await next;
                            if (pending.Count > 0)
                            {
                                if (!pending.Contains(item.Provider)) { buffered.Add(item.Update); continue; }
                                if (!item.Update.Snapshot || !pending.Remove(item.Provider)) throw new InvalidDataException("Provider 缺少初始快照。");
                                initial.AddRange(item.Update.Samples);
                                if (pending.Count > 0) continue;
                                await SendAsync(new(1, "snapshot", request.RequestId, Sequence: sequence++, Samples: [.. initial]));
                                foreach (var sample in initial) latest[sample.Resource] = sample;
                                foreach (var update in buffered) await ForwardAsync(update);
                                buffered.Clear();
                            }
                            else await ForwardAsync(item.Update);
                        }
                    }
                    finally
                    {
                        lifetime.Cancel();
                        try { await Task.WhenAll(watchers); } catch (OperationCanceledException) { }
                        try { await disconnected; } catch (Exception e) when (e is IOException or OperationCanceledException) { }
                    }
                    async Task SendAsync(ProviderMessage message)
                    {
                        using var write = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); write.CancelAfter(TimeSpan.FromSeconds(5));
                        await ProviderProtocol.WriteAsync(pipe, message, write.Token);
                    }
                    async Task ForwardAsync(ProviderUpdate update)
                    {
                        foreach (var sample in update.Samples) latest[sample.Resource] = sample;
                        if (update.Snapshot) await SendAsync(new(1, "snapshot", request.RequestId, Sequence: sequence++, Samples: [.. latest.Values]));
                        else foreach (var sample in update.Samples) await SendAsync(new(1, "update", request.RequestId, Sequence: sequence++, Samples: [sample]));
                    }
                }
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                Console.Error.WriteLine("Capability Provider: " + error.Message);
                using var write = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await ProviderProtocol.WriteAsync(pipe, new(1, "result", request?.RequestId ?? "rejected", CommandId: request?.CommandId,
                    Status: OutcomeStatus.Failed, Reason: error.Message), write.Token); }
                catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException) { }
            }
        }
    }
}

internal sealed class ProgramCapabilityProvider : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => ["process.running", "program.start", "program.close"];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Any(r => r.CapabilityId != "process.running" || !ValidName(r.ResourceId))) throw new ArgumentException("程序检测参数无效。");
        var gate = new object(); var initializing = true; var unknown = false;
        var instances = resources.ToDictionary(r => r.ResourceId, _ => new HashSet<(int, DateTimeOffset)>(), StringComparer.OrdinalIgnoreCase);
        ProviderSample Sample(ProviderResource r) => new(r, unknown ? StateQuality.Unknown : StateQuality.Known, "axiom.bool", 1,
            Boolean: unknown ? null : instances[r.ResourceId].Count > 0);
        await using var observer = new WindowsGameProcessObserver(resources.Select(r => r.ResourceId), change =>
        {
            lock (gate)
            {
                if (change.Instance is not { } instance)
                {
                    unknown = true;
                    if (!initializing) publish(new(true, resources.Select(Sample).ToArray()));
                    return;
                }
                var name = Path.GetFileName(instance.ExecutablePath);
                if (!instances.TryGetValue(name, out var active)) return;
                if (change.Running) active.Add((instance.ProcessId, instance.StartedAtUtc)); else active.Remove((instance.ProcessId, instance.StartedAtUtc));
                if (!initializing) publish(new(false, [Sample(resources.Single(r => string.Equals(r.ResourceId, name, StringComparison.OrdinalIgnoreCase)))]));
            }
        });
        observer.Start();
        lock (gate) { publish(new(true, resources.Select(Sample).ToArray())); initializing = false; }
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }
    private static bool ValidName(string name) => name.Length is >= 5 and <= 132 && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0;
    public async Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation config, CancellationToken token)
    {
        var paths = ProgramCapabilities.ValidateTargets(config); token.ThrowIfCancellationRequested();
        var result = config.CapabilityId == "program.start"
            ? WindowsProgramControl.Start(paths.Single(), ProgramCapabilities.Value(config, "arguments"), ProgramCapabilities.Value(config, "workingDirectory"),
                ProgramCapabilities.Value(config, "window"), ProgramCapabilities.Value(config, "ifRunning") == "skip")
            : await WindowsProgramBatch.ExecuteAsync(paths, (path, cancellation) => WindowsProgramControl.CloseAsync(path, ProgramCapabilities.Value(config, "mode") == "force",
                ProgramCapabilities.Value(config, "instances") == "latest", TimeSpan.FromSeconds((double)ProgramCapabilities.Number(config, "timeoutSeconds")), cancellation), token);
        return (result.Success ? OutcomeStatus.Succeeded : OutcomeStatus.Failed, result.Reason);
    }
}
