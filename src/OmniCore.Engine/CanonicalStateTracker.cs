namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Estado canónico de cada entidad de una sesión (Run, Task, Lane, Turn, ToolCall, PlanItem) y
/// validación de cada evento contra las máquinas de estado de ADR-0036. Lo usan
/// <see cref="EventStream"/> antes de persistir (un evento inválido nunca llega al journal) y las
/// proyecciones al reconstruir (un journal inválido falla con error tipado en vez de producir un
/// estado inventado). Un evento sobre una entidad que no existe también es inválido.
/// </summary>
public sealed class CanonicalStateTracker
{
    private readonly Dictionary<RunId, RunState> _runs;

    private readonly Dictionary<RunId, RunReasoningSelectionState> _runReasoningSelections;
    private readonly Dictionary<InteractionId, (InteractionKind Kind, bool Pending, string? OptionId,
        InteractionCause? Cause)> _interactionStates;

    private readonly Dictionary<TaskId, TaskState> _tasks;

    private readonly Dictionary<LaneId, LaneState> _lanes;

    private readonly Dictionary<TurnId, TurnState> _turns;

    private readonly Dictionary<ToolCallId, ToolCallState> _toolCalls;

    private readonly Dictionary<ToolCallId, ReconciliationOutcome> _toolCallReconciliationOutcomes;
    private readonly Dictionary<ToolCallId, WorktreeIntegrationLifecycle> _worktreeIntegrations;
    private readonly Dictionary<LaneId, LaneWorktreeCreated> _laneWorktrees;
    private readonly Dictionary<LaneId, LaneWorktreeBound> _laneWorktreeBindings;

    private readonly Dictionary<PlanItemId, PlanItemState> _planItems;

    // Identity relationships are retained separately from lifecycle states. Older TurnStarted
    // payloads remain readable, but steering must name an existing, matching Run/Task/Lane/Turn.
    private readonly Dictionary<TaskId, RunId> _taskRuns;
    private readonly Dictionary<LaneId, TaskId> _laneTasks;
    private readonly Dictionary<TurnId, LaneId> _turnLanes;
    private readonly Dictionary<TurnId, ReasoningResolution?> _turnReasoningResolutions;
    private sealed record SteeringEntry(RunId Run, LaneId Lane, TurnId Turn, string State);
    private sealed record WorktreeIntegrationLifecycle(string OwnershipId, string ProposalId,
        WorktreeIntegrationState State, string[] Paths);
    private readonly Dictionary<SteeringId, SteeringEntry> _steering;

    public CanonicalStateTracker()
        : this(new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new())
    {
    }

    private CanonicalStateTracker(Dictionary<RunId, RunState> runs, Dictionary<TaskId, TaskState> tasks,
        Dictionary<LaneId, LaneState> lanes, Dictionary<TurnId, TurnState> turns,
        Dictionary<ToolCallId, ToolCallState> toolCalls,
        Dictionary<ToolCallId, ReconciliationOutcome> toolCallReconciliationOutcomes,
        Dictionary<PlanItemId, PlanItemState> planItems, Dictionary<TaskId, RunId> taskRuns,
        Dictionary<LaneId, TaskId> laneTasks, Dictionary<TurnId, LaneId> turnLanes,
        Dictionary<TurnId, ReasoningResolution?> turnReasoningResolutions,
        Dictionary<SteeringId, SteeringEntry> steering,
        Dictionary<RunId, RunReasoningSelectionState> runReasoningSelections,
        Dictionary<InteractionId, (InteractionKind Kind, bool Pending, string? OptionId,
            InteractionCause? Cause)> interactionStates,
        Dictionary<ToolCallId, WorktreeIntegrationLifecycle> worktreeIntegrations,
        Dictionary<LaneId, LaneWorktreeCreated> laneWorktrees,
        Dictionary<LaneId, LaneWorktreeBound> laneWorktreeBindings)
    {
        _runs = runs;
        _runReasoningSelections = runReasoningSelections;
        _interactionStates = interactionStates;
        _tasks = tasks;
        _lanes = lanes;
        _turns = turns;
        _toolCalls = toolCalls;
        _toolCallReconciliationOutcomes = toolCallReconciliationOutcomes;
        _worktreeIntegrations = worktreeIntegrations;
        _laneWorktrees = laneWorktrees;
        _laneWorktreeBindings = laneWorktreeBindings;
        _planItems = planItems;
        _taskRuns = taskRuns;
        _laneTasks = laneTasks;
        _turnLanes = turnLanes;
        _turnReasoningResolutions = turnReasoningResolutions;
        _steering = steering;
    }

    /// <summary>Reconstruye el estado aplicando (y validando) todos los eventos en orden.</summary>
    public static CanonicalStateTracker Replay(IEventCodecRegistry codecs, IReadOnlyList<DomainEvent> events)
    {
        var tracker = new CanonicalStateTracker();
        foreach (var evt in events)
        {
            var payload = codecs.Decode(evt);
            if (RunModePayloadRunId(payload) is { } runId
                && (evt.RunId != runId || evt.CorrelationId != runId))
                throw new InvalidStateTransitionException("run mode authority", "event envelope scope mismatch",
                    payload.Type().ToString());
            if (payload is RunInteractionResumed resumed)
            {
                var requestEvent = events.Where(item => item.Sequence < evt.Sequence
                        && codecs.Decode(item) is InteractionRequested request
                        && request.InteractionId == resumed.InteractionId)
                    .OrderBy(item => item.Sequence).LastOrDefault();
                if (requestEvent is not null && codecs.Decode(requestEvent) is InteractionRequested
                    { Kind: InteractionKind.Permission } permissionRequest
                    && (evt.Causation is not CommandCausation resumeCause
                        || !RunProjection.ValidNativeWorktreePermissionResume(resumed, evt, resumeCause,
                            requestEvent, permissionRequest, codecs, events)
                        && !RunProjection.ValidNativeProcessControlPermissionResume(resumed, evt, resumeCause,
                            requestEvent, permissionRequest, codecs, events)))
                    throw new InvalidStateTransitionException("run", "native Permission resume is not bound to its canonical tool call",
                        payload.Type().ToString());
            }
            tracker.Apply(payload);
        }

        FanOutGroupProjection.Replay(codecs, events);

        return tracker;
    }

    private static RunId? RunModePayloadRunId(DomainEventPayload payload) => payload switch
    {
        RunModeChanged changed => changed.RunId,
        RunModeProposed proposed => proposed.RunId,
        RunModeTransitionAuthorized transition => transition.RunId,
        RunModeAuthoritySelected selected => selected.Authority.RunId,
        RunModeAuthorityRevoked revoked => revoked.RunId,
        _ => null,
    };

    /// <summary>Copia independiente (para validar un lote sin tocar el estado si falla).</summary>
    public CanonicalStateTracker Clone() => new(new(_runs), new(_tasks), new(_lanes), new(_turns),
        new(_toolCalls), new(_toolCallReconciliationOutcomes), new(_planItems), new(_taskRuns),
        new(_laneTasks), new(_turnLanes), new(_turnReasoningResolutions), new(_steering),
        new(_runReasoningSelections), new(_interactionStates), new(_worktreeIntegrations), new(_laneWorktrees),
        new(_laneWorktreeBindings));

    /// <summary>
    /// Foto canónica y ordenada de todos los estados ("entidad:id=estado"), para comparar dos
    /// reconstrucciones del mismo journal (golden rule, ADR-0041 §2).
    /// </summary>
    public IReadOnlyList<string> Snapshot()
    {
        var lines = new List<string>();
        lines.AddRange(_runs.Select(kv => "run:" + kv.Key + "=" + kv.Value));
        lines.AddRange(_runReasoningSelections.Select(kv => "run_reasoning:" + kv.Key + "="
            + kv.Value.Revision + ":" + kv.Value.HasSelection + ":" + kv.Value.Request?.Kind
            + ":captured=" + kv.Value.HasCapturedUserDefault + ":" + kv.Value.CapturedUserDefault?.Kind
            + ":capturedRevision=" + kv.Value.CapturedUserPreferenceRevision));
        lines.AddRange(_tasks.Select(kv => "task:" + kv.Key + "=" + kv.Value));
        lines.AddRange(_lanes.Select(kv => "lane:" + kv.Key + "=" + kv.Value));
        lines.AddRange(_turns.Select(kv => "turn:" + kv.Key + "=" + kv.Value));
        lines.AddRange(_toolCalls.Select(kv => "toolcall:" + kv.Key + "=" + kv.Value));
        lines.AddRange(_worktreeIntegrations.Select(kv => "worktree_integration:" + kv.Key + "="
            + kv.Value.OwnershipId + ":" + kv.Value.ProposalId + ":" + kv.Value.State + ":"
            + string.Join(",", kv.Value.Paths.OrderBy(path => path, StringComparer.Ordinal))));
        lines.AddRange(_laneWorktrees.Select(kv => "lane_worktree:" + kv.Key + "="
            + kv.Value.OwnershipId + ":" + kv.Value.SnapshotCommit + ":" + kv.Value.WorktreePath));
        lines.AddRange(_laneWorktreeBindings.Select(kv => "lane_worktree_binding:" + kv.Key + "="
            + kv.Value.OwnerLaneId + ":" + kv.Value.OwnershipId + ":" + kv.Value.SourceTree));
        lines.AddRange(_planItems.Select(kv => "plan_item:" + kv.Key + "=" + kv.Value));
        lines.AddRange(_interactionStates.Select(kv => "interaction:" + kv.Key + "=" + kv.Value.Kind + ":"
            + kv.Value.Pending + ":" + kv.Value.OptionId + ":" + kv.Value.Cause));
        lines.AddRange(_steering.Select(kv => "steering:" + kv.Key + "=" + kv.Value.State));
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    public RunState? Run(RunId id) => _runs.TryGetValue(id, out var s) ? s : null;

    public TaskState? Task(TaskId id) => _tasks.TryGetValue(id, out var s) ? s : null;

    public LaneState? Lane(LaneId id) => _lanes.TryGetValue(id, out var s) ? s : null;

    public TurnState? Turn(TurnId id) => _turns.TryGetValue(id, out var s) ? s : null;

    public ToolCallState? ToolCall(ToolCallId id) => _toolCalls.TryGetValue(id, out var s) ? s : null;

    public WorktreeIntegrationState? WorktreeIntegration(ToolCallId id) =>
        _worktreeIntegrations.TryGetValue(id, out var s) ? s.State : null;

    public LaneWorktreeCreated? LaneWorktree(LaneId id) =>
        _laneWorktrees.TryGetValue(id, out var worktree) ? worktree : null;

    public LaneWorktreeBound? LaneWorktreeBinding(LaneId id) =>
        _laneWorktreeBindings.TryGetValue(id, out var binding) ? binding : null;

    public PlanItemState? PlanItem(PlanItemId id) => _planItems.TryGetValue(id, out var s) ? s : null;

    /// <summary>
    /// Aplica un evento: calcula el estado destino con <see cref="StateMachines"/> o lanza
    /// <see cref="InvalidStateTransitionException"/> sin modificar nada.
    /// </summary>
    public void Apply(DomainEventPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        switch (payload)
        {
            case InteractionRequested requested:
                if (requested.InteractionId.Value == Guid.Empty || !Enum.IsDefined(requested.Kind))
                    throw new InvalidStateTransitionException("interaction", "invalid request identity or kind",
                        payload.Type().ToString());
                if (_interactionStates.TryGetValue(requested.InteractionId, out var priorInteraction)
                    && priorInteraction.Pending)
                    throw new InvalidStateTransitionException("interaction", "duplicate pending identity",
                        payload.Type().ToString());
                _interactionStates[requested.InteractionId] = (requested.Kind, true, null, null);
                break;
            case InteractionResolved resolved when _interactionStates.TryGetValue(resolved.InteractionId,
                out var pendingResolved):
                if (pendingResolved.Pending)
                    _interactionStates[resolved.InteractionId] = (pendingResolved.Kind, false,
                        resolved.OptionId, resolved.Cause);
                break;
            case InteractionExpired expired when _interactionStates.TryGetValue(expired.InteractionId,
                out var pendingExpired) && pendingExpired.Pending:
                _interactionStates[expired.InteractionId] = (pendingExpired.Kind, false, null, null);
                break;

            // ── Run ──
            case RunCreated created:
                Create(_runs, created.RunId, RunState.Created, "run", payload);
                break;
            case RunStarted e: Transition(_runs, e.RunId, "run", payload, StateMachines.ApplyRun); break;
            case RunAwaitingInput e: Transition(_runs, e.RunId, "run", payload, StateMachines.ApplyRun); break;
            case RunInteractionResumed e:
                if (e.InteractionId.Value == Guid.Empty || string.IsNullOrWhiteSpace(e.CommandId))
                    throw new InvalidStateTransitionException("run", "incomplete interaction resume", payload.Type().ToString());
                if (!_interactionStates.TryGetValue(e.InteractionId, out var resumedInteraction)
                    || resumedInteraction.Pending || _interactionStates.Values.Any(state => state.Pending))
                    throw new InvalidStateTransitionException("run", "interaction resume lacks resolved user consent",
                        payload.Type().ToString());
                if (!IsResumableUserInteraction(resumedInteraction))
                    throw new InvalidStateTransitionException("run",
                        resumedInteraction.Kind == InteractionKind.WeakSandboxConsent
                            ? "interaction resume lacks resolved user weak sandbox consent"
                            : "interaction resume lacks resolved user route consent",
                        payload.Type().ToString());
                Transition(_runs, e.RunId, "run", payload, StateMachines.ApplyRun);
                break;
            case UserInputReceived e: Transition(_runs, e.RunId, "run", payload, StateMachines.ApplyRun); break;
            case RunValidationStarted e: Transition(_runs, e.RunId, "run", payload, StateMachines.ApplyRun); break;
            case RunValidationRejected e: Transition(_runs, e.RunId, "run", payload, StateMachines.ApplyRun); break;
            case RunCompleted e: Transition(_runs, e.RunId, "run", payload, StateMachines.ApplyRun); break;
            case RunFailed e: Transition(_runs, e.RunId, "run", payload, StateMachines.ApplyRun); break;
            case RunCancelled e: Transition(_runs, e.RunId, "run", payload, StateMachines.ApplyRun); break;
            case RunModeChanged e: RequireNonTerminalRun(e.RunId, payload); break;
            case RunModeProposed e:
                ModeProposalProjection.Validate(e);
                RequireNonTerminalRun(e.RunId, payload);
                if (!_turnLanes.TryGetValue(e.TurnId, out var proposalLane)
                    || !_turns.TryGetValue(e.TurnId, out var proposalTurnState)
                    || proposalTurnState != TurnState.Started
                    || !_laneTasks.TryGetValue(proposalLane, out var proposalTask)
                    || !_taskRuns.TryGetValue(proposalTask, out var proposalRun) || proposalRun != e.RunId
                    || !_toolCalls.TryGetValue(e.ToolCallId, out var proposalToolState)
                    || proposalToolState != ToolCallState.Succeeded)
                    throw new InvalidStateTransitionException("run mode proposal", "missing turn or successful tool",
                        payload.Type().ToString());
                break;
            case RunModeAuthoritySelected e:
                e.Authority.Validate();
                if (string.IsNullOrWhiteSpace(e.CommandId)
                    || e.Origin is not ("RunCreated" or "User" or "PlanApproval" or "UltraCodePolicy"))
                    throw new InvalidStateTransitionException("run mode authority", "invalid selection provenance",
                        payload.Type().ToString());
                RequireNonTerminalRun(e.Authority.RunId, payload);
                break;
            case RunModeTransitionAuthorized e:
                if (string.IsNullOrWhiteSpace(e.CommandId) || string.IsNullOrWhiteSpace(e.Origin)
                    || string.IsNullOrWhiteSpace(e.Reason) || e.AuthorityRevision <= 0
                    || e.ObjectiveRevision <= 0 || e.PolicyRevision <= 0
                    || string.IsNullOrWhiteSpace(e.ObjectiveDigest)
                    || e.Origin is not ("User" or "UltraCodePolicy"))
                    throw new InvalidStateTransitionException("run mode authority", "incomplete transition metadata",
                        payload.Type().ToString());
                RequireNonTerminalRun(e.RunId, payload);
                break;
            case RunModeAuthorityRevoked e:
                if (e.AuthorizationId == Guid.Empty || e.AuthorityRevision <= 0
                    || string.IsNullOrWhiteSpace(e.CommandId) || string.IsNullOrWhiteSpace(e.Origin)
                    || string.IsNullOrWhiteSpace(e.Reason)
                    || e.Origin != "User")
                    throw new InvalidStateTransitionException("run mode authority", "incomplete revocation",
                        payload.Type().ToString());
                RequireNonTerminalRun(e.RunId, payload);
                break;
            case RunReasoningPreferenceSelected e:
                RequireNonTerminalRun(e.RunId, payload);
                if (string.IsNullOrWhiteSpace(e.CommandId) || e.Source is not ("User" or "UserDefault")
                    || (e.Source == "UserDefault"
                        ? e.CommandId != "RunCreated" || e.UserPreferenceRevision is not > 0 || e.Revision != 1
                        : e.CommandId == "RunCreated" || e.UserPreferenceRevision is not null))
                    throw new InvalidStateTransitionException("run reasoning preference", "invalid selection provenance",
                        payload.Type().ToString());
                var priorReasoningRevision = _runReasoningSelections.TryGetValue(e.RunId, out var previousSelection)
                    ? previousSelection.Revision : 0;
                if (e.Revision != priorReasoningRevision + 1)
                    throw new InvalidStateTransitionException("run reasoning preference", "revision mismatch",
                        payload.Type().ToString());
                ValidateReasoningRequest(e.Request, payload);
                var hasCapturedDefault = e.Source == "UserDefault"
                    || previousSelection?.HasCapturedUserDefault == true;
                var capturedDefaultRequest = e.Source == "UserDefault"
                    ? e.Request : previousSelection?.CapturedUserDefault;
                var capturedDefaultRevision = e.Source == "UserDefault"
                    ? e.UserPreferenceRevision : previousSelection?.CapturedUserPreferenceRevision;
                _runReasoningSelections[e.RunId] = new RunReasoningSelectionState(e.Revision, true,
                    e.Request, e.Source, e.UserPreferenceRevision, hasCapturedDefault,
                    capturedDefaultRequest, capturedDefaultRevision);
                break;
            case RunReasoningPreferenceRevoked e:
                RequireNonTerminalRun(e.RunId, payload);
                if (string.IsNullOrWhiteSpace(e.CommandId) || e.Origin != "User"
                    || !_runReasoningSelections.TryGetValue(e.RunId, out var currentSelection)
                    || !currentSelection.HasSelection || e.Revision != currentSelection.Revision + 1)
                    throw new InvalidStateTransitionException("run reasoning preference", "invalid revocation",
                        payload.Type().ToString());
                _runReasoningSelections[e.RunId] = new RunReasoningSelectionState(e.Revision, false, null, "User", null,
                    currentSelection.HasCapturedUserDefault, currentSelection.CapturedUserDefault,
                    currentSelection.CapturedUserPreferenceRevision);
                break;

            // ── Task ──
            case TaskCreated created:
                RequireNonTerminalRun(created.RunId, payload);
                Create(_tasks, created.TaskId, TaskState.Pending, "task", payload);
                _taskRuns[created.TaskId] = created.RunId;
                break;
            case TaskReady e: Transition(_tasks, e.TaskId, "task", payload, StateMachines.ApplyTask); break;
            case TaskStarted e: Transition(_tasks, e.TaskId, "task", payload, StateMachines.ApplyTask); break;
            case TaskBlocked e: Transition(_tasks, e.TaskId, "task", payload, StateMachines.ApplyTask); break;
            case TaskUnblocked e: Transition(_tasks, e.TaskId, "task", payload, StateMachines.ApplyTask); break;
            case TaskCompleted e: Transition(_tasks, e.TaskId, "task", payload, StateMachines.ApplyTask); break;
            case TaskFailed e: Transition(_tasks, e.TaskId, "task", payload, StateMachines.ApplyTask); break;
            case TaskSkipped e: Transition(_tasks, e.TaskId, "task", payload, StateMachines.ApplyTask); break;
            case TaskCancelled e: Transition(_tasks, e.TaskId, "task", payload, StateMachines.ApplyTask); break;

            // ── Lane ──
            case LaneCreated created:
                Require(_tasks, created.TaskId, "task", payload);
                if ((created.AgentProfileRevision is null) != (created.AgentProfileHash is null)
                    || created.AgentProfileRevision is < 1)
                    throw new InvalidStateTransitionException("lane", "incomplete AgentProfile binding",
                        payload.Type().ToString());
                Create(_lanes, created.LaneId, LaneState.Queued, "lane", payload);
                _laneTasks[created.LaneId] = created.TaskId;
                break;
            case LaneProvisioning e: Transition(_lanes, e.LaneId, "lane", payload, StateMachines.ApplyLane); break;
            case LaneWorktreeCreated e: ApplyLaneWorktreeCreated(e, payload); break;
            case LaneWorktreeBound e: ApplyLaneWorktreeBound(e, payload); break;
            case LaneStarted e: Transition(_lanes, e.LaneId, "lane", payload, StateMachines.ApplyLane); break;
            case LaneBlocked e: Transition(_lanes, e.LaneId, "lane", payload, StateMachines.ApplyLane); break;
            case LaneUnblocked e: Transition(_lanes, e.LaneId, "lane", payload, StateMachines.ApplyLane); break;
            case LaneCompleted e: Transition(_lanes, e.LaneId, "lane", payload, StateMachines.ApplyLane); break;
            case LaneFailed e: Transition(_lanes, e.LaneId, "lane", payload, StateMachines.ApplyLane); break;
            case LaneCancelled e: Transition(_lanes, e.LaneId, "lane", payload, StateMachines.ApplyLane); break;

            // ── Turn ──
            case TurnStarted started:
                if (started.InstructionSnapshot is { } instructionSnapshot)
                {
                    try { instructionSnapshot.Validate(); }
                    catch (ArgumentException)
                    { throw new InvalidStateTransitionException("turn", "invalid instruction snapshot", payload.Type().ToString()); }
                }
                if (started.ReasoningResolution is { } turnReasoning)
                    ValidateReasoningResolution(turnReasoning, payload);
                Create(_turns, started.TurnId, TurnState.Started, "turn", payload);
                _turnLanes[started.TurnId] = started.LaneId;
                _turnReasoningResolutions[started.TurnId] = started.ReasoningResolution;
                break;
            case ModelStepStarted stepStarted:
                if (stepStarted.StepIndex < 0)
                    throw new InvalidStateTransitionException("model step", "negative step index", payload.Type().ToString());
                if (stepStarted.ReasoningResolution is { } stepReasoning)
                {
                    ValidateReasoningResolution(stepReasoning, payload);
                    if (!_turnReasoningResolutions.TryGetValue(stepStarted.TurnId, out var turnResolution)
                        || turnResolution is null || !turnResolution.IsEquivalentTo(stepReasoning)
                        || stepReasoning.AppliedRequest?.Kind != stepStarted.ReasoningKind
                        || stepReasoning.AppliedRequest?.BudgetTokens != stepStarted.ReasoningBudgetTokens)
                        throw new InvalidStateTransitionException("model step reasoning", "does not match the durable Turn resolution",
                            payload.Type().ToString());
                }
                else if (_turnReasoningResolutions.TryGetValue(stepStarted.TurnId, out var expectedResolution)
                    && expectedResolution is not null)
                    throw new InvalidStateTransitionException("model step reasoning", "durable resolution is missing",
                        payload.Type().ToString());
                break;
            case ModelStepCompleted stepCompleted:
                ValidateGenerationAttempts(stepCompleted.GenerationAttempts, payload);
                break;
            case MetaModelInvocationCompleted metaCompleted:
                ValidateGenerationAttempts(metaCompleted.GenerationAttempts, payload);
                break;
            case MetaModelInvocationFailed metaFailed:
                ValidateGenerationAttempts(metaFailed.GenerationAttempts, payload);
                break;
            case ModelCompleted e: Transition(_turns, e.TurnId, "turn", payload, StateMachines.ApplyTurn); break;
            case TurnCompleted e: Transition(_turns, e.TurnId, "turn", payload, StateMachines.ApplyTurn); break;
            case TurnInterrupted e: Transition(_turns, e.TurnId, "turn", payload, StateMachines.ApplyTurn); break;
            case TurnAbandoned e: Transition(_turns, e.TurnId, "turn", payload, StateMachines.ApplyTurn); break;

            case TurnSteeringReceived received:
                RequireOpenSteeringDestination(received.RunId, received.LaneId, received.TurnId, payload);
                if (_steering.ContainsKey(received.SteeringId))
                    throw new InvalidStateTransitionException("steering", "duplicate identity", payload.Type().ToString());
                _steering.Add(received.SteeringId, new(received.RunId, received.LaneId, received.TurnId, "Pending"));
                break;
            case TurnSteeringApplied applied:
                var toApply = RequirePendingSteering(applied.SteeringId, applied.RunId, applied.LaneId, applied.TurnId, payload);
                RequireOpenSteeringDestination(applied.RunId, applied.LaneId, applied.TurnId, payload);
                if (applied.StepIndex < 0)
                    throw new InvalidStateTransitionException("steering", "negative step index", payload.Type().ToString());
                _steering[applied.SteeringId] = toApply with { State = "Applied" };
                break;
            case TurnSteeringDropped dropped:
                var toDrop = RequirePendingSteering(dropped.SteeringId, dropped.RunId, dropped.LaneId, dropped.TurnId, payload);
                if (string.IsNullOrWhiteSpace(dropped.Reason))
                    throw new InvalidStateTransitionException("steering", "empty drop reason", payload.Type().ToString());
                // Drop is legal during cancellation after the Run/Lane/Turn terminal event.
                _steering[dropped.SteeringId] = toDrop with { State = "Dropped" };
                break;

            // ── ToolCall (ADR-0004 §2, ADR-0036 §5) ──
            case PostEditValidationPending e:
                RequireNonTerminalRun(e.RunId, payload);
                break;
            case PostEditValidationConsumed e:
                RequireNonTerminalRun(e.RunId, payload);
                if (Require(_runs, e.RunId, "run", payload) != RunState.Validating
                    || e.Gate is not ("build" or "test"))
                    throw new InvalidStateTransitionException("run", "invalid validation evidence", payload.Type().ToString());
                break;
            case ToolCallRequested requested:
                Create(_toolCalls, requested.ToolCallId, ToolCallState.Requested, "toolcall", payload);
                break;
            case ToolCallPrepared e: ToolCallTransition(e.ToolCallId, payload); break;
            case ToolCallRejected e: ToolCallTransition(e.ToolCallId, payload); break;
            case PermissionEvaluated e: ToolCallTransition(e.ToolCallId, payload); break;
            case PermissionRequested e: ToolCallTransition(e.ToolCallId, payload); break;
            case PermissionGranted e: ToolCallTransition(e.ToolCallId, payload); break;
            case PermissionDenied e: ToolCallTransition(e.ToolCallId, payload); break;
            case ToolCallAuthorized e: ToolCallTransition(e.ToolCallId, payload); break;
            case ToolCallStarted e: ToolCallTransition(e.ToolCallId, payload); break;
            case ToolCallSucceeded e: ToolCallTransition(e.ToolCallId, payload); break;
            case ToolCallFailed e: ToolCallTransition(e.ToolCallId, payload); break;
            case ToolCallEffectUnknown e: ToolCallTransition(e.ToolCallId, payload); break;
            case ToolCallReconciled e: ApplyToolCallReconciled(e, payload); break;
            case ToolCallCancelled e: ToolCallTransition(e.ToolCallId, payload); break;
            case WorktreeIntegrationStateRecorded e: ApplyWorktreeIntegration(e, payload); break;

            // ── PlanItem (ADR-0036 §4) ──
            case PlanCreated created:
                RequireNonTerminalRun(created.RunId, payload);
                Create(_planItems, created.RootItemId, PlanItemState.Pending, "plan_item", payload);
                break;
            case PlanItemAdded added:
                Create(_planItems, added.PlanItemId, PlanItemState.Pending, "plan_item", payload);
                break;
            case PlanItemReady e: PlanItemTransition(e.PlanItemId, payload); break;
            case PlanItemStarted e: PlanItemTransition(e.PlanItemId, payload); break;
            case PlanItemBlocked e: PlanItemTransition(e.PlanItemId, payload); break;
            case PlanItemUnblocked e: PlanItemTransition(e.PlanItemId, payload); break;
            case PlanItemCompleted e: PlanItemTransition(e.PlanItemId, payload); break;
            case PlanItemFailed e: PlanItemTransition(e.PlanItemId, payload); break;
            case PlanItemSkipped e: PlanItemTransition(e.PlanItemId, payload); break;
            case PlanItemCancelled e: PlanItemTransition(e.PlanItemId, payload); break;
            case PlanItemReopened e: PlanItemTransition(e.PlanItemId, payload); break;
            case PlanItemUpdated e: Require(_planItems, e.PlanItemId, "plan_item", payload); break;
            case PlanItemLinked e: Require(_planItems, e.PlanItemId, "plan_item", payload); break;
            case PlanItemUnlinked e: Require(_planItems, e.PlanItemId, "plan_item", payload); break;

            // Resto (sesión, interacciones, audit, mensajes, reordenación…): no son transiciones de
            // estado canónico de una entidad.
        }
    }

    private static bool IsResumableUserInteraction((InteractionKind Kind, bool Pending, string? OptionId,
        InteractionCause? Cause) interaction) => interaction.Cause == InteractionCause.User
        && (interaction.Kind == InteractionKind.ModelRouteConsent && interaction.OptionId == "allow_route"
            || interaction.Kind == InteractionKind.WeakSandboxConsent
                && interaction.OptionId is "consent_once" or "deny"
            || interaction.Kind == InteractionKind.Permission
                && interaction.OptionId is "deny" or "allow_once" or "allow_run" or "allow_workspace");

    private static void ValidateReasoningRequest(ReasoningRequest? request, DomainEventPayload payload)
    {
        if (request is not null && (string.IsNullOrWhiteSpace(request.Kind)
            || (request.Kind == "budget" ? request.BudgetTokens is null or < 1024 : request.BudgetTokens is not null)))
            throw new InvalidStateTransitionException("run reasoning preference", "invalid request", payload.Type().ToString());
    }

    private static void ValidateReasoningResolution(ReasoningResolution resolution, DomainEventPayload payload)
    {
        try { resolution.Validate(); }
        catch (ArgumentException)
        { throw new InvalidStateTransitionException("reasoning resolution", "invalid or inconsistent provenance", payload.Type().ToString()); }
    }

    private static void ValidateGenerationAttempts(GenerationRequestAttemptEvidence? evidence,
        DomainEventPayload payload)
    {
        if (evidence is not null && !evidence.IsValid)
            throw new InvalidStateTransitionException("generation attempt evidence", "invalid observed count or bound",
                payload.Type().ToString());
    }

    private void ToolCallTransition(ToolCallId id, DomainEventPayload payload) =>
        Transition(_toolCalls, id, "toolcall", payload, StateMachines.ApplyToolCall);

    private void RequireOpenSteeringDestination(RunId run, LaneId lane, TurnId turn, DomainEventPayload payload)
    {
        RequireNonTerminalRun(run, payload);
        var laneState = Require(_lanes, lane, "lane", payload);
        var turnState = Require(_turns, turn, "turn", payload);
        if (StateMachines.IsLaneTerminal(laneState) || turnState is not (TurnState.Started or TurnState.ModelCompleted)
            || !_turnLanes.TryGetValue(turn, out var turnLane) || turnLane != lane
            || !_laneTasks.TryGetValue(lane, out var task) || !_taskRuns.TryGetValue(task, out var taskRun) || taskRun != run)
            throw new InvalidStateTransitionException("steering", "destination closed or out of scope", payload.Type().ToString());
    }

    private SteeringEntry RequirePendingSteering(SteeringId id, RunId run, LaneId lane, TurnId turn,
        DomainEventPayload payload)
    {
        var item = Require(_steering, id, "steering", payload);
        if (item.State != "Pending" || item.Run != run || item.Lane != lane || item.Turn != turn)
            throw new InvalidStateTransitionException("steering", "not pending or different scope", payload.Type().ToString());
        return item;
    }

    private void ApplyToolCallReconciled(ToolCallReconciled reconciled, DomainEventPayload payload)
    {
        var currentState = Require(_toolCalls, reconciled.ToolCallId, "toolcall", payload);
        if (currentState == ToolCallState.Reconciled
            && (!_toolCallReconciliationOutcomes.TryGetValue(reconciled.ToolCallId, out var priorOutcome)
                || priorOutcome is not (ReconciliationOutcome.Conflict or ReconciliationOutcome.Unresolvable)
                || reconciled.Cause != InteractionCause.User))
        {
            throw new InvalidStateTransitionException("toolcall", currentState, payload.Type().ToString());
        }

        var next = StateMachines.ApplyToolCall(currentState, payload);
        _toolCalls[reconciled.ToolCallId] = next;
        _toolCallReconciliationOutcomes[reconciled.ToolCallId] = reconciled.Outcome;
    }

    private void ApplyWorktreeIntegration(WorktreeIntegrationStateRecorded recorded, DomainEventPayload payload)
    {
        var callState = Require(_toolCalls, recorded.ToolCallId, "toolcall", payload);
        if (!Guid.TryParseExact(recorded.OwnershipId, "N", out _)
            || string.IsNullOrEmpty(recorded.ProposalId) || recorded.ProposalId.Length != 64
            || recorded.ProposalId.Any(character => !Uri.IsHexDigit(character))
            || recorded.Files is null || recorded.Files.Count is < 1 or > 64
            || recorded.Files.Any(file => file is null || !IsSafeIntegrationPath(file.RelativePath))
            || recorded.Files.Select(file => file.RelativePath).Distinct(StringComparer.Ordinal).Count() != recorded.Files.Count)
            throw new InvalidStateTransitionException("worktree integration", "invalid identity or file set",
                payload.Type().ToString());

        if (recorded.State == WorktreeIntegrationState.Started)
        {
            if (callState != ToolCallState.Authorized || _worktreeIntegrations.ContainsKey(recorded.ToolCallId)
                || recorded.Files.Any(file => file.Outcome != ReconciliationOutcome.NotApplied))
                throw new InvalidStateTransitionException("worktree integration", "invalid start", payload.Type().ToString());
            _worktreeIntegrations.Add(recorded.ToolCallId,
                new(recorded.OwnershipId, recorded.ProposalId, recorded.State,
                    recorded.Files.Select(file => file.RelativePath).ToArray()));
            return;
        }

        if (!_worktreeIntegrations.TryGetValue(recorded.ToolCallId, out var existing)
            || existing.OwnershipId != recorded.OwnershipId || existing.ProposalId != recorded.ProposalId
            || !existing.Paths.ToHashSet(StringComparer.Ordinal)
                .SetEquals(recorded.Files.Select(file => file.RelativePath))
            || callState is not (ToolCallState.Started or ToolCallState.EffectUnknown or ToolCallState.Reconciled
                or ToolCallState.Failed))
            throw new InvalidStateTransitionException("worktree integration", "terminal state without matching start",
                payload.Type().ToString());
        if (existing.State != WorktreeIntegrationState.Started && existing.State != recorded.State)
        {
            var expectedToolOutcome = recorded.State switch
            {
                WorktreeIntegrationState.Completed => ReconciliationOutcome.Applied,
                WorktreeIntegrationState.NotApplied => ReconciliationOutcome.NotApplied,
                _ => (ReconciliationOutcome?)null,
            };
            if (existing.State != WorktreeIntegrationState.Conflict || callState != ToolCallState.Reconciled
                || expectedToolOutcome is null
                || !_toolCallReconciliationOutcomes.TryGetValue(recorded.ToolCallId, out var reconciledOutcome)
                || reconciledOutcome != expectedToolOutcome)
                throw new InvalidStateTransitionException("worktree integration", existing.State, payload.Type().ToString());
        }
        if (recorded.State == WorktreeIntegrationState.Completed
            && recorded.Files.Any(file => file.Outcome != ReconciliationOutcome.Applied)
            || recorded.State == WorktreeIntegrationState.NotApplied
                && recorded.Files.Any(file => file.Outcome != ReconciliationOutcome.NotApplied))
            throw new InvalidStateTransitionException("worktree integration", "file outcomes disagree with terminal state",
                payload.Type().ToString());
        _worktreeIntegrations[recorded.ToolCallId] = existing with { State = recorded.State };
    }

    private void ApplyLaneWorktreeCreated(LaneWorktreeCreated created, DomainEventPayload payload)
    {
        var laneState = Require(_lanes, created.LaneId, "lane", payload);
        var laneTask = Require(_laneTasks, created.LaneId, "lane task", payload);
        if (laneState != LaneState.Provisioning || laneTask != created.TaskId
            || created.ExecutionId.Value == Guid.Empty
            || !Guid.TryParseExact(created.OwnershipId, "N", out _)
            || !IsAbsolutePath(created.RepositoryRoot) || !IsAbsolutePath(created.GitCommonDirectory)
            || !IsAbsolutePath(created.WorktreePath)
            || !IsFullGitObjectId(created.SnapshotCommit) || !IsFullGitObjectId(created.SnapshotTree)
            || created.TaskPacketRef is null || created.TaskPacketRef.Kind != ArtifactKind.Other
            || created.TaskPacketRef.MediaType != "application/vnd.omnicore.task-packet+json"
            || created.TaskPacketRef.Sensitivity != Sensitivity.Sensitive
            || created.TaskPacketRef.Size is <= 0 or > 1024 * 1024
            || created.SelectedProjectionRef is null || created.SelectedProjectionRef.Kind != ArtifactKind.Other
            || created.SelectedProjectionRef.MediaType != "application/vnd.omnicore.selected-context+json"
            || created.SelectedProjectionRef.Sensitivity != Sensitivity.Sensitive
            || created.SelectedProjectionRef.Size is <= 0 or > 1024 * 1024
            || _laneWorktrees.ContainsKey(created.LaneId))
            throw new InvalidStateTransitionException("lane worktree", "invalid or duplicate provisioning identity",
                payload.Type().ToString());
        _laneWorktrees.Add(created.LaneId, created);
    }

    private void ApplyLaneWorktreeBound(LaneWorktreeBound bound, DomainEventPayload payload)
    {
        var laneState = Require(_lanes, bound.LaneId, "lane", payload);
        var laneTask = Require(_laneTasks, bound.LaneId, "lane task", payload);
        var owner = _laneWorktrees.TryGetValue(bound.OwnerLaneId, out var created) ? created : null;
        if (laneState != LaneState.Provisioning || laneTask != bound.TaskId
            || bound.LaneId == bound.OwnerLaneId || bound.ExecutionId.Value == Guid.Empty
            || owner is null || owner.TaskId != bound.OwnerTaskId || owner.LaneId != bound.OwnerLaneId
            || owner.ExecutionId != bound.OwnerExecutionId || owner.OwnershipId != bound.OwnershipId
            || owner.RepositoryRoot != bound.RepositoryRoot || owner.GitCommonDirectory != bound.GitCommonDirectory
            || owner.WorktreePath != bound.WorktreePath || owner.SnapshotCommit != bound.SnapshotCommit
            || owner.SnapshotTree != bound.SnapshotTree
            || !Guid.TryParseExact(bound.OwnershipId, "N", out _)
            || !IsAbsolutePath(bound.RepositoryRoot) || !IsAbsolutePath(bound.GitCommonDirectory)
            || !IsAbsolutePath(bound.WorktreePath) || !IsFullGitObjectId(bound.SnapshotCommit)
            || !IsFullGitObjectId(bound.SnapshotTree) || !IsFullGitObjectId(bound.SourceTree)
            || !IsFullGitObjectId(bound.SourceHead) || string.IsNullOrWhiteSpace(bound.WorkflowId)
            || string.IsNullOrWhiteSpace(bound.WorkflowVersion) || string.IsNullOrWhiteSpace(bound.WorkflowInstance)
            || bound.TaskPacketRef is null || bound.TaskPacketRef.Kind != ArtifactKind.Other
            || bound.TaskPacketRef.MediaType != "application/vnd.omnicore.task-packet+json"
            || bound.TaskPacketRef.Sensitivity != Sensitivity.Sensitive
            || bound.TaskPacketRef.Size is <= 0 or > 1024 * 1024
            || bound.SelectedProjectionRef is null || bound.SelectedProjectionRef.Kind != ArtifactKind.Other
            || bound.SelectedProjectionRef.MediaType != "application/vnd.omnicore.selected-context+json"
            || bound.SelectedProjectionRef.Sensitivity != Sensitivity.Sensitive
            || bound.SelectedProjectionRef.Size is <= 0 or > 1024 * 1024
            || _laneWorktrees.ContainsKey(bound.LaneId) || _laneWorktreeBindings.ContainsKey(bound.LaneId))
            throw new InvalidStateTransitionException("lane worktree binding", "invalid, unowned, or duplicate binding",
                payload.Type().ToString());
        _laneWorktreeBindings.Add(bound.LaneId, bound);
    }

    private static bool IsAbsolutePath(string value) => !string.IsNullOrWhiteSpace(value)
        && System.IO.Path.IsPathFullyQualified(value);

    private static bool IsFullGitObjectId(string value) => value is { Length: 40 or 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSafeIntegrationPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || System.IO.Path.IsPathRooted(path)) return false;
        var segments = path.Replace('\\', '/').Split('/');
        return segments.All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }

    private void PlanItemTransition(PlanItemId id, DomainEventPayload payload) =>
        Transition(_planItems, id, "plan_item", payload, StateMachines.ApplyPlanItem);

    private void RequireNonTerminalRun(RunId id, DomainEventPayload payload)
    {
        var state = Require(_runs, id, "run", payload);
        if (StateMachines.IsRunTerminal(state))
        {
            throw new InvalidStateTransitionException("run", state, payload.Type().ToString());
        }
    }

    private static void Create<TId, TState>(Dictionary<TId, TState> map, TId id, TState initial, string entity,
        DomainEventPayload payload) where TId : notnull
    {
        if (map.TryGetValue(id, out var existing))
        {
            // Crear dos veces la misma entidad no es una transición válida.
            throw new InvalidStateTransitionException(entity, existing!, payload.Type().ToString());
        }

        map[id] = initial;
    }

    private static TState Require<TId, TState>(Dictionary<TId, TState> map, TId id, string entity,
        DomainEventPayload payload) where TId : notnull
    {
        if (!map.TryGetValue(id, out var state))
        {
            throw new InvalidStateTransitionException(entity, "inexistente", payload.Type().ToString());
        }

        return state;
    }

    private static void Transition<TId, TState>(Dictionary<TId, TState> map, TId id, string entity,
        DomainEventPayload payload, Func<TState, DomainEventPayload, TState> apply) where TId : notnull
    {
        var from = Require(map, id, entity, payload);
        map[id] = apply(from, payload);
    }
}
