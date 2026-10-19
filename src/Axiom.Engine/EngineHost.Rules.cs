using System.Collections.Immutable;
using Axiom.Domain;

namespace Axiom.Engine;

public sealed partial class EngineHost
{
    private sealed class RuntimeRule(RuleDefinition definition)
    {
        internal RuleDefinition Definition = definition;
        internal TruthValue Value;
        internal ImmutableDictionary<RuleCondition, TruthValue> History = ImmutableDictionary<RuleCondition, TruthValue>.Empty;
        internal bool Armed;
        internal bool CommandEdge;
        internal long Evaluations;
        internal long InputRevision;
        internal string Reason = "NotEvaluated";
        internal TimeSpan? DebounceUntil;
        internal TimeSpan? TrueSince;
        internal TimeSpan? LastTriggered = null;
        internal decimal? EventCursor;
        internal bool EventLatched;
        internal StateValue? EventStamp;
        internal string? AutomaticPauseReason;
        internal readonly HashSet<ResourceKey> FreshSnapshots = [];
    }
    private readonly Dictionary<string, RuntimeRule> rules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> ruleVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CapabilityBinding> capabilities = new(StringComparer.Ordinal);
    private readonly Dictionary<ResourceKey, HashSet<string>> targetRules = [];
    private readonly Dictionary<ResourceKey, ResourceDecision> decisions = [];
    private readonly Dictionary<ResourceKey, TimeSpan> receivedAt = [];
    private readonly Dictionary<ResourceKey, long> observationVersions = [];
    private long observationOrder;
    private readonly Dictionary<ResourceKey, ManualOverride> overrides = [];
    private IAuthorizationPolicy? authorization;
    private ICommandIdSource? commandIds;
    private long decisionGeneration;
    private long configurationRevision;
    private long authorizationRevision;
    private readonly Dictionary<string, long> capabilityVersions = new(StringComparer.Ordinal);
    private sealed record DecisionInputs(ImmutableArray<(string Id, long Version)> Rules, long CapabilityVersion, long AuthorizationRevision);
    private readonly Dictionary<ResourceKey, DecisionInputs> decisionInputs = [];
    private EngineMode effectiveMode = EngineMode.Normal;
    private EngineMode requestedMode = EngineMode.Normal;

    public void ConfigureAutomation(IAuthorizationPolicy policy, ICommandIdSource ids)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(ids);
        lock (gate)
        {
            RequireRunning();
            if (authorization is not null) throw new InvalidOperationException("Automation is already configured.");
            if (flights.Count != 0) throw new InvalidOperationException("Previous execution is still physically in flight.");
            if (heldResources.Count != 0) throw new InvalidOperationException("Acknowledge the previous lifecycle's observer release plan first.");
            authorization = policy;
            commandIds = ids;
            InitializeAutomation();
        }
    }
    partial void InitializeAutomation();
    private void RequireAutomation()
    {
        RequireRunning();
        if (authorization is null) throw new InvalidOperationException("Configure automation first.");
    }
    private AuthorizationDecision Authorize(string principal, ResourceKey key, CapabilityOperation operation)
    {
        try { return authorization?.Authorize(principal, key, operation) ?? new(false, "AutomationNotConfigured"); }
        catch (Exception) { return new(false, "AuthorizationPolicyFailed"); }
    }

    public void RegisterCapability(CapabilityBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        lock (gate)
        {
            RequireAutomation();
            if (!capabilities.ContainsKey(binding.Descriptor.Id) && capabilities.Count >= 1024) throw new InvalidOperationException("Capability capacity exceeded.");
            if (binding.Descriptor.Dependencies.Length > 1024) throw new ArgumentException("Capability dependency capacity exceeded.");
            capabilities[binding.Descriptor.Id] = binding;
            capabilityVersions[binding.Descriptor.Id] = capabilityVersions.GetValueOrDefault(binding.Descriptor.Id) + 1;
            ResetBlockedExecutions(binding.Descriptor.Id);
            configurationRevision++;
            RefreshRulesCore();
        }
    }

    public void SetRule(RuleDefinition definition, string? initialAutomaticPauseReason = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (gate)
        {
            RequireAutomation();
            if (ruleVersions.TryGetValue(definition.Id, out var version) && definition.Version <= version) throw new ArgumentException("Rule version must increase.");
            if (!ruleVersions.ContainsKey(definition.Id) && ruleVersions.Count >= 4096) throw new InvalidOperationException("Rule capacity exceeded.");
            ValidateRule(definition);
            if (!decisions.ContainsKey(definition.Action.Key) && decisions.Count >= 16_384) throw new InvalidOperationException("Managed resource capacity exceeded.");
            ResourceKey? previousTarget = null;
            if (rules.Remove(definition.Id, out var previous))
            {
                previousTarget = previous.Definition.Action.Key;
                RemoveRuleState(previous);
                RemoveTargetRule(previous);
            }
            ruleVersions[definition.Id] = definition.Version;
            var runtime = new RuntimeRule(definition) { AutomaticPauseReason = initialAutomaticPauseReason };
            rules[definition.Id] = runtime;
            index.Set(definition.Id, definition.Enabled && !Paused(runtime) ? definition.Condition.Dependencies : []);
            if (!targetRules.TryGetValue(definition.Action.Key, out var set)) targetRules[definition.Action.Key] = set = new(StringComparer.Ordinal);
            set.Add(definition.Id);
            configurationRevision++;
            EvaluateRule(runtime, baseline: true);
            if (previousTarget is not null && previousTarget != definition.Action.Key) Recompute(previousTarget);
            Recompute(definition.Action.Key);
            DemandChanged();
        }
    }
    public void SetRuleAutomaticPause(string ruleId, string? reason)
    {
        lock (gate)
        {
            RequireAutomation();
            var rule = rules[ruleId];
            if (rule.AutomaticPauseReason == reason) return;
            rule.AutomaticPauseReason = reason;
            rule.FreshSnapshots.Clear();
            if (reason is null && rule.Definition.Enabled)
                rule.FreshSnapshots.UnionWith(rule.Definition.Condition.Dependencies.Where(key => !heldResources.Contains(key)));
            rule.History = ImmutableDictionary<RuleCondition, TruthValue>.Empty;
            rule.EventCursor = null; rule.EventLatched = false; rule.EventStamp = null;
            index.Set(ruleId, rule.Definition.Enabled && !Paused(rule) ? rule.Definition.Condition.Dependencies : []);
            configurationRevision++;
            EvaluateRule(rule, baseline: true);
            Recompute(rule.Definition.Action.Key);
            DemandChanged();
        }
    }
    private void ValidateRule(RuleDefinition rule)
    {
        if (displayKey is { } display && (rule.Action.Key == display || rule.Condition.Dependencies.Contains(display)))
            throw new ArgumentException("Display-only SCM status cannot be used by automation rules.");
        if (!capabilities.TryGetValue(rule.Action.Key.CapabilityId, out var target) || target.Descriptor.Scope != rule.Scope.Kind || !target.Accepts(rule.Action.Value))
            throw new ArgumentException("Unknown target capability, scope or parameter schema/type mismatch.");
        if (rule.Action.Kind == RuleActionKind.Desired && !target.Descriptor.DesiredState || rule.Action.Kind == RuleActionKind.Command && !target.Descriptor.Command)
            throw new ArgumentException("Unsupported action kind.");
        if (rule.Action.Kind == RuleActionKind.Desired && (!target.Descriptor.Observe || target.Descriptor.Parameters != target.Descriptor.Result || target.ParametersKind != target.ResultKind))
            throw new ArgumentException("Current absolute-setting reconciliation requires matching observable target schema/type.");
        foreach (var key in rule.Condition.Dependencies)
        {
            if (!capabilities.TryGetValue(key.CapabilityId, out var input) || !input.Descriptor.Observe || input.Descriptor.Scope != key.Scope.Kind)
                throw new ArgumentException("Unknown observation capability or scope mismatch.");
            if ((rule.Action.Kind == RuleActionKind.Command || rule.Timing.MinimumDuration > TimeSpan.Zero) && input.Descriptor.Fidelity != EventFidelity.EdgeSensitive)
                throw new ArgumentException("Edges/continuous duration require edge-sensitive observations.");
            if (!Authorize(rule.Principal, key, CapabilityOperation.Observe).Allowed) throw new UnauthorizedAccessException("Input observation denied.");
        }
        void ValidateLeaf(RuleCondition node)
        {
            if (node.Key is { } key)
            {
                var binding = capabilities[key.CapabilityId];
                if (node.Expected!.Schema != binding.Descriptor.Result || node.Expected.Kind != binding.ResultKind)
                    throw new ArgumentException("Condition schema/type mismatch.");
            }
            foreach (var child in node.Children) ValidateLeaf(child);
        }
        ValidateLeaf(rule.Condition);
        foreach (var key in ExpandResources(rule.Condition.Dependencies.Append(rule.Action.Key)))
        {
            if (!capabilities.TryGetValue(key.CapabilityId, out var dependency) || dependency.Descriptor.Scope != key.Scope.Kind)
                throw new ArgumentException("Unresolved capability dependency or scope mismatch.");
            if (dependency.Descriptor.Observe && !Authorize(rule.Principal, key, CapabilityOperation.Observe).Allowed)
                throw new UnauthorizedAccessException("Transitive observation dependency denied.");
        }
        if (!Authorize(rule.Principal, rule.Action.Key, Operation(rule.Action)).Allowed) throw new UnauthorizedAccessException("Rule action denied.");
    }
    private static CapabilityOperation Operation(RuleAction action) => action.Kind == RuleActionKind.Command ? CapabilityOperation.ExecuteCommand : CapabilityOperation.ApplyDesired;
    public void RemoveRule(string ruleId)
    {
        Contract.Text(ruleId, nameof(ruleId));
        lock (gate)
        {
            RequireAutomation();
            if (!rules.Remove(ruleId, out var rule)) return;
            index.Remove(ruleId);
            RemoveRuleState(rule);
            RemoveTargetRule(rule);
            configurationRevision++;
            Recompute(rule.Definition.Action.Key);
            DemandChanged();
        }
    }
    private void RemoveTargetRule(RuntimeRule rule)
    {
        var key = rule.Definition.Action.Key;
        var set = targetRules[key];
        set.Remove(rule.Definition.Id);
        if (set.Count == 0) targetRules.Remove(key);
    }
    partial void RemoveRuleState(RuntimeRule rule);

    public RuleEvaluationView GetRuleEvaluation(string id)
    {
        lock (gate)
        {
            RequireAutomation();
            var rule = rules[id];
            return new(id, rule.Definition.Version, rule.InputRevision, rule.Value, rule.Reason, rule.Evaluations);
        }
    }
    public ResourceDecision GetDecision(ResourceKey key)
    {
        lock (gate)
        {
            RequireAutomation();
            return decisions.GetValueOrDefault(key) ?? new(key, 0, store.Revision, DecisionStatus.Released, null, [], "NotManaged");
        }
    }
    private bool Paused(RuntimeRule rule) => rule.AutomaticPauseReason is not null || (requestedMode == EngineMode.Lightweight || effectiveMode == EngineMode.Lightweight) && !rule.Definition.Retention.KeepInLightweight;
    private string? Eligibility(RuntimeRule rule)
    {
        var definition = rule.Definition;
        if (!definition.Enabled) return "Disabled";
        if (rule.AutomaticPauseReason is not null) return "AutomaticallyPaused: " + rule.AutomaticPauseReason;
        if (rule.FreshSnapshots.Count != 0) return "AwaitingAutomaticResumeSnapshot";
        if (Paused(rule)) return "PausedInLightweight";
        if (rule.Definition.Condition.Dependencies.Any(resumeSnapshots.Contains) || resumeSnapshots.Contains(rule.Definition.Action.Key)) return "AwaitingResumeSnapshot";
        if (!capabilities.TryGetValue(definition.Action.Key.CapabilityId, out var target) || !target.Descriptor.Available) return "CapabilityUnavailable";
        if (target.Descriptor.Scope != definition.Scope.Kind || !target.Accepts(definition.Action.Value) ||
            definition.Action.Kind == RuleActionKind.Desired && !target.Descriptor.DesiredState || definition.Action.Kind == RuleActionKind.Command && !target.Descriptor.Command)
            return "CapabilityContractChanged";
        if (!Authorize(definition.Principal, definition.Action.Key, Operation(definition.Action)).Allowed) return "ActionUnauthorized";
        foreach (var key in definition.Condition.Dependencies)
            if (!capabilities.TryGetValue(key.CapabilityId, out var input) || !input.Descriptor.Available || !input.Descriptor.Observe ||
                !Authorize(definition.Principal, key, CapabilityOperation.Observe).Allowed) return "InputUnavailableOrUnauthorized";
        bool Compatible(RuleCondition node) => node.Key is { } key
            ? capabilities[key.CapabilityId].Descriptor.Result == node.Expected!.Schema && capabilities[key.CapabilityId].ResultKind == node.Expected.Kind &&
              ((definition.Action.Kind != RuleActionKind.Command && definition.Timing.MinimumDuration == TimeSpan.Zero) || capabilities[key.CapabilityId].Descriptor.Fidelity == EventFidelity.EdgeSensitive)
            : node.Children.All(Compatible);
        if (!Compatible(definition.Condition)) return "InputCapabilityContractChanged";
        foreach (var key in ExpandResources(definition.Condition.Dependencies.Append(definition.Action.Key)))
            if (!capabilities.TryGetValue(key.CapabilityId, out var dependency) || !dependency.Descriptor.Available ||
                dependency.Descriptor.Observe && !Authorize(definition.Principal, key, CapabilityOperation.Observe).Allowed) return "DependencyUnavailableOrUnauthorized";
        return null;
    }
    private void EvaluateRule(RuntimeRule rule, bool baseline, bool fromDeadline = false)
    {
        rule.Evaluations++;
        rule.InputRevision = store.Revision;
        var blocked = Eligibility(rule);
        var states = store.Read(rule.Definition.Condition.Dependencies).States;
        if (rule.Definition.Timing.MaximumInputAge is { } age && rule.Definition.Condition.Dependencies.Any(key =>
            !receivedAt.TryGetValue(key, out var at) || clock.MonotonicNow - at >= age)) blocked ??= "InputStale";
        var evaluated = RuleEvaluator.Evaluate(rule.Definition.Condition, states,
            baseline ? null : rule.History);
        var value = blocked is null ? evaluated.Value : TruthValue.Unknown;
        var previous = rule.Value;
        if (rule.Definition.Condition.Kind == ConditionKind.Event)
        {
            var counter = states.GetValueOrDefault(rule.Definition.Condition.Key!)?.Observation.Value?.Number;
            if (value == TruthValue.Unknown) { rule.EventLatched = false; rule.EventStamp = null; }
            else if (baseline || rule.EventCursor is null)
            { rule.EventCursor = counter; rule.EventLatched = false; rule.EventStamp = null; }
            else if (!fromDeadline && counter > rule.EventCursor)
            {
                rule.EventCursor = counter;
                if (flights.ContainsKey(rule.Definition.Action.Key) || pendingByKey.ContainsKey(rule.Definition.Action.Key) ||
                    rule.TrueSince.HasValue && rule.Value != TruthValue.True)
                { rule.CommandEdge = false; rule.Reason = "Busy; event discarded"; return; }
                rule.EventLatched = true; rule.Armed = true; rule.Value = TruthValue.False; previous = TruthValue.False;
                rule.EventStamp = states.GetValueOrDefault(rule.Definition.Condition.Key!)?.Observation.Value;
                rule.TrueSince = null; rule.DebounceUntil = null;
            }
            if (value != TruthValue.Unknown) value = rule.EventLatched ? TruthValue.True : TruthValue.False;
        }
        if (baseline)
        {
            rule.Armed = false; rule.CommandEdge = false; rule.TrueSince = null;
            rule.DebounceUntil = null; rule.Value = TruthValue.Unknown; previous = TruthValue.Unknown;
        }
        if (value != TruthValue.True)
        {
            rule.History = evaluated.Hysteresis;
            rule.Value = value; rule.Reason = blocked ?? value.ToString();
            rule.TrueSince = null; rule.DebounceUntil = null; rule.CommandEdge = false;
            rule.Armed = value == TruthValue.False;
            CancelDeadline(("rule", rule.Definition.Id));
            ScheduleFreshness(rule);
            return;
        }
        if (!WithinTimeWindow(rule))
        {
            // Consume this edge now. Opening the window is not a new input edge.
            rule.History = evaluated.Hysteresis;
            rule.Value = TruthValue.True; rule.Armed = false; rule.CommandEdge = false;
            rule.TrueSince = null; rule.DebounceUntil = null;
            rule.Reason = "OutsideTimeWindow; edge discarded";
            CancelDeadline(("rule", rule.Definition.Id));
            ScheduleFreshness(rule);
            return;
        }
        var now = clock.MonotonicNow;
        if (!fromDeadline && rule.Definition.Timing.Debounce > TimeSpan.Zero)
            rule.DebounceUntil = now + rule.Definition.Timing.Debounce;
        TimeSpan? due = null;
        if (rule.DebounceUntil is { } stableAt && now < stableAt)
        {
            rule.Reason = "Debounce";
            due = stableAt;
        }
        else
        {
            rule.History = evaluated.Hysteresis;
            rule.TrueSince ??= now;
            var activeAt = rule.TrueSince.Value + rule.Definition.Timing.MinimumDuration;
            if (now < activeAt) { rule.Reason = "MinimumDuration"; due = activeAt; }
            else { rule.Value = TruthValue.True; rule.Reason = "True"; }
        }
        if (due is { } deadline)
            SetDeadline(("rule", rule.Definition.Id), deadline, () => { EvaluateRule(rule, false, fromDeadline: true); Recompute(rule.Definition.Action.Key); });
        else CancelDeadline(("rule", rule.Definition.Id));
        if (rule.Value == TruthValue.True && previous != TruthValue.True && rule.Armed && !baseline)
        {
            rule.CommandEdge = rule.LastTriggered is null || now - rule.LastTriggered >= rule.Definition.Timing.Cooldown;
            if (!rule.CommandEdge) rule.Reason = "Cooldown; edge discarded";
            rule.Armed = false;
        }
        ScheduleFreshness(rule);
    }
    private bool WithinTimeWindow(RuntimeRule rule) => rule.Definition.Timing.TimeWindow?.Contains(clock.UtcNow, clock.LocalTimeZone) ?? true;

    private void OnMutation(Mutation mutation)
    {
        if (mutation.Touched.IsEmpty) return;
        foreach (var state in mutation.Touched)
        {
            receivedAt[state.Observation.Key] = clock.MonotonicNow;
            observationVersions[state.Observation.Key] = ++observationOrder;
        }
        if (authorization is null) return;
        var affected = new SortedSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<ResourceKey>();
        foreach (var state in mutation.Changes)
        {
            foreach (var id in index.Match(state.Observation.Key)) if (rules.ContainsKey(id)) affected.Add(id);
        }
        foreach (var id in affected)
        {
            var rule = rules[id];
            if (Paused(rule)) continue;
            EvaluateRule(rule, mutation.IsSnapshot || mutation.Result.Status == UpdateStatus.Gap);
            targets.Add(rule.Definition.Action.Key);
        }
        foreach (var state in mutation.Touched)
        {
            ObservationRefreshed(state.Observation.Key);
            if (targetRules.ContainsKey(state.Observation.Key) || decisions.ContainsKey(state.Observation.Key)) targets.Add(state.Observation.Key);
        }
        foreach (var key in targets) Recompute(key);
        SnapshotArrived(mutation);
    }
    partial void ObservationRefreshed(ResourceKey key);
    partial void SnapshotArrived(Mutation mutation);
    private void Recompute(ResourceKey key)
    {
        var candidates = targetRules.TryGetValue(key, out var ids) ? ids.Select(id => rules[id])
            .Where(rule => rule.Value == TruthValue.True && Eligibility(rule) is null)
            .Select(rule => new RuleCandidate(rule.Definition.Id, rule.Definition.Principal, rule.Definition.Priority, rule.Definition.Action)) : [];
        var result = RuleArbitrator.Resolve(candidates, overrides.ContainsKey(key));
        var winners = result.Winners.Select(x => x.RuleId).ToImmutableArray();
        var previous = decisions.GetValueOrDefault(key);
        var inputs = new DecisionInputs(winners.Select(id => (id, rules[id].Definition.Version)).ToImmutableArray(),
            capabilityVersions.GetValueOrDefault(key.CapabilityId), authorizationRevision);
        var oldInputs = decisionInputs.GetValueOrDefault(key);
        var changed = previous is null || previous.Action != result.Action || !previous.Winners.SequenceEqual(winners) ||
            oldInputs is null || !oldInputs.Rules.SequenceEqual(inputs.Rules) || oldInputs.CapabilityVersion != inputs.CapabilityVersion || oldInputs.AuthorizationRevision != inputs.AuthorizationRevision;
        decisionInputs[key] = inputs;
        var generation = changed ? ++decisionGeneration : previous!.Generation;
        decisions[key] = new(key, generation, store.Revision, result.Status, result.Action, winners, result.Reason, previous?.LastOutcome);
        ReconcileDecision(key);
    }
    partial void ReconcileDecision(ResourceKey key);
    partial void DemandChanged();

    public void RefreshAuthorization()
    {
        lock (gate) { RequireAutomation(); configurationRevision++; authorizationRevision++; ResetBlockedExecutions(null); RefreshRulesCore(); }
    }

    public ManualOverride TakeOverride(ResourceKey key, string principal, string reason, TimeSpan? duration = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        Contract.Text(principal, nameof(principal));
        Contract.Text(reason, nameof(reason));
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        lock (gate)
        {
            RequireAutomation();
            if (!Authorize(principal, key, CapabilityOperation.Override).Allowed) throw new UnauthorizedAccessException("Override denied.");
            if (!decisions.ContainsKey(key) && decisions.Count >= 16_384) throw new InvalidOperationException("Managed resource capacity exceeded.");
            var item = new ManualOverride(key, principal, ++configurationRevision, clock.UtcNow, reason,
                duration is { } value ? clock.UtcNow + value : null);
            overrides[key] = item;
            OverrideChanged(key, duration);
            Recompute(key);
            DemandChanged();
            return item;
        }
    }
    public void ReleaseOverride(ResourceKey key, string principal)
    {
        ArgumentNullException.ThrowIfNull(key);
        Contract.Text(principal, nameof(principal));
        lock (gate)
        {
            RequireAutomation();
            if (!Authorize(principal, key, CapabilityOperation.ReleaseOverride).Allowed) throw new UnauthorizedAccessException("Release denied.");
            if (!overrides.Remove(key)) return;
            configurationRevision++;
            OverrideChanged(key, null);
            Recompute(key);
            DemandChanged();
        }
    }
    partial void OverrideChanged(ResourceKey key, TimeSpan? duration);
    private void RefreshRulesCore()
    {
        foreach (var rule in rules.Values.OrderBy(x => x.Definition.Id, StringComparer.Ordinal)) EvaluateRule(rule, baseline: true);
        foreach (var key in targetRules.Keys.ToArray()) Recompute(key);
        DemandChanged();
    }

    private void StopAutomation()
    {
        StopExecution();
        foreach (var rule in rules.Values) RemoveRuleState(rule);
        StopDeadlines();
        rules.Clear(); ruleVersions.Clear(); targetRules.Clear(); decisions.Clear(); receivedAt.Clear(); capabilities.Clear(); observationVersions.Clear();
        capabilityVersions.Clear(); decisionInputs.Clear();
        authorization = null; commandIds = null;
        StopControls();
    }
    partial void StopExecution();
    partial void StopControls();
}
