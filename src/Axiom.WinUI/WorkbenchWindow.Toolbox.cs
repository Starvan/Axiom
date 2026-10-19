using Axiom.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private SteamToolSnapshot? steamTool;
    private ProcessGroupEntry[] steamProcesses = [];
    private string? steamToolError;

    private void BuildToolbox()
    {

        var body = Column(12); var heading = ToolHeading();
        heading.Children.Add(new Border { Width = 40, Height = 40, Background = BlueSoft, CornerRadius = new CornerRadius(10),
            Child = new FontIcon { Glyph = "\uE7F8", FontSize = 20, Foreground = Blue } });
        var headingText = Text("Steam 游戏自动整理", 18, Ink, true); headingText.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(headingText, 1); heading.Children.Add(headingText); body.Children.Add(heading);
        body.Children.Add(Text($"检查本机 Steam 安装项目，自动维护“{SteamTool.GroupName}”进程组。", 14, Muted));
        var toggle = new ToggleSwitch { Header = "每次 Axiom 启动时检查更新", OnContent = "已开启", OffContent = "已关闭",
            IsOn = steamTool?.Settings.Enabled == true, IsEnabled = reachable && steamTool is not null };
        toggle.Toggled += async (_, _) => await SetSteamEnabledAsync(toggle.IsOn); PlaceToolToggle(heading, toggle);
        ToolTipService.SetToolTip(toggle, "开启后立即检查，之后每次后台启动更新。关闭后保留已有程序组。");
        if (steamTool is not null) body.Children.Add(Text(SteamStatusText(steamTool), 13, Muted));
        if (steamToolError is not null || steamTool?.Error is not null) body.Children.Add(Text(steamToolError ?? steamTool!.Error!, 12, Amber));
        var buttons = Row(10); var settings = Action("工具设置"); settings.IsEnabled = reachable;
        settings.Click += async (_, _) => await OpenSteamSettingsAsync(); buttons.Children.Add(settings);
        var refresh = Action("立即检查更新"); refresh.IsEnabled = reachable;
        refresh.Click += async (_, _) => { if (busy) return; SetBusy(true); try { await RefreshSteamToolAsync(); await ReadGroupsAsync(); } finally { SetBusy(false); Render(); } };
        buttons.Children.Add(refresh); body.Children.Add(buttons); content.Children.Add(Card(body));
        ToolTipService.SetToolTip(headingText, "自动更新程序组，不改变已保存任务的名单。");
        BuildTrayTool();
        BuildBrightnessWheelTool();
    }
    private static string SteamStatusText(SteamToolSnapshot snapshot) => snapshot.Scanning ? "正在后台检查，刷新状态可查看最新结果。" :
        snapshot.LastScanUtc is null ? "尚未检查，开启自动更新或点击立即检查更新。" :
        $"{snapshot.InstalledGames} 个安装项目 · {snapshot.DetectedCount} 个程序 · 组内 {snapshot.ExportedCount} 个 · {snapshot.LastScanUtc.Value.ToLocalTime():MM-dd HH:mm}";
    private async Task ReadSteamToolAsync(bool all)
    {
        try
        {
            var entries = new List<ProcessGroupEntry>(); var offset = 0; string? id = null; SteamToolSnapshot? first = null;
            do
            {
                var response = await WorkbenchClient.RequestAsync(new(1, "read-steam-tool", Guid.NewGuid().ToString("N"), SteamOffset: offset, SteamScanId: id));
                if (response.Type != "steam-tool" || response.SteamTool is null) throw new InvalidOperationException(response.Error ?? "Steam 工具状态未确认。");
                var result = response.SteamTool; first ??= result; entries.AddRange(result.Processes); id = result.ScanId;
                if (!all || result.NextOffset is not { } next) break;
                if (next <= offset || next > 4096) throw new InvalidDataException("Steam 名单页无效。"); offset = next;
            } while (!closed);
            steamTool = first; if (all) steamProcesses = [.. entries]; steamToolError = null;
        }
        catch (Exception error) { steamToolError = "无法读取 Steam 工具：" + error.Message; }
    }
    private async Task RefreshSteamToolAsync()
    {
        try
        {
            var response = await WorkbenchClient.RequestAsync(new(1, "refresh-steam-tool", Guid.NewGuid().ToString("N")));
            if (response.Type != "steam-tool" || response.SteamTool is null) throw new InvalidOperationException(response.Error ?? "检查结果未确认。");
            steamTool = response.SteamTool; await ReadSteamToolAsync(true);
        }
        catch (Exception error) { steamToolError = "无法检查更新：" + error.Message; }
    }
    private async Task SetSteamEnabledAsync(bool enabled)
    {
        if (busy || steamTool is null) return; SetBusy(true);
        try
        {
            var response = await WorkbenchClient.RequestAsync(new(1, "save-steam-settings", Guid.NewGuid().ToString("N"), SteamSettings: steamTool.Settings with { Enabled = enabled }));
            if (response.Type != "steam-tool" || response.SteamTool is null) throw new InvalidOperationException(response.Error ?? "设置保存未确认。");
            steamTool = response.SteamTool; steamToolError = null; await ReadGroupsAsync();
            ShowNotice(enabled ? "已开启启动自动检查。" : "已关闭自动检查，已有组保留。", true);
        }
        catch (Exception error) { await ReadSteamToolAsync(false); steamToolError = error.Message; ShowNotice(error.Message); }
        finally { SetBusy(false); Render(); }
    }
    private async Task OpenSteamSettingsAsync()
    {
        if (busy || activeDialog is not null) return;
        SetBusy(true); try { await ReadSteamToolAsync(true); } finally { SetBusy(false); }
        if (steamTool is null || closed) { ShowNotice(steamToolError ?? "Steam 工具状态未确认。"); return; }
        var version = steamTool.Settings.Version;
        var excluded = new ProcessSelectionState(string.Join("\n", steamTool.Settings.ExcludedProcesses));
        while (!closed)
        {
            var nextAction = "";
            var dialog = new ContentDialog { Title = "Steam 工具设置", PrimaryButtonText = "保存排除项", CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close, XamlRoot = shell.XamlRoot };
            dialog.Resources["ContentDialogMaxWidth"] = Math.Min(860d, shell.XamlRoot.Size.Width - 48);
            var body = new Grid { RowSpacing = 12, Width = Math.Max(260, Math.Min(740, shell.XamlRoot.Size.Width - 140)), Height = Math.Clamp(shell.XamlRoot.Size.Height - 220, 220, 540) };
            for (var i = 0; i < 5; i++) body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var statusText = Text(SteamStatusText(steamTool), 12, Muted); body.Children.Add(statusText);
            var hint = Text($"勾选表示排除：以后不导入“{SteamTool.GroupName}”。排除项只在保存后生效。", 13, Muted); Grid.SetRow(hint, 1); body.Children.Add(hint);
            var message = Text(steamToolError ?? steamTool.Error ?? string.Join("\n", steamTool.Warnings), 12, Amber);
            var warnings = new ScrollViewer { Content = message, MaxHeight = 70, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(warnings, 2); body.Children.Add(warnings);
            var filter = new TextBox { PlaceholderText = "搜索程序或游戏", MaxLength = 200 }; Grid.SetRow(filter, 3); body.Children.Add(filter);
            var toolbar = Column(8); var buttons = Row(8); var all = Action("排除当前结果"); var clear = Action("取消当前排除"); var batch = Action("批量添加排除项…");
            buttons.Children.Add(all); buttons.Children.Add(clear); buttons.Children.Add(batch); toolbar.Children.Add(buttons);
            var updates = Row(8); var refresh = Action("立即检查更新"); var read = Action("刷新状态"); var count = Text("", 12, Muted);
            updates.Children.Add(refresh); updates.Children.Add(read); updates.Children.Add(count); toolbar.Children.Add(updates); Grid.SetRow(toolbar, 4); body.Children.Add(toolbar);
            var list = new ListView { SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ContentControl HorizontalContentAlignment='Stretch'/></DataTemplate>") };
            Grid.SetRow(list, 5); body.Children.Add(list); dialog.Content = body;
            ProcessGroupEntry[] Filtered() => steamProcesses.Concat(excluded.Names.Where(n => !steamProcesses.Any(p => p.ProcessName.Equals(n, StringComparison.OrdinalIgnoreCase)))
                .Select(n => new ProcessGroupEntry(n, "当前未检测到，排除仍保留")))
                .Where(p => string.IsNullOrWhiteSpace(filter.Text) || p.ProcessName.Contains(filter.Text, StringComparison.OrdinalIgnoreCase) || p.DisplayName.Contains(filter.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
            void Summary() => count.Text = $"检测到 {steamProcesses.Length} 个 · 排除 {excluded.Names.Count} 个";
            void Draw() { list.ItemsSource = Filtered(); Summary(); }
            void Populate(ListViewBase sender, ContainerContentChangingEventArgs args)
            {
                if (args.ItemContainer.ContentTemplateRoot is ContentControl presenter)
                {
                    if (args.InRecycleQueue) presenter.Content = null;
                    else if (args.Item is ProcessGroupEntry entry)
                    {
                        var check = new CheckBox { Content = entry.ProcessName + "  ·  " + entry.DisplayName, IsChecked = excluded.Contains(entry.ProcessName), Margin = new Thickness(0, 4, 0, 4) };
                        AutomationProperties.SetName(check, "排除 " + entry.ProcessName);
                        check.Checked += (_, _) => { try { excluded.Set([entry.ProcessName], true); Summary(); } catch (ArgumentException error) { message.Text = error.Message; check.IsChecked = false; } };
                        check.Unchecked += (_, _) => { excluded.Set([entry.ProcessName], false); Summary(); }; presenter.Content = check;
                    }
                    args.Handled = true;
                }
                else if (!args.InRecycleQueue && args.Phase == 0) args.RegisterUpdateCallback(1, Populate);
            }
            list.ContainerContentChanging += Populate;
            filter.TextChanged += (_, _) => DispatcherQueue.TryEnqueue(Draw);
            all.Click += (_, _) => { try { excluded.Set(Filtered().Select(p => p.ProcessName), true); Draw(); } catch (ArgumentException error) { message.Text = error.Message; } };
            clear.Click += (_, _) => { excluded.Set(Filtered().Select(p => p.ProcessName), false); Draw(); };
            batch.Click += (_, _) => { nextAction = "batch"; dialog.Hide(); };
            async Task Update(bool scan)
            {
                if (busy) return; SetBusy(true); refresh.IsEnabled = read.IsEnabled = filter.IsEnabled = list.IsEnabled = all.IsEnabled = clear.IsEnabled = batch.IsEnabled = dialog.IsPrimaryButtonEnabled = false;
                try { if (scan) await RefreshSteamToolAsync(); else await ReadSteamToolAsync(true); await ReadGroupsAsync(); }
                finally
                {
                    SetBusy(false); refresh.IsEnabled = read.IsEnabled = filter.IsEnabled = list.IsEnabled = all.IsEnabled = clear.IsEnabled = batch.IsEnabled = dialog.IsPrimaryButtonEnabled = true;
                    statusText.Text = SteamStatusText(steamTool!); message.Text = steamToolError ?? steamTool!.Error ?? string.Join("\n", steamTool.Warnings); Draw(); Render();
                }
            }
            refresh.Click += async (_, _) => await Update(true); read.Click += async (_, _) => await Update(false); Draw();
            var result = await ShowDialogAsync(dialog);
            if (nextAction == "batch") { await ShowBatchProcessInputAsync(excluded, exclusions: true); continue; }
            if (result != ContentDialogResult.Primary) break;
            SetBusy(true);
            try
            {
                var response = await WorkbenchClient.RequestAsync(new(1, "save-steam-settings", Guid.NewGuid().ToString("N"), SteamSettings: new(version, steamTool!.Settings.Enabled, [.. excluded.Names])));
                if (response.Type != "steam-tool" || response.SteamTool is null) throw new InvalidOperationException(response.Error ?? "保存未确认。");
                steamTool = response.SteamTool; await ReadGroupsAsync(); ShowNotice("排除项已保存。", true); break;
            }
            catch (Exception error) { steamToolError = error.Message; ShowNotice(error.Message); }
            finally { SetBusy(false); Render(); }
        }
        Render();
    }
}
