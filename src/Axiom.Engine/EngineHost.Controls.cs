using System.Collections.Immutable;
using Axiom.Domain;

namespace Axiom.Engine;

public sealed record ObservationRequirement(TimeSpan MaximumDelay, bool PreserveEdges);
public sealed record ObservationPlan(long Version, long ConfigurationRevision, long ModeGeneration, EngineMode Mode,
    ImmutableDictionary<ResourceKey, int> References, ImmutableArray<ResourceKey> Acquire,
    ImmutableArray<ResourceKey> Release, ImmutableArray<string> Exceptions, ImmutableDictionary<ResourceKey, ObservationRequirement> Requirements);

public sealed partial class EngineHost
{
    private ModeState modeState = new(EngineMode.Normal, EngineMode.Normal, 1, TransitionStatus.Effective, "DefaultNormal");
    private long modeGeneration = 1;
    private long planVersion;
    private long acknowledgedPlanVersion;
    private ObservationPlan? observationPlan;
    private ImmutableHashSet<ResourceKey> heldResources = ImmutableHashSet<ResourceKey>.Empty;
    private readonly HashSet<ResourceKey> resumeSnapshots = [];
    private readonly HashSet<ResourceKey> transitionFlights = [];
    private readonly Dictionary<ResourceKey, long> transitionObservationFences = [];
    private TaskCompletionSource planSignal = NewSignal();
    private long baseReferencesRevision = -1;
    private EngineMode baseReferencesMode;
    private Dictionary<ResourceKey, int> baseReferences = [];
    private Dictionary<ResourceKey, ObservationRequirement> baseRequirements = [];
    private HashSet<ResourceKey> plannedAdditionalKeys = [];
    private long heldRevision;
    private long plannedHeldRevision = -1;
    private long transitionRevision;
    private long plannedTransitionRevision = -1;

    public ModeState CurrentMode { get { lock (gate) return modeState; } }
    public ObservationPlan GetObservationPlan()
    {
        lock (gate)
        {
            if (!running && observationPlan is not null) return observationPlan;
            RequireAutomation(); DemandChanged(); return observationPlan!;
        }
    }
    public async Task<ObservationPlan> WaitForObservationPlanAsync(long afterVersion, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (observationPlan is { } plan && plan.Version > afterVersion) return plan;
                if (running) RequireAutomation();
                else if (observationPlan is null) throw new InvalidOperationException("No observation plan exists.");
                wait = planSignal.Task;
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
    private HashSet<ResourceKey> ExpandResources(IEnumerable<ResourceKey> roots)
    {
        var result = new HashSet<ResourceKey>();
        var pending = new Stack<ResourceKey>(roots);
        while (pending.TryPop(out var key))
        {
            if (!result.Add(key)) continue;
            if (result.Count > 16_384) throw new InvalidOperationException("Resource dependency closure exceeds capacity.");
            if (capabilities.TryGetValue(key.CapabilityId, out var binding))
                foreach (var dependency in binding.Descriptor.Dependencies) pending.Push(dependency);
        }
        return result;
    }
    private Dictionary<ResourceKey, int> GetBaseReferences(EngineMode mode)
    {
        if (baseReferencesRevision == configurationRevision && baseReferencesMode == mode) return baseReferences;
        var refs = new Dictionary<ResourceKey, int>();
        var requirements = new Dictionary<ResourceKey, ObservationRequirement>();
        void Add(IEnumerable<ResourceKey> roots, TimeSpan? maximumAge = null, bool edges = false)
        {
            foreach (var key in ExpandResources(roots))
            {
                refs[key] = refs.GetValueOrDefault(key) + 1;
                var descriptor = capabilities.GetValueOrDefault(key.CapabilityId)?.Descriptor;
                var delay = descriptor?.MaximumRefreshDelay ?? TimeSpan.Zero;
                if (maximumAge is { } age && age < delay) delay = age;
                var preserve = edges || descriptor?.Fidelity == EventFidelity.EdgeSensitive;
                if (requirements.TryGetValue(key, out var old))
                { if (old.MaximumDelay < delay) delay = old.MaximumDelay; preserve |= old.PreserveEdges; }
                requirements[key] = new(delay, preserve);
            }
        }
        foreach (var rule in rules.Values)
        {
            if (!rule.Definition.Enabled || rule.AutomaticPauseReason is not null || mode == EngineMode.Lightweight && !rule.Definition.Retention.KeepInLightweight) continue;
            if (!Authorize(rule.Definition.Principal, rule.Definition.Action.Key, Operation(rule.Definition.Action)).Allowed ||
                rule.Definition.Condition.Dependencies.Any(key => !Authorize(rule.Definition.Principal, key, CapabilityOperation.Observe).Allowed)) continue;
            Add(rule.Definition.Condition.Dependencies.Append(rule.Definition.Action.Key), rule.Definition.Timing.MaximumInputAge,
                rule.Definition.Action.Kind == RuleActionKind.Command || rule.Definition.Timing.MinimumDuration > TimeSpan.Zero);
        }
        foreach (var key in overrides.Keys) Add([key]);
        baseReferences = refs; baseRequirements = requirements; baseReferencesRevision = configurationRevision; baseReferencesMode = mode;
        return refs;
    }
    private Dictionary<ResourceKey, int> BuildReferences(EngineMode mode)
    {
        var refs = new Dictionary<ResourceKey, int>(GetBaseReferences(mode));
        void Add(IEnumerable<ResourceKey> roots)
        {
            foreach (var key in ExpandResources(roots)) refs[key] = refs.GetValueOrDefault(key) + 1;
        }
        foreach (var flight in flights.Values) Add(flight.Work.RequiredResources.IsDefault ? [flight.Work.Key] : flight.Work.RequiredResources);
        foreach (var key in transitionObservationFences.Keys) Add([key]);
        return refs;
    }
    private string? ValidateResources(Dictionary<ResourceKey, int> refs)
    {
        foreach (var key in refs.Keys)
            if (!capabilities.TryGetValue(key.CapabilityId, out var binding) || !binding.Descriptor.Available || binding.Descriptor.Scope != key.Scope.Kind)
                return $"Required capability unavailable: {key.CapabilityId}";
        return null;
    }
    partial void InitializeAutomation()
    {
        foreach (var item in overrides.Values.ToArray())
        {
            if (item.ExpiresAt is not { } expiry || clock.UtcNow < item.StartsAt) continue;
            if (expiry <= clock.UtcNow) overrides.Remove(item.Key);
            else OverrideChanged(item.Key, expiry - clock.UtcNow);
        }
        DemandChanged();
    }
    partial void DemandChanged()
    {
        if (authorization is null || !running) { if (observationPlan is not null) PublishStoppedPlan(); return; }
        foreach (var key in transitionFlights.ToArray())
            if (!flights.ContainsKey(key))
            {
                transitionFlights.Remove(key);
                transitionObservationFences[key] = observationVersions.GetValueOrDefault(key);
                transitionRevision++;
            }
        var baseline = GetBaseReferences(requestedMode);
        var additional = ExpandResources(flights.Values.SelectMany(f => f.Work.RequiredResources.IsDefault ? [f.Work.Key] : f.Work.RequiredResources)
            .Concat(transitionObservationFences.Keys));
        additional.RemoveWhere(baseline.ContainsKey);
        if (observationPlan is { } cached && cached.ConfigurationRevision == configurationRevision && cached.ModeGeneration == modeGeneration &&
            cached.Mode == requestedMode && plannedAdditionalKeys.SetEquals(additional) && plannedHeldRevision == heldRevision && plannedTransitionRevision == transitionRevision) return;
        var refs = BuildReferences(requestedMode);
        var exceptions = refs.Keys.Where(key => requestedMode == EngineMode.Lightweight && capabilities.TryGetValue(key.CapabilityId, out var binding) &&
            !binding.Descriptor.SupportsLightweight).Select(key => $"Required normal-profile capability: {key.CapabilityId}").Distinct().Order(StringComparer.Ordinal).ToImmutableArray();
        var acquire = refs.Keys.Where(key => !heldResources.Contains(key)).ToImmutableArray();
        var release = heldResources.Where(key => !refs.ContainsKey(key)).ToImmutableArray();
        if (observationPlan is { } old && old.ConfigurationRevision == configurationRevision && old.ModeGeneration == modeGeneration &&
            old.Mode == requestedMode && old.References.Count == refs.Count && old.References.All(pair => refs.GetValueOrDefault(pair.Key) == pair.Value) &&
            old.Acquire.ToHashSet().SetEquals(acquire) && old.Release.ToHashSet().SetEquals(release))
        {
            plannedAdditionalKeys = additional; plannedHeldRevision = heldRevision; plannedTransitionRevision = transitionRevision;
            return;
        }
        var requirements = refs.Keys.ToImmutableDictionary(key => key, key => baseRequirements.GetValueOrDefault(key) ??
            observationPlan?.Requirements.GetValueOrDefault(key) ?? new ObservationRequirement(capabilities.GetValueOrDefault(key.CapabilityId)?.Descriptor.MaximumRefreshDelay ?? TimeSpan.Zero, true));
        observationPlan = new(++planVersion, configurationRevision, modeGeneration, requestedMode, refs.ToImmutableDictionary(), acquire, release, exceptions, requirements);
        plannedAdditionalKeys = additional; plannedHeldRevision = heldRevision; plannedTransitionRevision = transitionRevision;
        var previous = planSignal; planSignal = NewSignal(); previous.TrySetResult();
    }
    public ModeState RequestMode(EngineMode mode, TimeSpan? transitionTimeout = null)
    {
        Contract.EnumValue(mode);
        var timeout = transitionTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(transitionTimeout));
        lock (gate)
        {
            RequireAutomation();
            var error = ValidateResources(BuildReferences(mode));
            if (error is not null)
            {
                var previousPaused = rules.Values.Where(Paused).Select(x => x.Definition.Id).ToHashSet(StringComparer.Ordinal);
                requestedMode = effectiveMode;
                CancelDeadline(("mode", "transition")); transitionFlights.Clear(); transitionObservationFences.Clear();
                modeState = new(mode, effectiveMode, ++modeGeneration, TransitionStatus.Blocked, error);
                ChangeModeInputs(previousPaused, effectiveMode); DemandChanged();
                return modeState;
            }
            var previouslyPaused = rules.Values.Where(Paused).Select(x => x.Definition.Id).ToHashSet(StringComparer.Ordinal);
            requestedMode = mode; configurationRevision++;
            modeState = new(mode, effectiveMode, ++modeGeneration, TransitionStatus.Pending, "Awaiting resource confirmation");
            transitionFlights.Clear(); transitionObservationFences.Clear();
            if (mode == EngineMode.Lightweight)
                foreach (var pair in flights)
                    if (!pair.Value.Work.Manual && pair.Value.Work.Winners.Any(id => rules.TryGetValue(id, out var rule) && !rule.Definition.Retention.KeepInLightweight))
                        transitionFlights.Add(pair.Key);
            ChangeModeInputs(previouslyPaused, mode);
            PruneResumeSnapshots();
            DemandChanged();
            SetDeadline(("mode", "transition"), clock.MonotonicNow + timeout, () => FailMode("Mode transition timed out; resources or in-flight work remain unconfirmed"));
            return modeState;
        }
    }
    private void ChangeModeInputs(HashSet<string> previouslyPaused, EngineMode target)
    {
        foreach (var rule in rules.Values)
        {
            var willPause = rule.AutomaticPauseReason is not null || target == EngineMode.Lightweight && !rule.Definition.Retention.KeepInLightweight;
            var wasPaused = previouslyPaused.Contains(rule.Definition.Id);
            if (wasPaused == willPause) continue;
            if (wasPaused && !willPause)
                foreach (var key in rule.Definition.Condition.Dependencies.Append(rule.Definition.Action.Key))
                    if (!heldResources.Contains(key)) resumeSnapshots.Add(key);
            index.Set(rule.Definition.Id, rule.Definition.Enabled && !willPause ? rule.Definition.Condition.Dependencies : []);
            EvaluateRule(rule, baseline: true);
        }
        foreach (var key in targetRules.Keys.ToArray()) Recompute(key);
    }
    public bool AcknowledgeObservationPlan(long version, IEnumerable<ResourceKey> actualHeld, string? error = null,
        IReadOnlyDictionary<ResourceKey, ObservationRequirement>? guarantees = null)
    {
        ArgumentNullException.ThrowIfNull(actualHeld);
        var actual = actualHeld.Take(16_385).ToArray();
        if (actual.Length > 16_384 || actual.Any(x => x is null)) throw new ArgumentException("Invalid held resource set.");
        lock (gate)
        {
            if (running) RequireAutomation();
            var plan = observationPlan ?? throw new InvalidOperationException("No observation plan exists.");
            if (version <= acknowledgedPlanVersion || version > plan.Version) return false;
            acknowledgedPlanVersion = version;
            heldResources = actual.ToImmutableHashSet(); // actual effects of even a stale plan remain facts
            heldRevision++;
            var current = version == plan.Version;
            if (!running)
            {
                PublishStoppedPlan();
                return current && error is null && heldResources.SetEquals(plan.References.Keys);
            }
            if (current && error is not null) { FailMode(error); DemandChanged(); return false; }
            var exact = heldResources.SetEquals(plan.References.Keys);
            if (current && !exact) { FailMode("Resource confirmation mismatch; residual resources retained in plan"); DemandChanged(); return false; }
            if (current && exact && plan.Requirements.Any(pair => guarantees is null || !guarantees.TryGetValue(pair.Key, out var promised) ||
                promised.MaximumDelay < TimeSpan.Zero || promised.MaximumDelay > pair.Value.MaximumDelay || pair.Value.PreserveEdges && !promised.PreserveEdges))
            { FailMode("Observer guarantee missing or weaker than required freshness/fidelity"); DemandChanged(); return false; }
            if (current && exact && modeState.Status == TransitionStatus.Pending && transitionFlights.Count == 0 && transitionObservationFences.Count == 0 && resumeSnapshots.Count == 0)
            {
                var pausedBefore = rules.Values.Where(Paused).ToArray();
                effectiveMode = requestedMode;
                modeState = new(requestedMode, effectiveMode, modeGeneration, TransitionStatus.Effective,
                    plan.Exceptions.IsEmpty ? "Resources confirmed" : "Necessary exceptions retained", plan.Exceptions);
                CancelDeadline(("mode", "transition"));
                foreach (var rule in pausedBefore.Where(rule => !Paused(rule))) { EvaluateRule(rule, baseline: true); Recompute(rule.Definition.Action.Key); }
            }
            DemandChanged();
            return current && exact;
        }
    }
    private void FailMode(string reason)
    {
        var previousRequested = modeState.Requested;
        var previouslyPaused = rules.Values.Where(Paused).Select(x => x.Definition.Id).ToHashSet(StringComparer.Ordinal);
        requestedMode = effectiveMode;
        modeState = new(previousRequested, effectiveMode, modeGeneration, TransitionStatus.Failed, reason,
            heldResources.Select(key => $"Held: {key.CapabilityId}/{key.ResourceId}"));
        CancelDeadline(("mode", "transition"));
        transitionFlights.Clear(); transitionObservationFences.Clear();
        ChangeModeInputs(previouslyPaused, effectiveMode);
        PruneResumeSnapshots();
        DemandChanged();
    }
    private void PruneResumeSnapshots()
    {
        if (resumeSnapshots.Count == 0) return;
        var active = rules.Values.Where(rule => rule.Definition.Enabled && !Paused(rule))
            .SelectMany(rule => rule.Definition.Condition.Dependencies.Append(rule.Definition.Action.Key)).ToHashSet();
        // While exiting lightweight, logically resumed rules remain suppressed until acknowledgment.
        if (requestedMode == EngineMode.Normal)
            active.UnionWith(rules.Values.Where(rule => rule.Definition.Enabled).SelectMany(rule => rule.Definition.Condition.Dependencies.Append(rule.Definition.Action.Key)));
        resumeSnapshots.RemoveWhere(key => !active.Contains(key));
    }
    partial void SnapshotArrived(Mutation mutation)
    {
        if (mutation.IsSnapshot)
            foreach (var rule in rules.Values.Where(r => r.FreshSnapshots.Count != 0))
            {
                foreach (var sample in mutation.Touched) rule.FreshSnapshots.Remove(sample.Observation.Key);
                if (rule.FreshSnapshots.Count == 0) { EvaluateRule(rule, baseline: true); Recompute(rule.Definition.Action.Key); }
            }
        if ((!mutation.IsSnapshot || resumeSnapshots.Count == 0) && transitionObservationFences.Count == 0) return;
        var changed = false;
        var resuming = mutation.IsSnapshot && resumeSnapshots.Count != 0
            ? rules.Values.Where(rule => rule.Definition.Condition.Dependencies.Any(resumeSnapshots.Contains) || resumeSnapshots.Contains(rule.Definition.Action.Key)).ToArray() : [];
        if (mutation.IsSnapshot)
            foreach (var state in mutation.Touched) changed |= resumeSnapshots.Remove(state.Observation.Key);
        foreach (var state in mutation.Touched)
            if (state.Observation.Quality == StateQuality.Known && transitionObservationFences.TryGetValue(state.Observation.Key, out var fence) &&
                observationVersions.GetValueOrDefault(state.Observation.Key) > fence)
            { transitionObservationFences.Remove(state.Observation.Key); changed = true; }
        if (!changed) return;
        transitionRevision++;
        // Resume only after a complete baseline, never replaying missed Command edges.
        foreach (var rule in resuming)
            EvaluateRule(rule, baseline: true);
        foreach (var key in targetRules.Keys.ToArray()) Recompute(key);
        DemandChanged();
    }
    partial void StopControls()
    {
        readyObservations.Clear();
        requestedMode = EngineMode.Normal; effectiveMode = EngineMode.Normal;
        modeState = new(EngineMode.Normal, EngineMode.Normal, ++modeGeneration, TransitionStatus.Effective, "Stopped; mode runtime reset");
        baseReferencesRevision = -1; baseReferences.Clear(); baseRequirements.Clear(); plannedAdditionalKeys.Clear(); heldRevision++;
        resumeSnapshots.Clear(); transitionFlights.Clear(); transitionObservationFences.Clear();
        var previous = planSignal; planSignal = NewSignal(); previous.TrySetResult();
        PublishStoppedPlan();
        // Overrides intentionally survive Stop/Start of this object; durable recovery is a later adapter.
    }
    private void PublishStoppedPlan()
    {
        var refs = new Dictionary<ResourceKey, int>();
        foreach (var flight in flights.Values)
            foreach (var key in flight.Work.RequiredResources.IsDefault ? [flight.Work.Key] : flight.Work.RequiredResources)
                refs[key] = refs.GetValueOrDefault(key) + 1;
        var release = heldResources.Where(key => !refs.ContainsKey(key)).ToImmutableArray();
        if (observationPlan is { } old && old.ConfigurationRevision == -1 && old.References.Count == refs.Count &&
            old.References.All(pair => refs.GetValueOrDefault(pair.Key) == pair.Value) && old.Release.ToHashSet().SetEquals(release)) return;
        observationPlan = new(++planVersion, -1, modeGeneration, EngineMode.Normal, refs.ToImmutableDictionary(), [], release, [], ImmutableDictionary<ResourceKey, ObservationRequirement>.Empty);
        var previous = planSignal; planSignal = NewSignal(); previous.TrySetResult();
    }
}
