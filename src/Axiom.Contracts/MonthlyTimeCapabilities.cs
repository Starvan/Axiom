using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public sealed record MonthlyTimeSpec(int Hour, int Minute, uint Days, bool LastDay = false)
{
    public string Dates => string.Join(",", Enumerable.Range(1, 31).Where(day => (Days & (1u << day)) != 0)
        .Select(day => day.ToString(CultureInfo.InvariantCulture)).Concat(LastDay ? ["last"] : []));
    public string Resource => FormattableString.Invariant($"{Hour:00}:{Minute:00}/{Dates}");
    public void Validate()
    {
        if (Hour is < 0 or > 23 || Minute is < 0 or > 59 || (Days & 1) != 0 || Days == 0 && !LastDay)
            throw new ArgumentException("每月定时参数无效。");
    }
    public DateTimeOffset Next(DateTimeOffset after, TimeZoneInfo zone, DateOnly? lastFired = null)
    {
        Validate();
        var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime);
        // A clock rollback never replays dates already emitted in this subscription.
        if (lastFired is { } previous && previous > start) start = previous;
        var month = new DateOnly(start.Year, start.Month, 1);
        for (var offset = 0; offset < 15; offset++)
        {
            var current = month.AddMonths(offset);
            var length = DateTime.DaysInMonth(current.Year, current.Month);
            for (var day = 1; day <= length; day++)
            {
                if ((Days & (1u << day)) == 0 && !(LastDay && day == length)) continue;
                var date = new DateOnly(current.Year, current.Month, day);
                if (lastFired is { } fired && date <= fired) continue;
                var local = date.ToDateTime(new TimeOnly(Hour, Minute), DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(local)) continue;
                var utc = zone.IsAmbiguousTime(local)
                    ? new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime()
                    : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
                if (utc > after) return utc;
            }
        }
        throw new InvalidOperationException("当前时区内未找到下一次有效月度定时，请检查系统日期和时区。");
    }
}
public static class MonthlyTimeCapabilities
{
    public const string Due = "time.monthly";
    public static TaskCapability Descriptor { get; } = new(Due, 1, TaskCapabilityKind.Detection, "时间", "每月指定日期定时",
        "按Windows当前本地时区，在每月选定日期和时刻执行。支持多个日期及月末。",
        [new("dates", "每月日期", TaskFieldKind.Text, true, "逗号分隔1–31，可填last表示最后一天，例如1,15,last。不存在的日期跳过，不提前到月末。", "1,last", 100, []),
         new("hour", "小时（24小时制）", TaskFieldKind.Number, true, "0–23的整数。", "9", 2, [], Minimum: 0, Maximum: 23),
         new("minute", "分钟", TaskFieldKind.Number, true, "0–59的整数。", "0", 2, [], Minimum: 0, Maximum: 59)],
        "只等待下一个未来时刻，UI退出仍由后台执行。时钟、时区变化或恢复后重算，不补发错过机会；允许一分钟调度延迟。夏令时不存在的时刻跳过，重复时刻只取第一次。同一天与last重合只触发一次。",
        "不唤醒电脑，不判断节假日。同一检测会话已触发的日期不重复；重启重新等待未来时刻，不持久保存历史。暂停、重连不补发，忙时不积压动作。");
    public static MonthlyTimeSpec Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Due || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 3 ||
            !invocation.Parameters.TryGetValue("dates", out var dates) ||
            !invocation.Parameters.TryGetValue("hour", out var hour) || !int.TryParse(hour, NumberStyles.None, CultureInfo.InvariantCulture, out var h) ||
            !invocation.Parameters.TryGetValue("minute", out var minute) || !int.TryParse(minute, NumberStyles.None, CultureInfo.InvariantCulture, out var m))
            throw new ArgumentException("请填写每月日期及整数小时、分钟。");
        var (days, last) = ParseDates(dates);
        var spec = new MonthlyTimeSpec(h, m, days, last); spec.Validate(); return spec;
    }
    private static (uint Days, bool Last) ParseDates(string dates)
    {
        if (string.IsNullOrWhiteSpace(dates) || dates.Length > 100) throw new ArgumentException("请填写每月日期，例如1,15,last。");
        uint days = 0; var last = false;
        foreach (var item in dates.Split(','))
        {
            var text = item.Trim();
            if (text == "last") { last = true; continue; }
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var day) || day is < 1 or > 31)
                throw new ArgumentException("每月日期须为逗号分隔的1–31或last（最后一天）。");
            days |= 1u << day;
        }
        return (days, last);
    }
    public static MonthlyTimeSpec ParseResource(string resource)
    {
        if (resource.Length is < 7 or > 106 || resource[2] != ':' || resource[5] != '/' ||
            !int.TryParse(resource.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hour) ||
            !int.TryParse(resource.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minute))
            throw new ArgumentException("每月定时资源无效。");
        var (days, last) = ParseDates(resource[6..]);
        var spec = new MonthlyTimeSpec(hour, minute, days, last); spec.Validate();
        if (spec.Resource != resource) throw new ArgumentException("每月定时资源格式无效。");
        return spec;
    }
    public static string Label(string resource)
    {
        var spec = ParseResource(resource);
        return $"每月 {spec.Dates.Replace("last", "最后一天", StringComparison.Ordinal)} · {spec.Hour:00}:{spec.Minute:00}";
    }
}
