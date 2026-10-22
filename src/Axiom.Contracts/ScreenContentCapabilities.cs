using System.Globalization;
using System.Collections.Immutable;
using Axiom.Domain;

namespace Axiom.Contracts;

public sealed record ScreenContentSpec(int EnterPercent, int ExitPercent, decimal StableSeconds, int? StartMinute = null, int? EndMinute = null, string Mode = "area")
{
    public string Resource => (Mode == "regions" ? "r|" : "") + FormattableString.Invariant($"{EnterPercent}:{ExitPercent}:{StableSeconds:0.##}@") +
        (StartMinute is { } start && EndMinute is { } end ? FormattableString.Invariant($"{start / 60:00}:{start % 60:00}-{end / 60:00}:{end % 60:00}") : "*");
    public void Validate()
    {
        if (Mode is not ("area" or "regions") || EnterPercent is < 1 or > 100 || ExitPercent < 0 || ExitPercent >= EnterPercent || StableSeconds is < 0 or > 60 || decimal.Round(StableSeconds, 2) != StableSeconds ||
            StartMinute.HasValue != EndMinute.HasValue || StartMinute is < 0 or > 1439 || EndMinute is < 0 or > 1439 || StartMinute.HasValue && StartMinute == EndMinute)
            throw new ArgumentException("进入比例须大于退出比例，稳定时间为0–60秒，最多两位小数；0表示立即响应。检测时段须同时填写不同的HH:mm起止时刻，或均留空表示全天。");
    }
    public bool Contains(DateTimeOffset now, TimeZoneInfo zone)
    {
        if (StartMinute is not { } start || EndMinute is not { } end) return true;
        var minute = TimeZoneInfo.ConvertTime(now, zone).TimeOfDay.TotalMinutes;
        return start < end ? minute >= start && minute < end : minute >= start || minute < end;
    }
    public DateTimeOffset? NextBoundary(DateTimeOffset after, TimeZoneInfo zone)
    {
        if (StartMinute is not { } start || EndMinute is not { } end) return null;
        DateTimeOffset? next = null;
        var date = TimeZoneInfo.ConvertTime(after, zone).Date;
        for (var day = -1; day <= 3; day++)
            foreach (var minute in new[] { start, end })
            {
                var local = DateTime.SpecifyKind(date.AddDays(day).AddMinutes(minute), DateTimeKind.Unspecified);
                // A skipped local boundary takes effect at the first existing minute.
                for (var missing = 0; zone.IsInvalidTime(local) && missing < 180; missing++) local = local.AddMinutes(1);
                if (zone.IsInvalidTime(local)) continue;
                var offsets = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local) : [zone.GetUtcOffset(local)];
                foreach (var offset in offsets)
                {
                    var candidate = new DateTimeOffset(local, offset).ToUniversalTime();
                    if (candidate > after && (next is null || candidate < next)) next = candidate;
                }
            }
        return next ?? throw new InvalidOperationException("无法计算下一个检测时段边界。");
    }
}

public static class ScreenContentCapabilities
{
    public const string Bright = "screen.content-bright";
    public static TaskCapability Descriptor { get; } = new(Bright, 1, TaskCapabilityKind.Detection, "显示", "主屏画面大面积明亮",
        "检测主屏大块亮色或明亮总面积，适合夜间降低亮度并在画面变暗后恢复；不限白色。",
        [new("mode", "检测方式", TaskFieldKind.Choice, false, "大块亮区结合区域面积、亮度与周围对比，减少零散亮点误判；推荐进入20、退出12。原面积统计保持旧阈值含义。", "area", 7, [new("regions", "大块亮区"), new("area", "原面积")]),
         new("enterPercent", "进入值", TaskFieldKind.Number, true, "大块亮区为评分（推荐20）；原面积模式为超过原亮度门槛的采样点百分比。范围1–100，越低越容易触发。", "65", 3, [], Minimum: 1, Maximum: 100),
         new("exitPercent", "退出值", TaskFieldKind.Number, true, "低于或等于此值退出；须小于进入阈值。范围0–99，大块亮区推荐12，避免反复调亮。", "45", 3, [], Minimum: 0, Maximum: 99),
         new("stableSeconds", "画面变化稳定时间（秒）", TaskFieldKind.Number, true, "0表示立即响应；支持0–60秒、最多两位小数，例如0.2、0.5。进入和退出均须连续稳定，默认3秒。", "3", 5, [], Minimum: 0, Maximum: 60),
         new("start", "检测开始（HH:mm，可选）", TaskFieldKind.Text, false, "与结束同时留空表示全天；例如20:00。时段外完全停止画面采集。", "", 5, [], Group: "检测时段"),
         new("end", "检测结束（HH:mm，可选）", TaskFieldKind.Text, false, "例如08:00，结束早于开始表示跨午夜；结束时立即释放亮度控制。", "", 5, [], Group: "检测时段")],
        "启用且在检测时段内共享主屏采集，桌面更新时最多每250毫秒分析一次；GPU只回读64×36量化亮度网格及旧面积计数，不保存或发送图像。大块亮区评分综合连片面积、线性相对亮度和周围对比，排除小碎片及稀疏细线；全屏亮底同样可触发。这是可调检测规则，不是医学或标准眩光测量。",
        "HDR 不会在此检测内自动暂停；可在任务的自动暂停设置中选择 HDR 开启时暂停。锁屏、睡眠、屏幕关闭、受保护内容或捕获失败为未知并释放亮度控制，恢复事件后重建。统计基于系统转换后的桌面画面，HDR 下可能失真；是相对亮度与面积，不是背光或 nits。稀疏采样有误差，系统未报告的受保护遮蔽无法识别。", IsStateCondition: true);
    public static ScreenContentSpec Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Bright || invocation.SchemaVersion != 1 || invocation.Parameters is null ||
            invocation.Parameters.Keys.Any(k => k is not ("mode" or "enterPercent" or "exitPercent" or "stableSeconds" or "start" or "end")))
            throw new ArgumentException("画面明亮检测配置无效。");
        int Number(string key)
        {
            if (!invocation.Parameters.TryGetValue(key, out var value) || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                throw new ArgumentException("明亮面积须为整数。");
            return n;
        }
        int? Minute(string key)
        {
            var text = invocation.Parameters.GetValueOrDefault(key, "");
            if (text.Length == 0) return null;
            if (text.Length != 5 || !TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw new ArgumentException("检测时段须为HH:mm，例如20:00和08:00。");
            return parsed.Hour * 60 + parsed.Minute;
        }
        if (!invocation.Parameters.TryGetValue("stableSeconds", out var stability) || stability.Length > 5 ||
            !decimal.TryParse(stability, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds))
            throw new ArgumentException("稳定时间须为0–60秒，可填小数，例如0.2、0.5。");
        var spec = new ScreenContentSpec(Number("enterPercent"), Number("exitPercent"), seconds, Minute("start"), Minute("end"), invocation.Parameters.GetValueOrDefault("mode", "area"));
        spec.Validate(); return spec;
    }
    public static ScreenContentSpec ParseResource(string resource)
    {
        if (resource.Length > 30) throw new ArgumentException("画面检测资源无效。");
        var original = resource; var mode = resource.StartsWith("r|", StringComparison.Ordinal) ? "regions" : "area";
        if (mode == "regions") resource = resource[2..];
        var halves = resource.Split('@'); var numbers = halves[0].Split(':');
        if (halves.Length != 2 || numbers.Length != 3) throw new ArgumentException("画面检测资源无效。");
        var window = halves[1] == "*" ? ["", ""] : halves[1].Split('-');
        if (window.Length != 2) throw new ArgumentException("检测时段资源无效。");
        var fields = new Dictionary<string, string> { ["mode"] = mode, ["enterPercent"] = numbers[0], ["exitPercent"] = numbers[1], ["stableSeconds"] = numbers[2], ["start"] = window[0], ["end"] = window[1] };
        var spec = Validate(new(Bright, 1, fields.ToImmutableDictionary()));
        if (spec.Resource != original) throw new ArgumentException("画面检测资源格式无效。");
        return spec;
    }
}
