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
        (RunMode From, RunMode To, string Cause)? lastExplicitModeChange = null;
        RunModeAuthority? authorityBeforeLastExplicitModeChange = null;
        long? lastExplicitModeChangeSequence = null;
        long? pendingModeTransitionSequence = null;
        CausationId? lastExplicitModeChangeCausation = null;
        CausationId? pendingModeTransitionCausation = null;

        var created = false;
        foreach (var evt in evts)
        {
            var payload = registry.Decode(evt);
            var authorityEventRunId = payload switch
            {
                RunModeChanged changedAuthority => changedAuthority.RunId,
                RunModeTransitionAuthorized authorizedAuthority => authorizedAuthority.RunId,
                RunModeAuthoritySelected selectedAuthority => selectedAuthority.Authority.RunId,
                RunModeAuthorityRevoked revokedAuthority => revokedAuthority.RunId,
                _ => (RunId?)null,
            };
            if (authorityEventRunId is { } eventRunId
                && (evt.SessionId != sessionId || evt.RunId != eventRunId || evt.CorrelationId != eventRunId))
                throw new InvalidStateTransitionException("run mode authority", "event envelope scope mismatch",
                    payload.Type().ToString());
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
                if (mode is null || changed.From != mode || !Enum.IsDefined(changed.From)
                    || !Enum.IsDefined(changed.To) || state is RunState.Completed or RunState.Failed or RunState.Cancelled)
                    throw new InvalidStateTransitionException("run mode transition", "change does not match the current Run mode",
                        payload.Type().ToString());
                authorityBeforeLastExplicitModeChange = modeAuthority;
                lastExplicitModeChange = (changed.From, changed.To, changed.Cause);
                lastExplicitModeChangeSequence = evt.Sequence;
                lastExplicitModeChangeCausation = evt.Causation;
                mode = changed.To;
                if (modeAuthority is not null) modeAuthority = modeAuthority with { Mode = changed.To };
            }
            else if (payload is RunModeTransitionAuthorized transition && transition.RunId.Equals(id))
            {
                if (string.IsNullOrWhiteSpace(transition.CommandId)
                    || transition.Origin is not ("User" or "UltraCodePolicy")
                    || pendingModeTransition is not null || modeAuthority is null
                    || transition.To != mode || transition.AuthorityRevision != modeAuthority.Revision + 1
                    || (transition.From == transition.To && transition.From != modeAuthority.Mode)
                    || (transition.From != transition.To
                        && (lastExplicitModeChange is not { } explicitChange
                            || explicitChange.From != transition.From || explicitChange.To != transition.To)))
                    throw new InvalidStateTransitionException("run mode transition", "authorization does not match the effective mode/revision",
                        payload.Type().ToString());
                if (transition.Origin == "UltraCodePolicy")
                {
                    var previous = authorityBeforeLastExplicitModeChange;
                    var previousAuthorization = previous?.Authorization;
                    if (transition.PlanCoverage is { } coverage)
                    {
                        var preceding = evts.Where(item => item.SessionId == sessionId
                            && item.RunId == id && item.Sequence < evt.Sequence).ToArray();
                        var plan = PlanProjection.Replay(registry, preceding).Latest();
                        if (transition.From != RunMode.Plan || transition.To != RunMode.Act
                            || coverage != previousAuthorization?.PlanCoverage
                            || coverage.RunId != id || coverage.RootTaskId != rootTask
                            || plan is null || coverage.PlanId != plan.Id || coverage.PlanRevision != plan.Revision)
                            throw new InvalidStateTransitionException("run mode transition", "plan coverage mismatch",
                                payload.Type().ToString());
                    }
                    if (transition.ProposalEventId is { } proposalId)
                    {
                        var source = evts.SingleOrDefault(item => item.EventId == proposalId);
                        if (source is null || source.SessionId != sessionId || source.RunId != id
                            || source.Sequence >= evt.Sequence
                            || registry.Decode(source) is not RunModeProposed proposal
                            || proposal.RunId != id || proposal.From != transition.From || proposal.To != transition.To
                            || proposal.AuthorityRevision != previous?.Revision
                            || !evts.Any(item => item.SessionId == sessionId && item.RunId == id
                                && item.Sequence > source.Sequence && item.Sequence < evt.Sequence
                                && registry.Decode(item) is TurnCompleted completed && completed.TurnId == proposal.TurnId))
                            throw new InvalidStateTransitionException("run mode transition", "proposal receipt mismatch",
                                payload.Type().ToString());
                    }
                    var policyFailure = string.Join(", ", new string?[]
                    {
                        transition.From == transition.To ? "same-mode transition" : null,
                        lastExplicitModeChange is not { } policyChange ? "missing preceding mode change" : null,
                        lastExplicitModeChange is { } causeChange && causeChange.Cause != transition.Reason
                            ? "reason mismatch" : null,
                        lastExplicitModeChangeSequence is not { } changedSequence
                            || evt.Sequence != changedSequence + 1 ? "non-adjacent transition" : null,
                        previous is null ? "missing previous authority" : null,
                        evt.Causation is not CommandCausation transitionCause
                            || transitionCause.CommandId.ToString() != transition.CommandId ? "command mismatch" : null,
                        !SameCommandCause(lastExplicitModeChangeCausation, evt.Causation) ? "causation mismatch" : null,
                        previousAuthorization is null ? "missing previous authorization" : null,
                        previous is { ProductEffort: not ProductEffort.UltraCode } ? "not UltraCode" : null,
                        previous?.ModePinned == true ? "authority pinned" : null,
                        previous?.AutoModeSwitch != true ? "auto-switch disabled" : null,
                        previousAuthorization is not null && previous is not null
                            && previousAuthorization.AuthorityRevision != previous.Revision ? "authority revision mismatch" : null,
                        previousAuthorization is not null
                            && previousAuthorization.AuthorizationId != transition.AuthorizationId ? "authorization id mismatch" : null,
                        previousAuthorization is not null
                            && previousAuthorization.ObjectiveRevision != transition.ObjectiveRevision ? "objective revision mismatch" : null,
                        previousAuthorization is not null
                            && previousAuthorization.ObjectiveDigest != transition.ObjectiveDigest ? "objective digest mismatch" : null,
                        previousAuthorization is not null
                            && previousAuthorization.PolicyRevision != transition.PolicyRevision ? "policy revision mismatch" : null,
                        previousAuthorization is not null
                            && !previousAuthorization.AllowedModes.Contains(transition.To) ? "target not allowlisted" : null,
                        previousAuthorization is not null && previous is not null
                            && transition.AuthorityRevision != previous.Revision + 1 ? "next revision mismatch" : null,
                        previousAuthorization?.IsExpiredAt(evt.Timestamp) == true ? "grant expired at transition" : null,
                    }.Where(reason => reason is not null));
                    if (policyFailure.Length > 0)
                        throw new InvalidStateTransitionException("run mode transition",
                            "UltraCode policy transition lacks matching durable authority: " + policyFailure,
                            payload.Type().ToString());
                    pendingModeTransitionSequence = evt.Sequence;
                    pendingModeTransitionCausation = evt.Causation;
                }
                pendingModeTransition = transition;
                lastExplicitModeChange = null;
                lastExplicitModeChangeSequence = null;
            }
            else if (payload is RunModeAuthoritySelected selected
                && selected.Authority.RunId.Equals(id))
            {
                if (string.IsNullOrWhiteSpace(selected.CommandId)
                    || selected.Origin is not ("RunCreated" or "User" or "PlanApproval" or "UltraCodePolicy"))
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
                    || selected.Origin is not ("User" or "PlanApproval" or "UltraCodePolicy")
                    || (pendingModeTransition.Origin == "UltraCodePolicy") != (selected.Origin == "UltraCodePolicy")
                    || pendingModeTransition.CommandId != selected.CommandId
                    || pendingModeTransition.To != selected.Authority.Mode
                    || pendingModeTransition.AuthorityRevision != selected.Authority.Revision
                    || pendingModeTransition.ObjectiveRevision != selected.Authority.ObjectiveRevision
                    || pendingModeTransition.ObjectiveDigest != selected.Authority.ObjectiveDigest
                    || pendingModeTransition.PolicyRevision != selected.Authority.PolicyRevision
                    || pendingModeTransition.AuthorizationId != selected.Authority.Authorization?.AuthorizationId)
                    throw new InvalidStateTransitionException("run mode authority", "selection does not match its transition authorization",
                        payload.Type().ToString());
                if (pendingModeTransition?.Origin == "UltraCodePolicy")
                {
                    var previous = authorityBeforeLastExplicitModeChange
                        ?? throw new InvalidStateTransitionException("run mode authority", "missing prior UltraCode authority",
                            payload.Type().ToString());
                    var previousAuthorization = previous.Authorization
                        ?? throw new InvalidStateTransitionException("run mode authority", "missing prior UltraCode authorization",
                            payload.Type().ToString());
                    var nextAuthorization = selected.Authority.Authorization;
                    if (selected.Origin != "UltraCodePolicy"
                        || pendingModeTransitionSequence is not { } transitionSequence
                        || evt.Sequence != transitionSequence + 1
                        || evt.Causation is not CommandCausation selectedCause
                        || selectedCause.CommandId.ToString() != selected.CommandId
                        || !SameCommandCause(pendingModeTransitionCausation, evt.Causation)
                        || selected.Authority.ProductEffort != ProductEffort.UltraCode
                        || !selected.Authority.AutoModeSwitch || selected.Authority.ModePinned
                        || selected.Authority.Strategy != previous.Strategy
                        || nextAuthorization is null
                        || nextAuthorization.AuthorizationId != previousAuthorization.AuthorizationId
                        || nextAuthorization.AuthorityRevision != selected.Authority.Revision
                        || nextAuthorization.ObjectiveRevision != previousAuthorization.ObjectiveRevision
                        || nextAuthorization.ObjectiveDigest != previousAuthorization.ObjectiveDigest
                        || nextAuthorization.PolicyRevision != previousAuthorization.PolicyRevision
                        || !nextAuthorization.AllowedModes.SequenceEqual(previousAuthorization.AllowedModes)
                        || nextAuthorization.Limits != previousAuthorization.Limits
                        || nextAuthorization.GrantedAtUtc != previousAuthorization.GrantedAtUtc
                        || nextAuthorization.PlanCoverage != previousAuthorization.PlanCoverage
                        || selected.Authority.Revision != previous.Revision + 1
                        || selected.Authority.ObjectiveRevision != previous.ObjectiveRevision
                        || selected.Authority.ObjectiveDigest != previous.ObjectiveDigest
                        || selected.Authority.PolicyRevision != previous.PolicyRevision)
                        throw new InvalidStateTransitionException("run mode authority", "UltraCode selection changed the retained grant",
                            payload.Type().ToString());
                }
                modeAuthority = selected.Authority;
                mode = selected.Authority.Mode;
                strategy = selected.Authority.Strategy;
                pendingModeTransition = null;
                authorityBeforeLastExplicitModeChange = null;
                pendingModeTransitionSequence = null;
                pendingModeTransitionCausation = null;
                lastExplicitModeChangeCausation = null;
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
            || registry.Decode(requestEvent) is not InteractionRequested request)
            return false;

        if (request.Kind == InteractionKind.WeakSandboxConsent)
            return ValidWeakSandboxConsentResume(resumed, envelope, resumeCause, requestEvent, request, registry, events);
        if (request.Kind == InteractionKind.Permission)
            return ValidNativeWorktreePermissionResume(resumed, envelope, resumeCause, requestEvent, request,
                    registry, events)
                || ValidNativeProcessControlPermissionResume(resumed, envelope, resumeCause, requestEvent,
                    request, registry, events);
        if (request.Kind != InteractionKind.ModelRouteConsent)
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

    internal static bool ValidNativeWorktreePermissionResume(RunInteractionResumed resumed,
        DomainEvent resumeEnvelope, CommandCausation resumeCause, DomainEvent interactionEnvelope,
        InteractionRequested interaction, IEventCodecRegistry registry, IReadOnlyList<DomainEvent> events)
    {
        if (interaction.Kind != InteractionKind.Permission
            || interactionEnvelope.SessionId != resumeEnvelope.SessionId
            || interactionEnvelope.RunId != resumed.RunId
            || interactionEnvelope.TaskId is not { } taskId
            || interactionEnvelope.LaneId is not { } laneId
            || interactionEnvelope.TurnId is not { } turnId
            || interactionEnvelope.ExecutionId is not { } executionId
            || resumeEnvelope.TaskId != taskId || resumeEnvelope.LaneId != laneId
            || resumeEnvelope.TurnId != turnId
            || resumeEnvelope.ExecutionId != executionId
            || !OptionIds(interaction.OptionsJson).ToHashSet(StringComparer.Ordinal)
                .IsSubsetOf(["deny", "allow_once", "allow_run", "allow_workspace"])
            || !OptionIds(interaction.OptionsJson).Contains("deny", StringComparer.Ordinal)
            || !OptionIds(interaction.OptionsJson).Contains("allow_once", StringComparer.Ordinal)
            || OptionIds(interaction.OptionsJson).Distinct(StringComparer.Ordinal).Count()
                != OptionIds(interaction.OptionsJson).Count())
            return false;

        var scoped = events.Where(item => item.SessionId == interactionEnvelope.SessionId
            && item.Sequence < resumeEnvelope.Sequence && item.RunId == resumed.RunId
            && item.TaskId == taskId && item.LaneId == laneId && item.TurnId == turnId
            && item.ExecutionId == executionId).ToArray();
        var permissionRequests = scoped.Where(item => item.Sequence < interactionEnvelope.Sequence
            && registry.Decode(item) is PermissionRequested permission
            && permission.InteractionId == resumed.InteractionId).ToArray();
        if (permissionRequests.Length != 1
            || registry.Decode(permissionRequests[0]) is not PermissionRequested permissionRequest
            || permissionRequest.ToolCallId.Value == Guid.Empty
            || permissionRequests[0].ToolCallId is { } permissionEnvelopeCall
                && permissionEnvelopeCall != permissionRequest.ToolCallId
            || interactionEnvelope.ToolCallId is { } interactionEnvelopeCall
                && interactionEnvelopeCall != permissionRequest.ToolCallId
            || resumeEnvelope.ToolCallId != permissionRequest.ToolCallId)
            return false;
        var toolCallId = permissionRequest.ToolCallId;
        var calls = scoped.Where(item => item.ToolCallId == toolCallId
            && registry.Decode(item) is ToolCallRequested call
            && call.ToolCallId == toolCallId
            && call.ToolName is ("worktree.integrate" or "worktree.cleanup" or "worktree.discard")
            && call.ProviderCallId.StartsWith("native-action-", StringComparison.Ordinal)).ToArray();
        if (calls.Length != 1
            || registry.Decode(calls[0]) is not ToolCallRequested toolCall
            || !Guid.TryParseExact(toolCall.ProviderCallId["native-action-".Length..], "D", out var invocationGuid)
            || toolCall.ProviderCallId != "native-action-" + invocationGuid.ToString("D"))
            return false;
        var canonicalCommandId = toolCall.ToolName switch
        {
            "worktree.integrate" => "core:worktree.integrate",
            "worktree.cleanup" => "core:worktree.cleanup",
            "worktree.discard" => "core:worktree.discard",
            _ => null,
        };
        if (canonicalCommandId is null) return false;
        var preparedCalls = scoped.Where(item => item.ToolCallId == toolCallId
            && registry.Decode(item) is ToolCallPrepared prepared && prepared.ToolCallId == toolCallId).ToArray();
        if (preparedCalls.Length != 1 || preparedCalls[0].Sequence <= calls[0].Sequence
            || preparedCalls[0].Sequence >= permissionRequests[0].Sequence)
            return false;

        var invocation = new CommandId(invocationGuid);
        var admissions = events.Where(item => item.SessionId == interactionEnvelope.SessionId
            && item.Sequence < calls[0].Sequence && item.RunId == resumed.RunId
            && registry.Decode(item) is NativeCommandInvocationAdmitted admitted
            && admitted.InvocationId == invocation && admitted.SessionId == interactionEnvelope.SessionId
            && admitted.RunId == resumed.RunId && admitted.Kind == "nativeTool"
            && admitted.CanonicalCommandId == canonicalCommandId).ToArray();
        var starts = events.Where(item => item.SessionId == interactionEnvelope.SessionId
            && item.Sequence < calls[0].Sequence && item.RunId == resumed.RunId
            && registry.Decode(item) is NativeCommandInvocationStarted started
            && started.InvocationId == invocation && started.Kind == "nativeTool"
            && started.RunId == resumed.RunId).ToArray();
        if (admissions.Length != 1 || starts.Length != 1 || starts[0].Sequence <= admissions[0].Sequence
            || calls[0].Sequence <= starts[0].Sequence || permissionRequests[0].Sequence <= preparedCalls[0].Sequence
            || permissionRequests[0].Sequence >= interactionEnvelope.Sequence)
            return false;

        var resolutions = events.Where(item => item.SessionId == interactionEnvelope.SessionId
            && item.Sequence > interactionEnvelope.Sequence && item.Sequence < resumeEnvelope.Sequence
            && item.RunId == resumed.RunId && item.TaskId == taskId && item.LaneId == laneId
            && item.TurnId == turnId && item.ToolCallId == toolCallId && item.ExecutionId == executionId
            && registry.Decode(item) is InteractionResolved resolved
            && resolved.InteractionId == resumed.InteractionId
            && OptionIds(interaction.OptionsJson).Contains(resolved.OptionId, StringComparer.Ordinal)
            && resolved.Cause == InteractionCause.User).ToArray();
        if (resolutions.Length != 1 || resolutions[0].Causation is not CommandCausation resolutionCause
            || resolutionCause.CommandId != resumeCause.CommandId)
            return false;

        var pending = new HashSet<InteractionId>();
        foreach (var item in events.Where(item => item.Sequence < resumeEnvelope.Sequence).OrderBy(item => item.Sequence))
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

    internal static bool ValidNativeProcessControlPermissionResume(RunInteractionResumed resumed,
        DomainEvent resumeEnvelope, CommandCausation resumeCause, DomainEvent interactionEnvelope,
        InteractionRequested interaction, IEventCodecRegistry registry, IReadOnlyList<DomainEvent> events)
    {
        if (interaction.Kind != InteractionKind.Permission
            || interactionEnvelope.SessionId != resumeEnvelope.SessionId
            || interactionEnvelope.RunId != resumed.RunId
            || interactionEnvelope.TaskId is not { } taskId
            || interactionEnvelope.LaneId is not { } laneId
            || interactionEnvelope.TurnId is not { } turnId
            || interactionEnvelope.ExecutionId is not { } executionId
            || resumeEnvelope.TaskId != taskId || resumeEnvelope.LaneId != laneId
            || resumeEnvelope.TurnId != turnId || resumeEnvelope.ExecutionId != executionId
            || !OptionIds(interaction.OptionsJson).ToHashSet(StringComparer.Ordinal)
                .IsSubsetOf(["deny", "allow_once", "allow_run", "allow_workspace"])
            || !OptionIds(interaction.OptionsJson).Contains("deny", StringComparer.Ordinal)
            || !OptionIds(interaction.OptionsJson).Contains("allow_once", StringComparer.Ordinal)
            || OptionIds(interaction.OptionsJson).Distinct(StringComparer.Ordinal).Count()
                != OptionIds(interaction.OptionsJson).Count())
            return false;

        var scoped = events.Where(item => item.SessionId == interactionEnvelope.SessionId
            && item.Sequence < resumeEnvelope.Sequence && item.RunId == resumed.RunId
            && item.TaskId == taskId && item.LaneId == laneId && item.TurnId == turnId
            && item.ExecutionId == executionId).ToArray();
        var permissions = scoped.Where(item => item.Sequence < interactionEnvelope.Sequence
            && registry.Decode(item) is PermissionRequested permission
            && permission.InteractionId == resumed.InteractionId).ToArray();
        if (permissions.Length != 1 || registry.Decode(permissions[0]) is not PermissionRequested permissionRequest
            || permissionRequest.ToolCallId.Value == Guid.Empty
            || permissions[0].ToolCallId is { } permissionEnvelopeCall
                && permissionEnvelopeCall != permissionRequest.ToolCallId
            || interactionEnvelope.ToolCallId is { } interactionEnvelopeCall
                && interactionEnvelopeCall != permissionRequest.ToolCallId
            || resumeEnvelope.ToolCallId != permissionRequest.ToolCallId)
            return false;

        var toolCallId = permissionRequest.ToolCallId;
        var calls = scoped.Where(item => item.ToolCallId == toolCallId
            && registry.Decode(item) is ToolCallRequested call
            && call.ToolCallId == toolCallId
            && call.ToolName is "process.read" or "process.send_input" or "process.stop"
            && call.ProviderCallId.StartsWith("native-action-", StringComparison.Ordinal)).ToArray();
        if (calls.Length != 1 || registry.Decode(calls[0]) is not ToolCallRequested toolCall
            || !Guid.TryParseExact(toolCall.ProviderCallId["native-action-".Length..], "D", out var invocationGuid)
            || toolCall.ProviderCallId != "native-action-" + invocationGuid.ToString("D"))
            return false;

        var prepared = scoped.Where(item => item.ToolCallId == toolCallId
            && registry.Decode(item) is ToolCallPrepared call && call.ToolCallId == toolCallId).ToArray();
        if (prepared.Length != 1 || prepared[0].Sequence <= calls[0].Sequence
            || prepared[0].Sequence >= permissions[0].Sequence
            || permissions[0].Sequence >= interactionEnvelope.Sequence)
            return false;

        var invocation = new CommandId(invocationGuid);
        var admissions = events.Where(item => item.SessionId == interactionEnvelope.SessionId
            && item.Sequence < calls[0].Sequence && item.RunId == resumed.RunId
            && registry.Decode(item) is NativeCommandInvocationAdmitted admitted
            && admitted.InvocationId == invocation && admitted.SessionId == interactionEnvelope.SessionId
            && admitted.RunId == resumed.RunId && admitted.Kind == "nativeTool"
            && admitted.CanonicalCommandId == "core:process.control").ToArray();
        if (admissions.Length != 1
            || events.Any(item => item.SessionId == interactionEnvelope.SessionId
                && registry.Decode(item) is NativeCommandInvocationStarted started
                && started.InvocationId == invocation)
            || events.Any(item => item.SessionId == interactionEnvelope.SessionId
                && registry.Decode(item) is NativeCommandInvocationFinished finished
                && finished.InvocationId == invocation))
            return false;

        var resolutions = events.Where(item => item.SessionId == interactionEnvelope.SessionId
            && item.Sequence > interactionEnvelope.Sequence && item.Sequence < resumeEnvelope.Sequence
            && item.RunId == resumed.RunId && item.TaskId == taskId && item.LaneId == laneId
            && item.TurnId == turnId && item.ToolCallId == toolCallId && item.ExecutionId == executionId
            && registry.Decode(item) is InteractionResolved resolved
            && resolved.InteractionId == resumed.InteractionId
            && OptionIds(interaction.OptionsJson).Contains(resolved.OptionId, StringComparer.Ordinal)
            && resolved.Cause == InteractionCause.User).ToArray();
        if (resolutions.Length != 1 || resolutions[0].Causation is not CommandCausation resolutionCause
            || resolutionCause.CommandId != resumeCause.CommandId)
            return false;

        var pending = new HashSet<InteractionId>();
        foreach (var item in events.Where(item => item.Sequence < resumeEnvelope.Sequence).OrderBy(item => item.Sequence))
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

    private static bool ValidWeakSandboxConsentResume(RunInteractionResumed resumed, DomainEvent resumeEnvelope,
        CommandCausation resumeCause, DomainEvent requestEnvelope, InteractionRequested request,
        IEventCodecRegistry registry, IReadOnlyList<DomainEvent> events)
    {
        if (request.Kind != InteractionKind.WeakSandboxConsent
            || requestEnvelope.SessionId != resumeEnvelope.SessionId
            || requestEnvelope.RunId != resumed.RunId
            || requestEnvelope.TaskId is not { } taskId
            || requestEnvelope.LaneId is not { } laneId
            || requestEnvelope.TurnId is not { } turnId
            || requestEnvelope.ToolCallId is not { } toolCallId
            || requestEnvelope.ExecutionId is not { } executionId
            || resumeEnvelope.TaskId != taskId || resumeEnvelope.LaneId != laneId
            || resumeEnvelope.TurnId != turnId || resumeEnvelope.ToolCallId != toolCallId
            || resumeEnvelope.ExecutionId != executionId
            || !OptionIds(request.OptionsJson).SequenceEqual(["consent_once", "deny"], StringComparer.Ordinal))
            return false;

        var scopedEvents = events.Where(item => item.Sequence < resumeEnvelope.Sequence
            && item.SessionId == requestEnvelope.SessionId && item.RunId == resumed.RunId
            && item.TaskId == taskId && item.LaneId == laneId && item.TurnId == turnId
            && item.ToolCallId == toolCallId && item.ExecutionId == executionId).ToArray();
        var resolutions = scopedEvents.Where(item => item.Sequence > requestEnvelope.Sequence
            && registry.Decode(item) is InteractionResolved resolved
            && resolved.InteractionId == resumed.InteractionId && !resolved.IsQuestionnaire
            && resolved.Cause == InteractionCause.User
            && resolved.OptionId is "consent_once" or "deny").ToArray();
        if (resolutions.Length != 1) return false;
        var resolution = resolutions[0];
        if (resolution.Causation is not CommandCausation resolutionCause
            || resolutionCause.CommandId != resumeCause.CommandId)
            return false;
        var resolutionPayload = (InteractionResolved)registry.Decode(resolution);

        var beforeRequest = scopedEvents.Where(item => item.Sequence < requestEnvelope.Sequence).ToArray();
        var requestedCalls = beforeRequest.Where(item => registry.Decode(item) is ToolCallRequested call
            && call.ToolCallId == toolCallId && call.ToolName == "dyn.core.verify_integration").ToArray();
        if (requestedCalls.Length != 1 || registry.Decode(requestedCalls[0]) is not ToolCallRequested)
            return false;
        var toolRequest = requestedCalls[0];
        var prepared = beforeRequest.Where(item => registry.Decode(item) is ToolCallPrepared payload
            && payload.ToolCallId == toolCallId).ToArray();
        var authorized = beforeRequest.Where(item => registry.Decode(item) is ToolCallAuthorized payload
            && payload.ToolCallId == toolCallId).ToArray();
        if (prepared.Length != 1 || authorized.Length != 1
            || toolRequest.Sequence >= prepared[0].Sequence || prepared[0].Sequence >= authorized[0].Sequence
            || authorized[0].Sequence >= requestEnvelope.Sequence)
            return false;

        var runHistory = events.Where(item => item.Sequence < requestEnvelope.Sequence
            && item.SessionId == requestEnvelope.SessionId && item.RunId == resumed.RunId).ToArray();
        var executions = runHistory.Where(item => item.TaskId == taskId && item.LaneId == laneId
            && item.ExecutionId == executionId && registry.Decode(item) is AgentExecutionStarted started
            && started.ExecutionId == executionId && started.ParentExecutionId is not null
            && started.LaneId == laneId).ToArray();
        if (executions.Length != 1) return false;
        var startedPayload = (AgentExecutionStarted)registry.Decode(executions[0]);
        var accepted = runHistory.Where(item => registry.Decode(item) is DelegationAccepted delegation
            && delegation.ChildExecutionId == executionId && delegation.ExecutionId == startedPayload.ParentExecutionId)
            .ToArray();
        if (accepted.Length != 1 || registry.Decode(accepted[0]) is not DelegationAccepted acceptedPayload
            || accepted[0].Sequence <= executions[0].Sequence)
            return false;
        var created = runHistory.Where(item => registry.Decode(item) is DelegationCreated delegation
            && delegation.Delegation.DelegationId == acceptedPayload.DelegationId
            && delegation.Delegation.ParentExecutionId == acceptedPayload.ExecutionId
            && delegation.Delegation.ChildTaskId == taskId
            && delegation.Delegation.ChildLaneId == laneId).ToArray();
        if (created.Length != 1 || created[0].Sequence >= executions[0].Sequence
            || created[0].Sequence >= accepted[0].Sequence) return false;

        var afterRequest = scopedEvents.Where(item => item.Sequence > requestEnvelope.Sequence).ToArray();
        var terminalToolEvents = afterRequest.Where(item =>
            (registry.Decode(item) is ToolCallStarted started && started.ToolCallId == toolCallId)
            || (registry.Decode(item) is ToolCallSucceeded succeeded && succeeded.ToolCallId == toolCallId)
            || (registry.Decode(item) is ToolCallFailed failed && failed.ToolCallId == toolCallId)
            || (registry.Decode(item) is ToolCallEffectUnknown unknown && unknown.ToolCallId == toolCallId)
            || (registry.Decode(item) is ToolCallReconciled reconciled && reconciled.ToolCallId == toolCallId)).ToArray();
        if (terminalToolEvents.Length != 0) return false;
        var denials = afterRequest.Where(item => item.Sequence > resolution.Sequence
            && registry.Decode(item) is PermissionDenied denied
            && denied.ToolCallId == toolCallId).ToArray();
        if (resolutionPayload.OptionId == "deny"
            ? denials.Length != 1 || denials[0].Causation is not CommandCausation denialCause
                || denialCause.CommandId != resumeCause.CommandId
            : denials.Length != 0)
            return false;

        var pending = new HashSet<InteractionId>();
        foreach (var item in events.Where(item => item.Sequence < resumeEnvelope.Sequence).OrderBy(item => item.Sequence))
        {
            switch (registry.Decode(item))
            {
                case InteractionRequested pendingRequest: pending.Add(pendingRequest.InteractionId); break;
                case InteractionResolved resolved: pending.Remove(resolved.InteractionId); break;
                case InteractionExpired expired: pending.Remove(expired.InteractionId); break;
            }
        }
        return pending.Count == 0;
    }

    private static IReadOnlyList<string> OptionIds(string optionsJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(optionsJson);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return [];
            return document.RootElement.EnumerateArray()
                .Where(item => item.ValueKind == System.Text.Json.JsonValueKind.Object)
                .Select(item => item.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String
                    ? id.GetString() : null)
                .Where(id => id is not null).Select(id => id!).ToArray();
        }
        catch (System.Text.Json.JsonException) { return []; }
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

    private static bool SameCommandCause(CausationId? left, CausationId? right) =>
        left is CommandCausation leftCommand && right is CommandCausation rightCommand
        && leftCommand.CommandId == rightCommand.CommandId;
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
