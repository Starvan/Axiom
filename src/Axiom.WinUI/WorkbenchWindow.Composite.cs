using System.Collections.Immutable;
using Axiom.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private bool EditorCanSave => editor.CanSave(status);
    private static IEnumerable<TaskInvocation> TaskDetections(AutomationTask task)
    {
        if (task.DetectionExpression is null) return [task.Detection];
        return Leaves(task.DetectionExpression);
        static IEnumerable<TaskInvocation> Leaves(TaskDetectionExpression node)
        {
            if (node.Kind == "leaf" && node.Invocation is not null) yield return node.Invocation;
            if (!node.Children.IsDefault)
                foreach (var child in node.Children) foreach (var leaf in Leaves(child)) yield return leaf;
        }
    }
    private string DetectionSummary(AutomationTask task)
    {
        if (task.DetectionExpression is null) return Summary(task.Detection, TaskCapabilityKind.Detection);
        var text = Describe(task.DetectionExpression);
        return text.Length <= 360 ? text : text[..360] + "…";
        string Describe(TaskDetectionExpression node) => node.Kind == "leaf" ? node.Invocation is null ? "待选择条件" : Summary(node.Invocation, TaskCapabilityKind.Detection) :
            GroupLabel(node.Kind) + "（" + string.Join("；", node.Children.IsDefault ? Enumerable.Empty<string>() : node.Children.Select(Describe)) + "）";
    }
    private static string GroupLabel(string kind) => kind switch { "all" => "全部满足", "any" => "任一满足", "not" => "不满足此条件", _ => "条件暂不可用" };
    private Border BuildDetectionSection(Action<TaskCapabilityKind> changed)
    {
        var supported = status?.CompositeDetectionVersion >= 1;
        if (!supported && !editor.Composite)
            return BuildCapabilitySection(TaskCapabilityKind.Detection, "何时执行", "选择需要检测的状态或事件。", editor.Detection, changed);
        var panel = Column(12); panel.Children.Add(Text("何时执行", 18, Ink, true));
        var mode = new ComboBox { ItemsSource = new[] { "一个条件", "多个条件" }, SelectedIndex = editor.Composite ? 1 : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = supported };
        mode.SelectionChanged += (_, _) =>
        {
            if (mode.SelectedIndex == (editor.Composite ? 1 : 0)) return;
            try
            {
                if (mode.SelectedIndex == 1) editor.EnableComposite(status!.Capabilities, status.Combinations);
                else editor.DisableComposite();
                Render();
            }
            catch (ArgumentException failure) { ShowNotice(failure.Message); mode.SelectedIndex = editor.Composite ? 1 : 0; }
        };
        mode.Width = 180; mode.HorizontalAlignment = HorizontalAlignment.Left;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(mode, "条件组合方式");
        panel.Children.Add(mode);
        if (!editor.Composite)
        {
            panel.Children.Add(BuildCapabilitySection(TaskCapabilityKind.Detection, "", "检测一种事件或持续状态。", editor.Detection, changed));
            return Card(panel);
        }
        panel.Children.Add(Help("全部：每项都满足。任一：至少一项满足。不满足此条件：确认不满足时才成立。只组合持续状态，不能把快捷键或定时事件当作状态。", "多个条件的组合方式"));
        panel.Children.Add(Help("状态暂未确认时，组合也可能暂未确认；不满足此条件不会把“未确认”当成满足。持续动作的结束与恢复按所选动作说明执行。", "状态未确认时的行为"));
        if (!supported) panel.Children.Add(Text("当前后台版本尚不支持组合检测。配置和输入已保留，请更新后台后刷新。", 12, Amber));
        var treeHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = supported };
        var redrawQueued = false;
        void RefreshTree()
        {
            if (redrawQueued) return;
            redrawQueued = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                redrawQueued = false;
                if (!editing || closed || !editor.Composite) return;
                treeHost.Content = BuildNode([], editor.TreeVersion);
                changed(TaskCapabilityKind.Detection);
            });
        }
        void Change(Action operation)
        {
            try { operation(); RefreshTree(); }
            catch (Exception failure) when (failure is ArgumentException or InvalidOperationException) { ShowNotice(failure.Message); RefreshTree(); }
        }
        Border BuildNode(int[] path, long version)
        {
            var node = editor.Node(path); var body = Column(10);
            var children = node.Children.IsDefault ? ImmutableArray<TaskDetectionExpression>.Empty : node.Children;
            var heading = Row(8);
            heading.Children.Add(Text(node.Kind == "leaf" ? "状态条件" : GroupLabel(node.Kind), 16, Ink, true));
            if (path.Length > 0) heading.Children.Add(Text("第" + (path[^1] + 1) + "项", 12, Muted));
            body.Children.Add(heading);
            if (node.Kind == "leaf")
            {
                var choices = status is null ? [] : CapabilityPickerState.Candidates(status, editor, TaskCapabilityKind.Detection, stateOnly: true);
                var picker = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
                var fields = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
                void ShowLeafParameters(TaskInvocation? invocation)
                {
                    var parameters = Column(10); var descriptor = FindCapability(invocation, TaskCapabilityKind.Detection);
                    if (invocation is not null && (descriptor?.IsStateCondition != true || !choices.Any(c => c.Id == invocation.CapabilityId)))
                        parameters.Children.Add(Text("原条件当前不可用或与动作不兼容，配置仍保留。请恢复对应功能或选择兼容动作。", 12, Amber));
                    else if (choices.Length == 0) parameters.Children.Add(Text("当前没有兼容的状态检测，请先选择其他动作或刷新后台目录。", 12, Amber));
                    if (descriptor is not null && invocation is not null)
                    {
                        parameters.Children.Add(Text(descriptor.Description, 13, Muted));
                        void Set(string key, string value)
                        {
                            if (editor.TreeVersion == version && editor.Node(path).Invocation is { } current &&
                                current.CapabilityId == invocation.CapabilityId && current.SchemaVersion == invocation.SchemaVersion)
                                editor.SetLeafParameter(path, key, value);
                        }
                        foreach (var field in descriptor.Fields.Where(f => f.Group is null)) AddField(parameters, descriptor, invocation, field, Set);
                        foreach (var group in descriptor.Fields.Where(f => f.Group is not null).GroupBy(f => f.Group))
                        {
                            var extra = Column(10);
                            foreach (var field in group) AddField(extra, descriptor, invocation, field, Set);
                            parameters.Children.Add(new Expander { Header = group.Key, Content = extra, HorizontalAlignment = HorizontalAlignment.Stretch,
                                HorizontalContentAlignment = HorizontalAlignment.Stretch });
                        }
                    }
                    else parameters.Children.Add(Text("选择状态类型后填写它的参数。", 12, Muted));
                    fields.Content = parameters;
                }
                Grid Selection() => BuildCapabilityPicker("状态类型", editor.Node(path).Invocation, TaskCapabilityKind.Detection,
                    "CapabilityPicker.State." + string.Join(".", path), ChooseAsync);
                async Task ChooseAsync()
                {
                    if (busy || closed || !editing || page != Page.Rules || !picker.IsLoaded || editor.TreeVersion != version || status is null) return;
                    var snapshot = status; var id = editor.Id; var before = editor.Node(path).Invocation;
                    var selection = await ShowCapabilityPickerAsync("选择状态类型",
                        CapabilityPickerState.Candidates(snapshot, editor, TaskCapabilityKind.Detection, stateOnly: true), before);
                    if (selection is null || closed || busy || !editing || page != Page.Rules || !picker.IsLoaded || editor.Id != id ||
                        editor.TreeVersion != version || !ReferenceEquals(status, snapshot) || !ReferenceEquals(editor.Node(path).Invocation, before)) return;
                    try
                    {
                        editor.SelectLeaf(path, selection, snapshot.Combinations);
                        if (ReferenceEquals(editor.Node(path).Invocation, before)) return;
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (!editing || closed || page != Page.Rules || !picker.IsLoaded || editor.Id != id || editor.TreeVersion != version) return;
                            picker.Content = Selection(); ShowLeafParameters(editor.Node(path).Invocation); changed(TaskCapabilityKind.Detection);
                        });
                    }
                    catch (ArgumentException failure) { ShowNotice(failure.Message); RefreshTree(); }
                }
                picker.Content = Selection();
                body.Children.Add(picker); body.Children.Add(fields); ShowLeafParameters(node.Invocation);
            }
            else
            {
                var operators = new[] { new TaskChoice("all", "全部满足（AND）"), new TaskChoice("any", "任一满足（OR）"), new TaskChoice("not", "取反（NOT）") };
                var operation = new ComboBox { Header = "组合方式", ItemsSource = operators, DisplayMemberPath = nameof(TaskChoice.Label),
                    SelectedItem = operators.FirstOrDefault(c => c.Value == node.Kind), HorizontalAlignment = HorizontalAlignment.Stretch };
                operation.SelectionChanged += (_, _) =>
                {
                    if (editor.TreeVersion != version || operation.SelectedItem is not TaskChoice selection || selection.Value == node.Kind) return;
                    Change(() => editor.ChangeGroup(path, selection.Value));
                };
                body.Children.Add(operation);
                if (node.Kind == "not") body.Children.Add(Text("取反恰好包含一个条件，也可以取反整个条件组。", 12, Muted));
                if (children.Length < (node.Kind == "not" ? 1 : 2)) body.Children.Add(Text(node.Kind == "not" ? "请加入一个状态条件。" : "此组至少需两项，请继续添加条件或移除这个组。", 12, Amber));
                var nested = Column(10); nested.Margin = new Thickness(16, 0, 0, 0);
                for (var i = 0; i < children.Length; i++) nested.Children.Add(BuildNode([.. path, i], version));
                body.Children.Add(nested);
                var add = Row(8);
                var leaf = Action("＋ 状态条件"); leaf.IsEnabled = node.Kind != "not" || children.Length == 0;
                leaf.Click += (_, _) => { if (editor.TreeVersion == version) Change(() => editor.AddLeaf(path)); }; add.Children.Add(leaf);
                var group = Action("＋ 条件组"); group.IsEnabled = node.Kind != "not" || children.Length == 0;
                group.Click += (_, _) => { if (editor.TreeVersion == version) Change(() => editor.AddGroup(path)); }; add.Children.Add(group);
                body.Children.Add(add);
            }
            var tools = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
            tools.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tools.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            void Tool(Button button)
            {
                var index = tools.Children.Count;
                if (index % 2 == 0) tools.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(button, index / 2); Grid.SetColumn(button, index % 2);
                button.HorizontalAlignment = HorizontalAlignment.Stretch; tools.Children.Add(button);
            }
            var negate = Action(node.Kind == "not" ? "取消取反" : "取反此条件");
            negate.Click += (_, _) => { if (editor.TreeVersion == version) Change(() => editor.ToggleNot(path)); }; Tool(negate);
            if (node.Kind == "leaf")
            {
                var wrap = Action("放入新条件组"); wrap.Click += (_, _) => { if (editor.TreeVersion == version) Change(() => editor.WrapGroup(path)); }; Tool(wrap);
            }
            if (path.Length > 0)
            {
                var siblings = editor.Node(path[..^1]).Children;
                var up = Action("上移"); up.IsEnabled = path[^1] > 0;
                up.Click += (_, _) => { if (editor.TreeVersion == version) Change(() => editor.Move(path, -1)); }; Tool(up);
                var down = Action("下移"); down.IsEnabled = path[^1] + 1 < siblings.Length;
                down.Click += (_, _) => { if (editor.TreeVersion == version) Change(() => editor.Move(path, 1)); }; Tool(down);
                var move = Action("移到其他组…");
                move.Click += async (_, _) =>
                {
                    if (editor.TreeVersion != version) return;
                    var destinations = editor.Nodes().Where(p => p.Node.Kind is "all" or "any" &&
                        !p.Path.SequenceEqual(path[..^1]) && !(p.Path.Length >= path.Length && p.Path.Take(path.Length).SequenceEqual(path)))
                        .Select(p => new DetectionDestination(p.Path, p.Path.Length == 0 ? "根组" : "第 " + string.Join(" / ", p.Path.Select(i => i + 1)) + " 组 · " + GroupLabel(p.Node.Kind))).ToArray();
                    if (destinations.Length == 0) { ShowNotice("没有其他可用条件组，请先添加全部或任一组。"); return; }
                    var picker = new ComboBox { Header = "移入条件组", ItemsSource = destinations, DisplayMemberPath = nameof(DetectionDestination.Label), SelectedIndex = 0,
                        HorizontalAlignment = HorizontalAlignment.Stretch };
                    var dialog = new ContentDialog { Title = "移动条件或条件组", Content = picker, PrimaryButtonText = "移动", CloseButtonText = "取消", XamlRoot = shell.XamlRoot,
                        DefaultButton = ContentDialogButton.Close };
                    if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && editor.TreeVersion == version && picker.SelectedItem is DetectionDestination target)
                        Change(() => editor.MoveToGroup(path, target.Path));
                };
                Tool(move);
            }
            var remove = Action(path.Length == 0 ? "清空组合" : "删除");
            remove.Click += async (_, _) =>
            {
                if (editor.TreeVersion != version) return;
                if (node.Kind != "leaf" && !await ConfirmAsync("删除这组条件？", "组内检测和未保存的参数会一并移除；已保存的规则暂不改变。", "删除")) return;
                if (editor.TreeVersion == version) Change(() => editor.Remove(path));
            };
            Tool(remove); body.Children.Add(tools);
            return Card(body, path.Length == 0 ? Surface : Soft, 16);
        }
        treeHost.Content = BuildNode([], editor.TreeVersion);
        panel.Children.Add(treeHost);
        panel.Children.Add(Text("最多4层、16条状态条件；全部或任一组至少两项。动作只显示每条条件都支持的类型。", 12, Muted));
        return Card(panel);
    }
    private sealed record DetectionDestination(int[] Path, string Label);
}
