using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public sealed record DailyTimeSpec(int Hour, int Minute, int Days)
{
    public string Resource => FormattableString.Invariant($"{Hour:00}:{Minute:00}/{Days:X2}");
    public DateTimeOffset Next(DateTimeOffset after, TimeZoneInfo zone, DateOnly? lastFired = null)
    {
        if (Hour is < 0 or > 23 || Minute is < 0 or > 59 || Days is < 1 or > 127) throw new ArgumentException("定时参数无效。");
        var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime);
        for (var offset = 0; offset < 15; offset++)
        {
            var date = start.AddDays(offset);
            if (lastFired is { } fired && date <= fired || (Days & (1 << (int)date.DayOfWeek)) == 0) continue;
            var local = date.ToDateTime(new TimeOnly(Hour, Minute), DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local)) continue;
            // Use the first occurrence in a repeated local hour, never the second one.
            var utc = zone.IsAmbiguousTime(local)
                ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime()
                : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
            if (utc > after) return utc;
        }
        throw new InvalidOperationException("当前时区内未找到下一次有效定时，请检查系统日期和时区。");
    }
}
public static class DailyTimeCapabilities
{
    public const string Due = "time.daily";
    private static readonly string[] DayNames = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
    public static TaskCapability Descriptor { get; } = new(Due, 1, TaskCapabilityKind.Detection, "时间", "每天 / 每周定时",
        "按Windows当前本地时区，在选中的星期和时刻执行一次。",
        [new("hour", "小时（24小时制）", TaskFieldKind.Number, true, "0–23的整数。", "9", 2, [], Minimum: 0, Maximum: 23),
         new("minute", "分钟", TaskFieldKind.Number, true, "0–59的整数。", "0", 2, [], Minimum: 0, Maximum: 59),
         .. Enumerable.Range(0, 7).Select(index => (index + 1) % 7).Select(day => new TaskField("day" + day, DayNames[day], TaskFieldKind.Boolean, true,
             "至少选一天，默认周一至周五。", day is >= 1 and <= 5 ? "true" : "false", 5, [], Group: "重复日期"))],
        "只等待下一个未来时刻，UI退出仍由后台执行。时钟/系统设置变化或恢复后重算，不补发错过的定时；允许一分钟调度延迟，更晚则跳过。夏令时不存在的时刻跳过，重复时刻只取第一次。",
        "不唤醒电脑。同一检测会话已执行的日期不重复；回拨日期可能跳过机会，重启不保存已触发日期而是重新等待未来时刻。暂停/重连不补发，忙时不排队补执行，不提供日历节假日判断。");
    public static DailyTimeSpec Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Due || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 9 ||
            !invocation.Parameters.TryGetValue("hour", out var hour) || !invocation.Parameters.TryGetValue("minute", out var minute) ||
            !int.TryParse(hour, NumberStyles.None, CultureInfo.InvariantCulture, out var h) || h is < 0 or > 23 ||
            !int.TryParse(minute, NumberStyles.None, CultureInfo.InvariantCulture, out var m) || m is < 0 or > 59) throw new ArgumentException("请填写有效的整数小时和分钟。");
        var days = 0;
        for (var day = 0; day < 7; day++)
        {
            if (!invocation.Parameters.TryGetValue("day" + day, out var enabled) || enabled is not ("true" or "false")) throw new ArgumentException("定时星期选择无效。");
            if (enabled == "true") days |= 1 << day;
        }
        if (days == 0) throw new ArgumentException("请至少选择一个星期日期。");
        return new(h, m, days);
    }
    public static DailyTimeSpec ParseResource(string value)
    {
        if (value.Length != 8 || value[2] != ':' || value[5] != '/' ||
            !int.TryParse(value.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var h) || h is < 0 or > 23 ||
            !int.TryParse(value.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m) || m is < 0 or > 59 ||
            !int.TryParse(value.AsSpan(6, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var days) || days is < 1 or > 127)
            throw new ArgumentException("定时检测资源无效。");
        var spec = new DailyTimeSpec(h, m, days);
        if (spec.Resource != value) throw new ArgumentException("定时检测资源格式无效。");
        return spec;
    }
    public static string Label(string resource)
    {
        var spec = ParseResource(resource);
        var days = spec.Days == 127 ? "每天" : spec.Days == 62 ? "周一至周五" : string.Join("、", Enumerable.Range(0, 7).Where(d => (spec.Days & (1 << d)) != 0).Select(d => DayNames[d]));
        return $"{days} {spec.Hour:00}:{spec.Minute:00}";
    }
}
