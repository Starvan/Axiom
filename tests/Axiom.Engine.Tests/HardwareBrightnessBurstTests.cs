using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class HardwareBrightnessBurstTests
{
    [Fact]
    public void HardwareBurstUsesNativeWriteCadenceAndConfirmsWithinOneSecondAndAtEndpoint()
    {
        var time = new ManualTimeProvider(); uint current = 0;
        var reads = new List<TimeSpan>(); var writes = new List<(TimeSpan At, uint Value)>();
        var target = new BrightnessRestoreTarget(0, p => (uint)p * 10, () =>
        {
            reads.Add(time.GetElapsedTime(0)); time.Advance(TimeSpan.FromMilliseconds(260)); return current;
        }, value =>
        {
            var now = time.GetElapsedTime(0);
            Assert.True(now - reads[^1] - TimeSpan.FromMilliseconds(260) < TimeSpan.FromSeconds(1)); // Deadline starts after ownership confirmation.
            writes.Add((now, value)); current = value; time.Advance(TimeSpan.FromMilliseconds(55)); return true;
        }, hardwareBurst: true);
        BrightnessFade.Apply([(target, 1000)], new(true, 3), default, time, (delay, _) =>
        { Assert.True(delay <= TimeSpan.FromMilliseconds(100)); time.Advance(delay); });
        Assert.True(target.Transaction.Owned); Assert.False(target.Transaction.HasPendingFadeWrite);
        Assert.Equal(1000u, target.Transaction.Confirmed); Assert.Equal(writes[^1].Value, current);
        Assert.True(reads[^1] > writes[^1].At); Assert.InRange(reads.Count, 3, 5);
        Assert.InRange(writes.Count, 16, 30); Assert.Equal(writes.Select(w => w.Value).Distinct(), writes.Select(w => w.Value));
        Assert.Equal(writes.Select(w => w.Value).Order(), writes.Select(w => w.Value));
        Assert.Contains(writes.Zip(writes.Skip(1)), pair => pair.Second.At - pair.First.At == TimeSpan.FromMilliseconds(100));
        Assert.All(writes.Zip(writes.Skip(1)), pair => Assert.True(pair.Second.At - pair.First.At >= TimeSpan.FromMilliseconds(100)));
        Assert.True(time.GetElapsedTime(0) < TimeSpan.FromMilliseconds(3500));
    }

    [Fact]
    public void HardwareCadenceSkipsExpiredFramesWithoutDelayingSlowWritesAndCancellationStopsPendingWrite()
    {
        foreach (var cancel in new[] { false, true })
        {
            var time = new ManualTimeProvider(); using var stop = new CancellationTokenSource();
            var writes = new List<(TimeSpan At, uint Value, TimeSpan VerifiedAt)>(); var waits = new List<TimeSpan>();
            uint current = 0; var reads = 0; var verifiedAt = TimeSpan.Zero;
            var target = new BrightnessRestoreTarget(0, p => (uint)p * 10, () =>
            {
                reads++; time.Advance(TimeSpan.FromMilliseconds(30)); verifiedAt = time.GetElapsedTime(0); return current;
            }, value =>
            {
                var now = time.GetElapsedTime(0);
                writes.Add((now, value, verifiedAt)); current = value;
                time.Advance(TimeSpan.FromMilliseconds(cancel ? 55 : 150)); return true;
            }, hardwareBurst: true);
            void Wait(TimeSpan delay, CancellationToken token)
            {
                waits.Add(delay); time.Advance(delay);
                if (cancel && writes.Count == 1 && delay > TimeSpan.FromMilliseconds(16)) stop.Cancel();
                token.ThrowIfCancellationRequested();
            }
            if (cancel)
            {
                Assert.ThrowsAny<OperationCanceledException>(() => BrightnessFade.Apply([(target, 1000)], new(true, 3), stop.Token, time, Wait));
                Assert.Single(writes); Assert.Equal(1, reads);
                Assert.False(target.Transaction.Owned); Assert.True(target.Transaction.HasPendingFadeWrite);
                Assert.True(target.Transaction.UnconfirmedFailure); Assert.False(target.Transaction.TryCompleteConfirmedRelease());
                target.Transaction.Release(default); Assert.Single(writes); Assert.Equal(1, reads);
            }
            else
            {
                BrightnessFade.Apply([(target, 1000)], new(true, 3), stop.Token, time, Wait);
                Assert.All(waits, delay => Assert.True(delay <= TimeSpan.FromMilliseconds(16)));
                Assert.Contains(writes.Zip(writes.Skip(1)), pair => pair.Second.At - pair.First.At == TimeSpan.FromMilliseconds(150));
                Assert.All(writes.Zip(writes.Skip(1)), pair => Assert.True(pair.Second.At - pair.First.At >= TimeSpan.FromMilliseconds(150)));
                Assert.True(target.Transaction.Owned); Assert.False(target.Transaction.HasPendingFadeWrite);
                Assert.Equal(1000u, target.Transaction.Confirmed); Assert.InRange(reads, 3, 5);
            }
            Assert.All(writes, write =>
            {
                Assert.True(write.At - write.VerifiedAt < TimeSpan.FromSeconds(1));
                var progress = Math.Clamp(write.At.TotalSeconds / 3, 0, 1);
                var eased = progress * progress * (3 - 2 * progress);
                Assert.Equal((uint)Math.Round(1000 * eased, MidpointRounding.AwayFromZero), write.Value);
            });
        }
    }

    [Fact]
    public void BurstTracksOnlyLatestPendingValueAndCannotCompleteRestoreOrApplyUsingOldBaseline()
    {
        foreach (var operation in new[] { "confirm", "release", "apply" })
        {
            uint current = 100; var reads = 0; var writes = new List<uint>();
            var transaction = new ConditionalSettingTransaction<uint>(100, () => { reads++; return current; }, value => { writes.Add(value); current = value; }, "synthetic");
            var time = new ManualTimeProvider();
            Assert.True(transaction.ConfirmHardwareBurst(default, time, (delay, _) => time.Advance(delay)));
            Assert.True(transaction.WriteHardwareBurst(90, default));
            Assert.True(transaction.WriteHardwareBurst(80, default));
            Assert.True(transaction.WriteHardwareBurst(80, default)); // Identical native values are never resent.
            Assert.Equal(1, reads); Assert.Equal(new uint[] { 90, 80 }, writes);
            Assert.Equal(100u, transaction.Confirmed); Assert.Equal(80u, transaction.FadeExpected);
            Assert.True(transaction.HasPendingFadeWrite); Assert.False(transaction.TryCompleteConfirmedRelease());
            if (operation == "confirm")
            {
                current = 85; // An unsent value cannot confirm or extend the latest pending write.
                Assert.False(transaction.ConfirmHardwareBurst(default, time, (delay, _) => time.Advance(delay))); Assert.Equal(2, reads);
            }
            else
            {
                Assert.False((operation == "release" ? transaction.Release(default) : transaction.Apply(70, default)).Success);
                Assert.Equal(1, reads);
            }
            Assert.False(transaction.Owned); Assert.True(transaction.UnconfirmedFailure);
            Assert.Equal(new uint[] { 90, 80 }, writes);
        }
    }

    [Fact]
    public void HardwareBurstStopsOnRejectedWriteCancellationUnknownOrBoundaryMismatchWithoutRetry()
    {
        foreach (var failure in new[] { "write", "cancel", "unknown", "external" })
        {
            var time = new ManualTimeProvider(); uint current = 1000; var reads = 0; var writes = new List<uint>();
            var calls = new List<string>(); using var stop = new CancellationTokenSource();
            var target = new BrightnessRestoreTarget(1000, p => (uint)p * 10, () =>
            {
                calls.Add("read"); reads++;
                if (reads > 1 && failure == "unknown") return null;
                if (reads > 1 && failure == "external") current = 1000;
                return current;
            }, value =>
            {
                calls.Add("write"); writes.Add(value);
                if (writes.Count == 2 && failure == "write") return false;
                current = value;
                if (writes.Count == 2 && failure == "cancel") stop.Cancel();
                return true;
            }, hardwareBurst: true);
            if (failure == "cancel")
                Assert.ThrowsAny<OperationCanceledException>(() => BrightnessFade.Apply([(target, 0)], new(true, 3), stop.Token, time, (delay, _) => time.Advance(delay)));
            else BrightnessFade.Apply([(target, 0)], new(true, 3), stop.Token, time, (delay, _) => time.Advance(delay));
            Assert.False(target.Transaction.Owned); Assert.True(target.Transaction.UnconfirmedFailure);
            var beforeRelease = writes.Count; target.Transaction.Release(default); Assert.Equal(beforeRelease, writes.Count);
            if (failure is "write" or "cancel") { Assert.Equal(2, writes.Count); Assert.Equal(1, reads); Assert.Equal("write", calls[^1]); }
            else { Assert.True(writes.Count > 2); Assert.Equal(2, reads); Assert.Equal("read", calls[^1]); }
            if (failure == "external") Assert.Equal(1000u, current);
        }
    }

    [Fact]
    public void LateFramesConfirmExpiredBurstBeforeAnyFurtherWriteAndAlwaysConfirmFinalValue()
    {
        var time = new ManualTimeProvider(); uint current = 0; var calls = new List<string>();
        var target = new BrightnessRestoreTarget(0, p => (uint)p * 10,
            () => { calls.Add("read:" + current); return current; }, value => { calls.Add("write:" + value); current = value; return true; }, hardwareBurst: true);
        BrightnessFade.Apply([(target, 1000)], new(true, 3), default, time, (_, _) => time.Advance(TimeSpan.FromMilliseconds(1500)));
        Assert.Equal(new[] { "read:0", "write:500", "read:500", "write:1000", "read:1000" }, calls);
        Assert.Equal(1000u, target.Transaction.Confirmed); Assert.False(target.Transaction.HasPendingFadeWrite);
    }

    [Fact]
    public void AcceptedBurstReadbackLagWaitsWithoutWritesAndRequiresExactEndpoint()
    {
        foreach (var ascending in new[] { false, true })
        {
            var time = new ManualTimeProvider(); var baseline = ascending ? 0u : 100u;
            var values = ascending ? new uint[] { 10, 20, 30 } : new uint[] { 90, 80, 70 };
            var readings = new Queue<uint>(); readings.Enqueue(baseline); var writes = new List<uint>();
            var transaction = new ConditionalSettingTransaction<uint>(baseline, () => readings.Dequeue(), value => writes.Add(value), "synthetic");
            Assert.True(transaction.ConfirmHardwareBurst(default, time, (_, _) => Assert.Fail("Initial confirmation must not wait.")));
            foreach (var value in values) Assert.True(transaction.WriteHardwareBurst(value, default));
            foreach (var value in new[] { values[0], values[0], values[1], values[2] }) readings.Enqueue(value);
            var waits = 0;
            Assert.True(transaction.ConfirmHardwareBurst(default, time, (delay, _) =>
            {
                Assert.Equal(TimeSpan.FromMilliseconds(50), delay); waits++;
                Assert.Equal(baseline, transaction.Confirmed); Assert.True(transaction.HasPendingFadeWrite);
                Assert.False(transaction.TryCompleteConfirmedRelease()); Assert.Equal(values, writes);
                time.Advance(delay);
            }));
            Assert.Equal(3, waits); Assert.Equal(values, writes); Assert.Empty(readings);
            Assert.True(transaction.Owned); Assert.False(transaction.HasPendingFadeWrite);
            Assert.Equal(values[^1], transaction.Confirmed);
        }
    }

    [Fact]
    public void BurstReadbackLagRejectsUnknownUnsentBackwardCancellationAndDeadline()
    {
        foreach (var failure in new[] { "unknown", "unsent", "backward", "cancel", "deadline" })
        {
            var time = new ManualTimeProvider(); using var stop = new CancellationTokenSource();
            var reads = 0; var writes = new List<uint>(); var waits = 0;
            var transaction = new ConditionalSettingTransaction<uint>(100, () =>
            {
                if (++reads == 1) return 100;
                Assert.True(time.GetElapsedTime(0) < TimeSpan.FromSeconds(1));
                if (failure == "unknown") throw new IOException("Unknown native read.");
                time.Advance(TimeSpan.FromMilliseconds(17));
                return failure switch { "unsent" => 85, "backward" when reads == 2 => 80, _ => 90u };
            }, value => writes.Add(value), "synthetic");
            Assert.True(transaction.ConfirmHardwareBurst(stop.Token, time, (_, _) => Assert.Fail("Initial confirmation must not wait.")));
            foreach (var value in new uint[] { 90, 80, 70 }) Assert.True(transaction.WriteHardwareBurst(value, stop.Token));
            Assert.False(transaction.ConfirmHardwareBurst(stop.Token, time, (delay, _) =>
            {
                waits++; Assert.True(delay <= TimeSpan.FromMilliseconds(50));
                Assert.Equal(100u, transaction.Confirmed); Assert.True(transaction.HasPendingFadeWrite);
                Assert.False(transaction.TryCompleteConfirmedRelease()); Assert.Equal(new uint[] { 90, 80, 70 }, writes);
                time.Advance(delay); if (failure == "cancel") stop.Cancel();
            }));
            Assert.False(transaction.Owned); Assert.True(transaction.UnconfirmedFailure);
            Assert.Equal(100u, transaction.Confirmed); Assert.True(transaction.HasPendingFadeWrite);
            Assert.False(transaction.TryCompleteConfirmedRelease()); transaction.Release(default);
            Assert.False(transaction.Apply(100, default).Success); Assert.Equal(new uint[] { 90, 80, 70 }, writes);
            if (failure is "unknown" or "unsent") Assert.Equal(0, waits);
            if (failure == "backward") { Assert.Equal(1, waits); Assert.Equal(3, reads); }
            if (failure == "cancel") { Assert.Equal(1, waits); Assert.Equal(2, reads); }
            if (failure == "deadline") { Assert.Equal(TimeSpan.FromSeconds(1), time.GetElapsedTime(0)); Assert.InRange(reads, 2, 21); }
        }
    }

    [Fact]
    public void LaggingBurstConfirmsBeforeNextSegmentAndRestoresOriginalBaseline()
    {
        var time = new ManualTimeProvider(); uint current = 100; var accepted = new List<uint>();
        var stale = false; var awaitingExact = false; var confirmations = 0; var writes = 0;
        var target = new BrightnessRestoreTarget(100, p => (uint)p, () =>
        {
            time.Advance(TimeSpan.FromMilliseconds(60));
            if (accepted.Count > 1 && !stale)
            { stale = true; awaitingExact = true; return accepted[^2]; }
            if (accepted.Count > 0) { confirmations++; accepted.Clear(); stale = false; }
            awaitingExact = false; return current;
        }, value =>
        {
            Assert.False(awaitingExact); writes++; accepted.Add(value); current = value;
            time.Advance(TimeSpan.FromMilliseconds(55)); return true;
        }, hardwareBurst: true);
        void Wait(TimeSpan delay, CancellationToken token)
        {
            Assert.True(delay <= TimeSpan.FromMilliseconds(50));
            if (awaitingExact) { Assert.True(target.Transaction.HasPendingFadeWrite); Assert.False(target.Transaction.TryCompleteConfirmedRelease()); }
            time.Advance(delay);
        }
        BrightnessFade.Apply([(target, 40)], new(true, 3), default, time, Wait);
        Assert.True(target.Transaction.Owned); Assert.Equal(40u, target.Transaction.Confirmed);
        var applyConfirmations = confirmations; var applyWrites = writes;
        Assert.InRange(applyConfirmations, 2, 4); Assert.True(applyWrites > 10);
        BrightnessFade.Apply([(target, target.Baseline)], new(true, 3), default, time, Wait);
        Assert.Equal(100u, current); Assert.Equal(100u, target.Transaction.Confirmed);
        Assert.True(confirmations > applyConfirmations); Assert.True(writes > applyWrites);
        Assert.True(target.Transaction.TryCompleteConfirmedRelease()); Assert.False(target.Transaction.Owned);
        Assert.False(target.Transaction.HasPendingFadeWrite); Assert.False(target.Transaction.UnconfirmedFailure);
    }
}
