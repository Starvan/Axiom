using System.Collections.Immutable;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.ServiceHost;

namespace Axiom.Engine.Tests;

[Collection("Brightness native ownership")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class BrightnessWheelTests
{
    [Fact]
    public async Task ToolAndProviderEnvelopesRejectUnrelatedFieldsAndValidateChordsAndBounds()
    {
        var now = DateTimeOffset.UtcNow; var settings = new BrightnessWheelSettings(Enabled: true);
        var save = new AutomationMessage(1, "save-brightness-wheel", "tool", now.AddSeconds(10), WheelSettings: settings);
        foreach (var modifiers in new[] { ShortcutModifiers.Control | ShortcutModifiers.Alt, ShortcutModifiers.Control | ShortcutModifiers.Shift, ShortcutModifiers.Windows })
        foreach (var key in new[] { "", "B" }) BrightnessWheelTool.Validate(settings with { Modifiers = modifiers, Key = key });
        Assert.Equal("Ctrl + Shift + B + 滚轮", BrightnessWheelTool.Describe(settings with { Modifiers = ShortcutModifiers.Control | ShortcutModifiers.Shift, Key = "B" }));
        AutomationProtocol.ValidateRequest(save, now);
        AutomationProtocol.ValidateRequest(save with { Type = "read-brightness-wheel", WheelSettings = null }, now);
        AutomationProtocol.ValidateRequest(save with { Type = "resume-brightness-wheel", WheelSettings = null, ExpectedVersion = 1 }, now);
        foreach (var invalid in new[] { settings with { Step = 0 }, settings with { Step = 21 }, settings with { SmoothMilliseconds = 49 }, settings with { SmoothMilliseconds = 1001 },
            settings with { WritesPerSecond = 0 }, settings with { WritesPerSecond = 61 }, settings with { Modifiers = ShortcutModifiers.None }, settings with { Key = "Unknown" }, settings with { Target = "device-id" } })
            Assert.Throws<ArgumentException>(() => BrightnessWheelTool.Validate(invalid));
        foreach (var invalid in new[] { save with { Type = "read", WheelSettings = settings }, save with { ExpectedVersion = 1 }, save with { Bindings = [] },
            save with { TraySettings = new(0, false, "startup", []) }, save with { Wheel = new(settings, "forged") }, save with { StartupEnabled = true },
            save with { Type = "resume-brightness-wheel", WheelSettings = null }, save with { Type = "save-brightness-wheel", WheelSettings = null } })
            Assert.ThrowsAny<Exception>(() => AutomationProtocol.ValidateRequest(invalid, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(save, now, shortcutAgent: true));
        var observe = new ProviderMessage(1, "observe-brightness-wheel", Guid.NewGuid().ToString("N"), now.AddSeconds(10), WheelSettings: settings);
        ProviderProtocol.Validate(observe, now);
        foreach (var invalid in new[] { observe with { CommandId = Guid.NewGuid() }, observe with { WheelSteps = 1 }, observe with { Invocation = BrightnessWheelTool.Gesture(Guid.NewGuid().ToString("N"), 0) },
            observe with { Type = "invoke" }, observe with { WheelSettings = settings with { Enabled = false } }, observe with { Resources = [] } })
            Assert.ThrowsAny<Exception>(() => ProviderProtocol.Validate(invalid, now));
        foreach (var type in new[] { "abandon-brightness", "resume-brightness" })
        {
            var message = observe with { Type = type, WheelSettings = null }; ProviderProtocol.Validate(message, now);
            Assert.Throws<InvalidDataException>(() => ProviderProtocol.Validate(message with { CommandId = Guid.NewGuid() }, now));
        }
        var gesture = BrightnessWheelTool.Gesture(Guid.NewGuid().ToString("N"), -7);
        Assert.Equal(-7, BrightnessWheelTool.ValidateGesture(gesture).FromSteps);
        Assert.Throws<ArgumentException>(() => BrightnessWheelTool.ValidateGesture(gesture with { Parameters = gesture.Parameters.Add("percent", "100") }));
        using var wire = new MemoryStream(); await AutomationProtocol.WriteAsync(wire, save, default); wire.Position = 0;
        Assert.Equal(settings, (await AutomationProtocol.ReadAsync(wire, default)).WheelSettings);
        var highResolution = new BrightnessWheelDelta();
        Assert.Equal(0, highResolution.Add(30)); Assert.Equal(0, highResolution.Add(60)); Assert.Equal(1, highResolution.Add(30));
        Assert.Equal(0, highResolution.Add(-60)); Assert.Equal(-2, highResolution.Add(-180));
        highResolution.Add(30); highResolution.Reset(); Assert.Equal(0, highResolution.Add(90));
    }

    [Fact(Timeout = 10000)]
    public async Task ExplicitHandoffCancelsPendingFadeWithoutRestoringAndRetainsAdmissionUntilNativeCleanup()
    {
        uint current = 73; var writes = new List<uint>(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finish = new ManualResetEventSlim(); var closed = false; var ownerThread = 0;
        var session = await BrightnessRestoreSession.CreateAsync(_ =>
        {
            ownerThread = Environment.CurrentManagedThreadId;
            return new([new(73, p => (uint)p, () => current, value =>
            { current = value; writes.Add(value); entered.TrySetResult(); finish.Wait(); return true; }, hardwareBurst: true)], 0, 0, _ => { },
                () => { Assert.Equal(ownerThread, Environment.CurrentManagedThreadId); closed = true; });
        }, null, TimeSpan.FromSeconds(5), default);
        try
        {
            var apply = session.ApplyAsync(new Dictionary<string, string> { ["percent"] = "40" }, default);
            await entered.Task;
            var drained = BrightnessNativeOwnership.BeginManual();
            Assert.False(drained.IsCompleted); Assert.Null(BrightnessNativeOwnership.TryAcquire()); Assert.Null(BrightnessNativeOwnership.TryAcquire(manualGesture: true));
            finish.Set(); await apply; await drained;
            Assert.True(closed); Assert.Equal(new uint[] { 40 }, writes); Assert.Equal(40u, current);
            Assert.True((await session.ReleaseAsync(default)).Success); Assert.Equal(new uint[] { 40 }, writes);
            Assert.Null(BrightnessNativeOwnership.TryAcquire());
            using var manual = BrightnessNativeOwnership.TryAcquire(manualGesture: true); Assert.NotNull(manual);
            Assert.Throws<InvalidOperationException>(() => BrightnessNativeOwnership.EndManual());
        }
        finally { finish.Set(); await session.DisposeAsync(); BrightnessNativeOwnership.EndManual(); }
        using var ordinary = BrightnessNativeOwnership.TryAcquire(); Assert.NotNull(ordinary);
    }

    [Fact]
    public void EndpointConfirmsPriorSegmentBeforeSetAndRechecksInputAfterSlowRead()
    {
        foreach (var retarget in new[] { false, true })
        {
            var time = new ManualTimeProvider(); uint current = 97; long latest = -2;
            var unconfirmed = false; var reads = 0; var writes = new List<uint>();
            var target = new BrightnessRestoreTarget(97, p => (uint)p, () =>
            {
                reads++; unconfirmed = false;
                if (reads == 2)
                {
                    time.Advance(TimeSpan.FromMilliseconds(250));
                    if (retarget) latest = 2;
                }
                return current;
            }, value =>
            {
                if (value is 93 or 100) Assert.False(unconfirmed);
                writes.Add(value); current = value; unconfirmed = true;
                time.Advance(TimeSpan.FromMilliseconds(55)); return true;
            }, hardwareBurst: true);
            using var capture = new BrightnessRestoreCapture([target], 0, 0, _ => { }, () => { });
            var result = WindowsBrightnessWheelGesture.RunCaptured(capture, 2, 150, 18, 0, () => latest, default, time,
                (delay, _) => time.Advance(delay));
            Assert.True(result.Success); Assert.Equal(retarget ? 100u : 93u, current);
            Assert.Equal(latest, result.ConsumedSteps); Assert.False(target.Transaction.HasPendingFadeWrite);
            if (retarget) Assert.DoesNotContain(93u, writes);
            Assert.Single(writes, value => value == current);
            Assert.True(reads >= 3);
        }
    }

    [Fact]
    public void ContinuousInputRetargetsOneCaptureClampsBoundsAndConfirmsLatestEndpoint()
    {
        var time = new ManualTimeProvider(); uint current = 73; long latest = -1;
        var values = new List<uint>(); var at = new List<TimeSpan>(); var reads = 0;
        var target = new BrightnessRestoreTarget(73, p => (uint)p, () => { reads++; return current; }, value =>
        { values.Add(value); at.Add(time.GetElapsedTime(0)); current = value; time.Advance(TimeSpan.FromMilliseconds(55)); return true; }, hardwareBurst: true);
        using var capture = new BrightnessRestoreCapture([target], 0, 0, _ => { }, () => { });
        var result = WindowsBrightnessWheelGesture.RunCaptured(capture, 2, 150, 18, 0, () => latest, default, time, (delay, _) =>
        {
            time.Advance(delay);
            if (time.GetElapsedTime(0) >= TimeSpan.FromMilliseconds(100)) latest = -5;
        });
        Assert.True(result.Success); Assert.Equal(-5, result.ConsumedSteps); Assert.Equal(63u, current); Assert.Equal(63u, target.Transaction.Confirmed);
        Assert.False(target.Transaction.HasPendingFadeWrite); Assert.Equal(values.Distinct(), values); Assert.Equal(values.OrderDescending(), values);
        Assert.True(at[^1] - at[^2] >= TimeSpan.FromMilliseconds(100)); Assert.InRange(reads, 2, 3);
        Assert.True(time.GetElapsedTime(0) < TimeSpan.FromSeconds(1));
        foreach (var steps in new[] { -100L, 100L })
        {
            current = 73; var bounded = new BrightnessRestoreTarget(73, p => (uint)p, () => current, value => { current = value; return true; }, hardwareBurst: true);
            using var bounds = new BrightnessRestoreCapture([bounded], 0, 0, _ => { }, () => { });
            var boundedResult = WindowsBrightnessWheelGesture.RunCaptured(bounds, 20, 50, 60, 0, () => steps, default, time, (delay, _) => time.Advance(delay));
            Assert.True(boundedResult.Success); Assert.Equal(steps < 0 ? 0u : 100u, current);
        }
    }

    [Fact]
    public void ReversalWaits100msBeforeStrictFenceAndUsesLatestInputWithoutRestoringBaseline()
    {
        var time = new ManualTimeProvider(); uint current = 73; long latest = -10;
        var writes = new List<(TimeSpan At, uint Value)>(); var reads = new List<(TimeSpan At, uint Value)>(); var flipped = false;
        var target = new BrightnessRestoreTarget(73, p => (uint)p, () =>
        {
            var value = writes.Count == 1 && time.GetElapsedTime(0) - writes[0].At < TimeSpan.FromMilliseconds(100) ? 73u : current;
            reads.Add((time.GetElapsedTime(0), value)); return value;
        }, value => { writes.Add((time.GetElapsedTime(0), value)); current = value; time.Advance(TimeSpan.FromMilliseconds(55)); return true; }, hardwareBurst: true);
        using var capture = new BrightnessRestoreCapture([target], 0, 0, _ => { }, () => { });
        var result = WindowsBrightnessWheelGesture.RunCaptured(capture, 2, 150, 18, 0, () => latest, default, time, (delay, _) =>
        {
            time.Advance(delay);
            if (writes.Count == 1 && !flipped) { latest = 5; flipped = true; }
        });
        Assert.True(result.Success); Assert.Equal(5, result.ConsumedSteps); Assert.Equal(83u, current);
        Assert.True(reads[1].At - writes[0].At >= TimeSpan.FromMilliseconds(100)); Assert.Equal(writes[0].Value, reads[1].Value);
        Assert.True(reads[1].At < writes[1].At); Assert.Equal(73u, target.Baseline);
        Assert.False(target.Transaction.HasPendingFadeWrite); Assert.False(target.Transaction.UnconfirmedFailure);
    }

    [Fact]
    public void EndpointFenceRechecksEarlyWakeAndFreezesCandidateWhenClockAdvancesBeforeWrite()
    {
        foreach (var preempt in new[] { false, true }) foreach (var cancel in new[] { false, true })
        {
            var time = new ManualTimeProvider(); using var stop = new CancellationTokenSource(); uint current = 96;
            var reads = 0; var waits = 0; var observationsAfterFirstWrite = 0; var preempted = false;
            var writes = new List<(TimeSpan At, uint Value)>();
            var target = new BrightnessRestoreTarget(96, p => (uint)p, () =>
            {
                if (++reads == 1) time.Advance(TimeSpan.FromMilliseconds(preempt ? 22 : 55.4));
                return current;
            }, value =>
            {
                var at = time.GetElapsedTime(0);
                // Model the observed device: Set accepts every value, but a final write spaced
                // below 100ms can leave the previous accepted brightness physically unchanged.
                if (value != 100 || writes.Count == 0 || at - writes[^1].At >= TimeSpan.FromMilliseconds(100)) current = value;
                writes.Add((at, value)); time.Advance(TimeSpan.FromMilliseconds(55)); return true;
            }, hardwareBurst: true);
            using var capture = new BrightnessRestoreCapture([target], 0, 0, _ => { }, () => { });
            long Latest()
            {
                if (preempt && writes.Count == 1 && ++observationsAfterFirstWrite == 3)
                { preempted = true; time.Advance(TimeSpan.FromMilliseconds(20)); }
                return 2;
            }
            void Wait(TimeSpan delay, CancellationToken token)
            {
                waits++;
                // Task.Delay can return just before a fractional-millisecond deadline.
                time.Advance(delay - TimeSpan.FromMilliseconds(0.4));
                if (cancel && writes.Count > 0 && delay > TimeSpan.FromMilliseconds(16)) stop.Cancel();
                token.ThrowIfCancellationRequested();
            }
            if (cancel)
            {
                Assert.ThrowsAny<OperationCanceledException>(() => WindowsBrightnessWheelGesture.RunCaptured(capture, 2, 150, 18, 0, Latest, stop.Token, time, Wait));
                Assert.DoesNotContain(writes, write => write.Value == 100); Assert.True(target.Transaction.HasPendingFadeWrite);
                target.Transaction.Relinquish(); Assert.False(target.Transaction.TryCompleteConfirmedRelease());
            }
            else
            {
                var result = WindowsBrightnessWheelGesture.RunCaptured(capture, 2, 150, 18, 0, Latest, default, time, Wait);
                Assert.True(result.Success); Assert.Equal(100u, current); Assert.Equal(100u, target.Transaction.Confirmed);
                Assert.False(target.Transaction.HasPendingFadeWrite); Assert.Single(writes, write => write.Value == 100);
                Assert.True(writes[^1].At - writes[^2].At >= TimeSpan.FromMilliseconds(100));
                if (preempt) { Assert.True(preempted); Assert.Equal(99u, writes[1].Value); }
            }
            Assert.InRange(waits, 1, 30); Assert.True(time.GetElapsedTime(0) < TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void GestureUnknownWriteFailureOrCancellationStopsWithoutRetryOrBaselineRestoration()
    {
        foreach (var failure in new[] { "read", "write", "cancel" })
        {
            var time = new ManualTimeProvider(); using var stop = new CancellationTokenSource(); uint current = 73; var writes = new List<uint>(); var reads = 0;
            var target = new BrightnessRestoreTarget(73, p => (uint)p, () => ++reads > 1 && failure == "read" ? null : current, value =>
            { writes.Add(value); current = value; if (failure == "cancel") stop.Cancel(); return failure != "write"; }, hardwareBurst: true);
            using var capture = new BrightnessRestoreCapture([target], 0, 0, _ => { }, () => { });
            if (failure == "cancel")
            {
                BrightnessWheelGestureResult? cancelled = null;
                var error = Record.Exception(() => cancelled = WindowsBrightnessWheelGesture.RunCaptured(capture, 2, 50, 18, 0, () => -1, stop.Token, time,
                    (delay, token) => { time.Advance(delay); token.ThrowIfCancellationRequested(); }));
                if (error is null) Assert.False(cancelled!.Success); else Assert.IsAssignableFrom<OperationCanceledException>(error);
            }
            else Assert.False(WindowsBrightnessWheelGesture.RunCaptured(capture, 2, 50, 18, 0, () => -1, default, time, (delay, _) => time.Advance(delay)).Success);
            var before = writes.Count; target.Transaction.Relinquish(); target.Transaction.Release(default);
            Assert.Equal(before, writes.Count); Assert.DoesNotContain(73u, writes); Assert.False(target.Transaction.TryCompleteConfirmedRelease());
            if (failure is "cancel" or "write") Assert.Single(writes);
        }
    }

    [Fact(Timeout = 15000)]
    public async Task HostWaitsForMarkedAndCompleteBeforeEngineGestureAndDisableEndsTheSubscription()
    {
        var path = Path.Combine(Path.GetTempPath(), "axiom-wheel-pipe-synthetic-" + Guid.NewGuid().ToString("N"), "tasks.db");
        var sid = "wheel-test-" + Guid.NewGuid().ToString("N");
        var owner = WindowsIdentity.GetCurrent().User!.Value;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wheel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var marked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clients = new List<Task>();
        async Task Handle(NamedPipeServerStream pipe)
        {
            using (pipe)
            {
                var request = await ProviderProtocol.ReadAsync(pipe, limit.Token);
                ProviderProtocol.Validate(request, DateTimeOffset.UtcNow);
                if (request.Type == "observe-brightness-wheel")
                {
                    await ProviderProtocol.WriteAsync(pipe, new(1, "brightness-wheel-state", request.RequestId, Sequence: 0, Status: OutcomeStatus.Succeeded, WheelSteps: 0), limit.Token);
                    ready.TrySetResult(); await wheel.Task.WaitAsync(limit.Token);
                    await ProviderProtocol.WriteAsync(pipe, new(1, "brightness-wheel-state", request.RequestId, Sequence: 1, Status: OutcomeStatus.Succeeded, WheelSteps: -2), limit.Token);
                    try { await ProviderProtocol.ReadAsync(pipe, limit.Token); } catch (IOException) { disconnected.TrySetResult(); }
                }
                else if (request.Type == "abandon-brightness")
                {
                    await ProviderProtocol.WriteAsync(pipe, new(1, "brightness-abandon-marked", request.RequestId, Status: OutcomeStatus.Succeeded), limit.Token);
                    marked.TrySetResult(); await complete.Task.WaitAsync(limit.Token);
                    await ProviderProtocol.WriteAsync(pipe, new(1, "brightness-abandon-complete", request.RequestId, Status: OutcomeStatus.Succeeded), limit.Token);
                }
                else
                {
                    Assert.True(complete.Task.IsCompletedSuccessfully); Assert.Equal("invoke", request.Type);
                    Assert.Equal(BrightnessWheelTool.Command, request.Invocation!.CapabilityId); Assert.NotNull(request.CommandId);
                    Assert.Equal(0, BrightnessWheelTool.ValidateGesture(request.Invocation).FromSteps);
                    await ProviderProtocol.WriteAsync(pipe, new(1, "result", request.RequestId, CommandId: request.CommandId,
                        Status: OutcomeStatus.Succeeded, Reason: "synthetic strict endpoint confirmed", WheelSteps: -2), limit.Token);
                    invoked.TrySetResult();
                }
            }
        }
        async Task Accept()
        {
            for (var i = 0; i < 3; i++)
            {
                var pipe = LocalPipeFactory.Create(ProviderProtocol.PipeName(sid), owner);
                await pipe.WaitForConnectionAsync(limit.Token); clients.Add(Handle(pipe));
            }
        }
        var server = Accept();
        try
        {
            using var activity = new AutomationFixture(); using var store = new SqliteAutomationTaskStore(path);
            await using var runtime = new PluginTaskRuntime([new BuiltInBrightnessPlugin(), new HardwareBrightnessPlugin()], store, sid, activity.Engine);
            await runtime.InitializeAsync(); await runtime.SaveBrightnessWheelAsync(new(Enabled: true), limit.Token);
            await ready.Task.WaitAsync(limit.Token); wheel.TrySetResult(); await marked.Task.WaitAsync(limit.Token);
            Assert.False(invoked.Task.IsCompleted); Assert.True(runtime.ReadBrightnessWheel().ManualTakenOver);
            complete.TrySetResult(); await invoked.Task.WaitAsync(limit.Token);
            while (runtime.ReadBrightnessWheel().Running) await Task.Delay(5, limit.Token);
            var live = runtime.ReadBrightnessWheel(); Assert.Contains("synthetic strict", live.LastResult);
            var disabled = await runtime.SaveBrightnessWheelAsync(live.Settings with { Enabled = false }, limit.Token);
            Assert.False(disabled.Listening); Assert.True(disabled.ManualTakenOver); Assert.False(disabled.Settings.Enabled);
            await disconnected.Task.WaitAsync(limit.Token); await server; await Task.WhenAll(clients);
            Assert.Empty((await runtime.ReadAsync(default)).Tasks);
            await runtime.EnsureSafeToStopAsync(limit.Token);
        }
        finally
        {
            limit.Cancel();
            if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact(Timeout = 10000)]
    public async Task SettingsAndManualTakeoverPersistAsHiddenInternalTaskAndCannotBeChangedAsAnOrdinaryRule()
    {
        var path = Path.Combine(Path.GetTempPath(), "axiom-wheel-synthetic-" + Guid.NewGuid().ToString("N"), "tasks.db");
        try
        {
            using var activity = new AutomationFixture();
            using (var store = new SqliteAutomationTaskStore(path))
            {
                await using (var runtime = new PluginTaskRuntime([new BuiltInBrightnessPlugin(), new HardwareBrightnessPlugin()], store, "synthetic", activity.Engine))
                {
                    await runtime.InitializeAsync();
                    var saved = await runtime.SaveBrightnessWheelAsync(new(Key: "B", Modifiers: ShortcutModifiers.Control | ShortcutModifiers.Shift, SmoothMilliseconds: 200, WritesPerSecond: 60), default);
                    Assert.Equal(1, saved.Settings.Version); Assert.False(saved.Listening); Assert.False(saved.ManualTakenOver);
                    Assert.Empty((await runtime.ReadAsync(default)).Tasks);
                    var task = Assert.Single(await store.ListAsync());
                    await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SaveAsync(task, default));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.DeleteAsync(task.Id, task.Version, default));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SaveBrightnessWheelAsync(new(), default));
                }
                var persisted = Assert.Single(await store.ListAsync());
                await store.SaveAsync(persisted with { Detection = persisted.Detection with { Parameters = persisted.Detection.Parameters.SetItem("manual", "true") } }, default);
            }
            using (var store = new SqliteAutomationTaskStore(path))
            await using (var runtime = new PluginTaskRuntime([new PowerStatePlugin(), new BuiltInBrightnessPlugin(), new HardwareBrightnessPlugin()], store, "synthetic", activity.Engine))
            {
                await runtime.InitializeAsync(); var restored = runtime.ReadBrightnessWheel();
                Assert.True(restored.ManualTakenOver); Assert.Equal(60, restored.Settings.WritesPerSecond); Assert.Equal("B", restored.Settings.Key);
                Assert.Empty((await runtime.ReadAsync(default)).Tasks);
                var key = new ResourceKey(HardwareBrightnessRestoreCapabilities.Set, "brightness", ResourceScope.Session("synthetic", System.Diagnostics.Process.GetCurrentProcess().SessionId));
                Assert.True(runtime.Authorize("synthetic", key, CapabilityOperation.ApplyDesired).Allowed);
                Assert.True(runtime.Authorize("synthetic", key, CapabilityOperation.ReleaseOverride).Allowed);
                Assert.False(runtime.Authorize("other", key, CapabilityOperation.Override).Allowed);
                var rule = new AutomationTask(Guid.NewGuid().ToString("D"), 0, "synthetic brightness during manual takeover", false,
                    new(PowerStateCapabilities.State, 1, ImmutableDictionary<string, string>.Empty.Add("mode", "ac").Add("threshold", "20")),
                    new(HardwareBrightnessRestoreCapabilities.Set, 1, ImmutableDictionary<string, string>.Empty.Add("target", "primary").Add("percent", "40").Add("delaySeconds", "0")
                        .Add("fadeEnabled", "true").Add("fadeDurationSeconds", "1").Add("fadeWritesPerSecond", "10")));
                var first = await runtime.SaveAsync(rule, default);
                var updated = await runtime.SaveAsync(first with { Action = first.Action with { Parameters = first.Action.Parameters.SetItem("fadeWritesPerSecond", "18") } }, default);
                Assert.Equal(2, updated.Version);
                var visible = Assert.Single((await runtime.ReadAsync(default)).Tasks);
                var persistedRule = Assert.Single(await store.ListAsync(), t => t.Id == rule.Id);
                Assert.Equal(updated.Version, visible.Version); Assert.Equal(visible.Version, persistedRule.Version);
                Assert.Equal("18", visible.Action.Parameters["fadeWritesPerSecond"]); Assert.Equal("18", persistedRule.Action.Parameters["fadeWritesPerSecond"]);
                Assert.True(runtime.ReadBrightnessWheel().ManualTakenOver);
                var blocked = await runtime.ApplyDesiredAsync(new(key, StateValue.From(RestorableSettingInvocations.Schema, RestorableSettingInvocations.Encode(updated.Action)), "synthetic", 0, 1, "synthetic condition ends"), default);
                Assert.Equal(OutcomeStatus.Rejected, blocked.Status); Assert.Contains("手动接管", blocked.Reason);
                await runtime.EnsureSafeToStopAsync(default);
            }
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
}
