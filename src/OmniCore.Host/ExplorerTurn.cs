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

    private readonly IModelProvider? _metaModelProvider;

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

    private readonly QuestionnaireInteractionService? _questionnaires;

    private readonly Func<InteractionId, QuestionnaireSchema, QuestionnaireAskOutcome?>? _questionnaireResponder;

    public ExplorerTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete, IToolExecutor tools,
        FakeCatalog catalog, ContextMaterializer materializer, ExecutionFingerprint fingerprint,
        ModelSelection selection, IEventStore store, IEventCodecRegistry codecs, IArtifactStore artifacts,
        IAuditSink audit, RedactionPolicy redaction, HarnessPolicy? harness = null,
        ModelCapabilityBoundary? boundary = null, ModelPricing? pricing = null,
        bool enforceDefaultSpendCaps = false, decimal sessionCapUsd = 5m, decimal dailyCapUsd = 20m,
        QuestionnaireInteractionService? questionnaires = null,
        Func<InteractionId, QuestionnaireSchema, QuestionnaireAskOutcome?>? questionnaireResponder = null,
        IModelProvider? metaModelProvider = null)
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
        _questionnaires = questionnaires;
        _questionnaireResponder = questionnaireResponder;
        _metaModelProvider = metaModelProvider;
    }

    /// <summary>Journal del Turn (tests: para abrir en él el Run al que pertenece el Turn).</summary>
    internal IEventStore JournalStore => _store;

    /// <summary>Ledger de mutaciones del Run activo, compartido con las tools del boundary.</summary>
    internal MutationLedger? MutationLedger => _boundary?.ReadRegistry().Ledger;

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

        public InteractionId? PendingInteractionId { get; }

        public TurnResult(string? finalText, StopReason stopReason, int steps, TokenUsage usage,
            IReadOnlyList<ToolUseTrace> toolCalls, string? responseArtifactId,
            InteractionId? pendingInteractionId = null)
        {
            FinalText = finalText;
            StopReason = stopReason;
            Steps = steps;
            Usage = usage;
            ToolCalls = toolCalls;
            ResponseArtifactId = responseArtifactId;
            PendingInteractionId = pendingInteractionId;
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
        LaneId laneId, string workingStateText, CancellationToken cancellationToken, string? origin = null)
    {
        var stream = new EventStream(_store, _codecs, sessionId);
        var resumedTurnId = FindOpenTurn(stream, runId);
        var isResume = resumedTurnId is not null;
        var turnId = resumedTurnId ?? TurnId.New();
        if (isResume && _questionnaires is not null
            && _questionnaires.Pending(sessionId).FirstOrDefault() is { } pending)
        {
            return new TurnResult(null, StopReason.InputRequired, 0,
                new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ToolUseTrace>(), null, pending.InteractionId);
        }
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var persistedSpend = ReadJournalSpend(stream, sessionId, runId, today, _enforceDefaultSpendCaps);

        var messages = LoadConversation(stream, runId);
        var safeQuestion = isResume ? "" : _redaction.Redact(question ?? "");
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
            var preparedContext = MaterializeTurnContext(stream, sessionId, runId, laneId, turnId,
                workingStateText, instruction, messages, cancellationToken);
            var materialized = preparedContext.Snapshot;
            var snapshotArtifact = PersistContextSnapshot(materialized, _selection.ContextBudget);

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

            if (!isResume)
            {
                EnsureRunAwaitingInput(stream, runId, laneId);
                var encodedInput = System.Text.Json.JsonEncodedText.Encode(safeQuestion);
                stream.Append(new UserInputReceived(runId, "\"" + encodedInput + "\"", null, origin));
                stream.Append(new TurnStarted(turnId, laneId, _fingerprint, snapshotArtifact));
                // Límites de mutación por Turn (ADR-0044 §5): un Turn nuevo reinicia el contador del
                // Turn; los totales del Run se conservan. Un Turn reanudado sigue con su contador.
                _boundary?.BeginTurn();
            }
            started = true;

            string? finalText = null;
            var stop = StopReason.EndTurn;
            // Estado opaco del adapter: local a esta Ask, sin interpretación ni persistencia.
            ProviderState? continuation = null;
            for (var step = 0; step < MaxSteps; step++)
            {
                steps = step + 1;
                if (step > 0)
                {
                    preparedContext = MaterializeTurnContext(stream, sessionId, runId, laneId, turnId,
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
                    null, null, new CacheHints(4, "automatic"), continuation);

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
                    continuation = resolved.State;
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
                        finalText = _redaction.Redact(text.Text);
                    }
                    else if (block is ToolCallBlock call)
                    {
                        toolBlocks.Add(new ToolCallBlock(call.Id, call.ProviderCallId, call.ToolName,
                            _redaction.Redact(call.ArgumentsJson)));
                    }
                }

                if (resolved.StopReason != StopReason.ToolUse || toolBlocks.Count == 0)
                {
                    stop = resolved.StopReason;
                    break;
                }

                // Una respuesta ToolUse conserva sus bloques y orden en un solo mensaje.
                // Se mantiene la redacción vigente; el replay opaco requiere una política aparte.
                messages.Add(RedactMessage(new ModelMessage(MessageRole.Assistant, resolved.Content.ToArray())));

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
                                // Para el modelo la tool no existe: la frontera de capacidad no
                                // la expone, así que el rechazo se tipa UNKNOWN_TOOL (spec §71).
                                new ToolCallRejected(call.Id, "tool no disponible para este modelo",
                                    ToolErrorCode.UnknownTool)
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

                    QuestionnaireSchema? questionSchema = null;
                    InteractionId? questionInteractionId = null;
                    string? questionCallLink = null;
                    QuestionnaireInteractionService.PublishResult? questionPublication = null;
                    if (call.ToolName == "user.ask" && outcome.Succeeded && _questionnaires is not null)
                    {
                        questionSchema = QuestionnaireCodec.DecodeSchema(call.ArgumentsJson);
                        if (questionSchema is not null)
                        {
                            questionInteractionId = InteractionId.New();
                            questionCallLink = "{" + JsonObj.Field("toolCallId", call.Id.ToString()) + ","
                                + JsonObj.Field("providerCallId", call.ProviderCallId ?? call.Id.ToString()) + "}";
                            questionPublication = _questionnaires.PreparePublish(questionSchema!,
                                questionInteractionId!, laneId, questionCallLink);
                        }
                    }

                    // La publicación y la cadena de ToolCall se confirman juntas. El schema completo
                    // vive en el artifact; los eventos del ToolCall guardan solo su referencia.
                    var questionnaireArtifactId = questionPublication?.SchemaArtifact?.Hash.ToString() ?? "unavailable";
                    var toPersist = new List<DomainEventPayload>(outcome.Events.Count + planEvents.Count + 1);
                    foreach (var evt in outcome.Events)
                    {
                        if (planError is not null && evt is ToolCallSucceeded)
                        {
                            // La mutación (argumento de plan.propose) no identifica un item válido
                            // de este Run o el PlanService la rechaza: INVALID_ARGUMENTS (spec §71,
                            // familia "parche ambiguo") alimenta el repair loop del modelo.
                            toPersist.Add(new ToolCallFailed(call.Id, _redaction.Redact(planError),
                                EffectOutcome.None, ToolErrorCode.InvalidArguments));
                        }
                        else if (call.ToolName == "user.ask" && evt is ToolCallRequested requested)
                        {
                            toPersist.Add(new ToolCallRequested(requested.ToolCallId, requested.ProviderCallId,
                                requested.ToolName, "{\"questionnaireArtifact\":\"" + questionnaireArtifactId + "\"}"));
                        }
                        else if (call.ToolName == "user.ask" && evt is ToolCallPrepared prepared)
                        {
                            toPersist.Add(new ToolCallPrepared(prepared.ToolCallId,
                                "{\"questionnaireArtifact\":\"" + questionnaireArtifactId + "\"}"));
                        }
                        else
                        {
                            toPersist.Add(evt);
                        }
                    }

                    toPersist.AddRange(planEvents);
                    if (questionPublication is { Published: true, RequestEvent: not null })
                    {
                        toPersist.Add(questionPublication.RequestEvent!);
                        if (RunProjection.Replay(sessionId, runId, _codecs,
                            _store.ReadFrom(sessionId, 1)).State == RunState.Running)
                            toPersist.Add(new RunAwaitingInput(runId, laneId));
                    }
                    var pendingPermission = outcome.PendingInteractionId;
                    if (pendingPermission is not null
                        && RunProjection.Replay(sessionId, runId, _codecs,
                            _store.ReadFrom(sessionId, 1)).State == RunState.Running)
                        toPersist.Add(new RunAwaitingInput(runId, laneId));
                    stream.AppendBatch(toPersist, DurabilityClass.Standard);

                    if (pendingPermission is { } permissionInteractionId)
                    {
                        allToolCalls.Add(new ToolUseTrace(call.ToolName, true, "awaiting input",
                            _redaction.Redact(call.ArgumentsJson)));
                        return new TurnResult(null, StopReason.InputRequired, steps, usage,
                            allToolCalls.ToArray(), null, permissionInteractionId);
                    }

                    if (call.ToolName == "user.ask" && outcome.Succeeded)
                    {
                        if (_questionnaires is null || questionSchema is null
                            || questionInteractionId is null || questionPublication is not { Published: true })
                        {
                            finalText = "error: user.ask fue rechazado por el Host";
                            stop = StopReason.Error;
                            break;
                        }

                        var schema = questionSchema;
                        var interactionId = questionInteractionId!;
                        var callLink = questionCallLink!;
                        QuestionnaireAskOutcome? answer = _questionnaireResponder?.Invoke(interactionId, schema!);
                        if (answer is null || answer.IsInputRequired)
                        {
                            allToolCalls.Add(new ToolUseTrace(call.ToolName, true, "awaiting input",
                                _redaction.Redact(call.ArgumentsJson)));
                            return new TurnResult(null, StopReason.InputRequired, steps, usage,
                                allToolCalls.ToArray(), null, interactionId);
                        }

                        var persistedAnswer = _questionnaires.ResolvedOutcome(sessionId, interactionId);
                        if (persistedAnswer is null)
                        {
                            var projection = RunProjection.Replay(sessionId, runId, _codecs,
                                _store.ReadFrom(sessionId, 1));
                            DomainEventPayload? runTransition = projection.State == RunState.AwaitingInput
                                ? new UserInputReceived(runId, InputParts("QuestionnaireResponse:" + interactionId), null,
                                    "InteractionResponse(Questionnaire)")
                                : null;
                            var resolution = _questionnaires.Resolve(stream, interactionId,
                                answer.Answers ?? Array.Empty<QuestionAnswer>(), answer.Cancelled, callLink,
                                runTransition);
                            if (!resolution.Accepted)
                            {
                                allToolCalls.Add(new ToolUseTrace(call.ToolName, true, "answer rejected",
                                    _redaction.Redact(call.ArgumentsJson)));
                                return new TurnResult(null, StopReason.InputRequired, steps, usage,
                                    allToolCalls.ToArray(), null, interactionId);
                            }
                            else
                            {
                                answer = _questionnaires.ResolvedOutcome(sessionId, interactionId);
                            }
                        }
                        else
                        {
                            answer = persistedAnswer;
                        }

                        var answerJson = EncodeQuestionnaireOutcome(answer!);
                        var askResult = new ModelMessage(MessageRole.Tool, new ContentBlock[] {
                            new ToolResultBlock(call.Id, new ContentBlock[] { new TextBlock(answerJson) }, answer!.IsInvalid),
                        });
                        messages.Add(askResult);
                        allToolCalls.Add(new ToolUseTrace(call.ToolName, !answer!.IsInvalid,
                            answer.Status, _redaction.Redact(call.ArgumentsJson)));
                        continue;
                    }

                    // Redacción obligatoria del tool result antes de dárselo al modelo.
                    var content = outcome.Preview is not null && outcome.Preview!.Length > 0
                        ? _redaction.Redact(outcome.Preview!)
                        : (outcome.Summary is null ? "ok" : _redaction.Redact(outcome.Summary!));
                    var succeeded = outcome.Succeeded && planError is null;
                    var resultText = succeeded ? content : "error: " + _redaction.Redact(planError ?? outcome.Summary ?? "failed");
                    allToolCalls.Add(new ToolUseTrace(call.ToolName, succeeded,
                        _redaction.Redact(planError ?? outcome.Summary ?? ""),
                        _redaction.Redact(call.ArgumentsJson)));

                    var toolResult = new ModelMessage(MessageRole.Tool, new ContentBlock[] {
                        new ToolResultBlock(call.Id, new ContentBlock[] { new TextBlock(resultText) }, !succeeded),
                    });
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
            AuditSpend(sessionId, runId, laneId, turnId, stop, usage);
            AuditPolicy(sessionId, runId, turnId, stop);
            return new TurnResult(finalText is null ? null : _redaction.Redact(finalText), stop, steps,
                usage, allToolCalls.ToArray(), artifactId);
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
        StopReason stop, TokenUsage usage)
    {
        try
        {
            var details = new Dictionary<string, string>();
            details["stop"] = stop.ToString();
            details["inputTokens"] = usage.Input.ToString();
            details["outputTokens"] = usage.Output.ToString();
            var cost = _pricing?.CostUsd(usage);
            if (cost is not null) details["costUsd"] = cost.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
    private PersistedSpend ReadJournalSpend(EventStream stream, SessionId sessionId, RunId runId, string today,
        bool includeWorkspaceDaily)
    {
        decimal session = 0m, daily = 0m, run = 0m;
        var incomplete = false;
        IReadOnlyList<DomainEvent>? completions = null;
        if (includeWorkspaceDaily && _store is IWorkspaceJournalReader store)
        {
            try { completions = store.ReadEvents(EventType.Of("model.completed")); }
            catch (Exception)
            {
                // A failed cross-session scan cannot establish a safe daily total.
                incomplete = true;
            }
        }
        if (completions is null)
        {
            // Run-cost-only callers need no workspace-wide daily view. Default caps require it.
            if (includeWorkspaceDaily) incomplete = true;
            completions = stream.EventsSince(1)
                .Where(evt => evt.Type.ToString().Equals("model.completed", StringComparison.Ordinal)).ToArray();
        }

        foreach (var evt in completions)
        {
            ModelCompleted? completed;
            try { completed = _codecs.Decode(evt) as ModelCompleted; }
            catch (Exception ex) when (ex is System.Text.Json.JsonException
                or InvalidOperationException or FormatException or ArgumentException)
            {
                incomplete = true;
                continue;
            }
            if (completed?.ResponseArtifact is null) { incomplete = true; continue; }
            string? text;
            try { text = _artifacts.GetText(completed.ResponseArtifact.Hash); }
            catch (InvalidDataException)
            {
                incomplete = true;
                continue;
            }
            UsageEnvelope record;
            bool decoded;
            try { decoded = TryDecodeUsageEnvelope(text, out record); }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                incomplete = true;
                continue;
            }
            if (!decoded || record.CostUsd is null)
            {
                incomplete = true;
                continue;
            }
            if (!DateOnly.TryParseExact(record.Day, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _)
                || evt.RunId is null || record.RunId != evt.RunId.ToString())
            {
                // Invalid/mismatched metadata cannot safely exclude a record from today's or
                // this Run's total. Keep the known cost in broad totals and fail closed below.
                incomplete = true;
            }
            var cost = record.CostUsd.Value;
            if (record.Day == today) daily += cost;
            if (evt.SessionId == sessionId) session += cost;
            if (evt.SessionId == sessionId && evt.RunId == runId) run += cost;
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
                || item.Kind == ContextItemKind.Constraint || item.Kind == ContextItemKind.File
                || item.Kind == ContextItemKind.Summary || item.Kind == ContextItemKind.Checkpoint)
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
        var questionnaireCalls = new HashSet<string>(StringComparer.Ordinal);
        var questionnaireResults = new Dictionary<string, string>(StringComparer.Ordinal);
        var interactionCalls = new Dictionary<string, string>(StringComparer.Ordinal);
        var questionnaireArguments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var evt in stream.EventsSince(1))
        {
            var payload = _codecs.Decode(evt);
            if (payload is InteractionRequested request && request.Kind == InteractionKind.Question
                && request.ToolCallJson is not null)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(request.ToolCallJson);
                    if (doc.RootElement.TryGetProperty("toolCallId", out var id))
                    {
                        var callId = id.GetString();
                        if (!string.IsNullOrEmpty(callId))
                        {
                            questionnaireCalls.Add(callId!);
                            interactionCalls[request.InteractionId.ToString()] = callId!;
                            if (request.QuestionnaireSchemaRef is not null
                                && _artifacts.GetText(request.QuestionnaireSchemaRef.Hash) is { } schemaJson)
                                questionnaireArguments[callId!] = schemaJson;
                        }
                    }
                }
                catch (System.Text.Json.JsonException) { }
            }
            else if (payload is InteractionResolved resolved && resolved.IsQuestionnaire
                && resolved.AnswerRef is not null
                && interactionCalls.TryGetValue(resolved.InteractionId.ToString(), out var callId))
            {
                var answerJson = _artifacts.GetText(resolved.AnswerRef!.Hash);
                if (answerJson is not null)
                {
                    var outcome = resolved.State == "cancelled"
                        ? "{\"status\":\"cancelled\",\"cancelled\":true}"
                        : "{\"status\":\"answered\",\"answers\":" + answerJson + "}";
                    questionnaireResults[callId!] = outcome;
                }
            }
        }

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
                if (input is null || input.Origin == "InteractionResponse(Questionnaire)") continue;
                try
                {
                    using var parsed = System.Text.Json.JsonDocument.Parse(input.InputPartsJson);
                    var root = parsed.RootElement;
                    var parts = root.ValueKind == System.Text.Json.JsonValueKind.Array
                        ? root.EnumerateArray()
                            .Where(static part => part.ValueKind == System.Text.Json.JsonValueKind.String)
                            .Select(static part => part.GetString() ?? "")
                        : root.ValueKind == System.Text.Json.JsonValueKind.String
                            ? new[] { root.GetString() ?? "" }
                            : Array.Empty<string>();
                    var text = string.Join("\n", parts.Where(static part => part.Length > 0));
                    if (text.Length > 0)
                        history.Add(new ModelMessage(MessageRole.User,
                            new ContentBlock[] { new TextBlock(_redaction.Redact(text)) }));
                }
                catch (System.Text.Json.JsonException) { }
            }
            else if (type == "toolcall.requested")
            {
                var call = _codecs.Decode(evt) as ToolCallRequested;
                if (call is not null)
                {
                    var arguments = questionnaireArguments.TryGetValue(call.ToolCallId.ToString(), out var schemaJson)
                        ? schemaJson : call.ArgumentsJson;
                    history.Add(new ModelMessage(MessageRole.Assistant, new ContentBlock[] {
                        new ToolCallBlock(call.ToolCallId, call.ProviderCallId, call.ToolName,
                            _redaction.Redact(arguments))
                    }));
                }
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
                var isQuestionnaireCall = questionnaireCalls.Contains(callId!.ToString());
                if (isQuestionnaireCall && !questionnaireResults.TryGetValue(callId!.ToString(), out _))
                {
                    // El turno está suspendido: la tool call todavía no tiene resultado para el modelo.
                    continue;
                }
                var content = isQuestionnaireCall
                    ? questionnaireResults[callId!.ToString()]
                    : payload switch
                    {
                        ToolCallSucceeded e => _redaction.Redact(e.ResultJson),
                        ToolCallFailed e => "error: " + _redaction.Redact(e.Cause),
                        ToolCallRejected e => "error: " + _redaction.Redact(e.Reason),
                        _ => "error",
                    };
                history.Add(new ModelMessage(MessageRole.Tool, new ContentBlock[] {
                    new ToolResultBlock(callId!, new ContentBlock[] { new TextBlock(content) },
                        isQuestionnaireCall ? content.Contains("\"status\":\"invalid\"", StringComparison.Ordinal)
                            : payload is not ToolCallSucceeded)
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

    private TurnId? FindOpenTurn(EventStream stream, RunId runId)
    {
        TurnId? open = null;
        foreach (var evt in stream.EventsSince(1))
        {
            if (evt.RunId is null || !evt.RunId.Equals(runId)) continue;
            var payload = _codecs.Decode(evt);
            switch (payload)
            {
                case TurnStarted started:
                    open = started.TurnId;
                    break;
                case TurnCompleted completed when open?.Equals(completed.TurnId) == true:
                case TurnAbandoned abandoned when open?.Equals(abandoned.TurnId) == true:
                case TurnInterrupted interrupted when open?.Equals(interrupted.TurnId) == true:
                    open = null;
                    break;
            }
        }
        return open;
    }

    private static string InputParts(string text) => "[\"" + JsonObj.Escape(text) + "\"]";

    private static string EncodeQuestionnaireOutcome(QuestionnaireAskOutcome outcome)
    {
        if (outcome.Cancelled) return "{\"status\":\"cancelled\",\"cancelled\":true}";
        if (outcome.IsInvalid)
        {
            var errors = new List<string>();
            foreach (var error in outcome.Errors ?? Array.Empty<QuestionnaireError>())
                errors.Add("{\"code\":\"" + JsonObj.Escape(error.Code.ToString())
                    + "\",\"questionId\":"
                    + (error.QuestionId is null ? "null" : "\"" + JsonObj.Escape(error.QuestionId) + "\"") + "}");
            return "{\"status\":\"invalid\",\"errors\":[" + string.Join(",", errors) + "]}";
        }
        return "{\"status\":\"answered\",\"answers\":"
            + QuestionnaireCodec.EncodeAnswers(outcome.Answers ?? Array.Empty<QuestionAnswer>()) + "}";
    }

    private PreparedTurnContext MaterializeTurnContext(EventStream stream, SessionId sessionId, RunId runId,
        LaneId laneId, TurnId turnId, string workingStateText, string instruction,
        IReadOnlyList<ModelMessage> messages, CancellationToken cancellationToken)
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

        var policy = _harness?.ContextManagement ?? ContextManagementPolicy.Default;
        var priorCheckpoint = ReadLatestCheckpoint(stream, runId);
        var compactedThroughIndex = priorCheckpoint?.CompactedThroughItemIndex ?? -1;
        var compactedIds = new HashSet<string>(entries.Where(e => !e.PreserveWhenTrimming
                && ConversationIndex(e.Id) is var index && index >= 0 && index <= compactedThroughIndex)
            .Select(e => e.Id), StringComparer.Ordinal);
        var candidates = entries.Where(e => IsConversationKind(e.Kind) && !e.PreserveWhenTrimming).ToArray();
        var oldCount = Math.Max(0, candidates.Length - Math.Max(0, policy.RecentTailItems));
        var oldItems = candidates.Take(oldCount).ToArray();
        var newOldItems = oldItems.Where(e => !compactedIds.Contains(e.Id)).ToArray();
        if (ContextCompaction.ShouldCompact(newOldItems.Length, policy))
        {
            var material = (priorCheckpoint?.Summary is { Length: > 0 } oldSummary
                    ? "Previous checkpoint:\\n" + oldSummary + "\\n\\nNew older history:\\n" : "")
                + string.Join("\\n", newOldItems.Select(e => e.Content));
            string summary;
            var metaFingerprint = "deterministic-v1";
            MetaModelService? metaModel = null;
            if (_metaModelProvider is not null)
            {
                metaModel = new MetaModelService(_metaModelProvider, _artifacts,
                    new ContextStreamEventSink(stream), _selection, _redaction.Redact);
                try
                {
                    summary = metaModel.SummarizeAsync(runId, "CompressContext", material,
                        policy.MaxCheckpointCharacters, cancellationToken).GetAwaiter().GetResult();
                    metaFingerprint = metaModel.Fingerprint("CompressContext");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    summary = DeterministicFallbackSummary(material, policy.MaxCheckpointCharacters);
                    metaFingerprint = "deterministic-fallback-v1";
                }
            }
            else summary = DeterministicFallbackSummary(material, policy.MaxCheckpointCharacters);

            foreach (var entry in oldItems) compactedIds.Add(entry.Id);
            var checkpointId = Guid.NewGuid().ToString("N");
            compactedThroughIndex = compactedIds.Select(ConversationIndex).DefaultIfEmpty(-1).Max();
            var throughSequence = _store.CurrentSequence(sessionId);
            var checkpointArtifact = PersistCheckpointArtifact(checkpointId, runId, throughSequence,
                compactedThroughIndex, summary, metaFingerprint);
            stream.Append(new ContextCheckpointRecorded(checkpointId, runId, throughSequence,
                checkpointArtifact, metaFingerprint));
            using var checkpointDocument = System.Text.Json.JsonDocument.Parse(
                _artifacts.GetText(checkpointArtifact.Hash)!);
            var checkpointContext = RenderCheckpointContext(checkpointDocument.RootElement);
            priorCheckpoint = new LoadedCheckpoint(checkpointId, throughSequence, compactedThroughIndex,
                checkpointContext, checkpointArtifact);
        }

        var filteredEntries = entries.Where(e => !compactedIds.Contains(e.Id)).ToArray();
        contributors.Add(new SessionConversationContributor(filteredEntries));
        if (priorCheckpoint is not null)
        {
            contributors.Add(new ContextCheckpointContributor(priorCheckpoint.Id,
                priorCheckpoint.ThroughSequence, priorCheckpoint.Summary, priorCheckpoint.Artifact));
        }
        var priorDiagnostics = entries.Where(e => compactedIds.Contains(e.Id)).Select(e => new ContextDiagnostic(e.Id,
            new ContextProvenance("session-conversation", ContributionCategory.Conversation, "engine",
                ScopeLevel.Session, false), ContextDecision.Compacted, e.Content.Length)).ToArray();
        var materializer = new ContextMaterializer(_materializer.Counter(), contributors, _artifacts, policy);
        var snapshot = materializer.MaterializeWithinBudget(
            new MaterializeRequest(sessionId, runId, null, laneId, turnId, _store.CurrentSequence(sessionId),
                _fingerprint, priorDiagnostics), cancellationToken, (int)_selection.ContextBudget);
        var selectedMessages = new List<ModelMessage>();
        foreach (var item in snapshot.Items)
        {
            if (messageById.TryGetValue(item.Id, out var message))
            {
                var diagnostic = snapshot.Diagnostics.FirstOrDefault(d => d.ItemId == item.Id);
                selectedMessages.Add(ProjectConversationMessage(message, item, diagnostic?.Decision ?? ContextDecision.Included,
                    policy.CompressBodyCharacters));
            }
        }

        return new PreparedTurnContext(snapshot, selectedMessages.ToArray());
    }

    private static ModelMessage ProjectConversationMessage(ModelMessage original, ContextItem item,
        ContextDecision decision, int compressionCharacters)
    {
        var externalized = item.Provenance.Refs?.Any(reference => reference.StartsWith("artifact=sha256:",
            StringComparison.Ordinal)) == true;
        if (externalized)
        {
            var blocks = original.Content.Select(block => block is ToolResultBlock result
                ? (ContentBlock)(result with { Content = new ContentBlock[] { new TextBlock(item.Content) } })
                : block is TextBlock ? new TextBlock(item.Content) : block).ToArray();
            return new ModelMessage(original.Role, blocks);
        }
        if (decision is not (ContextDecision.Compressed or ContextDecision.TruncatedByBudget)) return original;

        var charLimit = decision == ContextDecision.TruncatedByBudget
            ? Math.Max(32, item.EstimatedTokens * 4)
            : compressionCharacters;
        var blockLimit = Math.Max(32, charLimit / Math.Max(1, original.Content.Count));
        var compacted = new List<ContentBlock>();
        foreach (var block in original.Content)
        {
            switch (block)
            {
                case TextBlock text:
                    compacted.Add(new TextBlock(TrimBody(text.Text, blockLimit)));
                    break;
                case ToolCallBlock call:
                    var preview = System.Text.Json.JsonEncodedText.Encode(TrimBody(call.ArgumentsJson, blockLimit));
                    compacted.Add(new ToolCallBlock(call.Id, call.ProviderCallId, call.ToolName,
                        "{\"_omnicore_compressed\":true,\"preview\":\"" + preview + "\"}"));
                    break;
                case ToolResultBlock result:
                    compacted.Add(result with { Content = result.Content.Select(content => content is TextBlock text
                        ? (ContentBlock)new TextBlock(TrimBody(text.Text, blockLimit)) : content).ToArray() });
                    break;
                case CitationBlock citation:
                    compacted.Add(citation with { Text = TrimBody(citation.Text, blockLimit),
                        SourceRef = TrimBody(citation.SourceRef, blockLimit) });
                    break;
                case ReasoningBlock:
                    compacted.Add(new TextBlock("[older reasoning omitted]"));
                    break;
                case ProviderOpaqueBlock:
                    compacted.Add(new TextBlock("[provider state omitted]"));
                    break;
            }
        }
        return new ModelMessage(original.Role, compacted.ToArray());
    }

    private static string TrimBody(string content, int maxCharacters)
    {
        const string marker = "…[compressed]";
        if (content.Length <= maxCharacters) return content;
        var available = Math.Max(0, maxCharacters - marker.Length);
        return content[..Math.Min(available, content.Length)] + marker;
    }

    private sealed record LoadedCheckpoint(string Id, long ThroughSequence, int CompactedThroughItemIndex,
        string Summary, ArtifactRef Artifact);

    private LoadedCheckpoint? ReadLatestCheckpoint(EventStream stream, RunId runId)
    {
        ContextCheckpointRecorded? latest = null;
        foreach (var evt in stream.EventsSince(1))
        {
            if (_codecs.Decode(evt) is ContextCheckpointRecorded checkpoint && checkpoint.RunId.Equals(runId))
                latest = checkpoint;
        }
        if (latest is null || _artifacts.GetText(latest.CheckpointArtifact.Hash) is not { } json) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            var compactedThrough = root.GetProperty("compactedThroughItemIndex").GetInt32();
            return new LoadedCheckpoint(latest.CheckpointId, latest.ThroughEventSequence, compactedThrough,
                RenderCheckpointContext(root), latest.CheckpointArtifact);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string RenderCheckpointContext(System.Text.Json.JsonElement checkpoint)
    {
        var sections = new (string Property, string Label)[]
        {
            ("goals", "Goals"), ("constraints", "Constraints"), ("decisions", "Decisions"),
            ("facts", "Facts"), ("relevantFiles", "Relevant files"), ("modifiedFiles", "Modified files"),
            ("failedAttempts", "Failed attempts"), ("openQuestions", "Open questions"),
        };
        var parts = new List<string> { checkpoint.GetProperty("summary").GetString() ?? "" };
        foreach (var (property, label) in sections)
        {
            if (!checkpoint.TryGetProperty(property, out var values) || values.ValueKind != System.Text.Json.JsonValueKind.Array)
                continue;
            var entries = values.EnumerateArray().Select(value => value.GetString() ?? "")
                .Where(value => value.Length > 0).ToArray();
            if (entries.Length > 0) parts.Add(label + ": " + string.Join("; ", entries));
        }
        if (checkpoint.TryGetProperty("testState", out var tests)
            && tests.GetString() is { Length: > 0 } testState && testState != "unknown")
            parts.Add("Tests: " + testState);
        return string.Join("\\n", parts.Where(part => part.Length > 0));
    }

    private ArtifactRef PersistCheckpointArtifact(string checkpointId, RunId runId, long throughSequence,
        int compactedThroughItemIndex, string summary, string metaModelFingerprint)
    {
        var lines = summary.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string[] Section(params string[] labels) => lines.Where(line => labels.Any(label =>
                line.StartsWith(label + ":", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("- " + label + ":", StringComparison.OrdinalIgnoreCase)))
            .Select(line => line[(line.IndexOf(':') + 1)..].Trim()).Where(line => line.Length > 0).ToArray();
        var sectionLabels = new[] { "Goal", "Goals", "Constraint", "Constraints", "Decision", "Decisions",
            "Fact", "Facts", "Relevant file", "Modified file", "Failed attempt", "Pending", "Open question", "Tests" };
        var categorizedLines = lines.Where(line => sectionLabels.Any(label =>
            line.StartsWith(label + ":", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("- " + label + ":", StringComparison.OrdinalIgnoreCase))).ToArray();
        var facts = Section("Fact", "Facts");
        if (facts.Length == 0) facts = lines.Except(categorizedLines, StringComparer.Ordinal).ToArray();
        using var output = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("checkpointId", checkpointId);
            writer.WriteString("runId", runId.ToString());
            writer.WriteNumber("throughEventSequence", throughSequence);
            writer.WriteString("metaModelFingerprint", metaModelFingerprint);
            writer.WriteString("summary", _redaction.Redact(summary));
            WriteCheckpointSection(writer, "goals", Section("Goal", "Goals"));
            WriteCheckpointSection(writer, "constraints", Section("Constraint", "Constraints"));
            WriteCheckpointSection(writer, "decisions", Section("Decision", "Decisions"));
            WriteCheckpointSection(writer, "facts", facts);
            WriteCheckpointSection(writer, "relevantFiles", Section("Relevant file"));
            WriteCheckpointSection(writer, "modifiedFiles", Section("Modified file"));
            WriteCheckpointSection(writer, "failedAttempts", Section("Failed attempt"));
            writer.WriteString("testState", Section("Tests").FirstOrDefault() ?? "unknown");
            WriteCheckpointSection(writer, "pendingWork", Section("Pending"));
            WriteCheckpointSection(writer, "openQuestions", Section("Open question"));
            writer.WriteNumber("compactedThroughItemIndex", compactedThroughItemIndex);
            writer.WriteEndObject();
        }
        return _artifacts.PutText(_redaction.Redact(System.Text.Encoding.UTF8.GetString(output.ToArray())),
            "application/vnd.omnicore.context-checkpoint+json", ArtifactKind.ContextSnapshot, Sensitivity.Sensitive);
    }

    private static void WriteCheckpointSection(System.Text.Json.Utf8JsonWriter writer, string name,
        IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static string DeterministicFallbackSummary(string content, int maxCharacters)
    {
        if (content.Length <= maxCharacters) return content;
        if (maxCharacters < 64) return content[..maxCharacters];
        var half = (maxCharacters - 32) / 2;
        return content[..half] + "\\n[…older history omitted deterministically…]\\n" + content[^half..];
    }

    private static bool IsConversationKind(ContextItemKind kind) => kind is ContextItemKind.UserMessage
        or ContextItemKind.AssistantMessage or ContextItemKind.ToolResult;

    private static int ConversationIndex(string id)
    {
        const string prefix = "conversation-";
        return id.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(id.AsSpan(prefix.Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var index) ? index : -1;
    }

    private sealed class ContextStreamEventSink : IContextEventSink
    {
        private readonly EventStream _stream;
        public ContextStreamEventSink(EventStream stream) => _stream = stream;
        public ValueTask AppendAsync(DomainEventPayload payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _stream.Append(payload);
            return ValueTask.CompletedTask;
        }
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

    private ArtifactRef PersistContextSnapshot(ContextSnapshot snapshot, long tokenBudget)
    {
        using var output = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("snapshotId", snapshot.SnapshotId);
            writer.WriteString("fingerprint", snapshot.Fingerprint.Hash());
            writer.WriteString("snapshotFingerprint", snapshot.SnapshotFingerprint);
            writer.WriteNumber("tokenCount", snapshot.TokenCount);
            writer.WriteNumber("tokenBudget", tokenBudget);
            var accuracy = _materializer.Counter().Accuracy.ToString().ToLowerInvariant();
            writer.WriteString("tokenAccuracy", accuracy);
            writer.WriteBoolean("overflowed", snapshot.Overflowed);
            writer.WriteStartArray("items");
            foreach (var item in snapshot.Items)
            {
                writer.WriteStartObject();
                writer.WriteString("id", item.Id);
                writer.WriteString("kind", item.Kind.ToString());
                writer.WriteString("content", item.Content);
                writer.WriteNumber("tokens", item.EstimatedTokens);
                writer.WriteString("tokenAccuracy", accuracy);
                writer.WriteString("priority", item.Priority.ToString());
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
                writer.WriteString("reason", diagnostic.Decision switch
                {
                    ContextDecision.OmittedByBudget => "omitted by context budget",
                    ContextDecision.TruncatedByBudget => "trimmed by context budget",
                    ContextDecision.Pruned => "superseded context pruned",
                    ContextDecision.Externalized => "tool output stored in artifact",
                    ContextDecision.Compressed => "older conversation compressed deterministically",
                    ContextDecision.Compacted => "covered by context checkpoint",
                    _ => "included",
                });
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
        if (provenance.Refs is not null)
        {
            writer.WriteStartArray("refs");
            foreach (var reference in provenance.Refs) writer.WriteStringValue(reference);
            writer.WriteEndArray();
        }
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
