using Axiom.Contracts;
using Axiom.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow : Window
{
    private enum Page { Rules, Groups, Toolbox, Usage, Activity, Settings }
    private static SolidColorBrush Color(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));
    private static readonly SolidColorBrush Canvas = Color(246, 247, 249);
    private static readonly SolidColorBrush Surface = Color(255, 255, 255);
    private static readonly SolidColorBrush Primary = Color(36, 99, 83);
    private static readonly SolidColorBrush OnPrimary = Color(255, 255, 255);
    private static readonly SolidColorBrush Ink = Color(30, 38, 47);
    private static readonly SolidColorBrush Muted = Color(99, 110, 122);
    private static readonly SolidColorBrush Line = Color(226, 230, 234);
    private static readonly SolidColorBrush Accent = Color(36, 99, 83);
    private static readonly SolidColorBrush Soft = Color(233, 243, 238);
    private static readonly SolidColorBrush Amber = Color(139, 89, 20);
    private static readonly SolidColorBrush PaleAmber = Color(255, 246, 227);
    private static readonly SolidColorBrush Rail = Color(24, 34, 47);
    private static readonly SolidColorBrush RailInk = Color(233, 240, 247);
    private static readonly SolidColorBrush RailMuted = Color(151, 169, 188);
    private static readonly SolidColorBrush RailSelected = Color(40, 66, 69);
    private static readonly SolidColorBrush RailAccent = Color(148, 228, 197);
    private static readonly SolidColorBrush Blue = Color(65, 103, 168);
    private static readonly SolidColorBrush BlueSoft = Color(234, 240, 251);
    private static readonly SolidColorBrush Hover = Color(245, 248, 251);
    private readonly FontIcon pageGlyph = new() { Glyph = "\uE8A5", FontSize = 22, Foreground = Accent };
    private StackPanel content = null!;
    private readonly ContentControl contentHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock pageTitle = Text("", 28, Ink, true);
    private readonly TextBlock pageSubtitle = Text("", 14, Muted);
    private readonly TextBlock connection = Text("正在连接…", 12, Muted);
    private readonly InfoBar notice = new() { IsOpen = false, IsClosable = true };
    private readonly Dictionary<Page, Button> navigation = [];
    private StackPanel footer = Row(10);
    private readonly ContentControl footerHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private ContentControl shell = null!;
    private Grid shellGrid = null!;
    private ScrollViewer pageScroll = null!;
    private StackPanel sideNote = null!;
    private AutomationSnapshot? status;
    private LoginStartupStatus? startup;
    private string? startupError;
    private ShortcutSnapshot? shortcuts;
    private string? shortcutError;
    private Page page = Page.Rules;
    private readonly TaskEditorState editor = new();
    private bool editing, loaded, busy, closed, reachable, closingConfirmed, confirmingClose, renderQueued;
    private DateTimeOffset? readAt;

    public WorkbenchWindow()
    {
        Title = "Axiom";
        AppWindow.Resize(new SizeInt32(1160, 820));
        shell = BuildShell();
        Content = shell;
        InitializeTheme();
        Closed += (_, _) => { closed = true; pendingUsageLease?.Dispose(); usageLease?.Dispose(); StopRunStatesWatch(); };
        AppWindow.Closing += async (_, args) =>
        {
            if (closingConfirmed) return;
            if (busy) { args.Cancel = true; return; }
            if (activeDialog is not null) { args.Cancel = true; activeDialog.Hide(); return; }
            if (!Dirty) return;
            args.Cancel = true;
            if (confirmingClose) return;
            confirmingClose = true;
            try { if (await ConfirmDiscardAsync()) { closingConfirmed = true; Close(); } }
            finally { confirmingClose = false; }
        };
        Activated += async (_, _) => { if (loaded) return; loaded = true; await RefreshAsync(); };
        Render();
    }

    private bool Dirty => editing && editor.Dirty || groupEditing && GroupDirty;
    private static TextBlock Text(string value, int size = 14, SolidColorBrush? brush = null, bool bold = false) =>
        new() { Text = value, FontSize = size, Foreground = brush ?? Ink, FontWeight = new Windows.UI.Text.FontWeight { Weight = (ushort)(bold ? 600 : 400) }, TextWrapping = TextWrapping.Wrap };
    private static StackPanel Column(int spacing = 10) => new() { Spacing = spacing };
    private static StackPanel Row(int spacing = 10) => new() { Orientation = Orientation.Horizontal, Spacing = spacing };
    private static Border Card(UIElement child, SolidColorBrush? background = null, int padding = 20) => new()
    {
        Child = child, Background = background ?? Surface, BorderBrush = Line, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14), Padding = new Thickness(padding)
    };
    private static Button Action(string label, bool primary = false) => new()
    {
        Content = label, MinHeight = 38, Padding = new Thickness(16, 8, 16, 8), CornerRadius = new CornerRadius(9),
        Background = primary ? Primary : Surface, Foreground = primary ? OnPrimary : Ink,
        HorizontalAlignment = HorizontalAlignment.Left
    };
    private static Button Help(string message, string label = "了解更多")
    {
        var button = new Button { Content = new FontIcon { Glyph = "\uE946", FontSize = 14, Foreground = Muted },
            Padding = new Thickness(6), MinWidth = 28, MinHeight = 28,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, message);
        button.Flyout = new Flyout { Content = new ScrollViewer { MaxHeight = 300,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, IsTextSelectionEnabled = true } } };
        return button;
    }
    private static Expander Details(string label, UIElement child) => new() { Header = label, Content = child,
        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private static TextBlock Preview(string value, int size = 13, SolidColorBrush? brush = null, bool bold = false)
    {
        var text = Text(value, size, brush, bold); text.MaxLines = 1; text.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTipService.SetToolTip(text, value); return text;
    }
    private static Grid ToolHeading()
    {
        var heading = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        heading.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        return heading;
    }
    private static void PlaceToolToggle(Grid heading, ToggleSwitch toggle)
    {
        AutomationProperties.SetName(toggle, toggle.Header?.ToString() ?? "启用工具");
        toggle.Header = null; toggle.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(toggle);
        void Fit()
        {
            var narrow = heading.ActualWidth < 520;
            Grid.SetRow(toggle, narrow ? 1 : 0); Grid.SetColumn(toggle, narrow ? 1 : 2);
            Grid.SetColumnSpan(toggle, narrow ? 2 : 1);
        }
        heading.SizeChanged += (_, _) => Fit(); Fit();
    }
    private static Border CategoryTile(string? category, int size = 36)
    {
        var program = category is "程序" or "窗口" or "系统" or "Axiom";
        var glyph = category switch { "显示" => "\uE7F4", "程序" => "\uE756", "窗口" => "\uE737", "声音" or "音频" => "\uE767",
            "键盘" => "\uE765", "时间" => "\uE787", "网络" => "\uE774", "电源" => "\uE7E8", "文件夹" => "\uE8B7", _ => "\uE945" };
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(10),
            Background = program ? BlueSoft : Soft, VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon { Glyph = glyph, FontSize = 17, Foreground = program ? Blue : Accent } };
    }
    private static Border Badge(string label, bool paused = false) => new()
    {
        Background = paused ? Canvas : Soft, CornerRadius = new CornerRadius(12), Padding = new Thickness(9, 4, 9, 4),
        HorizontalAlignment = HorizontalAlignment.Left, Child = Text(label, 12, paused ? Muted : Accent, true)
    };
    private ContentControl BuildShell()
    {
        shellGrid = new Grid { Background = Canvas };
        shellGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        shellGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var side = new Grid { Background = Rail, Padding = new Thickness(14, 20, 14, 20) };
        side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var nav = Column(6);
        var navLabel = Text("工作空间", 11, RailMuted, true); navLabel.Margin = new Thickness(14, 0, 0, 14); nav.Children.Add(navLabel);
        AddNavigation(nav, Page.Rules, "自动化", "\uE8A5");
        AddNavigation(nav, Page.Groups, "程序组", "\uE8F1");
        AddNavigation(nav, Page.Toolbox, "工具箱", "\uE90F");
        AddNavigation(nav, Page.Usage, "使用观察", "\uE9D9");
        AddNavigation(nav, Page.Activity, "活动记录", "\uE81C");

        side.Children.Add(nav);
        sideNote = Column(14);
        AddNavigation(sideNote, Page.Settings, "设置", "\uE713");
        connection.Foreground = RailMuted; connection.Margin = new Thickness(12, 0, 0, 0);
        ToolTipService.SetToolTip(connection, "关闭窗口后，已启用的任务继续在后台工作。");
        sideNote.Children.Add(connection);
        Grid.SetRow(sideNote, 1); side.Children.Add(sideNote);
        shellGrid.Children.Add(side);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        pageScroll = scroll;
        pageScroll.SizeChanged += (_, _) => QueueActivityViewportRefresh();
        var body = Column(18);
        body.Padding = new Thickness(32, 24, 32, 28);
        var heading = new Grid { ColumnSpacing = 14, Margin = new Thickness(0, 0, 0, 4) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.Children.Add(new Border { Width = 48, Height = 48, Background = Soft, CornerRadius = new CornerRadius(15),
            VerticalAlignment = VerticalAlignment.Center, Child = pageGlyph });
        var headingText = Column(4); headingText.Children.Add(pageTitle); headingText.Children.Add(pageSubtitle);
        Grid.SetColumn(headingText, 1); heading.Children.Add(headingText);
        body.Children.Add(heading); body.Children.Add(contentHost);
        scroll.Content = body;
        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.Children.Add(scroll);
        var bottom = Column(8); usageStop.Visibility = usageIndicator.Visibility = Visibility.Collapsed; usageStop.Click += async (_, _) => await UsageCommandAsync("stop"); bottom.Children.Add(usageIndicator); bottom.Children.Add(usageStop); bottom.Children.Add(notice); bottom.Children.Add(footerHost);
        bottom.Margin = new Thickness(24, 0, 24, 16);
        Grid.SetRow(bottom, 1); main.Children.Add(bottom);
        Grid.SetColumn(main, 1); shellGrid.Children.Add(main);
        var titleRail = new Border { Width = 200, Background = Rail, HorizontalAlignment = HorizontalAlignment.Left };
        shellGrid.SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width < 850;
            shellGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 150 : 200);
            body.Padding = new Thickness(compact ? 20 : 32, 24, compact ? 20 : 32, 24);
            titleRail.Width = compact ? 150 : 200;
            sideNote.Visibility = Visibility.Visible;
        };
        var title = new Grid { Height = 52, Background = Canvas };
        title.Children.Add(titleRail);
        var brand = Row(10); brand.Margin = new Thickness(24, 0, 0, 0); brand.VerticalAlignment = VerticalAlignment.Center;
        brand.Children.Add(new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/Axiom.png")), Width = 26, Height = 26 });
        brand.Children.Add(Text("Axiom", 17, RailInk, true)); title.Children.Add(brand);
        var root = new Grid { Background = Canvas };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(title); Grid.SetRow(shellGrid, 1); root.Children.Add(shellGrid);
        ExtendsContentIntoTitleBar = true; SetTitleBar(title);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        AppWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Axiom.ico"));
        return new ContentControl { Content = root, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    }
    private void AddNavigation(StackPanel panel, Page target, string label, string glyph)
    {
        var button = Action(label);
        button.MinHeight = 44; button.Padding = new Thickness(8, 8, 12, 8);
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(19) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new Border { Width = 3, Height = 20, CornerRadius = new CornerRadius(2),
            Background = RailAccent, Opacity = 0, VerticalAlignment = VerticalAlignment.Center });
        var icon = new FontIcon { Glyph = glyph, FontSize = 17, Foreground = RailMuted }; Grid.SetColumn(icon, 1); row.Children.Add(icon);
        var caption = Text(label, 14, RailInk); caption.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(caption, 2); row.Children.Add(caption);
        button.Content = row; button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.BorderThickness = new Thickness(0);
        button.Resources["ButtonBackgroundPointerOver"] = RailSelected;
        button.Resources["ButtonBackgroundPressed"] = RailSelected;
        button.Resources["ButtonForegroundPointerOver"] = RailInk;
        button.Resources["ButtonForegroundPressed"] = RailInk;
        AutomationProperties.SetName(button, label);
        button.Click += async (_, _) =>
        {
            if (busy || !await ConfirmDiscardAsync()) return;
            editing = false; groupEditing = false; viewingProcessGroups = false; page = target; notice.IsOpen = false; Render(); await RefreshAsync();
        };
        navigation[target] = button; panel.Children.Add(button);
    }
    private void Render()
    {
        // Leave the input/accessibility callback before replacing its visual tree.
        if (closed || renderQueued) return;
        renderQueued = true;
        DispatcherQueue.TryEnqueue(() => { renderQueued = false; RenderCore(); });
    }
    private void RenderCore()
    {
        if (closed) return;
        SyncRunStatesWatch();
        foreach (var (key, button) in navigation)
        {
            var selected = key == page;
            button.Background = selected ? RailSelected : Rail;
            if (button.Content is Grid row)
            {
                row.Children[0].Opacity = selected ? 1 : 0;
                ((FontIcon)row.Children[1]).Foreground = selected ? RailAccent : RailMuted;
                ((TextBlock)row.Children[2]).Foreground = selected ? RailAccent : RailInk;
            }
        }
        pageGlyph.Glyph = page switch { Page.Rules => "\uE8A5", Page.Groups => "\uE8F1", Page.Toolbox => "\uE90F",
            Page.Usage => "\uE9D9", Page.Activity => "\uE81C", _ => "\uE713" };
        (pageTitle.Text, pageSubtitle.Text) = page switch
        {
            Page.Rules when editing => (editor.Version == 0 ? "新建任务" : "编辑任务", "选择检测什么，再决定执行什么。"),
            Page.Rules => ("我的自动化", "设定一次，日常交给 Axiom。"),
            Page.Groups when groupEditing => (groupDraft?.Version == 0 ? "新建进程组" : "编辑进程组", "整理一次，配置任务时随手选择。"),
            Page.Groups when viewingProcessGroups => ("进程组", "整理程序名单，在不同任务中重复使用。"),
            Page.Groups => ("程序组", "把常用选择放在一起。"),
            Page.Toolbox => ("工具箱", "省去重复整理的时间。"),
            Page.Usage => ("使用观察", "主动记录一段过程，发现值得自动化的环节。"),
            Page.Activity => ("活动记录", "查看执行结果与需要留意的事。"),
            _ => ("设置", "按你的习惯使用 Axiom。")
        };
        connection.Text = reachable ? "●  后台已连接" : "○  后台状态未确认";
        // Build a detached page and replace it once. Clearing the live child vector
        // can invalidate WinUI accessibility peers while an edit invocation is returning.
        content = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Stretch };
        footer = Row(10);
        footer.IsHitTestVisible = !busy;
        footer.Visibility = editing && page == Page.Rules || groupEditing && page == Page.Groups ? Visibility.Visible : Visibility.Collapsed;
        content.MaxWidth = editing && page == Page.Rules ? 780 : double.PositiveInfinity;
        switch (page) { case Page.Rules: if (editing) BuildEditor(); else BuildRules(); break; case Page.Groups: BuildGroups(); break;
            case Page.Toolbox: BuildToolbox(); break; case Page.Usage: BuildUsage(); break; case Page.Activity: BuildActivity(); break; default: BuildSettings(); break; }
        contentHost.Content = content;
        footerHost.Content = footer;
        if (activityScrollToTop && page == Page.Activity)
        {
            activityScrollToTop = false;
            DispatcherQueue.TryEnqueue(() => pageScroll.ChangeView(null, 0, null, true));
        }
    }
    private void ShowNotice(string message, bool success = false)
    {
        notice.Message = message; notice.Severity = success ? InfoBarSeverity.Success : InfoBarSeverity.Warning; notice.IsOpen = true;
    }
    private void SetBusy(bool value)
    {
        busy = value;
        // Keep the navigation rail stable while a page refresh is in flight.
        // Disabling the whole shell briefly applies WinUI's disabled opacity and
        // caused a visible flash in dark mode when switching tabs.
        // IsHitTestVisible prevents duplicate clicks without applying WinUI's
        // disabled visual state (StackPanel does not expose IsEnabled here).
        pageScroll.IsHitTestVisible = !value;
        footer.IsHitTestVisible = !value;
    }
    private async Task<bool> ConfirmAsync(string title, string message, string action) => await ShowDialogAsync(new ContentDialog
    {
        Title = title, Content = message, PrimaryButtonText = action, CloseButtonText = "取消",
        DefaultButton = ContentDialogButton.Close, XamlRoot = shell.XamlRoot
    }) == ContentDialogResult.Primary;
    private Task<bool> ConfirmDiscardAsync() => !Dirty ? Task.FromResult(true) : ConfirmAsync("放弃未保存的更改？", "已保存的任务和进程组不会改变。", "放弃更改");
    private void SelectRule(AutomationTask? rule)
    {
        editor.Load(rule);
        editing = true; notice.IsOpen = false; Render();
    }
    private async Task RefreshAsync()
    {
        if (busy || closed) return;
        SetBusy(true);
        try
        {
            var response = await WorkbenchClient.RequestAsync(new(1, "read", Guid.NewGuid().ToString("N")));
            if (response.Type != "snapshot" || response.Snapshot is null) throw new InvalidDataException(response.Error);
            status = response.Snapshot; reachable = true; readAt = DateTimeOffset.Now;
            if (runStatesFailed) { StopRunStatesWatch(); runStatesFailed = false; }
            await ReadGroupsAsync();
            if (page == Page.Toolbox) { await ReadSteamToolAsync(false); await ReadTrayToolAsync(); await ReadBrightnessWheelAsync(); }
            if (page == Page.Usage) await ReadUsageAsync();
            Render();
            if (page == Page.Activity) await ReadActivityCoreAsync(null, reset: true);
            if (page == Page.Settings)
            {
                startup = null; startupError = null;
                try
                {
                    var setting = await WorkbenchClient.RequestAsync(new(1, "read-startup", Guid.NewGuid().ToString("N")));
                    if (setting.Type != "startup" || setting.Startup is null) throw new InvalidDataException();
                    startup = setting.Startup; startupError = startup.Error;
                }
                catch (Exception) { startupError = "登录启动设置未确认，请刷新后重试。"; }
                shortcuts = null; shortcutError = null;
                try
                {
                    var responseKeys = await WorkbenchClient.RequestAsync(new(1, "read-shortcuts", Guid.NewGuid().ToString("N")));
                    if (responseKeys.Type != "shortcuts" || responseKeys.Shortcuts is null) throw new InvalidDataException();
                    shortcuts = responseKeys.Shortcuts;
                }
                catch (Exception) { shortcutError = "快捷键状态未确认，请刷新后重试。"; }
                Render();
            }
        }
        catch (Exception) { reachable = false; Render(); ShowNotice("暂时无法连接后台。请重新打开 Axiom，再点击刷新。下面保留的是上次读取的内容。"); }
        finally { SetBusy(false); }
    }
    private async Task SaveAsync()
    {
        if (busy || closed) return;
        if (string.IsNullOrWhiteSpace(editor.Title) || !EditorCanSave)
        { ShowNotice("请填写名称，并补齐有效检测条件和兼容动作。"); return; }
        await MutateAsync(new(1, "save", Guid.NewGuid().ToString("N"), Task: editor.Build()), "saved", "任务已保存。", true);
    }
    private async Task ToggleRuleAsync(AutomationTask rule)
    {
        if (busy || closed) return;
        var warning = ActionWarning(rule.Action);
        if (!rule.Paused && !string.IsNullOrWhiteSpace(warning) &&
            !await ConfirmAsync("暂停这个任务？", warning, "暂停")) return;
        await MutateAsync(new(1, "save", Guid.NewGuid().ToString("N"), Task: rule with { Paused = !rule.Paused }), "saved", rule.Paused ? "已启用，等待检测条件。" : "已暂停。设置已保留。", false);
    }
    private async Task DeleteAsync()
    {
        if (busy || closed || editor.Version == 0) return;
        if (!await ConfirmAsync("删除这个任务？", "删除后停止检测。" + (editor.Action is null ? "" : ActionWarning(editor.Action)), "删除")) return;
        await MutateAsync(new(1, "delete", Guid.NewGuid().ToString("N"), Id: editor.Id, ExpectedVersion: editor.Version), "deleted", "任务已删除。", true);
    }
    private async Task MutateAsync(AutomationMessage request, string expected, string message, bool leaveEditor)
    {
        SetBusy(true);
        try
        {
            var response = await WorkbenchClient.RequestAsync(request);
            if (response.Type != expected || response.Snapshot is null)
            { ShowNotice(WorkbenchPresentation.SaveError(response.Error)); return; }
            status = response.Snapshot; reachable = true; readAt = DateTimeOffset.Now;
            if (leaveEditor) editing = false;
            Render(); ShowNotice(message, true);
        }
        catch (Exception) { reachable = false; Render(); ShowNotice("暂未收到保存结果，输入已保留。请刷新并核对已保存的内容，避免重复操作。"); }
        finally { SetBusy(false); }
    }
    private async Task StopBackendAsync()
    {
        if (busy || closed || !await ConfirmAsync("停止后台并退出？", "所有任务将停止检测，直到下次手动打开或登录自动启动。请先让正在执行的任务正常结束。", "停止并退出")) return;
        SetBusy(true);
        try
        {
            var response = await WorkbenchClient.RequestAsync(new(1, "shutdown", Guid.NewGuid().ToString("N")));
            if (response.Type != "stopping") { ShowNotice("后台未停止。请先让正在执行的任务正常结束，再重试。"); return; }
            closingConfirmed = true; Close();
        }
        catch (Exception) { ShowNotice("未收到停止确认。请刷新检查后台状态。"); }
        finally { SetBusy(false); }
    }

    private async Task SetLoginStartupAsync(bool enabled)
    {
        if (busy || closed) return;
        SetBusy(true);
        try
        {
            var response = await WorkbenchClient.RequestAsync(new(1, "set-startup", Guid.NewGuid().ToString("N"), StartupEnabled: enabled));
            if (response.Type != "startup" || response.Startup is null)
            {
                startupError = response.Error is { Length: > 0 } ? response.Error : "未能保存登录启动设置，请刷新后核对。";
                ShowNotice(startupError); return;
            }
            startup = response.Startup; startupError = startup.Error;
            ShowNotice(enabled ? "已登记登录启动，下次登录时在后台运行。" : "已关闭登录启动，当前后台继续工作。", true);
        }
        catch (Exception) { startup = null; startupError = "暂未收到设置结果，请刷新核对登录启动设置。"; ShowNotice(startupError); }
        finally { Render(); SetBusy(false); }
    }
}
