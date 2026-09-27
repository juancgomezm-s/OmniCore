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

    // Límites de sesión/día (ADR-0037 §7): $5 por sesión, $20 por día — capa sobre el guard del Run.
    private readonly decimal _sessionCapUsd = 5m;

    private readonly decimal _dailyCapUsd = 20m;

    private decimal _sessionCostUsd;

    private decimal _dailyCostUsd;

    private readonly string _dailyKey;

    public ExplorerTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete, IToolExecutor tools,
        FakeCatalog catalog, ContextMaterializer materializer, ExecutionFingerprint fingerprint,
        ModelSelection selection, IEventStore store, IEventCodecRegistry codecs, IArtifactStore artifacts,
        IAuditSink audit, RedactionPolicy redaction)
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
        _dailyKey = DateTimeOffset.Now.ToString("yyyy-MM-dd");
    }

    /// <summary>Constructor de conveniencia: en-memoria (tests, sin persistencia durable).</summary>
    public ExplorerTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete, IToolExecutor tools,
        FakeCatalog catalog, ContextMaterializer materializer, ExecutionFingerprint fingerprint,
        ModelSelection selection)
        : this(complete, tools, catalog, materializer, fingerprint, selection,
            new OmniCore.Infrastructure.InMemoryEventStore(),
            OmniCore.Infrastructure.EventCodecs.Create(),
            new OmniCore.Infrastructure.FileArtifactStore(
                System.IO.Path.GetTempPath() + "omnicore-artifacts-test"),
            new OmniCore.Infrastructure.InMemoryAuditSink(),
            new OmniCore.Domain.RedactionPolicy())
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

        var messages = new List<ModelMessage>();
        if (question is not null && question.Length > 0)
        {
            messages.Add(new ModelMessage(MessageRole.User, new ContentBlock[] { new TextBlock(question) }));
        }

        var allToolCalls = new List<ToolUseTrace>();
        var usage = new TokenUsage(0, 0, 0, 0, 0);

        var budget = ReadRunBudget(stream);
        var guard = new SpendGuard(budget);
        var steps = 1;

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
                stream.Append(new TurnAbandoned(turnId, "ContextOverflow: el contexto no entra en el presupuesto"));
                stream.Append(new TurnCompleted(turnId));
                return new TurnResult("ContextOverflow: el contexto no cabe en el presupuesto del modelo",
                    StopReason.ContextOverflow, 0, usage, allToolCalls.ToArray(), null);
            }

            stream.Append(new TurnStarted(turnId, laneId));

            string? finalText = null;
            var stop = StopReason.EndTurn;
            for (var step = 0; step < MaxSteps; step++)
            {
                steps = step + 1;
                var request = new ModelRequest(
                    _selection,
                    messages.ToArray(),
                    instruction is not null ? instruction!.Replace("{context}", contextText) : contextText,
                    _catalog.Definitions(),
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
                    ValidateSessionDaily(stepCost, stream, turnId);
                }
                catch (BudgetExceededException budgetEx)
                {
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
                    var outcome = _tools.ExecuteTool(validated, false, cancellationToken);

                    // Persistir los eventos del pipeline REAL (request/permission/auth/outcome).
                    foreach (var evt in outcome.Events)
                    {
                        stream.Append(evt);
                    }

                    // Redacción obligatoria del tool result antes de dárselo al modelo.
                    var content = outcome.Preview is not null && outcome.Preview!.Length > 0
                        ? _redaction.Redact(outcome.Preview!)
                        : (outcome.Summary is null ? "ok" : _redaction.Redact(outcome.Summary!));
                    var resultText = outcome.Succeeded ? content : "error: " + (outcome.Summary ?? "failed");
                    allToolCalls.Add(new ToolUseTrace(call.ToolName, outcome.Succeeded, outcome.Summary,
                        call.ArgumentsJson));

                    // plan.propose: aplicar la mutación con las proyecciones de ESTE Run.
                    if (call.ToolName == "plan.propose" && outcome.Succeeded
                        && outcome.Effect == EffectOutcome.Applied && outcome.Preview is not null)
                    {
                        ApplyPlanProposal(stream, sessionId, runId, laneId, outcome.Preview!);
                    }

                    var assistant = new ModelMessage(MessageRole.Assistant, new ContentBlock[] {
                        new ToolCallBlock(call.Id, call.ProviderCallId, call.ToolName, call.ArgumentsJson),
                    });
                    var toolResult = new ModelMessage(MessageRole.Tool, new ContentBlock[] {
                        new ToolResultBlock(call.Id, new ContentBlock[] { new TextBlock(resultText) }, !outcome.Succeeded),
                    });
                    messages.Add(assistant);
                    messages.Add(toolResult);
                }

                if (stop == StopReason.Cancelled)
                {
                    break;
                }
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
            return new TurnResult(finalText, stop, steps, usage, allToolCalls.ToArray(), artifactId);
        }
        catch (Exception ex)
        {
            try
            {
                stream.Append(new TurnAbandoned(turnId, "turn falló: " + _redaction.Redact(ex.Message ?? "")));
                stream.Append(new TurnCompleted(turnId));
            }
            catch (Exception)
            {
            }

            return new TurnResult(null, StopReason.Error, steps, usage, allToolCalls.ToArray(), null);
        }
    }

    /// <summary>Presupuesto del Turn desde el RunCreated del journal (el comando lo configura).</summary>
    private TaskBudget ReadRunBudget(EventStream stream)
    {
        var tail = stream.EventsSince(1);
        foreach (var evt in tail)
        {
            if (evt.Type.ToString().Equals("run.created", StringComparison.Ordinal))
            {
                var payload = _codecs.CodecFor(evt.Type).Decode(evt.Type, evt.PayloadJson);
                if (payload is RunCreated runCreated)
                {
                    return runCreated.Budget is null
                        ? new TaskBudget(null, null, null, null)
                        : runCreated.Budget!;
                }
            }
        }

        return new TaskBudget(null, null, null, null);
    }

    /// <summary>Aplica plan.propose contra las proyecciones del mismo Run (P0-6 + requisito 2).</summary>
    private void ApplyPlanProposal(EventStream stream, SessionId sessionId, RunId runId, LaneId laneId,
        string mutationJson)
    {
        var tail = stream.EventsSince(1);
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
            return;
        }

        var tasksProj = TaskGraphProjection.Replay(_codecs, tail);
        var lanesProj = LaneProjection.Replay(_codecs, tail);
        var result = new PlanService().Apply(planProj, tasksProj, lanesProj, plan!);
        if (!result.Accepted)
        {
            return;
        }

        foreach (var evt in result.Events)
        {
            stream.Append(evt);
        }
    }

    /// <summary>Emite InteractionRequested(BudgetExceeded) y el evento de uso al journal.</summary>
    private void EmitBudgetExceeded(EventStream stream, TurnId turnId, string detail)
    {
        var interactionId = InteractionId.New();
        stream.Append(new InteractionRequested(interactionId, InteractionKind.BudgetExceeded,
            "{\"detail\":\"presupuesto agotado\"}", "[{\"id\":\"deny\",\"intent\":\"deny\"}]", "deny",
            null, null, null, null, 0, 1));
    }

    /// <summary>
    /// Aplica los topes de sesión/día (ADR-0037 §7: 5/20 USD): si la sesión o el día superan el
    /// tope, emite InteractionRequested(BudgetExceeded) y aborta el Turn con Cancelled.
    /// </summary>
    private void ValidateSessionDaily(decimal stepCost, EventStream stream, TurnId turnId)
    {
        _sessionCostUsd += stepCost;
        _dailyCostUsd += stepCost;
        if (_sessionCostUsd > _sessionCapUsd)
        {
            EmitBudgetExceeded(stream, turnId, "límite de sesión ($" + _sessionCapUsd + ")");
            throw new OmniCore.Engine.BudgetExceededException("límite de sesión ($" + _sessionCapUsd + ")");
        }

        if (_dailyCostUsd > _dailyCapUsd)
        {
            EmitBudgetExceeded(stream, turnId, "límite diario ($" + _dailyCapUsd + ")");
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