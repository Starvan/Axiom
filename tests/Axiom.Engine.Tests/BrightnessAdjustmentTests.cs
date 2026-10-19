using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class BrightnessAdjustmentTests
{
    private static TaskInvocation Invocation(bool hardware, string operation = "increase", string step = "5") => new(hardware ? HardwareBrightnessAdjustCapabilities.Adjust : BuiltInBrightnessAdjustCapabilities.Adjust, 1,
        (hardware ? new Dictionary<string, string> { ["target"] = "primary" } : []).Concat(new Dictionary<string, string> { ["operation"] = operation, ["step"] = step, ["delaySeconds"] = "0" }).ToImmutableDictionary());
    [Fact]
    public async Task RelativeCapabilitiesReuseRegistryStrictImportPausedTemplatesAndSavedParametersWithoutChangingAbsoluteSchema()
    {
        var modules = BuiltInAutomationPlugins.Create(); var catalog = new AutomationSnapshot([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
            [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
        var recorder = new UsageRecorder(new(5, true, true, []), DateTimeOffset.UtcNow);
        var detection = new TaskInvocation(HotkeyCapabilities.Pressed, 1, ImmutableDictionary<string, string>.Empty.Add("chords", "Ctrl+Alt+F9"));
        var compiler = new AutomationTaskCompiler(); foreach (var module in modules) { foreach (var d in module.Detections) compiler.Register(d); foreach (var a in module.Actions) compiler.Register(a); }
        Assert.Equal(5, BuiltInBrightnessCapabilities.Descriptor.Fields.Length); Assert.Equal(6, HardwareBrightnessCapabilities.Descriptor.Fields.Length);
        foreach (var hardware in new[] { false, true })
        {
            var invocation = Invocation(hardware); var proposal = new UsageImport(1, recorder.Session.Id, "existing", "relative brightness", "synthetic", detection, invocation);
            var draft = UsageAnalysis.Import(JsonSerializer.Serialize(proposal), recorder.Session, catalog).Draft!; Assert.True(draft.Paused);
            foreach (var invalid in new[] { Invocation(hardware, "toggle"), Invocation(hardware, step: "0"), Invocation(hardware, step: "101"), Invocation(hardware, step: " 5") })
                Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = invalid }), recorder.Session, catalog));
            Assert.Throws<ArgumentException>(() => UsageAnalysis.Import(JsonSerializer.Serialize(proposal with { Action = invocation with { Parameters = invocation.Parameters.Add("percent", "30") } }), recorder.Session, catalog));
            var template = Assert.Single(AutomationTemplates.Build(catalog), t => t.Id == (hardware ? "shortcut-hardware-brightness-up" : "shortcut-internal-brightness-down")); Assert.True(template.Draft.Paused);
            var path = Path.Combine(Path.GetTempPath(), "axiom-synthetic-relative-brightness-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var store = new SqliteAutomationTaskStore(path)) await store.SaveAsync(draft, default);
                using var reopened = new SqliteAutomationTaskStore(path); var loaded = Assert.Single(await reopened.ListAsync()); Assert.Equal(draft.Action.Parameters, loaded.Action.Parameters);
                Assert.Equal(RuleActionKind.Command, compiler.Compile(loaded, "owner", ResourceScope.Session("owner", 1)).Action.Kind);
            }
            finally { foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(f); }
        }
    }
    [Fact]
    public void RelativeMappingClampsKnownRangesAndUnknownPartialOrCancellationNeverWritesOtherTargets()
    {
        Assert.Equal(uint.MaxValue, HardwareBrightnessOperation.AdjustedValue(new(1, uint.MaxValue - 1, uint.MaxValue), 100));
        Assert.Equal(10u, HardwareBrightnessOperation.AdjustedValue(new(10, 11, 110), -5));
        Assert.Equal(75u, HardwareBrightnessOperation.AdjustedValue(new(10, 70, 110), 5));
        Assert.Equal(2u, HardwareBrightnessOperation.AdjustedValue(new(0, 1, 10), 5));
        Assert.Null(HardwareBrightnessOperation.AdjustedValue(new(10, 9, 110), 5)); Assert.Null(HardwareBrightnessOperation.AdjustedValue(null, 5));
        var wmi = new Wmi { Current = 98 }; Assert.True(BuiltInBrightnessOperation.Adjust(wmi, "increase", 5, default).Success); Assert.Equal(100, wmi.Current);
        wmi.Current = 1; Assert.True(BuiltInBrightnessOperation.Adjust(wmi, "decrease", 5, default).Success); Assert.Equal(0, wmi.Current);
        wmi.Current = null; var previous = wmi.Sets; Assert.False(BuiltInBrightnessOperation.Adjust(wmi, "increase", 5, default).Success); Assert.Equal(previous, wmi.Sets);
        var api = new Hardware(); api.Ranges[22] = null; var result = HardwareBrightnessOperation.Adjust(api, "all", "increase", 5, default);
        Assert.False(result.Success); Assert.Contains("部分完成", result.Reason); Assert.Equal(75u, api.Values[11]); Assert.False(api.Values.ContainsKey(22)); Assert.Equal(1, api.Lease.Closes);
        using var stop = new CancellationTokenSource(); var canceled = new Hardware { AfterSet = stop.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() => HardwareBrightnessOperation.Adjust(canceled, "primary", "increase", 5, stop.Token)); Assert.Single(canceled.Values); Assert.Equal(1, canceled.Lease.Closes);
    }
    private sealed class Wmi : IBuiltInBrightnessApi
    {
        internal int? Current; internal int Sets;
        public IReadOnlyList<BrightnessPanel> FindPanels() => [new("synthetic", BrightnessPanelSupport.Supported)];
        public bool Verify(string key) => true;
        public uint Set(string key, byte value) { Sets++; Current = value; return 0; }
        public int? Read(string key) => Current;
    }
    private sealed class Hardware : IHardwareBrightnessApi
    {
        internal readonly MonitorLease Lease = new(); internal readonly Dictionary<nint, HardwareBrightnessRange?> Ranges = [];
        internal readonly Dictionary<nint, uint> Values = []; internal Action? AfterSet;
        public bool IsAuthorized() => true;
        public IReadOnlyList<HardwareLogicalMonitor> ReadLogicalMonitors() => [new(1, true)];
        public IHardwareMonitorLease Acquire(nint logicalMonitor, int remainingCapacity) => Lease;
        public uint? Capabilities(nint monitor) => 2;
        public HardwareBrightnessRange? Read(nint monitor) => Values.TryGetValue(monitor, out var value) ? new(10, value, 110) : Ranges.GetValueOrDefault(monitor, new(10, 70, 110));
        public bool Set(nint monitor, uint brightness) { Values[monitor] = brightness; AfterSet?.Invoke(); return true; }
    }
    private sealed class MonitorLease : IHardwareMonitorLease
    {
        public IReadOnlyList<nint> Handles => new nint[] { 11, 22 }; internal int Closes;
        public void Dispose() => Closes++;
    }
}
