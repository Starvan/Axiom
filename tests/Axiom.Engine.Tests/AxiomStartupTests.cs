using System.Collections.Immutable;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.ServiceHost;
using Axiom.WinUI;
using Microsoft.Data.Sqlite;

namespace Axiom.Engine.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class AxiomStartupTests
{
    [Fact]
    public void ReleasedSourceDiscardsOwnershipAndRejectsOldEpochAfterResourcesChange()
    {
        using var fixture = new AutomationFixture(); var scope = ResourceScope.Session("owner", 1);
        var firstKey = new ResourceKey(AxiomStartupPlugin.CapabilityId, "first", scope);
        var secondKey = new ResourceKey(AxiomStartupPlugin.CapabilityId, "second", scope);
        var source = fixture.Engine.RegisterSource("lifecycle", [firstKey]);
        fixture.Engine.ApplySnapshot(source, 0, [new(firstKey, StateQuality.Known, StateValue.From(new("axiom.bool", 1), false), DateTimeOffset.UtcNow)]);
        fixture.Engine.ReleaseSource(source);
        var replacement = fixture.Engine.RegisterSource("lifecycle", [secondKey]);
        Assert.Equal(UpdateStatus.StaleEpoch, fixture.Engine.Update(source, 1, new(firstKey, StateQuality.Known, StateValue.From(new("axiom.bool", 1), true), DateTimeOffset.UtcNow)).Status);
        Assert.Equal(UpdateStatus.SnapshotRequired, fixture.Engine.Update(replacement, 1, new(secondKey, StateQuality.Known, StateValue.From(new("axiom.bool", 1), true), DateTimeOffset.UtcNow)).Status);
        Assert.DoesNotContain(firstKey, fixture.Engine.ReadSnapshot().States.Keys);
        for (var i = 0; i < 150; i++) { fixture.Engine.ReleaseSource(replacement); replacement = fixture.Engine.RegisterSource("lifecycle", [secondKey]); }
        Assert.Equal(UpdateStatus.StaleEpoch, fixture.Engine.ReleaseSource(source).Status);
        Assert.Contains(secondKey, fixture.Engine.ReadSnapshot().States.Keys);
    }
    private static AutomationTask TaskRule(string frequency, string executable, bool paused = false, string delay = "0") =>
        new(Guid.NewGuid().ToString("D"), 0, "启动 " + frequency, paused,
            new(AxiomStartupPlugin.CapabilityId, 1, ImmutableDictionary<string, string>.Empty.Add("frequency", frequency)),
            new("program.start", 1, ProgramCapabilities.Describe(true).Fields.Where(f => f.DefaultValue is not null)
                .ToImmutableDictionary(f => f.Id, f => f.DefaultValue!).SetItem("executable", executable).SetItem("delaySeconds", delay)));

    [Fact]
    public void GenericEditorCompilesBothChoicesWithTaskIdentityAndProgramActions()
    {
        var modules = new IAutomationPlugin[] { new ProgramPlugin(), new AxiomStartupPlugin() };
        var compiler = new AutomationTaskCompiler();
        foreach (var module in modules) { foreach (var d in module.Detections) compiler.Register(d); foreach (var a in module.Actions) compiler.Register(a); }
        var editor = new TaskEditorState(); editor.Load(null); editor.Title = "启动规则";
        editor.Select(new AxiomStartupTaskDetection().Descriptor, [.. modules.SelectMany(m => m.Combinations)]);
        editor.Select(ProgramCapabilities.Describe(true), [.. modules.SelectMany(m => m.Combinations)]);
        editor.SetParameter(TaskCapabilityKind.Action, "executable", @"C:\Apps\one.exe");
        foreach (var choice in new[] { "every", "firstBoot" })
        {
            editor.SetParameter(TaskCapabilityKind.Detection, "frequency", choice);
            var task = editor.Build(); var rule = compiler.Compile(task, "owner", ResourceScope.Session("owner", 1));
            Assert.Equal(task.Id, Assert.Single(rule.Condition.Dependencies).ResourceId);
            Assert.Equal(RuleActionKind.Command, rule.Action.Kind);
        }
        editor.SetParameter(TaskCapabilityKind.Detection, "frequency", "invalid");
        Assert.Throws<ArgumentException>(() => compiler.Compile(editor.Build(), "owner", ResourceScope.Session("owner", 1)));
        Assert.Contains(new(AxiomStartupPlugin.CapabilityId, "program.close"), modules[1].Combinations);
    }

    [Fact]
    public async Task BootClaimIsAtomicSurvivesReopenAndMigratesExistingTasksWithoutReset()
    {
        var directory = NewDirectory(); var path = Path.Combine(directory, "tasks.db");
        try
        {
            AutomationTask saved;
            using (var store = new SqliteAutomationTaskStore(path)) saved = await store.SaveAsync(TaskRule("every", @"C:\Apps\one.exe"), default);
            // Model the previous schema with an existing task; migration must preserve it.
            using (var legacy = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            { legacy.Open(); using var command = legacy.CreateCommand(); command.CommandText = "DROP TABLE host_startup; PRAGMA user_version=1;"; command.ExecuteNonQuery(); }
            using (var first = new SqliteAutomationTaskStore(path))
            using (var second = new SqliteAutomationTaskStore(path))
            {
                var claimed = await Task.WhenAll(System.Threading.Tasks.Task.Run(() => first.ClaimBootStartAsync(uint.MaxValue)), System.Threading.Tasks.Task.Run(() => second.ClaimBootStartAsync(uint.MaxValue)));
                Assert.Single(claimed, value => value);
                var loaded = Assert.Single(await first.ListAsync()); Assert.Equal(saved.Id, loaded.Id); Assert.Equal(saved.Version, loaded.Version);
            }
            using (var store = new SqliteAutomationTaskStore(path))
            { Assert.False(await store.ClaimBootStartAsync(uint.MaxValue)); Assert.True(await store.ClaimBootStartAsync(0)); Assert.False(await store.ClaimBootStartAsync(0)); }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact(Timeout = 20000)]
    public async Task StartupExecutesEveryTimeFirstOnlyOnceAcrossRuntimeRestartAndPausedNever()
    {
        var directory = NewDirectory(); var path = Path.Combine(directory, "tasks.db");
        try
        {
            using (var store = new SqliteAutomationTaskStore(path))
            {
                await store.SaveAsync(TaskRule("every", @"C:\Apps\every.exe"), default);
                await store.SaveAsync(TaskRule("firstBoot", @"C:\Apps\once.exe"), default);
                await store.SaveAsync(TaskRule("every", @"C:\Apps\paused.exe", true), default);
            }
            foreach (var (boot, expected) in new[] { (42u, 2), (42u, 1), (43u, 2) })
            {
                var sid = "test-" + Guid.NewGuid().ToString("N"); await using var provider = new ProviderFixture(sid);
                using var activity = new AutomationFixture(); using var store = new SqliteAutomationTaskStore(path);
                await using var runtime = new PluginTaskRuntime([new ProgramPlugin(), new AxiomStartupPlugin()], store, sid, activity.Engine, () => boot);
                await runtime.InitializeAsync();
                var request = await provider.Observations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Empty(request.Resources!); Assert.Equal(["program.start"], request.RequiredCapabilities!);
                var calls = new List<string>(); for (var i = 0; i < expected; i++) calls.Add((await provider.NextCommand()).Invocation!.Parameters["executable"]);
                Assert.Contains(@"C:\Apps\every.exe", calls); Assert.DoesNotContain(@"C:\Apps\paused.exe", calls);
                if (expected == 2) Assert.Contains(@"C:\Apps\once.exe", calls);
                await runtime.EnsureSafeToStopAsync(default); Assert.Equal(expected, provider.Calls);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact(Timeout = 15000)]
    public async Task RuntimeEditsAndLateRulesDoNotExecuteOnInitialConnectionAndDelaysCancelOnPause()
    {
        var directory = NewDirectory();
        try
        {
            var sid = "test-" + Guid.NewGuid().ToString("N"); await using var provider = new ProviderFixture(sid, waitForReady: true);
            using var activity = new AutomationFixture(); using var store = new SqliteAutomationTaskStore(Path.Combine(directory, "tasks.db"));
            var edited = await store.SaveAsync(TaskRule("every", @"C:\Apps\edited.exe"), default);
            var delayed = await store.SaveAsync(TaskRule("every", @"C:\Apps\delayed.exe", delay: "1") with { Title = "delayed" }, default);
            await using var runtime = new PluginTaskRuntime([new ProgramPlugin(), new AxiomStartupPlugin()], store, sid, activity.Engine, () => 42);
            await runtime.InitializeAsync(); await provider.Observations.Reader.ReadAsync();
            await runtime.SaveAsync(edited with { Title = "edited during startup" }, default);
            var late = await runtime.SaveAsync(TaskRule("every", @"C:\Apps\late.exe") with { Title = "late" }, default);
            provider.Ready.TrySetResult();
            await WaitUntil(async () => (await runtime.ReadAsync(default)).Observations.Any(o => o.Name == delayed.Title && o.Value == "已触发启动条件"));
            await runtime.SaveAsync(delayed with { Paused = true }, default);
            await runtime.SaveAsync(late with { Paused = true }, default); await runtime.SaveAsync(late with { Version = 2, Paused = false }, default);
            await Task.Delay(1200); Assert.Equal(0, provider.Calls);
            Assert.DoesNotContain((await runtime.ReadAsync(default)).Observations, o => o.Name == late.Title && o.Value == "已触发启动条件");
            await runtime.EnsureSafeToStopAsync(default);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact(Timeout = 20000)]
    public async Task MixedProcessObservationAndReconnectDoNotReplayStartup()
    {
        var directory = NewDirectory();
        try
        {
            var sid = "test-" + Guid.NewGuid().ToString("N"); await using var provider = new ProviderFixture(sid);
            using var activity = new AutomationFixture(); using var store = new SqliteAutomationTaskStore(Path.Combine(directory, "tasks.db"));
            await store.SaveAsync(TaskRule("every", @"C:\Apps\startup.exe"), default);
            await store.SaveAsync(TaskRule("every", @"C:\Apps\process.exe") with { Detection = new("process.running", 2, ImmutableDictionary<string, string>.Empty.Add("processNames", "trigger.exe")) }, default);
            await using var runtime = new PluginTaskRuntime([new ProgramPlugin(), new AxiomStartupPlugin()], store, sid, activity.Engine, () => 42);
            await runtime.InitializeAsync();
            var request = await provider.Observations.Reader.ReadAsync(); Assert.Equal("process.running", Assert.Single(request.Resources!).CapabilityId);
            Assert.Equal(@"C:\Apps\startup.exe", (await provider.NextCommand()).Invocation!.Parameters["executable"]);
            provider.DropObservations();
            await provider.Observations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
            await WaitUntil(async () => (await runtime.ReadAsync(default)).DetectionAvailable);
            await runtime.EnsureSafeToStopAsync(default); Assert.Equal(1, provider.Calls);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact(Timeout = 10000)]
    public async Task MissingBootIdentityLeavesFirstUnknownButEveryStillRuns()
    {
        var directory = NewDirectory();
        try
        {
            var sid = "test-" + Guid.NewGuid().ToString("N"); await using var provider = new ProviderFixture(sid);
            using var activity = new AutomationFixture(); using var store = new SqliteAutomationTaskStore(Path.Combine(directory, "tasks.db"));
            await store.SaveAsync(TaskRule("firstBoot", @"C:\Apps\once.exe"), default); await store.SaveAsync(TaskRule("every", @"C:\Apps\every.exe"), default);
            await using var runtime = new PluginTaskRuntime([new ProgramPlugin(), new AxiomStartupPlugin()], store, sid, activity.Engine, () => throw new InvalidDataException("test boot identity unavailable"));
            await runtime.InitializeAsync(); Assert.Equal(@"C:\Apps\every.exe", (await provider.NextCommand()).Invocation!.Parameters["executable"]);
            Assert.False((await runtime.ReadAsync(default)).DetectionAvailable);
            await runtime.EnsureSafeToStopAsync(default); Assert.Equal(1, provider.Calls);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact(Timeout = 20000)]
    public async Task HdrGateAcquiringUnrelatedObservationDoesNotCancelStartupWindowAndTrayCommands()
    {
        var directory = NewDirectory(); var sid = "test-" + Guid.NewGuid().ToString("N");
        var hdr = Channel.CreateUnbounded<bool?>();
        var observing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Observe(Action<bool?> publish, CancellationToken token)
        { observing.TrySetResult(); await foreach (var value in hdr.Reader.ReadAllAsync(token)) publish(value); }
        try
        {
            using var activity = new AutomationFixture(); using var store = new SqliteAutomationTaskStore(Path.Combine(directory, "tasks.db"));
            await store.SaveAsync(TaskRule("firstBoot", @"C:\Apps\Startup.exe"), default);
            await store.SaveAsync(TaskRule("firstBoot", @"C:\Apps\Window.exe") with { Action = new(WindowCapabilities.Close, 1,
                ImmutableDictionary<string, string>.Empty.Add("executable", @"C:\Apps\Window.exe").Add("waitSeconds", "30").Add("delaySeconds", "0").Add("title", "")) }, default);
            await store.SaveAsync(new(TrayTool.TaskId, 0, TrayTool.Name, false,
                new(TrayTool.Trigger, 1, ImmutableDictionary<string, string>.Empty.Add("frequency", "every").Add("mode", TrayTool.Startup)),
                new(TrayCapabilities.Hide, 1, ImmutableDictionary<string, string>.Empty.Add("executable", @"C:\Apps\Tray.exe").Add("waitSeconds", "60").Add("delaySeconds", "0"))), default);
            await store.SaveAsync(TaskRule("every", @"C:\Apps\Gated.exe") with { SchemaVersion = 4, RunConditions = new("off"),
                Detection = new("process.running", 2, ImmutableDictionary<string, string>.Empty.Add("processNames", "synthetic.exe")) }, default);
            await using var provider = new ProviderFixture(sid, holdCommands: true);
            await using var runtime = new PluginTaskRuntime([new ProgramPlugin(), new AxiomStartupPlugin(), new WindowPlugin(), new TrayPlugin(), new TrayToolPlugin()],
                store, sid, activity.Engine, () => 42, observeRunHdr: Observe);
            await runtime.InitializeAsync(); await observing.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var initial = await provider.Observations.Reader.ReadAsync(); Assert.Empty(initial.Resources!);
            var calls = new List<ProviderMessage>(); for (var i = 0; i < 3; i++) calls.Add(await provider.NextCommand());
            Assert.Equal(new[] { "program.start", "tray.hide", "window.close" }, calls.Select(c => c.Invocation!.CapabilityId).Order().ToArray());
            hdr.Writer.TryWrite(false);
            var rebound = await provider.Observations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("process.running", Assert.Single(rebound.Resources!).CapabilityId);
            await WaitUntil(async () => (await runtime.ReadAsync(default)).DetectionAvailable);
            provider.ReleaseCommands.TrySetResult();
            await WaitUntil(() => Task.FromResult(runtime.ReadTrayTool().LastStatus == OutcomeStatus.Succeeded && provider.Receipts == 3));
            Assert.Equal(3, provider.Calls); Assert.Equal(3, provider.Receipts); Assert.Equal(0, provider.CancelledCommands);
            await runtime.EnsureSafeToStopAsync(default);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact(Timeout = 15000)]
    public async Task ActualProviderDisconnectStillCancelsStartupAndReconnectDoesNotReplayIt()
    {
        var directory = NewDirectory(); var sid = "test-" + Guid.NewGuid().ToString("N");
        try
        {
            using var activity = new AutomationFixture(); using var store = new SqliteAutomationTaskStore(Path.Combine(directory, "tasks.db"));
            await store.SaveAsync(TaskRule("every", @"C:\Apps\Startup.exe"), default);
            await using var provider = new ProviderFixture(sid, holdCommands: true);
            await using var runtime = new PluginTaskRuntime([new ProgramPlugin(), new AxiomStartupPlugin()], store, sid, activity.Engine, () => 42);
            await runtime.InitializeAsync(); await provider.Observations.Reader.ReadAsync(); await provider.NextCommand();
            provider.DropObservations();
            await WaitUntil(() => Task.FromResult(provider.CancelledCommands == 1));
            await provider.Observations.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
            await WaitUntil(async () => (await runtime.ReadAsync(default)).DetectionAvailable);
            Assert.Equal(1, provider.Calls); Assert.Equal(0, provider.Receipts);
            await runtime.EnsureSafeToStopAsync(default);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string NewDirectory() { var dir = Path.Combine(Path.GetTempPath(), "axiom-startup-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); return dir; }
    private static async Task WaitUntil(Func<Task<bool>> predicate)
    { for (var i = 0; i < 400; i++) { if (await predicate()) return; await Task.Delay(10); } throw new TimeoutException("Runtime did not reach expected state."); }

    private sealed class ProviderFixture : IAsyncDisposable
    {
        private readonly string name, owner = WindowsIdentity.GetCurrent().User!.Value;
        private readonly CancellationTokenSource cancel = new();
        private readonly List<NamedPipeServerStream> streams = [];
        private readonly List<Task> handlers = [];
        private readonly Task server;
        private int calls, receipts, cancelledCommands;
        public int Calls => Volatile.Read(ref calls);
        public int Receipts => Volatile.Read(ref receipts);
        public int CancelledCommands => Volatile.Read(ref cancelledCommands);
        public Channel<ProviderMessage> Observations { get; } = Channel.CreateUnbounded<ProviderMessage>();
        private readonly Channel<ProviderMessage> commands = Channel.CreateUnbounded<ProviderMessage>();
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCommands { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderFixture(string sid, bool waitForReady = false, bool holdCommands = false)
        { name = ProviderProtocol.PipeName(sid); if (!waitForReady) Ready.SetResult(); if (!holdCommands) ReleaseCommands.SetResult(); server = Serve(); }
        public Task<ProviderMessage> NextCommand() => commands.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        public void DropObservations() { lock (streams) foreach (var pipe in streams) pipe.Dispose(); }
        private async Task Serve()
        {
            while (!cancel.IsCancellationRequested)
            {
                var pipe = LocalPipeFactory.Create(name, owner);
                try { await pipe.WaitForConnectionAsync(cancel.Token); }
                catch (OperationCanceledException) { pipe.Dispose(); break; }
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
                    lock (streams) streams.Add(pipe);
                    Observations.Writer.TryWrite(request); await Ready.Task.WaitAsync(cancel.Token);
                    await ProviderProtocol.WriteAsync(pipe, new(1, "snapshot", request.RequestId, Samples: request.Resources!.Select(r => new ProviderSample(r, StateQuality.Known, "axiom.bool", 1, Boolean: false)).ToArray()), cancel.Token);
                    await ProviderProtocol.ReadAsync(pipe, cancel.Token);
                }
                else
                {
                    Interlocked.Increment(ref calls); commands.Writer.TryWrite(request);
                    var disconnected = ProviderProtocol.ReadAsync(pipe, cancel.Token);
                    if (await Task.WhenAny(ReleaseCommands.Task, disconnected) != ReleaseCommands.Task)
                    { Interlocked.Increment(ref cancelledCommands); await disconnected; return; }
                    await ReleaseCommands.Task.WaitAsync(cancel.Token);
                    await ProviderProtocol.WriteAsync(pipe, new(1, "result", request.RequestId, CommandId: request.CommandId, Status: OutcomeStatus.Succeeded, Reason: "fixture action completed"), cancel.Token);
                    Interlocked.Increment(ref receipts); await disconnected;
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync() { cancel.Cancel(); DropObservations(); await server; cancel.Dispose(); }
    }
}
