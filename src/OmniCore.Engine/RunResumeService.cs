namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Reanudación segura y reutilizable de un Run tras un crash (ADR-0004 §5, ADR-0041 §2): detecta
/// en el journal las ToolCalls <c>Started</c>-sin-outcome DENTRO del Run pedido, emite
/// <c>ToolCallEffectUnknown</c> —solo si aún no se emitió— y las reconcilia contra los metadatos
/// canónicos persistidos en su propio <c>ToolCallStarted</c> (ruta + hashes pre/post; ADR-0004 §4),
/// SIN re-ejecutar el efecto. Es IDEMPOTENTE y está scoped por Run. Lo usan por igual el
/// <c>SimulationEngine</c> (resume del sim) y el Host (recuperación al arrancar/continuar el Run
/// real de Explorer), de forma que la lógica no se duplica.
///
/// Contrato de seguridad: sin reconciliador o sin metadatos → <c>Unresolvable</c> (falla cerrado,
/// nunca <c>Applied</c>). Nunca reanuda un Run terminal ni toolcalls huérfanas de otros Runs.
/// </summary>
public sealed class RunResumeService
{
    private readonly IEventStore _store;

    private readonly IEventCodecRegistry _codecs;

    private readonly IFilesystemReconciler? _fsReconciler;

    private readonly string _workspaceRoot;

    public RunResumeService(IEventStore store, IEventCodecRegistry codecs,
        IFilesystemReconciler? reconciler, string workspaceRoot)
    {
        _store = store;
        _codecs = codecs;
        _fsReconciler = reconciler;
        _workspaceRoot = workspaceRoot;
    }

    /// <summary>
    /// Reanuda el <c>runId</c> de la sesión y devuelve cuántas ToolCalls se reconciliaron.
    /// Idempotente: <c>ToolCallEffectUnknown</c> es un estado INTERMEDIO (ADR-0004 §2) y no
    /// terminal, así que un crash entre <c>EffectUnknown</c> y <c>Reconciled</c> continúa la
    /// reconciliación en el siguiente resume sin duplicar el EffectUnknown; una ToolCall ya
    /// resuelta (Succeeded/Failed/Reconciled) no se vuelve a tocar. No resuelve un Run terminal
    /// ni toolcalls de otro Run.
    /// </summary>
    public int Resume(SessionId sessionId, RunId runId) =>
        ResumeRun(sessionId, runId) + ReconcileTerminalRuns(sessionId);

    /// <summary>
    /// Reconcilia los <c>ToolCallEffectUnknown</c> sin resolver de los Runs TERMINALES de la sesión
    /// (p. ej. cancelados tras un crash), con el mismo reconciliador y resultados que en un Run
    /// activo, sin tocar el estado del Run. Sin esto quedarían pendientes para siempre (ADR-0004 §5).
    /// Idempotente: una ToolCall ya resuelta no se vuelve a tocar.
    /// </summary>
    public int ReconcileTerminalRuns(SessionId sessionId)
    {
        var tail = _store.ReadFrom(sessionId, 1);
        var starts = new List<int>();
        for (var i = 0; i < tail.Count; i++)
        {
            if (_codecs.Decode(tail[i]) is RunCreated)
            {
                starts.Add(i);
            }
        }

        // Reconciliation outcomes may be appended after later Runs were created. ToolCallId is
        // the durable identity for an outcome, so include session-wide terminal outcomes when
        // deciding whether an older Run's unknown effect is still pending.
        var resolvedAcrossSession = new HashSet<ToolCallId>();
        foreach (var evt in tail)
        {
            switch (_codecs.Decode(evt))
            {
                case ToolCallReconciled reconciled: resolvedAcrossSession.Add(reconciled.ToolCallId); break;
                case ToolCallSucceeded succeeded: resolvedAcrossSession.Add(succeeded.ToolCallId); break;
                case ToolCallFailed failed: resolvedAcrossSession.Add(failed.ToolCallId); break;
            }
        }

        var stream = new EventStream(_store, _codecs, sessionId);
        var count = 0;
        for (var k = 0; k < starts.Count; k++)
        {
            var from = starts[k];
            var to = k + 1 < starts.Count ? starts[k + 1] : tail.Count;
            var runId = ((RunCreated)_codecs.Decode(tail[from])).RunId;
            var slice = OwnEvents(tail, new[] { from, to });
            if (!RunProjection.Replay(sessionId, runId, _codecs, slice).IsTerminal())
            {
                continue;
            }

            var started = new Dictionary<ToolCallId, ToolCallStarted>();
            var startedEvents = new Dictionary<ToolCallId, DomainEvent>();
            var unknown = new Dictionary<ToolCallId, DomainEvent>();
            var resolved = new HashSet<ToolCallId>();
            foreach (var evt in slice)
            {
                switch (_codecs.Decode(evt))
                {
                    case ToolCallStarted st:
                        started[st.ToolCallId] = st;
                        startedEvents[st.ToolCallId] = evt;
                        break;
                    case ToolCallEffectUnknown u: unknown[u.ToolCallId] = evt; break;
                    case ToolCallReconciled r: resolved.Add(r.ToolCallId); break;
                    case ToolCallSucceeded su: resolved.Add(su.ToolCallId); break;
                    case ToolCallFailed f: resolved.Add(f.ToolCallId); break;
                }
            }

            foreach (var (id, origin) in unknown.Where(pair => !resolved.Contains(pair.Key)
                && !resolvedAcrossSession.Contains(pair.Key)))
            {
                var json = started.TryGetValue(id, out var st) ? st.ReconciliationJson : null;
                startedEvents.TryGetValue(id, out var startedEvent);
                // A terminal Run can be older than the session's current Run. Attribute this
                // reconciliation to its own source effect and restore only entity ids recorded
                // on that source/ToolCallStarted envelope; never inherit the stream's last Run.
                using var execution = ExecutionScope.Begin(new ExecutionScopeState(
                    RunId: runId,
                    TaskId: origin.TaskId ?? startedEvent?.TaskId,
                    LaneId: origin.LaneId ?? startedEvent?.LaneId,
                    TurnId: origin.TurnId ?? startedEvent?.TurnId,
                    ToolCallId: id,
                    ExecutionId: origin.ExecutionId ?? startedEvent?.ExecutionId));
                using var causation = CausationScope.Begin(new EventCausation(origin.EventId));
                stream.Append(ReconcileCall(id, json));
                count += 1;
            }
        }

        PublishEffectResolutionRequests(sessionId);
        return count;
    }

    /// <summary>
    /// ¿Hay algo que reconciliar con efectos laterales en la sesión? True si algún Run no terminal
    /// tiene ToolCalls <c>Started</c>-sin-outcome con <c>EffectClass</c> distinta de None, o si algún
    /// <c>ToolCallEffectUnknown</c> (de cualquier Run) sigue sin resolver. Consulta pura: no escribe.
    /// Permite al Host no exigir raíz de workspace cuando no hay nada pendiente (p. ej. tras un sim).
    /// </summary>
    public bool HasPendingSideEffects(SessionId sessionId)
    {
        var tail = _store.ReadFrom(sessionId, 1);
        var started = new Dictionary<ToolCallId, ToolCallStarted>();
        var unknown = new HashSet<ToolCallId>();
        var resolved = new HashSet<ToolCallId>();
        foreach (var evt in tail)
        {
            switch (_codecs.Decode(evt))
            {
                case ToolCallStarted st: started[st.ToolCallId] = st; break;
                case ToolCallEffectUnknown u: unknown.Add(u.ToolCallId); break;
                case ToolCallReconciled r: resolved.Add(r.ToolCallId); break;
                case ToolCallSucceeded su: resolved.Add(su.ToolCallId); break;
                case ToolCallFailed f: resolved.Add(f.ToolCallId); break;
            }
        }

        foreach (var id in unknown)
        {
            if (!resolved.Contains(id))
            {
                return true;
            }
        }

        foreach (var entry in started)
        {
            if (!resolved.Contains(entry.Key) && entry.Value.EffectClass != EffectClass.None)
            {
                return true;
            }
        }

        return false;
    }

    private void PublishEffectResolutionRequests(SessionId sessionId)
    {
        var events = _store.ReadFrom(sessionId, 1);
        var tools = new Dictionary<ToolCallId, ToolCallRequested>();
        var started = new Dictionary<ToolCallId, ToolCallStarted>();
        var outcomes = new Dictionary<ToolCallId, ToolCallReconciled>();
        var outcomeEvents = new Dictionary<ToolCallId, DomainEvent>();
        var pendingRequests = new Dictionary<InteractionId, InteractionRequested>();
        foreach (var evt in events)
        {
            switch (_codecs.Decode(evt))
            {
                case ToolCallRequested call:
                    tools[call.ToolCallId] = call;
                    break;
                case ToolCallStarted start:
                    started[start.ToolCallId] = start;
                    break;
                case ToolCallReconciled reconciled:
                    outcomes[reconciled.ToolCallId] = reconciled;
                    outcomeEvents[reconciled.ToolCallId] = evt;
                    break;
                case InteractionRequested interaction when interaction.Kind == InteractionKind.ReconciliationConflict:
                    pendingRequests[interaction.InteractionId] = interaction;
                    break;
                case InteractionResolved resolved:
                    pendingRequests.Remove(resolved.InteractionId);
                    break;
                case InteractionExpired expired:
                    pendingRequests.Remove(expired.InteractionId);
                    break;
            }
        }

        var requested = pendingRequests.Values.Select(interaction => TryGetToolCallId(interaction.ToolCallJson))
            .Where(id => id is not null).Select(id => id!).ToHashSet();
        var batch = new List<DomainEventPayload>();
        var batchScopes = new List<ExecutionScopeState?>();
        var batchCauses = new List<CausationId?>();
        var activeRunId = new RunControlService(_store, _codecs).ActiveRun(sessionId);
        var sourceScopes = new Dictionary<ToolCallId, ExecutionScopeState>();
        foreach (var pair in outcomes)
        {
            var id = pair.Key;
            var reconciliation = pair.Value;
            if (requested.Contains(id) || reconciliation.Cause == InteractionCause.User
                || reconciliation.Outcome is not (ReconciliationOutcome.Unresolvable or ReconciliationOutcome.Conflict))
            {
                continue;
            }

            tools.TryGetValue(id, out var call);
            started.TryGetValue(id, out var start);
            var target = TargetFrom(start?.ReconciliationJson);
            var detail = SafeDetail(reconciliation.Detail, target);
            var safeTarget = IsProtectedTarget(target) ? "[recurso protegido]" : new PiiRedactor().Redact(target);
            var subject = "{"
                + "\"operation\":" + JsonString("interaction.reconciliation_conflict.operation") + ","
                + "\"toolOrExecutable\":" + JsonString(call?.ToolName ?? "herramienta desconocida") + ","
                + "\"target\":" + JsonString(safeTarget) + ","
                + "\"risk\":\"Critical\","
                + "\"reason\":" + JsonString("interaction.reconciliation_conflict.reason") + ","
                + "\"details\":[{\"key\":\"reconciliation\",\"value\":" + JsonString(detail) + "}]}";
            var options = reconciliation.Outcome == ReconciliationOutcome.Conflict
                ? "[{\"id\":\"resolution_applied\",\"intent\":\"choose\"},"
                    + "{\"id\":\"resolution_not_applied\",\"intent\":\"choose\"},"
                    + "{\"id\":\"resolution_keep_current\",\"intent\":\"choose\"}]"
                : "[{\"id\":\"resolution_applied\",\"intent\":\"choose\"},"
                    + "{\"id\":\"resolution_not_applied\",\"intent\":\"choose\"}]";
            var interaction = new InteractionRequested(InteractionId.New(), InteractionKind.ReconciliationConflict,
                subject, options,
                reconciliation.Outcome == ReconciliationOutcome.Conflict
                    ? "resolution_keep_current" : "resolution_not_applied",
                null, null, null, null, 0, 1, null,
                "{\"toolCallId\":" + JsonString(id.ToString()) + "}");
            batch.Add(interaction);
            batchCauses.Add(new EventCausation(outcomeEvents[id].EventId));
            if (!sourceScopes.TryGetValue(id, out var sourceScope))
                sourceScopes[id] = sourceScope = SourceEffectScope(events, id);
            batchScopes.Add(activeRunId is { } currentRun
                ? ScopeForRun(events, currentRun)
                : sourceScope);
        }

        if (batch.Count > 0)
        {
            var activeRun = ActiveRunToAwait(sessionId, events);
            if (activeRun is not null)
            {
                batch.Add(activeRun);
                batchScopes.Add(ScopeForRun(events, activeRun.RunId) with { LaneId = activeRun.RootLaneId });
                // The first unresolved effect is sufficient to block this Run. Do not
                // invent a link to another request merely because it precedes this item.
                batchCauses.Add(CausationScope.Current ?? batchCauses[0]);
            }
            new EventStream(_store, _codecs, sessionId).AppendBatch(batch, DurabilityClass.Standard,
                batchScopes, batchCauses);
        }
    }

    private ExecutionScopeState ScopeForRun(IReadOnlyList<DomainEvent> events, RunId runId)
    {
        var created = events.FirstOrDefault(evt => _codecs.Decode(evt) is RunCreated run && run.RunId == runId);
        var payload = created is null ? null : _codecs.Decode(created) as RunCreated;
        return new ExecutionScopeState(runId, payload?.RootTask);
    }

    private ExecutionScopeState SourceEffectScope(IReadOnlyList<DomainEvent> events, ToolCallId id)
    {
        var source = events.FirstOrDefault(evt => _codecs.Decode(evt) is ToolCallEffectUnknown unknown
            && unknown.ToolCallId == id)
            ?? events.FirstOrDefault(evt => _codecs.Decode(evt) is ToolCallStarted started
                && started.ToolCallId == id);
        var start = events.FirstOrDefault(evt => _codecs.Decode(evt) is ToolCallStarted started
            && started.ToolCallId == id);
        var owner = source is null ? null : RunAtEvent(events, source);
        return new ExecutionScopeState(owner,
            source?.TaskId ?? start?.TaskId,
            source?.LaneId ?? start?.LaneId,
            source?.TurnId ?? start?.TurnId,
            id,
            source?.ExecutionId ?? start?.ExecutionId);
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

    private RunAwaitingInput? ActiveRunToAwait(SessionId session, IReadOnlyList<DomainEvent> events)
    {
        foreach (var createdEvent in events.Reverse())
        {
            if (_codecs.Decode(createdEvent) is not RunCreated created) continue;
            var projection = RunProjection.Replay(session, created.RunId, _codecs, events);
            if (projection.IsTerminal() || projection.State != RunState.Running || projection.RootTask is null)
                continue;
            var rootLane = LaneProjection.Replay(_codecs, events).ForTask(projection.RootTask)
                .FirstOrDefault(lane => lane.State == LaneState.Running)?.Id;
            return rootLane is null ? null : new RunAwaitingInput(created.RunId, rootLane);
        }
        return null;
    }

    private static ToolCallId? TryGetToolCallId(string? json)
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

    private static string TargetFrom(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "recurso no disponible";
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("path", out var path) && path.GetString() is { } value
                ? value : "recurso no disponible";
        }
        catch (System.Text.Json.JsonException)
        {
            return "recurso no disponible";
        }
    }

    private static bool IsProtectedTarget(string target) =>
        new RedactionPolicy().IsSecretPath(target);

    private static string SafeDetail(string detail, string target) => IsProtectedTarget(target)
        ? "detalle oculto para un recurso protegido"
        : new PiiRedactor().Redact(detail ?? "resultado no disponible");

    private static string JsonString(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value, JsonStrings.Default.String);

    private ToolCallReconciled ReconcileCall(ToolCallId id, string? reconciliationJson)
    {
        FilesystemReconciliation result = _fsReconciler is not null && reconciliationJson is not null
            ? _fsReconciler!.Reconcile(_workspaceRoot, reconciliationJson!, CancellationToken.None)
            : FilesystemReconciliation.Unresolvable(
                "metadatos de reconciliación ausentes o sin reconciliador: falla cerrado, sin re-ejecutar");
        return new ToolCallReconciled(id, result.Outcome, result.Detail);
    }

    private int ResumeRun(SessionId sessionId, RunId runId)
    {
        // A caller's explicit resume command is a real cause, not a chronological
        // fallback. Background recovery instead names the persisted effect source.
        var requestedBy = CausationScope.Current;
        var tail = _store.ReadFrom(sessionId, 1);
        var range = RunEventRange(tail, _codecs, runId);
        if (range is null)
        {
            return 0; // el run no está en la sesión: nada que reconciliar
        }

        // ADR-0004 §5.5: un Run solo es reanudable si su estado proyectado no es terminal.
        var runProjection = RunProjection.Replay(sessionId, runId, _codecs, OwnEvents(tail, range!));
        if (runProjection.IsTerminal())
        {
            return 0;
        }

        var from = range![0];
        var to = range![1];
        var startedNoOutcome = new Dictionary<ToolCallId, ToolCallStarted>();
        var startedEvents = new Dictionary<ToolCallId, DomainEvent>();
        var unknownEvents = new Dictionary<ToolCallId, DomainEvent>();
        // Solo Succeeded/Failed/Reconciled son terminales (ADR-0004 §2). EffectUnknown es un estado
        // INTERMEDIO: si el crash fue DESPUÉS de emitirlo y ANTES de Reconciled, la ToolCall sigue
        // sin outcome y el siguiente resume debe continuar la reconciliación sin duplicarlo.
        var terminal = new HashSet<ToolCallId>();
        var alreadyUnknown = new HashSet<ToolCallId>();
        for (var i = from; i < to; i++)
        {
            var payload = _codecs.Decode(tail[i]);
            if (payload is ToolCallStarted started)
            {
                startedNoOutcome[started.ToolCallId] = started;
                startedEvents[started.ToolCallId] = tail[i];
            }
            else if (payload is ToolCallEffectUnknown unknown)
            {
                alreadyUnknown.Add(unknown.ToolCallId);
                unknownEvents[unknown.ToolCallId] = tail[i];
            }
            else if (payload is ToolCallSucceeded || payload is ToolCallFailed
                || payload is ToolCallReconciled)
            {
                var id = ToolCallIdOf(payload);
                if (id is not null)
                {
                    terminal.Add(id!);
                }
            }
        }

        var stream = new EventStream(_store, _codecs, sessionId);
        var reconciled = 0;
        foreach (var entry in startedNoOutcome)
        {
            var id = entry.Key;
            var started = entry.Value;
            if (terminal.Contains(id))
            {
                continue; // idempotencia: ya resuelta por su outcome (o un Reconciled previo)
            }

            var startedEvent = startedEvents[id];
            var origin = unknownEvents.TryGetValue(id, out var recordedUnknown) ? recordedUnknown : startedEvent;
            using var execution = ExecutionScope.Begin(new ExecutionScopeState(runId,
                origin.TaskId ?? startedEvent.TaskId, origin.LaneId ?? startedEvent.LaneId,
                origin.TurnId ?? startedEvent.TurnId, id, origin.ExecutionId ?? startedEvent.ExecutionId));

            if (started.EffectClass == EffectClass.None && !alreadyUnknown.Contains(id))
            {
                // ADR-0004 §3: una tool sin efectos (EffectClass.None) se reejecuta siempre: no hay
                // nada que reconciliar ni que bloquee el Run. Se cierra como fallida-sin-efecto
                // (Started → Failed, ADR-0036) para que el modelo pueda reintentarla.
                // Código tipado CANCELLATION (spec §71): la ejecución se interrumpió por el crash.
                // No es UNKNOWN_EFFECT porque el efecto NO es desconocido (None, sin efecto
                // parcial); ese código queda para la reconciliación conservadora (ADR-0004 §2).
                using (CausationScope.Begin(requestedBy ?? new EventCausation(startedEvent.EventId)))
                    stream.Append(new ToolCallFailed(id,
                        "interrupted by crash before completion; no side effects, safe to retry", EffectOutcome.None,
                        ToolErrorCode.Cancellation));
                reconciled += 1;
                continue;
            }

            if (!alreadyUnknown.Contains(id))
            {
                // Solo el primer resume emite EffectUnknown; si ya está en el journal (crash
                // posterior a EffectUnknown), se continúa sin duplicarlo.
                var before = _store.CurrentSequence(sessionId);
                using (CausationScope.Begin(requestedBy ?? new EventCausation(startedEvent.EventId)))
                    stream.Append(new ToolCallEffectUnknown(id, started.EffectClass));
                unknownEvents[id] = stream.EventsSince(before + 1).Single(evt =>
                    _codecs.Decode(evt) is ToolCallEffectUnknown unknown && unknown.ToolCallId == id);
            }

            FilesystemReconciliation result;
            if (_fsReconciler is not null && started.ReconciliationJson is not null)
            {
                result = _fsReconciler!.Reconcile(_workspaceRoot, started.ReconciliationJson!,
                    CancellationToken.None);
            }
            else
            {
                // Sin metadatos/reconciliador: falla cerrado. Nunca se re-ejecuta ni se clasifica.
                result = FilesystemReconciliation.Unresolvable(
                    "metadatos de reconciliación ausentes o sin reconciliador: falla cerrado, sin re-ejecutar");
            }

            using (CausationScope.Begin(requestedBy ?? new EventCausation(unknownEvents[id].EventId)))
                stream.Append(new ToolCallReconciled(id, result.Outcome, result.Detail));
            reconciled += 1;
        }

        return reconciled;
    }

    /// <summary>Eventos del Run pedido (slice del rango [from, to)) para proyectar su estado.</summary>
    private static IReadOnlyList<DomainEvent> OwnEvents(IReadOnlyList<DomainEvent> tail, int[] range)
    {
        var events = new List<DomainEvent>();
        for (var i = range[0]; i < range[1]; i++)
        {
            events.Add(tail[i]);
        }

        return events;
    }

    /// <summary>Extrae el ToolCallId de un evento de outcome (estos campos son no-nulos).</summary>
    private static ToolCallId? ToolCallIdOf(DomainEventPayload payload)
    {
        if (payload is ToolCallSucceeded s) return s.ToolCallId;
        if (payload is ToolCallFailed f) return f.ToolCallId;
        if (payload is ToolCallReconciled r) return r.ToolCallId;
        if (payload is ToolCallEffectUnknown u) return u.ToolCallId;
        return null;
    }

    /// <summary>
    /// Rango de secuencias [from, to) de los eventos atribuibles al run pedido: desde su RunCreated
    /// hasta el RunCreated del siguiente run (o el final de la sesión). Un solo run activo por
    /// sesión (ADR-0035). Devuelve null si el run no aparece en la sesión.
    /// </summary>
    private static int[]? RunEventRange(IReadOnlyList<DomainEvent> tail, IEventCodecRegistry codecs,
        RunId runId)
    {
        var from = -1;
        var to = tail.Count;
        for (var i = 0; i < tail.Count; i++)
        {
            var payload = codecs.Decode(tail[i]);
            if (payload is not RunCreated run)
            {
                continue;
            }

            if (run.RunId == runId)
            {
                from = i;
            }
            else if (from >= 0)
            {
                to = i;
                break;
            }
        }

        if (from < 0)
        {
            return null;
        }

        return new int[] { from, to };
    }
}
