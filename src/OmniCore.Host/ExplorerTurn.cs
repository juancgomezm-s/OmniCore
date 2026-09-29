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
/// contexto hace overflow (WorkingState pinned mayor al presupuesto) termina con
/// StopReason.ContextOverflow. SIN cliente interactivo → las Ask se deniegan (ADR-0003).
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

    // Límites de sesión/día (ADR-0037 §7): $5 por sesión, $20 por día — capa sobre el guard del Run.
    private readonly decimal _sessionCapUsd = 5m;

    private readonly decimal _dailyCapUsd = 20m;

    private decimal _sessionCostUsd;

    private decimal _dailyCostUsd;

    private readonly string _dailyKey;

    public ExplorerTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete, IToolExecutor tools,
        FakeCatalog catalog, ContextMaterializer materializer, ExecutionFingerprint fingerprint,
        ModelSelection selection, IEventStore store, IEventCodecRegistry codecs, IArtifactStore artifacts,
        IAuditSink audit, RedactionPolicy redaction, HarnessPolicy? harness = null,
        ModelCapabilityBoundary? boundary = null)
    {
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
        _dailyKey = DateTimeOffset.Now.ToString("yyyy-MM-dd");
    }

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
            boundary)
    {
    }

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

        var messages = LoadConversation(stream);
        if (question is not null && question.Length > 0)
        {
            messages.Add(new ModelMessage(MessageRole.User, new ContentBlock[] { new TextBlock(question) }));
        }

        var allToolCalls = new List<ToolUseTrace>();
        var usage = new TokenUsage(0, 0, 0, 0, 0);

        var budget = ReadRunBudget(stream, runId);
        var guard = new SpendGuard(budget);
        var steps = 1;
        var started = false;

        try
        {
            var contributors = new List<OmniCore.Context.IContextContributor>();
            var injected = _materializer.Contributors();
            foreach (var c in injected)
            {
                contributors.Add(c);
            }

            var hasWorkingState = HasWorkingStateContributor(contributors);
            if (workingStateText is not null && workingStateText.Length > 0 && !hasWorkingState)
            {
                contributors.Add(new WorkingStateContributor(workingStateText!));
            }

            var turnMaterializer = new ContextMaterializer(_materializer.Counter(), contributors);
            var materialized = turnMaterializer.MaterializeWithinBudget(
                new MaterializeRequest(sessionId, runId, null, laneId, turnId, 0L,
                    _fingerprint), cancellationToken, (int) _selection.ContextBudget);
            var contextText = RenderContext(materialized);

            // ContextOverflow: el WorkingState pinned (u otro item crítico) no cabe ni truncado.
            if (materialized.Overflowed)
            {
                stream.Append(new TurnStarted(turnId, laneId));
                started = true;
                // La state machine de Turn: Started → … → Abandoned (terminal). NUNCA se emite
                // TurnCompleted tras Abandoned (P1: transición inválida).
                stream.Append(new TurnAbandoned(turnId, "ContextOverflow: el contexto no entra en el presupuesto"));
                return new TurnResult("ContextOverflow: el contexto no cabe en el presupuesto del modelo",
                    StopReason.ContextOverflow, 0, usage, allToolCalls.ToArray(), null);
            }

            EnsureRunAwaitingInput(stream, runId, laneId);
            var encodedInput = System.Text.Json.JsonEncodedText.Encode(_redaction.Redact(question ?? ""));
            stream.Append(new UserInputReceived(runId, "\"" + encodedInput + "\"", null));
            stream.Append(new TurnStarted(turnId, laneId));
            started = true;

            string? finalText = null;
            var stop = StopReason.EndTurn;
            for (var step = 0; step < MaxSteps; step++)
            {
                steps = step + 1;
                var request = new ModelRequest(
                    _selection,
                    messages.ToArray(),
                    instruction is not null ? instruction!.Replace("{context}", contextText) : contextText,
                    VisibleTools(),
                    ToolChoice.Auto(),
                    null, null, new CacheHints(4, "automatic"), null);

                ModelResponse resolved;
                try
                {
                    resolved = _complete(request, cancellationToken);
                    guard.AdvanceTurn(resolved.Usage.Input + resolved.Usage.Output);
                    // Costo real registrado (rate por token local; ADR-0037 §7): el guard
                    // corta si excede MaxCostUsd del Run y emite InteractionRequested.
                    var stepCost = EstimateCostUsd(resolved.Usage);
                    guard.AddCostUsd(stepCost);
                    ValidateSessionDaily(stepCost);
                }
                catch (BudgetExceededException budgetEx)
                {
                    // Una SOLA emisión de la interacción de presupuesto con las opciones de
                    // ADR-0037 (Continuar hasta +X / Detener). ValidateSessionDaily solo lanza.
                    EmitBudgetExceeded(stream, turnId, budgetEx.Detail);
                    stop = StopReason.Cancelled;
                    finalText = "Presupuesto agotado: " + budgetEx.Detail;
                    break;
                }

                usage = CombineUsage(usage, resolved.Usage);

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
                var artifact = _artifacts.PutText(_redaction.Redact(finalText!), "text/plain",
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
            details["costUsd"] = EstimateCostUsd(usage).ToString();
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
    private void ValidateSessionDaily(decimal stepCost)
    {
        _sessionCostUsd += stepCost;
        _dailyCostUsd += stepCost;
        if (_sessionCostUsd > _sessionCapUsd)
        {
            throw new OmniCore.Engine.BudgetExceededException("límite de sesión ($" + _sessionCapUsd + ")");
        }

        if (_dailyCostUsd > _dailyCapUsd)
        {
            throw new OmniCore.Engine.BudgetExceededException("límite diario ($" + _dailyCapUsd + ")");
        }
    }

    /// <summary>Renderiza el snapshot materializado como texto para el system prompt.</summary>
    public static string RenderContext(ContextSnapshot snapshot)
    {
        var parts = new List<string>();
        parts.Add("Contexto del workspace (fuentes del run):");
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
            else if (item.Kind == ContextItemKind.ToolResult && item.Content.Length > 0)
            {
                parts.Add("- resultado: " + item.Content.Replace("\n", " "));
            }
        }

        parts.Add("Fingerprint: " + snapshot.Fingerprint.ModelKey + " · " + snapshot.Fingerprint.ContextPolicyHash);
        return string.Join("\n", parts.ToArray());
    }

    private List<ModelMessage> LoadConversation(EventStream stream)
    {
        var history = new List<ModelMessage>();
        foreach (var evt in stream.EventsSince(1))
        {
            if (evt.Type.ToString() == "user_input.received")
            {
                var input = _codecs.Decode(evt) as UserInputReceived;
                if (input is null) continue;
                try
                {
                    using var parsed = System.Text.Json.JsonDocument.Parse(input.InputPartsJson);
                    var text = parsed.RootElement.GetString();
                    if (!string.IsNullOrEmpty(text))
                        history.Add(new ModelMessage(MessageRole.User,
                            new ContentBlock[] { new TextBlock(text) }));
                }
                catch (System.Text.Json.JsonException) { }
            }
            else if (evt.Type.ToString() == "model.completed")
            {
                var completed = _codecs.Decode(evt) as ModelCompleted;
                if (completed?.ResponseArtifact is null) continue;
                var text = _artifacts.GetText(completed.ResponseArtifact.Hash);
                if (!string.IsNullOrEmpty(text))
                    history.Add(new ModelMessage(MessageRole.Assistant,
                        new ContentBlock[] { new TextBlock(text) }));
            }
        }

        // M2 conserva el primer input de la sesión y la ventana más reciente. La política de
        // recorte por tokens exactos y compaction llega en M4.
        if (history.Count <= 8) return history;
        var window = new List<ModelMessage> { history[0] };
        window.AddRange(history.Skip(history.Count - 7));
        return window;
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

    /// <summary>Costo estimado en USD de un uso (rate por token local, determinista; ADR-0037 §7).</summary>
    internal static decimal EstimateCostUsd(TokenUsage usage)
    {
        // $0.002/1K input, $0.005/1K output (típico de un local 27B/user en la nube).
        return usage.Input / 1000m * 0.002m + usage.Output / 1000m * 0.005m;
    }

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
