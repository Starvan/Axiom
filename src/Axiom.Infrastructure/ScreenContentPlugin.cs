using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class ScreenContentPlugin : IAutomationPlugin
{
    public string Id => "axiom.screen-content";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new ScreenContentTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations =>
        [new(ScreenContentCapabilities.Bright, BuiltInBrightnessRestoreCapabilities.Set), new(ScreenContentCapabilities.Bright, HardwareBrightnessRestoreCapabilities.Set)];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(ScreenContentCapabilities.Bright, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Moderate, true, "active screen content rules within detection hours", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}
public sealed class ScreenContentTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => ScreenContentCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope) =>
        RuleCondition.Equal(new(ScreenContentCapabilities.Bright, ScreenContentCapabilities.Validate(configuration).Resource, scope), StateValue.From(new("axiom.bool", 1), true));
}
