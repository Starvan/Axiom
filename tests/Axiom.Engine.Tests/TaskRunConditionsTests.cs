using System.Collections.Immutable;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.ServiceHost;
using Axiom.SessionAgent;
using Microsoft.Data.Sqlite;
using static Axiom.Engine.Tests.AutomationFixture;

namespace Axiom.Engine.Tests;

public sealed class TaskRunConditionsTests
{
    private static TaskInvocation Hdr() => new(HdrStateCapabilities.State, 1, ImmutableDictionary<string, string>.Empty.Add("state", "on"));
    private static TaskInvocation Awake() => new(KeepAwakeCapabilities.Hold, 1, ImmutableDictionary<string, string>.Empty.Add("target", "display"));
    private static AutomationTask Config(TaskRunConditions? conditions = null) => new(Guid.NewGuid().ToString("D"), 0, "synthetic", false,
        Hdr(), Awake(), conditions is null ? 1 : 4, RunConditions: conditions);

    [Fact]
    public void OuterHdrUnknownAndTimeWindowsAreConjunctiveAndNearestBoundaryIsExact()
    {
        var at = new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);
        var window = new WeeklyTimeWindow(9 * 60, 17 * 60, 127);
        var conditions = new TaskRunConditions("off", window);
        Assert.Contains("未知", conditions.PauseReason(null, at, TimeZoneInfo.Utc));
        Assert.Contains("关闭", conditions.PauseReason(true, at, TimeZoneInfo.Utc));
        Assert.Contains("时段", conditions.PauseReason(false, at, TimeZoneInfo.Utc));
        Assert.Null(conditions.PauseReason(false, at.AddHours(2), TimeZoneInfo.Utc));
        Assert.Equal(at.AddHours(1), window.NextBoundary(at, TimeZoneInfo.Utc));
        Assert.Equal(at.AddHours(9), window.NextBoundary(at.AddHours(1), TimeZoneInfo.Utc));
        var overnight = new WeeklyTimeWindow(22 * 60, 6 * 60, 1); // Sunday start
        Assert.True(overnight.Contains(at.AddHours(17), TimeZoneInfo.Utc));
        Assert.Equal(at.AddHours(22), overnight.NextBoundary(at.AddHours(17), TimeZoneInfo.Utc));
        Assert.Throws<ArgumentException>(() => new TaskRunConditions("maybe").Validate());
    }

    [Fact]
    public void InitiallyPausedRuleNeverAcquiresInnerDemandAndSharedRulesKeepTheirDemand()
    {
        using var f = new AutomationFixture();
        f.Engine.SetRule(f.Rule("gated"), "HDR unknown");
        Assert.Empty(f.Engine.GetObservationPlan().References);
        var evaluations = f.Engine.GetRuleEvaluation("gated").EvaluationCount;
        f.Update(true); Assert.Equal(evaluations, f.Engine.GetRuleEvaluation("gated").EvaluationCount);
        f.Engine.SetRule(f.Rule("shared", output: Noise));
        var observers = new FakeObservers(); observers.Apply(f.Engine);
        f.Engine.SetRuleAutomaticPause("gated", null);
        Assert.Equal(2, f.Engine.GetObservationPlan().References[Input]);
        Assert.Equal(TruthValue.True, f.Engine.GetRuleEvaluation("gated").Value);
        f.Engine.SetRuleAutomaticPause("gated", "time closed");
        Assert.Equal(1, f.Engine.GetObservationPlan().References[Input]);
        Assert.DoesNotContain(Input, f.Engine.GetObservationPlan().Release);
        Assert.Equal(1, f.Engine.GetRuleEvaluation("gated").Version);
        evaluations = f.Engine.GetRuleEvaluation("gated").EvaluationCount;
        f.Update(false); Assert.Equal(evaluations, f.Engine.GetRuleEvaluation("gated").EvaluationCount);
    }

    [Fact(Timeout = 10000)]
    public async Task ResumeUsesNewBaselineAndDoesNotReplayMissedCommands()
    {
        var executor = new ControlledExecutor(); using var f = new AutomationFixture(executor);
        f.Engine.SetRule(f.Rule(command: true), "closed"); f.Engine.StartExecution();
        f.Update(true); f.Engine.SetRuleAutomaticPause("rule", null);
        Assert.Equal("AwaitingAutomaticResumeSnapshot", f.Engine.GetRuleEvaluation("rule").Reason);
        f.Update(false); f.Update(true); await f.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        f.Engine.ApplySnapshot(f.Source, ++f.Sequence, [Observe(Input, true), Observe(Output, false), Observe(Noise, false)]);
        await f.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        f.Update(false); f.Update(true); var call = await executor.NextAsync(); call.Complete();
        await f.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
    }

    [Fact(Timeout = 10000)]
    public async Task EventResumeBaselinesCounterAndNeverReplaysMissedEvents()
    {
        var executor = new ControlledExecutor(); using var f = new AutomationFixture(executor);
        var schema = new ValueSchema("events", 1); var key = new ResourceKey("events", "counter", ResourceScope.Machine);
        f.Engine.RegisterCapability(new(new("events", schema, schema, ScopeKind.Machine, true, false, false, ExecutionLocation.Service,
            "fake", true, "fake", false, TimeSpan.FromSeconds(10), true, ConcurrencyScope.Resource, RetryCategory.Never,
            ConfirmationSource.Observation, EventFidelity.EdgeSensitive, TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active", "owner", []), ScalarKind.Number, ScalarKind.Number));
        var source = f.Engine.RegisterSource("counter", [key]);
        Observation Value(decimal count) => new(key, StateQuality.Known, StateValue.From(schema, count), DateTimeOffset.UnixEpoch);
        f.Engine.ApplySnapshot(source, 0, [Value(0)]);
        f.Engine.SetRule(new("event", 1, "owner", ResourceScope.Machine, RuleCondition.Event(key, schema), f.Rule(command: true).Action), "closed");
        f.Engine.StartExecution(); f.Engine.Update(source, 1, Value(50));
        f.Engine.SetRuleAutomaticPause("event", null);
        f.Engine.ApplySnapshot(source, 2, [Value(50)]); await f.Engine.WaitForIdleAsync(); Assert.Equal(0, executor.Count);
        f.Engine.Update(source, 3, Value(51)); var call = await executor.NextAsync(); call.Complete();
        await f.Engine.WaitForIdleAsync(); Assert.Equal(1, executor.Count);
    }

    [Fact(Timeout = 10000)]
    public async Task PauseCancelsInFlightButKeepsPhysicalObservationFenceUntilCallReturns()
    {
        var executor = new ControlledExecutor(); using var f = new AutomationFixture(executor);
        f.Engine.SetRule(f.Rule(command: true)); f.Engine.StartExecution(); f.Update(true);
        var call = await executor.NextAsync();
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = call.Token.Register(() => canceled.TrySetResult());
        f.Engine.SetRuleAutomaticPause("rule", "closed"); await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(Input, f.Engine.GetObservationPlan().References.Keys);
        Assert.Equal(1, f.Engine.InFlightExecutions);
        call.Complete(); await f.Engine.WaitForIdleAsync();
        Assert.Empty(f.Engine.GetObservationPlan().References);
    }

    [Fact(Timeout = 10000)]
    public async Task AutomaticPauseReleasesDesiredAuthorizationAndManualDisabledStaysDisabled()
    {
        var executor = new LeaseExecutor(); using var f = new AutomationFixture(executor);
        f.Engine.SetRule(f.Rule()); f.Engine.StartExecution(); f.Update(true); await f.Engine.WaitForIdleAsync();
        Assert.True(executor.Authorized);
        f.Engine.SetRuleAutomaticPause("rule", "closed"); Assert.False(executor.Authorized);
        Assert.Empty(f.Engine.GetObservationPlan().References);
        f.Engine.SetRule(new("manual", 1, "owner", ResourceScope.Machine, f.Rule().Condition, f.Rule().Action, enabled: false), "closed");
        f.Engine.SetRuleAutomaticPause("manual", null);
        Assert.Equal("Disabled", f.Engine.GetRuleEvaluation("manual").Reason);
        Assert.Empty(f.Engine.GetObservationPlan().References);
    }

    [Fact]
    public void VersionFourRequiresConditionsAndOlderCompilersCannotIgnoreThem()
    {
        var task = Config(new("off", new(540, 1020, 127)));
        Assert.Single(TaskDetectionExpressions.ValidateTask(task));
        Assert.Throws<ArgumentException>(() => TaskDetectionExpressions.ValidateTask(task with { SchemaVersion = 1 }));
        Assert.Throws<ArgumentException>(() => TaskDetectionExpressions.ValidateTask(task with { RunConditions = null }));
        var old = Compiler(false); Assert.Throws<ArgumentException>(() => old.Compile(task, "owner", ResourceScope.Session("owner", 1)));
        Assert.Equal(RuleActionKind.Desired, Compiler(true).Compile(task, "owner", ResourceScope.Session("owner", 1)).Action.Kind);
        Assert.True(Compiler(true).Catalog.Single(c => c.Kind == TaskCapabilityKind.Action).SupportsRunConditions);
        Assert.False(GameHdrTasks.CreateCompiler().Catalog.Single(c => c.Kind == TaskCapabilityKind.Action).SupportsRunConditions);
        var oldSnapshot = new AutomationSnapshot([], [], [], true, "", "", DateTimeOffset.UnixEpoch, []);
        Assert.Empty(JsonSerializer.Deserialize<AutomationSnapshot>(JsonSerializer.Serialize(oldSnapshot))!.RunStates);
        var watch = new AutomationMessage(1, "watch-run-states", "test", DateTimeOffset.UtcNow.AddSeconds(5));
        AutomationProtocol.ValidateRequest(watch, DateTimeOffset.UtcNow);
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(watch, DateTimeOffset.UtcNow, shortcutAgent: true));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(watch with { RunStatesSequence = 0 }, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task SqliteAndProtocolPreserveOldVersionsAndProtectVersionFourFromOldReaders()
    {
        var path = Path.Combine(Path.GetTempPath(), "axiom-run-conditions-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var gated = Config(new("off")); var old = Config();
            using (var store = new SqliteAutomationTaskStore(path)) { await store.SaveAsync(old, default); await store.SaveAsync(gated, default); }
            using (var store = new SqliteAutomationTaskStore(path))
            {
                var tasks = await store.ListAsync(); Assert.Equal(1, tasks.Single(t => t.Id == old.Id).SchemaVersion);
                var saved = tasks.Single(t => t.Id == gated.Id); Assert.Equal(gated.RunConditions, saved.RunConditions);
                await store.SaveAsync(saved with { SchemaVersion = 1, RunConditions = null }, default);
            }
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;"; Assert.Equal(4L, Convert.ToInt64(cmd.ExecuteScalar()));
            using var wire = new MemoryStream(); await AutomationProtocol.WriteAsync(wire, new(1, "save", "test", DateTimeOffset.UtcNow.AddSeconds(5), Task: gated), default); wire.Position = 0;
            var read = await AutomationProtocol.ReadAsync(wire, default); AutomationProtocol.ValidateRequest(read, DateTimeOffset.UtcNow);
            Assert.Equal(gated.RunConditions, read.Task!.RunConditions);
            cmd.CommandText = "PRAGMA user_version=5;"; cmd.ExecuteNonQuery();
            Assert.Throws<InvalidDataException>(() => new SqliteAutomationTaskStore(path));
        }
        finally { SqliteConnection.ClearAllPools(); foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file); }
    }

    [Fact]
    public void StrictImportRequiresVersionAndNegotiationAndRejectsUnknownConditionFields()
    {
        var compiler = Compiler(true);
        var catalog = new AutomationSnapshot([], compiler.Catalog, [.. new HdrStatePlugin().Combinations], true, "", "", DateTimeOffset.UtcNow, [], RunConditionsVersion: 1);
        var recorder = new UsageRecorder(new(5, true, true, []), DateTimeOffset.UtcNow);
        var proposal = new UsageImport(3, recorder.Session.Id, "existing", "synthetic", "conditions", Hdr(), Awake(), RunConditions: new("off"));
        var json = JsonSerializer.Serialize(proposal); var draft = UsageAnalysis.Import(json, recorder.Session, catalog).Draft!;
        Assert.True(draft.Paused); Assert.Equal(4, draft.SchemaVersion); Assert.Equal(proposal.RunConditions, draft.RunConditions);
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(json, recorder.Session, catalog with { RunConditionsVersion = 0 }));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Version = 1 }), recorder.Session, catalog));
        Assert.Throws<JsonException>(() => UsageAnalysis.Import(json.Replace("\"Hdr\":", "\"Unexpected\":true,\"Hdr\":"), recorder.Session, catalog));
        Assert.True(HdrStateCapabilities.Descriptor.IsStateCondition);
        Assert.Throws<ArgumentException>(() => HdrStateCapabilities.Validate(Hdr() with { Parameters = Hdr().Parameters.Add("ignored", "true") }));
    }

    [Fact(Timeout = 10000)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task HostOuterWatcherSurvivesPausedInnerDemandAndStatusWatchPublishesWithoutPolling()
    {
        var path = Path.Combine(Path.GetTempPath(), "axiom-run-host-" + Guid.NewGuid().ToString("N") + ".db");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var token = deadline.Token;
        var hdr = Channel.CreateUnbounded<bool?>(); var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sid = "test-" + Guid.NewGuid().ToString("N");
        try
        {
            using var fixture = new AutomationFixture(); using var store = new SqliteAutomationTaskStore(path);
            var process = new TaskInvocation("process.running", 2, ImmutableDictionary<string, string>.Empty.Add("processNames", "synthetic.exe"));
            var action = new TaskInvocation("program.start", 1, ProgramCapabilities.Describe(true).Fields.Where(f => f.DefaultValue is not null)
                .ToImmutableDictionary(f => f.Id, f => f.DefaultValue!).SetItem("executable", @"C:\Apps\synthetic.exe"));
            var saved = await store.SaveAsync(new(Guid.NewGuid().ToString("D"), 0, "synthetic", false, process, action, SchemaVersion: 4, RunConditions: new("on")), token);
            async Task Observe(Action<bool?> publish, CancellationToken cancellation)
            { ready.TrySetResult(); await foreach (var value in hdr.Reader.ReadAllAsync(cancellation)) publish(value); }
            await using var runtime = new PluginTaskRuntime([new ProgramPlugin()], store, sid, fixture.Engine, observeRunHdr: Observe);
            await runtime.InitializeAsync(); await ready.Task.WaitAsync(token);
            await using var states = runtime.WatchRunStatesAsync(token).GetAsyncEnumerator(token);
            Assert.True(await states.MoveNextAsync()); Assert.True(Assert.Single(states.Current).AutomaticallyPaused);
            Assert.Empty((await runtime.ReadAsync(token)).Observations);
            var actualSid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            using var provider = LocalPipeFactory.Create(ProviderProtocol.PipeName(sid), actualSid); var connected = provider.WaitForConnectionAsync(token);
            hdr.Writer.TryWrite(true); Assert.True(await states.MoveNextAsync()); Assert.False(Assert.Single(states.Current).AutomaticallyPaused);
            await connected; var request = await ProviderProtocol.ReadAsync(provider, token);
            Assert.Single(request.Resources!); Assert.Equal("process.running", request.Resources![0].CapabilityId);
            await ProviderProtocol.WriteAsync(provider, new(1, "snapshot", request.RequestId, Samples: [new(request.Resources[0], StateQuality.Known, "axiom.bool", 1, Boolean: false)]), token);
            hdr.Writer.TryWrite(null); Assert.True(await states.MoveNextAsync()); Assert.True(Assert.Single(states.Current).AutomaticallyPaused);
            saved = await runtime.SaveAsync(saved with { Paused = true }, token);
            Assert.True(await states.MoveNextAsync()); Assert.False(Assert.Single(states.Current).AutomaticallyPaused);
            Assert.True((await runtime.ReadAsync(token)).Tasks.Single().Paused);
            await runtime.EnsureSafeToStopAsync(token);
        }
        finally { SqliteConnection.ClearAllPools(); foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file); }
    }

    [Fact(Timeout = 10000)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task HostTimeGateUsesOneNearestDeadlineAndReleasesItWhenManuallyPaused()
    {
        var path = Path.Combine(Path.GetTempPath(), "axiom-run-time-" + Guid.NewGuid().ToString("N") + ".db");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)); var token = deadline.Token;
        var time = new ManualTimeProvider();
        var at = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        time.JumpUtc(new DateTimeOffset(at) - time.GetUtcNow());
        try
        {
            var sid = "test-" + Guid.NewGuid().ToString("N");
            var actualSid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            using var provider = LocalPipeFactory.Create(ProviderProtocol.PipeName(sid), actualSid);
            var accepted = provider.WaitForConnectionAsync(token);
            using var fixture = new AutomationFixture(); using var store = new SqliteAutomationTaskStore(path);
            var task = await store.SaveAsync(Config(new("any", new(540, 1020, 127))), token);
            await using var runtime = new PluginTaskRuntime([new HdrStatePlugin(), new KeepAwakePlugin()], store,
                sid, fixture.Engine, windowTime: time);
            await runtime.InitializeAsync();
            await accepted;
            var request = await ProviderProtocol.ReadAsync(provider, token);
            Assert.Equal(new ProviderResource(SystemEventCapabilities.ClockEvents, "local"), Assert.Single(request.Resources!));
            await ProviderProtocol.WriteAsync(provider, new(1, "snapshot", request.RequestId, Samples: [new(request.Resources![0], StateQuality.Known, "axiom.event-count", 1, Number: 0)]), token);
            await using var states = runtime.WatchRunStatesAsync(token).GetAsyncEnumerator(token);
            Assert.True(await states.MoveNextAsync()); Assert.True(Assert.Single(states.Current).AutomaticallyPaused);
            Assert.Single(time.Timers, t => t.At.HasValue);
            Assert.Equal(TimeSpan.FromHours(1), time.Timers.Single(t => t.At.HasValue).LastDue);
            time.Advance(TimeSpan.FromHours(1)); Assert.True(await states.MoveNextAsync()); Assert.False(Assert.Single(states.Current).AutomaticallyPaused);
            while (!time.Timers.Any(t => t.At.HasValue)) await Task.Delay(1, token);
            Assert.Single(time.Timers, t => t.At.HasValue);
            Assert.Equal(TimeSpan.FromHours(8), time.Timers.Single(t => t.At.HasValue).LastDue);
            // A wall-clock/resume notification must recalculate immediately,
            // even though the previously scheduled monotonic deadline has not fired.
            time.JumpUtc(TimeSpan.FromHours(9));
            await ProviderProtocol.WriteAsync(provider, new(1, "update", request.RequestId, Sequence: 1,
                Samples: [new(request.Resources[0], StateQuality.Known, "axiom.event-count", 1, Number: 1)]), token);
            Assert.True(await states.MoveNextAsync()); Assert.True(Assert.Single(states.Current).AutomaticallyPaused);
            task = await runtime.SaveAsync(task with { Paused = true }, token);
            Assert.True(await states.MoveNextAsync()); Assert.False(Assert.Single(states.Current).AutomaticallyPaused);
            await runtime.EnsureSafeToStopAsync(token);
            Assert.All(time.Timers, t => Assert.Null(t.At));
        }
        finally { SqliteConnection.ClearAllPools(); foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file); }
    }

    [Fact(Timeout = 10000)]
    public async Task InternalClockProviderReportsInitialAndOnlyNewNotificationsAndReleasesSourceOnCancel()
    {
        using var stop = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var changes = Channel.CreateUnbounded<byte>(); var updates = Channel.CreateUnbounded<ProviderUpdate>(); var released = false;
        async Task Observe(Action changed, CancellationToken token)
        {
            try { changed(); await foreach (var _ in changes.Reader.ReadAllAsync(token)) changed(); }
            finally { released = true; }
        }
        var provider = new ClockEventCapabilityProvider(Observe);
        var resource = new ProviderResource(SystemEventCapabilities.ClockEvents, "local");
        var run = provider.ObserveAsync([resource], update => updates.Writer.TryWrite(update), stop.Token);
        var initial = await updates.Reader.ReadAsync(deadline.Token);
        Assert.True(initial.Snapshot); Assert.Equal(0m, Assert.Single(initial.Samples).Number);
        Assert.False(updates.Reader.TryRead(out _));
        changes.Writer.TryWrite(1); var changed = await updates.Reader.ReadAsync(deadline.Token);
        Assert.False(changed.Snapshot); Assert.Equal(1m, Assert.Single(changed.Samples).Number);
        Assert.DoesNotContain(BuiltInAutomationPlugins.Create().SelectMany(p => p.Detections), d => d.Descriptor.Id == SystemEventCapabilities.ClockEvents);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); Assert.True(released);
    }

    private static AutomationTaskCompiler Compiler(bool supports)
    {
        var compiler = new AutomationTaskCompiler(supportsCompositeDetection: true, supportsRunConditions: supports);
        foreach (var module in new IAutomationPlugin[] { new HdrStatePlugin(), new KeepAwakePlugin() })
        { foreach (var detection in module.Detections) compiler.Register(detection); foreach (var action in module.Actions) compiler.Register(action); foreach (var combination in module.Combinations) compiler.RegisterCombination(combination); }
        return compiler;
    }
    private sealed class LeaseExecutor : ICapabilityExecutor, ILeasedDesiredExecutor
    {
        public bool Authorized;
        public void SetDesiredAuthorization(ResourceKey key, long generation, bool authorized) => Authorized = authorized;
        public void ReleaseAllDesired() => Authorized = false;
        public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken token) => ValueTask.FromResult(new Outcome(OutcomeStatus.Succeeded, "synthetic", Guid.NewGuid()));
        public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken token) => throw new InvalidOperationException();
    }
}
