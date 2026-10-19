using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class HdrStatePlugin : IAutomationPlugin
{
    public string Id => "axiom.hdr-state";
    public IEnumerable<ITaskDetectionCompiler> Detections => [new HdrStateTaskDetection()];
    public IEnumerable<ITaskActionCompiler> Actions => [];
    public ImmutableArray<TaskCombination> Combinations => [.. new[] { "program.start", "program.close", WindowCapabilities.State,
        WindowMonitorCapabilities.Move, OpenTargetCapabilities.Open, NotificationCapabilities.Show, KeepAwakeCapabilities.Hold,
        BuiltInBrightnessRestoreCapabilities.Set, HardwareBrightnessRestoreCapabilities.Set }
        .Select(action => new TaskCombination(HdrStateCapabilities.State, action))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(HdrStateCapabilities.State, new("axiom.bool", 1), new("axiom.bool", 1),
        ScopeKind.Session, true, false, false, ExecutionLocation.SessionAgent, "same-user-session", true, "available", false,
        TimeSpan.FromSeconds(40), true, ConcurrencyScope.Resource, RetryCategory.Never, ConfirmationSource.Observation, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active primary HDR conditions", "same user", []), ScalarKind.Boolean, ScalarKind.Boolean)];
}
public sealed class HdrStateTaskDetection : ITaskDetectionCompiler
{
    public TaskCapability Descriptor => HdrStateCapabilities.Descriptor;
    public RuleCondition Compile(TaskInvocation configuration, ResourceScope scope) =>
        RuleCondition.Equal(new(HdrStateCapabilities.State, "primary", scope), StateValue.From(new("axiom.bool", 1), HdrStateCapabilities.Validate(configuration) == "on"));
}
