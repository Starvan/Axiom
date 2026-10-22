using System.Threading.Channels;

namespace Axiom.Providers.Windows;

public sealed record ScreenContentReading(double? AreaPercent, string? Reason = null, bool CaptureLost = false, ScreenBrightRegionMetrics? Regions = null);
public interface IScreenContentSource : IDisposable
{
    ChannelReader<ScreenContentReading> Readings { get; }
}
public interface IScreenContentEnvironment : IDisposable
{
    ChannelReader<string> Events { get; }
    bool? ReadLocked(CancellationToken token);
    IScreenContentSource Capture();
}
public sealed record ScreenContentStateSample(bool? Bright, bool Baseline, string Reason);

// AdvancedColorInfoChanged also reports brightness/white-level metadata. Only
// an actual SDR/WCG/HDR mode transition should invalidate screen capture.
public sealed class ScreenContentColorModeFilter
{
    private int? mode;
    public void Baseline(int current) => mode = current;
    public bool Update(int current)
    {
        var changed = mode.HasValue && mode.Value != current;
        mode = current;
        return changed;
    }
}

// Monotonic elapsed time is supplied by the Agent; unchanged desktops need only
// one stability deadline, not repeated captures or a health timer.
public sealed class ScreenContentState(int enterPercent, int exitPercent, TimeSpan stableFor, bool useRegions = false)
{
    private bool? value, pending;
    private TimeSpan pendingSince;
    private bool baseline = true;
    private ScreenBrightRegionMetrics? regions;
    public double? AreaPercent { get; private set; }
    public TimeSpan? PendingAt => pending.HasValue ? pendingSince + stableFor : null;
    public ScreenContentStateSample Current { get; private set; } = new(null, true, "正在等待主屏画面。");
    public ScreenContentStateSample Unknown(string reason)
    {
        value = pending = null; AreaPercent = null; regions = null; baseline = true;
        return Current = new(null, true, reason);
    }
    public ScreenContentStateSample OutsideWindow()
    {
        value = false; pending = null; AreaPercent = null; regions = null; baseline = true;
        return Current = new(false, true, "不在检测时段，已停止画面采集。");
    }
    public ScreenContentStateSample Read(ScreenContentReading reading, TimeSpan now)
    {
        if (useRegions && reading.Regions is null) return Unknown(reading.Reason ?? "正在等待大块亮区分析。");
        var signal = useRegions ? reading.Regions!.Score : reading.AreaPercent;
        if (signal is not { } area || !double.IsFinite(area) || area is < 0 or > 100)
            return Unknown(reading.Reason ?? "无法读取当前主屏画面。");
        regions = useRegions ? reading.Regions : null;
        AreaPercent = area;
        var candidate = value == true ? area > exitPercent : area >= enterPercent;
        if (candidate == value) pending = null;
        else if (pending != candidate) { pending = candidate; pendingSince = now; }
        return Advance(now);
    }
    public ScreenContentStateSample Advance(TimeSpan now)
    {
        var isBaseline = baseline;
        if (pending.HasValue && now >= pendingSince + stableFor)
        { value = pending; pending = null; baseline = false; }
        var reason = AreaPercent is { } area
            ? (regions is { } r ? $"大块亮区评分{area:F1}/100；最大亮区{r.LargestAreaPercent:F1}%，亮度{r.LuminancePercent:F0}%，周围对比{r.Contrast:F1}倍；" : $"主屏明亮面积约{area:F1}%；") + (pending.HasValue ? "画面变化正在等待稳定。" : value == true ? "已达到明亮条件。" : "未达到明亮条件。")
            : Current.Reason;
        return Current = new(value, isBaseline, reason);
    }
}
