using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class BrightnessWheelAgent(ShortcutAgent shortcuts, string sid)
{
    private sealed class Subscription(string id, BrightnessWheelSettings settings, CancellationToken token) : IDisposable
    {
        internal readonly string Id = id;
        internal readonly BrightnessWheelSettings Settings = settings;
        internal readonly CancellationTokenSource Lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        internal readonly Channel<byte> Signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        internal readonly SemaphoreSlim Gesture = new(1, 1);
        internal long Steps, Consumed;
        public void Dispose() { Lifetime.Dispose(); Gesture.Dispose(); }
    }
    private readonly object gate = new();
    private Subscription? active;
    internal async Task ObserveAsync(Stream pipe, ProviderMessage request, CancellationToken token)
    {
        var settings = request.WheelSettings!; BrightnessWheelTool.Validate(settings);
        using var subscription = new Subscription(request.RequestId, settings, token);
        lock (gate) { if (active is not null) throw new IOException("旧滚轮监听仍在清理，请稍后启用。"); active = subscription; }
        WindowsBrightnessWheelHook? hook = null;
        var remainder = new BrightnessWheelDelta();
        var disconnected = ProviderProtocol.ReadAsync(pipe, subscription.Lifetime.Token);
        try
        {
            hook = await shortcuts.OnMessageThreadAsync(() => new WindowsBrightnessWheelHook((int)settings.Modifiers,
                settings.Key.Length == 0 ? 0u : HotkeyCapabilities.VirtualKey(new(settings.Modifiers, settings.Key)), delta =>
                {
                    var steps = remainder.Add(delta);
                    if (steps == 0 || subscription.Lifetime.IsCancellationRequested) return;
                    Interlocked.Add(ref subscription.Steps, steps); subscription.Signals.Writer.TryWrite(0);
                }));
            long sequence = 0;
            await Send();
            while (!subscription.Lifetime.IsCancellationRequested)
            {
                var next = subscription.Signals.Reader.ReadAsync(subscription.Lifetime.Token).AsTask();
                if (await Task.WhenAny(next, disconnected) != next) break;
                await next; await Send();
            }
            async Task Send()
            {
                using var write = CancellationTokenSource.CreateLinkedTokenSource(subscription.Lifetime.Token); write.CancelAfter(TimeSpan.FromSeconds(5));
                await ProviderProtocol.WriteAsync(pipe, new(1, "brightness-wheel-state", request.RequestId, Sequence: sequence++, Status: OutcomeStatus.Succeeded,
                    Reason: "滚轮监听已安装；命中组合后由Engine授权调节。", WheelSteps: Interlocked.Read(ref subscription.Steps)), write.Token);
            }
        }
        finally
        {
            subscription.Lifetime.Cancel();
            if (hook is not null) await shortcuts.OnMessageThreadAsync(() => { hook.Dispose(); return true; });
            await subscription.Gesture.WaitAsync(); subscription.Gesture.Release();
            lock (gate) if (ReferenceEquals(active, subscription)) active = null;
            try { await disconnected; } catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
    }
    internal async Task<BrightnessWheelGestureResult> InvokeAsync(TaskInvocation invocation, CancellationToken token)
    {
        var gesture = BrightnessWheelTool.ValidateGesture(invocation);
        Subscription current;
        lock (gate) current = active is { } item && item.Id == gesture.Subscription ? item : throw new IOException("滚轮监听代际已变化。");
        if (!current.Gesture.Wait(0)) throw new IOException("原滚轮手势尚未清理，不排队。");
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, current.Lifetime.Token);
            lifetime.CancelAfter(TimeSpan.FromSeconds(28));
            if (gesture.FromSteps != Interlocked.Read(ref current.Consumed)) throw new IOException("滚轮累计步数与授权起点不符。");
            var result = await WindowsHardwareBrightness.RunWheelGestureAsync(current.Settings.Target, sid, current.Settings.Step,
                current.Settings.SmoothMilliseconds, current.Settings.WritesPerSecond, gesture.FromSteps, () => Interlocked.Read(ref current.Steps), lifetime.Token);
            Interlocked.Exchange(ref current.Consumed, result.ConsumedSteps);
            if (!result.Success) current.Lifetime.Cancel();
            return result;
        }
        finally { current.Gesture.Release(); }
    }
}

