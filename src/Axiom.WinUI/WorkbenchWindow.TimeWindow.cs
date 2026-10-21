using Axiom.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private static readonly string[] WeekdayLabels = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
    private static string TimeWindowLabel(WeeklyTimeWindow window) =>
        string.Join("、", Enumerable.Range(0, 7).Select(i => (i + 1) % 7).Where(d => (window.Days & (1 << d)) != 0).Select(d => WeekdayLabels[d])) +
        $" {window.StartMinute / 60:00}:{window.StartMinute % 60:00}–{(window.EndMinute < window.StartMinute ? "次日" : "")}{window.EndMinute / 60:00}:{window.EndMinute % 60:00} 接受新触发";

    private StackPanel BuildTimeWindowEditor(bool supported)
    {
        var panel = Column(10);
        if (!supported && editor.TimeWindow is null) return panel;
        var toggle = new ToggleSwitch { Header = "只在选定时段执行", IsOn = editor.TimeWindow is not null,
            OnContent = "限制新触发与开始执行", OffContent = "不限制时间" };
        panel.Children.Add(toggle);
        var settings = Column(10);
        var current = editor.TimeWindow ?? new WeeklyTimeWindow(540, 1080, 62);
        var start = new TimePicker { Header = "开始时间", ClockIdentifier = "24HourClock", Time = TimeSpan.FromMinutes(current.StartMinute) };
        var end = new TimePicker { Header = "结束时间（不包含）", ClockIdentifier = "24HourClock", Time = TimeSpan.FromMinutes(current.EndMinute) };
        settings.Children.Add(SideBySide(start, end));
        var days = Enumerable.Range(0, 7).Select(i => (i + 1) % 7)
            .Select(d => (Day: d, Box: new CheckBox { Content = WeekdayLabels[d], IsChecked = (current.Days & (1 << d)) != 0 })).ToArray();
        var dayPanel = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 4 };
        foreach (var day in days) dayPanel.Children.Add(day.Box);
        settings.Children.Add(dayPanel);
        settings.Children.Add(Help("按电脑本地时间；结束早于开始表示跨午夜，至少选一天，起止不能相同。", "时段填写说明"));
        settings.Children.Add(Help("时段外触发跳过，不补执行；排队跨过结束时间也跳过。已经开始的动作不受影响。", "执行时段说明"));
        if (!supported) panel.Children.Add(Text("当前动作不支持时间段限制。请关闭此限制，或选择一次性动作后保存。原设置仍保留。", 12, Amber));
        panel.Children.Add(settings);
        void SaveDraft()
        {
            settings.Visibility = toggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
            editor.TimeWindow = toggle.IsOn ? new((int)start.Time.TotalMinutes, (int)end.Time.TotalMinutes,
                days.Where(d => d.Box.IsChecked == true).Aggregate(0, (mask, d) => mask | (1 << d.Day))) : null;
        }
        settings.Visibility = toggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        toggle.Toggled += (_, _) => SaveDraft();
        start.TimeChanged += (_, _) => SaveDraft(); end.TimeChanged += (_, _) => SaveDraft();
        foreach (var day in days) { day.Box.Checked += (_, _) => SaveDraft(); day.Box.Unchecked += (_, _) => SaveDraft(); }
        return panel;
    }
}
