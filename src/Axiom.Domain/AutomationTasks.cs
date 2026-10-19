using System.Collections.Immutable;

namespace Axiom.Domain;

public static class AutomationTaskLimits
{
    public const int MaximumTasks = 128;
}

public enum TaskCapabilityKind { Detection, Action }
public enum TaskFieldKind { Text, Boolean, Choice, Resource, TextList, FilePath, DirectoryPath, Number, ProcessSelection, ExecutableSelection, TraySelection, HotkeyList }
public sealed record TaskChoice(string Value, string Label);
public sealed record TaskField(string Id, string Label, TaskFieldKind Kind, bool Required,
    string Hint, string? DefaultValue, int MaximumLength, ImmutableArray<TaskChoice> Choices,
    decimal? Minimum = null, decimal? Maximum = null, string? Group = null, bool BrowseLocalPath = false, bool EditAsList = false);
public sealed record TaskCapability(string Id, int SchemaVersion, TaskCapabilityKind Kind,
    string Category, string Name, string Description, ImmutableArray<TaskField> Fields,
    string Behavior, string EditWarning, bool SupportsTimeWindow = false,
    bool IsStateCondition = false,
    bool SupportsCompositeDetection = false, bool SupportsRunConditions = true);
public sealed record TaskInvocation(string CapabilityId, int SchemaVersion, ImmutableDictionary<string, string> Parameters);
public sealed record AutomationTask(string Id, long Version, string Title, bool Paused,
    TaskInvocation Detection, TaskInvocation Action, int SchemaVersion = 1, WeeklyTimeWindow? TimeWindow = null,
    TaskDetectionExpression? DetectionExpression = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] TaskRunConditions? RunConditions = null);
public sealed record TaskRunConditions(string Hdr = "any", WeeklyTimeWindow? TimeWindow = null)
{
    public void Validate()
    {
        if (Hdr is not ("any" or "on" or "off")) throw new ArgumentException("主屏HDR条件无效。");
        TimeWindow?.Validate();
    }
    public string? PauseReason(bool? hdr, DateTimeOffset at, TimeZoneInfo zone)
    {
        Validate();
        if (Hdr != "any" && hdr is null) return "主屏HDR状态未知，等待系统通知。";
        if (Hdr != "any" && hdr != (Hdr == "on")) return Hdr == "on" ? "等待主屏HDR开启。" : "等待主屏HDR关闭。";
        return TimeWindow is not null && !TimeWindow.Contains(at, zone) ? "不在允许运行时段。" : null;
    }
}
public sealed record TaskRunStatus(string TaskId, bool AutomaticallyPaused, string? Reason);
public sealed record TaskCombination(string DetectionId, string ActionId);
public sealed record TaskObservation(string Name, string Value, string? TaskId = null);
public sealed record AutomationSnapshot(ImmutableArray<AutomationTask> Tasks,
    ImmutableArray<TaskCapability> Capabilities, ImmutableArray<TaskCombination> Combinations,
    bool DetectionAvailable, string State, string Reason, DateTimeOffset AtUtc,
    ImmutableArray<TaskObservation> Observations,
    int CompositeDetectionVersion = 0, int RunConditionsVersion = 0, ImmutableArray<TaskRunStatus> RunStates = default)
{
    private ImmutableArray<TaskRunStatus> runStates = RunStates.IsDefault ? [] : RunStates;
    public ImmutableArray<TaskRunStatus> RunStates
    {
        get => runStates;
        init => runStates = value.IsDefault ? [] : value;
    }
}
