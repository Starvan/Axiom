using System.Globalization;
using System.Threading.Channels;

namespace Axiom.Providers.Windows;

// WMI and DXVA2 can refer to the same screen. An owned restoration blocks other Axiom brightness writes.
internal static class BrightnessNativeOwnership
{
    private static readonly object gate = new();
    private static Lease? active;
    private static bool manual;
    private static bool cleanupFailed;
    internal static IDisposable? TryAcquire(Action? abandon = null, bool manualGesture = false)
    {
        lock (gate)
        {
            if (cleanupFailed || active is not null || manual && !manualGesture || manualGesture && !manual) return null;
            return active = new Lease(abandon);
        }
    }
    internal static Task DrainAsync() => Volatile.Read(ref active)?.Stopped.Task ?? Task.CompletedTask;
    internal static Task BeginManual()
    {
        Lease? current;
        lock (gate) { manual = true; current = active; }
        current?.Abandon?.Invoke();
        return current?.Stopped.Task ?? Task.CompletedTask;
    }
    internal static void FailCleanup() { lock (gate) cleanupFailed = true; }
    internal static void EndManual()
    {
        lock (gate)
        {
            if (active is not null || cleanupFailed) throw new InvalidOperationException("亮度原生工作或清理尚未确认，不能恢复自动控制。");
            manual = false;
        }
    }
    private sealed class Lease(Action? abandon) : IDisposable
    {
        internal readonly Action? Abandon = abandon;
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() { lock (gate) if (ReferenceEquals(active, this)) { active = null; Stopped.TrySetResult(); } }
    }
}

internal sealed class BrightnessRestoreTarget
{
    internal readonly uint Baseline;
    internal readonly Func<int, uint?> Map;
    internal readonly ConditionalSettingTransaction<uint> Transaction;
    internal readonly bool HardwareBurst;
    internal uint? LastRead;
    internal BrightnessRestoreTarget(uint baseline, Func<int, uint?> map, Func<uint?> read, Func<uint, bool> write, bool hardwareBurst = false)
    {
        Baseline = baseline; Map = map; HardwareBurst = hardwareBurst;
        Transaction = new(baseline, () =>
        {
            LastRead = read(); return LastRead ?? throw new IOException("原亮度目标未知。");
        }, value => { if (!write(value)) throw new IOException("原亮度目标未接受设置。"); }, "屏幕亮度");
    }
}
internal sealed class BrightnessRestoreCapture(IReadOnlyList<BrightnessRestoreTarget> targets, int unknown, int unsupported,
    Action<CancellationToken> useToken, Action close) : IDisposable
{
    internal readonly IReadOnlyList<BrightnessRestoreTarget> Targets = targets;
    internal readonly int Unknown = unknown, Unsupported = unsupported;
    internal readonly Action<CancellationToken> UseToken = useToken;
    public void Dispose() => close();
}

// One worker keeps captured WMI objects/physical handles on their original thread. Requests are not polled or queued behind writes.
internal sealed class BrightnessRestoreSession : IRestorableSettingSession
{
    private sealed record Request(int? Percent, BrightnessFadeOptions Fade, CancellationToken Token, TaskCompletionSource<RestorableSettingResult> Result);
    private readonly Channel<Request> requests = Channel.CreateBounded<Request>(1);
    private readonly SemaphoreSlim phase = new(1, 1);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<RestorableSettingResult> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource captureStop;
    private readonly CancellationTokenSource abandonStop = new();
    private readonly string? selection;
    private readonly TimeSpan timeout;
    private readonly TimeProvider time;
    private readonly Action<TimeSpan, CancellationToken>? wait;
    private BrightnessFadeOptions fade;
    private TimeSpan OperationTimeout => timeout + TimeSpan.FromSeconds(fade.Enabled ? fade.DurationSeconds : 0);
    private int terminal, closeRequested;
    private int abandoned;
    private BrightnessRestoreSession(string? selection, TimeSpan timeout, TimeProvider time, CancellationToken token, Action<TimeSpan, CancellationToken>? wait)
    { this.selection = selection; this.timeout = timeout; this.time = time; this.wait = wait; captureStop = CancellationTokenSource.CreateLinkedTokenSource(token); }
    internal static async Task<IRestorableSettingSession> CreateAsync(Func<CancellationToken, BrightnessRestoreCapture> capture,
        string? selection, TimeSpan timeout, CancellationToken token, TimeProvider? time = null, Action<TimeSpan, CancellationToken>? wait = null)
    {
        var session = new BrightnessRestoreSession(selection, timeout, time ?? TimeProvider.System, token, wait);
        token.ThrowIfCancellationRequested(); var ownership = BrightnessNativeOwnership.TryAcquire(session.Abandon);
        if (ownership is null) { session.captureStop.Dispose(); session.abandonStop.Dispose(); throw new IOException("已有亮度操作、恢复租约或手动接管；请先等待释放或恢复自动控制。本次不排队、不修改。"); }
        try { new Thread(() => session.Run(capture, ownership)) { IsBackground = true, Name = "Axiom owned brightness restoration" }.Start(); }
        catch { ownership.Dispose(); session.captureStop.Dispose(); session.abandonStop.Dispose(); throw; }
        try { await session.ready.Task.WaitAsync(timeout, session.time, token); return session; }
        catch { session.RequestClose(); throw; } // the actual worker keeps ownership/resources until it returns and cleans up
    }
    public async Task<RestorableSettingResult> ApplyAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken token)
    {
        var requestedFade = BrightnessFadeOptions.Parse(parameters);
        if (!parameters.TryGetValue("percent", out var text) || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var percent) || percent is < 0 or > 100)
            throw new ArgumentException("恢复租约亮度须为0–100整数。");
        if (selection is not null && (!parameters.TryGetValue("target", out var target) || target != selection))
        { Interlocked.Exchange(ref terminal, 1); return new(false, "本次亮度租约的屏幕范围已固定；不会改控新屏幕。", false); }
        if (Volatile.Read(ref terminal) != 0 || !phase.Wait(0)) return new(false, "亮度租约已让出或上次原生操作尚未返回；不重新捕获基线。", false);
        fade = requestedFade;
        return await SubmitAsync(percent, token);
    }
    public async Task<RestorableSettingResult> ReleaseAsync(CancellationToken token)
    {
        Interlocked.Exchange(ref terminal, 1);
        if (stopped.Task.IsCompleted) return await stopped.Task;
        try { await phase.WaitAsync(token).WaitAsync(OperationTimeout, time, token); }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        { RequestClose(); return new(false, "亮度释放等待原生操作结束；恢复尚未确认，句柄与准入仍保留。", false); }
        if (stopped.Task.IsCompleted) { phase.Release(); return await stopped.Task; }
        return await SubmitAsync(null, token);
    }
    private async Task<RestorableSettingResult> SubmitAsync(int? percent, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, abandonStop.Token);
        var result = new TaskCompletionSource<RestorableSettingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!requests.Writer.TryWrite(new(percent, fade, lifetime.Token, result)))
        { phase.Release(); return new(false, "亮度租约已结束；不会写入或重新捕获。", false); }
        try { return await result.Task.WaitAsync(OperationTimeout, time, token); }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        {
            Interlocked.Exchange(ref terminal, 1);
            return new(false, "亮度操作取消或超时，可能已设置但未确认；不重试，原生返回后才释放资源。", false);
        }
        finally { lifetime.Cancel(); } // worker, rather than the awaiting caller, releases phase admission
    }
    private void RequestClose()
    {
        Interlocked.Exchange(ref terminal, 1); Interlocked.Exchange(ref closeRequested, 1);
        try { captureStop.Cancel(); } catch (ObjectDisposedException) { } requests.Writer.TryComplete();
    }
    private void Abandon()
    {
        Interlocked.Exchange(ref abandoned, 1);
        try { abandonStop.Cancel(); } catch (ObjectDisposedException) { }
        RequestClose();
    }
    public async ValueTask DisposeAsync() { RequestClose(); await stopped.Task; }
    private void Run(Func<CancellationToken, BrightnessRestoreCapture> create, IDisposable ownership)
    {
        BrightnessRestoreCapture? capture = null; Request? release = null;
        RestorableSettingResult receipt = new(false, "未取得已知亮度基线，本次未修改。", false); var releaseAttempted = false;
        try
        {
            capture = create(captureStop.Token); captureStop.Token.ThrowIfCancellationRequested();
            if (capture.Targets.Count == 0) throw new IOException("没有可确认支持的已知亮度目标；本次未修改。");
            ready.TrySetResult();
            while (requests.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult() && requests.Reader.TryRead(out var request))
            {
                if (request.Percent is null)
                {
                    release = request; releaseAttempted = true; receipt = Restore(capture, request.Fade, request.Token); break;
                }
                CompleteApply(phase, request.Result, () =>
                {
                    try
                    {
                        var applied = Apply(capture, request.Percent.Value, request.Fade, request.Token);
                        if (!applied.Owned) Interlocked.Exchange(ref terminal, 1);
                        return applied;
                    }
                    catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
                    {
                        Interlocked.Exchange(ref terminal, 1); return new(false, "亮度设置未确认；不重新捕获或重试，结束时仅恢复仍归本次租约所有的已确认值。", false);
                    }
                });
                if (Volatile.Read(ref closeRequested) != 0) break;
            }
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { ready.TrySetException(error); receipt = new(false, "亮度捕获或恢复未确认；原目标可能不可用，不会改控新屏幕。", false); }
        finally
        {
            try
            {
                if (capture is not null && Volatile.Read(ref abandoned) != 0)
                {
                    foreach (var target in capture.Targets) target.Transaction.Relinquish();
                    receipt = new(true, "已明确放弃本轮亮度恢复，保留当下亮度；原生工作及资源已结束。", false);
                }
                else if (capture is not null && !releaseAttempted)
                {
                    using var cleanup = new CancellationTokenSource(OperationTimeout); receipt = Restore(capture, fade, cleanup.Token);
                }
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { receipt = new(false, "亮度恢复未确认；不会重试或改控新屏幕。", false); }
            try { capture?.Dispose(); }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { receipt = new(false, receipt.Reason + " 亮度资源释放未确认。", false); }
            requests.Writer.TryComplete(); Interlocked.Exchange(ref terminal, 1);
            while (requests.Reader.TryRead(out var pending)) { pending.Result.TrySetResult(new(false, "亮度租约已结束。", false)); phase.Release(); }
            if (release is not null) phase.Release();
            ownership.Dispose(); captureStop.Dispose(); abandonStop.Dispose(); stopped.TrySetResult(receipt); release?.Result.TrySetResult(receipt);
        }
    }
    internal static void CompleteApply(SemaphoreSlim admission, TaskCompletionSource<RestorableSettingResult> completion,
        Func<RestorableSettingResult> apply)
    {
        RestorableSettingResult receipt;
        try { receipt = apply(); }
        finally { admission.Release(); }
        // An awaiting consumer may run before TrySetResult returns, even with asynchronous continuations.
        completion.TrySetResult(receipt);
    }
    private RestorableSettingResult Apply(BrightnessRestoreCapture capture, int percent, BrightnessFadeOptions settings, CancellationToken token)
    {
        capture.UseToken(token); var confirmed = 0; var failed = capture.Unknown; var relinquished = 0;
        var transitions = new List<(BrightnessRestoreTarget Target, uint Value)>();
        foreach (var target in capture.Targets)
        {
            token.ThrowIfCancellationRequested();
            if (!target.Transaction.Owned) { relinquished++; continue; }
            var requested = target.Map(percent);
            if (!requested.HasValue) { target.Transaction.Relinquish(); failed++; continue; }
            transitions.Add((target, requested.Value));
        }
        BrightnessFade.Apply(transitions, settings, token, time, wait);
        confirmed += transitions.Count(t => t.Target.Transaction.Owned);
        failed += transitions.Count(t => !t.Target.Transaction.Owned);
        var owned = confirmed > 0 && failed == 0 && relinquished == 0;
        return new(owned, $"亮度条件设置{(owned ? "已确认" : confirmed > 0 ? "部分完成" : "未确认")}：{confirmed}块确认，{capture.Unsupported}块已知不支持跳过，{failed}个目标未知或失败，{relinquished}块已让出；外部改变或未确认不覆盖，本次基线保留。", owned);
    }
    private RestorableSettingResult Restore(BrightnessRestoreCapture capture, BrightnessFadeOptions settings, CancellationToken token)
    {
        capture.UseToken(token); var restored = 0; var skipped = 0; var failed = 0;
        if (settings.Enabled)
            BrightnessFade.Apply(capture.Targets.Where(t => t.Transaction.Owned).Select(t => (t, t.Baseline)).ToArray(), settings, token, time, wait);
        foreach (var target in capture.Targets)
        {
            token.ThrowIfCancellationRequested(); var previouslyOwned = target.Transaction.Owned;
            // The fade's final Apply has already checked ownership and read back the exact baseline.
            var receipt = settings.Enabled && target.Transaction.TryCompleteConfirmedRelease()
                ? new RestorableSettingResult(true, "渐变已读回确认原基线，租约已释放。", false)
                : target.Transaction.Release(token);
            token.ThrowIfCancellationRequested();
            if (settings.Enabled && target.Transaction.UnconfirmedFailure) failed++;
            else if (!previouslyOwned || receipt.Success && target.LastRead != target.Baseline) skipped++;
            else if (receipt.Success) restored++;
            else failed++;
        }
        return new(failed == 0, $"亮度结束恢复：{restored}块基线读回确认，{skipped}块其他来源改变/设备未知/未确认值未覆盖，{failed}块恢复未确认；不重试、不改控新屏幕。", false);
    }
}
