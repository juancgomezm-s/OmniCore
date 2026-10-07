namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Proyección pura de un Run desde su historial de eventos canónicos (ADR-0001 §1, INV-021).
/// Nunca se muta; cada evento produce un snapshot inmutable nuevo. El WorkingState se deriva
/// de estas proyecciones en cada Turn (ADR-0016 §7).
/// </summary>
public sealed class RunProjection
{
    public SessionId SessionId { get; }

    public RunId Id { get; }

    public DateTimeOffset CreatedAt { get; }

    public string? Objective { get; }

    public RunMode? Mode { get; }

    public RunModeAuthority? ModeAuthority { get; }

    public RunReasoningSelectionState? ReasoningSelection { get; }

    public ExecutionStrategy? Strategy { get; }

    public FailurePolicy? FailurePolicy { get; }

    public RunState State { get; }

    public TaskId? RootTask { get; }

    public bool HasPlan { get; }

    private RunProjection(SessionId sessionId, RunId id, DateTimeOffset createdAt, string? objective,
        RunMode? mode, RunModeAuthority? modeAuthority, RunReasoningSelectionState? reasoningSelection,
        ExecutionStrategy? strategy, FailurePolicy? failurePolicy, RunState state, TaskId? rootTask, bool hasPlan)
    {
        SessionId = sessionId;
        Id = id;
        CreatedAt = createdAt;
        Objective = objective;
        Mode = mode;
        ModeAuthority = modeAuthority;
        ReasoningSelection = reasoningSelection;
        Strategy = strategy;
        FailurePolicy = failurePolicy;
        State = state;
        RootTask = rootTask;
        HasPlan = hasPlan;
    }

    /// <summary>
    /// Reconstruye la proyección aplicando en orden los eventos de ESTE Run (una sesión puede tener
    /// varios). El estado se calcula con <see cref="StateMachines.ApplyRun"/>: un journal con una
    /// transición inválida lanza <see cref="InvalidStateTransitionException"/> (ADR-0036).
    /// </summary>
    public static RunProjection Replay(SessionId sessionId, RunId id, IEventCodecRegistry registry,
        IReadOnlyList<DomainEvent> evts)
    {
        var state = RunState.Created;
        var createdAt = DateTimeOffset.Now;
        string? objective = null;
        RunMode? mode = null;
        RunModeAuthority? modeAuthority = null;
        RunReasoningSelectionState? reasoningSelection = null;
        ExecutionStrategy? strategy = null;
        FailurePolicy? failurePolicy = null;
        TaskId? rootTask = null;
        var hasPlan = false;
        RunModeTransitionAuthorized? pendingModeTransition = null;
        (RunMode From, RunMode To)? lastExplicitModeChange = null;

        var created = false;
        foreach (var evt in evts)
        {
            var payload = registry.Decode(evt);
            if (payload is RunCreated runCreated)
            {
                if (!runCreated.RunId.Equals(id))
                {
                    continue;
                }

                if (created)
                {
                    throw new InvalidStateTransitionException("run", state, payload.Type().ToString());
                }

                created = true;
                objective = runCreated.Objective;
                mode = runCreated.Mode;
                modeAuthority = RunModeAuthority.Legacy(runCreated);
                strategy = runCreated.Strategy;
                failurePolicy = runCreated.FailurePolicy;
                rootTask = runCreated.RootTask;
                createdAt = runCreated.CreatedAt;
            }
            else if (RunOf(payload) is { } runId && runId.Equals(id))
            {
                if (!created)
                {
                    throw new InvalidStateTransitionException("run", "inexistente", payload.Type().ToString());
                }

                if (payload is RunInteractionResumed resumed
                    && !ValidInteractionResume(resumed, evt, registry, evts))
                    throw new InvalidStateTransitionException("run", "interaction resume lacks matching resolved user consent",
                        payload.Type().ToString());

                state = StateMachines.ApplyRun(state, payload);
            }
            else if (payload is RunModeChanged changed && changed.RunId.Equals(id))
            {
                lastExplicitModeChange = (changed.From, changed.To);
                mode = changed.To;
                if (modeAuthority is not null) modeAuthority = modeAuthority with { Mode = changed.To };
            }
            else if (payload is RunModeTransitionAuthorized transition && transition.RunId.Equals(id))
            {
                if (string.IsNullOrWhiteSpace(transition.CommandId)
                    || transition.Origin != "User"
                    || pendingModeTransition is not null || modeAuthority is null
                    || transition.To != mode || transition.AuthorityRevision != modeAuthority.Revision + 1
                    || (transition.From == transition.To && transition.From != modeAuthority.Mode)
                    || (transition.From != transition.To
                        && lastExplicitModeChange != (transition.From, transition.To)))
                    throw new InvalidStateTransitionException("run mode transition", "authorization does not match the effective mode/revision",
                        payload.Type().ToString());
                pendingModeTransition = transition;
                lastExplicitModeChange = null;
            }
            else if (payload is RunModeAuthoritySelected selected
                && selected.Authority.RunId.Equals(id))
            {
                if (string.IsNullOrWhiteSpace(selected.CommandId)
                    || selected.Origin is not ("RunCreated" or "User" or "PlanApproval"))
                    throw new InvalidStateTransitionException("run mode authority", "invalid selection provenance",
                        payload.Type().ToString());
                selected.Authority.Validate();
                if (modeAuthority is null || selected.Authority.Revision != modeAuthority.Revision + 1
                    || selected.Authority.ObjectiveDigest != RunModeAuthority.ObjectiveDigestFor(objective ?? ""))
                    throw new InvalidStateTransitionException("run mode authority", "revision or objective mismatch",
                        payload.Type().ToString());
                if (pendingModeTransition is null)
                {
                    if (selected.Origin != "RunCreated" || selected.Authority.Revision != 1
                        || selected.Authority.Mode != mode)
                        throw new InvalidStateTransitionException("run mode authority", "selection has no matching authorization",
                            payload.Type().ToString());
                }
                else if (selected.Origin == "RunCreated"
                    || selected.Origin is not ("User" or "PlanApproval")
                    || pendingModeTransition.CommandId != selected.CommandId
                    || pendingModeTransition.To != selected.Authority.Mode
                    || pendingModeTransition.AuthorityRevision != selected.Authority.Revision
                    || pendingModeTransition.ObjectiveRevision != selected.Authority.ObjectiveRevision
                    || pendingModeTransition.ObjectiveDigest != selected.Authority.ObjectiveDigest
                    || pendingModeTransition.PolicyRevision != selected.Authority.PolicyRevision
                    || pendingModeTransition.AuthorizationId != selected.Authority.Authorization?.AuthorizationId)
                    throw new InvalidStateTransitionException("run mode authority", "selection does not match its transition authorization",
                        payload.Type().ToString());
                modeAuthority = selected.Authority;
                mode = selected.Authority.Mode;
                strategy = selected.Authority.Strategy;
                pendingModeTransition = null;
            }
            else if (payload is RunModeAuthorityRevoked revoked && revoked.RunId.Equals(id))
            {
                if (string.IsNullOrWhiteSpace(revoked.CommandId) || string.IsNullOrWhiteSpace(revoked.Reason)
                    || revoked.Origin != "User"
                    || modeAuthority?.Authorization is not { } authorization
                    || authorization.AuthorizationId != revoked.AuthorizationId
                    || revoked.AuthorityRevision != modeAuthority.Revision + 1)
                    throw new InvalidStateTransitionException("run mode authority", "invalid revocation",
                        payload.Type().ToString());
                modeAuthority = modeAuthority with { Revision = revoked.AuthorityRevision,
                    AutoModeSwitch = false, Authorization = null, ProductEffort = ProductEffort.Standard,
                    ModePinned = true };
            }
            else if (payload is RunReasoningPreferenceSelected selectedReasoning
                && selectedReasoning.RunId.Equals(id))
            {
                if (string.IsNullOrWhiteSpace(selectedReasoning.CommandId)
                    || selectedReasoning.Source is not ("User" or "UserDefault")
                    || (selectedReasoning.Source == "UserDefault"
                        ? selectedReasoning.CommandId != "RunCreated" || selectedReasoning.UserPreferenceRevision is not > 0
                            || selectedReasoning.Revision != 1
                        : selectedReasoning.CommandId == "RunCreated" || selectedReasoning.UserPreferenceRevision is not null)
                    || selectedReasoning.Revision != (reasoningSelection?.Revision ?? 0) + 1)
                    throw new InvalidStateTransitionException("run reasoning preference", "invalid selection provenance or revision",
                        payload.Type().ToString());
                ValidateReasoningRequest(selectedReasoning.Request);
                var capturedDefault = selectedReasoning.Source == "UserDefault"
                    ? (HasSelection: true, Request: selectedReasoning.Request,
                        Revision: selectedReasoning.UserPreferenceRevision)
                    : (HasSelection: reasoningSelection?.HasCapturedUserDefault ?? false,
                        Request: reasoningSelection?.CapturedUserDefault,
                        Revision: reasoningSelection?.CapturedUserPreferenceRevision);
                reasoningSelection = new RunReasoningSelectionState(selectedReasoning.Revision, true,
                    selectedReasoning.Request, selectedReasoning.Source, selectedReasoning.UserPreferenceRevision,
                    capturedDefault.HasSelection, capturedDefault.Request, capturedDefault.Revision);
            }
            else if (payload is RunReasoningPreferenceRevoked revokedReasoning
                && revokedReasoning.RunId.Equals(id))
            {
                if (string.IsNullOrWhiteSpace(revokedReasoning.CommandId) || revokedReasoning.Origin != "User"
                    || reasoningSelection is null || !reasoningSelection.HasSelection
                    || revokedReasoning.Revision != reasoningSelection.Revision + 1)
                    throw new InvalidStateTransitionException("run reasoning preference", "invalid revocation",
                        payload.Type().ToString());
                reasoningSelection = new RunReasoningSelectionState(revokedReasoning.Revision, false,
                    null, "User", null, reasoningSelection.HasCapturedUserDefault,
                    reasoningSelection.CapturedUserDefault, reasoningSelection.CapturedUserPreferenceRevision);
            }
            else if (payload is PlanCreated plan && plan.RunId.Equals(id))
            {
                hasPlan = true;
            }
        }

        if (pendingModeTransition is not null)
            throw new InvalidStateTransitionException("run mode transition", "authorized selection is missing",
                pendingModeTransition.Type().ToString());

        return new RunProjection(sessionId, id, createdAt, objective, mode, modeAuthority, reasoningSelection,
            strategy, failurePolicy, state, rootTask, hasPlan);
    }

    /// <summary>RunId de los eventos que cambian el estado del Run (ADR-0036 §1).</summary>
    private static RunId? RunOf(DomainEventPayload payload) => payload switch
    {
        RunStarted e => e.RunId,
        RunAwaitingInput e => e.RunId,
        RunInteractionResumed e => e.RunId,
        UserInputReceived e => e.RunId,
        RunValidationStarted e => e.RunId,
        RunValidationRejected e => e.RunId,
        RunCompleted e => e.RunId,
        RunFailed e => e.RunId,
        RunCancelled e => e.RunId,
        _ => null,
    };

    private static bool ValidInteractionResume(RunInteractionResumed resumed, DomainEvent envelope,
        IEventCodecRegistry registry, IReadOnlyList<DomainEvent> events)
    {
        if (resumed.InteractionId.Value == Guid.Empty || string.IsNullOrWhiteSpace(resumed.CommandId)
            || envelope.RunId != resumed.RunId
            || envelope.Causation is not CommandCausation resumeCause
            || resumeCause.CommandId.ToString() != resumed.CommandId)
            return false;

        var requestEvent = events.Where(item => item.Sequence < envelope.Sequence
                && registry.Decode(item) is InteractionRequested request
                && request.InteractionId == resumed.InteractionId)
            .OrderBy(item => item.Sequence).LastOrDefault();
        if (requestEvent is null || requestEvent.RunId != resumed.RunId
            || registry.Decode(requestEvent) is not InteractionRequested
                { Kind: InteractionKind.ModelRouteConsent })
            return false;

        var resolutions = events.Where(item => item.Sequence > requestEvent.Sequence
            && item.Sequence < envelope.Sequence
            && registry.Decode(item) is InteractionResolved resolved
            && resolved.InteractionId == resumed.InteractionId
            && resolved.OptionId == "allow_route" && resolved.Cause == InteractionCause.User).ToArray();
        if (resolutions.Length != 1) return false;
        var resolution = resolutions[0];
        if (resolution is null || resolution.RunId != resumed.RunId
            || resolution.Causation is not CommandCausation resolutionCause
            || resolutionCause.CommandId != resumeCause.CommandId)
            return false;

        var pending = new HashSet<InteractionId>();
        foreach (var item in events.Where(item => item.Sequence < envelope.Sequence).OrderBy(item => item.Sequence))
        {
            switch (registry.Decode(item))
            {
                case InteractionRequested requested: pending.Add(requested.InteractionId); break;
                case InteractionResolved resolved: pending.Remove(resolved.InteractionId); break;
                case InteractionExpired expired: pending.Remove(expired.InteractionId); break;
            }
        }
        return pending.Count == 0;
    }

    public bool IsTerminal() => StateMachines.IsRunTerminal(State);

    public bool IsActiveNonTerminal() => !IsTerminal();

    /// <summary>Construye una proyección Run mínima para samples/demos (M2).</summary>
    public static RunProjection ForSample(SessionId sessionId, RunId id, string objective) =>
        new RunProjection(sessionId, id, DateTimeOffset.Now, objective, RunMode.Act, null, null, ExecutionStrategy.Direct,
            OmniCore.Domain.FailurePolicy.BlockDependents, RunState.Running, null, true);

    private static void ValidateReasoningRequest(ReasoningRequest? request)
    {
        if (request is not null && (string.IsNullOrWhiteSpace(request.Kind)
            || (request.Kind == "budget" ? request.BudgetTokens is null or < 1024 : request.BudgetTokens is not null)))
            throw new InvalidStateTransitionException("run reasoning preference", "invalid request", "run.reasoning_preference_selected");
    }
}

/// <summary>Proyección del TaskGraph (spec §9, ADR-0036 §2).</summary>
public sealed class TaskGraphProjection
{
    private readonly Dictionary<TaskId, Task> _tasks = new();

    private TaskGraphProjection() { }

    /// <summary>Proyección desde payloads en memoria (estado vivo, sin journal).</summary>
    public static TaskGraphProjection FromPayloads(IEnumerable<DomainEventPayload> payloads)
    {
        var projection = new TaskGraphProjection();
        foreach (var payload in payloads)
        {
            projection.Apply(payload);
        }

        return projection;
    }

    public static TaskGraphProjection Replay(IEventCodecRegistry registry, IReadOnlyList<DomainEvent> evts)
    {
        var projection = new TaskGraphProjection();
        foreach (var evt in evts)
        {
            projection.Apply(registry.Decode(evt));
        }

        return projection;
    }

    public IReadOnlyList<Task> Tasks() => _tasks.Values.ToArray();

    public Task? Get(TaskId id) => _tasks.TryGetValue(id, out var task) ? task : null;

    public TaskState? StateOf(TaskId id)
    {
        var task = Get(id);
        return task is null ? null : task.State;
    }

    private void Apply(DomainEventPayload payload)
    {
        if (payload is TaskCreated created)
        {
            _tasks[created.TaskId] = new Task(created.TaskId, created.Objective, TaskState.Pending,
                created.Dependencies, created.Budget);
        }
        else if (TaskOf(payload) is { } id)
        {
            var task = Get(id)
                ?? throw new InvalidStateTransitionException("task", "inexistente", payload.Type().ToString());
            _tasks[id] = new Task(task.Id, task.Objective, StateMachines.ApplyTask(task.State, payload),
                task.Dependencies, task.Budget);
        }
    }

    private static TaskId? TaskOf(DomainEventPayload payload) => payload switch
    {
        TaskReady e => e.TaskId,
        TaskStarted e => e.TaskId,
        TaskBlocked e => e.TaskId,
        TaskUnblocked e => e.TaskId,
        TaskCompleted e => e.TaskId,
        TaskFailed e => e.TaskId,
        TaskSkipped e => e.TaskId,
        TaskCancelled e => e.TaskId,
        _ => null,
    };
}

/// <summary>Proyección de las Lanes (spec §10, ADR-0036 §3).</summary>
public sealed class LaneProjection
{
    private readonly Dictionary<LaneId, Lane> _lanes = new();

    private LaneProjection() { }

    public static LaneProjection Empty() => new LaneProjection();

    /// <summary>Proyección desde payloads en memoria (estado vivo, sin journal).</summary>
    public static LaneProjection FromPayloads(IEnumerable<DomainEventPayload> payloads)
    {
        var projection = new LaneProjection();
        foreach (var payload in payloads)
        {
            projection.Apply(payload);
        }

        return projection;
    }

    public static LaneProjection Replay(IEventCodecRegistry registry, IReadOnlyList<DomainEvent> evts)
    {
        var projection = new LaneProjection();
        foreach (var evt in evts)
        {
            projection.Apply(registry.Decode(evt));
        }

        return projection;
    }

    public IReadOnlyList<Lane> Lanes() => _lanes.Values.ToArray();

    public Lane? Get(LaneId id) => _lanes.TryGetValue(id, out var lane) ? lane : null;

    public LaneState? StateOf(LaneId id)
    {
        var lane = Get(id);
        return lane is null ? null : lane.State;
    }

    public IReadOnlyList<Lane> ForTask(TaskId taskId)
    {
        var result = new List<Lane>();
        foreach (var lane in _lanes.Values)
        {
            if (lane.TaskId.Equals(taskId))
            {
                result.Add(lane);
            }
        }

        return result.ToArray();
    }

    private void Apply(DomainEventPayload payload)
    {
        if (payload is LaneCreated created)
        {
            _lanes[created.LaneId] = new Lane(created.LaneId, created.TaskId, LaneState.Queued,
                created.AgentProfile, null);
        }
        else if (LaneOf(payload) is { } id)
        {
            var lane = Get(id)
                ?? throw new InvalidStateTransitionException("lane", "inexistente", payload.Type().ToString());
            _lanes[id] = new Lane(lane.Id, lane.TaskId, StateMachines.ApplyLane(lane.State, payload),
                lane.AgentProfile, lane.LastHeartbeatAt);
        }
    }

    private static LaneId? LaneOf(DomainEventPayload payload) => payload switch
    {
        LaneProvisioning e => e.LaneId,
        LaneStarted e => e.LaneId,
        LaneBlocked e => e.LaneId,
        LaneUnblocked e => e.LaneId,
        LaneCompleted e => e.LaneId,
        LaneFailed e => e.LaneId,
        LaneCancelled e => e.LaneId,
        _ => null,
    };
}
