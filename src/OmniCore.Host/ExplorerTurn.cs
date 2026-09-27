namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Tools;

/// <summary>
/// Turn de Explorer end-to-end (ADR-0005 §2, ADR-0035 §3): conecta el contexto materializado
/// (ContextMaterializer + WorkingState del run + fingerprint) con el modelo, ejecuta las
/// tool calls que el modelo propone a través del pipeline REAL de tools y permisos
/// (IToolExecutor con ToolRuntime + Policy), y retroalimenta los resultados en el historial
/// hasta EndTurn o el máximo de pasos. SIN cliente interactivo: las tools que requieren Ask
/// se deniegan (ADR-0003) — este turno del CLI no puede abrir una interacción.
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

    public ExplorerTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete, IToolExecutor tools,
        FakeCatalog catalog, ContextMaterializer materializer, ExecutionFingerprint fingerprint,
        ModelSelection selection)
    {
        _complete = complete;
        _tools = tools;
        _catalog = catalog;
        _materializer = materializer;
        _fingerprint = fingerprint;
        _selection = selection;
    }

    public sealed class TurnResult
    {
        public string? FinalText { get; }

        public StopReason StopReason { get; }

        public int Steps { get; }

        public TokenUsage Usage { get; }

        public IReadOnlyList<ToolUseTrace> ToolCalls { get; }

        public TurnResult(string? finalText, StopReason stopReason, int steps, TokenUsage usage,
            IReadOnlyList<ToolUseTrace> toolCalls)
        {
            FinalText = finalText;
            StopReason = stopReason;
            Steps = steps;
            Usage = usage;
            ToolCalls = toolCalls;
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

    /// <summary>Ejecuta la pregunta del usuario contra el modelo con contexto y tools reales.</summary>
    public TurnResult Ask(string question, string instruction, SessionId sessionId, RunId runId,
        string workingStateText, CancellationToken cancellationToken)
    {
        var messages = new List<ModelMessage>();
        if (question is not null && question.Length > 0)
        {
            messages.Add(new ModelMessage(MessageRole.User, new ContentBlock[] { new TextBlock(question) }));
        }

        var allToolCalls = new List<ToolUseTrace>();
        var usage = new TokenUsage(0, 0, 0, 0, 0);

        // Presupuesto del turno (ADR-0037 §7): vigila tokens, turnos y tool calls; si se
        // excede, la interacción BudgetExceeded corta el turno (P1-11: ya no es aislado).
        var guard = new SpendGuard(new TaskBudget(null, 16L * 8192, 16, 24));

        // Contexto REAL: el materializer del turno combina los contributors del Host con el
        // WorkingState del run (P0-2: el estado anunciado llega al prompt, no queda en
        // BasedOnEventSequence).
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
            new MaterializeRequest(sessionId, runId, null, null, null, 0L,
                _fingerprint), cancellationToken, (int) _selection.ContextBudget);
        var contextText = RenderContext(materialized);

        string? finalText = null;
        var stop = StopReason.EndTurn;
        var steps = 1;
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
            }
            catch (BudgetExceededException budgetEx)
            {
                // Tope de gasto (ADR-0037 §7 / ADR-0034): el turno corta con un texto
                // claro; la interacción BudgetExceeded quedó registrada como stop.
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

            // El modelo pidió tools: ejecutar por el pipeline real y continuar. SIN cliente
            // interactivo en este turno del CLI → las Ask se deniegan (ADR-0003, INV-002).
            foreach (ToolCallBlock call in toolBlocks)
            {
                try
                {
                    guard.RecordToolCall();
                }
                catch (BudgetExceededException budgetEx)
                {
                    stop = StopReason.Cancelled;
                    finalText = "Presupuesto agotado: " + budgetEx.Detail;
                    break;
                }

                var validated = new ValidatedToolCall(call.Id, new ToolId(call.ToolName),
                    call.ProviderCallId ?? call.Id.ToString(), call.ArgumentsJson);
                var outcome = _tools.ExecuteTool(validated, false, cancellationToken);
                allToolCalls.Add(new ToolUseTrace(call.ToolName, outcome.Succeeded, outcome.Summary,
                    call.ArgumentsJson));

                // El contenido real de la tool (Preview) es lo que el modelo observa
                // (p. ej. el archivo leído por filesystem.read): no solo el summary.
                var content = outcome.Preview is not null && outcome.Preview!.Length > 0
                    ? outcome.Preview!
                    : (outcome.Summary ?? "ok");
                var resultText = outcome.Succeeded ? content : "error: " + (outcome.Summary ?? "failed");
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

        return new TurnResult(finalText, stop, steps, usage, allToolCalls.ToArray());
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

    /// <summary>True si alguno de los contributors ya aporta el WorkingState del run.</summary>
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