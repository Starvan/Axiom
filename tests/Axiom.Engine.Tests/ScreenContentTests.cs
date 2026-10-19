using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class ScreenContentTests
{
    [Fact]
    public void AdvancedColorMetadataWithinSameModeDoesNotInvalidateCaptureButModeTransitionsDo()
    {
        var mode = new ScreenContentColorModeFilter();
        mode.Baseline(0); // SDR initial subscription
        Assert.False(mode.Update(0)); Assert.False(mode.Update(0)); // brightness/white-level changes
        Assert.True(mode.Update(2)); Assert.False(mode.Update(2)); // HDR transition, then metadata only
        Assert.True(mode.Update(1)); Assert.False(mode.Update(1)); // WCG transition, then metadata only
        Assert.True(mode.Update(0)); Assert.False(mode.Update(0)); // back to SDR
        mode.Baseline(2); // topology rebind establishes a new initial mode
        Assert.False(mode.Update(2)); Assert.True(mode.Update(0));
    }
    private static TaskInvocation Detection(string start = "", string end = "") => new(ScreenContentCapabilities.Bright, 1,
        new Dictionary<string, string> { ["enterPercent"] = "65", ["exitPercent"] = "45", ["stableSeconds"] = "3", ["start"] = start, ["end"] = end }.ToImmutableDictionary());
    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 10, 17, hour, minute, 0, TimeSpan.Zero);
    private static ManualTimeProvider Clock(int hour, int minute = 0) { var time = new ManualTimeProvider(); time.JumpUtc(At(hour, minute) - time.GetUtcNow()); return time; }
    private static async Task Until(Func<bool> ready)
    { for (var i = 0; i < 500; i++) { if (ready()) return; await Task.Delay(10); } throw new TimeoutException("Screen content state did not settle."); }
    private static async Task<ProviderSample> Sample(ChannelReader<ProviderUpdate> updates, Func<ProviderSample, bool> accept)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true) foreach (var sample in (await updates.ReadAsync(timeout.Token)).Samples) if (accept(sample)) return sample;
    }
    private static async Task Stability(ManualTimeProvider time)
    {
        await Until(() => time.Timers.Any(t => t.At == time.GetTimestamp() + TimeSpan.FromSeconds(3).Ticks));
        time.Advance(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void StrictParametersCanonicalResourcesAndAllDayOvernightBoundariesUseOnlyNecessaryDeadlines()
    {
        var all = ScreenContentCapabilities.Validate(Detection()); Assert.True(all.Contains(At(12), TimeZoneInfo.Utc)); Assert.Null(all.NextBoundary(At(12), TimeZoneInfo.Utc));
        var night = ScreenContentCapabilities.Validate(Detection("20:00", "08:00"));
        Assert.Equal(night, ScreenContentCapabilities.ParseResource(night.Resource));
        Assert.False(night.Contains(At(19, 59), TimeZoneInfo.Utc)); Assert.True(night.Contains(At(20), TimeZoneInfo.Utc));
        Assert.True(night.Contains(At(7, 59), TimeZoneInfo.Utc)); Assert.False(night.Contains(At(8), TimeZoneInfo.Utc));
        Assert.Equal(At(20), night.NextBoundary(At(8), TimeZoneInfo.Utc)); Assert.Equal(At(8).AddDays(1), night.NextBoundary(At(20), TimeZoneInfo.Utc));
        foreach (var invocation in new[] { Detection("20:00", ""), Detection("20:00", "20:00"), Detection("8:00", "20:00"),
            Detection() with { Parameters = Detection().Parameters.SetItem("exitPercent", "65") },
            Detection() with { Parameters = Detection().Parameters.SetItem("stableSeconds", "-1") },
            Detection() with { Parameters = Detection().Parameters.SetItem("enterPercent", "65.5") },
            Detection() with { Parameters = Detection().Parameters.Add("monitor", "all") }, Detection() with { SchemaVersion = 2 } })
            Assert.Throws<ArgumentException>(() => ScreenContentCapabilities.Validate(invocation));
        foreach (var text in new[] { "65:45:3@20:00-08:00/", "065:45:3@*", "65:45:3@20:00", "65:65:3@*", "65:45:3@20:00-20:00" })
            Assert.Throws<ArgumentException>(() => ScreenContentCapabilities.ParseResource(text));
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var spring = new ScreenContentSpec(65, 45, 3, 150, 240);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), spring.NextBoundary(new(2026, 3, 8, 6, 59, 0, TimeSpan.Zero), zone));
    }

    [Fact]
    public void FractionalStabilityIsInvariantCanonicalAndRejectsInvalidPrecision()
    {
        foreach (var text in new[] { "0.01", "0.2", "0.50", "1.25", "60", "0" })
        {
            var invocation = Detection("20:00", "08:00") with { Parameters = Detection("20:00", "08:00").Parameters.SetItem("stableSeconds", text) };
            var spec = ScreenContentCapabilities.Validate(invocation);
            Assert.Equal(spec, ScreenContentCapabilities.ParseResource(spec.Resource));
            Assert.Equal(decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture), spec.StableSeconds);
        }
        Assert.Equal("65:45:0.5@*", ScreenContentCapabilities.Validate(Detection() with { Parameters = Detection().Parameters.SetItem("stableSeconds", "0.50") }).Resource);
        foreach (var text in new[] { "0.001", "60.01", "NaN", "Infinity", "0,5", "1e-1", "-0.1" })
            Assert.Throws<ArgumentException>(() => ScreenContentCapabilities.Validate(Detection() with { Parameters = Detection().Parameters.SetItem("stableSeconds", text) }));
    }

    [Fact(Timeout = 10000)]
    public async Task FractionalStabilityProviderUsesSubsecondDeadlinesForEnterAndExitWithoutNewFrames()
    {
        var time = Clock(21); using var env = new FakeEnvironment(); using var stop = new CancellationTokenSource();
        var updates = Channel.CreateUnbounded<ProviderUpdate>();
        var provider = new ScreenContentCapabilityProvider(() => env, time, () => TimeZoneInfo.Utc);
        var spec = ScreenContentCapabilities.Validate(Detection() with { Parameters = Detection().Parameters.SetItem("stableSeconds", "0.25") });
        var run = provider.ObserveAsync([new(ScreenContentCapabilities.Bright, spec.Resource)], u => updates.Writer.TryWrite(u), stop.Token);
        try
        {
            await Until(() => env.Captures.Count == 1);
            env.Captures[0].Emit(new(80));
            await Until(() => time.Timers.Any(t => t.At == TimeSpan.FromMilliseconds(250).Ticks));
            time.Advance(TimeSpan.FromMilliseconds(249));
            while (updates.Reader.TryRead(out var update)) Assert.All(update.Samples, sample => Assert.NotEqual(true, sample.Boolean));
            time.Advance(TimeSpan.FromMilliseconds(1)); Assert.True((await Sample(updates.Reader, s => s.Boolean == true)).Boolean);
            env.Captures[0].Emit(new(20));
            await Until(() => time.Timers.Any(t => t.At == TimeSpan.FromMilliseconds(500).Ticks));
            time.Advance(TimeSpan.FromMilliseconds(249));
            while (updates.Reader.TryRead(out var update)) Assert.All(update.Samples, sample => Assert.NotEqual(false, sample.Boolean));
            time.Advance(TimeSpan.FromMilliseconds(1)); Assert.False((await Sample(updates.Reader, s => s.Boolean == false)).Boolean);
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
    }

    [Fact]
    public void ZeroStabilityEntersAndExitsImmediatelyWithHysteresisAndUnknownBaselineProtection()
    {
        var invocation = Detection() with { Parameters = Detection().Parameters.SetItem("stableSeconds", "0") };
        var spec = ScreenContentCapabilities.Validate(invocation);
        Assert.Equal(spec, ScreenContentCapabilities.ParseResource(spec.Resource)); Assert.Equal(0, spec.StableSeconds);
        Assert.Equal(3, ScreenContentCapabilities.Validate(Detection()).StableSeconds);
        var state = new ScreenContentState(spec.EnterPercent, spec.ExitPercent, TimeSpan.Zero);
        Assert.True(state.Read(new(80), TimeSpan.Zero).Bright); Assert.Null(state.PendingAt);
        Assert.True(state.Read(new(60), TimeSpan.Zero).Bright); Assert.Null(state.PendingAt);
        Assert.False(state.Read(new(45), TimeSpan.Zero).Bright); Assert.Null(state.PendingAt);
        Assert.Null(state.Unknown("HDR").Bright);
        var recovered = state.Read(new(90), TimeSpan.Zero); Assert.True(recovered.Bright); Assert.True(recovered.Baseline);
        Assert.False(state.OutsideWindow().Bright); Assert.Null(state.PendingAt);
    }

    [Fact(Timeout = 10000)]
    public async Task ZeroStabilityProviderPublishesTheFirstFrameImmediatelyWithoutAnyStabilityDeadline()
    {
        var time = Clock(21); using var env = new FakeEnvironment(); using var stop = new CancellationTokenSource();
        var updates = Channel.CreateUnbounded<ProviderUpdate>();
        var provider = new ScreenContentCapabilityProvider(() => env, time, () => TimeZoneInfo.Utc);
        var spec = ScreenContentCapabilities.Validate(Detection() with { Parameters = Detection().Parameters.SetItem("stableSeconds", "0") });
        var run = provider.ObserveAsync([new(ScreenContentCapabilities.Bright, spec.Resource)], u => updates.Writer.TryWrite(u), stop.Token);
        try
        {
            await Until(() => env.Captures.Count == 1);
            env.Captures[0].Emit(new(80)); Assert.True((await Sample(updates.Reader, s => s.Boolean == true)).Boolean);
            env.Captures[0].Emit(new(45)); Assert.False((await Sample(updates.Reader, s => s.Boolean == false)).Boolean);
            Assert.Equal(0, time.GetTimestamp()); Assert.All(time.Timers, t => Assert.Null(t.At));
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
        Assert.True(env.Captures[0].Disposed);
    }

    [Fact]
    public void AreaHysteresisRequiresContinuousStabilityAndUnknownRecoveryRebuildsBaseline()
    {
        var state = new ScreenContentState(65, 45, TimeSpan.FromSeconds(3));
        ScreenContentStateSample Read(double area, int second) => state.Read(new(area), TimeSpan.FromSeconds(second));
        Assert.Null(Read(80, 0).Bright); Assert.Equal(TimeSpan.FromSeconds(3), state.PendingAt);
        Assert.Null(Read(60, 2).Bright); Assert.Equal(TimeSpan.FromSeconds(5), state.PendingAt);
        Assert.False(state.Advance(TimeSpan.FromSeconds(5)).Bright);
        Assert.False(Read(64, 6).Bright); Assert.Null(state.PendingAt);
        Assert.False(Read(65, 7).Bright); Assert.True(state.Advance(TimeSpan.FromSeconds(10)).Bright);
        Assert.True(Read(50, 11).Bright); Assert.Null(state.PendingAt);
        Assert.True(Read(45, 12).Bright); Assert.True(Read(46, 14).Bright); Assert.Null(state.PendingAt);
        Read(40, 15); Assert.False(state.Advance(TimeSpan.FromSeconds(18)).Bright);
        Assert.Null(state.Unknown("locked").Bright); Read(90, 19);
        var recovered = state.Advance(TimeSpan.FromSeconds(22)); Assert.True(recovered.Bright); Assert.True(recovered.Baseline);
        Assert.False(Read(90, 23).Baseline); Assert.Null(state.Read(new(double.NaN), TimeSpan.FromSeconds(24)).Bright);
        Assert.False(state.OutsideWindow().Bright); Assert.Null(state.PendingAt); Assert.Null(state.AreaPercent);
    }

    [Fact(Timeout = 15000)]
    public async Task DetectionHoursActuallyStopCaptureAndOneStabilityDeadlineHandlesAnUnchangedDesktop()
    {
        var time = Clock(19, 59); using var env = new FakeEnvironment(); using var stop = new CancellationTokenSource();
        var updates = Channel.CreateUnbounded<ProviderUpdate>();
        var provider = new ScreenContentCapabilityProvider(() => env, time, () => TimeZoneInfo.Utc);
        var spec = ScreenContentCapabilities.Validate(Detection("20:00", "08:00"));
        var run = provider.ObserveAsync([new(ScreenContentCapabilities.Bright, spec.Resource)], u => updates.Writer.TryWrite(u), stop.Token);
        try
        {
            var outside = await Sample(updates.Reader, s => s.Boolean == false); Assert.Contains("停止", outside.Reason); Assert.Empty(env.Captures);
            await Until(() => time.Timers.Any(t => t.At == time.GetTimestamp() + TimeSpan.FromMinutes(1).Ticks));
            time.Advance(TimeSpan.FromMinutes(1)); await Until(() => env.Captures.Count == 1);
            env.Captures[0].Emit(new(80)); await Sample(updates.Reader, s => s.Reason!.Contains("80.0")); await Stability(time);
            Assert.True((await Sample(updates.Reader, s => s.Boolean == true)).Boolean);
            env.Captures[0].Emit(new(40)); await Sample(updates.Reader, s => s.Reason!.Contains("40.0")); await Stability(time);
            Assert.False((await Sample(updates.Reader, s => s.Boolean == false)).Boolean);
            time.JumpUtc(At(8).AddDays(1) - time.GetUtcNow()); env.Emit("clock-changed");
            await Sample(updates.Reader, s => s.Boolean == false && s.Reason!.Contains("停止")); Assert.True(env.Captures[0].Disposed);
            Assert.Single(env.Captures);
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
        Assert.True(env.Disposed); Assert.All(time.Timers, t => Assert.Null(t.At));
    }

    [Fact(Timeout = 15000)]
    public async Task LockPowerHdrAndCaptureLossReleaseResourcesAndRecoverOnlyFromRelevantEvents()
    {
        var time = Clock(21); using var env = new FakeEnvironment(); using var stop = new CancellationTokenSource();
        var updates = Channel.CreateUnbounded<ProviderUpdate>();
        var provider = new ScreenContentCapabilityProvider(() => env, time, () => TimeZoneInfo.Utc);
        var run = provider.ObserveAsync([new(ScreenContentCapabilities.Bright, ScreenContentCapabilities.Validate(Detection()).Resource)], u => updates.Writer.TryWrite(u), stop.Token);
        try
        {
            await Until(() => env.Captures.Count == 1); var first = env.Captures[0];
            first.Emit(new(90)); await Sample(updates.Reader, s => s.Reason!.Contains("90.0")); await Stability(time);
            await Sample(updates.Reader, s => s.Boolean == true);
            env.Locked = true; env.Emit("session-lock-changed"); await Sample(updates.Reader, s => s.Quality == StateQuality.Unknown && s.Reason!.Contains("锁定")); Assert.True(first.Disposed);
            env.Locked = false; env.Emit("session-lock-changed"); await Until(() => env.Captures.Count == 2);
            env.Emit("screen-off"); await Sample(updates.Reader, s => s.Reason!.Contains("屏幕关闭")); Assert.True(env.Captures[1].Disposed);
            env.NextError = new NotSupportedException("主屏HDR已开启，暂停检测。"); env.Emit("screen-on");
            await Sample(updates.Reader, s => s.Quality == StateQuality.Unknown && s.Reason!.Contains("HDR")); Assert.Equal(2, env.Captures.Count);
            time.Advance(TimeSpan.FromHours(1)); Assert.Equal(2, env.Captures.Count); // no health polling or auto-retry
            env.Emit("display-color-changed"); await Until(() => env.Captures.Count == 3);
            var recovered = env.Captures[2]; recovered.Emit(new(null, "受保护画面"));
            await Sample(updates.Reader, s => s.Reason == "受保护画面"); Assert.False(recovered.Disposed);
            recovered.Emit(new(85)); await Sample(updates.Reader, s => s.Reason!.Contains("85.0")); await Stability(time);
            await Sample(updates.Reader, s => s.Boolean == true);
            recovered.Emit(new(null, "设备已失效", CaptureLost: true)); await Sample(updates.Reader, s => s.Reason == "设备已失效"); Assert.True(recovered.Disposed);
            env.Emit("display-changed"); await Until(() => env.Captures.Count == 4);
            env.Emit("screen-reset"); await Sample(updates.Reader, s => s.Reason!.Contains("睡眠")); Assert.True(env.Captures[3].Disposed);
            env.Emit("screen-on"); await Until(() => env.Captures.Count == 5);
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); }
        Assert.All(env.Captures, c => Assert.True(c.Disposed)); Assert.All(time.Timers, t => Assert.Null(t.At));
    }

    [Fact]
    public async Task CatalogTemplateStrictImportPersistenceAndCompositeUseExistingBrightnessRestoreOnly()
    {
        var modules = BuiltInAutomationPlugins.Create(); var plugin = Assert.Single(modules.OfType<ScreenContentPlugin>());
        Assert.Equal(2, plugin.Combinations.Length); Assert.All(plugin.Combinations, c => Assert.Contains(c.ActionId, new[] { BuiltInBrightnessRestoreCapabilities.Set, HardwareBrightnessRestoreCapabilities.Set }));
        var compiler = new AutomationTaskCompiler(supportsCompositeDetection: true);
        foreach (var m in modules) { foreach (var d in m.Detections) compiler.Register(d); foreach (var a in m.Actions) compiler.Register(a); foreach (var c in m.Combinations) compiler.RegisterCombination(c); }
        var catalog = new AutomationSnapshot([], compiler.Catalog, [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", At(21), [], CompositeDetectionVersion: 1);
        var draft = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == "screen-content-night-dim").Draft;
        Assert.True(draft.Paused); Assert.Equal("20:00", draft.Detection.Parameters["start"]); Assert.Equal("40", draft.Action.Parameters["percent"]);
        draft = draft with { Detection = draft.Detection with { Parameters = draft.Detection.Parameters.SetItem("stableSeconds", "0.25") } };
        var compiled = compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)); Assert.Equal(RuleActionKind.Desired, compiled.Action.Kind);
        var leaf = new TaskDetectionExpression("leaf", draft.Detection, []);
        Assert.NotNull(compiler.Compile(draft with { SchemaVersion = 3, DetectionExpression = new("not", null, [leaf]) }, "owner", ResourceScope.Session("owner", 1)));
        var recorder = new UsageRecorder(new(5, false, false, [], SystemEvents: true), At(21));
        var fractional = Detection() with { Parameters = Detection().Parameters.SetItem("stableSeconds", "0.25") };
        var proposal = new UsageImport(1, recorder.Session.Id, "existing", "night dim", "synthetic", fractional, draft.Action);
        Assert.True(UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!.Paused);
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Detection = Detection() with { Parameters = Detection().Parameters.SetItem("exitPercent", "99") } }), recorder.Session, catalog));
        Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Detection = draft.Detection }), recorder.Session, catalog)); // HH:mm text remains manually reviewed, like other imported free text.
        var path = Path.Combine(Path.GetTempPath(), "axiom-synthetic-screen-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var store = new SqliteAutomationTaskStore(path)) await store.SaveAsync(draft, default);
            using var reopened = new SqliteAutomationTaskStore(path); var loaded = Assert.Single(await reopened.ListAsync());
            Assert.Equal(draft.Detection.Parameters, loaded.Detection.Parameters); Assert.NotNull(compiler.Compile(loaded, "owner", compiled.Scope));
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(f); }
    }

    [Fact(Timeout = 10000)]
    public async Task InitialKnownBrightnessConditionCanHoldDesiredAndFalseUnknownPauseReleaseIt()
    {
        var executor = new DesiredExecutor(); using var fixture = new AutomationFixture(executor); var schema = new ValueSchema("axiom.bool", 1);
        fixture.Engine.RegisterCapability(AutomationFixture.Binding(ScreenContentCapabilities.Bright, schema: schema));
        var condition = new ScreenContentTaskDetection().Compile(Detection(), ResourceScope.Machine); var key = Assert.Single(condition.Dependencies);
        var source = fixture.Engine.RegisterSource("synthetic-screen", [key]); long seq = 0;
        void Set(bool? value) => fixture.Engine.ApplySnapshot(source, ++seq, [new(key, value.HasValue ? StateQuality.Known : StateQuality.Unknown,
            value.HasValue ? StateValue.From(schema, value.Value) : null, fixture.Clock.UtcNow)]);
        var rule = new RuleDefinition("screen-desired", 1, "owner", ResourceScope.Machine, condition, RuleAction.Desired(AutomationFixture.Output, StateValue.From(AutomationFixture.Schema, true)));
        Set(true); fixture.Engine.SetRule(rule); fixture.Engine.StartExecution(); await fixture.Engine.WaitForIdleAsync(); Assert.True(executor.Authorized);
        Set(false); Assert.False(executor.Authorized); Set(true); await fixture.Engine.WaitForIdleAsync(); Set(null); Assert.False(executor.Authorized);
        Set(true); await fixture.Engine.WaitForIdleAsync(); fixture.Engine.SetRule(new(rule.Id, 2, "owner", rule.Scope, condition, rule.Action, enabled: false)); Assert.False(executor.Authorized);
    }
    private sealed class FakeCapture : IScreenContentSource
    {
        internal readonly Channel<ScreenContentReading> Queue = Channel.CreateUnbounded<ScreenContentReading>();
        internal bool Disposed;
        public ChannelReader<ScreenContentReading> Readings => Queue.Reader;
        internal void Emit(ScreenContentReading reading) => Assert.True(Queue.Writer.TryWrite(reading));
        public void Dispose() { Disposed = true; Queue.Writer.TryComplete(); }
    }
    private sealed class FakeEnvironment : IScreenContentEnvironment
    {
        private readonly Channel<string> events = Channel.CreateUnbounded<string>();
        public ChannelReader<string> Events => events.Reader;
        internal readonly List<FakeCapture> Captures = [];
        internal bool? Locked = false; internal Exception? NextError; internal bool Disposed;
        internal FakeEnvironment() { Emit("screen-on"); }
        internal void Emit(string kind) => Assert.True(events.Writer.TryWrite(kind));
        public bool? ReadLocked(CancellationToken token) => Locked;
        public IScreenContentSource Capture() { if (NextError is { } error) { NextError = null; throw error; } var capture = new FakeCapture(); Captures.Add(capture); return capture; }
        public void Dispose() { Disposed = true; events.Writer.TryComplete(); }
    }
    private sealed class DesiredExecutor : ICapabilityExecutor, ILeasedDesiredExecutor
    {
        internal bool Authorized;
        public void SetDesiredAuthorization(ResourceKey key, long generation, bool authorized) => Authorized = authorized;
        public void ReleaseAllDesired() => Authorized = false;
        public ValueTask<Outcome> ApplyDesiredAsync(DesiredState desired, CancellationToken token) => ValueTask.FromResult(new Outcome(OutcomeStatus.Succeeded, "synthetic lease", Guid.NewGuid()));
        public ValueTask<Outcome> ExecuteCommandAsync(Command command, CancellationToken token) => throw new InvalidOperationException("Unexpected command.");
    }
}
