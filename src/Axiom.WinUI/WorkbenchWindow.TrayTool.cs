using Axiom.Contracts;
using Axiom.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private TrayToolSnapshot? trayTool;
    private string? trayToolError;

    private void BuildTrayTool()
    {
        var body = Column(12); var heading = ToolHeading();
        heading.Children.Add(new Border { Width = 40, Height = 40, Background = Soft, CornerRadius = new CornerRadius(10),
            Child = new FontIcon { Glyph = "\uE7F4", FontSize = 20, Foreground = Accent } });
        var headingText = Text(TrayTool.Name, 18, Ink, true); headingText.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(headingText, 1); heading.Children.Add(headingText); body.Children.Add(heading);
        body.Children.Add(Text("多选需要隐藏托盘图标的程序，选择启动时隐藏一次或持续隐藏。保留后台进程。", 14, Muted));
        var toggle = new ToggleSwitch { Header = "启用托盘管理", OnContent = "已启用", OffContent = "已关闭", IsOn = trayTool?.Settings.Enabled == true,
            IsEnabled = reachable && trayTool is { Settings.Executables.Length: > 0 } };
        toggle.Toggled += async (_, _) => { if (trayTool is not null) await SaveTrayToolSettingsAsync(trayTool.Settings with { Enabled = toggle.IsOn }); };
        PlaceToolToggle(heading, toggle);
        if (trayTool is not null)
        {
            body.Children.Add(Text($"{trayTool.Settings.Executables.Length} 个程序 · {TrayModeText(trayTool.Settings.Mode)} · 最多等待 {trayTool.Settings.WaitSeconds} 秒", 13, Muted));
            body.Children.Add(Text(trayTool.State, 13, trayTool.LastStatus is OutcomeStatus.Failed or OutcomeStatus.Unknown or OutcomeStatus.Rejected ? Amber : Muted));
            if (trayTool.LastRunUtc is { } at) body.Children.Add(Text($"上次结果：{TrayResultText(trayTool.LastStatus)} · {at.ToLocalTime():MM-dd HH:mm:ss}（详情见工具设置）", 12, Muted));
        }
        if (trayToolError is not null) body.Children.Add(Text(trayToolError, 12, Amber));
        body.Children.Add(Help("开启或启用时保存名单，会先处理当前图标一次；之后按所选模式运行。关闭窗口不影响，关闭工具会停止未来操作。"));
        var actions = Row(10); var settings = Action("工具设置"); settings.IsEnabled = reachable;
        settings.Click += async (_, _) => await OpenTrayToolSettingsAsync(); actions.Children.Add(settings);
        var hide = Action("立即隐藏"); hide.IsEnabled = reachable && trayTool?.Settings.Enabled == true;
        ToolTipService.SetToolTip(hide, "启用工具后，可手动再处理当前图标一次。");
        hide.Click += async (_, _) => await RunTrayToolAsync(); actions.Children.Add(hide);
        var refresh = Action("刷新结果"); refresh.IsEnabled = reachable;
        refresh.Click += async (_, _) => { if (busy) return; SetBusy(true); try { await ReadTrayToolAsync(); } finally { SetBusy(false); Render(); } };
        actions.Children.Add(refresh); body.Children.Add(actions);
        body.Children.Add(Text("隐藏后托盘菜单和气泡通知可能不可用。关闭不恢复已隐藏图标；可重新打开或重启目标软件恢复。", 12, Amber));
        content.Children.Add(Card(body));
    }
    private static string TrayModeText(string mode) => mode == TrayTool.Continuous ? "持续隐藏" : "启动时隐藏一次";
    private static string TrayResultText(OutcomeStatus? status) => status == OutcomeStatus.Succeeded ? "本次已完成" : status == OutcomeStatus.Unknown ? "未确认" : "本次未完成";
    private async Task ReadTrayToolAsync()
    {
        try
        {
            var response = await WorkbenchClient.RequestAsync(new(1, "read-tray-tool", Guid.NewGuid().ToString("N")));
            if (response.Type != "tray-tool" || response.TrayTool is null) throw new InvalidOperationException(response.Error ?? "托盘工具状态未确认。");
            trayTool = response.TrayTool; trayToolError = null;
        }
        catch (Exception error) { trayToolError = "无法读取托盘工具：" + error.Message; }
    }
    private async Task<bool> SaveTrayToolSettingsAsync(TrayToolSettings settings)
    {
        if (busy) return false; SetBusy(true);
        try
        {
            var response = await WorkbenchClient.RequestAsync(new(1, "save-tray-tool", Guid.NewGuid().ToString("N"), TraySettings: settings));
            if (response.Type != "tray-tool" || response.TrayTool is null) throw new InvalidOperationException(response.Error ?? "托盘设置保存未确认。");
            trayTool = response.TrayTool; trayToolError = null;
            ShowNotice(settings.Enabled ? "设置已保存，已请求处理当前图标；请刷新查看执行结果。" : "设置已保存；工具已关闭，名单保留。", true); return true;
        }
        catch (Exception error) { await ReadTrayToolAsync(); trayToolError = error.Message; ShowNotice(error.Message); return false; }
        finally { SetBusy(false); Render(); }
    }
    private async Task RunTrayToolAsync()
    {
        if (busy || trayTool is null) return; SetBusy(true);
        try
        {
            var response = await WorkbenchClient.RequestAsync(new(1, "run-tray-tool", Guid.NewGuid().ToString("N"), ExpectedVersion: trayTool.Settings.Version));
            if (response.Type != "tray-tool" || response.TrayTool is null) throw new InvalidOperationException(response.Error ?? "隐藏请求未确认。");
            trayTool = response.TrayTool; trayToolError = null;
            ShowNotice("已请求隐藏当前图标，执行结果尚未确认；请刷新结果。", true);
        }
        catch (Exception error) { trayToolError = error.Message; ShowNotice(error.Message); }
        finally { SetBusy(false); Render(); }
    }
    private async Task OpenTrayToolSettingsAsync()
    {
        if (busy || activeDialog is not null) return;
        SetBusy(true); try { await ReadTrayToolAsync(); } finally { SetBusy(false); }
        if (trayTool is null || closed) { ShowNotice(trayToolError ?? "托盘工具状态未确认。"); return; }
        var original = trayTool.Settings;
        var selection = new ProcessSelectionState(string.Join("\n", original.Executables), true);
        var mode = original.Mode; var enabled = original.Enabled; var waitSeconds = original.WaitSeconds;
        var waitText = waitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        while (!closed)
        {
            var next = "";
            var body = Column(12); body.MaxWidth = 620;
            body.Children.Add(Text("按程序完整路径处理当前可识别的托盘图标，包含折叠区；最多 32 个程序。", 13, Muted));
            var modes = new ComboBox { Header = "隐藏模式", HorizontalAlignment = HorizontalAlignment.Stretch };
            modes.Items.Add("仅 Axiom 后台启动时执行一次"); modes.Items.Add("持续隐藏（收到系统通知时处理）");
            modes.SelectedIndex = mode == TrayTool.Continuous ? 1 : 0;
            modes.SelectionChanged += (_, _) => mode = modes.SelectedIndex == 1 ? TrayTool.Continuous : TrayTool.Startup;
            body.Children.Add(modes);
            body.Children.Add(Help("仅启动：软件或资源管理器重建图标后可能再出现，可点击立即隐藏。\n持续：订阅系统通知，发现名单内图标重新出现后再隐藏；没有定时扫描。"));
            var wait = new TextBox { Header = "等待托盘图标出现（秒）", Text = waitText, MaxLength = 8 };
            wait.TextChanged += (_, _) => waitText = wait.Text; body.Children.Add(wait);
            body.Children.Add(Help($"填写 1–{TrayCapabilities.MaximumWaitSeconds} 的整数秒。已有图标立即隐藏；尚未出现时，最多等待所设秒数的系统通知。每个程序分别等待，超时会说明未找到。开机启动较慢可设为 60 秒；这不是固定延迟。"));
            var toggle = new ToggleSwitch { Header = "启用托盘管理", OnContent = "已启用", OffContent = "已关闭", IsOn = enabled };
            toggle.Toggled += (_, _) => enabled = toggle.IsOn; body.Children.Add(toggle);
            body.Children.Add(Text("启用或启用时保存会处理当前图标一次。关闭只停止未来操作，不恢复已隐藏图标。系统版本、权限或未知图标身份可能使识别/隐藏失败，结果会说明。", 12, Amber));
            body.Children.Add(Text($"已选择 {selection.Names.Count} 个程序", 13, Ink, true));
            var rows = Column(6);
            foreach (var path in selection.Names.ToArray())
            {
                var row = Column(4); row.Children.Add(Text(path, 12, Muted)); var remove = Action("移除");
                remove.Click += (_, _) => { selection.Set([path], false); next = "redraw"; activeDialog?.Hide(); }; row.Children.Add(remove); rows.Children.Add(row);
            }
            if (selection.Names.Count == 0) rows.Children.Add(Text("先从当前托盘多选，或批量添加完整程序路径。", 12, Muted));
            body.Children.Add(new ScrollViewer { Content = rows, MaxHeight = Math.Clamp(shell.ActualHeight - 570, 80, 180), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var buttons = Row(8); var tray = Action("从当前托盘选择…", true); var batch = Action("批量输入名单…"); var browse = Action("选择程序文件…");
            tray.Click += (_, _) => { next = "tray"; activeDialog?.Hide(); }; batch.Click += (_, _) => { next = "batch"; activeDialog?.Hide(); }; browse.Click += (_, _) => { next = "browse"; activeDialog?.Hide(); };
            buttons.Children.Add(tray); buttons.Children.Add(batch); buttons.Children.Add(browse); body.Children.Add(buttons);
            if (trayTool.LastResult is { } result)
            {
                body.Children.Add(Text($"上次执行：{TrayResultText(trayTool.LastStatus)} · {trayTool.LastRunUtc?.ToLocalTime():MM-dd HH:mm:ss}", 12, Muted));
                body.Children.Add(new ScrollViewer { Content = Text(result, 12, trayTool.LastStatus == OutcomeStatus.Succeeded ? Muted : Amber), MaxHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            }
            var error = Text("", 12, Amber); body.Children.Add(error);
            var dialog = new ContentDialog { Title = TrayTool.Name + "设置", Content = new ScrollViewer { Content = body, MaxHeight = Math.Clamp(shell.ActualHeight - 200, 240, 680) },
                PrimaryButtonText = "保存设置", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = shell.XamlRoot };
            dialog.Resources["ContentDialogMaxWidth"] = Math.Min(760d, shell.XamlRoot.Size.Width - 48);
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try
                {
                    if (!int.TryParse(wait.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.CurrentCulture, out var seconds) || seconds is < 1 or > TrayCapabilities.MaximumWaitSeconds)
                        throw new ArgumentException($"等待托盘图标出现的时间须为 1–{TrayCapabilities.MaximumWaitSeconds} 的整数秒。");
                    waitSeconds = seconds;
                    TrayTool.Validate(new(original.Version, enabled, mode, [.. selection.Names], waitSeconds));
                }
                catch (ArgumentException exception) { error.Text = exception.Message; args.Cancel = true; }
            };
            var answer = await ShowDialogAsync(dialog);
            if (next == "redraw") continue;
            if (next == "tray") { await ShowTrayPickerAsync(selection); continue; }
            if (next == "batch") { await ShowBatchProcessInputAsync(selection, executablePaths: true); continue; }
            if (next == "browse")
            {
                try
                {
                    var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id); picker.FileTypeFilter.Add(".exe");
                    var files = await picker.PickMultipleFilesAsync(); if (files.Count > 0) selection.Set(files.Select(f => f.Path), true);
                }
                catch (Exception exception) { ShowNotice("无法选择程序：" + exception.Message); }
                continue;
            }
            if (answer != ContentDialogResult.Primary) break;
            if (await SaveTrayToolSettingsAsync(new(original.Version, enabled, mode, [.. selection.Names], waitSeconds))) break;
        }
        Render();
    }
}
