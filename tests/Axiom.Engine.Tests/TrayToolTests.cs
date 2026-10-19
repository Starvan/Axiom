using System.Collections.Immutable;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.ServiceHost;

namespace Axiom.Engine.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class TrayToolTests
{
    private static IAutomationPlugin[] Plugins => [new AxiomStartupPlugin(), new TrayPlugin(), new TrayToolPlugin()];
    private static TrayToolSettings Settings(long version = 0, bool enabled = true, string mode = TrayTool.Startup, string path = @"C:\Apps\TraySample.exe", int waitSeconds = 30) => new(version, enabled, mode, [path], waitSeconds);
    private static string NewDirectory() { var directory = Path.Combine(Path.GetTempPath(), "axiom-tray-tool-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); return directory; }
    private static async Task Until(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(10, limit.Token);
    }

    [Fact]
    public async Task RequestsAreStrictAndObservationCannotCarryAnExecutionIdentity()
    {
        var now = DateTimeOffset.UtcNow;
        var save = new AutomationMessage(1, "save-tray-tool", "tool", now.AddSeconds(10), TraySettings: Settings());
        AutomationProtocol.ValidateRequest(save, now);
        AutomationProtocol.ValidateRequest(save with { Type = "read-tray-tool", TraySettings = null }, now);
        AutomationProtocol.ValidateRequest(save with { Type = "run-tray-tool", TraySettings = null, ExpectedVersion = 1 }, now);
        foreach (var invalid in new[] { save with { ExpectedVersion = 1 }, save with { Group = new(Guid.NewGuid().ToString("D"), 0, "group", []) },
            save with { Type = "read", TraySettings = Settings() }, save with { Type = "run-tray-tool", TraySettings = null },
            save with { TrayTool = new(Settings(), "forged result") }, save with { TraySettings = Settings(mode: "poll") },
            save with { TraySettings = Settings(path: "sample.exe") }, save with { TraySettings = Settings() with { Executables = [] } },
            save with { TraySettings = Settings(waitSeconds: 0) }, save with { TraySettings = Settings(waitSeconds: 121) } })
            Assert.ThrowsAny<Exception>(() => AutomationProtocol.ValidateRequest(invalid, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(save, now, true));
        var invoke = new TaskInvocation(TrayCapabilities.Hide, 1, ImmutableDictionary<string, string>.Empty.Add("executable", @"C:\Apps\TraySample.exe").Add("waitSeconds", "1").Add("delaySeconds", "0"));
        var observe = new ProviderMessage(1, "observe-tray", "monitor", now.AddSeconds(10), Invocation: invoke);
        ProviderProtocol.Validate(observe, now);
        Assert.Throws<InvalidDataException>(() => ProviderProtocol.Validate(observe with { CommandId = Guid.NewGuid() }, now));
        Assert.Throws<InvalidDataException>(() => ProviderProtocol.Validate(observe with { Resources = [] }, now));
        Assert.Throws<ArgumentException>(() => ProviderProtocol.Validate(observe with { Invocation = invoke with { Parameters = invoke.Parameters.SetItem("executable", "sample.exe") } }, now));
        var longInvoke = observe with { Type = "invoke", CommandId = Guid.NewGuid(), DeadlineUtc = now.AddSeconds(130),
            Invocation = invoke with { Parameters = invoke.Parameters.SetItem("waitSeconds", "120") } };
        ProviderProtocol.Validate(longInvoke, now);
        Assert.Throws<InvalidDataException>(() => ProviderProtocol.Validate(longInvoke with { DeadlineUtc = now.AddSeconds(136) }, now));
        Assert.Throws<InvalidDataException>(() => ProviderProtocol.Validate(longInvoke with { Invocation = invoke with { CapabilityId = "program.close" } }, now));
        Assert.Throws<InvalidDataException>(() => ProviderProtocol.Validate(observe with { DeadlineUtc = now.AddSeconds(130) }, now));
        using var wire = new MemoryStream(); await AutomationProtocol.WriteAsync(wire, save, default); wire.Position = 0;
        var settings = (await AutomationProtocol.ReadAsync(wire, default)).TraySettings!;
        Assert.Equal(TrayTool.Startup, settings.Mode); Assert.Equal(30, settings.WaitSeconds);
        Assert.Equal(30, System.Text.Json.JsonSerializer.Deserialize<TrayToolSettings>("""{"Version":0,"Enabled":false,"Mode":"startup","Executables":[]}""")!.WaitSeconds);
    }

    [Fact(Timeout = 15000)]
    public async Task DisabledSettingsPersistWithVersionAndInternalTaskCannotOverwriteUserRules()
    {
        var directory = NewDirectory(); var path = Path.Combine(directory, "tasks.db");
        try
        {
            using var activity = new AutomationFixture();
            using (var store = new SqliteAutomationTaskStore(path))
            await using (var runtime = new PluginTaskRuntime(Plugins, store, "test-" + Guid.NewGuid().ToString("N"), activity.Engine, () => 1))
            {
                await runtime.InitializeAsync(); Assert.Empty(runtime.ReadTrayTool().Settings.Executables);
                var saved = await runtime.SaveTrayToolAsync(Settings(enabled: false, mode: TrayTool.Continuous, waitSeconds: 120), default);
                Assert.False(saved.Settings.Enabled); Assert.Equal(1, saved.Settings.Version);
                Assert.Empty((await runtime.ReadAsync(default)).Tasks);
                Assert.DoesNotContain((await runtime.ReadAsync(default)).Capabilities, c => c.Id == TrayTool.Trigger);
                var internalTask = Assert.Single(await store.ListAsync());
                await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SaveAsync(internalTask, default));
                await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.DeleteAsync(internalTask.Id, internalTask.Version, default));
                await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SaveTrayToolAsync(Settings(), default));
                await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RunTrayToolAsync(1, default));
                Assert.Equal(1, Assert.Single(await store.ListAsync()).Version);
                var ordinary = internalTask with { Id = Guid.NewGuid().ToString("D"), Version = 0, Title = "user startup",
                    Detection = new(AxiomStartupPlugin.CapabilityId, 1, ImmutableDictionary<string, string>.Empty.Add("frequency", "every")) };
                await runtime.SaveAsync(ordinary, default); Assert.Equal(ordinary.Id, Assert.Single((await runtime.ReadAsync(default)).Tasks).Id);
                await runtime.EnsureSafeToStopAsync(default);
            }
            using (var store = new SqliteAutomationTaskStore(path))
            await using (var runtime = new PluginTaskRuntime(Plugins, store, "test-" + Guid.NewGuid().ToString("N"), activity.Engine, () => 1))
            {
                await runtime.InitializeAsync(); var loaded = runtime.ReadTrayTool().Settings;
                Assert.Equal(1, loaded.Version); Assert.False(loaded.Enabled); Assert.Equal(TrayTool.Continuous, loaded.Mode); Assert.Equal(120, loaded.WaitSeconds);
                Assert.Equal([@"C:\Apps\TraySample.exe"], loaded.Executables); await runtime.EnsureSafeToStopAsync(default);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact(Timeout = 20000)]
    public async Task EnableSaveAndExplicitRetryUseEngineReceiptsAndStartupRunsAgainWithoutUi()
    {
        var directory = NewDirectory(); var path = Path.Combine(directory, "tasks.db");
        try
        {
            using var activity = new AutomationFixture();
            foreach (var restart in new[] { false, true })
            {
                var sid = "test-" + Guid.NewGuid().ToString("N"); await using var provider = new Provider(sid);
                using var store = new SqliteAutomationTaskStore(path); await using var runtime = new PluginTaskRuntime(Plugins, store, sid, activity.Engine, () => 1);
                await runtime.InitializeAsync();
                if (!restart) await runtime.SaveTrayToolAsync(Settings(), default);
                var first = await provider.NextCall(); Assert.Equal(TrayCapabilities.Hide, first.Request.Invocation!.CapabilityId);
                Assert.Contains("TraySample.exe", first.Request.Invocation.Parameters["executable"]);
                Assert.Equal(restart ? "60" : "30", first.Request.Invocation.Parameters["waitSeconds"]);
                Assert.True(first.Request.DeadlineUtc > DateTimeOffset.UtcNow.AddSeconds(120), "Host must not cut off the longer native wait at the old 40-second deadline.");
                first.Complete(OutcomeStatus.Succeeded, "fixture hidden");
                await Until(() => runtime.ReadTrayTool().LastStatus == OutcomeStatus.Succeeded);
                if (!restart)
                {
                    var saved = await runtime.SaveTrayToolAsync(Settings(1, path: @"C:\Apps\Changed.exe", waitSeconds: 7), default);
                    var changed = await provider.NextCall(); Assert.Equal(@"C:\Apps\Changed.exe", changed.Request.Invocation!.Parameters["executable"]);
                    Assert.Equal("7", changed.Request.Invocation.Parameters["waitSeconds"]);
                    changed.Complete(OutcomeStatus.Failed, "fixture unchanged failure"); await Until(() => runtime.ReadTrayTool().LastStatus == OutcomeStatus.Failed);
                    await runtime.RunTrayToolAsync(saved.Settings.Version, default);
                    var retry = await provider.NextCall(); Assert.Equal("7", retry.Request.Invocation!.Parameters["waitSeconds"]);
                    retry.Complete(OutcomeStatus.Succeeded, "fixture retry hidden");
                    await Until(() => runtime.ReadTrayTool().LastResult == "fixture retry hidden");
                    await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RunTrayToolAsync(1, default));
                    Assert.Equal(3, provider.Calls); Assert.Equal(0, provider.Monitors);
                    // Preserve the same target for the restart assertion.
                    await runtime.SaveTrayToolAsync(Settings(2, waitSeconds: 60), default);
                    var final = await provider.NextCall(); final.Complete(OutcomeStatus.Succeeded, "fixture reset");
                    await Until(() => runtime.ReadTrayTool().LastResult == "fixture reset");
                }
                else { Assert.Equal(1, provider.Calls); Assert.Equal(0, provider.Monitors); }
                await runtime.EnsureSafeToStopAsync(default);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact(Timeout = 15000)]
    public async Task LegacyOneSecondTaskRemainsInternalAndCanSaveLongerWait()
    {
        var directory = NewDirectory();
        try
        {
            using var activity = new AutomationFixture(); using var store = new SqliteAutomationTaskStore(Path.Combine(directory, "tasks.db"));
            await store.SaveAsync(new(TrayTool.TaskId, 0, TrayTool.Name, true,
                new(TrayTool.Trigger, 1, ImmutableDictionary<string, string>.Empty.Add("frequency", "every").Add("mode", TrayTool.Startup)),
                new(TrayCapabilities.Hide, 1, ImmutableDictionary<string, string>.Empty.Add("executable", @"C:\Apps\TraySample.exe").Add("waitSeconds", "1").Add("delaySeconds", "0"))), default);
            await using var runtime = new PluginTaskRuntime(Plugins, store, "test-" + Guid.NewGuid().ToString("N"), activity.Engine, () => 1);
            await runtime.InitializeAsync();
            Assert.Equal(1, runtime.ReadTrayTool().Settings.WaitSeconds); Assert.Empty((await runtime.ReadAsync(default)).Tasks);
            var saved = await runtime.SaveTrayToolAsync(runtime.ReadTrayTool().Settings with { WaitSeconds = 30 }, default);
            Assert.Equal(30, saved.Settings.WaitSeconds); Assert.False(saved.Settings.Enabled);
            Assert.Equal("30", Assert.Single(await store.ListAsync()).Action.Parameters["waitSeconds"]);
            Assert.Empty((await runtime.ReadAsync(default)).Tasks); await runtime.EnsureSafeToStopAsync(default);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact(Timeout = 20000)]
    public async Task ContinuousEventsMergeWhileBusyRemovalDoesNotLoopAndPauseCancelsSubscriptionAndCommand()
    {
        var directory = NewDirectory(); var sid = "test-" + Guid.NewGuid().ToString("N");
        try
        {
            await using var provider = new Provider(sid); using var activity = new AutomationFixture();
            using var store = new SqliteAutomationTaskStore(Path.Combine(directory, "tasks.db"));
            await using var runtime = new PluginTaskRuntime(Plugins, store, sid, activity.Engine, () => 1);
            await runtime.InitializeAsync(); var settings = (await runtime.SaveTrayToolAsync(Settings(mode: TrayTool.Continuous), default)).Settings;
            var monitor = await provider.NextMonitor(); var initial = await provider.NextCall();
            await monitor.Update(1, true); await monitor.Update(2, true); await monitor.Update(3, true);
            initial.Complete(OutcomeStatus.Succeeded, "initial hidden");
            var merged = await provider.NextCall(); Assert.Equal(2, provider.Calls);
            await monitor.Update(3, false); merged.Complete(OutcomeStatus.Succeeded, "merged hidden");
            await Until(() => runtime.ReadTrayTool().LastResult == "merged hidden");
            // No new identity / removal-only notification can produce another command.
            await monitor.Update(3, false); await monitor.Update(3, false);
            await monitor.Update(4, true); var fresh = await provider.NextCall(); Assert.Equal(3, provider.Calls);
            var stopped = await runtime.SaveTrayToolAsync(settings with { Enabled = false }, default);
            await monitor.Disconnected.WaitAsync(TimeSpan.FromSeconds(2)); await fresh.Disconnected.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(stopped.Settings.Enabled); Assert.False(stopped.Running);
            Assert.Equal("merged hidden", runtime.ReadTrayTool().LastResult);
            await runtime.EnsureSafeToStopAsync(default);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact(Timeout = 15000)]
    public async Task EditsBeforeSessionReadyOnlyRunLatestVersionAndCollisionIsPreserved()
    {
        var directory = NewDirectory(); var sid = "test-" + Guid.NewGuid().ToString("N");
        try
        {
            await using var provider = new Provider(sid, ready: false); using var activity = new AutomationFixture();
            using var store = new SqliteAutomationTaskStore(Path.Combine(directory, "tasks.db"));
            await using var runtime = new PluginTaskRuntime(Plugins, store, sid, activity.Engine, () => 1);
            await runtime.InitializeAsync(); await runtime.SaveTrayToolAsync(Settings(), default);
            await runtime.SaveTrayToolAsync(Settings(1, path: @"C:\Apps\Latest.exe"), default);
            provider.Ready.TrySetResult(); var call = await provider.NextCall();
            Assert.Equal(@"C:\Apps\Latest.exe", call.Request.Invocation!.Parameters["executable"]);
            call.Complete(OutcomeStatus.Succeeded, "latest only"); await Until(() => runtime.ReadTrayTool().LastResult == "latest only");
            Assert.Equal(1, provider.Calls); await runtime.EnsureSafeToStopAsync(default);
        }
        finally { Directory.Delete(directory, true); }
        directory = NewDirectory();
        try
        {
            using var activity = new AutomationFixture(); using var store = new SqliteAutomationTaskStore(Path.Combine(directory, "tasks.db"));
            var action = new TaskInvocation(TrayCapabilities.Hide, 1, ImmutableDictionary<string, string>.Empty.Add("executable", @"C:\Apps\Original.exe").Add("waitSeconds", "1").Add("delaySeconds", "0"));
            await store.SaveAsync(new(TrayTool.TaskId, 0, "original user rule", true, new(AxiomStartupPlugin.CapabilityId, 1, ImmutableDictionary<string, string>.Empty.Add("frequency", "every")), action), default);
            await using var runtime = new PluginTaskRuntime(Plugins, store, "test-" + Guid.NewGuid().ToString("N"), activity.Engine, () => 1);
            await runtime.InitializeAsync(); await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SaveTrayToolAsync(Settings(1), default));
            Assert.Equal("original user rule", Assert.Single(await store.ListAsync()).Title);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact(Timeout = 15000)]
    public async Task ConflictingUserCommandIsExplainedAndNeverOverriddenByTool()
    {
        var directory = NewDirectory(); var sid = "test-" + Guid.NewGuid().ToString("N");
        try
        {
            await using var provider = new Provider(sid); using var activity = new AutomationFixture();
            using var store = new SqliteAutomationTaskStore(Path.Combine(directory, "tasks.db"));
            var user = await store.SaveAsync(new(Guid.NewGuid().ToString("D"), 0, "user tray command", false,
                new(AxiomStartupPlugin.CapabilityId, 1, ImmutableDictionary<string, string>.Empty.Add("frequency", "every")),
                new(TrayCapabilities.Hide, 1, ImmutableDictionary<string, string>.Empty.Add("executable", @"C:\Apps\TraySample.exe").Add("waitSeconds", "2").Add("delaySeconds", "0"))), default);
            await using var runtime = new PluginTaskRuntime(Plugins, store, sid, activity.Engine, () => 1);
            await runtime.InitializeAsync(); var original = await provider.NextCall();
            await runtime.SaveTrayToolAsync(Settings(), default);
            await Until(() => runtime.ReadTrayTool().State.Contains("冲突"));
            await original.Disconnected.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, provider.Calls); Assert.Null(runtime.ReadTrayTool().LastStatus);
            Assert.Equal(user.Id, Assert.Single((await runtime.ReadAsync(default)).Tasks).Id);
            Assert.Equal(user.Version, (await store.ListAsync()).Single(t => t.Id == user.Id).Version);
            await runtime.EnsureSafeToStopAsync(default);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class Call(ProviderMessage request)
    {
        internal ProviderMessage Request = request;
        internal TaskCompletionSource<(OutcomeStatus Status, string Reason)> Receipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Disconnected => Closed.Task;
        internal void Complete(OutcomeStatus status, string reason) => Receipt.TrySetResult((status, reason));
    }
    private sealed class Monitor(NamedPipeServerStream pipe, ProviderMessage request, CancellationToken token)
    {
        private long sequence = 1;
        internal TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Disconnected => Closed.Task;
        internal Task Update(long counter, bool active) => ProviderProtocol.WriteAsync(pipe, Message("update", sequence++, counter, active), token);
        internal ProviderMessage Message(string type, long sequence, long counter, bool active) => new(1, type, request.RequestId, Sequence: sequence,
            Samples: [new(new(TrayTool.Trigger, ExecutablePaths.ResourceId(TrayCapabilities.ValidateTargets(request.Invocation!))), StateQuality.Known, "axiom.event-count", 1, Boolean: active, Number: counter)]);
    }
    private sealed class Provider : IAsyncDisposable
    {
        private readonly string sid, owner = WindowsIdentity.GetCurrent().User!.Value;
        private readonly CancellationTokenSource cancel = new();
        private readonly Task server;
        private readonly List<Task> handlers = [];
        private readonly Channel<Call> calls = Channel.CreateUnbounded<Call>();
        private readonly Channel<Monitor> monitors = Channel.CreateUnbounded<Monitor>();
        private int callCount, monitorCount;
        internal int Calls => Volatile.Read(ref callCount);
        internal int Monitors => Volatile.Read(ref monitorCount);
        internal TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Provider(string sid, bool ready = true) { this.sid = sid; if (ready) Ready.SetResult(); server = Serve(); }
        internal Task<Call> NextCall() => calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
        internal Task<Monitor> NextMonitor() => monitors.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
        private async Task Serve()
        {
            while (!cancel.IsCancellationRequested)
            {
                var pipe = LocalPipeFactory.Create(ProviderProtocol.PipeName(sid), owner);
                try { await pipe.WaitForConnectionAsync(cancel.Token); } catch (OperationCanceledException) { pipe.Dispose(); break; }
                handlers.Add(Handle(pipe));
            }
            await Task.WhenAll(handlers);
        }
        private async Task Handle(NamedPipeServerStream pipe)
        {
            using (pipe)
            try
            {
                var request = await ProviderProtocol.ReadAsync(pipe, cancel.Token); ProviderProtocol.Validate(request, DateTimeOffset.UtcNow);
                if (request.Type == "observe")
                {
                    await Ready.Task.WaitAsync(cancel.Token);
                    await ProviderProtocol.WriteAsync(pipe, new(1, "snapshot", request.RequestId, Samples: []), cancel.Token);
                    await ProviderProtocol.ReadAsync(pipe, cancel.Token);
                }
                else if (request.Type == "observe-tray")
                {
                    var monitor = new Monitor(pipe, request, cancel.Token); Interlocked.Increment(ref monitorCount);
                    await ProviderProtocol.WriteAsync(pipe, monitor.Message("snapshot", 0, 0, false), cancel.Token); monitors.Writer.TryWrite(monitor);
                    try { await ProviderProtocol.ReadAsync(pipe, cancel.Token); } finally { monitor.Closed.TrySetResult(); }
                }
                else
                {
                    var call = new Call(request); Interlocked.Increment(ref callCount); calls.Writer.TryWrite(call);
                    var disconnected = ProviderProtocol.ReadAsync(pipe, cancel.Token);
                    try
                    {
                        if (await Task.WhenAny(call.Receipt.Task, disconnected) == call.Receipt.Task)
                        {
                            var result = await call.Receipt.Task;
                            await ProviderProtocol.WriteAsync(pipe, new(1, "result", request.RequestId, CommandId: request.CommandId, Status: result.Status, Reason: result.Reason), cancel.Token);
                        }
                        await disconnected;
                    }
                    finally { call.Closed.TrySetResult(); }
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync() { cancel.Cancel(); await server; cancel.Dispose(); }
    }
}
