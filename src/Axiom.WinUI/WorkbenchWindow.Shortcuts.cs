using Axiom.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private void BuildShortcutSettings()
    {
        var body = Column(14); body.Children.Add(Text("快捷键", 18, Ink, true));
        if (shortcutError is not null) body.Children.Add(Text(shortcutError, 13, Amber));
        if (shortcuts is null) body.Children.Add(Text("尚未读取快捷键，请刷新后重试。", 13, Muted));
        else
        {
            if (!shortcuts.ListenerAvailable) body.Children.Add(Text("快捷键监听暂不可用，请重新打开 Axiom。", 13, Amber));
            if (shortcuts.LastError is not null) body.Children.Add(Text(shortcuts.LastError, 13, Amber));
            foreach (var item in shortcuts.Shortcuts)
            {
                var row = Column(6);
                row.Children.Add(Text(shortcuts.Functions.FirstOrDefault(x => x.Id == item.Binding.FunctionId)?.Name ?? "原功能（当前不可用）", 16, Ink, true));
                var heading = Row(12); heading.Children.Add(Text(item.Binding.Chord.ToString(), 16, Ink, true));
                heading.Children.Add(Badge(!item.Binding.Enabled ? "已停用" : item.Registered ? "已生效" : "未生效", !item.Registered)); row.Children.Add(heading);
                if (item.Error is not null) row.Children.Add(Text(item.Error, 12, Amber));
                var actions = FlowActions();
                void AddAction(Button button)
                {
                    button.Content = Text((string)button.Content, 14); button.MinWidth = 0; button.Padding = new Thickness(8);
                    button.HorizontalAlignment = HorizontalAlignment.Stretch;
                    actions.Children.Add(button);
                }
                var edit = Action("编辑"); edit.IsEnabled = shortcuts.ListenerAvailable;
                edit.Click += async (_, _) => await EditShortcutAsync(item.Binding); AddAction(edit);
                var toggle = Action(item.Binding.Enabled ? "停用" : "启用"); toggle.IsEnabled = shortcuts.ListenerAvailable;
                toggle.Click += async (_, _) => await MutateShortcutAsync(new(1, "save-shortcut", Guid.NewGuid().ToString("N"), Shortcut: item.Binding with { Enabled = !item.Binding.Enabled })); AddAction(toggle);
                var delete = Action("删除"); delete.IsEnabled = shortcuts.ListenerAvailable;
                delete.Click += async (_, _) =>
                {
                    if (!await ConfirmAsync("删除这条快捷键？", "组合键将解除，其他快捷键和自动化任务继续工作。", "删除")) return;
                    await MutateShortcutAsync(new(1, "delete-shortcut", Guid.NewGuid().ToString("N"), Id: item.Binding.Id, ExpectedVersion: item.Binding.Version));
                }; AddAction(delete);
                var entry = SideBySide(row, actions);
                body.Children.Add(new Border { Child = entry, Padding = new Thickness(0, 12, 0, 12), BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0) });
            }
            if (shortcuts.Shortcuts.IsEmpty) body.Children.Add(Text("没有快捷键。新增一个，方便随时打开 Axiom。", 13, Muted));
        }
        var add = Action("新增快捷键"); add.IsEnabled = reachable && shortcuts?.ListenerAvailable == true && !shortcuts.Functions.IsEmpty && shortcuts.Shortcuts.Length < KeyboardShortcuts.MaximumBindings;
        add.Click += async (_, _) => await EditShortcutAsync(null); body.Children.Add(add); content.Children.Add(Card(body));
    }
    private async Task EditShortcutAsync(ShortcutBinding? binding)
    {
        if (busy || closed || shortcuts is null) return;
        var functions = shortcuts.Functions;
        var function = new ComboBox { Header = "调用功能", ItemsSource = functions, DisplayMemberPath = nameof(ApplicationFunction.Name), HorizontalAlignment = HorizontalAlignment.Stretch };
        function.SelectedItem = functions.FirstOrDefault(x => x.Id == binding?.FunctionId) ?? (binding is null ? functions.FirstOrDefault() : null);
        var chord = binding?.Chord ?? new(ShortcutModifiers.Control | ShortcutModifiers.Alt, "A");
        CheckBox Modifier(string name, ShortcutModifiers value) => new() { Content = name, IsChecked = chord.Modifiers.HasFlag(value) };
        var ctrl = Modifier("Ctrl", ShortcutModifiers.Control); var alt = Modifier("Alt", ShortcutModifiers.Alt);
        var shift = Modifier("Shift", ShortcutModifiers.Shift); var win = Modifier("Win", ShortcutModifiers.Windows);
        var modifiers = Row(14); modifiers.Children.Add(ctrl); modifiers.Children.Add(alt); modifiers.Children.Add(shift); modifiers.Children.Add(win);
        var key = new ComboBox { Header = "按键", ItemsSource = KeyboardShortcuts.Keys, SelectedItem = chord.Key, HorizontalAlignment = HorizontalAlignment.Stretch };
        var enabled = new CheckBox { Content = "启用这条快捷键", IsChecked = binding?.Enabled ?? true };
        var error = Text("", 13, Amber);
        var body = Column(16); body.Children.Add(function); body.Children.Add(Text("组合键", 14, Ink, true)); body.Children.Add(modifiers); body.Children.Add(key); body.Children.Add(enabled);
        body.Children.Add(Text("至少选择 Ctrl 或 Alt。若组合被其他程序占用，原设置会保留。", 12, Muted)); body.Children.Add(error);
        var editorPanel = new ContentControl { Content = body, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var dialog = new ContentDialog { Title = binding is null ? "新增快捷键" : "编辑快捷键", Content = editorPanel, PrimaryButtonText = "保存快捷键", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = shell.XamlRoot };
        var draftId = binding?.Id ?? Guid.NewGuid().ToString("D");
        var resultUnknown = false;
        dialog.Closing += (_, args) => { if (busy) args.Cancel = true; };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true; var deferral = args.GetDeferral();
            try
            {
                var flags = (ctrl.IsChecked == true ? ShortcutModifiers.Control : 0) | (alt.IsChecked == true ? ShortcutModifiers.Alt : 0) |
                    (shift.IsChecked == true ? ShortcutModifiers.Shift : 0) | (win.IsChecked == true ? ShortcutModifiers.Windows : 0);
                if (function.SelectedItem is not ApplicationFunction selected || key.SelectedItem is not string selectedKey)
                    throw new ArgumentException("请选择功能和按键。");
                var proposed = new ShortcutBinding(draftId, binding?.Version ?? 0, selected.Id, new(flags, selectedKey), enabled.IsChecked == true);
                KeyboardShortcuts.Validate(proposed);
                SetBusy(true); editorPanel.IsEnabled = false; dialog.IsPrimaryButtonEnabled = false; dialog.CloseButtonText = "请稍候";
                var response = await WorkbenchClient.RequestAsync(new(1, "save-shortcut", Guid.NewGuid().ToString("N"), Shortcut: proposed));
                if (response.Type != "shortcuts" || response.Shortcuts is null) { error.Text = response.Error ?? "未能保存，请重试。"; return; }
                shortcuts = response.Shortcuts; shortcutError = null; args.Cancel = false;
                ShowNotice(enabled.IsChecked == true ? "快捷键已保存并生效。" : "快捷键已保存，当前已停用。", true);
            }
            catch (ArgumentException problem) { error.Text = problem.Message; }
            catch (Exception) { resultUnknown = true; error.Text = "暂未收到保存结果，请取消后刷新核对，避免重复添加。"; }
            finally { editorPanel.IsEnabled = true; dialog.IsPrimaryButtonEnabled = !resultUnknown; dialog.CloseButtonText = "取消"; SetBusy(false); deferral.Complete(); }
        };
        await ShowDialogAsync(dialog); Render();
    }
    private async Task MutateShortcutAsync(AutomationMessage request)
    {
        if (busy || closed) return; SetBusy(true);
        try
        {
            var response = await WorkbenchClient.RequestAsync(request);
            if (response.Type != "shortcuts" || response.Shortcuts is null) { ShowNotice(response.Error ?? "未能更新快捷键，请刷新后重试。"); return; }
            shortcuts = response.Shortcuts; shortcutError = null;
            ShowNotice(request.Type == "delete-shortcut" ? "快捷键已删除。" : request.Shortcut!.Enabled ? "快捷键已生效。" : "快捷键已停用。", true);
        }
        catch (Exception) { shortcutError = "快捷键更新结果未确认，请刷新核对。"; ShowNotice(shortcutError); }
        finally { SetBusy(false); Render(); }
    }
}
