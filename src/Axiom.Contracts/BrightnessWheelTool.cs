using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Axiom.Domain;

namespace Axiom.Contracts;

public sealed record BrightnessWheelSettings(long Version = 0, bool Enabled = false,
    ShortcutModifiers Modifiers = ShortcutModifiers.Control | ShortcutModifiers.Alt, string Key = "",
    string Target = "primary", int Step = 2, int SmoothMilliseconds = 150, int WritesPerSecond = 18);
public sealed record BrightnessWheelSnapshot(BrightnessWheelSettings Settings, string State,
    bool Listening = false, bool ManualTakenOver = false, bool Running = false, string? LastResult = null);

public static class BrightnessWheelTool
{
    public const string TaskId = "725968f6-98c5-4b13-86ec-e3e6485ffcf9";
    public const string Name = "快捷键 + 滚轮调亮度";
    public const string Trigger = "brightness.wheel-tool";
    public const string Command = "brightness.wheel-gesture";
    public static void Validate(BrightnessWheelSettings settings)
    {
        if (settings.Version < 0 || settings.Modifiers == ShortcutModifiers.None || (settings.Modifiers & ~(ShortcutModifiers)15) != 0 ||
            settings.Key is null || settings.Key != "" && !HotkeyCapabilities.Keys.Contains(settings.Key) || settings.Target is not ("primary" or "all") ||
            settings.Step is < 1 or > 20 || settings.SmoothMilliseconds is < 50 or > 1000 || settings.WritesPerSecond is < 1 or > 60)
            throw new ArgumentException("请选择修饰键和可选主键；每格1–20百分点、平滑50–1000毫秒、频率1–60次/秒。");
    }
    public static TaskInvocation Gesture(string subscription, long fromSteps) => new(Command, 1,
        ImmutableDictionary<string, string>.Empty.Add("subscription", subscription).Add("fromSteps", fromSteps.ToString(CultureInfo.InvariantCulture)));
    public static (string Subscription, long FromSteps) ValidateGesture(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Command || invocation.SchemaVersion != 1 || invocation.Parameters.Count != 2 ||
            !Guid.TryParseExact(invocation.Parameters.GetValueOrDefault("subscription"), "N", out _) ||
            !long.TryParse(invocation.Parameters.GetValueOrDefault("fromSteps"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var from) || Math.Abs((decimal)from) > 1_000_000_000)
            throw new ArgumentException("亮度滚轮手势授权无效。");
        return (invocation.Parameters["subscription"], from);
    }
    public static string Describe(BrightnessWheelSettings settings) => string.Join(" + ", new[]
    {
        settings.Modifiers.HasFlag(ShortcutModifiers.Control) ? "Ctrl" : null, settings.Modifiers.HasFlag(ShortcutModifiers.Alt) ? "Alt" : null,
        settings.Modifiers.HasFlag(ShortcutModifiers.Shift) ? "Shift" : null, settings.Modifiers.HasFlag(ShortcutModifiers.Windows) ? "Win" : null,
        settings.Key.Length > 0 ? settings.Key : null, "滚轮"
    }.Where(s => s is not null));
}

// Pure high-resolution wheel accounting. The hook owns this on its message thread.
public sealed class BrightnessWheelDelta
{
    private int remainder;
    public int Add(int delta)
    {
        remainder += delta; var steps = remainder / 120; remainder %= 120; return steps;
    }
    public void Reset() => remainder = 0;
}
