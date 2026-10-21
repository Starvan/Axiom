using Axiom.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private Grid BuildCapabilityPicker(string label, TaskInvocation? invocation, TaskCapabilityKind kind, string automationId, Func<Task> choose)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var caption = Column(4); caption.Children.Add(Text(label, 12, Muted));
        var descriptor = FindCapability(invocation, kind);
        caption.Children.Add(Text(descriptor?.Name ?? (invocation is null ? "尚未选择" : "原类型暂不可用"), 15, Ink, true));

        row.Children.Add(caption);
        var button = Action(invocation is null ? "选择…" : "更改…"); button.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(button, "选择" + label);
        AutomationProperties.SetAutomationId(button, automationId);
        button.Click += async (_, _) => await choose();
        var actions = Row(4);
        if (descriptor is not null) actions.Children.Add(Help(descriptor.Description, "类型说明：" + descriptor.Name));
        actions.Children.Add(button); Grid.SetColumn(actions, 1); row.Children.Add(actions);
        return row;
    }

    private sealed record CapabilityCategory(string? Value, string Label);

    private async Task<TaskCapability?> ShowCapabilityPickerAsync(string title, TaskCapability[] candidates, TaskInvocation? current)
    {
        if (busy || closed || activeDialog is not null) return null;
        var draft = new CapabilityPickerState(candidates, current?.CapabilityId);
        var body = new Grid { RowSpacing = 12 };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var detailRow = new RowDefinition { Height = new GridLength(100) };
        body.RowDefinitions.Add(detailRow);
        var search = new TextBox { Header = "查找类型", PlaceholderText = "搜索名称、说明或类别", MaxLength = 160 };
        AutomationProperties.SetName(search, "搜索能力类型"); AutomationProperties.SetAutomationId(search, "CapabilityPicker.Search");
        body.Children.Add(search);
        var lists = new Grid { ColumnSpacing = 12 };
        lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var categories = new ListView { SelectionMode = ListViewSelectionMode.Single,
            ItemsSource = new[] { new CapabilityCategory(null, "全部") }.Concat(draft.Categories.Select(c => new CapabilityCategory(c, c))).ToArray(),
            DisplayMemberPath = nameof(CapabilityCategory.Label), SelectedIndex = 0 };
        AutomationProperties.SetName(categories, "能力类别"); AutomationProperties.SetAutomationId(categories, "CapabilityPicker.Categories");
        ScrollViewer.SetHorizontalScrollBarVisibility(categories, ScrollBarVisibility.Disabled);
        var results = new ListView { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(results, "可选类型"); AutomationProperties.SetAutomationId(results, "CapabilityPicker.Results");
        ScrollViewer.SetHorizontalScrollBarVisibility(results, ScrollBarVisibility.Disabled);
        var resultHost = new Grid(); resultHost.Children.Add(results);
        var empty = Text("没有匹配的类型，请修改关键词或选择其他类别。", 13, Muted); empty.Margin = new Thickness(8);
        empty.IsHitTestVisible = false; resultHost.Children.Add(empty);
        lists.Children.Add(categories); Grid.SetColumn(resultHost, 1); lists.Children.Add(resultHost);
        Grid.SetRow(lists, 1); body.Children.Add(lists);
        var details = Text("", 13, Muted);
        AutomationProperties.SetName(details, "所选类型说明"); AutomationProperties.SetAutomationId(details, "CapabilityPicker.Details");
        var detailScroll = new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(detailScroll, 2); body.Children.Add(detailScroll);
        var dialog = new ContentDialog { Title = title, Content = body, PrimaryButtonText = "选用", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = false, XamlRoot = shell.XamlRoot };
        dialog.Resources["ContentDialogMaxWidth"] = 760d;
        AutomationProperties.SetName(dialog, title); AutomationProperties.SetAutomationId(dialog, "CapabilityPicker.Dialog");
        var rendering = false;
        void UpdateDetails()
        {
            dialog.IsPrimaryButtonEnabled = draft.Selected is not null;
            details.Text = draft.Selected is { } selected ? selected.Category + " / " + selected.Name + "\n" + selected.Description +
                (string.IsNullOrWhiteSpace(selected.Behavior) ? "" : "\n" + selected.Behavior) :
                candidates.Length == 0 ? "当前没有兼容的类型，请取消后更换条件或动作，或刷新后台目录。" : "请选择一个类型，选用后才会更改当前任务。";
        }
        void RenderResults()
        {
            draft.Filter((categories.SelectedItem as CapabilityCategory)?.Value, search.Text);
            rendering = true;
            try
            {
                var items = draft.Visible.Select(c =>
                {
                    var item = new ListViewItem { Content = Text(c.Name, 14, Ink), Tag = c, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    AutomationProperties.SetName(item, c.Category + " / " + c.Name);
                    return item;
                }).ToArray();
                results.ItemsSource = items;
                results.SelectedItem = items.FirstOrDefault(i => ReferenceEquals(i.Tag, draft.Selected));
                empty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { rendering = false; }
            UpdateDetails();
        }
        void Resize()
        {
            // Lists and long descriptions scroll independently; the dialog buttons stay outside this height.
            body.Width = Math.Clamp(shell.ActualWidth - 96, 280, 680);
            body.Height = Math.Clamp(shell.ActualHeight - 205, 250, 500);
            detailRow.Height = new GridLength(shell.ActualHeight < 600 ? 64 : 100);
        }
        SizeChangedEventHandler resized = (_, _) => Resize();
        search.TextChanged += (_, _) => RenderResults();
        categories.SelectionChanged += (_, _) => RenderResults();
        results.SelectionChanged += (_, _) =>
        {
            if (rendering) return;
            draft.Select((results.SelectedItem as ListViewItem)?.Tag as TaskCapability); UpdateDetails();
        };
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = draft.Selected is null;
        dialog.Opened += (_, _) =>
        {
            shell.SizeChanged += resized; Resize(); search.Focus(FocusState.Programmatic);
            if (results.SelectedItem is { } selected) results.ScrollIntoView(selected);
        };
        dialog.Closed += (_, _) => shell.SizeChanged -= resized;
        Resize(); RenderResults();
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary ? draft.Selected : null;
    }
}
