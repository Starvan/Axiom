using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Engine;

namespace Axiom.Infrastructure;

public sealed class HostsPlugin : IAutomationPlugin
{
    public string Id => "axiom.hosts";
    public IEnumerable<ITaskDetectionCompiler> Detections => [];
    public IEnumerable<ITaskActionCompiler> Actions => [new HostsTaskAction()];
    // Use every currently registered public detection, including state/composite leaves.
    public ImmutableArray<TaskCombination> Combinations => [.. BuiltInAutomationPlugins.CreateWithoutHosts()
        .SelectMany(p => p.Detections).Select(d => d.Descriptor.Id).Distinct().Select(d => new TaskCombination(d, HostsCapabilities.Update))];
    public IEnumerable<CapabilityBinding> Bindings => [new(new(HostsCapabilities.Update, TaskInvocationCommands.Schema, new("axiom.bool", 1),
        ScopeKind.Session, false, true, false, ExecutionLocation.Service, "administrator-fixed-system-hosts", true, "available", true,
        TimeSpan.FromSeconds(90), true, ConcurrencyScope.Capability, RetryCategory.Never, ConfirmationSource.ReceiptOnly, EventFidelity.EdgeSensitive,
        TimeSpan.Zero, TimeSpan.Zero, CostClass.Low, true, "active hosts commands", "same user; fixed hosts path", []), ScalarKind.Text, ScalarKind.Boolean)];
}
public sealed class HostsTaskAction : ITaskActionCompiler
{
    public bool SupportsTimeWindow => true;
    public TaskCapability Descriptor => HostsCapabilities.Descriptor with { SupportsCompositeDetection = true };
    public RuleAction Compile(TaskInvocation configuration, ResourceScope scope)
    {
        _ = HostsCapabilities.Validate(configuration);
        return TaskInvocationCommands.Compile(configuration, Descriptor, scope, "system-hosts");
    }
    public RuleTiming Timing(TaskInvocation configuration) => new();
}
