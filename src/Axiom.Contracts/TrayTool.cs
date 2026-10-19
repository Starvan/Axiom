using Axiom.Domain;

namespace Axiom.Contracts;

public sealed record TrayToolSettings(long Version, bool Enabled, string Mode, string[] Executables, int WaitSeconds = 30);
public sealed record TrayToolSnapshot(TrayToolSettings Settings, string State, bool Running = false,
    DateTimeOffset? LastRunUtc = null, OutcomeStatus? LastStatus = null, string? LastResult = null);

public static class TrayTool
{
    public const string TaskId = "fa1b8ac6-151f-47d2-bf39-9d372321c743";
    public const string Name = "任务栏托盘管理";
    public const string Trigger = "tray.tool-started";
    public const string Startup = "startup";
    public const string Continuous = "continuous";
    public static void Validate(TrayToolSettings settings)
    {
        if (settings.Version < 0 || settings.Mode is not (Startup or Continuous) || settings.Executables is null || settings.Executables.Length is < 1 or > 32)
            throw new ArgumentException("托盘工具设置无效；请选择 1–32 个程序及隐藏模式。");
        if (settings.WaitSeconds is < 1 or > TrayCapabilities.MaximumWaitSeconds)
            throw new ArgumentException($"等待托盘图标出现的时间须为 1–{TrayCapabilities.MaximumWaitSeconds} 秒。");
        _ = ExecutablePaths.Parse(string.Join("\n", settings.Executables));
    }
}
