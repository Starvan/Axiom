using Axiom.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private StackPanel BuildNamedListEditor(TaskField field, string? value, Action<string> changed)
    {
        var selected = value ?? "";
        var panel = Column(8);
        panel.Children.Add(Text(field.Label, 14, Ink, true));
        var summary = Text("", 13, Muted); panel.Children.Add(summary);
        void RefreshSummary()
        {
            var entries = selected.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            summary.Text = entries.Length == 0 ? "尚未填写" : string.Join("、", entries.Take(3)) + (entries.Length > 3 ? $" 等{entries.Length}项" : "");
        }
        var edit = Action("编辑名单…");
        edit.Click += async (_, _) =>
        {
            var input = new TextBox { Header = field.Label, AcceptsReturn = true, Text = selected, TextWrapping = TextWrapping.Wrap,
                MaxLength = field.MaximumLength, MinHeight = 180, MaxHeight = 320 };
            var body = Column(10); body.Children.Add(Text(field.Hint, 13, Muted)); body.Children.Add(input);
            var error = Text("", 12, Amber); body.Children.Add(error);
            var dialog = new ContentDialog { Title = "编辑" + field.Label, Content = body, PrimaryButtonText = "使用此名单", CloseButtonText = "取消", XamlRoot = shell.XamlRoot };
            string? confirmed = null;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                // Preserve meaningful spaces/case in local names. Only identical whole lines are merged.
                var names = input.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
                if (field.Required && names.Length == 0 || names.Length > 16 || names.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.Any(char.IsControl)))
                { args.Cancel = true; error.Text = "每行填写一个名称，最多16项，每项最多255个字符，不能包含控制字符。"; return; }
                confirmed = string.Join("\n", names);
            };
            if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && confirmed is not null)
            { selected = confirmed; changed(selected); RefreshSummary(); }
        };
        panel.Children.Add(edit); RefreshSummary(); return panel;
    }
}
