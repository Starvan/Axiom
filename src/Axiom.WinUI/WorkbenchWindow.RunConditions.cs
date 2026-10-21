using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private CancellationTokenSource? runStatesStop;
    private bool runStatesFailed;

    private TaskRunStatus? RunState(AutomationTask task) => runStatesFailed || status is null || status.RunStates.IsDefault
        ? null : status.RunStates.FirstOrDefault(state => state.TaskId == task.Id);
    private string TaskStateLabel(AutomationTask task) => task.Paused ? "手动暂停" : task.RunConditions is null ? "已启用" :
        RunState(task) is not { } state ? "状态未确认" : state.AutomaticallyPaused ? "自动暂停" : "已启用";

    private UIElement BuildRunConditionsEditor()
    {
        var current = editor.RunConditions ?? new();
        if (status?.RunConditionsVersion is not >= 1)
            return Text(editor.RunConditions is null ? "" : "后台暂不支持自动暂停，请更新后台后再保存。原设置已保留。", 12, Amber);
        if (FindCapability(editor.Action, TaskCapabilityKind.Action) is { SupportsRunConditions: false })
        {
            var unsupported = Column(8);
            unsupported.Children.Add(Text("此动作暂不支持任务级自动暂停。已填条件会保留，可清除条件或换用支持的动作后保存。", 12, Amber));
            if (editor.RunConditions is not null)
            {
                var clear = Action("清除自动暂停条件");
                clear.Click += (_, _) => { editor.RunConditions = null; Render(); };
                unsupported.Children.Add(clear);
            }
            return unsupported;
        }
        var panel = Column(12);
        panel.Children.Add(Help("条件不满足时，整个任务自动暂停，内部检测停止。条件满足后自动恢复；手动暂停的任务不会自动启用。"));
        var hdr = new ComboBox { Header = "HDR 自动暂停（主显示器）", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "不根据 HDR 暂停", "HDR 开启时自动暂停", "HDR 关闭时自动暂停" },
            SelectedIndex = current.Hdr == "off" ? 1 : current.Hdr == "on" ? 2 : 0 };
        AutomationProperties.SetName(hdr, "HDR 自动暂停（主显示器）"); panel.Children.Add(hdr);
        var toggle = new ToggleSwitch { Header = "只在指定时段运行", IsOn = current.TimeWindow is not null,
            OnContent = "时段外自动暂停", OffContent = "不按时间暂停" };
        panel.Children.Add(toggle);
        var times = Column(10); var window = current.TimeWindow ?? new WeeklyTimeWindow(1200, 480, 127);
        var start = new TimePicker { Header = "允许运行的开始时间", ClockIdentifier = "24HourClock", Time = TimeSpan.FromMinutes(window.StartMinute) };
        var end = new TimePicker { Header = "允许运行的结束时间", ClockIdentifier = "24HourClock", Time = TimeSpan.FromMinutes(window.EndMinute) };
        AutomationProperties.SetName(start, "自动暂停 开始时间"); AutomationProperties.SetName(end, "自动暂停 结束时间");
        times.Children.Add(SideBySide(start, end));
        var days = Enumerable.Range(0, 7).Select(i => (i + 1) % 7).Select(day =>
        {
            var box = new CheckBox { Content = WeekdayLabels[day], IsChecked = (window.Days & (1 << day)) != 0 };
            AutomationProperties.SetName(box, "自动暂停 " + WeekdayLabels[day]); return (Day: day, Box: box);
        }).ToArray();
        var week = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 4 };
        foreach (var day in days) week.Children.Add(day.Box);
        times.Children.Add(week);
        times.Children.Add(Help("按电脑本地时间；结束早于开始表示跨午夜，后半段属于开始当天。恢复时重新读取状态，暂停期间错过的触发不会补执行。"));
        times.Visibility = toggle.IsOn ? Visibility.Visible : Visibility.Collapsed; panel.Children.Add(times);
        void SaveDraft()
        {
            times.Visibility = toggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
            var mode = hdr.SelectedIndex == 1 ? "off" : hdr.SelectedIndex == 2 ? "on" : "any";
            var selectedWindow = toggle.IsOn ? new WeeklyTimeWindow((int)start.Time.TotalMinutes, (int)end.Time.TotalMinutes,
                days.Where(day => day.Box.IsChecked == true).Aggregate(0, (mask, day) => mask | (1 << day.Day))) : null;
            editor.RunConditions = mode == "any" && selectedWindow is null ? null : new(mode, selectedWindow);
        }
        hdr.SelectionChanged += (_, _) => SaveDraft(); toggle.Toggled += (_, _) => SaveDraft();
        start.TimeChanged += (_, _) => SaveDraft(); end.TimeChanged += (_, _) => SaveDraft();
        foreach (var day in days) { day.Box.Checked += (_, _) => SaveDraft(); day.Box.Unchecked += (_, _) => SaveDraft(); }
        return new Expander { Header = "自动暂停", Content = panel, IsExpanded = editor.RunConditions is not null,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    private void SyncRunStatesWatch()
    {
        var needed = !closed && reachable && page == Page.Rules && !editing && status?.RunConditionsVersion >= 1;
        if (!needed)
        {
            StopRunStatesWatch(); return;
        }
        if (runStatesStop is not null) return;
        var lifetime = new CancellationTokenSource(); runStatesStop = lifetime; runStatesFailed = false;
        _ = WatchRunStatesAsync(lifetime);
    }
    private void StopRunStatesWatch()
    {
        var previous = runStatesStop; runStatesStop = null;
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
    }
    private async Task WatchRunStatesAsync(CancellationTokenSource lifetime)
    {
        try
        {
            await foreach (var states in WorkbenchClient.WatchRunStatesAsync(lifetime.Token))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (closed || lifetime.IsCancellationRequested || !ReferenceEquals(runStatesStop, lifetime) || status is null) return;
                    var updated = states.ToImmutableArray();
                    var changed = status.RunStates.IsDefault || !status.RunStates.SequenceEqual(updated);
                    status = status with { RunStates = updated }; runStatesFailed = false;
                    if (changed) Render();
                });
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (closed || lifetime.IsCancellationRequested || !ReferenceEquals(runStatesStop, lifetime)) return;
                runStatesFailed = true; ShowNotice("自动暂停状态连接中断，请刷新核对。"); Render();
            });
        }
        finally { lifetime.Dispose(); }
    }
}
