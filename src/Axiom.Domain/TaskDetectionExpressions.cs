using System.Collections.Immutable;

namespace Axiom.Domain;

public sealed record TaskDetectionExpression(string Kind,
    TaskInvocation? Invocation = null,
    ImmutableArray<TaskDetectionExpression> Children = default)
{
    private ImmutableArray<TaskDetectionExpression> children = Children.IsDefault ? [] : Children;
    public ImmutableArray<TaskDetectionExpression> Children
    {
        get => children;
        init => children = value.IsDefault ? [] : value;
    }
}

public static class TaskDetectionExpressionLimits
{
    public const int MaximumDepth = 4;
    public const int MaximumLeaves = 16;
    public const int MaximumNodes = 64;
}

public static class TaskDetectionExpressions
{
    public static ImmutableArray<TaskInvocation> Validate(TaskDetectionExpression expression)
    {
        var leaves = ImmutableArray.CreateBuilder<TaskInvocation>(); var nodes = 0;
        Visit(expression, 1); return leaves.ToImmutable();
        void Visit(TaskDetectionExpression? node, int depth)
        {
            if (node is null || depth > TaskDetectionExpressionLimits.MaximumDepth || ++nodes > TaskDetectionExpressionLimits.MaximumNodes)
                throw new ArgumentException("组合检测最多4层、16个条件和64个节点。");
            if (node.Kind == "leaf")
            {
                if (node.Invocation is null || !node.Children.IsDefaultOrEmpty || leaves.Count >= TaskDetectionExpressionLimits.MaximumLeaves)
                    throw new ArgumentException("检测叶必须包含一项配置且无子节点；最多16个条件。");
                leaves.Add(node.Invocation); return;
            }
            if (node.Invocation is not null || node.Children.IsDefault ||
                (node.Kind == "not" ? node.Children.Length != 1 : node.Kind is not ("all" or "any") || node.Children.Length is < 2 or > TaskDetectionExpressionLimits.MaximumLeaves))
                throw new ArgumentException("组合仅支持至少两个条件的且/或组，或恰好一个子条件的非组。");
            foreach (var child in node.Children) Visit(child, depth + 1);
        }
    }
    public static ImmutableArray<TaskInvocation> ValidateTask(AutomationTask task)
    {
        if (task.Detection is null) throw new ArgumentException("缺少检测配置。");
        if (task.RunConditions is not null)
        {
            if (task.SchemaVersion != 4) throw new ArgumentException("自动暂停条件必须使用任务版本4。");
            task.RunConditions.Validate();
        }
        else if (task.SchemaVersion == 4) throw new ArgumentException("版本4任务必须声明自动暂停条件。");
        if (task.DetectionExpression is null)
        {
            if (task.SchemaVersion != 4 && task.SchemaVersion != (task.TimeWindow is null ? 1 : 2)) throw new ArgumentException("单检测任务版本无效。");
            return [task.Detection];
        }
        if (task.SchemaVersion is not (3 or 4)) throw new ArgumentException("组合检测必须使用任务版本3或4，旧版本不得忽略条件树。");
        var leaves = Validate(task.DetectionExpression);
        if (!SameInvocation(task.Detection, leaves[0])) throw new ArgumentException("组合检测首条件与兼容展示配置不一致。");
        return leaves;
    }
    public static bool SameInvocation(TaskInvocation left, TaskInvocation right) =>
        left.CapabilityId == right.CapabilityId && left.SchemaVersion == right.SchemaVersion && left.Parameters is not null && right.Parameters is not null &&
        left.Parameters.Count == right.Parameters.Count && left.Parameters.All(p => right.Parameters.TryGetValue(p.Key, out var value) && p.Value == value);
}
