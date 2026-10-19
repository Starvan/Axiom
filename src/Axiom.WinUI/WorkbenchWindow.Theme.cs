using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Axiom.WinUI;

public sealed partial class WorkbenchWindow
{
    private ElementTheme themePreference;
    private ContentDialog? activeDialog;

    private void InitializeTheme()
    {
        try { themePreference = UiThemePreference.Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ShowNotice("无法读取外观设置，暂时跟随系统。可在设置中重新选择。"); }
        shell.ActualThemeChanged += (_, _) => UpdateThemeColors();
        shell.Loaded += (_, _) => UpdateThemeColors();
        shell.RequestedTheme = themePreference;
        UpdateThemeColors();
    }

    private void UpdateThemeColors()
    {
        var dark = shell.ActualTheme == ElementTheme.Dark;
        void Set(SolidColorBrush brush, byte r, byte g, byte b) => brush.Color = Windows.UI.Color.FromArgb(255, r, g, b);
        // Mutate shared brushes so existing inputs and open dialogs keep their contents.
        if (dark)
        {
            Set(Canvas, 19, 26, 36); Set(Surface, 27, 36, 49);
            Set(Rail, 14, 21, 31); Set(RailSelected, 30, 58, 59);
            Set(Blue, 151, 187, 245); Set(BlueSoft, 33, 49, 74); Set(Hover, 33, 44, 58);
            Set(Ink, 234, 239, 243); Set(Muted, 165, 177, 188); Set(Line, 48, 61, 77);
            Set(Accent, 149, 225, 195); Set(Primary, 43, 119, 97); Set(Soft, 31, 57, 54);
            Set(Amber, 239, 194, 116); Set(PaleAmber, 60, 46, 28);
        }
        else
        {
            Set(Canvas, 243, 246, 250); Set(Surface, 255, 255, 255);
            Set(Rail, 24, 34, 47); Set(RailSelected, 40, 66, 69);
            Set(Blue, 65, 103, 168); Set(BlueSoft, 234, 240, 251); Set(Hover, 245, 248, 251);
            Set(Ink, 30, 38, 47); Set(Muted, 99, 110, 122); Set(Line, 229, 234, 240);
            Set(Accent, 36, 99, 83); Set(Primary, 36, 99, 83); Set(Soft, 233, 243, 238);
            Set(Amber, 139, 89, 20); Set(PaleAmber, 255, 246, 227);
        }
        var title = AppWindow.TitleBar;
        title.ButtonForegroundColor = Ink.Color;
        title.ButtonInactiveForegroundColor = Muted.Color;
        title.ButtonHoverForegroundColor = Ink.Color;
        title.ButtonHoverBackgroundColor = Line.Color;
        title.ButtonPressedForegroundColor = Ink.Color;
        title.ButtonPressedBackgroundColor = Soft.Color;
        if (activeDialog is not null) activeDialog.RequestedTheme = shell.ActualTheme;
    }

    private void BuildThemeSettings()
    {
        var body = Column(12);
        body.Children.Add(Text("外观", 20, Ink, true));
        var picker = new ComboBox
        {
            Header = "主题", ItemsSource = new[] { "跟随系统", "浅色", "深色" },
            SelectedIndex = (int)themePreference, MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(picker, "主题");
        picker.SelectionChanged += (_, _) =>
        {
            var next = (ElementTheme)picker.SelectedIndex;
            if (next == themePreference || picker.SelectedIndex < 0) return;
            try
            {
                UiThemePreference.Save(next);
                themePreference = next;
                shell.RequestedTheme = next;
                UpdateThemeColors();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                picker.SelectedIndex = (int)themePreference;
                ShowNotice("未能保存外观设置，请重试。原设置已保留。");
            }
        };
        body.Children.Add(picker);
        ToolTipService.SetToolTip(picker, "自动保存并立即生效");
        content.Children.Add(Card(body));
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        dialog.RequestedTheme = shell.ActualTheme;
        activeDialog = dialog;
        try { return await dialog.ShowAsync(); }
        finally { activeDialog = null; }
    }
}
