using System.Collections.Immutable;
using Axiom.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private string ruleSearch = "";
    private int ruleStateFilter;
    private TaskCapability? FindCapability(TaskInvocation? invocation, TaskCapabilityKind kind) => invocation is null ? null :
        status?.Capabilities.FirstOrDefault(x => x.Id == invocation.CapabilityId && x.Kind == kind);
    private string ActionWarning(TaskInvocation invocation) => FindCapability(invocation, TaskCapabilityKind.Action)?.EditWarning ?? "";
    private string Summary(TaskInvocation invocation, TaskCapabilityKind kind)
    {
        var descriptor = FindCapability(invocation, kind);
        if (descriptor is null) return "该类型暂不可用";
        var values = descriptor.Fields.Where(f => f.Kind != TaskFieldKind.ProcessSelection && invocation.Parameters.TryGetValue(f.Id, out var v) && !string.IsNullOrWhiteSpace(v) &&
            (f.Required && f.Group is null || v != f.DefaultValue)).Select(f =>
        {
            var value = invocation.Parameters[f.Id];
            var label = f.Kind == TaskFieldKind.Boolean ? value == "true" ? "开" : "关" :
                f.Kind is TaskFieldKind.TextList or TaskFieldKind.HotkeyList ? string.Join("、", value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3)) :
                f.Choices.FirstOrDefault(x => x.Value == value)?.Label ?? (f.Kind == TaskFieldKind.Resource ? "已选资源" :
                    f.Kind == TaskFieldKind.FilePath ? System.IO.Path.GetFileName(value) :
                    f.Kind is TaskFieldKind.ExecutableSelection or TaskFieldKind.TraySelection ? string.Join("、", value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3).Select(System.IO.Path.GetFileName)) +
                        (value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length > 3 ? " 等" : "") : value.Length > 60 ? value[..60] + "…" : value);
            return f.Label + "：" + label;
        });
        var details = string.Join(" / ", values);
        return descriptor.Name + (details.Length == 0 ? "" : "  ·  " + details);
    }
    private Button RefreshButton()
    {
        var button = Action("刷新"); button.Click += async (_, _) => await RefreshAsync(); return button;
    }
    private void BuildRules()
    {
        var toolbar = new Grid { ColumnSpacing = 12, RowSpacing = 10 };
        toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var counts = Column(6);
        counts.Children.Add(Text(status is null ? "你的任务会显示在这里" : $"{status.Tasks.Length} 个任务  ·  {status.Tasks.Count(x => TaskStateLabel(x) == "已启用")} 个已启用  ·  {status.Tasks.Count(x => TaskStateLabel(x) == "自动暂停")} 个自动暂停", 14, Muted));
        ToolTipService.SetToolTip(counts, readAt is null ? "尚未读取后台" : $"状态读取于 {readAt:HH:mm:ss}");
        toolbar.Children.Add(counts);
        var buttons = Row(8); buttons.Children.Add(RefreshButton());
        var add = Action("＋  新建任务", true); add.IsEnabled = status is not null;
        add.Click += (_, _) => SelectRule(null); buttons.Children.Add(add);
        Grid.SetColumn(buttons, 1); toolbar.Children.Add(buttons); content.Children.Add(toolbar);
        var template = Action("常用场景"); template.IsEnabled = reachable && status is not null;
        template.Click += async (_, _) => await ChooseTaskTemplateAsync(); buttons.Children.Insert(0, template);
        void FitToolbar()
        {
            var narrow = toolbar.ActualWidth < 620;
            Grid.SetRow(buttons, narrow ? 1 : 0); Grid.SetColumn(buttons, narrow ? 0 : 1);
            Grid.SetColumnSpan(buttons, narrow ? 2 : 1); Grid.SetColumnSpan(counts, narrow ? 2 : 1);
        }
        toolbar.SizeChanged += (_, _) => FitToolbar();
        if (!reachable || status?.DetectionAvailable != true)
            content.Children.Add(Card(Text(reachable ? "检测暂不可用。请重新打开 Axiom，然后刷新状态。" : "后台状态未确认，显示上次读取的内容。", 13, Amber), PaleAmber, 16));
        if (status?.Tasks.Length is not > 0)
        {
            var empty = Column(14);
            empty.Children.Add(Text("创建你的第一个后台任务", 23, Ink, true));
            empty.Children.Add(Text("选择一种检测条件，配上要执行的动作。保存后，Axiom 在后台等待条件并完成任务。", 14, Muted));
            empty.Children.Add(Text("检测条件  →  执行动作  →  查看结果", 16, Accent, true));
            content.Children.Add(Card(empty, Surface, 32));
        }
        if (status?.Tasks.Length is > 0)
        {
            var filter = new Grid { ColumnSpacing = 12 };
            filter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var search = new TextBox { PlaceholderText = "任务名称、检测或动作名称", Text = ruleSearch, MaxLength = 200 };
            var state = new ComboBox { ItemsSource = new[] { "全部任务", "仅已启用", "仅自动暂停", "仅手动暂停" }, SelectedIndex = ruleStateFilter, MinWidth = 150 };
            filter.Children.Add(search); Grid.SetColumn(state, 1); filter.Children.Add(state); content.Children.Add(filter);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search, "查找任务");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(state, "任务状态筛选");
            var shown = Text("", 12, Muted); var cards = Column(0); content.Children.Add(shown); content.Children.Add(Card(cards, Surface, 0));
            search.TextChanged += (_, _) => { ruleSearch = search.Text; ShowRules(); };
            state.SelectionChanged += (_, _) => { ruleStateFilter = Math.Max(0, state.SelectedIndex); ShowRules(); };
            ShowRules();
            void ShowRules()
            {
                var query = ruleSearch.Trim();
                var visible = (status?.Tasks ?? []).Where(task => (ruleStateFilter == 0 || TaskStateLabel(task) == (ruleStateFilter == 1 ? "已启用" : ruleStateFilter == 2 ? "自动暂停" : "手动暂停")) &&
                    (query.Length == 0 || task.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     TaskDetections(task).Any(d => FindCapability(d, TaskCapabilityKind.Detection)?.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                     (FindCapability(task.Action, TaskCapabilityKind.Action)?.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))).ToArray();
                shown.Text = $"显示 {visible.Length} / {status?.Tasks.Length ?? 0} 个任务";
                cards.Children.Clear();
                if (visible.Length == 0) cards.Children.Add(new Border { Padding = new Thickness(18), Child = Text("没有匹配的任务，换个关键词或筛选试试。", 14, Muted) });
                foreach (var task in visible)
                {
                    var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, 12, 16, 12) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var body = Column(5);
                    var heading = new Grid { ColumnSpacing = 10 };
                    heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    heading.Children.Add(Preview(task.Title, 16, Ink, true));
                    var badge = Badge(TaskStateLabel(task), TaskStateLabel(task) != "已启用");
                    Grid.SetColumn(badge, 1); heading.Children.Add(badge); body.Children.Add(heading);
                    var detection = task.DetectionExpression is null ? FindCapability(task.Detection, TaskCapabilityKind.Detection)?.Name ?? "检测暂不可用" : DetectionSummary(task);
                    var action = FindCapability(task.Action, TaskCapabilityKind.Action)?.Name ?? "动作暂不可用";
                    body.Children.Add(Preview(detection + "  →  " + action, 12, Muted));
                    if (!task.Paused && RunState(task) is { AutomaticallyPaused: true } runState)
                        body.Children.Add(Text(runState.Reason ?? "等待运行条件满足", 12, Amber));
                    if (TaskStateLabel(task) == "状态未确认") body.Children.Add(Text("刷新以确认运行条件", 12, Amber));
                    row.Children.Add(CategoryTile(FindCapability(task.Action, TaskCapabilityKind.Action)?.Category));
                    Grid.SetColumn(body, 1); row.Children.Add(body);
                    var actions = Row(6); actions.VerticalAlignment = VerticalAlignment.Center;
                    var edit = Action("编辑"); edit.MinHeight = 32; edit.Padding = new Thickness(10, 4, 10, 4); edit.Click += (_, _) => SelectRule(task);
                    var toggle = Action(task.Paused ? "启用" : "暂停"); toggle.MinHeight = 32; toggle.Padding = new Thickness(10, 4, 10, 4);
                    toggle.IsEnabled = reachable; toggle.Click += async (_, _) => await ToggleRuleAsync(task);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(edit, "编辑 " + task.Title);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, (task.Paused ? "启用 " : "暂停 ") + task.Title);
                    actions.Children.Add(edit); actions.Children.Add(toggle);
                    var detail = DetectionSummary(task) + "\n" + Summary(task.Action, TaskCapabilityKind.Action);
                    if (task.RunConditions is { } gates)
                    {
                        if (gates.Hdr != "any") detail += gates.Hdr == "off" ? "\n主屏 HDR 关闭时运行" : "\n主屏 HDR 开启时运行";
                        if (gates.TimeWindow is { } allowed) detail += "\n" + TimeWindowLabel(allowed).Replace("接受新触发", "允许运行");
                    }
                    if (task.TimeWindow is { } window)
                    {
                        detail += "\n" + TimeWindowLabel(window);
                        if (status?.Observations.FirstOrDefault(o => o.TaskId == task.Id)?.Value is { } admission) detail += "\n" + admission;
                    }
                    actions.Children.Add(Help(detail, "任务详情：" + task.Title));
                    Grid.SetColumn(actions, 2); row.Children.Add(actions);
                    var item = new Border { Child = row, Background = Surface, BorderBrush = Line,
                        BorderThickness = new Thickness(0, cards.Children.Count == 0 ? 0 : 1, 0, 0) };
                    item.PointerEntered += (_, _) => item.Background = Hover;
                    item.PointerExited += (_, _) => item.Background = Surface;
                    cards.Children.Add(item);
                }
            }
        }
        if (status is not null)
        {
            var details = Action("查看活动记录 →"); details.Click += async (_, _) => { page = Page.Activity; Render(); await RefreshAsync(); };
            content.Children.Add(details);
        }
    }
    private void BuildEditor()
    {
        var back = Action("← 返回任务列表"); back.Click += async (_, _) => { if (!await ConfirmDiscardAsync()) return; editing = false; notice.IsOpen = false; Render(); }; content.Children.Add(back);
        var name = new TextBox { Header = "任务名称", PlaceholderText = "为这个任务起一个容易识别的名字", MaxLength = 120, Text = editor.Title };
        name.TextChanged += (_, _) => editor.Title = name.Text; content.Children.Add(name);
        var detectionHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var actionHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var save = Action("保存任务", true); save.IsEnabled = EditorCanSave; save.Click += async (_, _) => await SaveAsync();
        var behavior = Column(12); behavior.Children.Add(Text("03   运行方式", 18, Ink, true));
        var actionDescriptor = FindCapability(editor.Action, TaskCapabilityKind.Action);
        var timeWindowHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var runConditionsHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void UpdateTimeWindow()
        {
            timeWindowHost.Content = BuildTimeWindowEditor(FindCapability(editor.Action, TaskCapabilityKind.Action)?.SupportsTimeWindow == true);
            runConditionsHost.Content = BuildRunConditionsEditor();
        }
        var behaviorText = Text(actionDescriptor?.Behavior ?? "选择动作后，这里会说明执行和结束方式。", 13, Muted);
        var warningText = Text(editor.Version > 0 ? actionDescriptor?.EditWarning ?? "" : "", 12, Amber);
        var warningDetails = Details("修改任务须知", warningText);
        warningDetails.Visibility = string.IsNullOrWhiteSpace(warningText.Text) ? Visibility.Collapsed : Visibility.Visible;
        void SelectionChanged(TaskCapabilityKind kind)
        {
            if (kind == TaskCapabilityKind.Detection) BuildActions();
            else if (editor.Composite) detectionHost.Content = BuildDetectionSection(SelectionChanged);
            var selected = FindCapability(editor.Action, TaskCapabilityKind.Action);
            behaviorText.Text = selected?.Behavior ?? "选择动作后，这里会说明执行和结束方式。";
            warningText.Text = editor.Version > 0 ? selected?.EditWarning ?? "" : "";
            warningDetails.Visibility = string.IsNullOrWhiteSpace(warningText.Text) ? Visibility.Collapsed : Visibility.Visible;
            save.IsEnabled = EditorCanSave;
            UpdateTimeWindow();
        }
        void BuildActions() => actionHost.Content = BuildCapabilitySection(TaskCapabilityKind.Action,
            "02   执行什么", "选择条件满足后要执行的动作。", editor.Action, SelectionChanged);
        detectionHost.Content = BuildDetectionSection(SelectionChanged); content.Children.Add(detectionHost);
        BuildActions(); content.Children.Add(actionHost);
        behavior.Children.Add(Details("执行与结束方式", behaviorText));
        behavior.Children.Add(runConditionsHost);
        UpdateTimeWindow(); behavior.Children.Add(timeWindowHost);
        var enabled = new ToggleSwitch { Header = "启用任务", OnContent = "保存后按运行条件检测", OffContent = "只保存，暂不检测", IsOn = !editor.Paused };
        enabled.Toggled += (_, _) => editor.Paused = !enabled.IsOn; behavior.Children.Add(enabled);
        behavior.Children.Add(warningDetails);
        content.Children.Add(Card(behavior));
        footer.Children.Add(save);
        var cancel = Action("取消"); cancel.Click += async (_, _) => { if (!await ConfirmDiscardAsync()) return; editing = false; Render(); }; footer.Children.Add(cancel);
        if (editor.Version > 0)
        {
            var delete = Action("删除任务"); delete.Click += async (_, _) => await DeleteAsync(); content.Children.Add(delete);
            var reload = Action("重新读取已保存版本"); reload.Click += async (_, _) =>
            {
                if (!await ConfirmDiscardAsync()) return;
                await RefreshAsync(); if (!reachable) return;
                var saved = status?.Tasks.FirstOrDefault(x => x.Id == editor.Id);
                if (saved is null) { ShowNotice("任务已不存在，当前输入仍保留。"); return; } SelectRule(saved);
            }; content.Children.Add(reload);
        }
    }
    private Border BuildCapabilitySection(TaskCapabilityKind kind, string title, string hint, TaskInvocation? invocation, Action<TaskCapabilityKind> changed)
    {
        var body = Column(12); if (!string.IsNullOrWhiteSpace(title)) body.Children.Add(Text(title, 18, Ink, true));
        var choices = status is null ? [] : CapabilityPickerState.Candidates(status, editor, kind);
        var picker = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var parameters = Column(12);
        if (kind == TaskCapabilityKind.Action && editor.Composite && invocation is not null && !choices.Any(c => c.Id == invocation.CapabilityId))
            body.Children.Add(Text("原动作不支持全部条件，参数仍保留。请选择兼容动作后保存。", 12, Amber));
        void ShowParameters(TaskInvocation? current)
        {
            parameters.Children.Clear();
            var descriptor = FindCapability(current, kind);
            if (descriptor is null)
            {
                parameters.Children.Add(Text(current is null ? "选择类型后填写对应参数。" : "原类型暂不可用。配置已保留，请恢复对应功能后再编辑。", 12, Muted));
                return;
            }
            parameters.Children.Add(Help(descriptor.Description, "类型说明"));
            foreach (var field in descriptor.Fields.Where(f => f.Group is null)) AddField(parameters, descriptor, current!, field);
            foreach (var group in descriptor.Fields.Where(f => f.Group is not null).GroupBy(f => f.Group))
            {
                var extra = Column(12);
                foreach (var field in group) AddField(extra, descriptor, current!, field);
                parameters.Children.Add(new Expander { Header = group.Key, Content = extra,
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
        }
        TaskInvocation? Current() => kind == TaskCapabilityKind.Detection ? editor.Detection : editor.Action;
        Grid Selection() => BuildCapabilityPicker(kind == TaskCapabilityKind.Detection ? "检测类型" : "动作类型", Current(), kind,
            "CapabilityPicker." + kind, ChooseAsync);
        async Task ChooseAsync()
        {
            if (busy || closed || !editing || page != Page.Rules || !picker.IsLoaded || status is null) return;
            var snapshot = status; var id = editor.Id; var version = editor.TreeVersion; var before = Current();
            var selection = await ShowCapabilityPickerAsync(kind == TaskCapabilityKind.Detection ? "选择检测类型" : "选择动作类型",
                CapabilityPickerState.Candidates(snapshot, editor, kind), before);
            if (selection is null || closed || busy || !editing || page != Page.Rules || !picker.IsLoaded || editor.Id != id ||
                editor.TreeVersion != version || !ReferenceEquals(status, snapshot) || !ReferenceEquals(Current(), before)) return;
            try
            {
                editor.Select(selection, snapshot.Combinations);
                if (ReferenceEquals(Current(), before)) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (closed || !editing || page != Page.Rules || !picker.IsLoaded || editor.Id != id || editor.TreeVersion != version) return;
                    picker.Content = Selection(); ShowParameters(Current()); changed(kind);
                });
            }
            catch (ArgumentException failure) { ShowNotice(failure.Message); }
        }
        picker.Content = Selection();
        body.Children.Add(picker);
        body.Children.Add(parameters); ShowParameters(invocation);
        return string.IsNullOrWhiteSpace(title) ? new Border { Child = body } : Card(body);
    }
    private void AddField(StackPanel host, TaskCapability descriptor, TaskInvocation invocation, TaskField field, Action<string, string>? changed = null)
    {
        var body = Column(6);
        changed ??= (key, value) => editor.SetParameter(descriptor.Kind, key, value);
        invocation.Parameters.TryGetValue(field.Id, out var value);
        switch (field.Kind)
        {
            case TaskFieldKind.FilePath:
            case TaskFieldKind.DirectoryPath:
                var path = new TextBox { Header = field.Label, Text = value ?? "", MaxLength = field.MaximumLength };
                path.TextChanged += (_, _) => changed(field.Id, path.Text); body.Children.Add(path);
                AddLocalPathBrowse(body, path, field.Kind == TaskFieldKind.FilePath, field.Kind == TaskFieldKind.DirectoryPath); break;
            case TaskFieldKind.Number:
                var number = new NumberBox { Header = field.Label,
                    Minimum = (double)(field.Minimum ?? decimal.MinValue), Maximum = (double)(field.Maximum ?? decimal.MaxValue),
                    Value = double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, SmallChange = 1, LargeChange = 10 };
                number.ValueChanged += (_, _) => changed(field.Id,
                    double.IsNaN(number.Value) ? "" : number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                body.Children.Add(number); break;
            case TaskFieldKind.Text:
                var text = new TextBox { Header = field.Label, Text = value ?? "", MaxLength = field.MaximumLength };
                text.TextChanged += (_, _) => changed(field.Id, text.Text); body.Children.Add(text);
                if (field.BrowseLocalPath) AddLocalPathBrowse(body, text, true, true);
                break;
            case TaskFieldKind.TextList:
                if (field.EditAsList)
                { body.Children.Add(BuildNamedListEditor(field, value, selection => changed(field.Id, selection))); break; }
                var lines = new TextBox { Header = field.Label, MaxLength = field.MaximumLength,
                    AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 112, Text = value ?? "" };
                lines.TextChanged += (_, _) => changed(field.Id, lines.Text); body.Children.Add(lines); break;
            case TaskFieldKind.ProcessSelection:
                body.Children.Add(BuildProcessPicker(value, selection => changed(field.Id, selection)));
                break;
            case TaskFieldKind.HotkeyList:
                body.Children.Add(Text(field.Label, 14, Ink, true));
                body.Children.Add(BuildHotkeyPicker(value, descriptor.Kind == TaskCapabilityKind.Detection, selection => changed(field.Id, selection)));
                break;
            case TaskFieldKind.ExecutableSelection:
            case TaskFieldKind.TraySelection:
                body.Children.Add(Text(field.Label, 14, Ink, true));
                body.Children.Add(BuildProcessPicker(value, selection => changed(field.Id, selection), false, true, field.Kind == TaskFieldKind.TraySelection));
                break;
            case TaskFieldKind.Boolean:
                var toggle = new ToggleSwitch { Header = field.Label, IsOn = value == "true", OnContent = "开", OffContent = "关" };
                toggle.Toggled += (_, _) => changed(field.Id, toggle.IsOn ? "true" : "false"); body.Children.Add(toggle); break;
            case TaskFieldKind.Choice:
            case TaskFieldKind.Resource:
                var choices = field.Choices;
                if (!string.IsNullOrEmpty(value) && !choices.Any(x => x.Value == value)) choices = choices.Add(new(value, "原选择（当前不可用）"));
                var select = new ComboBox { Header = field.Label, PlaceholderText = "请选择", ItemsSource = choices, DisplayMemberPath = nameof(TaskChoice.Label), HorizontalAlignment = HorizontalAlignment.Stretch };
                select.SelectedItem = choices.FirstOrDefault(x => x.Value == value);
                select.SelectionChanged += (_, _) => { if (select.SelectedItem is TaskChoice item) changed(field.Id, item.Value); }; body.Children.Add(select);
                if (field.Choices.IsEmpty) { body.Children.Add(Text("当前没有可用选项，连接设备或恢复功能后刷新。", 12, Amber)); body.Children.Add(RefreshButton()); }
                break;
            default: body.Children.Add(Text("此参数类型需要更新 Axiom 后编辑。", 12, Amber)); break;
        }
        var layout = new Grid { ColumnSpacing = 6 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(body);
        if (!string.IsNullOrWhiteSpace(field.Hint))
        {
            var help = Help(field.Hint, field.Label + "说明"); help.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(help, 1); layout.Children.Add(help);
        }
        host.Children.Add(layout);
    }
    private void AddLocalPathBrowse(StackPanel body, TextBox path, bool files, bool directories)
    {
        var choices = Row(8);
        if (files) Add("选择文件…", true);
        if (directories) Add("选择文件夹…", false);
        body.Children.Add(choices);
        void Add(string label, bool file)
        {
            var browse = Action(label);
            browse.Click += async (_, _) =>
            {
                foreach (var button in choices.Children.OfType<Button>()) button.IsEnabled = false;
                try
                {
                    if (file)
                    {
                        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id); picker.FileTypeFilter.Add("*");
                        var selected = await picker.PickSingleFileAsync(); if (selected is not null) path.Text = selected.Path;
                    }
                    else
                    {
                        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id);
                        var selected = await picker.PickSingleFolderAsync(); if (selected is not null) path.Text = selected.Path;
                    }
                }
                catch (Exception failure) { ShowNotice("无法打开选择器，原输入已保留：" + failure.Message); }
                finally { foreach (var button in choices.Children.OfType<Button>()) button.IsEnabled = true; }
            };
            choices.Children.Add(browse);
        }
    }
    private void BuildSettings()
    {
        BuildThemeSettings();
        BuildShortcutSettings();
        var body = Column(14); body.Children.Add(Text("后台运行", 20, Ink, true)); body.Children.Add(Text("关闭窗口后，已启用的任务继续工作。再次打开 Axiom 即可查看或修改。", 14));
        content.Children.Add(Card(body));
        var login = Column(12); login.Children.Add(Text("登录启动", 20, Ink, true));
        var toggle = new ToggleSwitch { Header = "登录 Windows 后自动在后台运行", OnContent = "已登记",
            OffContent = startup is null || startup.Error is not null ? "状态未确认" : "已关闭",
            IsOn = startup?.Registered == true, IsEnabled = reachable && startup is not null && startup.Error is null && (startup.LauncherAvailable || startup.Registered) };
        toggle.Toggled += async (_, _) => await SetLoginStartupAsync(toggle.IsOn); login.Children.Add(toggle);
        login.Children.Add(Text("只启动后台，不打开窗口。需要管理任务时，再打开 Axiom。", 13, Muted));
        if (startupError is not null) login.Children.Add(Text(startupError, 13, Amber));
        else if (startup is null) login.Children.Add(Text("正在读取登录启动设置；可点击刷新重试。", 13, Muted));
        if (startup?.Registered == true && !startup.CurrentLocation)
        {
            login.Children.Add(Text("启动登记指向其他位置。若移动过程序，请更新为当前文件夹。", 13, Amber));
            var repair = Action("更新启动位置"); repair.IsEnabled = startup.LauncherAvailable;
            repair.Click += async (_, _) => await SetLoginStartupAsync(true); login.Children.Add(repair);
        }
        if (startup?.LauncherAvailable == false) login.Children.Add(Text("当前程序文件不完整，请从完整交付文件夹打开 Axiom 后设置。", 13, Amber));
        login.Children.Add(Help("以管理员权限在当前用户登录后启动后台，不保存密码。Windows 可能延迟启动；未自动运行时可检查任务计划程序中的 Axiom 任务。删除程序前请先关闭登录启动。", "登录启动说明"));
        login.Children.Add(RefreshButton()); content.Children.Add(Card(login));
        var stop = Column(12); stop.Children.Add(Text("停止所有任务", 20, Ink, true)); stop.Children.Add(Text("停止后不再检测；保存的任务会在下次打开时加载。", 14, Muted));
        var button = Action("停止后台并退出"); button.Click += async (_, _) => await StopBackendAsync(); stop.Children.Add(button); content.Children.Add(Card(stop));
    }
}
