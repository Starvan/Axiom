using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class ScreenContentCapabilityProvider(Func<IScreenContentEnvironment> connect, TimeProvider? timeProvider = null,
    Func<TimeZoneInfo>? readZone = null) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [ScreenContentCapabilities.Bright];
    private sealed class Entry(ProviderResource resource)
    {
        internal readonly ProviderResource Resource = resource;
        internal readonly ScreenContentSpec Spec = ScreenContentCapabilities.ParseResource(resource.ResourceId);
        internal ScreenContentState State = null!;
        internal bool Active;
        internal ScreenContentStateSample? Sent;
    }
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length is < 1 or > 128 || resources.Any(r => r.CapabilityId != ScreenContentCapabilities.Bright) || resources.Distinct().Count() != resources.Length)
            throw new ArgumentException("画面检测资源无效。");
        token.ThrowIfCancellationRequested();
        var entries = resources.Select(r => new Entry(r)).ToArray();
        foreach (var e in entries) e.State = new(e.Spec.EnterPercent, e.Spec.ExitPercent, TimeSpan.FromSeconds((double)e.Spec.StableSeconds), e.Spec.Mode == "regions");
        var time = timeProvider ?? TimeProvider.System;
        TimeZoneInfo Zone() { if (readZone is not null) return readZone(); TimeZoneInfo.ClearCachedData(); return TimeZoneInfo.Local; }
        TimeSpan Elapsed() => TimeSpan.FromSeconds((double)time.GetTimestamp() / time.TimestampFrequency);
        var zone = Zone();
        var now = time.GetUtcNow();
        foreach (var e in entries) { e.Active = e.Spec.Contains(now, zone); if (!e.Active) e.State.OutsideWindow(); }
        Send(forceSnapshot: true);
        IScreenContentEnvironment? environment = null;
        IScreenContentSource? capture = null;
        Task<ScreenContentReading>? frame = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var never = Task.Delay(Timeout.Infinite, lifetime.Token);
        try
        {
            environment = connect();
            var locked = environment.ReadLocked(token);
            bool? screenOn = null;
            var captureFailed = false;
            string? failureReason = null;
            var changed = environment.Events.ReadAsync(lifetime.Token).AsTask();
            while (true)
            {
                token.ThrowIfCancellationRequested(); now = time.GetUtcNow();
                foreach (var e in entries)
                {
                    var active = e.Spec.Contains(now, zone);
                    if (active != e.Active)
                    {
                        e.Active = active;
                        if (active) { e.State.Unknown("已进入检测时段，正在等待主屏画面。"); captureFailed = false; }
                        else e.State.OutsideWindow();
                    }
                }
                var needed = entries.Any(e => e.Active);
                if (!needed || locked != false || screenOn != true || captureFailed)
                {
                    StopCapture();
                    var reason = locked != false ? "当前会话已锁定或状态未知，画面检测暂停。" : screenOn != true ? "屏幕关闭、睡眠或电源状态未知，画面检测暂停。" : failureReason ?? "主屏捕获不可用，等待系统显示或会话恢复通知。";
                    foreach (var e in entries.Where(e => e.Active)) e.State.Unknown(reason);
                }
                else if (capture is null)
                {
                    try { capture = environment.Capture(); frame = capture.Readings.ReadAsync(lifetime.Token).AsTask(); }
                    catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
                    {
                        captureFailed = true; failureReason = SafeReason(error);
                        foreach (var e in entries.Where(e => e.Active)) e.State.Unknown(failureReason);
                    }
                }
                foreach (var e in entries.Where(e => e.Active && e.State.PendingAt.HasValue)) e.State.Advance(Elapsed());
                Send();
                var boundary = entries.Select(e => e.Spec.NextBoundary(now, zone)).Where(b => b.HasValue).Select(b => b!.Value).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
                var remaining = boundary == DateTimeOffset.MaxValue ? Timeout.InfiniteTimeSpan : boundary - now;
                foreach (var e in entries.Where(e => e.Active))
                    if (e.State.PendingAt is { } pending)
                    {
                        var until = pending - Elapsed();
                        if (remaining == Timeout.InfiniteTimeSpan || until < remaining) remaining = until;
                    }
                if (remaining != Timeout.InfiniteTimeSpan && remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                var due = Task.Delay(remaining, time, deadline.Token);
                try
                {
                    await Task.WhenAny(changed, frame ?? never, due);
                    token.ThrowIfCancellationRequested();
                    if (changed.IsCompleted)
                    {
                        var kind = await changed;
                        do
                        {
                            if (kind is "screen-on" or "screen-dim") screenOn = true;
                            else if (kind is "screen-off" or "screen-reset") screenOn = kind == "screen-off" ? false : null;
                            if (kind is not "clock-changed")
                            {
                                StopCapture();
                                locked = environment.ReadLocked(token);
                                captureFailed = false; failureReason = null;
                                foreach (var e in entries.Where(e => e.Active)) e.State.Unknown("系统显示或会话已变化，正在重新核验主屏。");
                            }
                            if (kind is "clock-changed" or "resumed") zone = Zone();
                        } while (environment.Events.TryRead(out kind));
                        changed = environment.Events.ReadAsync(lifetime.Token).AsTask();
                    }
                    if (frame?.IsCompleted == true)
                    {
                        var reading = await frame;
                        if (reading.AreaPercent is null)
                        {
                            failureReason = reading.Reason ?? "主屏捕获不可用，等待显示或会话恢复通知。";
                            if (reading.CaptureLost) { captureFailed = true; StopCapture(); }
                            else frame = capture!.Readings.ReadAsync(lifetime.Token).AsTask();
                            foreach (var e in entries.Where(e => e.Active)) e.State.Unknown(failureReason);
                        }
                        else
                        {
                            foreach (var e in entries.Where(e => e.Active)) e.State.Read(reading, Elapsed());
                            frame = capture!.Readings.ReadAsync(lifetime.Token).AsTask();
                        }
                    }
                }
                finally { deadline.Cancel(); try { await due; } catch (OperationCanceledException) when (deadline.IsCancellationRequested) { } }
            }
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            foreach (var e in entries.Where(e => e.Active)) e.State.Unknown(SafeReason(error));
            Send(); StopCapture(); environment?.Dispose(); environment = null;
            await Task.Delay(Timeout.Infinite, token);
        }
        finally { lifetime.Cancel(); StopCapture(); environment?.Dispose(); }
        void StopCapture() { var old = capture; capture = null; frame = null; old?.Dispose(); }
        void Send(bool forceSnapshot = false)
        {
            var updated = entries.Where(e => forceSnapshot || e.Sent != e.State.Current).ToArray();
            if (updated.Length == 0) return;
            var snapshot = forceSnapshot || updated.Any(e => e.State.Current.Baseline);
            publish(new(snapshot, [.. updated.Select(e => new ProviderSample(e.Resource, e.State.Current.Bright.HasValue ? StateQuality.Known : StateQuality.Unknown,
                "axiom.bool", 1, Boolean: e.State.Current.Bright, Reason: e.State.Current.Reason))]));
            foreach (var e in updated) e.Sent = e.State.Current;
        }
    }
    private static string SafeReason(Exception error) => error is NotSupportedException ? error.Message : "主屏捕获或系统通知不可用，已暂停检测；等待显示、会话或时段变化后重建。";
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("画面明亮只能作为检测。");
}
