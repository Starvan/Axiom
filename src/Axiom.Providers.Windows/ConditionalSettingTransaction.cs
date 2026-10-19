namespace Axiom.Providers.Windows;

// A single captured setting. The caller serializes native access and pins its actual target.
internal sealed class ConditionalSettingTransaction<T>(T baseline, Func<T> read, Action<T> write, string name,
    IEqualityComparer<T>? comparer = null, Func<T, T, bool>? acceptsTarget = null) where T : struct
{
    private readonly IEqualityComparer<T> equality = comparer ?? EqualityComparer<T>.Default;
    private readonly T original = baseline;
    private T confirmed = baseline;
    private int relinquished;
    private bool released;
    private bool fadePending;
    private T pendingTarget;
    private readonly List<T> hardwareBurstValues = [];
    private bool hardwareBurstVerified;
    public bool Owned => Volatile.Read(ref relinquished) == 0 && !released;
    internal T Confirmed => confirmed;
    internal bool HasPendingFadeWrite => fadePending;
    internal T FadeExpected => fadePending ? pendingTarget : confirmed;
    internal bool UnconfirmedFailure { get; private set; }
    internal bool HardwareBurstFull => hardwareBurstValues.Count >= 100;
    public void Relinquish()
    {
        if (fadePending) UnconfirmedFailure = true;
        hardwareBurstVerified = false;
        Interlocked.Exchange(ref relinquished, 1);
    }
    internal bool TryCompleteConfirmedRelease()
    {
        if (!Owned || fadePending || !equality.Equals(confirmed, original)) return false;
        released = true; Relinquish(); return true;
    }
    public RestorableSettingResult Apply(T target, CancellationToken token)
    {
        if (!Owned) return new(false, name + "本次控制已让出；不会重新抓取基线或重试。", false);
        if (fadePending) { Relinquish(); return new(false, name + "上次渐变写入尚未确认，本次让出，不覆盖。", false); }
        try
        {
            token.ThrowIfCancellationRequested();
            var current = read();
            if (!equality.Equals(current, confirmed))
            { Relinquish(); return new(false, name + "已被其他来源改变，本次让出，不证明是人为操作。", false); }
            token.ThrowIfCancellationRequested();
            if (!equality.Equals(current, target)) write(target);
            token.ThrowIfCancellationRequested();
            var actual = read();
            if (!(acceptsTarget?.Invoke(actual, target) ?? equality.Equals(actual, target)) || !Owned)
            { UnconfirmedFailure = true; Relinquish(); return new(false, name + "写入结果未确认，本次让出，不重试。", false); }
            confirmed = actual;
            return new(true, name + "已读回确认；条件结束时仅在仍等于本次最后确认值时恢复。", true);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { UnconfirmedFailure = true; Relinquish(); return new(false, name + "目标或读写未确认，本次让出；可能已修改，不自动恢复未确认写入。", false); }
    }
    // WMI fade confirms each previous write before issuing the next one.
    internal bool AdvanceFade(T target, CancellationToken token, Func<T>? targetAfterConfirmation = null)
    {
        if (!Owned) return false;
        try
        {
            if (!ConfirmFadeCore(token)) return false;
            token.ThrowIfCancellationRequested();
            if (targetAfterConfirmation is not null) target = targetAfterConfirmation();
            if (equality.Equals(FadeExpected, target)) return true;
            pendingTarget = target; fadePending = true;
            write(target);
            token.ThrowIfCancellationRequested();
            return Owned;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { UnconfirmedFailure = true; Relinquish(); return false; }
    }
    // Only hardware fade may send a bounded segment after its initial ownership check. Keep the
    // accepted values for this segment alone; successful Set is not a confirmed brightness value.
    internal bool WriteHardwareBurst(T target, CancellationToken token)
    {
        if (!Owned) return false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!hardwareBurstVerified || HardwareBurstFull) { Relinquish(); return false; }
            if (equality.Equals(FadeExpected, target)) return true;
            pendingTarget = target; fadePending = true;
            write(target);
            hardwareBurstValues.Add(target);
            token.ThrowIfCancellationRequested();
            return Owned;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { UnconfirmedFailure = true; Relinquish(); return false; }
    }
    // A checkpoint records actual progress, not completion of the requested target. A skipped
    // intermediate Set may be superseded only after reading a value sent in this finite segment.
    internal bool CheckpointHardwareBurst(CancellationToken token)
    {
        if (!Owned) return false;
        try
        {
            token.ThrowIfCancellationRequested();
            var actual = read();
            token.ThrowIfCancellationRequested();
            if (!Owned) return false;
            if (fadePending ? !hardwareBurstValues.Contains(actual) : !equality.Equals(actual, confirmed))
            { Relinquish(); return false; }
            var wasPending = fadePending;
            confirmed = actual; fadePending = false; hardwareBurstValues.Clear();
            // Retain only this observed, previously written value as a possible delayed reply to
            // the next Set. It is never sufficient to confirm that next Set's final endpoint.
            if (wasPending) hardwareBurstValues.Add(actual);
            hardwareBurstVerified = true;
            return true;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { UnconfirmedFailure = true; Relinquish(); return false; }
    }
    internal bool ConfirmHardwareBurst(CancellationToken token, TimeProvider time, Action<TimeSpan, CancellationToken> wait)
    {
        if (!Owned) return false;
        try
        {
            var started = time.GetTimestamp();
            var previousIndex = -1;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var actual = read();
                token.ThrowIfCancellationRequested();
                if (!Owned) return false;
                if (!fadePending)
                {
                    if (!equality.Equals(actual, confirmed)) { Relinquish(); return false; }
                    hardwareBurstVerified = true;
                    return true;
                }
                if (equality.Equals(actual, pendingTarget))
                {
                    confirmed = actual; fadePending = false; hardwareBurstValues.Clear();
                    hardwareBurstVerified = true;
                    return true;
                }
                var index = hardwareBurstValues.FindIndex(value => equality.Equals(value, actual));
                if (index < 0 || index < previousIndex) { Relinquish(); return false; }
                previousIndex = index;
                var remaining = TimeSpan.FromSeconds(1) - time.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) { Relinquish(); return false; }
                wait(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50), token);
                token.ThrowIfCancellationRequested();
                // Native calls cannot be interrupted, but never start another read after this deadline.
                if (time.GetElapsedTime(started) >= TimeSpan.FromSeconds(1)) { Relinquish(); return false; }
            }
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { UnconfirmedFailure = true; Relinquish(); return false; }
    }
    internal bool ConfirmFade(CancellationToken token)
    {
        if (!Owned) return false;
        try { return ConfirmFadeCore(token); }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { UnconfirmedFailure = true; Relinquish(); return false; }
    }
    private bool ConfirmFadeCore(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var actual = read();
        token.ThrowIfCancellationRequested();
        if (fadePending)
        {
            if (!(acceptsTarget?.Invoke(actual, pendingTarget) ?? equality.Equals(actual, pendingTarget)) || !Owned)
            { UnconfirmedFailure = true; Relinquish(); return false; }
            confirmed = actual; fadePending = false;
        }
        else if (!equality.Equals(actual, confirmed) || !Owned) { Relinquish(); return false; }
        return true;
    }
    public RestorableSettingResult Release(CancellationToken token)
    {
        if (released) return new(true, name + "租约已释放。", false);
        if (!Owned) { released = true; return new(true, name + "本次已让出，保留当前设置；没有恢复写入。", false); }
        if (fadePending) { released = true; Relinquish(); return new(false, name + "渐变写入尚未确认，不恢复未确认值。", false); }
        try
        {
            token.ThrowIfCancellationRequested();
            if (!equality.Equals(read(), confirmed))
            { Relinquish(); released = true; return new(true, name + "已被其他来源改变，保留当前设置，不恢复旧值。", false); }
            token.ThrowIfCancellationRequested();
            if (!equality.Equals(confirmed, original)) write(original);
            token.ThrowIfCancellationRequested();
            var restored = equality.Equals(read(), original);
            released = true; Relinquish();
            return new(restored, restored ? name + "已读回确认恢复本轮最初设置。" : name + "恢复结果未确认；可能已修改，不重试。", false);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        { released = true; Relinquish(); return new(false, name + "原目标不可核验或恢复未确认；保留当前设置，不改替代目标。", false); }
    }
}
