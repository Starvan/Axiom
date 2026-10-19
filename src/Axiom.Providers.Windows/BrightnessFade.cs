
namespace Axiom.Providers.Windows;

public readonly record struct BrightnessFadeOptions(bool Enabled = false, int DurationSeconds = 3, int WritesPerSecond = 10)
{
    internal TimeSpan OperationTimeout => TimeSpan.FromSeconds(10 + (Enabled ? DurationSeconds : 0));
    public static BrightnessFadeOptions Parse(IReadOnlyDictionary<string, string> parameters)
    {
        var enabled = parameters.GetValueOrDefault("fadeEnabled", "false");
        var duration = parameters.GetValueOrDefault("fadeDurationSeconds", "3");
        var frequency = parameters.GetValueOrDefault("fadeWritesPerSecond", "10");
        if (enabled is not ("true" or "false") ||
            !int.TryParse(duration, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 15 ||
            !int.TryParse(frequency, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var writes) || writes is < 1 or > 60)
            throw new ArgumentException("亮度渐变参数无效。");
        return new(enabled == "true", seconds, writes);
    }
    internal void Validate()
    {
        if (Enabled && DurationSeconds is < 1 or > 15) throw new ArgumentException("亮度渐变时长须为1–15秒。");
        if (Enabled && WritesPerSecond is < 1 or > 60) throw new ArgumentException("渐变平滑度须为1–60整数次/秒。");
    }
}

// A finite transition on already captured targets, on their existing native owner thread.
internal static class BrightnessFade
{
    internal static void Apply(IReadOnlyList<(BrightnessRestoreTarget Target, uint Value)> targets,
        BrightnessFadeOptions settings, CancellationToken token, TimeProvider? clock = null,
        Action<TimeSpan, CancellationToken>? wait = null)
    {
        settings.Validate();
        var time = clock ?? TimeProvider.System;
        void Wait(TimeSpan delay, CancellationToken cancellation)
        {
            if (wait is not null) wait(delay, cancellation);
            else Task.Delay(delay, time, cancellation).GetAwaiter().GetResult();
            cancellation.ThrowIfCancellationRequested();
        }
        try
        {
            if (!settings.Enabled)
            {
                foreach (var (target, end) in targets)
                { token.ThrowIfCancellationRequested(); target.Transaction.Apply(end, token); token.ThrowIfCancellationRequested(); }
                return;
            }
            var moving = new List<(BrightnessRestoreTarget Target, uint Value, uint Start)>();
            var completed = new HashSet<BrightnessRestoreTarget>();
            var bursts = new Dictionary<BrightnessRestoreTarget, TimeSpan>();
            var hardwareWrites = new Dictionary<BrightnessRestoreTarget, TimeSpan>();
            foreach (var (target, end) in targets)
            {
                token.ThrowIfCancellationRequested();
                var initial = target.Transaction.Confirmed;
                if (initial == end)
                {
                    // Confirm the live value once before excluding a zero-distance target.
                    target.Transaction.ConfirmFade(token);
                    token.ThrowIfCancellationRequested();
                }
                else moving.Add((target, end, initial));
            }
            var start = time.GetTimestamp();
            var next = TimeSpan.Zero;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!moving.Any(t => t.Target.Transaction.Owned && !completed.Contains(t.Target))) return;
                var remaining = next - time.GetElapsedTime(start);
                if (remaining > TimeSpan.Zero) Wait(remaining, token);
                token.ThrowIfCancellationRequested();
                var frameStarted = time.GetElapsedTime(start);
                var progress = Math.Clamp(frameStarted.TotalSeconds / settings.DurationSeconds, 0, 1);
                var eased = progress * progress * (3 - 2 * progress);
                foreach (var (target, end, initial) in moving)
                {
                    token.ThrowIfCancellationRequested();
                    if (!target.Transaction.Owned || completed.Contains(target)) continue;
                    var value = progress >= 1 ? end : (uint)Math.Round(initial + ((double)end - initial) * eased, MidpointRounding.AwayFromZero);
                    TimeSpan Cadence(uint candidate) => TimeSpan.FromMilliseconds(Math.Max(1000d / settings.WritesPerSecond, candidate == end ? 100 : 0));
                    if (target.HardwareBurst)
                    {
                        var elapsed = time.GetElapsedTime(start);
                        if (target.Transaction.HasPendingFadeWrite && target.Transaction.FadeExpected == end)
                        {
                            target.Transaction.ConfirmHardwareBurst(token, time, Wait); bursts.Remove(target);
                        }
                        else
                        {
                            var pending = target.Transaction.HasPendingFadeWrite;
                            var continuing = pending && !target.Transaction.HardwareBurstFull &&
                                bursts.TryGetValue(target, out var burstStarted) && elapsed - burstStarted < TimeSpan.FromSeconds(1);
                            if (pending && !continuing || value != target.Transaction.FadeExpected)
                            {
                                if (continuing && hardwareWrites.TryGetValue(target, out var previousWrite))
                                {
                                    var untilWrite = previousWrite + Cadence(value) - elapsed;
                                    var untilBoundary = bursts[target] + TimeSpan.FromSeconds(1) - elapsed;
                                    if (untilWrite > TimeSpan.Zero) Wait(untilWrite < untilBoundary ? untilWrite : untilBoundary, token);
                                    elapsed = time.GetElapsedTime(start);
                                    continuing = elapsed - bursts[target] < TimeSpan.FromSeconds(1);
                                }
                                // Reuse the strict boundary confirmation for the next segment's ownership check.
                                // A device may present earlier accepted values briefly; only that finite sequence can wait.
                                if (!continuing && !target.Transaction.CheckpointHardwareBurst(token)) continue;
                                if (hardwareWrites.TryGetValue(target, out previousWrite))
                                {
                                    var untilWrite = previousWrite + Cadence(value) - time.GetElapsedTime(start);
                                    if (untilWrite > TimeSpan.Zero) Wait(untilWrite, token);
                                }
                                var segmentStarted = time.GetElapsedTime(start);
                                // Native work and cadence waits both skip expired interpolation frames.
                                var currentProgress = Math.Clamp(segmentStarted.TotalSeconds / settings.DurationSeconds, 0, 1);
                                var currentEased = currentProgress * currentProgress * (3 - 2 * currentProgress);
                                value = currentProgress >= 1 ? end : (uint)Math.Round(initial + ((double)end - initial) * currentEased, MidpointRounding.AwayFromZero);
                                // A native confirmation or cadence wait can make this the endpoint frame.
                                // Enforce its own spacing without issuing a second Set or bypassing a boundary.
                                var endpointReady = true;
                                if (value == end && hardwareWrites.TryGetValue(target, out previousWrite))
                                {
                                    var untilEndpoint = previousWrite + Cadence(end) - time.GetElapsedTime(start);
                                    while (untilEndpoint > TimeSpan.Zero)
                                    {
                                        var delay = untilEndpoint;
                                        if (continuing)
                                        {
                                            var untilBoundary = bursts[target] + TimeSpan.FromSeconds(1) - time.GetElapsedTime(start);
                                            if (untilBoundary < delay) delay = untilBoundary;
                                        }
                                        if (delay > TimeSpan.Zero) Wait(delay, token);
                                        if (continuing && time.GetElapsedTime(start) - bursts[target] >= TimeSpan.FromSeconds(1))
                                        {
                                            if (!target.Transaction.CheckpointHardwareBurst(token)) { endpointReady = false; break; }
                                            continuing = false;
                                        }
                                        untilEndpoint = previousWrite + Cadence(end) - time.GetElapsedTime(start);
                                    }
                                }
                                if (!endpointReady) continue;
                                // Match the wheel path: spacing alone does not guarantee that a monitor
                                // will apply the last Set. Confirm this segment before issuing its endpoint.
                                if (value == end && continuing)
                                {
                                    if (!target.Transaction.CheckpointHardwareBurst(token)) continue;
                                    continuing = false;
                                }
                                segmentStarted = time.GetElapsedTime(start);
                                if (value != target.Transaction.FadeExpected) hardwareWrites[target] = segmentStarted;
                                target.Transaction.WriteHardwareBurst(value, token);
                                if (target.Transaction.Owned && target.Transaction.HasPendingFadeWrite && !continuing) bursts[target] = segmentStarted;
                                else if (!target.Transaction.HasPendingFadeWrite) bursts.Remove(target);
                            }
                        }
                    }
                    else if (target.Transaction.HasPendingFadeWrite || value != target.Transaction.Confirmed)
                    {
                        if (hardwareWrites.TryGetValue(target, out var previousWrite))
                        {
                            var untilWrite = previousWrite + TimeSpan.FromMilliseconds(1000d / settings.WritesPerSecond) - time.GetElapsedTime(start);
                            if (untilWrite > TimeSpan.Zero) Wait(untilWrite, token);
                        }
                        target.Transaction.AdvanceFade(value, token, () =>
                        {
                            var now = time.GetElapsedTime(start);
                            var currentProgress = Math.Clamp(now.TotalSeconds / settings.DurationSeconds, 0, 1);
                            var currentEased = currentProgress * currentProgress * (3 - 2 * currentProgress);
                            var nextValue = currentProgress >= 1 ? end : (uint)Math.Round(initial + ((double)end - initial) * currentEased, MidpointRounding.AwayFromZero);
                            if (nextValue != target.Transaction.Confirmed) hardwareWrites[target] = now;
                            return nextValue;
                        });
                    }
                    token.ThrowIfCancellationRequested();
                    if (target.Transaction.Owned && !target.Transaction.HasPendingFadeWrite && target.Transaction.Confirmed == end)
                        completed.Add(target);
                }
                // Slow native work skips expired frames; no backlog and no added hardware settling delay.
                next = frameStarted + TimeSpan.FromMilliseconds(16);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var (target, _) in targets) target.Transaction.Relinquish();
            throw;
        }
    }
}
