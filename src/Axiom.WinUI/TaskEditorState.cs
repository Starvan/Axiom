using System.Collections.Immutable;
using Axiom.Domain;

namespace Axiom.WinUI;

internal sealed class TaskEditorState
{
    public string Id { get; private set; } = Guid.NewGuid().ToString("D");
    public long Version { get; private set; }
    public string Title { get; set; } = "";
    public bool Paused { get; set; }
    public WeeklyTimeWindow? TimeWindow { get; set; }
    public TaskRunConditions? RunConditions { get; set; }
    public TaskInvocation? Detection { get; private set; }
    public TaskInvocation? Action { get; private set; }
    public TaskDetectionExpression? DetectionExpression { get; private set; }
    public long TreeVersion { get; private set; }
    public bool Composite => DetectionExpression is not null;
    private AutomationTask? original;
    public bool Complete
    {
        get
        {
            if (Detection is null || Action is null) return false;
            if (DetectionExpression is null) return true;
            try { TaskDetectionExpressions.Validate(DetectionExpression); return true; }
            catch (ArgumentException) { return false; }
        }
    }
    public bool Dirty => original is null ? Title.Length > 0 || Detection is not null || DetectionExpression is not null || Action is not null || Paused || TimeWindow is not null || RunConditions is not null :
        Title != original.Title || Paused != original.Paused || TimeWindow != original.TimeWindow || RunConditions != original.RunConditions || !Same(Detection, original.Detection) ||
        !Same(Action, original.Action) || !SameTree(DetectionExpression, original.DetectionExpression);
    private static bool Same(TaskInvocation? a, TaskInvocation? b) => a is null ? b is null : b is not null && TaskDetectionExpressions.SameInvocation(a, b);
    private static bool SameTree(TaskDetectionExpression? a, TaskDetectionExpression? b) => a is null ? b is null : b is not null &&
        a.Kind == b.Kind && Same(a.Invocation, b.Invocation) && Children(a).Length == Children(b).Length && Children(a).Zip(Children(b)).All(pair => SameTree(pair.First, pair.Second));
    private static ImmutableArray<TaskDetectionExpression> Children(TaskDetectionExpression node) => node.Children.IsDefault ? [] : node.Children;
    private static TaskDetectionExpression Leaf(TaskInvocation? invocation = null) => new("leaf", invocation, []);
    private static TaskInvocation Defaults(TaskCapability descriptor) => new(descriptor.Id, descriptor.SchemaVersion,
        descriptor.Fields.Where(x => x.DefaultValue is not null || x.Kind == TaskFieldKind.Boolean)
            .ToImmutableDictionary(x => x.Id, x => x.DefaultValue ?? "false"));
    public void Load(AutomationTask? task)
    {
        original = task; Id = task?.Id ?? Guid.NewGuid().ToString("D"); Version = task?.Version ?? 0;
        TimeWindow = task?.TimeWindow;
        RunConditions = task?.RunConditions;
        Title = task?.Title ?? ""; Paused = task?.Paused ?? false; Detection = task?.Detection; Action = task?.Action;
        DetectionExpression = task?.DetectionExpression; TreeVersion++;
    }
    public void LoadDraft(AutomationTask task)
    {
        if (task.Version != 0) throw new ArgumentException("候选必须是未保存的任务。");
        Load(task); original = null;
    }
    public bool CanSave(AutomationSnapshot? snapshot) => Complete && (RunConditions is null || snapshot is { RunConditionsVersion: >= 1 } &&
        snapshot.Capabilities.Any(c => c.Id == Action!.CapabilityId && c.Kind == TaskCapabilityKind.Action && c.SupportsRunConditions)) && (!Composite || snapshot is { CompositeDetectionVersion: >= 1 } &&
        Detections().All(d => snapshot.Capabilities.Any(c => c.Id == d.CapabilityId && c.Kind == TaskCapabilityKind.Detection &&
            c.SchemaVersion == d.SchemaVersion && c.IsStateCondition)) &&
        snapshot.Capabilities.FirstOrDefault(c => c.Id == Action!.CapabilityId && c.Kind == TaskCapabilityKind.Action) is { } action && SupportsAction(action, snapshot.Combinations));
    public bool SupportsAction(TaskCapability descriptor, ImmutableArray<TaskCombination> combinations) => descriptor.Kind == TaskCapabilityKind.Action &&
        (!Composite || descriptor.SupportsCompositeDetection) && Detections().All(d => combinations.Contains(new(d.CapabilityId, descriptor.Id)));
    public IEnumerable<TaskInvocation> Detections() => Composite ? Nodes().Where(p => p.Node.Kind == "leaf" && p.Node.Invocation is not null).Select(p => p.Node.Invocation!) : Detection is null ? [] : [Detection];
    public void Select(TaskCapability descriptor, ImmutableArray<TaskCombination> combinations)
    {
        if (descriptor.Kind == TaskCapabilityKind.Detection)
        {
            if (Composite) throw new InvalidOperationException("请在对应子条件里选择检测类型。");
            if (Detection?.CapabilityId == descriptor.Id) return;
            Detection = Defaults(descriptor); KeepCompatibleAction(combinations);
        }
        else
        {
            if (!SupportsAction(descriptor, combinations)) throw new ArgumentException("此动作不能用于当前全部检测条件，请选择兼容动作。");
            if (Action?.CapabilityId == descriptor.Id) return;
            Action = Defaults(descriptor);
        }
    }
    public void SetParameter(TaskCapabilityKind kind, string key, string value)
    {
        if (kind == TaskCapabilityKind.Detection && Detection is not null)
        {
            if (Composite) throw new InvalidOperationException("请编辑对应子条件的参数。");
            Detection = Detection with { Parameters = Detection.Parameters.SetItem(key, value) };
        }
        if (kind == TaskCapabilityKind.Action && Action is not null) Action = Action with { Parameters = Action.Parameters.SetItem(key, value) };
    }
    public void EnableComposite(ImmutableArray<TaskCapability> capabilities, ImmutableArray<TaskCombination> combinations)
    {
        if (Composite) return;
        if (Detection is not null && !capabilities.Any(c => c.Id == Detection.CapabilityId && c.Kind == TaskCapabilityKind.Detection && c.IsStateCondition))
            throw new ArgumentException("当前检测是一次事件或暂不可用，不能加入状态组合。请先选择持续状态检测。");
        ApplyTree(new("all", Children: [Leaf(Detection), Leaf()]));
        if (Action is not null && !capabilities.Any(c => c.Id == Action.CapabilityId && c.SupportsCompositeDetection)) Action = null;
        KeepCompatibleAction(combinations);
    }
    public void DisableComposite()
    {
        if (DetectionExpression is null) return;
        var node = DetectionExpression;
        while (node.Kind is "all" or "any" && Children(node).Length == 1) node = Children(node)[0];
        if (node.Kind != "leaf")
            throw new ArgumentException("当前还有多个条件或取反。请先移除多余条件及取反，再切回单个检测；已填参数不会自动删除。");
        Detection = node.Invocation; DetectionExpression = null; TreeVersion++;
    }
    public void SelectLeaf(int[] path, TaskCapability descriptor, ImmutableArray<TaskCombination> combinations)
    {
        if (descriptor.Kind != TaskCapabilityKind.Detection || !descriptor.IsStateCondition) throw new ArgumentException("组合只接受持续状态检测。");
        if (Action is not null && !combinations.Contains(new(descriptor.Id, Action.CapabilityId))) throw new ArgumentException("此条件与当前动作不兼容，请先更换动作。");
        var node = Node(path);
        if (node.Kind != "leaf") throw new ArgumentException("请在检测条件里选择类型。");
        if (node.Invocation?.CapabilityId == descriptor.Id) return;
        ApplyTree(Replace(DetectionExpression!, path, node with { Invocation = Defaults(descriptor) }), structural: false);
        KeepCompatibleAction(combinations);
    }
    public void SetLeafParameter(int[] path, string key, string value)
    {
        var node = Node(path);
        if (node.Kind != "leaf" || node.Invocation is null) throw new ArgumentException("请先选择状态类型。");
        var updated = node with { Invocation = node.Invocation with { Parameters = node.Invocation.Parameters.SetItem(key, value) } };
        ApplyTree(Replace(DetectionExpression!, path, updated), structural: false);
    }
    public TaskDetectionExpression Node(int[] path)
    {
        var node = DetectionExpression ?? throw new InvalidOperationException("尚未开启组合检测。");
        foreach (var index in path) node = Children(node)[index];
        return node;
    }
    public IEnumerable<(int[] Path, TaskDetectionExpression Node)> Nodes()
    {
        if (DetectionExpression is null) yield break;
        foreach (var pair in Visit(DetectionExpression, [])) yield return pair;
        static IEnumerable<(int[] Path, TaskDetectionExpression Node)> Visit(TaskDetectionExpression node, int[] path)
        {
            yield return (path, node);
            for (var i = 0; i < Children(node).Length; i++)
                foreach (var pair in Visit(Children(node)[i], [.. path, i])) yield return pair;
        }
    }
    public void AddLeaf(int[] groupPath) => Add(groupPath, Leaf());
    public void AddGroup(int[] groupPath) => Add(groupPath, new("all", Children: [Leaf(), Leaf()]));
    private void Add(int[] path, TaskDetectionExpression child)
    {
        var node = Node(path);
        if (node.Kind == "leaf" || node.Kind == "not" && Children(node).Length == 1) throw new ArgumentException("取反只能包含一个条件；请在其内部组中添加。");
        ApplyTree(Replace(DetectionExpression!, path, node with { Children = Children(node).Add(child) }));
    }
    public void ChangeGroup(int[] path, string kind)
    {
        if (kind is not ("all" or "any" or "not")) throw new ArgumentException("请选择全部、任一或取反。");
        var node = Node(path); if (node.Kind == kind) return;
        if (node.Kind == "leaf") throw new ArgumentException("请先将检测放入条件组。");
        TaskDetectionExpression replacement = kind == "not" ? new TaskDetectionExpression("not", Children: [node]) :
            node.Kind == "not" && Children(node).Length == 1 ? Children(node)[0].Kind is "all" or "any" ?
                Children(node)[0] with { Kind = kind } : new(kind, Children: [Children(node)[0], Leaf()]) : node with { Kind = kind };
        ApplyTree(Replace(DetectionExpression!, path, replacement));
    }
    public void ToggleNot(int[] path)
    {
        var node = Node(path);
        ApplyTree(Replace(DetectionExpression!, path, node.Kind == "not" ? Children(node).Length == 1 ? Children(node)[0] : Leaf() : new("not", Children: [node])));
    }
    public void WrapGroup(int[] path) => ApplyTree(Replace(DetectionExpression!, path, new("all", Children: [Node(path), Leaf()])));
    public void Remove(int[] path)
    {
        if (path.Length == 0) ApplyTree(Leaf());
        else
        {
            var parent = path[..^1]; var node = Node(parent);
            ApplyTree(Replace(DetectionExpression!, parent, node with { Children = Children(node).RemoveAt(path[^1]) }));
        }
    }
    public void Move(int[] path, int direction)
    {
        if (path.Length == 0) return;
        var parent = path[..^1]; var node = Node(parent); var target = path[^1] + direction;
        if (target < 0 || target >= Children(node).Length) return;
        var list = Children(node).RemoveAt(path[^1]).Insert(target, Node(path));
        ApplyTree(Replace(DetectionExpression!, parent, node with { Children = list }));
    }
    public void MoveToGroup(int[] path, int[] destination)
    {
        if (path.Length == 0 || destination.Length >= path.Length && destination.Take(path.Length).SequenceEqual(path))
            throw new ArgumentException("不能将条件移入它自己或自己的子组。");
        var item = Node(path); var target = Node(destination);
        if (target.Kind == "leaf" || target.Kind == "not" && Children(target).Length > 0) throw new ArgumentException("请选择可容纳条件的全部或任一组。");
        var parentPath = path[..^1]; var parent = Node(parentPath);
        var tree = Replace(DetectionExpression!, parentPath, parent with { Children = Children(parent).RemoveAt(path[^1]) });
        var adjusted = destination.ToArray();
        if (adjusted.Length > parentPath.Length && adjusted.Take(parentPath.Length).SequenceEqual(parentPath) && adjusted[parentPath.Length] > path[^1]) adjusted[parentPath.Length]--;
        var group = tree; foreach (var index in adjusted) group = Children(group)[index];
        ApplyTree(Replace(tree, adjusted, group with { Children = Children(group).Add(item) }));
    }
    private static TaskDetectionExpression Replace(TaskDetectionExpression root, int[] path, TaskDetectionExpression replacement)
    {
        if (path.Length == 0) return replacement;
        return root with { Children = Children(root).SetItem(path[0], Replace(Children(root)[path[0]], path[1..], replacement)) };
    }
    private void ApplyTree(TaskDetectionExpression tree, bool structural = true)
    {
        var nodes = 0; var leaves = 0; Check(tree, 1);
        DetectionExpression = tree;
        Detection = Detections().FirstOrDefault();
        if (structural) TreeVersion++;
        void Check(TaskDetectionExpression node, int depth)
        {
            if (++nodes > TaskDetectionExpressionLimits.MaximumNodes || depth > TaskDetectionExpressionLimits.MaximumDepth ||
                node.Kind == "leaf" && ++leaves > TaskDetectionExpressionLimits.MaximumLeaves || Children(node).Length > TaskDetectionExpressionLimits.MaximumLeaves)
                throw new ArgumentException("组合最多4层、16个状态条件。请减少嵌套或条件数量；已有配置保留。");
            foreach (var child in Children(node)) Check(child, depth + 1);
        }
    }
    private void KeepCompatibleAction(ImmutableArray<TaskCombination> combinations)
    {
        if (Action is not null && Detections().Any(d => !combinations.Contains(new(d.CapabilityId, Action.CapabilityId)))) Action = null;
    }
    public AutomationTask Build()
    {
        if (!Complete) throw new InvalidOperationException("请选择检测和动作，全部/任一组至少需两项，取反组恰好需一项。");
        var anchor = DetectionExpression is null ? Detection! : TaskDetectionExpressions.Validate(DetectionExpression)[0];
        RunConditions?.Validate();
        return new(Id, Version, Title.Trim(), Paused, anchor, Action!, SchemaVersion: RunConditions is not null ? 4 : Composite ? 3 : TimeWindow is null ? 1 : 2,
            TimeWindow: TimeWindow, DetectionExpression: DetectionExpression, RunConditions: RunConditions);
    }
}
