namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Conversación ↔ Run, interrupción, cancelación y respuesta a interacciones (ADR-0035 §1, §4, §6;
/// ADR-0034). Los clientes llegan aquí solo a través de comandos (INV-011); el servicio decide
/// sobre el journal y emite los eventos canónicos, validados por <see cref="EventStream"/>.
/// </summary>
public sealed class RunControlService
{
    private readonly IEventStore _store;

    private readonly IEventCodecRegistry _codecs;
    private readonly Func<string, decimal, decimal>? _otherDailyLimit;

    public RunControlService(IEventStore store, IEventCodecRegistry codecs,
        Func<string, decimal, decimal>? otherDailyLimit = null)
    {
        _store = store;
        _codecs = codecs;
        _otherDailyLimit = otherDailyLimit;
    }

    /// <summary>Run no terminal de la sesión (hay como mucho uno, ADR-0035 §1), o null.</summary>
    public RunId? ActiveRun(SessionId session)
    {
        var events = _store.ReadFrom(session, 1);
        RunId? active = null;
        foreach (var evt in events)
        {
            if (_codecs.Decode(evt) is RunCreated created
                && !RunProjection.Replay(session, created.RunId, _codecs, events).IsTerminal())
            {
                active = created.RunId;
            }
        }

        return active;
    }

    /// <summary>
    /// ToolCalls de la sesión que bloquean un Run nuevo (ADR-0004 §5): siguen en EffectUnknown, o su
    /// reconciliación terminó en Unresolvable/Conflict (falla cerrado: bloqueada, visible; requiere
    /// resolución humana). Applied/NotApplied desbloquean.
    /// </summary>
    public IReadOnlyList<ToolCallId> UnreconciledEffects(SessionId session)
    {
        var blocking = new Dictionary<ToolCallId, bool>();
        foreach (var evt in _store.ReadFrom(session, 1))
        {
            switch (_codecs.Decode(evt))
            {
                case ToolCallEffectUnknown u: blocking[u.ToolCallId] = true; break;
                case ToolCallReconciled r when blocking.ContainsKey(r.ToolCallId):
                    blocking[r.ToolCallId] = r.Cause != InteractionCause.User
                        && (r.Outcome is ReconciliationOutcome.Unresolvable or ReconciliationOutcome.Conflict);
                    break;
                case ToolCallSucceeded s when blocking.ContainsKey(s.ToolCallId): blocking[s.ToolCallId] = false; break;
                case ToolCallFailed f when blocking.ContainsKey(f.ToolCallId): blocking[f.ToolCallId] = false; break;
            }
        }

        return blocking.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
    }

    /// <summary>
    /// Abre un Run nuevo en la sesión con su Task y Lane raíz y el Plan rev.1 de un item
    /// (ADR-0035 §2-3). Falla con <see cref="RunAlreadyActiveException"/> si ya hay uno activo.
    /// </summary>
    public RunId StartRun(SessionId session, string objective, RunMode mode)
    {
        ArgumentException.ThrowIfNullOrEmpty(objective);
        if (ActiveRun(session) is { } active)
        {
            throw new RunAlreadyActiveException(active);
        }

        // ADR-0004 §5: un efecto desconocido debe reconciliarse antes de seguir; un Run nuevo no lo pierde.
        var pending = UnreconciledEffects(session);
        if (pending.Count > 0)
        {
            throw new UnreconciledEffectException(pending, PendingEffectInteractionIds(session, pending));
        }

        var run = RunId.New();
        var task = TaskId.New();
        var lane = LaneId.New();
        var budget = new TaskBudget(null, null, null, null);
        new EventStream(_store, _codecs, session).AppendBatch(new DomainEventPayload[] {
            new RunCreated(run, session, objective, mode, ExecutionStrategy.Direct, FailurePolicy.BlockDependents,
                budget, task, DateTimeOffset.UtcNow),
            new RunStarted(run),
            new TaskCreated(task, run, objective, Array.Empty<TaskDependency>(), budget),
            new TaskReady(task),
            new LaneCreated(lane, task, ProfileId.New()),
            new LaneStarted(lane),
            new TaskStarted(task, lane),
            new PlanCreated(PlanId.New(), run, PlanItemId.New(), objective),
        }, DurabilityClass.Standard);
        return run;
    }

    /// <summary>
    /// <c>SendInput</c> (ADR-0035 §1): sin Run activo, el texto es el objetivo de un Run nuevo; con
    /// Run activo, se añade como mensaje del usuario (y reactiva la Lane raíz si esperaba input).
    /// </summary>
    public RunId SendInput(SessionId session, string text, RunMode defaultMode, string? origin = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var run = ActiveRun(session) ?? StartRun(session, text, defaultMode);
        var currentEvents = _store.ReadFrom(session, 1);
        var projection = RunProjection.Replay(session, run, _codecs, currentEvents);
        if (FollowUpQueue.TryQueue(_store, _codecs, session, run, RootLane(currentEvents, projection), text, origin))
        {
            return run;
        }
        var parts = "[" + System.Text.Json.JsonSerializer.Serialize(text, JsonStrings.Default.String) + "]";
        new EventStream(_store, _codecs, session).Append(new UserInputReceived(run, parts, null, origin));
        return run;
    }

    /// <summary>
    /// <c>Interrupt</c> (ADR-0035 §6): corta el Turn en curso, cancela las ToolCalls sin empezar y
    /// cierra las empezadas con efecto desconocido; la Lane raíz queda esperando input. La segunda
    /// interrupción (el Run ya esperaba input) cancela el Run.
    /// </summary>
    public void Interrupt(SessionId session, RunId run)
    {
        var events = _store.ReadFrom(session, 1);
        var projection = RunProjection.Replay(session, run, _codecs, events);
        RequireActive(projection, events);
        if (projection.State == RunState.AwaitingInput)
        {
            CancelRun(session, run);
            return;
        }

        var stream = new EventStream(_store, _codecs, session);
        var cut = CutInFlightWork(events, "interrupción del usuario");
        cut.AddRange(PendingSteeringDrops(session, run, events, "turn interrupted by user"));
        var rootLane = RootLane(events, projection);
        if (rootLane is not null && projection.State == RunState.Running)
        {
            cut.Add(new RunAwaitingInput(run, rootLane));
        }

        stream.AppendBatch(cut, DurabilityClass.Standard, SteeringDropScopes(cut, events));
    }

    /// <summary>
    /// <c>CancelRun</c> (ADR-0035 §6): igual que la interrupción para Turns y ToolCalls, y además
    /// cancela todas las Lanes, Tasks e interacciones pendientes; el Run termina <c>Cancelled</c>.
    /// </summary>
    public void CancelRun(SessionId session, RunId run)
    {
        var events = _store.ReadFrom(session, 1);
        var projection = RunProjection.Replay(session, run, _codecs, events);
        RequireActive(projection, events);

        var batch = CutInFlightWork(events, "run cancelado");
        batch.AddRange(PendingSteeringDrops(session, run, events, "run cancelled by user"));
        foreach (var lane in LaneProjection.Replay(_codecs, events).Lanes())
        {
            if (!StateMachines.IsLaneTerminal(lane.State) && TaskBelongsToRun(events, lane.TaskId, run))
            {
                batch.Add(new LaneCancelled(lane.Id));
            }
        }

        foreach (var task in TaskGraphProjection.Replay(_codecs, events).Tasks())
        {
            if (!StateMachines.IsTaskTerminal(task.State) && TaskBelongsToRun(events, task.Id, run))
            {
                batch.Add(new TaskCancelled(task.Id));
            }
        }

        foreach (var pending in PendingInteractions(events))
        {
            batch.Add(new InteractionExpired(pending.InteractionId));
        }

        batch.Add(new RunCancelled(run));
        new EventStream(_store, _codecs, session).AppendBatch(batch, DurabilityClass.Standard,
            SteeringDropScopes(batch, events));
    }

    private IReadOnlyList<ExecutionScopeState?> SteeringDropScopes(IReadOnlyList<DomainEventPayload> batch,
        IReadOnlyList<DomainEvent> events) => batch.Select(payload =>
        {
            if (payload is TurnSteeringDropped drop)
                return new ExecutionScopeState(drop.RunId, events.Select(_codecs.Decode).OfType<LaneCreated>()
                    .First(lane => lane.LaneId == drop.LaneId).TaskId, drop.LaneId, drop.TurnId);
            // Terminal events close historical work: never inherit a foreign caller's scope.
            var source = payload switch
            {
                ToolCallFailed failed => events.LastOrDefault(evt => _codecs.Decode(evt) is ToolCallStarted started
                    && started.ToolCallId == failed.ToolCallId),
                ToolCallCancelled cancelled => events.FirstOrDefault(evt => _codecs.Decode(evt) is ToolCallRequested requested
                    && requested.ToolCallId == cancelled.ToolCallId),
                InteractionExpired expired => events.FirstOrDefault(evt => _codecs.Decode(evt) is InteractionRequested requested
                    && requested.InteractionId == expired.InteractionId),
                TurnInterrupted interrupted => events.FirstOrDefault(evt => _codecs.Decode(evt) is TurnStarted started
                    && started.TurnId == interrupted.TurnId),
                _ => null,
            };
            return source is null ? null : new ExecutionScopeState(RunAtEvent(events, source), source.TaskId,
                source.LaneId, source.TurnId, source.ToolCallId, source.ExecutionId);
        }).ToArray();

    private IEnumerable<DomainEventPayload> PendingSteeringDrops(SessionId session, RunId run,
        IReadOnlyList<DomainEvent> events, string reason)
    {
        foreach (var scope in events.Select(_codecs.Decode).OfType<TurnSteeringReceived>()
            .Where(item => item.RunId == run).Select(item => (item.LaneId, item.TurnId)).Distinct())
        {
            var pending = SteeringQueue.Pending(_store, _codecs, session, run, scope.LaneId, scope.TurnId);
            foreach (var payload in SteeringQueue.DropEvents(pending, run, scope.LaneId, scope.TurnId, reason))
                yield return payload;
        }
    }

    /// <summary>
    /// <c>RespondToInteraction</c> (ADR-0034, INV-025): la interacción debe estar pendiente y la
    /// opción debe ser una de las que decidió el servidor. Para <c>PlanApproval</c> aplica además
    /// su efecto (ADR-0035 §4).
    /// </summary>
    public void Respond(SessionId session, InteractionId interaction, string optionId)
        => ResolveInteraction(session, interaction, optionId, InteractionCause.User, noClient: false);

    /// <summary>Aplica el default deny de ADR-0003 a una interacción presupuestaria sin cliente.</summary>
    public void ResolveBudgetWithoutClient(SessionId session, InteractionId interaction)
        => ResolveInteraction(session, interaction, "deny", InteractionCause.NoClient, noClient: true);

    public void ResolveModelRouteWithoutClient(SessionId session, InteractionId interaction)
    {
        var request = PendingInteractions(_store.ReadFrom(session, 1))
            .FirstOrDefault(request => request.InteractionId == interaction);
        if (request?.Kind != InteractionKind.ModelRouteConsent)
            throw new InvalidInteractionOptionException(interaction, "deny");
        ResolveInteraction(session, interaction, "deny", InteractionCause.NoClient, noClient: false);
    }

    private void ResolveInteraction(SessionId session, InteractionId interaction, string optionId,
        InteractionCause cause, bool noClient)
    {
        ArgumentException.ThrowIfNullOrEmpty(optionId);
        var events = _store.ReadFrom(session, 1);
        var pendingRequest = PendingInteractionEnvelopes(events)
            .FirstOrDefault(r => r.Request.InteractionId.Equals(interaction));
        var request = pendingRequest?.Request
            ?? throw new InteractionNotPendingException(interaction);
        if (noClient && (request.Kind != InteractionKind.BudgetExceeded || optionId != "deny"))
            throw new InvalidInteractionOptionException(interaction, optionId);
        if (!OptionIds(request.OptionsJson).Contains(optionId, StringComparer.Ordinal))
        {
            throw new InvalidInteractionOptionException(interaction, optionId);
        }

        if (request.Kind == InteractionKind.Question)
        {
            // Los cuestionarios validan la respuesta tipada contra su schema (ADR-0045).
            throw new InvalidInteractionOptionException(interaction, optionId);
        }

        if (request.Kind == InteractionKind.BudgetExceeded && optionId == "allow_plus")
        {
            if (cause != InteractionCause.User)
                throw new InvalidInteractionOptionException(interaction, optionId);
            var offer = BudgetContinuation.Offer(request)
                ?? throw new InvalidInteractionOptionException(interaction, optionId);
            var run = ActiveRun(session);
            if (offer.Scope == "run" && run?.ToString() != offer.RunId)
                throw new InvalidInteractionOptionException(interaction, optionId);
            var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (offer.Scope == "daily" && offer.Day != today)
                throw new InvalidInteractionOptionException(interaction, optionId);
            IEnumerable<DomainEvent> consentEvents = events;
            if (offer.Scope == "daily")
            {
                if (_store is not IWorkspaceJournalReader workspaceJournal)
                    throw new InvalidInteractionOptionException(interaction, optionId);
                consentEvents = workspaceJournal.ReadEvents(EventType.Of("interaction.requested"))
                    .Concat(workspaceJournal.ReadEvents(EventType.Of("interaction.resolved")))
                    .Concat(workspaceJournal.ReadEvents(EventType.Of("interaction.expired")));
            }
            var effectiveLimit = BudgetContinuation.Limit(consentEvents, _codecs, session, run ?? RunId.New(),
                today, offer.Scope, offer.BaselineUsd);
            if (offer.Scope == "daily" && _otherDailyLimit is not null)
                effectiveLimit = Math.Max(effectiveLimit, _otherDailyLimit(today, offer.BaselineUsd));
            if (effectiveLimit != offer.CurrentUsd)
                throw new InvalidInteractionOptionException(interaction, optionId);
        }

        var batch = new List<DomainEventPayload> { new InteractionResolved(interaction, optionId, cause) };
        if (request.Kind == InteractionKind.ModelRouteConsent && optionId == "allow_route")
        {
            if (cause != InteractionCause.User)
                throw new InvalidInteractionOptionException(interaction, optionId);
            var revised = SessionRoutingAuthorization.Resolve(events, _codecs, session, request);
            batch.Add(new SessionRoutingPolicyRevised(session, revised, interaction));
        }
        RunId? budgetDeniedRun = null;
        if (request.Kind == InteractionKind.BudgetExceeded && optionId == "deny")
        {
            budgetDeniedRun = AddBudgetDenialLifecycle(events, pendingRequest!.Envelope, request, batch);
        }
        if (request.Kind == InteractionKind.ReconciliationConflict)
        {
            var toolCallId = ToolCallIdFrom(request.ToolCallJson)
                ?? throw new InvalidInteractionOptionException(interaction, optionId);
            var lastReconciliation = events.Select(evt => _codecs.Decode(evt))
                .OfType<ToolCallReconciled>().LastOrDefault(item => item.ToolCallId == toolCallId);
            if (lastReconciliation is null || lastReconciliation.Cause == InteractionCause.User
                || lastReconciliation.Outcome is not (ReconciliationOutcome.Unresolvable or ReconciliationOutcome.Conflict))
            {
                throw new InteractionNotPendingException(interaction);
            }

            var resolvedOutcome = optionId switch
            {
                "resolution_applied" => ReconciliationOutcome.Applied,
                "resolution_not_applied" => ReconciliationOutcome.NotApplied,
                "resolution_keep_current" when lastReconciliation.Outcome == ReconciliationOutcome.Conflict
                    => ReconciliationOutcome.Conflict,
                _ => throw new InvalidInteractionOptionException(interaction, optionId),
            };
            batch.Add(new ToolCallReconciled(toolCallId, resolvedOutcome,
                "human resolution: " + resolvedOutcome, InteractionCause.User));
            if (ActiveRun(session) is { } effectRun
                && PendingInteractions(events).All(pending => pending.InteractionId == interaction))
            {
                var projection = RunProjection.Replay(session, effectRun, _codecs, events);
                if (projection.State == RunState.AwaitingInput)
                    batch.Add(new UserInputReceived(effectRun,
                        InputParts("EffectResolution: " + optionId), null, "InteractionResponse(EffectResolution)"));
            }
        }
        else if (request.Kind == InteractionKind.PlanApproval && ActiveRun(session) is { } run)
        {
            batch.AddRange(PlanApprovalEffect(events, session, run, optionId));
        }
        else if (request.Kind == InteractionKind.AcceptanceConfirmation && ActiveRun(session) is { } acceptanceRun)
        {
            var projection = RunProjection.Replay(session, acceptanceRun, _codecs, events);
            if (projection.State == RunState.AwaitingInput && RootLane(events, projection) is not null)
                batch.Add(new UserInputReceived(acceptanceRun, InputParts("AcceptanceConfirmation: " + optionId), null,
                    "InteractionResponse(AcceptanceConfirmation)"));
        }

        var stream = new EventStream(_store, _codecs, session);
        if (budgetDeniedRun is { } originRun)
        {
            batch.AddRange(PendingSteeringDrops(session, originRun, events, "budget continuation denied"));
            // Payloads without an explicit RunId (InteractionResolved/Expired and lane/task/turn
            // cancellation) must retain attribution to the request's originating Run, not the
            // latest ambient Run in the session.
            using (ExecutionScope.Begin(new ExecutionScopeState(originRun)))
                stream.AppendBatch(batch, DurabilityClass.Standard, SteeringDropScopes(batch, events));
        }
        else if (request.Kind == InteractionKind.ReconciliationConflict)
        {
            var toolCallId = ToolCallIdFrom(request.ToolCallJson)
                ?? throw new InvalidInteractionOptionException(interaction, optionId);
            var requestRun = RunAtEvent(events, pendingRequest!.Envelope)
                ?? throw new InvalidInteractionOptionException(interaction, optionId);
            var requestScope = new ExecutionScopeState(requestRun, pendingRequest.Envelope.TaskId,
                pendingRequest.Envelope.LaneId, pendingRequest.Envelope.TurnId,
                ExecutionId: pendingRequest.Envelope.ExecutionId);
            var sourceScope = SourceEffectScope(events, toolCallId)
                ?? throw new InvalidInteractionOptionException(interaction, optionId);
            var scopes = batch.Select(payload => payload is ToolCallReconciled reconciled
                    && reconciled.ToolCallId == toolCallId && reconciled.Cause == InteractionCause.User
                ? sourceScope
                : requestScope)
                .Cast<ExecutionScopeState?>().ToArray();
            stream.AppendBatch(batch, DurabilityClass.Standard, scopes);
        }
        else
        {
            stream.AppendBatch(batch, DurabilityClass.Standard);
        }
    }

    private ExecutionScopeState? SourceEffectScope(IReadOnlyList<DomainEvent> events, ToolCallId id)
    {
        var source = events.FirstOrDefault(evt => _codecs.Decode(evt) is ToolCallEffectUnknown unknown
            && unknown.ToolCallId == id)
            ?? events.FirstOrDefault(evt => _codecs.Decode(evt) is ToolCallStarted started
                && started.ToolCallId == id);
        if (source is null) return null;
        var startedEvent = events.FirstOrDefault(evt => _codecs.Decode(evt) is ToolCallStarted started
            && started.ToolCallId == id);
        var runId = RunAtEvent(events, source);
        if (runId is null) return null;
        return new ExecutionScopeState(runId, source.TaskId ?? startedEvent?.TaskId,
            source.LaneId ?? startedEvent?.LaneId, source.TurnId ?? startedEvent?.TurnId,
            id, source.ExecutionId ?? startedEvent?.ExecutionId);
    }

    private RunId? RunAtEvent(IReadOnlyList<DomainEvent> events, DomainEvent target)
    {
        // Explicit ownership remains authoritative for events appended after a newer Run.
        if (target.RunId is { } recordedRun) return recordedRun;
        RunId? current = null;
        foreach (var evt in events)
        {
            if (_codecs.Decode(evt) is RunCreated created) current = created.RunId;
            if (evt.EventId == target.EventId) return current ?? target.RunId;
        }
        return target.RunId;
    }

    private RunId AddBudgetDenialLifecycle(IReadOnlyList<DomainEvent> events, DomainEvent requestEnvelope,
        InteractionRequested request, List<DomainEventPayload> batch)
    {
        var originRun = requestEnvelope.RunId
            ?? throw new InvalidInteractionOptionException(request.InteractionId, "deny");
        var exists = events.Any(evt => _codecs.Decode(evt) is RunCreated created && created.RunId == originRun);
        if (!exists) throw new InvalidInteractionOptionException(request.InteractionId, "deny");

        var projection = RunProjection.Replay(requestEnvelope.SessionId, originRun, _codecs, events);
        if (projection.IsTerminal()
            || projection.State is not (RunState.Running or RunState.Validating or RunState.AwaitingInput))
            throw new RunNotActiveException(originRun);

        var tracker = CanonicalStateTracker.Replay(_codecs, events);
        var taskIds = events.Select(evt => _codecs.Decode(evt))
            .OfType<TaskCreated>().Where(created => created.RunId == originRun)
            .Select(created => created.TaskId).ToHashSet();

        foreach (var lane in LaneProjection.Replay(_codecs, events).Lanes())
        {
            if (taskIds.Contains(lane.TaskId) && !StateMachines.IsLaneTerminal(lane.State))
                batch.Add(new LaneCancelled(lane.Id));
        }

        foreach (var task in TaskGraphProjection.Replay(_codecs, events).Tasks())
        {
            if (taskIds.Contains(task.Id) && !StateMachines.IsTaskTerminal(task.State))
                batch.Add(new TaskCancelled(task.Id));
        }

        var planIds = new HashSet<PlanId>();
        var planItemIds = new HashSet<PlanItemId>();
        foreach (var evt in events)
        {
            switch (_codecs.Decode(evt))
            {
                case PlanCreated created when created.RunId == originRun:
                    planIds.Add(created.PlanId);
                    planItemIds.Add(created.RootItemId);
                    break;
                case PlanItemAdded added when planIds.Contains(added.PlanId):
                    planItemIds.Add(added.PlanItemId);
                    break;
            }
        }
        foreach (var planItemId in planItemIds)
        {
            if (tracker.PlanItem(planItemId) is { } state && !StateMachines.IsPlanItemTerminal(state))
                batch.Add(new PlanItemCancelled(planItemId, "BudgetExceeded"));
        }

        var turns = events.Where(evt => evt.RunId == originRun)
            .Select(evt => _codecs.Decode(evt)).OfType<TurnStarted>().Select(started => started.TurnId).ToHashSet();
        foreach (var turn in turns)
        {
            if (tracker.Turn(turn) is TurnState.Started or TurnState.ModelCompleted)
                batch.Add(new TurnInterrupted(turn));
        }

        // Requests/prepared/authorized work has not crossed the physical start barrier and is
        // safe to cancel. Started calls become EffectUnknown for recovery; already unknown calls
        // and completed outcomes remain untouched.
        var calls = events.Where(evt => evt.RunId == originRun)
            .Select(evt => _codecs.Decode(evt)).OfType<ToolCallRequested>()
            .Select(requested => requested.ToolCallId).ToHashSet();
        var startedEffectClasses = events.Where(evt => evt.RunId == originRun)
            .Select(evt => _codecs.Decode(evt)).OfType<ToolCallStarted>()
            .GroupBy(started => started.ToolCallId)
            .ToDictionary(group => group.Key, group => group.Last().EffectClass);
        foreach (var call in calls)
        {
            switch (tracker.ToolCall(call))
            {
                case ToolCallState.Requested or ToolCallState.Prepared
                    or ToolCallState.AwaitingPermission or ToolCallState.Authorized:
                    batch.Add(new ToolCallCancelled(call, "BudgetExceeded"));
                    break;
                case ToolCallState.Started when startedEffectClasses.TryGetValue(call, out var effectClass):
                    batch.Add(new ToolCallEffectUnknown(call, effectClass));
                    break;
                // EffectUnknown, succeeded, failed, and other terminal states remain unchanged.
            }
        }

        foreach (var pending in PendingInteractionEnvelopes(events))
        {
            if (pending.Request.InteractionId != request.InteractionId && pending.Envelope.RunId == originRun)
                batch.Add(new InteractionExpired(pending.Request.InteractionId));
        }

        batch.Add(new RunFailed(originRun, "BudgetExceeded"));
        return originRun;
    }

    /// <summary>Efecto de la respuesta a un PlanApproval (ADR-0035 §4).</summary>
    public IReadOnlyList<DomainEventPayload> PlanApprovalEffect(IReadOnlyList<DomainEvent> events, SessionId session,
        RunId run, string optionId)
    {
        var projection = RunProjection.Replay(session, run, _codecs, events);
        // Responder una InteractionRequest es input humano. ADR-0036 define que la única
        // transición AwaitingInput → Running es UserInputReceived; el optionId se conserva
        // como input canónico además de InteractionResolved.
        var resume = projection.State == RunState.AwaitingInput
            ? new UserInputReceived(run, InputParts("PlanApproval: " + optionId), null,
                "InteractionResponse(PlanApproval)")
            : null;
        switch (optionId)
        {
            case "approve_execute":
                return resume is null
                    ? new DomainEventPayload[] { new RunModeChanged(run, RunMode.Plan, RunMode.Act, "PlanApproved") }
                    : new DomainEventPayload[] { resume, new RunModeChanged(run, RunMode.Plan, RunMode.Act, "PlanApproved") };
            case "approve_only":
                var close = new List<DomainEventPayload>();
                if (resume is not null) close.Add(resume);
                close.Add(new RunValidationStarted(run));

                // EPIC-007: cerrar la Lane raíz pasa antes por el Lane Completion Pipeline. Si un
                // gate falla, el Run vuelve a Running con el rechazo (no existe evento de rechazo a
                // nivel de Lane): la conversación sigue abierta con su trabajo en vuelo.
                var laneMissing = new List<string>();
                if (projection.RootTask is not null)
                {
                    var lanes = LaneProjection.Replay(_codecs, events);
                    foreach (var lane in lanes.ForTask(projection.RootTask).Where(l => l.State == LaneState.Running))
                    {
                        var laneResult = new LaneCompletionPipeline().Check(_codecs, events, lane.Id);
                        if (!laneResult.Passed)
                        {
                            laneMissing.AddRange(laneResult.Missing);
                        }
                    }
                }

                if (laneMissing.Count > 0)
                {
                    close.Add(new RunValidationRejected(run, ["lane"], laneMissing));
                    return close;
                }

                close.AddRange(CloseRoot(events, projection));
                close.Add(new RunCompleted(run, RunOutcome.Planned));
                return close;
            default:
                // "Seguir planificando" (o rechazo): registra la respuesta humana, vuelve a
                // Running por UserInputReceived y deja la Lane raíz esperando de nuevo.
                var rootLane = RootLane(events, projection);
                if (rootLane is null) return Array.Empty<DomainEventPayload>();
                if (resume is not null)
                    return new DomainEventPayload[] { resume, new RunAwaitingInput(run, rootLane) };
                return projection.State == RunState.Running
                    ? new DomainEventPayload[] { new RunAwaitingInput(run, rootLane) }
                    : Array.Empty<DomainEventPayload>();
        }
    }

    // ── Auxiliares ─────────────────────────────────────────────────────────────────────────

    /// <summary>El Run debe existir en la sesión y no ser terminal (un Run inexistente tampoco está activo).</summary>
    private void RequireActive(RunProjection run, IReadOnlyList<DomainEvent> events)
    {
        var exists = events.Any(evt => _codecs.Decode(evt) is RunCreated created && created.RunId.Equals(run.Id));
        if (!exists || run.IsTerminal())
        {
            throw new RunNotActiveException(run.Id);
        }
    }

    /// <summary>Turns abiertos → TurnInterrupted; ToolCalls sin empezar → Cancelled; empezadas → Failed.</summary>
    private List<DomainEventPayload> CutInFlightWork(IReadOnlyList<DomainEvent> events, string cause)
    {
        var tracker = CanonicalStateTracker.Replay(_codecs, events);
        var batch = new List<DomainEventPayload>();
        var turns = new HashSet<TurnId>();
        var calls = new HashSet<ToolCallId>();
        foreach (var evt in events)
        {
            switch (_codecs.Decode(evt))
            {
                case TurnStarted started: turns.Add(started.TurnId); break;
                case ToolCallRequested requested: calls.Add(requested.ToolCallId); break;
            }
        }

        foreach (var call in calls)
        {
            switch (tracker.ToolCall(call))
            {
                case ToolCallState.Requested or ToolCallState.Prepared or ToolCallState.AwaitingPermission
                    or ToolCallState.Authorized:
                    batch.Add(new ToolCallCancelled(call, cause));
                    break;
                case ToolCallState.Started:
                    // Cancelación durante la ejecución: nunca Cancelled; el efecto queda por reconciliar.
                    // Código tipado CANCELLATION (spec §71) tanto si corta el usuario (Interrupt)
                    // como si cancela el Run completo (CancelRun): la ejecución se interrumpió.
                    batch.Add(new ToolCallFailed(call, cause, EffectOutcome.Unknown, ToolErrorCode.Cancellation));
                    break;
            }
        }

        foreach (var turn in turns)
        {
            if (tracker.Turn(turn) is TurnState.Started or TurnState.ModelCompleted)
            {
                batch.Add(new TurnInterrupted(turn)); // la respuesta parcial queda como interrumpida
            }
        }

        return batch;
    }

    private IEnumerable<DomainEventPayload> CloseRoot(IReadOnlyList<DomainEvent> events, RunProjection run)
    {
        if (run.RootTask is null)
        {
            yield break;
        }

        foreach (var lane in LaneProjection.Replay(_codecs, events).ForTask(run.RootTask))
        {
            if (lane.State == LaneState.Running)
            {
                yield return new LaneCompleted(lane.Id, null);
            }
        }

        if (TaskGraphProjection.Replay(_codecs, events).StateOf(run.RootTask) == TaskState.Running)
        {
            yield return new TaskCompleted(run.RootTask, null);
        }
    }

    private LaneId? RootLane(IReadOnlyList<DomainEvent> events, RunProjection run) =>
        run.RootTask is null ? null : LaneProjection.Replay(_codecs, events).ForTask(run.RootTask).FirstOrDefault()?.Id;

    private bool TaskBelongsToRun(IReadOnlyList<DomainEvent> events, TaskId task, RunId run)
    {
        foreach (var evt in events)
        {
            if (_codecs.Decode(evt) is TaskCreated created && created.TaskId.Equals(task))
            {
                return created.RunId.Equals(run);
            }
        }

        return false;
    }

    private sealed record PendingInteractionEnvelope(DomainEvent Envelope, InteractionRequested Request);

    private List<PendingInteractionEnvelope> PendingInteractionEnvelopes(IReadOnlyList<DomainEvent> events)
    {
        var pending = new Dictionary<InteractionId, PendingInteractionEnvelope>();
        foreach (var evt in events)
        {
            switch (_codecs.Decode(evt))
            {
                case InteractionRequested requested:
                    pending[requested.InteractionId] = new PendingInteractionEnvelope(evt, requested);
                    break;
                case InteractionResolved resolved: pending.Remove(resolved.InteractionId); break;
                case InteractionExpired expired: pending.Remove(expired.InteractionId); break;
            }
        }

        return pending.Values.ToList();
    }

    private List<InteractionRequested> PendingInteractions(IReadOnlyList<DomainEvent> events) =>
        PendingInteractionEnvelopes(events).Select(pending => pending.Request).ToList();

    private static string InputParts(string text) => "["
        + System.Text.Json.JsonSerializer.Serialize(text, JsonStrings.Default.String) + "]";

    private IReadOnlyList<InteractionId> PendingEffectInteractionIds(SessionId session, IReadOnlyList<ToolCallId> calls)
    {
        var callSet = calls.ToHashSet();
        return PendingInteractions(_store.ReadFrom(session, 1))
            .Where(request => request.Kind == InteractionKind.ReconciliationConflict
                && ToolCallIdFrom(request.ToolCallJson) is { } call && callSet.Contains(call))
            .Select(request => request.InteractionId).ToArray();
    }

    private static ToolCallId? ToolCallIdFrom(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("toolCallId", out var id)
                && id.GetString() is { } text ? ToolCallId.Parse(text) : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> OptionIds(string optionsJson)
    {
        var ids = new List<string>();
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(optionsJson);
            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var option in document.RootElement.EnumerateArray())
                {
                    if (option.ValueKind == System.Text.Json.JsonValueKind.Object
                        && option.TryGetProperty("id", out var id) && id.GetString() is { } text)
                    {
                        ids.Add(text);
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // opciones ilegibles: ninguna opción es válida
        }

        return ids;
    }
}

/// <summary>Contexto de serialización para strings sueltos (sin reflexión).</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class JsonStrings : System.Text.Json.Serialization.JsonSerializerContext
{
}

/// <summary>Ya hay un Run activo en la sesión (ADR-0035 §1: uno por sesión en v1).</summary>
public sealed class RunAlreadyActiveException : InvalidOperationException
{
    public RunId ActiveRun { get; }

    public RunAlreadyActiveException(RunId active) : base("ya hay un Run activo en la sesión: " + active)
    {
        ActiveRun = active;
    }
}

/// <summary>El Run no está activo (terminó o no existe).</summary>
public sealed class RunNotActiveException : InvalidOperationException
{
    public RunId Run { get; }

    public RunNotActiveException(RunId run) : base("el Run no está activo: " + run)
    {
        Run = run;
    }
}

/// <summary>La interacción no está pendiente (no existe, ya se resolvió o venció).</summary>
public sealed class InteractionNotPendingException : InvalidOperationException
{
    public InteractionId Interaction { get; }

    public InteractionNotPendingException(InteractionId interaction)
        : base("la interacción no está pendiente: " + interaction)
    {
        Interaction = interaction;
    }
}

/// <summary>La opción no es una de las que el servidor ofreció para esa interacción (ADR-0034).</summary>
public sealed class InvalidInteractionOptionException : InvalidOperationException
{
    public InteractionId Interaction { get; }

    public string OptionId { get; }

    public InvalidInteractionOptionException(InteractionId interaction, string optionId)
        : base("opción no válida para la interacción " + interaction + ": " + optionId)
    {
        Interaction = interaction;
        OptionId = optionId;
    }
}

/// <summary>Hay ToolCalls con efecto desconocido sin reconciliar: hay que reconciliarlas antes de un Run nuevo (ADR-0004 §5).</summary>
public sealed class UnreconciledEffectException : InvalidOperationException
{
    public IReadOnlyList<ToolCallId> ToolCalls { get; }

    public IReadOnlyList<InteractionId> Interactions { get; }

    public UnreconciledEffectException(IReadOnlyList<ToolCallId> toolCalls,
        IReadOnlyList<InteractionId>? interactions = null)
        : base("efectos sin resolución humana antes de un Run nuevo: " + string.Join(", ", toolCalls))
    {
        ToolCalls = toolCalls;
        Interactions = interactions ?? Array.Empty<InteractionId>();
    }
}
