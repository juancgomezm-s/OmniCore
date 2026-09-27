namespace OmniCore.Tools;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Tool Runtime: ejecuta el pipeline completo (ADR-0014 §1, INV-001, INV-002, INV-003):
/// validación de esquema → Prepare (puro) → Permission Engine → AuthorizedToolIntent →
/// ExecuteAsync. El modelo nunca ejecuta tools; expone el resultado por etapas vía callback
/// para que el motor persista los eventos (ADR-0036 §5).
/// </summary>
public sealed class ToolRuntime
{
    private readonly FakeCatalog _catalog;

    private readonly IPermissionPolicy _policy;

    private readonly Func<DomainEventPayload, VoidBox> _emit;

    public ToolRuntime(FakeCatalog catalog, IPermissionPolicy policy, Func<DomainEventPayload, VoidBox> emit)
    {
        _catalog = catalog;
        _policy = policy;
        _emit = emit;
    }

    /// <summary>Resultado de ejecutar o rechazar una raw tool call del modelo.</summary>
    public sealed class Outcome
    {
        public bool Succeeded { get; }

        public string? Summary { get; }

        public ToolCallState FinalState { get; }

        public EffectOutcome Effect { get; }

        public Outcome(bool succeeded, string? summary, ToolCallState finalState, EffectOutcome effect)
        {
            Succeeded = succeeded;
            Summary = summary;
            FinalState = finalState;
            Effect = effect;
        }
    }

    /// <summary>
    /// Pipeline completo para una raw tool call propuesta por el modelo.
    /// </summary>
    public Outcome Run(ValidatedToolCall validated, ToolPreparationContext prepContext,
        ToolExecutionContext execContext, bool userApprovesAsk, CancellationToken cancellationToken)
    {
        var tool = _catalog.Find(validated.ToolId);
        if (tool is null)
        {
            _emit(new ToolCallRejected(validated.ToolCallId, "tool no encontrada: " + validated.ToolId));
            return new Outcome(false, "tool no encontrada", ToolCallState.Rejected, EffectOutcome.None);
        }

        // 1. Prepare (puro, síncrono; ADR-0014 §3).
        var preparation = tool.Prepare(validated, prepContext);
        if (preparation is PreparationRejected rejected)
        {
            _emit(new ToolCallRejected(validated.ToolCallId, rejected.Reason));
            return new Outcome(false, rejected.Reason, ToolCallState.Rejected, EffectOutcome.None);
        }

        var intent = ((Prepared) preparation).Intent;

        // 2. Permission Engine (ADR-0037 §2): traza siempre; Ask según flujo de interacción.
        var decision = _policy.Evaluate(intent);
        _emit(new PermissionEvaluated(validated.ToolCallId, decision.Final, LayersToJson(decision), null));
        if (decision.Final == PermissionDecision.Deny)
        {
            _emit(new PermissionDenied(validated.ToolCallId, "deny por política"));
            return new Outcome(false, "denegado", ToolCallState.Rejected, EffectOutcome.None);
        }

        if (decision.Final == PermissionDecision.Ask)
        {
            var interactionId = InteractionId.New();
            _emit(new PermissionRequested(validated.ToolCallId, interactionId));
            // ADR-0034/0036 §5: el Ask abre un InteractionRequest con sujeto y opciones del
            // servidor. El default es Deny (más restrictiva, ADR-0034 §1).
            _emit(new InteractionRequested(
                interactionId,
                InteractionKind.Permission,
                SubjectJson(validated.ToolId.ToString()),
                OptionsJson(),
                "deny",
                null,
                null,
                null,
                null,
                0,
                1));
            if (!userApprovesAsk)
            {
                _emit(new PermissionDenied(validated.ToolCallId, "Sin cliente interactivo → Deny (ADR-0003)"));
                return new Outcome(false, "denegado (sin aprobación)", ToolCallState.Rejected, EffectOutcome.None);
            }

            _emit(new PermissionGranted(validated.ToolCallId, null, null));
            // INV-002: la política ya quedó evaluada con la aprobación humana; el runtime
            // NO vuelve a Evaluar (eso lo rompería: Authorize re-lanzaría Ask).
            try
            {
                var approved = _policy.AuthorizeApproved(intent, null);
                _emit(new ToolCallAuthorized(validated.ToolCallId));
                return Execute(validated, approved, intent, tool, execContext, cancellationToken);
            }
            catch (OmniCore.Domain.PermissionDeniedException ex)
            {
                _emit(new PermissionDenied(validated.ToolCallId, ex.Reason));
                return new Outcome(false, ex.Reason, ToolCallState.Rejected, EffectOutcome.None);
            }
        }

        // 3. Solo el Permission Engine construye el intent autorizado (INV-018).
        try
        {
            var authorized = _policy.Authorize(intent);
            _emit(new ToolCallAuthorized(validated.ToolCallId));
            return Execute(validated, authorized, intent, tool, execContext, cancellationToken);
        }
        catch (OmniCore.Domain.PermissionDeniedException ex)
        {
            _emit(new PermissionDenied(validated.ToolCallId, ex.Reason));
            return new Outcome(false, ex.Reason, ToolCallState.Rejected, EffectOutcome.None);
        }
    }

    private Outcome Execute(ValidatedToolCall call, IAuthorizedToolIntent authorized, ToolIntent intent, ITool tool,
        ToolExecutionContext execContext, CancellationToken cancellationToken)
    {
        // 4. Ejecución (Barrier si hay efecto; ADR-0002 §2, ADR-0004 §2).
        _emit(new ToolCallStarted(call.ToolCallId, intent.Effect));

        var resultTask = tool.ExecuteAsync(authorized, execContext, cancellationToken);
        var result = resultTask.Result;
        var effect = result.EffectOutcome;
        if (effect == EffectOutcome.Unknown)
        {
            _emit(new ToolCallFailed(call.ToolCallId, "efecto desconocido", effect));
            return new Outcome(false, result.Summary, ToolCallState.Failed, effect);
        }

        _emit(new ToolCallSucceeded(call.ToolCallId, "{\"summary\":\"" + Esc(result.Summary) + "\"}"));
        return new Outcome(true, result.Summary, ToolCallState.Succeeded, effect);
    }

    public static ToolRuntime For(FakeCatalog catalog, IPermissionPolicy policy,
        Func<DomainEventPayload, VoidBox> emit) => new ToolRuntime(catalog, policy, emit);

    /// <summary>
    /// Reconciliar un efecto tras un crash (ADR-0004 §4): la ToolCall fue Started pero su
    /// outcome no se persistió. Para una clase Reconcilable con ReconciliationSpec pre/post,
    /// la herramienta de simulación reporta si el efecto quedó aplicado (post-hash presente).
    /// Devuelve los eventos ToolCallEffectUnknown + ToolCallReconciled.
    /// </summary>
    public ToolCallState Reconcile(ToolCallId toolCallId, ToolIntent intent,
        Func<string, string?> currentHash, CancellationToken cancellationToken)
    {
        _emit(new ToolCallEffectUnknown(toolCallId, intent.Effect));
        if (intent.Reconciliation is null)
        {
            _emit(new ToolCallReconciled(toolCallId, ReconciliationOutcome.NotApplied, "sin spec de reconciliación"));
            return ToolCallState.Reconciled;
        }

        var post = intent.Reconciliation!.ExpectedPostHash;
        if (post is not null)
        {
            var current = currentHash(post!);
            if (current == post)
            {
                _emit(new ToolCallReconciled(toolCallId, ReconciliationOutcome.Applied, "post-hash coincide"));
                return ToolCallState.Reconciled;
            }

            _emit(new ToolCallReconciled(toolCallId, ReconciliationOutcome.NotApplied, "post-hash ausente"));
            return ToolCallState.Reconciled;
        }

        _emit(new ToolCallReconciled(toolCallId, ReconciliationOutcome.Conflict, "hash inesperado"));
        return ToolCallState.Reconciled;
    }

    private static string LayersToJson(PermissionDecisionRecord decision)
    {
        var parts = new List<string>();
        foreach (var layer in decision.Layers)
        {
            parts.Add("\"" + Esc(layer.Layer) + "\":\"" + layer.Decision + "\"");
        }

        return "{" + string.Join(",", parts.ToArray()) + "}";
    }

    /// <summary>Sujeto de la interacción (redactado; ADR-0034 §1, ADR-0018).</summary>
    private static string SubjectJson(string tool)
    {
        return "{\"operation\":\"" + Esc(tool) + "\",\"risk\":\"low\",\"reason\":\"permission.required\"}";
    }

    /// <summary>Opciones decididas por el servidor; el default (más restrictiva) es Deny.</summary>
    private static string OptionsJson()
    {
        return "[{\"id\":\"deny\",\"intent\":\"deny\"},{\"id\":\"allow_once\",\"intent\":\"allow\",\"lifetime\":\"once\"}]";
    }

    private static string Esc(string value) => value.Replace("\"", "\\\"").Replace("\n", "\\n");
}

/// <summary>Resultado no-null de los callbacks de emisión de eventos.</summary>
public sealed class VoidBox
{
    public static readonly VoidBox Instance = new VoidBox();
}