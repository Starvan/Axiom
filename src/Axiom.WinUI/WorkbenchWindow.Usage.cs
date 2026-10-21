using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Axiom.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private UsageSnapshot? usageSnapshot;
    private NamedPipeClientStream? usageLease;
    private NamedPipeClientStream? pendingUsageLease;
    private string? usageSelection;
    private int usageOffset;
    private readonly Button usageStop = Action("结束使用观察");
    private readonly TextBlock usageIndicator = Text("", 12, Amber);

    private async Task<AutomationMessage> UsageAsync(UsageRequest request)
    {
        var response = await WorkbenchClient.RequestAsync(new(1, "usage", Guid.NewGuid().ToString("N"), Usage: request));
        if (response.Type != "usage" || response.UsageSnapshot is null) throw new InvalidOperationException(response.Error ?? "观察状态未确认。");
        usageSnapshot = response.UsageSnapshot; UpdateUsageIndicator(); return response;
    }
    private void UpdateUsageIndicator()
    {
        var active = usageSnapshot?.Sessions.FirstOrDefault(s => s.Id == usageSnapshot.ActiveId);
        usageIndicator.Text = active is null ? "" : $"使用观察：{(active.State == "paused" ? "已暂停" : "已开启")} · {active.EndsUtc.ToLocalTime():HH:mm} 到期 · 关闭窗口即结束";
        usageIndicator.Visibility = usageStop.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private async Task ReadUsageAsync() => await UsageAsync(new("read", SessionId: usageSelection, Offset: usageOffset));
    private async Task UsageCommandAsync(string operation, string? id = null, string? feedback = null, string? text = null)
    {
        if (busy) return; SetBusy(true);
        try
        {
            await UsageAsync(new(operation, SessionId: id ?? usageSelection, Text: text, FeedbackId: feedback, Offset: usageOffset));
            if (operation is "stop" or "clear" || operation == "delete" && usageSnapshot?.ActiveId is null) { usageLease?.Dispose(); usageLease = null; }
        }
        catch (Exception error) { ShowNotice(error.Message); }
        finally { SetBusy(false); Render(); }
    }
    private void BuildUsage()
    {
        content.Children.Add(Text("记录一段使用过程，寻找可自动化的日常操作。关闭窗口即结束观察。", 14, Muted));
        var privacy = Row(4);
        privacy.Children.Add(Help("仅在你开启后记录选定的应用、会话、供电、低电量、显示模式、USB类别、默认音频及网络可用性元数据；不采集窗口标题、正文、按键、剪贴板、截图或命令行。关闭此窗口立即结束观察；自动化规则继续工作。", "会记录哪些内容"));
        privacy.Children.Add(Help("最多 120 分钟、4096 条事件、128 个应用；本地最多保留 12 次、7 天。未知来源不能当作手动操作。", "记录范围与保留期限"));
        var actions = FlowActions();
        var start = Action("开始观察", true); start.IsEnabled = reachable && usageSnapshot?.ActiveId is null; start.Click += async (_, _) => await StartUsageAsync(); actions.Children.Add(start);
        var refresh = Action("刷新状态"); refresh.Click += async (_, _) => await UsageCommandAsync("read"); actions.Children.Add(refresh);
        var active = usageSnapshot?.Sessions.FirstOrDefault(s => s.Id == usageSnapshot.ActiveId);
        if (active is not null)
        {
            var pause = Action(active.State == "paused" ? "继续" : "暂停"); pause.Click += async (_, _) => await UsageCommandAsync(active.State == "paused" ? "resume" : "pause"); actions.Children.Add(pause);
            var stop = Action("结束"); stop.Click += async (_, _) => await UsageCommandAsync("stop"); actions.Children.Add(stop);
        }
        pageTools.Children.Add(actions);
        content.Children.Add(privacy);
        var catalogButtons = FlowActions();
        foreach (var item in new[] { (Operation: "catalog", Label: "导出能力 JSON…"), (Operation: "catalog-readable", Label: "查看能力开发资料…") })
        {
            var button = Action(item.Label); button.Click += async (_, _) =>
            {
                try { var result = await UsageAsync(new(item.Operation)); await PreviewUsageTextAsync("当前能力目录", result.ExportText!); }
                catch (Exception error) { ShowNotice(error.Message); }
            }; catalogButtons.Children.Add(button);
        }
        content.Children.Add(Details("高级 · 导出能力资料", catalogButtons));
        if (usageSnapshot is null) { content.Children.Add(Text("尚未读取观察状态。", 13, Muted)); return; }
        content.Children.Add(Text($"状态读取于 {usageSnapshot.ReadAtUtc.ToLocalTime():HH:mm:ss}。", 12, Muted));
        var sessions = new ComboBox { Header = "本地观察记录", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = usageSnapshot.Sessions.Select(s => $"{s.StartedUtc.ToLocalTime():MM-dd HH:mm} · {StateName(s.State)}").ToArray() };
        var selected = usageSnapshot.Selected;
        sessions.SelectedIndex = Array.FindIndex(usageSnapshot.Sessions, s => s.Id == selected?.Id);
        sessions.SelectionChanged += async (_, _) => { if (sessions.SelectedIndex < 0 || busy) return; usageSelection = usageSnapshot.Sessions[sessions.SelectedIndex].Id; usageOffset = 0; await UsageCommandAsync("read"); };
        content.Children.Add(sessions);
        if (selected is null) { content.Children.Add(Text("没有观察记录。先开启一次你愿意记录的使用过程。", 14, Muted)); return; }
        var scope = Column(6);
        scope.Children.Add(Text("本次范围：" + string.Join("、", new[] { selected.Options.Foreground ? "前台应用" : null, selected.Options.ProcessStarts ? "程序启动" : null, selected.Options.ProcessExits ? "本次新进程退出" : null, selected.Options.WindowStates ? "窗口显示/隐藏/状态/近似布局" : null, selected.Options.AudioEvents ? "默认播放设备/默认通信麦克风变化" : null, selected.Options.SystemEvents ? "会话/供电/低电量/显示模式/USB类别/网络可用性" : null }.Where(x => x is not null)), 13, Muted));
        scope.Children.Add(Text("排除应用：" + (selected.Options.ExcludedApplications.Length == 0 ? "无" : string.Join("、", selected.Options.ExcludedApplications)), 12, Muted));
        scope.Children.Add(Text("本次目的：" + (selected.Options.Purpose.Length == 0 ? "未填写" : selected.Options.Purpose), 13, Muted));
        content.Children.Add(Details("本次记录范围", scope));
        foreach (var gap in selected.Gaps) content.Children.Add(Text(gap, 12, Amber));
        var tools = FlowActions();
        var annotate = Action("补充本次目的…"); annotate.Click += async (_, _) => await AnnotateUsageAsync(selected); tools.Children.Add(annotate);
        var export = Action("预览分析摘要…"); export.Click += async (_, _) => await PreviewUsageExportAsync(selected.Id); tools.Children.Add(export);
        var import = Action("导入 AI 建议…"); import.Click += async (_, _) => await ImportUsageAsync(selected.Id); tools.Children.Add(import);
        content.Children.Add(tools);
        var extensionRequest = Action("补充需求并生成开发任务…");
        extensionRequest.Click += async (_, _) => await CreateUsageExtensionAsync(selected.Id);
        content.Children.Add(extensionRequest);
        if (usageSnapshot.DismissedSuggestions > 0)
        {
            var restore = Action($"重新显示已处理建议（{usageSnapshot.DismissedSuggestions}）");
            restore.Click += async (_, _) => await UsageCommandAsync("restore-suggestions", selected.Id);
            content.Children.Add(restore);
            content.Children.Add(Text("每次撤销最多12个与本次记录相关的已处理反馈，让这些模式重新显示；不改观察或已有规则。生成草稿后未保存，也可从这里重新审阅。", 12, Muted));
        }
        foreach (var suggestion in usageSnapshot.Suggestions ?? [])
        {
            var card = Column(8); card.Children.Add(Text(suggestion.Title + (suggestion.Kind == "existing" ? " · 可审阅规则" : " · 证据不足"), 17, Ink, true));
            card.Children.Add(Text(string.Join(" → ", suggestion.ObjectIds.Select(id => LocalName(selected, id))), 14));
            card.Children.Add(Text(suggestion.Evidence, 13));
            if (suggestion.EvidenceSamples is { Length: > 0 } examples)
                card.Children.Add(Text("代表片段：" + string.Join("；", examples.Select(e => $"开始后 {e.FromOffsetSeconds} 秒 → {e.ToOffsetSeconds} 秒")), 12, Muted)); card.Children.Add(Text(suggestion.Limitation, 12, Muted));
            if (suggestion.Kind == "existing")
            {
                var accept = Action("确认意图并审阅规则…", true);
                accept.Click += async (_, _) =>
                {
                    if (!await ConfirmAsync("这是你希望自动完成的重复操作吗？", suggestion.Limitation, "生成暂停的草稿")) return;
                    try { var result = await UsageAsync(new("draft", SessionId: selected.Id, FeedbackId: suggestion.Id)); page = Page.Rules; SelectDraft(result.Task!); }
                    catch (Exception error) { ShowNotice(error.Message); }
                };
                card.Children.Add(accept);
            }
            var feedback = Row(8); var ignore = Action("忽略"); ignore.Click += async (_, _) => await UsageCommandAsync("feedback", selected.Id, suggestion.Id, "ignored"); feedback.Children.Add(ignore);
            var irrelevant = Action("不符合需求"); irrelevant.Click += async (_, _) => await UsageCommandAsync("feedback", selected.Id, suggestion.Id, "irrelevant"); feedback.Children.Add(irrelevant);
            var extension = Action("补充需求…"); extension.Click += async (_, _) => await CreateUsageExtensionAsync(selected.Id, suggestion); feedback.Children.Add(extension);
            card.Children.Add(feedback); content.Children.Add(Card(card));
        }
        if (usageSnapshot.Suggestions is not { Length: > 0 }) content.Children.Add(Text("目前没有足够重复证据，或建议已处理。可使用摘要与 AI 分析，不会自动配置规则。", 13, Muted));
        var records = Column(4); records.Children.Add(Text($"记录 {usageOffset + (selected.Events.Length > 0 ? 1 : 0)}–{usageOffset + selected.Events.Length} / {usageSnapshot.TotalEvents}", 15, Ink, true));
        foreach (var e in selected.Events) records.Children.Add(Text($"{e.AtUtc.ToLocalTime():HH:mm:ss} · {EventName(e.Kind)} · {LocalName(selected, e.ObjectId)} · {(e.Origin == "axiom" ? "Axiom" : "来源未知")}{(e.DwellSeconds is { } dwell ? $" · 前台停留约 {dwell:0} 秒" : "")}", 12, Muted));
        content.Children.Add(Card(records));
        var pages = Row(8); var before = Action("上一页"); before.IsEnabled = usageOffset > 0; before.Click += async (_, _) => { usageOffset = Math.Max(0, usageOffset - 64); await UsageCommandAsync("read"); }; pages.Children.Add(before);
        var next = Action("下一页"); next.IsEnabled = usageOffset + 64 < usageSnapshot.TotalEvents; next.Click += async (_, _) => { usageOffset += 64; await UsageCommandAsync("read"); }; pages.Children.Add(next); content.Children.Add(pages);
        var removal = Row(8); var delete = Action("删除本次记录"); delete.Click += async (_, _) => { usageOffset = 0; await UsageCommandAsync("delete", selected.Id); }; removal.Children.Add(delete);
        var clear = Action("清空记录"); clear.Click += async (_, _) => { if (await ConfirmAsync("清空使用观察？", "全部观察与相关反馈将被删除；现有规则保留。", "清空")) { usageOffset = 0; usageSelection = null; await UsageCommandAsync("clear"); } }; removal.Children.Add(clear); content.Children.Add(removal);
    }
    private static string StateName(string state) => state switch { "recording" => "观察中", "paused" => "已暂停", _ => "已结束" };
    private static string EventName(string kind) => kind switch { "foreground" => "前台切换", "process-start" => "程序启动", "process-exit" => "进程退出", "window-shown" => "窗口显示", "window-hidden" => "窗口隐藏或销毁", "window-minimized" => "窗口最小化", "window-maximized" => "窗口最大化", "window-normal" => "窗口普通状态/位置", "window-left" => "近似左半屏", "window-right" => "近似右半屏", "window-center" => "近似中央（80%）", "axiom-action" => "Axiom 发起动作", "system-locked" => "锁屏", "system-unlocked" => "解锁", "system-resumed" => "睡眠恢复", "system-ac" => "交流电供电", "system-battery" => "电池供电", "system-network-up" => "网络可用", "system-network-down" => "网络不可用", "system-battery-low20" => "电量降至 20% 或以下", "system-battery-low10" => "电量降至 10% 或以下", "system-display-changed" => "显示模式变化", "system-usb-arrived" => "USB类别接入通知", "system-usb-removed" => "USB类别移除通知", "audio-output" => "默认播放设备变化", "audio-input" => "默认通信麦克风变化", _ => "记录间断" };
    private static string LocalName(UsageSession session, string? id) => session.Objects.FirstOrDefault(o => o.Id == id) is { } item ? Path.GetFileName(item.Executable) : "—";

    private async Task AnnotateUsageAsync(UsageSession session)
    {
        if (busy || activeDialog is not null) return;
        var body = Column(10);
        var purpose = new TextBox { Header = "我刚才在做什么、希望减少什么操作", Text = session.Options.Purpose, MaxLength = 500, AcceptsReturn = true, Height = 120 };
        body.Children.Add(purpose); body.Children.Add(Text("只补充你对这次观察的说明，不改动事件证据。这段文字会进入摘要预览，请避免填写账号、私人内容或完整路径。可清空。", 12, Muted));
        var dialog = new ContentDialog { Title = "补充观察目的", Content = body, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = shell.XamlRoot };
        var error = Text("", 12, Amber); body.Children.Add(error); var submitting = false;
        dialog.Closing += (_, args) => { if (submitting) args.Cancel = true; };
        dialog.PrimaryButtonClick += async (sender, args) =>
        {
            var deferral = args.GetDeferral(); submitting = true; purpose.IsEnabled = false;
            error.Text = "正在保存本次目的…";
            try { await UsageAsync(new("annotate", SessionId: session.Id, Text: purpose.Text, Offset: usageOffset)); }
            catch (Exception failure) { args.Cancel = true; error.Text = "保存未确认，输入已保留：" + failure.Message; }
            finally { purpose.IsEnabled = true; submitting = false; deferral.Complete(); }
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) Render();
    }

    private async Task StartUsageAsync()
    {
        if (busy || activeDialog is not null) return;
        var body = Column(12); var foreground = new CheckBox { Content = "前台应用切换（不含窗口标题）", IsChecked = true }; var starts = new CheckBox { Content = "程序启动（不含命令行）", IsChecked = true };
        var exits = new CheckBox { Content = "本次新进程退出（不推断是否手动关闭）", IsChecked = false };
        var windows = new CheckBox { Content = "窗口显示/隐藏/状态/近似布局（不含标题或坐标）", IsChecked = false };
        var system = new CheckBox { Content = "会话、电源、显示模式、USB类别及网络事件", IsChecked = true };
        var audio = new CheckBox { Content = new TextBlock { Text = "默认播放设备/默认通信麦克风变化（不含设备标识）", TextWrapping = TextWrapping.Wrap, MaxWidth = 360 }, IsChecked = false };
        var duration = new ComboBox { Header = "持续时间", ItemsSource = new[] { "5 分钟", "15 分钟", "30 分钟", "60 分钟", "120 分钟" }, SelectedIndex = 1 };
        var exclusions = new TextBox { Header = "排除应用：每行一个 exe 文件名", AcceptsReturn = true, Height = 80, MaxLength = 17000 };
        var purpose = new TextBox { Header = "我准备做什么（选填，会进入导出预览）", MaxLength = 500 };
        body.Children.Add(foreground); body.Children.Add(starts); body.Children.Add(exits); body.Children.Add(windows); body.Children.Add(system); body.Children.Add(audio); body.Children.Add(duration); body.Children.Add(exclusions); body.Children.Add(purpose);
        body.Children.Add(Text("记录只留本机；关闭窗口即停止，重启不会自动恢复。Axiom 动作会单独标记，其他事件来源未知。退出仅覆盖本次核验的新进程，最多同时 128 个；暂停期间和恢复前已有进程不补跟踪。", 12, Muted));
        var dialog = new ContentDialog { Title = "主动开启使用观察", Content = new ScrollViewer { Content = body, MaxHeight = Math.Max(180, Math.Min(560, shell.ActualHeight - 180)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, PrimaryButtonText = "开始", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = shell.XamlRoot };
        var error = Text("", 12, Amber); body.Children.Add(error);
        Control[] inputs = [foreground, starts, exits, windows, system, audio, duration, exclusions, purpose];
        var submitting = false;
        dialog.Closing += (_, args) => { if (submitting) args.Cancel = true; };
        dialog.PrimaryButtonClick += async (sender, args) =>
        {
            var deferral = args.GetDeferral(); submitting = true;
            foreach (var input in inputs) input.IsEnabled = false;
            error.Text = "正在校验并连接后台…";
            NamedPipeClientStream? pendingLease = null;
            try
            {
                var options = new UsageOptions(new[] { 5, 15, 30, 60, 120 }[duration.SelectedIndex], foreground.IsChecked == true, starts.IsChecked == true,
                    exclusions.Text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), purpose.Text, system.IsChecked == true, exits.IsChecked == true, windows.IsChecked == true, audio.IsChecked == true);
                UsageValidation.Options(options);
                var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new UnauthorizedAccessException();
                pendingLease = LocalUserPipe.Create(AutomationProtocol.PipeName(sid)); pendingUsageLease = pendingLease;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); await pendingLease.ConnectForUserAsync(3000, timeout.Token);
                var id = Guid.NewGuid().ToString("N");
                await AutomationProtocol.WriteAsync(pendingLease, new(1, "usage", id, DateTimeOffset.UtcNow.AddSeconds(12), Usage: new("start", options)), timeout.Token);
                var response = await AutomationProtocol.ReadAsync(pendingLease, timeout.Token);
                if (closed) throw new OperationCanceledException("窗口已关闭，观察连接已结束。");
                if (response.RequestId != id || response.Version != 1 || response.Type != "usage" || response.UsageSnapshot is null) throw new InvalidOperationException(response.Error ?? "开启结果未确认。");
                usageLease?.Dispose(); usageLease = pendingLease; pendingLease = null; pendingUsageLease = null;
                usageSnapshot = response.UsageSnapshot; usageSelection = usageSnapshot.ActiveId; usageOffset = 0; UpdateUsageIndicator();
                _ = WatchUsageLeaseAsync(usageLease, id);
            }
            catch (Exception failure)
            {
                pendingLease?.Dispose(); args.Cancel = true;
                error.Text = "未确认开启，输入已保留。请修正或检查后台后重试：" + failure.Message;
            }
            finally
            {
                pendingUsageLease = null;
                foreach (var input in inputs) input.IsEnabled = true;
                submitting = false; deferral.Complete();
            }
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) Render();
    }

    private async Task WatchUsageLeaseAsync(NamedPipeClientStream lease, string id)
    {
        try
        {
            var response = await AutomationProtocol.ReadAsync(lease, default);
            if (response.Version != 1 || response.RequestId != id || response.Type != "usage-ended") throw new InvalidDataException("观察连接状态未确认。");
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidDataException) { }
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (closed || !ReferenceEquals(usageLease, lease)) return;
            lease.Dispose(); usageLease = null;
            try { await ReadUsageAsync(); Render(); }
            catch (Exception error) { usageIndicator.Text = "观察连接已断开；记录状态未确认。"; ShowNotice(error.Message); }
        });
    }
    private async Task PreviewUsageExportAsync(string id)
    {
        try { var response = await UsageAsync(new("export", SessionId: id)); await PreviewUsageTextAsync("摘要预览与删减（64 KiB 上限）", response.ExportText!); }
        catch (Exception error) { ShowNotice(error.Message); }
    }
    private async Task PreviewUsageTextAsync(string title, string text)
    {
        var input = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 65536, Height = Math.Clamp(shell.ActualHeight - 330, 140, 380) };
        var size = Text("", 12, Muted); var error = Text("", 12, Amber);
        var body = Column(8); body.Children.Add(Text("可删除不愿分享的段落。只有点击复制才写入剪贴板；不会上传。对象映射与完整路径仍留本机。", 12, Muted)); body.Children.Add(size); body.Children.Add(input); body.Children.Add(error);
        var dialog = new ContentDialog { Title = title, Content = body, PrimaryButtonText = "复制确认后的内容", CloseButtonText = "取消", XamlRoot = shell.XamlRoot };
        void UpdateSize()
        {
            var bytes = Encoding.UTF8.GetByteCount(input.Text);
            size.Text = $"UTF-8 大小：{bytes:N0} / {UsageValidation.MaximumExportBytes:N0} 字节";
            dialog.IsPrimaryButtonEnabled = bytes <= UsageValidation.MaximumExportBytes;
            error.Text = bytes > UsageValidation.MaximumExportBytes ? "内容超过64 KiB，请继续删减；尚未复制。" : "";
        }
        input.TextChanged += (_, _) => UpdateSize(); UpdateSize();
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                if (Encoding.UTF8.GetByteCount(input.Text) > UsageValidation.MaximumExportBytes) throw new ArgumentException("内容超过64 KiB，请继续删减。");
                var data = new DataPackage(); data.SetText(input.Text); Clipboard.SetContent(data);
            }
            catch (Exception failure) { args.Cancel = true; error.Text = "未确认复制成功：" + failure.Message; }
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) ShowNotice("已复制你确认的内容。", true);
    }
    private async Task ImportUsageAsync(string id)
    {
        var input = new TextBox { AcceptsReturn = true, Height = Math.Clamp(shell.ActualHeight - 300, 140, 320), MaxLength = 65536, PlaceholderText = "粘贴结构化 JSON 建议" };
        var size = Text("", 12, Muted); var error = Text("", 12, Amber); var body = Column(8);
        body.Children.Add(size); body.Children.Add(input); body.Children.Add(error);
        var dialog = new ContentDialog { Title = "导入建议（不执行）", Content = body, PrimaryButtonText = "校验并预览", CloseButtonText = "取消", XamlRoot = shell.XamlRoot };
        AutomationMessage? response = null;
        void UpdateSize()
        {
            var bytes = Encoding.UTF8.GetByteCount(input.Text);
            size.Text = $"UTF-8 大小：{bytes:N0} / {UsageValidation.MaximumExportBytes:N0} 字节";
            dialog.IsPrimaryButtonEnabled = bytes > 0 && bytes <= UsageValidation.MaximumExportBytes;
            error.Text = bytes > UsageValidation.MaximumExportBytes ? "内容超过64 KiB，请删减后再校验。" : "";
        }
        input.TextChanged += (_, _) => UpdateSize(); UpdateSize();
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral(); input.IsEnabled = false; error.Text = "正在校验，尚未创建或启用规则…";
            try { response = await UsageAsync(new("import", SessionId: id, Text: input.Text)); }
            catch (Exception failure) { args.Cancel = true; error.Text = "校验未通过，请修正后重试：" + failure.Message; }
            finally { input.IsEnabled = true; deferral.Complete(); }
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || response is null) return;
        try
        {
            if (response.Task is { } draft)
            {
                if (!await ConfirmAsync("审阅候选规则", response.ExportText + "\n将进入规则编辑器，默认暂停；必须保存并启用才可能执行。", "打开编辑器")) return;
                page = Page.Rules; SelectDraft(draft);
            }
            else if (response.ExportText?.StartsWith("extension：", StringComparison.Ordinal) == true)
            {
                if (await ConfirmAsync("生成扩展需求", response.ExportText + "\n请确认此需求是你希望解决的问题。", "生成任务包"))
                {
                    var package = await UsageAsync(new("extension", SessionId: id, Text: input.Text)); await PreviewUsageTextAsync("扩展开发任务预览", package.ExportText!);
                }
            }
            else await ConfirmAsync("证据不足", response.ExportText ?? "还需要更多观察或意图说明。", "知道了");
        }
        catch (Exception failure) { ShowNotice(failure.Message); }
    }
    private void SelectDraft(Axiom.Domain.AutomationTask draft)
    {
        editor.LoadDraft(draft); editing = true; notice.IsOpen = false; Render();
    }
    private async Task CreateUsageExtensionAsync(string id, UsageSuggestion? suggestion = null)
    {
        var title = new TextBox { Header = "希望解决什么问题", MaxLength = 120, Text = suggestion?.Title ?? "" };
        var description = new TextBox { Header = "操作前后对比、希望自动完成的步骤（不填路径或个人信息）", AcceptsReturn = true, Height = 180, MaxLength = 2000 };
        var body = Column(10);
        if (suggestion is not null)
            body.Children.Add(Text("观察线索：" + suggestion.Evidence + "\n这不证明存在重复劳动或缺少能力。请在下面说明希望改变的行为；不会默认附带观察记录或对象映射。", 12, Muted));
        body.Children.Add(title); body.Children.Add(description);
        var dialog = new ContentDialog { Title = "补充并确认扩展需求", Content = new ScrollViewer { Content = body, MaxHeight = Math.Clamp(shell.ActualHeight - 230, 180, 500), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, PrimaryButtonText = "生成任务包", CloseButtonText = "取消", XamlRoot = shell.XamlRoot };
        var error = Text("", 12, Amber); body.Children.Add(error);
        string? package = null; var submitting = false;
        dialog.Closing += (_, args) => { if (submitting) args.Cancel = true; };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral(); submitting = true; title.IsEnabled = description.IsEnabled = false;
            error.Text = "正在校验需求并生成本地任务包…";
            try
            {
                var json = JsonSerializer.Serialize(new { Version = 1, SessionId = id, Kind = "extension", Title = title.Text, Explanation = description.Text });
                var result = await UsageAsync(new("extension", SessionId: id, Text: json));
                package = result.ExportText ?? throw new InvalidOperationException("任务包结果未确认。");
            }
            catch (Exception failure) { args.Cancel = true; error.Text = "未生成可预览的任务包，输入已保留：" + failure.Message; }
            finally { title.IsEnabled = description.IsEnabled = true; submitting = false; deferral.Complete(); }
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && package is not null)
            await PreviewUsageTextAsync("扩展开发任务预览", package);
    }
}
