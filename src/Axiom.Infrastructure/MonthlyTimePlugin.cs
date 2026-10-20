using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class MonthlyTimePlugin : IAutomationPlugin
{
    public string Id => "axiom.monthly-time";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new MonthlyTimeTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "program.start", "program.close", WindowCapabilities.Close, WindowCapabilities.State, TrayCapabilities.Hide, OpenTargetCapabilities.Open }
        .Select(action => new TaskCombination(MonthlyTimeCapabilities.Due, action))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(MonthlyTimeCapabilities.Due, new("axiom.event-count", 1), new("axiom.event-count", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active monthly calendar rules", "same user", []), ScalarKind.Number, ScalarKind.Number)];
}
public sealed class MonthlyTimeTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => MonthlyTimeCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope) => RuleCondition.Event(
        new(MonthlyTimeCapabilities.Due, MonthlyTimeCapabilities.Validate(configuration).Resource, scope), new("axiom.event-count", 1));
}
