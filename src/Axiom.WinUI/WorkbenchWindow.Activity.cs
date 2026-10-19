using Axiom.Contracts;
using Axiom.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private ActivityPage? activity;
    private string? activityError;
    private long? activityBeforeId;
    private int activityPageSize;
    private readonly Stack<long?> activityPositions = [];
    private bool activityScrollToTop;
    private bool activityResizeQueued;

    private int CalculateActivityPageSize()
    {
        var height = pageScroll.ViewportHeight;
        var width = pageScroll.ViewportWidth;
        if (!double.IsFinite(height) || height <= 0) return 4;
        // A card row contains a title, detail and metadata. Reserve the heading,
        // toolbar and date labels so a page contains only complete visible rows.
        var rowHeight = width < 760 ? 104 : 92;
        var available = Math.Max(200, height - 180);
        return Math.Clamp((int)Math.Floor(available / rowHeight), 1, 64);
    }

    private void QueueActivityViewportRefresh()
    {
        if (!loaded || closed || page != Page.Activity || busy || activityResizeQueued) return;
        var next = CalculateActivityPageSize();
        if (activityPageSize == 0 || next == activityPageSize) return;
        activityResizeQueued = true;
        DispatcherQueue.TryEnqueue(async () =>
        {
            activityResizeQueued = false;
            if (closed || page != Page.Activity || busy) return;
            activityPositions.Clear();
            await ReadActivityCoreAsync(null, reset: true);
        });
    }

    private void BuildActivity()
    {
        var toolbar = Column(12);
        var actions = new Grid { ColumnSpacing = 12 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.Children.Add(RefreshButton());
        var clear = Action("清除 7 天前的记录");
        clear.IsEnabled = reachable && activity is not null;
        clear.Click += async (_, _) => await ClearOldActivityAsync();
        Grid.SetColumn(clear, 1); actions.Children.Add(clear);
        toolbar.Children.Add(actions);
        ToolTipService.SetToolTip(clear, "保留最近七天的记录，清理后无法撤销");
        content.Children.Add(toolbar);

        if (activityError is not null || activity?.Error is not null || !reachable)
            content.Children.Add(Card(Text(activityError ?? activity?.Error ?? "后台未连接，下面保留的是上次读取的记录。", 13, Amber), PaleAmber, 16));
        if (activity?.Records.Length is not > 0)
        {
            var empty = Column(12);
            empty.Children.Add(new FontIcon { Glyph = "\uE81C", FontSize = 30, Foreground = Muted,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) });
            empty.Children.Add(Text(activity is null ? "活动记录尚未读取" : "这里还没有活动", 22, Ink, true));
            empty.Children.Add(Text(activity is null ? "连接后台后点击刷新，即可查看已保存的记录。" :
                activityBeforeId is null ? "检测、执行与配置操作发生后，会按时间显示在这里。" : "更早的记录已被清理，可返回查看较新的活动。", 14, Muted));
            content.Children.Add(Card(empty, Surface, 28));
        }
        else
        {
            if (activityPositions.Count > 0 || activity.BeforeId is not null) content.Children.Add(ActivityPager());
            // Group consecutive dates so each page follows the durable event order.
            DateOnly? date = null;
            StackPanel? rows = null;
            foreach (var record in activity.Records)
            {
                var local = record.Event.AtUtc.ToLocalTime();
                var nextDate = DateOnly.FromDateTime(local.DateTime);
                if (date != nextDate)
                {
                    date = nextDate;
                    var section = Column(10);
                    section.Children.Add(Text(local.ToString("yyyy 年 M 月 d 日"), 13, Muted, true));
                    rows = Column(0); section.Children.Add(Card(rows, Surface, 0)); content.Children.Add(section);
                }
                var entry = record.Event;
                var warning = entry.Level is ActivityLevel.Warning or ActivityLevel.Error;
                var row = new Grid { ColumnSpacing = 14, Padding = new Thickness(16, 10, 16, 10) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var icon = new FontIcon { Glyph = entry.Level switch { ActivityLevel.Success => "\uE73E",
                    ActivityLevel.Warning => "\uE7BA", ActivityLevel.Error => "\uEA39", _ => "\uE946" },
                    FontSize = 16, Foreground = warning ? Amber : entry.Level == ActivityLevel.Success ? Accent : Muted,
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
                AutomationProperties.SetName(icon, entry.Level switch { ActivityLevel.Success => "完成", ActivityLevel.Warning => "提醒",
                    ActivityLevel.Error => "异常", _ => "活动" });
                row.Children.Add(icon);
                var body = Column(6);
                body.Children.Add(Preview(entry.Title, 14, Ink, true));
                if (!string.IsNullOrEmpty(entry.Subject)) body.Children.Add(Preview(entry.Subject, 12, Accent));
                if (!string.IsNullOrWhiteSpace(entry.Detail))
                {
                    if (warning) body.Children.Add(Text(entry.Detail, 13, Amber));
                    else
                    {
                        var detail = new Grid { ColumnSpacing = 8 };
                        detail.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        detail.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        var preview = Preview(entry.Detail, 13, Muted); preview.VerticalAlignment = VerticalAlignment.Center;
                        detail.Children.Add(preview);
                        var help = Help(entry.Detail, "活动详情"); Grid.SetColumn(help, 1); detail.Children.Add(help);
                        body.Children.Add(detail);
                    }
                }
                body.Children.Add(Text($"{local:HH:mm:ss}  ·  {entry.Category}", 11, Muted));
                Grid.SetColumn(body, 1); row.Children.Add(body);
                rows!.Children.Add(new Border { Child = row, BorderBrush = Line,
                    BorderThickness = new Thickness(0, rows.Children.Count == 0 ? 0 : 1, 0, 0) });
            }
        }
        if (activity is not null && (activityPositions.Count > 0 || activity.BeforeId is not null)) content.Children.Add(ActivityPager());
    }

    private Grid ActivityPager()
    {
        var pager = new Grid { ColumnSpacing = 8 };
        pager.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pager.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var newer = Action("← 较新记录"); newer.IsEnabled = reachable && activityPositions.Count > 0;
        newer.Click += async (_, _) => await ChangeActivityPageAsync(older: false); pager.Children.Add(newer);
        var older = Action("更早记录 →"); older.IsEnabled = reachable && activity?.BeforeId is not null;
        older.Click += async (_, _) => await ChangeActivityPageAsync(older: true);
        Grid.SetColumn(older, 1); pager.Children.Add(older);
        return pager;
    }

    private async Task<bool> ReadActivityCoreAsync(long? beforeId, bool reset)
    {
        try
        {
            var pageSize = beforeId is null || activityPageSize == 0 ? CalculateActivityPageSize() : activityPageSize;
            var response = await WorkbenchClient.RequestAsync(new(1, "read-activity", Guid.NewGuid().ToString("N"),
                ActivityBeforeId: beforeId, ActivityPageSize: pageSize));
            if (response.Type != "activity" || response.Activity is null) throw new InvalidDataException(response.Error ?? "活动记录回复未确认。");
            activity = response.Activity; activityBeforeId = beforeId; activityError = null;
            activityPageSize = pageSize;
            activityScrollToTop = true;
            if (reset) activityPositions.Clear();
            Render(); return true;
        }
        catch (Exception) { activityError = "活动记录读取失败，请刷新重试。上次读取的记录仍保留。"; Render(); return false; }
    }

    private async Task ChangeActivityPageAsync(bool older)
    {
        if (busy || closed) return;
        var target = older ? activity?.BeforeId : activityPositions.Count > 0 ? activityPositions.Peek() : null;
        if (older && target is null || !older && activityPositions.Count == 0) return;
        var previous = activityBeforeId;
        SetBusy(true);
        try
        {
            if (await ReadActivityCoreAsync(target, reset: false))
            {
                if (older) activityPositions.Push(previous); else activityPositions.Pop();
                Render();
            }
        }
        finally { SetBusy(false); }
    }

    private async Task ClearOldActivityAsync()
    {
        if (busy || closed) return;
        SetBusy(true);
        try
        {
            var response = await WorkbenchClient.RequestAsync(new(1, "clear-old-activity", Guid.NewGuid().ToString("N")));
            if (response.Type != "activity-cleared" || response.ActivityCleanup is null)
                throw new InvalidDataException(response.Error ?? "清理结果未确认，请刷新核对。");
            await ReadActivityCoreAsync(null, reset: true);
            ShowNotice(response.ActivityCleanup.DeletedCount == 0 ? "没有七天前的记录，最近七天的活动已保留。" :
                $"已清除 {response.ActivityCleanup.DeletedCount} 条七天前的记录，最近七天的活动已保留。", true);
        }
        catch (Exception error) { ShowNotice("清理结果未确认：" + error.Message); }
        finally { SetBusy(false); }
    }
}
