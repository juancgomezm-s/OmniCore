namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Tools;

/// <summary>
/// Turn de Explorer end-to-end (ADR-0005 §2, ADR-0035 §3, ADR-0041 §2): conecta el contexto
/// materializado con el modelo, ejecuta las tool calls por el pipeline REAL de tools y permisos,
/// y PERSISTE en el journal: TurnStarted, cada evento de tool (request/permission/auth/outcome),
/// la respuesta como artifact + ModelCompleted, y TurnCompleted. El budget del Turn se lee del
/// RunCreated; cuando se excede se emite InteractionRequested(BudgetExceeded) y corta. Si el
/// contexto protegido no cabe tras recortar la conversación termina con StopReason.ContextOverflow.
/// SIN cliente interactivo → las Ask se deniegan (ADR-0003).
/// </summary>
public sealed class ExplorerTurn
{
    public static readonly int MaxSteps = 8;

    private readonly Func<ModelRequest, CancellationToken, ModelResponse> _complete;

    private readonly IToolExecutor _tools;

    private readonly FakeCatalog _catalog;

    private readonly ContextMaterializer _materializer;

    private readonly ExecutionFingerprint _fingerprint;

    private readonly ModelSelection _selection;

    private readonly IEventStore _store;

    private readonly IEventCodecRegistry _codecs;

    private readonly IArtifactStore _artifacts;

    private readonly IAuditSink _audit;

    private readonly RedactionPolicy _redaction;

    private readonly HarnessPolicy? _harness;

    /// <summary>
    /// Frontera de capacidad del modelo (ADR-0044 §5): el ToolPlanner (VisibleTools) oculta las
    /// tools fuera del techo y el ToolRuntime la re-valida antes de permisos y antes de ejecutar.
    /// La frontera restringe; jamás autoriza. null = sin frontera (semántica M2). La política
    /// efectiva ya está intersectada con el harness por el resolver.
    /// </summary>
    private readonly ModelCapabilityBoundary? _boundary;

    private readonly ModelPricing? _pricing;

    private readonly bool _enforceDefaultSpendCaps;

    // ADR-0037 §7. Solo se aplican a providers que requieren API key; se pueden cambiar por
    // constructor/configuración de Host. Las tarifas nunca se inventan localmente.
    private readonly decimal _sessionCapUsd;

    private readonly decimal _dailyCapUsd;

    public ExplorerTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete, IToolExecutor tools,
        FakeCatalog catalog, ContextMaterializer materializer, ExecutionFingerprint fingerprint,
        ModelSelection selection, IEventStore store, IEventCodecRegistry codecs, IArtifactStore artifacts,
        IAuditSink audit, RedactionPolicy redaction, HarnessPolicy? harness = null,
        ModelCapabilityBoundary? boundary = null, ModelPricing? pricing = null,
        bool enforceDefaultSpendCaps = false, decimal sessionCapUsd = 5m, decimal dailyCapUsd = 20m)
    {
        if (sessionCapUsd < 0m) throw new ArgumentOutOfRangeException(nameof(sessionCapUsd));
        if (dailyCapUsd < 0m) throw new ArgumentOutOfRangeException(nameof(dailyCapUsd));
        _complete = complete;
        _tools = tools;
        _catalog = catalog;
        _materializer = materializer;
        _fingerprint = fingerprint;
        _selection = selection;
        _store = store;
        _codecs = codecs;
        _artifacts = artifacts;
        _audit = audit;
        _redaction = redaction;
        _harness = harness;
        _boundary = boundary;
        _pricing = pricing;
        _enforceDefaultSpendCaps = enforceDefaultSpendCaps;
        _sessionCapUsd = sessionCapUsd;
        _dailyCapUsd = dailyCapUsd;
    }

    /// <summary>Journal del Turn (tests: para abrir en él el Run al que pertenece el Turn).</summary>
    internal IEventStore JournalStore => _store;

    /// <summary>Constructor de conveniencia: en-memoria (tests, sin persistencia durable).</summary>
    public ExplorerTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete, IToolExecutor tools,
        FakeCatalog catalog, ContextMaterializer materializer, ExecutionFingerprint fingerprint,
        ModelSelection selection, ModelCapabilityBoundary? boundary = null)
        : this(complete, tools, catalog, materializer, fingerprint, selection,
            new OmniCore.Infrastructure.InMemoryEventStore(),
            OmniCore.Infrastructure.EventCodecs.Create(),
            new OmniCore.Infrastructure.FileArtifactStore(
                System.IO.Path.GetTempPath() + "omnicore-artifacts-test"),
            new OmniCore.Infrastructure.InMemoryAuditSink(),
            new OmniCore.Domain.RedactionPolicy(),
            null,
            boundary,
            null,
            false)
    {
    }

    private sealed record PersistedSpend(decimal SessionUsd, decimal DailyUsd, decimal RunUsd, bool Incomplete);

    private sealed record UsageEnvelope(string Response, string RunId, string Day, decimal? CostUsd);

    public sealed class TurnResult
    {
        public string? FinalText { get; }

        public StopReason StopReason { get; }

        public int Steps { get; }

        public TokenUsage Usage { get; }

        public IReadOnlyList<ToolUseTrace> ToolCalls { get; }

        public string? ResponseArtifactId { get; }

        public TurnResult(string? finalText, StopReason stopReason, int steps, TokenUsage usage,
            IReadOnlyList<ToolUseTrace> toolCalls, string? responseArtifactId)
        {
            FinalText = finalText;
            StopReason = stopReason;
            Steps = steps;
            Usage = usage;
            ToolCalls = toolCalls;
            ResponseArtifactId = responseArtifactId;
        }
    }

    public sealed class ToolUseTrace
    {
        public string ToolName { get; }

        public bool Succeeded { get; }

        public string? Summary { get; }

        public string ArgsJson { get; }

        public ToolUseTrace(string toolName, bool succeeded, string? summary, string argsJson)
        {
            ToolName = toolName;
            Succeeded = succeeded;
            Summary = summary;
            ArgsJson = argsJson;
        }
    }

    /// <summary>Overload de conveniencia para tests: crea una Lane efímera.</summary>
    public TurnResult Ask(string question, string instruction, SessionId sessionId, RunId runId,
        string workingStateText, CancellationToken cancellationToken)
    {
        return Ask(question, instruction, sessionId, runId, LaneId.New(), workingStateText, cancellationToken);
    }

    /// <summary>Ejecuta la pregunta del usuario con contexto real y persiste el Turn en el journal.</summary>
    public TurnResult Ask(string question, string instruction, SessionId sessionId, RunId runId,
        LaneId laneId, string workingStateText, CancellationToken cancellationToken)
    {
        var stream = new EventStream(_store, _codecs, sessionId);
        var turnId = TurnId.New();
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var persistedSpend = ReadJournalSpend(stream, runId, today);

        var messages = LoadConversation(stream, runId);
        var safeQuestion = _redaction.Redact(question ?? "");
        if (safeQuestion.Length > 0)
        {
            messages.Add(new ModelMessage(MessageRole.User, new ContentBlock[] { new TextBlock(safeQuestion) }));
        }

        var allToolCalls = new List<ToolUseTrace>();
        var usage = new TokenUsage(0, 0, 0, 0, 0);

        var budget = ReadRunBudget(stream, runId);
        var guard = new SpendGuard(budget);
        var steps = 1;
        var started = false;

        try
        {
            var preparedContext = MaterializeTurnContext(sessionId, runId, laneId, turnId,
                workingStateText, instruction, messages, cancellationToken);
            var materialized = preparedContext.Snapshot;
            var snapshotArtifact = PersistContextSnapshot(materialized);

            // ContextOverflow: el contenido protegido no cabe ni después de recortar la conversación.
            if (materialized.Overflowed)
            {
                stream.Append(new TurnStarted(turnId, laneId, _fingerprint, snapshotArtifact));
                started = true;
                // La state machine de Turn: Started → … → Abandoned (terminal). NUNCA se emite
                // TurnCompleted tras Abandoned (P1: transición inválida).
                stream.Append(new TurnAbandoned(turnId, "ContextOverflow: el contexto no entra en el presupuesto"));
                return new TurnResult("ContextOverflow: el contexto no cabe en el presupuesto del modelo",
                    StopReason.ContextOverflow, 0, usage, allToolCalls.ToArray(), null);
            }

            EnsureRunAwaitingInput(stream, runId, laneId);
            var encodedInput = System.Text.Json.JsonEncodedText.Encode(safeQuestion);
            stream.Append(new UserInputReceived(runId, "\"" + encodedInput + "\"", null));
            stream.Append(new TurnStarted(turnId, laneId, _fingerprint, snapshotArtifact));
            started = true;

            string? finalText = null;
            var stop = StopReason.EndTurn;
            for (var step = 0; step < MaxSteps; step++)
            {
                steps = step + 1;
                if (step > 0)
                {
                    preparedContext = MaterializeTurnContext(sessionId, runId, laneId, turnId,
                        workingStateText, instruction, messages, cancellationToken);
                    materialized = preparedContext.Snapshot;
                    if (materialized.Overflowed)
                    {
                        stream.Append(new TurnAbandoned(turnId,
                            "ContextOverflow: los resultados de tools exceden el presupuesto"));
                        return new TurnResult("ContextOverflow: el contexto no cabe en el presupuesto del modelo",
                            StopReason.ContextOverflow, steps, usage, allToolCalls.ToArray(), null);
                    }
                }

                var request = new ModelRequest(
                    _selection,
                    preparedContext.Messages,
                    RenderContext(materialized),
                    VisibleTools(),
                    ToolChoice.Auto(),
                    null, null, new CacheHints(4, "automatic"), null);

                ModelResponse resolved;
                try
                {
                    var budgeted = budget.MaxCostUsd is not null || _enforceDefaultSpendCaps;
                    if (budgeted && (_pricing is null || !_pricing.IsComplete))
                        throw new BudgetExceededException("precio desconocido: no se puede hacer cumplir el tope");
                    if (budgeted && persistedSpend.Incomplete)
                        throw new BudgetExceededException("uso histórico incompleto: no se puede hacer cumplir el tope");
                    var accumulatedRunCost = persistedSpend.RunUsd + guard.CostUsd()
                        + (_pricing?.CostUsd(usage) ?? 0m);
                    var accumulatedSessionCost = persistedSpend.SessionUsd + (_pricing?.CostUsd(usage) ?? 0m);
                    var accumulatedDailyCost = persistedSpend.DailyUsd + (_pricing?.CostUsd(usage) ?? 0m);
                    if (budget.MaxCostUsd is not null && accumulatedRunCost >= budget.MaxCostUsd.Value)
                        throw new BudgetExceededException("límite de costo de Run alcanzado ($"
                            + budget.MaxCostUsd.Value + ")");
                    if (_enforceDefaultSpendCaps && accumulatedSessionCost >= _sessionCapUsd)
                        throw new BudgetExceededException("límite de sesión alcanzado ($" + _sessionCapUsd + ")");
                    if (_enforceDefaultSpendCaps && accumulatedDailyCost >= _dailyCapUsd)
                        throw new BudgetExceededException("límite diario alcanzado ($" + _dailyCapUsd + ")");

                    resolved = _complete(request, cancellationToken);
                    usage = CombineUsage(usage, resolved.Usage);
                    guard.AdvanceTurn(resolved.Usage.Input + resolved.Usage.Output);
                    var stepCost = _pricing?.CostUsd(resolved.Usage);
                    if (stepCost is not null) guard.AddCostUsd(stepCost.Value);
                    if (budget.MaxCostUsd is not null
                        && persistedSpend.RunUsd + guard.CostUsd() > budget.MaxCostUsd.Value)
                        throw new BudgetExceededException("límite de costo de Run ($" + budget.MaxCostUsd.Value + ")");
                    if (_enforceDefaultSpendCaps)
                    {
                        var turnCost = _pricing!.CostUsd(usage) ?? 0m;
                        ValidateSessionDaily(persistedSpend.SessionUsd + turnCost,
                            persistedSpend.DailyUsd + turnCost);
                    }
                }
                catch (BudgetExceededException budgetEx)
                {
                    EmitBudgetExceeded(stream, turnId, budgetEx.Detail);
                    stop = StopReason.Cancelled;
                    finalText = "Presupuesto agotado: " + budgetEx.Detail;
                    break;
                }

                finalText = null;
                var toolBlocks = new List<ToolCallBlock>();
                foreach (ContentBlock block in resolved.Content)
                {
                    if (block is TextBlock text)
                    {
                        finalText = text.Text;
                    }
                    else if (block is ToolCallBlock call)
                    {
                        toolBlocks.Add(call);
                    }
                }

                if (resolved.StopReason != StopReason.ToolUse || toolBlocks.Count == 0)
                {
                    stop = resolved.StopReason;
                    break;
                }

                foreach (ToolCallBlock call in toolBlocks)
                {
                    try
                    {
                        guard.RecordToolCall();
                    }
                    catch (BudgetExceededException budgetEx)
                    {
                        EmitBudgetExceeded(stream, turnId, budgetEx.Detail);
                        stop = StopReason.Cancelled;
                        finalText = "Presupuesto agotado: " + budgetEx.Detail;
                        break;
                    }

                    var validated = new ValidatedToolCall(call.Id, new ToolId(call.ToolName),
                        call.ProviderCallId ?? call.Id.ToString(), call.ArgumentsJson);
                    var exposed = VisibleTools().Any(tool => tool.Name == call.ToolName);
                    var outcome = exposed
                        ? _tools.ExecuteTool(validated, false, cancellationToken, stream)
                        : ToolOutcome.Failed("tool no disponible para este modelo", null,
                            ToolCallState.Rejected, new DomainEventPayload[] {
                                new ToolCallRequested(call.Id, validated.ProviderCallId, call.ToolName,
                                    call.ArgumentsJson),
                                new ToolCallRejected(call.Id, "tool no disponible para este modelo")
                            });

                    string? planError = null;
                    IReadOnlyList<DomainEventPayload> planEvents = Array.Empty<DomainEventPayload>();
                    if (call.ToolName == "plan.propose" && outcome.Succeeded
                        && outcome.Effect == EffectOutcome.Applied && outcome.Preview is not null)
                    {
                        var proposal = ApplyPlanProposal(stream, runId, outcome.Preview!);
                        planError = proposal.Error;
                        planEvents = proposal.Events;
                    }

                    // Persistir los eventos del pipeline REAL (request/permission/auth/outcome) y los del
                    // plan en un solo lote atómico: un crash nunca deja la cadena de la ToolCall a medias.
                    var toPersist = new List<DomainEventPayload>(outcome.Events.Count + planEvents.Count);
                    foreach (var evt in outcome.Events)
                    {
                        toPersist.Add(planError is not null && evt is ToolCallSucceeded
                            ? new ToolCallFailed(call.Id, _redaction.Redact(planError), EffectOutcome.None)
                            : evt);
                    }

                    toPersist.AddRange(planEvents);
                    stream.AppendBatch(toPersist, DurabilityClass.Standard);

                    // Redacción obligatoria del tool result antes de dárselo al modelo.
                    var content = outcome.Preview is not null && outcome.Preview!.Length > 0
                        ? _redaction.Redact(outcome.Preview!)
                        : (outcome.Summary is null ? "ok" : _redaction.Redact(outcome.Summary!));
                    var succeeded = outcome.Succeeded && planError is null;
                    var resultText = succeeded ? content : "error: " + _redaction.Redact(planError ?? outcome.Summary ?? "failed");
                    allToolCalls.Add(new ToolUseTrace(call.ToolName, succeeded, planError ?? outcome.Summary,
                        call.ArgumentsJson));

                    var assistant = new ModelMessage(MessageRole.Assistant, new ContentBlock[] {
                        new ToolCallBlock(call.Id, call.ProviderCallId, call.ToolName, call.ArgumentsJson),
                    });
                    var toolResult = new ModelMessage(MessageRole.Tool, new ContentBlock[] {
                        new ToolResultBlock(call.Id, new ContentBlock[] { new TextBlock(resultText) }, !succeeded),
                    });
                    messages.Add(assistant);
                    messages.Add(toolResult);
                }

                if (stop == StopReason.Cancelled)
                {
                    break;
                }
            }

            if (finalText is null && stop == StopReason.EndTurn)
            {
                stream.Append(new TurnAbandoned(turnId, "Se agotó el límite de pasos del Explorer"));
                return new TurnResult(null, StopReason.Error, steps, usage, allToolCalls.ToArray(), null);
            }

            // Respuesta como artifact + ModelCompleted (persistencia del Turn).
            string? artifactId = null;
            if (finalText is not null && finalText!.Length > 0)
            {
                var safeResponse = _redaction.Redact(finalText!);
                var cost = _pricing?.CostUsd(usage);
                var journalRecord = EncodeUsageResponse(safeResponse, usage, cost, runId, today);
                var artifact = _artifacts.PutText(journalRecord, "application/vnd.omnicore.model-usage+json",
                    ArtifactKind.ModelResponse, Sensitivity.Sensitive);
                artifactId = artifact.Hash.ToString();
                stream.Append(new ModelCompleted(turnId, artifact));
            }

            stream.Append(new TurnCompleted(turnId));
            AuditSpend(sessionId, runId, laneId, turnId, stop, usage, _redaction.Redact(finalText ?? ""));
            AuditPolicy(sessionId, runId, turnId, stop);
            return new TurnResult(finalText, stop, steps, usage, allToolCalls.ToArray(), artifactId);
        }
        catch (Exception ex)
        {
            try
            {
                // Started → … → Abandoned (terminal); NUNCA Completed tras Abandoned (state machine).
                if (started)
                    stream.Append(new TurnAbandoned(turnId, "turn falló: " + _redaction.Redact(ex.Message ?? "")));
            }
            catch (Exception)
            {
            }

            return new TurnResult(null, StopReason.Error, steps, usage, allToolCalls.ToArray(), null);
        }
    }

    /// <summary>
    /// Registra el gasto del turno en el AUDIT (ADR-0043, INV-012): detalles redactados,
    /// sin secretos. Persiste el gasto real del turno para los reportes y la capa de
    /// presupuesto por sesión/día (P1: el audit sink del turno se usa).
    /// </summary>
    private void AuditSpend(SessionId sessionId, RunId runId, LaneId laneId, TurnId turnId,
        StopReason stop, TokenUsage usage, string redactedFinal)
    {
        try
        {
            var details = new Dictionary<string, string>();
            details["stop"] = stop.ToString();
            details["inputTokens"] = usage.Input.ToString();
            details["outputTokens"] = usage.Output.ToString();
            var cost = _pricing?.CostUsd(usage);
            if (cost is not null) details["costUsd"] = cost.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            details["final"] = redactedFinal.Length > 200 ? redactedFinal.Substring(0, 200) : redactedFinal;
            _audit.Record(new AuditRecord("turn.spend", null, sessionId, runId, DateTimeOffset.Now,
                turnId.ToString(), details), CancellationToken.None);
        }
        catch (Exception)
        {
            // un fallo del audit no rompe el turno
        }
    }

    /// <summary>Presupuesto del Turn desde el RunCreated del journal (el comando lo configura).</summary>
    private TaskBudget ReadRunBudget(EventStream stream)
    {
        return ReadRunBudget(stream, null);
    }

    private TaskBudget ReadRunBudget(EventStream stream, RunId? ofRun)
    {
        var tail = stream.EventsSince(1);
        foreach (var evt in tail)
        {
            if (!evt.Type.ToString().Equals("run.created", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = _codecs.Decode(evt);
            if (payload is RunCreated runCreated)
            {
                // P0-4: filtrar por runId — nunca el primer RunCreated de la sesión.
                if (ofRun is not null && !runCreated.RunId.ToString().Equals(ofRun!.ToString(), StringComparison.Ordinal))
                {
                    continue;
                }

                return runCreated.Budget is null
                    ? new TaskBudget(null, null, null, null)
                    : runCreated.Budget!;
            }
        }

        return new TaskBudget(null, null, null, null);
    }

    /// <summary>
    /// Gasto ya confirmado en el journal del workspace. ModelCompleted referencia un artifact
    /// versionado que conserva la respuesta y la usage/cost metadata; el replay no depende de
    /// contadores en memoria ni de tarifas que hayan cambiado desde entonces.
    /// </summary>
    private PersistedSpend ReadJournalSpend(EventStream stream, RunId runId, string today)
    {
        decimal session = 0m, daily = 0m, run = 0m;
        var incomplete = false;
        foreach (var evt in stream.EventsSince(1))
        {
            if (!evt.Type.ToString().Equals("model.completed", StringComparison.Ordinal)) continue;
            var completed = _codecs.Decode(evt) as ModelCompleted;
            if (completed?.ResponseArtifact is null) { incomplete = true; continue; }
            var text = _artifacts.GetText(completed.ResponseArtifact.Hash);
            if (!TryDecodeUsageEnvelope(text, out var record) || record.CostUsd is null)
            {
                incomplete = true;
                continue;
            }
            var cost = record.CostUsd.Value;
            session += cost;
            if (record.Day == today) daily += cost;
            if (record.RunId == runId.ToString()) run += cost;
        }
        return new PersistedSpend(session, daily, run, incomplete);
    }

    private static string EncodeUsageResponse(string response, TokenUsage usage, decimal? cost,
        RunId runId, string day)
    {
        var costJson = cost is null ? "null" : "\""
            + cost.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"";
        return "{\"omnicoreUsage\":1," + JsonObj.Field("response", response)
            + ",\"runId\":\"" + runId + "\",\"day\":\"" + day
            + "\",\"input\":" + usage.Input.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"output\":" + usage.Output.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"cacheRead\":" + usage.CacheRead.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"cacheWrite\":" + usage.CacheWrite.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"reasoning\":" + usage.Reasoning.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"costUsd\":" + costJson + "}";
    }

    private static string DecodeUsageResponse(string? text) =>
        TryDecodeUsageEnvelope(text, out var record) ? record.Response : text ?? "";

    private static bool TryDecodeUsageEnvelope(string? text, out UsageEnvelope record)
    {
        record = new UsageEnvelope("", "", "", null);
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(text);
            var root = document.RootElement;
            if (!root.TryGetProperty("omnicoreUsage", out var version) || version.GetInt32() != 1
                || !root.TryGetProperty("response", out var response)
                || !root.TryGetProperty("runId", out var runId)
                || !root.TryGetProperty("day", out var day)
                || !root.TryGetProperty("costUsd", out var cost)) return false;
            decimal? parsedCost = cost.ValueKind == System.Text.Json.JsonValueKind.String
                && decimal.TryParse(cost.GetString(), System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
            record = new UsageEnvelope(response.GetString() ?? "", runId.GetString() ?? "",
                day.GetString() ?? "", parsedCost);
            return true;
        }
        catch (System.Text.Json.JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Aplica plan.propose contra las proyecciones del mismo Run (P0-6 + requisito 2).</summary>
    private (string? Error, IReadOnlyList<DomainEventPayload> Events) ApplyPlanProposal(EventStream stream, RunId runId,
        string mutationJson)
    {
        var tail = OwnTail(stream, runId);
        var planProj = PlanProjection.Replay(_codecs, tail);
        var indexes = new Dictionary<string, PlanItemId>();
        var first = 1;
        foreach (var item in planProj.Items())
        {
            indexes["P" + first] = item.Id;
            indexes[item.Id.ToString()] = item.Id;
            first += 1;
        }

        var plan = Mutations.ResolveDeclared(mutationJson, indexes);
        if (plan is null)
        {
            const string reason = "La mutación no identifica un item válido de este Run";
            using var document = System.Text.Json.JsonDocument.Parse(mutationJson);
            var kindText = document.RootElement.GetProperty("kind").GetString() ?? "";
            var kind = PlanProposeTool.ParseKind(kindText);
            return (reason, kind is null ? Array.Empty<DomainEventPayload>()
                : new DomainEventPayload[] { new PlanMutationRejected(null, kind.Value, reason) });
        }

        var tasksProj = TaskGraphProjection.Replay(_codecs, tail);
        var lanesProj = LaneProjection.Replay(_codecs, tail);
        var result = new PlanService().Apply(planProj, tasksProj, lanesProj, plan!);
        if (!result.Accepted)
        {
            return (result.Reason ?? "mutación rechazada", new DomainEventPayload[] {
                new PlanMutationRejected(plan.ItemId, plan.Kind,
                    _redaction.Redact(result.Reason ?? "mutación rechazada"))
            });
        }

        return (null, result.Events);
    }

    /// <summary>
    /// Devuelve solo los eventos del Run dado (desde su RunCreated) — P0-4: "mismo Run" significa
    /// filtrar por runId real, no todo lo de la sesión. El primer RunCreated de la sesión no compite.
    /// </summary>
    private IReadOnlyList<DomainEvent> OwnTail(EventStream stream, RunId runId)
    {
        var all = stream.EventsSince(1);
        var result = new List<DomainEvent>();
        var capture = false;
        foreach (var evt in all)
        {
            if (evt.Type.ToString().Equals("run.created", StringComparison.Ordinal))
            {
                var payload = _codecs.Decode(evt);
                var runCreated = payload as RunCreated;
                capture = runCreated is not null
                    && runCreated!.RunId.ToString().Equals(runId.ToString(), StringComparison.Ordinal);
                if (capture)
                {
                    result.Add(evt);
                }

                continue;
            }

            if (capture)
            {
                result.Add(evt);
            }
        }

        return result.ToArray();
    }

    /// <summary>Emite InteractionRequested(BudgetExceeded) y el evento de uso al journal.</summary>
    /// <summary>
    /// Emite la interacción de presupuesto (INTERACTION BudgetExceeded, ADR-0037 §7 / ADR-0034)
    /// con las opciones canónicas: deny = Detener, allow_plus = Continuar hasta +N.
    /// Se emite UNA sola vez por exceso (el listener del turno corta; no se re-emite).
    /// </summary>
    private void EmitBudgetExceeded(EventStream stream, TurnId turnId, string detail)
    {
        var interactionId = InteractionId.New();
        stream.Append(new InteractionRequested(interactionId, InteractionKind.BudgetExceeded,
            "{\"detail\":\"" + detail + "\"}",
            "[{\"id\":\"deny\",\"intent\":\"deny\"},{\"id\":\"allow_plus\",\"intent\":\"allow_plus\",\"value\":10}]",
            "deny", null, null, null, null, 0, 1));
    }

    /// <summary>
    /// Acumula costo de sesión/día (ADR-0037 §7: 5/20 USD). SOLO lanza al superar el tope:
    /// la emisión de la interacción la hace el catch del turno (evita la doble emisión).
    /// </summary>
    private void ValidateSessionDaily(decimal totalSessionCost, decimal totalDailyCost)
    {
        if (totalSessionCost > _sessionCapUsd)
            throw new OmniCore.Engine.BudgetExceededException("límite de sesión ($" + _sessionCapUsd + ")");
        if (totalDailyCost > _dailyCapUsd)
            throw new OmniCore.Engine.BudgetExceededException("límite diario ($" + _dailyCapUsd + ")");
    }

    /// <summary>Renderiza el snapshot materializado como texto para el system prompt.</summary>
    public static string RenderContext(ContextSnapshot snapshot)
    {
        var parts = new List<string>();
        foreach (ContextItem item in snapshot.Items)
        {
            if (item.Kind == ContextItemKind.WorkingState || item.Kind == ContextItemKind.System
                || item.Kind == ContextItemKind.Task || item.Kind == ContextItemKind.Decision
                || item.Kind == ContextItemKind.Constraint || item.Kind == ContextItemKind.File)
            {
                if (item.Content.Length > 0)
                {
                    parts.Add("- " + item.Content.Replace("\n", " "));
                }
            }
        }

        return string.Join("\n", parts.ToArray());
    }

    private List<ModelMessage> LoadConversation(EventStream stream, RunId runId)
    {
        var history = new List<ModelMessage>();
        foreach (var evt in stream.EventsSince(1))
        {
            if (evt.RunId is null || !evt.RunId.ToString().Equals(runId.ToString(), StringComparison.Ordinal))
            {
                continue;
            }

            var type = evt.Type.ToString();
            if (type == "user_input.received")
            {
                var input = _codecs.Decode(evt) as UserInputReceived;
                if (input is null) continue;
                try
                {
                    using var parsed = System.Text.Json.JsonDocument.Parse(input.InputPartsJson);
                    var text = parsed.RootElement.GetString();
                    if (!string.IsNullOrEmpty(text))
                        history.Add(new ModelMessage(MessageRole.User,
                            new ContentBlock[] { new TextBlock(_redaction.Redact(text)) }));
                }
                catch (System.Text.Json.JsonException) { }
            }
            else if (type == "toolcall.requested")
            {
                var call = _codecs.Decode(evt) as ToolCallRequested;
                if (call is not null)
                    history.Add(new ModelMessage(MessageRole.Assistant, new ContentBlock[] {
                        new ToolCallBlock(call.ToolCallId, call.ProviderCallId, call.ToolName,
                            _redaction.Redact(call.ArgumentsJson))
                    }));
            }
            else if (type == "toolcall.succeeded" || type == "toolcall.failed" || type == "toolcall.rejected")
            {
                var payload = _codecs.Decode(evt);
                var callId = payload switch
                {
                    ToolCallSucceeded e => e.ToolCallId,
                    ToolCallFailed e => e.ToolCallId,
                    ToolCallRejected e => e.ToolCallId,
                    _ => (ToolCallId?)null,
                };
                if (callId is null) continue;
                var content = payload switch
                {
                    ToolCallSucceeded e => _redaction.Redact(e.ResultJson),
                    ToolCallFailed e => "error: " + _redaction.Redact(e.Cause),
                    ToolCallRejected e => "error: " + _redaction.Redact(e.Reason),
                    _ => "error",
                };
                history.Add(new ModelMessage(MessageRole.Tool, new ContentBlock[] {
                    new ToolResultBlock(callId!, new ContentBlock[] { new TextBlock(content) },
                        payload is not ToolCallSucceeded)
                }));
            }
            else if (type == "model.completed")
            {
                var completed = _codecs.Decode(evt) as ModelCompleted;
                if (completed?.ResponseArtifact is null) continue;
                var text = DecodeUsageResponse(_artifacts.GetText(completed.ResponseArtifact.Hash));
                if (!string.IsNullOrEmpty(text))
                    history.Add(new ModelMessage(MessageRole.Assistant,
                        new ContentBlock[] { new TextBlock(_redaction.Redact(text)) }));
            }
        }

        return history;
    }

    private PreparedTurnContext MaterializeTurnContext(SessionId sessionId, RunId runId, LaneId laneId,
        TurnId turnId, string workingStateText, string instruction, IReadOnlyList<ModelMessage> messages,
        CancellationToken cancellationToken)
    {
        var contributors = new List<IContextContributor>();
        var hasWorkingState = false;
        foreach (var contributor in _materializer.Contributors())
        {
            hasWorkingState |= contributor is WorkingStateContributor;
            contributors.Add(new RedactingContextContributor(contributor, _redaction));
        }

        if (!hasWorkingState && !string.IsNullOrWhiteSpace(workingStateText))
        {
            contributors.Add(new RedactingContextContributor(new WorkingStateContributor(workingStateText),
                _redaction));
        }

        var prompt = "Contexto del workspace (fuentes del run):\n"
            + (instruction ?? "").Replace("{context}", "", StringComparison.Ordinal).Trim()
            + "\nFingerprint: " + _fingerprint.ModelKey + " · " + _fingerprint.ContextPolicyHash;
        contributors.Add(new RedactingContextContributor(new SystemPromptContributor(prompt), _redaction));
        var entries = new List<ConversationContextEntry>();
        var messageById = new Dictionary<string, ModelMessage>(StringComparer.Ordinal);
        var firstUser = true;
        for (var i = 0; i < messages.Count; i++)
        {
            var safe = RedactMessage(messages[i]);
            var id = "conversation-" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            var kind = safe.Role switch
            {
                MessageRole.User => ContextItemKind.UserMessage,
                MessageRole.Assistant => ContextItemKind.AssistantMessage,
                _ => ContextItemKind.ToolResult,
            };
            var preserve = safe.Role == MessageRole.User && firstUser;
            if (safe.Role == MessageRole.User)
            {
                firstUser = false;
            }

            entries.Add(new ConversationContextEntry(id, kind, RenderMessage(safe), preserve));
            messageById.Add(id, safe);
        }

        contributors.Add(new SessionConversationContributor(entries));
        var materializer = new ContextMaterializer(_materializer.Counter(), contributors);
        var snapshot = materializer.MaterializeWithinBudget(
            new MaterializeRequest(sessionId, runId, null, laneId, turnId, 0L, _fingerprint),
            cancellationToken, (int)_selection.ContextBudget);
        var selectedMessages = new List<ModelMessage>();
        foreach (var item in snapshot.Items)
        {
            if (messageById.TryGetValue(item.Id, out var message))
            {
                selectedMessages.Add(message);
            }
        }

        return new PreparedTurnContext(snapshot, selectedMessages.ToArray());
    }

    private ModelMessage RedactMessage(ModelMessage message)
    {
        var blocks = new List<ContentBlock>();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock text:
                    blocks.Add(new TextBlock(_redaction.Redact(text.Text)));
                    break;
                case ToolCallBlock call:
                    blocks.Add(new ToolCallBlock(call.Id, call.ProviderCallId, call.ToolName,
                        _redaction.Redact(call.ArgumentsJson)));
                    break;
                case ToolResultBlock result:
                    blocks.Add(new ToolResultBlock(result.Id, RedactBlocks(result.Content), result.IsError));
                    break;
                case ReasoningBlock reasoning:
                    blocks.Add(new ReasoningBlock(reasoning.VisibleText is null
                        ? null : _redaction.Redact(reasoning.VisibleText), reasoning.Visibility, null));
                    break;
                case CitationBlock citation:
                    blocks.Add(new CitationBlock(_redaction.Redact(citation.Text),
                        _redaction.Redact(citation.SourceRef)));
                    break;
                case ProviderOpaqueBlock:
                    blocks.Add(new TextBlock("[estado opaco del provider omitido]"));
                    break;
            }
        }

        return new ModelMessage(message.Role, blocks.ToArray());
    }

    private IReadOnlyList<ContentBlock> RedactBlocks(IReadOnlyList<ContentBlock> blocks)
    {
        var result = new List<ContentBlock>();
        foreach (var block in blocks)
        {
            var safe = RedactMessage(new ModelMessage(MessageRole.Tool, new[] { block }));
            result.AddRange(safe.Content);
        }

        return result.ToArray();
    }

    private static string RenderMessage(ModelMessage message)
    {
        var parts = new List<string>();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock text:
                    parts.Add(text.Text);
                    break;
                case ToolCallBlock call:
                    parts.Add("tool call " + call.ToolName + " " + call.ArgumentsJson);
                    break;
                case ToolResultBlock result:
                    parts.Add("tool result " + string.Join(" ", result.Content.OfType<TextBlock>()
                        .Select(text => text.Text)));
                    break;
                case ReasoningBlock reasoning when reasoning.VisibleText is not null:
                    parts.Add(reasoning.VisibleText);
                    break;
                case CitationBlock citation:
                    parts.Add(citation.Text + " " + citation.SourceRef);
                    break;
                case ProviderOpaqueBlock:
                    parts.Add("[estado opaco del provider]");
                    break;
            }
        }

        return message.Role.ToString() + ": " + string.Join(" ", parts);
    }

    private ArtifactRef PersistContextSnapshot(ContextSnapshot snapshot)
    {
        using var output = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("snapshotId", snapshot.SnapshotId);
            writer.WriteString("fingerprint", snapshot.Fingerprint.Hash());
            writer.WriteNumber("tokenCount", snapshot.TokenCount);
            writer.WriteBoolean("overflowed", snapshot.Overflowed);
            writer.WriteStartArray("items");
            foreach (var item in snapshot.Items)
            {
                writer.WriteStartObject();
                writer.WriteString("id", item.Id);
                writer.WriteString("kind", item.Kind.ToString());
                writer.WriteString("content", item.Content);
                writer.WriteNumber("tokens", item.EstimatedTokens);
                WriteProvenance(writer, item.Provenance);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("diagnostics");
            foreach (var diagnostic in snapshot.Diagnostics)
            {
                writer.WriteStartObject();
                writer.WriteString("itemId", diagnostic.ItemId);
                writer.WriteString("decision", diagnostic.Decision.ToString());
                writer.WriteNumber("tokens", diagnostic.Tokens);
                WriteProvenance(writer, diagnostic.Provenance);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var content = System.Text.Encoding.UTF8.GetString(output.ToArray());
        return _artifacts.PutText(_redaction.Redact(content), "application/json",
            ArtifactKind.ContextSnapshot, Sensitivity.Sensitive);
    }

    private static void WriteProvenance(System.Text.Json.Utf8JsonWriter writer, ContextProvenance provenance)
    {
        writer.WriteString("contributor", provenance.ContributorId);
        writer.WriteString("category", provenance.Category.ToString());
        writer.WriteString("source", provenance.ComponentSource);
        writer.WriteString("scope", provenance.Scope.ToString());
        writer.WriteBoolean("sensitive", provenance.Sensitive);
    }

    private sealed class PreparedTurnContext
    {
        public ContextSnapshot Snapshot { get; }
        public IReadOnlyList<ModelMessage> Messages { get; }

        public PreparedTurnContext(ContextSnapshot snapshot, IReadOnlyList<ModelMessage> messages)
        {
            Snapshot = snapshot;
            Messages = messages;
        }
    }

    private sealed class RedactingContextContributor : IContextContributor
    {
        private readonly IContextContributor _inner;
        private readonly RedactionPolicy _redaction;

        public RedactingContextContributor(IContextContributor inner, RedactionPolicy redaction)
        {
            _inner = inner;
            _redaction = redaction;
        }

        public async Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
            CancellationToken cancellationToken)
        {
            var items = await _inner.GetContextAsync(request, cancellationToken).ConfigureAwait(false);
            var result = new List<ContextItem>();
            foreach (var item in items)
            {
                var content = item.Provenance.Sensitive
                    ? "[contenido sensible omitido]"
                    : _redaction.Redact(item.Content);
                result.Add(new ContextItem(item.Id, item.Kind, content, item.EstimatedTokens, item.Priority,
                    item.Retention, item.Provenance, item.PreserveWhenTrimming));
            }

            return result.ToArray();
        }
    }

    private void EnsureRunAwaitingInput(EventStream stream, RunId runId, LaneId laneId)
    {
        var started = false;
        var state = RunState.Created;
        foreach (var evt in stream.EventsSince(1))
        {
            var type = evt.Type.ToString();
            if (type != "run.created" && type != "run.started" && type != "run.awaiting_input"
                && type != "user_input.received" && type != "run.validation_started"
                && type != "run.validation_rejected" && type != "run.completed"
                && type != "run.failed" && type != "run.cancelled") continue;

            var payload = _codecs.Decode(evt);
            var eventRun = payload switch
            {
                RunCreated e => e.RunId,
                RunStarted e => e.RunId,
                RunAwaitingInput e => e.RunId,
                UserInputReceived e => e.RunId,
                RunValidationStarted e => e.RunId,
                RunValidationRejected e => e.RunId,
                RunCompleted e => e.RunId,
                RunFailed e => e.RunId,
                RunCancelled e => e.RunId,
                _ => null,
            };
            if (eventRun is null || !eventRun.ToString().Equals(runId.ToString(), StringComparison.Ordinal))
                continue;
            if (payload is RunCreated)
            {
                state = StateMachines.ApplyRun(state, payload);
                continue;
            }
            if (payload is RunStarted) started = true;
            if (!started) continue;
            state = StateMachines.ApplyRun(state, payload);
        }

        // Los tests de unidad pueden ejecutar un turno sin Run persistido. En producción,
        // RunStarted deja Running; la nueva pregunta exige Running → AwaitingInput → Running.
        if (!started) return;
        if (state == RunState.Running)
            stream.Append(new RunAwaitingInput(runId, laneId));
        else if (state != RunState.AwaitingInput)
            throw new InvalidOperationException("El Run no admite input en estado " + state);
    }

    private IReadOnlyList<ToolDefinition> VisibleTools()
    {
        var all = _catalog.Definitions();
        var visible = new List<ToolDefinition>();
        foreach (var tool in all)
        {
            // ToolPlanner (ADR-0044 §5.1): la frontera oculta las tools fuera del techo de la
            // categoría. La política efectiva del boundary ya está intersectada con el harness.
            if (_boundary is not null && !_boundary!.IsToolVisible(tool.Name))
            {
                continue;
            }

            var maxVisible = _boundary is not null
                ? _boundary!.MaxVisibleTools()
                : (_harness?.MaxVisibleTools ?? Int32.MaxValue);
            if (visible.Count >= maxVisible)
            {
                break;
            }

            visible.Add(tool);
        }

        return visible;
    }

    /// <summary>
    /// Registra en el audit (ADR-0043) la política efectiva aplicada al Turn — solo hash,
    /// categoría y revisión (nunca contenido). ADR-0044 §8: el fingerprint del Turn registra el
    /// hash/revisión de la política; aquí se añade la huella auditable de la misma.
    /// Un fallo del audit no rompe el turno.
    /// </summary>
    private void AuditPolicy(SessionId sessionId, RunId runId, TurnId turnId, StopReason stop)
    {
        try
        {
            var details = new Dictionary<string, string>();
            // El fingerprint ya se construyó con el hash de la política en ModelPolicyHash.
            details["modelPolicyHash"] = _fingerprint.ModelPolicyHash;
            details["stop"] = stop.ToString();
            _audit.Record(new AuditRecord("turn.policy", null, sessionId, runId, DateTimeOffset.Now,
                turnId.ToString(), details), CancellationToken.None);
        }
        catch (Exception)
        {
            // un fallo del audit no rompe el turno
        }
    }

    private static TokenUsage CombineUsage(TokenUsage a, TokenUsage b) =>
        new TokenUsage(a.Input + b.Input, a.Output + b.Output, a.CacheRead + b.CacheRead,
            a.CacheWrite + b.CacheWrite, a.Reasoning + b.Reasoning);

    private static bool HasWorkingStateContributor(List<OmniCore.Context.IContextContributor> contributors)
    {
        foreach (var c in contributors)
        {
            if (c is WorkingStateContributor)
            {
                return true;
            }
        }

        return false;
    }
}
