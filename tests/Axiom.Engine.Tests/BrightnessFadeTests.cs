using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

[Collection("Brightness native ownership")]
public sealed class BrightnessFadeTests
{
    [Fact]
    public void SixCapabilitiesAcceptLegacyAndOptionalFadeButRejectMalformedOrExtraParameters()
    {
        foreach (var descriptor in Descriptors())
        {
            var legacy = descriptor.Fields.Where(f => !f.Id.StartsWith("fade", StringComparison.Ordinal))
                .ToImmutableDictionary(f => f.Id, f => f.DefaultValue!);
            var invocation = new TaskInvocation(descriptor.Id, 1, legacy);
            Validate(invocation); Assert.False(BrightnessFadeSettings.Parse(legacy).Enabled);
            Assert.Equal(TimeSpan.FromSeconds(10), BrightnessFadeOptions.Parse(legacy).OperationTimeout);
            Assert.Equal("false", descriptor.Fields.Single(f => f.Id == "fadeEnabled").DefaultValue);
            Assert.Equal("3", descriptor.Fields.Single(f => f.Id == "fadeDurationSeconds").DefaultValue);
            Assert.Equal("10", descriptor.Fields.Single(f => f.Id == "fadeWritesPerSecond").DefaultValue);
            Assert.Equal(10, BrightnessFadeSettings.Parse(legacy).WritesPerSecond);
            Assert.Equal(10, BrightnessFadeOptions.Parse(legacy).WritesPerSecond);
            Assert.All(descriptor.Fields.Where(f => f.Id.StartsWith("fade", StringComparison.Ordinal)), f => Assert.Equal("渐变设置", f.Group));
            foreach (var duration in new[] { "1", "3", "15" })
            {
                var configured = legacy.Add("fadeEnabled", "true").Add("fadeDurationSeconds", duration);
                Validate(invocation with { Parameters = configured });
                Assert.True(BrightnessFadeSettings.Parse(configured).Enabled);
                Assert.Equal(BrightnessFadeSettings.Parse(configured).OperationTimeout, BrightnessFadeOptions.Parse(configured).OperationTimeout);
            }
            Validate(invocation with { Parameters = legacy.Add("fadeEnabled", "true") });
            Validate(invocation with { Parameters = legacy.Add("fadeDurationSeconds", "5") });
            foreach (var frequency in new[] { "1", "10", "18", "60" })
            {
                var configured = legacy.Add("fadeWritesPerSecond", frequency);
                Validate(invocation with { Parameters = configured });
                Assert.Equal(int.Parse(frequency), BrightnessFadeSettings.Parse(configured).WritesPerSecond);
                Assert.Equal(BrightnessFadeSettings.Parse(configured).WritesPerSecond, BrightnessFadeOptions.Parse(configured).WritesPerSecond);
            }
            foreach (var invalid in new[] { "0", "61", "18.0", "+18", " 18", "18 ", "1e1", "", "-1" })
            {
                var configured = legacy.Add("fadeWritesPerSecond", invalid);
                Assert.Throws<ArgumentException>(() => Validate(invocation with { Parameters = configured }));
                Assert.Throws<ArgumentException>(() => BrightnessFadeOptions.Parse(configured));
            }
            foreach (var invalid in new[] { "0", "16", "1.5", " 3", "", "-1" })
                Assert.Throws<ArgumentException>(() => Validate(invocation with { Parameters = legacy.Add("fadeDurationSeconds", invalid) }));
            foreach (var invalid in new[] { "True", "1", "", "false " })
                Assert.Throws<ArgumentException>(() => Validate(invocation with { Parameters = legacy.Add("fadeEnabled", invalid) }));
            Assert.Throws<ArgumentException>(() => Validate(invocation with { Parameters = legacy.Add("fadeSpeed", "fast") }));
        }
        var modules = BuiltInAutomationPlugins.Create();
        foreach (var binding in modules.OfType<BuiltInBrightnessPlugin>().Single().Bindings.Concat(modules.OfType<HardwareBrightnessPlugin>().Single().Bindings))
            Assert.Equal(TimeSpan.FromSeconds(binding.Descriptor.DesiredState ? 40 : 30), binding.Descriptor.Timeout);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WmiAndDdcAbsoluteAndRelativeFadeHaveMonotonicDistinctConfirmedStepsAndExactEndpoints(bool hardware, bool relative)
    {
        var clock = new ManualTimeProvider(); var api = new Display(clock); var fade = new BrightnessFadeOptions(true, 1);
        void Wait(TimeSpan delay, CancellationToken token) { token.ThrowIfCancellationRequested(); clock.Advance(delay); }
        var result = hardware
            ? relative ? HardwareBrightnessOperation.Adjust(api, "primary", "increase", 40, default, fade, clock, Wait) : HardwareBrightnessOperation.Apply(api, "primary", 90, default, fade, clock, Wait)
            : relative ? BuiltInBrightnessOperation.Adjust(api, "increase", 40, default, fade: fade, time: clock, wait: Wait) : BuiltInBrightnessOperation.Apply(api, 90, default, fade: fade, time: clock, wait: Wait);
        Assert.True(result.Success); Assert.Equal(90u, api.Current);
        Assert.InRange(api.Writes.Count, 2, 11); Assert.Equal(api.Writes.Distinct(), api.Writes);
        Assert.Equal(api.Writes.Order(), api.Writes); Assert.Equal(90u, api.Writes[^1]);
        Assert.All(api.WriteTimes.Zip(api.WriteTimes.Skip(1)), pair => Assert.True(pair.Second - pair.First >= TimeSpan.FromMilliseconds(100)));
        Assert.Equal("read", api.Calls[^1]); // The endpoint is never acknowledged before its native readback.
        Assert.Equal(1, hardware ? api.Acquisitions : api.Discoveries); Assert.Equal(hardware ? 1 : 0, api.Closes);
        Assert.InRange(clock.GetElapsedTime(0), TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(1200));
    }

    [Fact]
    public void FadeConfirmsUnchangedValueOnceAndUnknownOrExternalChangeStillRelinquishesWhileLegacyKeepsTwoReads()
    {
        foreach (var enabled in new[] { false, true })
        foreach (var state in new[] { "unchanged", "unknown", "external" })
        {
            var clock = new ManualTimeProvider(); var reads = 0; var writes = 0; var waits = 0;
            uint? current = state == "unknown" ? null : state == "external" ? 17u : 50u;
            var target = new BrightnessRestoreTarget(50, p => (uint)p, () => { reads++; return current; },
                _ => { writes++; return true; });
            BrightnessFade.Apply([(target, 50)], new(enabled, 3), default, clock, (_, _) => waits++);
            Assert.Equal(state == "unchanged" && !enabled ? 2 : 1, reads);
            Assert.Equal(state == "unchanged", target.Transaction.Owned);
            Assert.Equal(0, writes); Assert.Equal(0, waits);
        }
    }

    [Fact]
    public void HardwareFadeConfirmsBurstBoundariesAndLegacyKeepsImmediateReadback()
    {
        foreach (var captured in new[] { false, true }) foreach (var enabled in new[] { false, true })
        {
            var clock = new ManualTimeProvider(); var api = new Display(clock); var settings = new BrightnessFadeOptions(enabled, 3);
            void Wait(TimeSpan delay, CancellationToken token)
            { api.Calls.Add("wait:" + delay.TotalMilliseconds); clock.Advance(delay); token.ThrowIfCancellationRequested(); }
            if (captured)
            {
                var target = HardwareBrightnessOperation.CaptureTarget(api, 11, () => true, default, out _)!;
                BrightnessFade.Apply([(target, 95)], settings, default, clock, Wait);
                Assert.True(target.Transaction.Owned); Assert.Equal(95u, api.Current);
            }
            else Assert.True(HardwareBrightnessOperation.Apply(api, "primary", 95, default, settings, clock, Wait).Success);
            for (var i = 0; i < api.Calls.Count; i++)
            {
                if (api.Calls[i] != "write") continue;
                if (!enabled)
                {
                    var nextNative = api.Calls.Skip(i + 1).First(c => !c.StartsWith("wait:", StringComparison.Ordinal));
                    Assert.Equal("read", nextNative);
                    Assert.Equal("read", api.Calls.Take(i).Last(c => !c.StartsWith("wait:", StringComparison.Ordinal)));
                }
            }
            Assert.DoesNotContain("wait:500", api.Calls); Assert.DoesNotContain("wait:250", api.Calls);
            Assert.Equal("read", api.Calls[^1]);
            if (enabled) Assert.True(api.Calls.Count(c => c == "write") > api.Calls.Count(c => c == "read"));
            if (enabled) Assert.All(api.WriteTimes.Zip(api.WriteTimes.Skip(1)), pair =>
                Assert.True(pair.Second - pair.First >= TimeSpan.FromMilliseconds(100)));
            if (enabled) Assert.All(api.Calls.Where(c => c.StartsWith("wait:", StringComparison.Ordinal)), c =>
                Assert.InRange(double.Parse(c.AsSpan(5), System.Globalization.CultureInfo.CurrentCulture), double.Epsilon, 100));
            if (!enabled) Assert.DoesNotContain(api.Calls, c => c.StartsWith("wait:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void CancellationAfterAcceptedFadeWriteRelinquishesPendingAndDoesNotConfirmOrIssueAnotherWrite()
    {
        foreach (var captured in new[] { false, true })
        {
            var clock = new ManualTimeProvider(); var api = new Display(clock); using var stop = new CancellationTokenSource();
            api.AfterWrite = stop.Cancel;
            void Wait(TimeSpan delay, CancellationToken token)
            {
                api.Calls.Add("wait:" + delay.TotalMilliseconds); clock.Advance(delay);
                token.ThrowIfCancellationRequested();
            }
            if (captured)
            {
                var target = HardwareBrightnessOperation.CaptureTarget(api, 11, () => true, default, out _)!;
                Assert.ThrowsAny<OperationCanceledException>(() => BrightnessFade.Apply([(target, 95)], new(true, 3), stop.Token, clock, Wait));
                Assert.False(target.Transaction.Owned); Assert.True(target.Transaction.Release(default).Success);
                Assert.True(target.Transaction.UnconfirmedFailure); Assert.True(target.Transaction.HasPendingFadeWrite);
            }
            else Assert.ThrowsAny<OperationCanceledException>(() => HardwareBrightnessOperation.Apply(api, "primary", 95, stop.Token, new(true, 3), clock, Wait));
            Assert.Single(api.Writes); Assert.Equal("write", api.Calls[^1]);
            Assert.DoesNotContain(api.Calls.Skip(api.Calls.IndexOf("write") + 1), c => c == "read");
        }
    }

    [Fact]
    public void PendingFadeNeverCompletesReleasesOrAllowsOrdinaryApplyUsingAnOldConfirmedBaseline()
    {
        foreach (var operation in new[] { "release", "apply" })
        {
            uint current = 80; var reads = 0; var writes = new List<uint>();
            var transaction = new ConditionalSettingTransaction<uint>(80, () => { reads++; return current; }, value => { current = value; writes.Add(value); }, "synthetic");
            Assert.True(transaction.AdvanceFade(60, default));
            Assert.True(transaction.HasPendingFadeWrite); Assert.Equal(80u, transaction.Confirmed);
            Assert.False(transaction.TryCompleteConfirmedRelease());
            var receipt = operation == "release" ? transaction.Release(default) : transaction.Apply(70, default);
            Assert.False(receipt.Success); Assert.False(receipt.Owned); Assert.True(transaction.UnconfirmedFailure);
            Assert.Equal(1, reads); Assert.Equal(new uint[] { 60 }, writes); Assert.Equal(60u, current);
        }
    }

    [Fact]
    public void SlowNativeFadeConfirmsEachAcceptedWriteAndSkipsExpiredFramesWithoutArtificialDelay()
    {
        var clock = new ManualTimeProvider(); uint current = 50; var pending = false;
        var writes = new List<uint>(); var confirmations = new List<uint>(); var timestamps = new List<TimeSpan>();
        var target = new BrightnessRestoreTarget(50, p => (uint)p, () =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(40));
            if (pending) { confirmations.Add(current); pending = false; }
            return current;
        }, value =>
        {
            Assert.False(pending); pending = true; current = value;
            writes.Add(value); timestamps.Add(clock.GetElapsedTime(0));
            clock.Advance(TimeSpan.FromMilliseconds(50)); return true;
        });
        BrightnessFade.Apply([(target, 90)], new(true, 1, 60), default, clock, (delay, _) =>
        { Assert.True(delay > TimeSpan.Zero && delay <= TimeSpan.FromMilliseconds(16)); clock.Advance(delay); });
        Assert.True(target.Transaction.Owned); Assert.False(target.Transaction.HasPendingFadeWrite);
        Assert.False(pending); Assert.Equal(writes, confirmations); Assert.Equal(90u, target.Transaction.Confirmed);
        Assert.InRange(writes.Count, 2, 13); Assert.Equal(writes.Order(), writes); Assert.Equal(writes.Distinct(), writes);
        Assert.All(timestamps.Zip(timestamps.Skip(1)), pair => Assert.Equal(TimeSpan.FromMilliseconds(90), pair.Second - pair.First));
        Assert.True(clock.GetElapsedTime(0) <= TimeSpan.FromMilliseconds(1250));
    }

    [Fact]
    public void OrdinaryApplyAndReleaseKeepImmediateStrictReadbackAndPreserveExternalChanges()
    {
        foreach (var external in new[] { false, true })
        {
            uint current = 80; var reads = 0; var writes = new List<uint>();
            var transaction = new ConditionalSettingTransaction<uint>(80, () => { reads++; return current; }, value => { current = value; writes.Add(value); }, "synthetic");
            Assert.True(transaction.Apply(30, default).Owned); Assert.Equal(2, reads);
            if (external) current = 45;
            Assert.True(transaction.Release(default).Success);
            Assert.Equal(external ? 3 : 4, reads); Assert.Equal(external ? 45u : 80u, current);
            Assert.Equal(external ? new uint[] { 30 } : new uint[] { 30, 80 }, writes);
        }
    }

    [Fact]
    public void ZeroDistanceFadeConfirmsCurrentOwnershipWithoutWaitingOrWriting()
    {
        foreach (var externallyChanged in new[] { false, true })
        {
            var clock = new ManualTimeProvider(); uint current = externallyChanged ? 17u : 50u;
            var reads = 0; var writes = 0; var waits = 0;
            var target = new BrightnessRestoreTarget(50, p => (uint)p, () => { reads++; return current; },
                value => { writes++; current = value; return true; });
            BrightnessFade.Apply([(target, 50)], new(true, 15), default, clock, (_, _) => waits++);
            Assert.Equal(1, reads); Assert.Equal(0, writes); Assert.Equal(0, waits);
            Assert.Equal(TimeSpan.Zero, clock.GetElapsedTime(0));
            Assert.Equal(!externallyChanged, target.Transaction.Owned);
            Assert.Equal(externallyChanged ? 17u : 50u, current);
        }
    }

    [Fact]
    public void CoarseNativeMappingWritesEachValueOnceAndSlowCallsSkipFramesWithoutCatchup()
    {
        var clock = new ManualTimeProvider(); uint current = 0; var values = new List<uint>(); var timestamps = new List<TimeSpan>();
        var target = new BrightnessRestoreTarget(0, p => (uint)p, () => current, value =>
        { timestamps.Add(clock.GetElapsedTime(0)); values.Add(value); current = value; clock.Advance(TimeSpan.FromMilliseconds(600)); return true; });
        BrightnessFade.Apply([(target, 2)], new(true, 3), default, clock, (delay, _) => clock.Advance(delay));
        Assert.Equal(new uint[] { 1, 2 }, values);
        Assert.True(timestamps[1] - timestamps[0] >= TimeSpan.FromMilliseconds(600));
        Assert.Equal(2u, current); Assert.True(clock.GetElapsedTime(0) < TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MidTransitionExternalChangeOrCancellationStopsAllRemainingWrites(bool cancel)
    {
        var clock = new ManualTimeProvider(); var api = new Display(clock); using var stop = new CancellationTokenSource();
        void Wait(TimeSpan delay, CancellationToken token)
        {
            clock.Advance(delay);
            if (api.Writes.Count == 1) { if (cancel) stop.Cancel(); else api.Current = 17; }
            token.ThrowIfCancellationRequested();
        }
        if (cancel) Assert.ThrowsAny<OperationCanceledException>(() => BuiltInBrightnessOperation.Apply(api, 90, stop.Token, fade: new(true, 3), time: clock, wait: Wait));
        else Assert.False(BuiltInBrightnessOperation.Apply(api, 90, stop.Token, fade: new(true, 3), time: clock, wait: Wait).Success);
        Assert.Single(api.Writes); Assert.Equal(cancel ? api.Writes[0] : 17u, api.Current); Assert.Equal(0, api.Closes);
    }

    [Fact]
    public void UnconfirmedStepIsNeverRetriedOrRestored()
    {
        var clock = new ManualTimeProvider(); var api = new Display(clock) { RefuseReadback = true };
        Assert.False(BuiltInBrightnessOperation.Apply(api, 90, default, fade: new(true, 3), time: clock, wait: (delay, _) => clock.Advance(delay)).Success);
        Assert.Single(api.Writes); Assert.NotEqual(90u, api.Current);
    }

    [Fact(Timeout = 10000)]
    public async Task FadeReleaseCompletesAfterExactBaselineConfirmationWithoutExtraReadsAndRejectsUnconfirmedEndpoint()
    {
        foreach (var unconfirmed in new[] { false, true })
        {
            var clock = new ManualTimeProvider(); uint current = 100; var restoring = false; var baselineReads = 0;
            var writes = new List<uint>();
            var session = await BrightnessRestoreSession.CreateAsync(_ =>
            {
                var target = new BrightnessRestoreTarget(100, p => (uint)p, () =>
                {
                    if (restoring && current == 100)
                    {
                        baselineReads++;
                        if (baselineReads > 1) throw new IOException("No native read is allowed after endpoint confirmation.");
                        if (unconfirmed) return null;
                    }
                    return current;
                }, value => { current = value; writes.Add(value); return true; });
                return new([target], 0, 0, _ => { }, () => { });
            }, null, TimeSpan.FromSeconds(10), default, clock, (_, _) => clock.Advance(TimeSpan.FromSeconds(3)));
            try
            {
                Assert.True((await session.ApplyAsync(new Dictionary<string, string>
                { ["percent"] = "95", ["fadeEnabled"] = "true", ["fadeDurationSeconds"] = "3" }, default)).Owned);
                restoring = true;
                var released = await session.ReleaseAsync(default);
                Assert.Equal(!unconfirmed, released.Success); Assert.False(released.Owned);
                Assert.Equal(1, baselineReads); Assert.Equal(new uint[] { 95, 100 }, writes);
            }
            finally { await session.DisposeAsync(); }
            Assert.Equal(1, baselineReads); Assert.Equal(new uint[] { 95, 100 }, writes);
        }
    }

    [Fact(Timeout = 10000)]
    public async Task RestoreFadesBothDirectionsPreservesOriginalBaselineAcrossUpdatesAndManualTakeover()
    {
        foreach (var hardware in new[] { false, true }) foreach (var takeover in new[] { false, true })
        {
            var clock = new ManualTimeProvider(); var api = new Display(clock) { Current = 73 }; var captures = 0;
            var session = await BrightnessRestoreSession.CreateAsync(_ =>
            {
                captures++; var target = new BrightnessRestoreTarget(73, p => (uint)p, () => api.Current, value => api.Write(value), hardwareBurst: hardware);
                return new([target], 0, 0, _ => { }, () => { });
            }, null, TimeSpan.FromSeconds(10), default, clock, (delay, _) => clock.Advance(delay));
            try
            {
                Dictionary<string, string> Parameters(int percent) => new() { ["percent"] = percent.ToString(), ["fadeEnabled"] = "true", ["fadeDurationSeconds"] = "3" };
                Assert.True((await session.ApplyAsync(Parameters(40), default)).Owned);
                Assert.True(api.Writes.Count > 1); Assert.Equal(40u, api.Current);
                Assert.True((await session.ApplyAsync(Parameters(60), default)).Owned); Assert.Equal(1, captures);
                var applyCount = api.Writes.Count;
                if (takeover) api.Current = 19;
                var result = await session.ReleaseAsync(default); Assert.True(result.Success);
                Assert.Equal(takeover ? 19u : 73u, api.Current);
                if (takeover) Assert.Equal(applyCount, api.Writes.Count);
                else { Assert.True(api.Writes.Count > applyCount + 1); Assert.Equal(api.Writes.Skip(applyCount).Order(), api.Writes.Skip(applyCount)); }
            }
            finally { await session.DisposeAsync(); }
        }
    }

    [Fact]
    public async Task RepeatedHardwareFadesConfirmPriorSegmentAndRestoreOriginalBaselineEveryCycle()
    {
        var clock = new ManualTimeProvider(); var captures = 0;
        for (var cycle = 0; cycle < 8; cycle++)
        {
            var baseline = cycle % 2 == 0 ? 73u : 91u; var current = baseline; var end = 40u;
            var pending = false; var writes = new List<uint>();
            await using var session = await BrightnessRestoreSession.CreateAsync(_ =>
            {
                captures++;
                var target = new BrightnessRestoreTarget(baseline, p => (uint)p, () =>
                {
                    pending = false; clock.Advance(TimeSpan.FromMilliseconds(55)); return current;
                }, value =>
                {
                    writes.Add(value);
                    // After two cycles, model accepted intermediate writes being silently skipped
                    // until the controller reads back actual progress, as observed on hardware.
                    if (!(cycle >= 2 && pending)) current = value;
                    pending = true; clock.Advance(TimeSpan.FromMilliseconds(55)); return true;
                }, hardwareBurst: true);
                return new([target], 0, 0, _ => { }, () => { });
            }, null, TimeSpan.FromSeconds(10), default, clock, (delay, _) => clock.Advance(delay));
            var applied = await session.ApplyAsync(new Dictionary<string, string>
            {
                ["percent"] = "40", ["fadeEnabled"] = "true", ["fadeDurationSeconds"] = "1", ["fadeWritesPerSecond"] = "60"
            }, default);
            Assert.True(applied.Success, applied.Reason); Assert.Equal(40u, current);
            end = baseline;
            var restored = await session.ReleaseAsync(default);
            Assert.True(restored.Success, restored.Reason); Assert.Equal(baseline, current);
            Assert.Single(writes, value => value == 40); Assert.Single(writes, value => value == baseline);
            Assert.Equal(cycle + 1, captures);
        }
    }

    [Fact]
    public void HardwareCheckpointOnlyAcceptsThisSegmentsWrittenValuesAndNeverConfirmsItsEndpoint()
    {
        foreach (var actual in new uint[] { 60, 40, 73, 17 })
        {
            uint current = 73; var writes = new List<uint>(); var clock = new ManualTimeProvider();
            var transaction = new ConditionalSettingTransaction<uint>(73, () => current, value => writes.Add(value), "synthetic");
            Assert.True(transaction.CheckpointHardwareBurst(default));
            Assert.True(transaction.WriteHardwareBurst(60, default)); Assert.True(transaction.WriteHardwareBurst(40, default));
            current = actual;
            var accepted = transaction.CheckpointHardwareBurst(default);
            Assert.Equal(actual is 60 or 40, accepted);
            Assert.Equal(new uint[] { 60, 40 }, writes);
            if (accepted)
            {
                Assert.Equal(actual, transaction.Confirmed); Assert.False(transaction.HasPendingFadeWrite);
                Assert.False(transaction.TryCompleteConfirmedRelease());
                Assert.True(transaction.WriteHardwareBurst(30, default));
                // Progress is not success: a final write still requires its exact endpoint.
                Assert.False(transaction.ConfirmHardwareBurst(default, clock, (delay, _) => clock.Advance(delay)));
                Assert.False(transaction.Owned);
            }
            else Assert.False(transaction.Owned);
        }
        using var stop = new CancellationTokenSource(); stop.Cancel(); var read = false;
        var cancelled = new ConditionalSettingTransaction<uint>(73, () => { read = true; return 73; }, _ => { }, "synthetic");
        Assert.False(cancelled.CheckpointHardwareBurst(stop.Token)); Assert.False(read); Assert.False(cancelled.Owned);
        uint delayed = 73;
        var settled = new ConditionalSettingTransaction<uint>(73, () => delayed, _ => { }, "synthetic");
        Assert.True(settled.CheckpointHardwareBurst(default)); Assert.True(settled.WriteHardwareBurst(60, default));
        delayed = 60; Assert.True(settled.CheckpointHardwareBurst(default)); Assert.True(settled.WriteHardwareBurst(40, default));
        var waits = 0; var time = new ManualTimeProvider();
        Assert.True(settled.ConfirmHardwareBurst(default, time, (delay, _) => { waits++; time.Advance(delay); delayed = 40; }));
        Assert.Equal(1, waits); Assert.Equal(40u, settled.Confirmed);
    }

    [Fact]
    public void RequestedWriteFrequencyCapsBothBackendsAndHardwareEndpointRetains100msSpacing()
    {
        foreach (var hardware in new[] { false, true }) foreach (var frequency in new[] { 1, 10, 18, 60 })
        {
            var clock = new ManualTimeProvider(); uint current = 73;
            var writes = new List<(TimeSpan At, uint Value)>(); var calls = new List<string>();
            var target = new BrightnessRestoreTarget(73, p => (uint)p, () =>
            { calls.Add("read"); clock.Advance(TimeSpan.FromMilliseconds(15)); return current; }, value =>
            { calls.Add("write"); writes.Add((clock.GetElapsedTime(0), value)); current = value; clock.Advance(TimeSpan.FromMilliseconds(55)); return true; }, hardwareBurst: hardware);
            BrightnessFade.Apply([(target, 40)], new(true, 1, frequency), default, clock, (delay, token) => { clock.Advance(delay); token.ThrowIfCancellationRequested(); });
            Assert.True(target.Transaction.Owned); Assert.False(target.Transaction.HasPendingFadeWrite);
            Assert.Equal(40u, current); Assert.Equal(40u, target.Transaction.Confirmed); Assert.Equal("read", calls[^1]);
            Assert.Equal(writes.Select(w => w.Value).OrderDescending(), writes.Select(w => w.Value));
            Assert.Equal(writes.Select(w => w.Value).Distinct(), writes.Select(w => w.Value));
            Assert.All(writes.Zip(writes.Skip(1)), pair =>
                Assert.True(pair.Second.At - pair.First.At >= TimeSpan.FromMilliseconds(1000d / frequency) - TimeSpan.FromTicks(1)));
            if (hardware && writes.Count > 1) Assert.True(writes[^1].At - writes[^2].At >= TimeSpan.FromMilliseconds(100));
            if (!hardware) Assert.All(calls.Select((call, i) => (call, i)).Where(c => c.call == "write"), c => Assert.Equal("read", calls[c.i - 1]));
            Assert.InRange(clock.GetElapsedTime(0), TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(3));
        }
    }

    private static IEnumerable<TaskCapability> Descriptors() => [BuiltInBrightnessCapabilities.Descriptor, HardwareBrightnessCapabilities.Descriptor,
        BuiltInBrightnessAdjustCapabilities.Descriptor, HardwareBrightnessAdjustCapabilities.Descriptor,
        BuiltInBrightnessRestoreCapabilities.Descriptor, HardwareBrightnessRestoreCapabilities.Descriptor];
    private static void Validate(TaskInvocation invocation)
    {
        switch (invocation.CapabilityId)
        {
            case BuiltInBrightnessCapabilities.Set: BuiltInBrightnessCapabilities.Validate(invocation); break;
            case HardwareBrightnessCapabilities.Set: HardwareBrightnessCapabilities.Validate(invocation); break;
            case BuiltInBrightnessAdjustCapabilities.Adjust: BuiltInBrightnessAdjustCapabilities.Validate(invocation); break;
            case HardwareBrightnessAdjustCapabilities.Adjust: HardwareBrightnessAdjustCapabilities.Validate(invocation); break;
            case BuiltInBrightnessRestoreCapabilities.Set: BuiltInBrightnessRestoreCapabilities.Validate(invocation); break;
            case HardwareBrightnessRestoreCapabilities.Set: HardwareBrightnessRestoreCapabilities.Validate(invocation); break;
        }
    }
    private sealed class Display(ManualTimeProvider clock) : IBuiltInBrightnessApi, IHardwareBrightnessApi, IHardwareMonitorLease
    {
        internal uint Current = 50; internal bool RefuseReadback; internal int Discoveries, Acquisitions, Closes; internal Action? AfterWrite;
        internal readonly List<uint> Writes = []; internal readonly List<TimeSpan> WriteTimes = [];
        internal readonly List<string> Calls = [];
        public IReadOnlyList<BrightnessPanel> FindPanels() { Discoveries++; return [new("synthetic", BrightnessPanelSupport.Supported)]; }
        public bool Verify(string key) => true;
        public uint Set(string key, byte value) => Write(value) ? 0u : 1u;
        public int? Read(string key) { Calls.Add("read"); return (int)Current; }
        public bool IsAuthorized() => true;
        public IReadOnlyList<HardwareLogicalMonitor> ReadLogicalMonitors() => [new(1, true)];
        public IHardwareMonitorLease Acquire(nint logicalMonitor, int remainingCapacity) { Acquisitions++; return this; }
        public uint? Capabilities(nint monitor) => 2;
        public HardwareBrightnessRange? Read(nint monitor) { Calls.Add("read"); return new(0, Current, 100); }
        public bool Set(nint monitor, uint value) => Write(value);
        internal bool Write(uint value) { Calls.Add("write"); Writes.Add(value); WriteTimes.Add(clock.GetElapsedTime(0)); Current = RefuseReadback ? value + 1 : value; AfterWrite?.Invoke(); return true; }
        public IReadOnlyList<nint> Handles => [11];
        public void Dispose() => Closes++;
    }
}
