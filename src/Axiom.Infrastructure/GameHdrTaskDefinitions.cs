using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class ProcessRunningTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor { get; } = new("process.running", 2, TaskCapabilityKind.Detection,
        "程序", "程序运行", "检测当前用户会话中指定程序的运行状态。",
        [new("processNames", "检测的程序", TaskFieldKind.ProcessSelection, true,
            "任意一个程序运行即满足条件，包含没有窗口的进程。仅检测当前用户会话。", null, 32768, [])],
        "任意一个指定程序运行期间条件成立；全部退出后条件结束。", "", IsStateCondition: true);
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, Descriptor);
        var names = ProcessNameSelection.Parse(configuration.Parameters["processNames"]);
        var conditions = names.Select(name => RuleCondition.Equal(new("process.running", name, scope),
            StateValue.From(new("axiom.bool", 1), true))).ToArray();
        return conditions.Length == 1 ? conditions[0] : RuleCondition.Any(conditions);
    }
}

public static class ProcessNameSelection
{
    public const int MaximumPerTask = 128;
    public const int MaximumWatched = 128;

    public static string[] Parse(string? text)
    {
        var names = (text ?? "").Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length is < 1 or > MaximumPerTask || names.Any(name => !ProcessGroups.ValidProcessName(name)))
            throw new ArgumentException("请选择有效的 .exe 文件名，每个任务最多 128 个。");
        return names.Select(name => name.ToLowerInvariant()).ToArray();
    }

    public static string[] FromRule(GameHdrRuleDto rule) => Parse(rule.ProcessNamesText ?? rule.ProcessName);
}
public sealed class HdrTaskAction : ITaskActionCompiler
{
    public TaskCapability Descriptor { get; } = new("display.hdr", 1, TaskCapabilityKind.Action,
        "显示", "设置 HDR", "在条件成立时将指定显示器的 HDR 设为开启或关闭。",
        [new("display", "显示器", TaskFieldKind.Resource, true, "选择支持 HDR 的显示器。", null, 1024, []),
         new("enabled", "开启 HDR", TaskFieldKind.Boolean, true, "关闭此开关表示目标为关闭 HDR。", "true", 5, [])],
        "正常结束后恢复开始前的设置；相反目标同时生效时暂停切换。",
        "执行期间修改、暂停或删除此任务，会保留当前 HDR，本次结束不再自动恢复。", SupportsRunConditions: false);
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        AutomationTaskCompiler.ValidateInvocation(configuration, Descriptor);
        return RuleAction.Desired(new("display.hdr", configuration.Parameters["display"], scope),
            StateValue.From(new("axiom.bool", 1), configuration.Parameters["enabled"] == "true"));
    }
}

// Compatibility boundary only: UI/protocol/task compiler do not know the legacy storage shape.
public static class GameHdrTasks
{
    public static AutomationTaskCompiler CreateCompiler()
    {
        var compiler = new AutomationTaskCompiler(); compiler.Register(new ProcessRunningTaskDetection()); compiler.Register(new HdrTaskAction()); return compiler;
    }
    public static AutomationTask FromLegacy(GameHdrRuleDto rule) => new(rule.Id, rule.Version, rule.Title, rule.Paused,
        new("process.running", 2, ImmutableDictionary<string, string>.Empty.Add("processNames", string.Join("\n", ProcessNameSelection.FromRule(rule)))),
        new("display.hdr", 1, ImmutableDictionary<string, string>.Empty.Add("display", rule.DisplayPath).Add("enabled", rule.HdrEnabled ? "true" : "false")));
    public static GameHdrRuleDto ToLegacy(AutomationTask task)
    {
        if (task.DetectionExpression is not null || task.SchemaVersion >= 3 || task.RunConditions is not null) throw new ArgumentException("HDR旧适配器不支持组合检测或自动暂停条件。");
        TaskDetectionExpressions.ValidateTask(task);
        if (task.TimeWindow is not null) throw new ArgumentException("HDR 不支持命令时间段限制。");
        if (task.Detection.CapabilityId != "process.running" || task.Action.CapabilityId != "display.hdr")
            throw new ArgumentException("当前适配器不支持此任务组合。");
        var names = ProcessNameSelection.Parse(task.Detection.Parameters["processNames"]);
        return new(task.Id, task.Version, task.Title, names[0], task.Action.Parameters["display"],
            task.Action.Parameters["enabled"] == "true", task.Paused, string.Join("\n", names));
    }
}
