using System.Globalization;
using System.Collections.Immutable;
using Axiom.Domain;

namespace Axiom.Contracts;

public sealed record ScreenContentSpec(int EnterPercent, int ExitPercent, int StableSeconds, int? StartMinute = null, int? EndMinute = null)
{
    public string Resource => FormattableString.Invariant($"{EnterPercent}:{ExitPercent}:{StableSeconds}@") +
        (StartMinute is { } start && EndMinute is { } end ? FormattableString.Invariant($"{start / 60:00}:{start % 60:00}-{end / 60:00}:{end % 60:00}") : "*");
    public void Validate()
    {
        if (EnterPercent is < 1 or > 100 || ExitPercent < 0 || ExitPercent >= EnterPercent || StableSeconds is < 0 or > 60 ||
            StartMinute.HasValue != EndMinute.HasValue || StartMinute is < 0 or > 1439 || EndMinute is < 0 or > 1439 || StartMinute.HasValue && StartMinute == EndMinute)
            throw new ArgumentException("进入比例须大于退出比例，稳定时间为0–60秒；0表示立即响应。检测时段须同时填写不同的HH:mm起止时刻，或均留空表示全天。");
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
        "按主屏画面中较亮区域的面积检测，适合夜间自动降低屏幕亮度并在画面变暗后恢复；不限白色。",
        [new("enterPercent", "进入条件的明亮面积（%）", TaskFieldKind.Number, true, "64×36网格中，感知亮度达到70%的采样点比例。", "65", 3, [], Minimum: 1, Maximum: 100),
         new("exitPercent", "退出条件的明亮面积（%）", TaskFieldKind.Number, true, "低于或等于此比例才退出；须小于进入比例，避免反复调亮。", "45", 3, [], Minimum: 0, Maximum: 99),
         new("stableSeconds", "画面变化稳定时间（秒）", TaskFieldKind.Number, true, "0表示立即进入和退出；1–60表示进入和退出均须连续稳定，默认3秒。", "3", 2, [], Minimum: 0, Maximum: 60),
         new("start", "检测开始（HH:mm，可选）", TaskFieldKind.Text, false, "与结束同时留空表示全天；例如20:00。时段外完全停止画面采集。", "", 5, [], Group: "检测时段"),
         new("end", "检测结束（HH:mm，可选）", TaskFieldKind.Text, false, "例如08:00，结束早于开始表示跨午夜；结束时立即释放亮度控制。", "", 5, [], Group: "检测时段")],
        "只在有启用规则且处于本检测时段时共享采集主屏。桌面更新时最多每250毫秒处理一次，GPU稀疏采样后仅回读4字节统计；画面不保存、不编码、不发送。明亮/变暗经滞回和所选稳定时间后驱动现有条件亮度恢复动作，稳定时间为0时立即响应。",
        "HDR 不会在此检测内自动暂停；可在任务的自动暂停设置中选择 HDR 开启时暂停。锁屏、睡眠、屏幕关闭、受保护内容或捕获失败为未知并释放亮度控制，恢复事件后重建。统计基于系统转换后的桌面画面，HDR 下可能失真；是相对亮度与面积，不是背光或 nits。稀疏采样有误差，系统未报告的受保护遮蔽无法识别。", IsStateCondition: true);
    public static ScreenContentSpec Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Bright || invocation.SchemaVersion != 1 || invocation.Parameters is null ||
            invocation.Parameters.Keys.Any(k => k is not ("enterPercent" or "exitPercent" or "stableSeconds" or "start" or "end")))
            throw new ArgumentException("画面明亮检测配置无效。");
        int Number(string key)
        {
            if (!invocation.Parameters.TryGetValue(key, out var value) || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                throw new ArgumentException("面积及稳定时间须为整数。");
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
        var spec = new ScreenContentSpec(Number("enterPercent"), Number("exitPercent"), Number("stableSeconds"), Minute("start"), Minute("end"));
        spec.Validate(); return spec;
    }
    public static ScreenContentSpec ParseResource(string resource)
    {
        if (resource.Length > 28) throw new ArgumentException("画面检测资源无效。");
        var halves = resource.Split('@'); var numbers = halves[0].Split(':');
        if (halves.Length != 2 || numbers.Length != 3) throw new ArgumentException("画面检测资源无效。");
        var window = halves[1] == "*" ? ["", ""] : halves[1].Split('-');
        if (window.Length != 2) throw new ArgumentException("检测时段资源无效。");
        var fields = new Dictionary<string, string> { ["enterPercent"] = numbers[0], ["exitPercent"] = numbers[1], ["stableSeconds"] = numbers[2], ["start"] = window[0], ["end"] = window[1] };
        var spec = Validate(new(Bright, 1, fields.ToImmutableDictionary()));
        if (spec.Resource != resource) throw new ArgumentException("画面检测资源格式无效。");
        return spec;
    }
}
