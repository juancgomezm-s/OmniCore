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

    private readonly ModelCapabilityBoundary? _boundary;

    public ToolRuntime(FakeCatalog catalog, IPermissionPolicy policy, Func<DomainEventPayload, VoidBox> emit)
        : this(catalog, policy, emit, null)
    {
    }

    /// <summary>
    /// Frontera de capacidad opcional (ADR-0044 §5): restringe, nunca autoriza. Se evalúa tras
    /// Prepare (antes del Permission Engine) y de nuevo antes de ejecutar. Con null el pipeline
    /// es idéntico al de M2: la frontera es un añadido componible, no un cambio de semántica.
    /// </summary>
    public ToolRuntime(FakeCatalog catalog, IPermissionPolicy policy, Func<DomainEventPayload, VoidBox> emit,
        ModelCapabilityBoundary? boundary)
    {
        _catalog = catalog;
        _policy = policy;
        _emit = emit;
        _boundary = boundary;
    }

    /// <summary>Resultado de ejecutar o rechazar una raw tool call del modelo.</summary>
    public sealed class Outcome
    {
        public bool Succeeded { get; }

        public string? Summary { get; }

        /// <summary>Contenido de la herramienta (Preview), p. ej. el archivo leído — vuelve al modelo.</summary>
        public string? Preview { get; }

        public ToolCallState FinalState { get; }

        public EffectOutcome Effect { get; }

        public Outcome(bool succeeded, string? summary, string? preview, ToolCallState finalState,
            EffectOutcome effect)
        {
            Succeeded = succeeded;
            Summary = summary;
            Preview = preview;
            FinalState = finalState;
            Effect = effect;
        }

        public Outcome(bool succeeded, string? summary, ToolCallState finalState, EffectOutcome effect) :
            this(succeeded, summary, null, finalState, effect)
        {
        }
    }

    /// <summary>
    /// Pipeline completo para una raw tool call propuesta por el modelo.
    /// </summary>
    public Outcome Run(ValidatedToolCall validated, ToolPreparationContext prepContext,
        ToolExecutionContext execContext, bool userApprovesAsk, CancellationToken cancellationToken)
    {
        _emit(new ToolCallRequested(validated.ToolCallId, validated.ProviderCallId,
            validated.ToolId.ToString(), validated.NormalizedArgumentsJson));
        var tool = _catalog.Find(validated.ToolId);
        if (tool is null)
        {
            _emit(new ToolCallRejected(validated.ToolCallId, "tool no encontrada: " + validated.ToolId));
            return new Outcome(false, "tool no encontrada", ToolCallState.Rejected, EffectOutcome.None);
        }

        // P0-1: ciclo durable completo — Requested → Prepared → PermissionEvaluated → …
        // La state machine de ToolCall solo acepta PermissionEvaluated desde Prepared; cada
        // runtime (Engine y Explorer por igual) persiste las dos primeras etapas aquí.
        // 1. Prepare (puro, síncrono; ADR-0014 §3).
        ToolPreparation preparation;
        try
        {
            preparation = tool.Prepare(validated, prepContext);
        }
        catch (Exception ex)
        {
            _emit(new ToolCallRejected(validated.ToolCallId, "prepare falló: " + ex.Message));
            return new Outcome(false, "prepare falló: " + ex.Message,
                ToolCallState.Rejected, EffectOutcome.None);
        }
        if (preparation is PreparationRejected rejected)
        {
            _emit(new ToolCallRejected(validated.ToolCallId, rejected.Reason));
            return new Outcome(false, rejected.Reason, ToolCallState.Rejected, EffectOutcome.None);
        }

        var intent = ((Prepared) preparation).Intent;

        // 1b. Frontera de capacidad del modelo (ADR-0044 §5): antes del Permission Engine. Con
        // el estado aún en Requested el rechazo es Rejected en el ciclo durable (ADR-0036 §5).
        // La frontera restringe; jamás autoriza (INV-018): una tool fuera del techo de la
        // categoría muere aquí aunque el Permission Engine la permitiría.
        if (_boundary is not null)
        {
            var boundaryDecision = _boundary!.Evaluate(intent);
            if (!boundaryDecision.Allowed)
            {
                _emit(new ToolCallRejected(validated.ToolCallId,
                    "frontera de capacidad: " + boundaryDecision.Reason));
                return new Outcome(false, boundaryDecision.Reason, ToolCallState.Rejected,
                    EffectOutcome.None);
            }
        }

        _emit(new ToolCallPrepared(validated.ToolCallId, intent.NormalizedArgumentsJson));

        // 2. Permission Engine (ADR-0037 §2): traza siempre; Ask según flujo de interacción.
        PermissionDecisionRecord decision;
        try
        {
            decision = _policy.Evaluate(intent);
        }
        catch (Exception ex)
        {
            _emit(new ToolCallRejected(validated.ToolCallId, "política falló: " + ex.Message));
            return new Outcome(false, "política falló: " + ex.Message,
                ToolCallState.Rejected, EffectOutcome.None);
        }
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
            // Toda interacción abierta se cierra con InteractionResolved (ADR-0034): si no, la Lane
            // quedaría esperando un permiso ya decidido.
            if (!userApprovesAsk)
            {
                _emit(new InteractionResolved(interactionId, "deny", InteractionCause.NoClient));
                _emit(new PermissionDenied(validated.ToolCallId, "Sin aprobación → Deny (ADR-0003)"));
                return new Outcome(false, "denegado (sin aprobación)", ToolCallState.Rejected, EffectOutcome.None);
            }

            _emit(new InteractionResolved(interactionId, "allow_once", InteractionCause.User));
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

    private Outcome Execute(ValidatedToolCall call, AuthorizedToolIntent authorized, ToolIntent intent, ITool tool,
        ToolExecutionContext execContext, CancellationToken cancellationToken)
    {
        // 4a. Frontera de capacidad otra vez antes de ejecutar (ADR-0044 §5): el intent ya está
        // autorizado; si el techo cambió o la primera evaluación se saltó, Authorized → Failed.
        if (_boundary is not null)
        {
            var boundaryDecision = _boundary!.Evaluate(intent);
            if (!boundaryDecision.Allowed)
            {
                _emit(new ToolCallFailed(call.ToolCallId,
                    "frontera de capacidad: " + boundaryDecision.Reason, EffectOutcome.None));
                return new Outcome(false, boundaryDecision.Reason, null, ToolCallState.Failed,
                    EffectOutcome.None);
            }
        }

        // 4. Ejecución (Barrier si hay efecto; ADR-0002 §2, ADR-0004 §2). El Started lleva, cuando el
        // intent declara una reconciliación con pre/post y EXACTAMENTE un write target, la serialización
        // canónica de los metadatos (ruta + hashes) para que un crash posterior pueda reconciliar el
        // efecto desde el journal sin re-ejecutar (ADR-0004 §4). Si la tool calcula esos metadatos
        // (IReconcilableTool), se piden aquí: ya autorizada y antes del efecto, nunca en Prepare.
        var reconciliation = intent.Reconciliation;
        if (reconciliation is null && tool is IReconcilableTool reconcilable)
        {
            try
            {
                reconciliation = reconcilable.DescribeReconciliation(authorized, execContext);
            }
            catch (Exception)
            {
                reconciliation = null; // sin metadatos: reconciliación conservadora, nunca bloquea
            }
        }

        _emit(new ToolCallStarted(call.ToolCallId, intent.Effect, ReconciliationJsonFor(intent.Claims, reconciliation)));

        ToolResult result;
        try
        {
            result = tool.ExecuteAsync(authorized, execContext, cancellationToken).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            var cause = "tool lanzó " + ex.GetType().Name + ": " + ex.Message;
            if (intent.Effect != EffectClass.None)
            {
                _emit(new ToolCallEffectUnknown(call.ToolCallId, intent.Effect));
                return new Outcome(false, cause, ToolCallState.EffectUnknown, EffectOutcome.Unknown);
            }

            _emit(new ToolCallFailed(call.ToolCallId, cause, EffectOutcome.None));
            return new Outcome(false, cause, ToolCallState.Failed, EffectOutcome.None);
        }
        if (result.IsError)
        {
            // P0-2: los fallos de la tool (acceso denegado, no encontrado, falta path, ref
            // inválida) NUNCA producen toolcall.succeeded; terminan Failed/Rejected.
            _emit(new ToolCallFailed(call.ToolCallId, result.Summary, EffectOutcome.None));
            return new Outcome(false, result.Summary, result.Preview, ToolCallState.Failed, EffectOutcome.None);
        }

        var effect = result.EffectOutcome;
        if (effect == EffectOutcome.Unknown)
        {
            _emit(new ToolCallFailed(call.ToolCallId, "efecto desconocido", effect));
            return new Outcome(false, result.Summary, result.Preview, ToolCallState.Failed, effect);
        }

        _emit(new ToolCallSucceeded(call.ToolCallId, "{\"summary\":\"" + Esc(result.Summary) + "\"}"));
        // Preview: contenido real de la herramienta (p. ej. el archivo leído) → vuelve al modelo.
        return new Outcome(true, result.Summary, result.Preview, ToolCallState.Succeeded, effect);
    }

    public static ToolRuntime For(FakeCatalog catalog, IPermissionPolicy policy,
        Func<DomainEventPayload, VoidBox> emit) => new ToolRuntime(catalog, policy, emit);

    public static ToolRuntime For(FakeCatalog catalog, IPermissionPolicy policy,
        Func<DomainEventPayload, VoidBox> emit, ModelCapabilityBoundary? boundary)
        => new ToolRuntime(catalog, policy, emit, boundary);

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

    /// <summary>
    /// Serializa los metadatos canónicos de reconciliación para un intent con efecto. Devuelve null
    /// (reconciliación conservadora) cuando no hay <c>ReconciliationSpec</c>, cuando el intent declara
    /// más de un write target (ambigüedad: no se sabe contra qué archivo reconciliar) o cuando no hay
    /// ningún target. El Engine/reconciliador trata null como "sin metadatos" y falla cerrado.
    /// </summary>
    private static string? ReconciliationJsonFor(ResourceClaims claims, ReconciliationSpec? reconciliation)
    {
        if (reconciliation is null || claims.Writes.Count != 1)
        {
            return null;
        }

        return FilesystemReconciliationMetadata.Encode(claims.Writes[0],
            reconciliation.ExpectedPreHash, reconciliation.ExpectedPostHash);
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
