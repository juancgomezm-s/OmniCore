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
    // Attribution only: never rewrite the claims used by authorization or execution.
    private static string? RelativeTargetRef(IReadOnlyList<string> writes, string workspaceRoot)
    {
        if (writes.Count != 1 || string.IsNullOrWhiteSpace(writes[0])
            || writes[0].Contains("://", StringComparison.Ordinal)) return null;
        try
        {
            var root = Path.GetFullPath(workspaceRoot);
            var target = Path.GetFullPath(writes[0], root);
            var relative = Path.GetRelativePath(root, target);
            if (Path.IsPathRooted(relative) || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
            return relative.Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private readonly FakeCatalog _catalog;

    private readonly IPermissionPolicy _policy;

    private readonly Func<DomainEventPayload, VoidBox> _emit;

    private readonly ModelCapabilityBoundary? _boundary;

    private readonly IExecutableResolver? _executableResolver;

    public ToolRuntime(FakeCatalog catalog, IPermissionPolicy policy, Func<DomainEventPayload, VoidBox> emit)
        : this(catalog, policy, emit, null, null)
    {
    }

    /// <summary>
    /// Frontera de capacidad opcional (ADR-0044 §5): restringe, nunca autoriza. Se evalúa tras
    /// Prepare (antes del Permission Engine) y de nuevo antes de ejecutar. Con null el pipeline
    /// es idéntico al de M2: la frontera es un añadido componible, no un cambio de semántica.
    /// </summary>
    public ToolRuntime(FakeCatalog catalog, IPermissionPolicy policy, Func<DomainEventPayload, VoidBox> emit,
        ModelCapabilityBoundary? boundary)
        : this(catalog, policy, emit, boundary, null)
    {
    }

    public ToolRuntime(FakeCatalog catalog, IPermissionPolicy policy, Func<DomainEventPayload, VoidBox> emit,
        ModelCapabilityBoundary? boundary, IExecutableResolver? executableResolver)
    {
        _catalog = catalog;
        _policy = policy;
        _emit = emit;
        _boundary = boundary;
        _executableResolver = executableResolver;
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

        public InteractionId? PendingInteractionId { get; }

        public Outcome(bool succeeded, string? summary, string? preview, ToolCallState finalState,
            EffectOutcome effect, InteractionId? pendingInteractionId = null)
        {
            Succeeded = succeeded;
            Summary = summary;
            Preview = preview;
            FinalState = finalState;
            Effect = effect;
            PendingInteractionId = pendingInteractionId;
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
        ToolExecutionContext execContext, bool userApprovesAsk, CancellationToken cancellationToken,
        GrantLifetime approvalLifetime = GrantLifetime.Once)
        => RunCore(validated, prepContext, execContext, userApprovesAsk, cancellationToken,
            approvalLifetime, requestReceipt: null);

    /// <summary>Consumes only a receipt for this exact attributed call; a prior request event
    /// with another id, arguments, or execution scope cannot authorize omission of the request.</summary>
    public Outcome Run(ValidatedToolCall validated, ToolPreparationContext prepContext,
        ToolExecutionContext execContext, bool userApprovesAsk, CancellationToken cancellationToken,
        ToolCallRequestReceipt requestReceipt, GrantLifetime approvalLifetime = GrantLifetime.Once)
    {
        ArgumentNullException.ThrowIfNull(requestReceipt);
        if (!requestReceipt.MatchesCall(validated))
            throw new InvalidOperationException("Tool request receipt does not match this call.");
        return RunCore(validated, prepContext, execContext, userApprovesAsk, cancellationToken,
            approvalLifetime, requestReceipt);
    }

    private Outcome RunCore(ValidatedToolCall validated, ToolPreparationContext prepContext,
        ToolExecutionContext execContext, bool userApprovesAsk, CancellationToken cancellationToken,
        GrantLifetime approvalLifetime, ToolCallRequestReceipt? requestReceipt)
    {
        if (requestReceipt is null)
            _emit(new ToolCallRequested(validated.ToolCallId, validated.ProviderCallId,
                validated.ToolId.ToString(), validated.NormalizedArgumentsJson));
        var tool = _catalog.Find(validated.ToolId);
        if (tool is null)
        {
            _emit(new ToolCallRejected(validated.ToolCallId, "tool no encontrada: " + validated.ToolId,
                ToolErrorCode.UnknownTool));
            return new Outcome(false, "tool no encontrada", ToolCallState.Rejected, EffectOutcome.None);
        }

        // 0. Validación de argumentos contra el InputSchema declarado (ADR-0014 §1, INV-001):
        // campos requeridos, tipos y sin campos desconocidos. La tool NUNCA llega a Prepare ni
        // a ejecutarse con argumentos que no conforman su schema; el rechazo es un error tipado
        // que alimenta el repair loop (spec §71). El estado sigue en Requested → Rejected.
        var schemaError = ToolSchemaValidator.Validate(validated.NormalizedArgumentsJson, tool.Descriptor.InputSchema);
        if (schemaError is not null)
        {
            var reason = ToolSchemaValidator.InvalidArgumentsCode + ": " + schemaError;
            _emit(new ToolCallRejected(validated.ToolCallId, reason, ToolErrorCode.InvalidArguments));
            return new Outcome(false, reason, ToolCallState.Rejected, EffectOutcome.None);
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
            _emit(new ToolCallRejected(validated.ToolCallId, "prepare falló: " + ex.Message,
                ToolErrorCode.ToolFailure));
            return new Outcome(false, "prepare falló: " + ex.Message,
                ToolCallState.Rejected, EffectOutcome.None);
        }
        if (preparation is PreparationRejected rejected)
        {
            // Invariante de código tipado (spec §71): todo rechazo persistido lleva código. Una
            // tool que no fijó el suyo se clasifica TOOL_FAILURE al persistir, igual que
            // ToolResult.Error normaliza a falta de código.
            _emit(new ToolCallRejected(validated.ToolCallId, rejected.Reason,
                rejected.ErrorCode ?? ToolErrorCode.ToolFailure));
            return new Outcome(false, rejected.Reason, ToolCallState.Rejected, EffectOutcome.None);
        }

        var intent = ((Prepared) preparation).Intent;

        // La resolución se realiza entre Prepare (puro) y la evaluación del Permission Engine,
        // para que Security autorice el ejecutable real, nunca el nombre solicitado (ADR-0015).
        if (intent.Claims.Process is { } processClaim)
        {
            if (_executableResolver is null)
            {
                const string reason = "No hay resolver de ejecutables configurado; proceso rechazado por seguridad";
                _emit(new ToolCallRejected(validated.ToolCallId, reason, ToolErrorCode.PermissionDenied));
                return new Outcome(false, reason, ToolCallState.Rejected, EffectOutcome.None);
            }
            try
            {
                var resolved = _executableResolver.Resolve(processClaim.Executable, prepContext.WorkspaceRoot);
                var claims = new ResourceClaims(intent.Claims.Reads, intent.Claims.Writes, intent.Claims.Network,
                    new ProcessClaim(resolved.ResolvedPath, processClaim.Args, processClaim.EffectClass,
                        processClaim.NetworkRequired, processClaim.WorkingDirectory), intent.Claims.Secrets);
                intent = new ToolIntent(intent.ToolCallId, intent.ToolId, intent.NormalizedArgumentsJson,
                    intent.Effect, claims, intent.Risk, intent.Reconciliation);
            }
            catch (ExecutableNotFoundException ex)
            {
                _emit(new ToolCallRejected(validated.ToolCallId, ex.Message, ToolErrorCode.ProcessFailure));
                return new Outcome(false, ex.Message, ToolCallState.Rejected, EffectOutcome.None);
            }
        }

        // 1b. Frontera de capacidad del modelo (ADR-0044 §5): antes del Permission Engine. Con
        // el estado aún en Requested el rechazo es Rejected en el ciclo durable (ADR-0036 §5).
        // La frontera restringe; jamás autoriza (INV-018): una tool fuera del techo de la
        // categoría muere aquí aunque el Permission Engine la permitiría.
        var boundaryAsk = false;
        if (_boundary is not null)
        {
            var boundaryDecision = _boundary!.Evaluate(intent);
            boundaryAsk = boundaryDecision.RequiresAsk;
            if (!boundaryDecision.Allowed)
            {
                _emit(new ToolCallRejected(validated.ToolCallId,
                    "frontera de capacidad: " + boundaryDecision.Reason, ToolErrorCode.CapabilityRefused));
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
            _emit(new ToolCallRejected(validated.ToolCallId, "política falló: " + ex.Message,
                ToolErrorCode.ToolFailure));
            return new Outcome(false, "política falló: " + ex.Message,
                ToolCallState.Rejected, EffectOutcome.None);
        }
        if (boundaryAsk && decision.Final == PermissionDecision.Allow)
        {
            // Mínimo entre capas (INV-028): el Ask de la frontera baja un Allow; nunca sube un Deny.
            var layers = new List<LayerDecision>(decision.Layers)
            {
                new("ModelCapabilityBoundary", PermissionDecision.Ask, "destructive-action-ask"),
            };
            decision = new PermissionDecisionRecord(PermissionDecision.Ask, layers, null);
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
            var interaction = new InteractionRequested(interactionId, InteractionKind.Permission,
                SubjectJson(validated.ToolId.ToString()),
                OptionsJson(_policy is IGrantablePermissionPolicy { CanCreatePersistentGrants: true }
                    && IsGrantableAsk(decision.Layers)),
                "deny", null, null, null, null, 0, 1);
            _emit(interaction);
            // El host resuelve la InteractionRequest; sin cliente interactivo se deniega (ADR-0003).
            var selectedOption = userApprovesAsk
                ? approvalLifetime switch
                {
                    GrantLifetime.Once => "allow_once",
                    GrantLifetime.Run => "allow_run",
                    GrantLifetime.Workspace => "allow_workspace",
                    _ => "deny",
                }
                : execContext.IsInteractive ? execContext.ResolveInteraction?.Invoke(interaction) : null;
            if (!execContext.IsInteractive && execContext.ResolveInteraction is not null && selectedOption is null)
            {
                // Headless callers must not invent a denial/approval: keep the server-owned
                // interaction pending so the Run can surface InputRequired without a decision.
                return new Outcome(false, "awaiting input", null, ToolCallState.AwaitingPermission,
                    EffectOutcome.None, interactionId);
            }
            if (selectedOption is not ("allow_once" or "allow_run" or "allow_workspace"))
            {
                _emit(new InteractionResolved(interactionId, "deny",
                    selectedOption is null
                        ? (execContext.IsInteractive ? InteractionCause.Timeout : InteractionCause.NoClient)
                        : InteractionCause.User));
                _emit(new PermissionDenied(validated.ToolCallId, "Sin aprobación → Deny (ADR-0003)"));
                return new Outcome(false, "denegado (sin aprobación)", ToolCallState.Rejected, EffectOutcome.None);
            }

            var optionId = selectedOption;
            var effectiveLifetime = optionId switch
            {
                "allow_once" => GrantLifetime.Once,
                "allow_run" => GrantLifetime.Run,
                "allow_workspace" => GrantLifetime.Workspace,
                _ => GrantLifetime.Once,
            };
            GrantId? approvedGrant = effectiveLifetime == GrantLifetime.Once ? null : GrantId.New();
            if (effectiveLifetime != GrantLifetime.Once)
            {
                if (_policy is not IGrantablePermissionPolicy grantable)
                {
                    _emit(new InteractionResolved(interactionId, "deny", InteractionCause.User));
                    _emit(new PermissionDenied(validated.ToolCallId, "la política no permite grants persistentes"));
                    return new Outcome(false, "denegado", ToolCallState.Rejected, EffectOutcome.None);
                }
                try
                {
                    approvedGrant = grantable.RecordApprovedGrant(intent, effectiveLifetime, cancellationToken);
                }
                catch (Exception ex)
                {
                    _emit(new InteractionResolved(interactionId, "deny", InteractionCause.User));
                    _emit(new PermissionDenied(validated.ToolCallId, "grant rechazado: " + ex.GetType().Name));
                    return new Outcome(false, "grant rechazado", ToolCallState.Rejected, EffectOutcome.None);
                }
            }

            _emit(new InteractionResolved(interactionId, optionId, InteractionCause.User));
            _emit(new PermissionGranted(validated.ToolCallId, approvedGrant, effectiveLifetime));
            // INV-002: la política ya quedó evaluada con la aprobación humana; no se pide un
            // nuevo permiso. La política de Security comprueba de nuevo que no exista Deny.
            try
            {
                var approved = _policy.AuthorizeApproved(intent, approvedGrant);
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
                    "frontera de capacidad: " + boundaryDecision.Reason, EffectOutcome.None,
                    ToolErrorCode.CapabilityRefused));
                return new Outcome(false, boundaryDecision.Reason, null, ToolCallState.Failed,
                    EffectOutcome.None);
            }
        }

        // 4. Ejecución (Barrier si hay efecto; ADR-0002 §2, ADR-0004 §2). El Started lleva, cuando el
        // intent declara una reconciliación con pre/post y EXACTAMENTE un write target, la serialización
        // canónica de los metadatos (ruta + hashes) para que un crash posterior pueda reconciliar el
        // efecto desde el journal sin re-ejecutar (ADR-0004 §4). Si la tool calcula esos metadatos
        // (IReconcilableTool), se piden aquí: ya autorizada y antes del efecto, nunca en Prepare.
        IDisposable? publication;
        try
        {
            publication = tool is FilesystemWriteTool or FilesystemPatchTool && execContext.Artifacts is not null
                ? (execContext.Artifacts as IArtifactPublicationLease
                    ?? throw new FilesystemPreimageException()).AcquirePublicationLease(cancellationToken)
                : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            const string cause = "Filesystem pre-image publication could not be protected.";
            _emit(new ToolCallFailed(call.ToolCallId, cause, EffectOutcome.None, ToolErrorCode.ToolFailure));
            return new Outcome(false, cause, ToolCallState.Failed, EffectOutcome.None);
        }
        using var heldPublication = publication;
        var reconciliation = intent.Reconciliation;
        if (reconciliation is null && tool is IReconcilableTool reconcilable)
        {
            try
            {
                reconciliation = reconcilable.DescribeReconciliation(authorized, execContext);
            }
            catch (FilesystemPreimageException)
            {
                const string cause = "Filesystem pre-image could not be captured faithfully and safely.";
                _emit(new ToolCallFailed(call.ToolCallId, cause, EffectOutcome.None, ToolErrorCode.ToolFailure));
                return new Outcome(false, cause, ToolCallState.Failed, EffectOutcome.None);
            }
            catch (Exception)
            {
                reconciliation = null; // sin metadatos: reconciliación conservadora, nunca bloquea
            }
        }

        execContext.BeforeEffect?.Invoke(intent);
        _emit(new ToolCallStarted(call.ToolCallId, intent.Effect, ReconciliationJsonFor(intent.Claims, reconciliation))
        {
            // Attribution is not proof of reversibility: pre/post hashes cannot restore bytes.
            TargetRef = RelativeTargetRef(intent.Claims.Writes, execContext.WorkspaceRoot),
            Reversibility = reconciliation?.Reversibility ?? Reversibility.Unknown,
            BeforeStateRef = reconciliation?.BeforeStateRef,
        });

        ToolResult result;
        try
        {
            result = tool.ExecuteAsync(authorized, execContext, cancellationToken).GetAwaiter().GetResult();
        }
        catch (WeakSandboxConsentRequiredException ex)
        {
            _emit(new ToolCallFailed(call.ToolCallId, ex.Message, EffectOutcome.None,
                ToolErrorCode.PermissionDenied));
            return new Outcome(false, ex.Message, null, ToolCallState.Failed, EffectOutcome.None);
        }
        catch (Exception ex)
        {
            var cause = "tool lanzó " + ex.GetType().Name + ": " + ex.Message;
            if (intent.Effect != EffectClass.None)
            {
                _emit(new ToolCallEffectUnknown(call.ToolCallId, intent.Effect));
                return new Outcome(false, cause, ToolCallState.EffectUnknown, EffectOutcome.Unknown);
            }

            _emit(new ToolCallFailed(call.ToolCallId, cause, EffectOutcome.None, ToolErrorCode.ToolFailure));
            return new Outcome(false, cause, ToolCallState.Failed, EffectOutcome.None);
        }
        if (result.IsError)
        {
            // Un proceso que llegó a arrancar puede haber producido efectos parciales antes de
            // fallar o agotar timeout: no fingir que no hubo efecto (ADR-0004).
            if (result.EffectOutcome == EffectOutcome.Unknown)
            {
                _emit(new ToolCallEffectUnknown(call.ToolCallId, intent.Effect));
                return new Outcome(false, result.Summary, result.Preview, ToolCallState.EffectUnknown,
                    EffectOutcome.Unknown);
            }
            _emit(new ToolCallFailed(call.ToolCallId, result.Summary, result.EffectOutcome,
                result.ErrorCode));
            return new Outcome(false, result.Summary, result.Preview, ToolCallState.Failed, result.EffectOutcome);
        }

        var effect = result.EffectOutcome;
        if (effect == EffectOutcome.Unknown)
        {
            _emit(new ToolCallFailed(call.ToolCallId, "efecto desconocido", effect, ToolErrorCode.UnknownEffect));
            return new Outcome(false, result.Summary, result.Preview, ToolCallState.Failed, effect);
        }

        _emit(new ToolCallSucceeded(call.ToolCallId, "{\"summary\":\"" + Esc(result.Summary) + "\"}")
        { AfterStateRef = tool is FilesystemWriteTool or FilesystemPatchTool && result.AfterStateBytes is { } bytes
            && reconciliation?.ExpectedPostHash == FileVersion.VersionToken(bytes)
                ? FilesystemPreimage.CaptureAfter(execContext.Artifacts, bytes) : null });
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
    private static string OptionsJson(bool grantsAvailable)
    {
        var options = "[{\"id\":\"deny\",\"intent\":\"deny\"},"
            + "{\"id\":\"allow_once\",\"intent\":\"allow\",\"lifetime\":\"once\"}";
        if (grantsAvailable)
            options += ",{\"id\":\"allow_run\",\"intent\":\"allow\",\"lifetime\":\"run\"},"
                + "{\"id\":\"allow_workspace\",\"intent\":\"allow\",\"lifetime\":\"workspace\"}";
        return options + "]";
    }

    /// <summary>Solo los Ask explícitamente grantables pueden ofrecer permisos persistentes.</summary>
    private static bool IsGrantableAsk(IReadOnlyList<LayerDecision> layers)
    {
        var asks = layers.Where(layer => layer.Decision == PermissionDecision.Ask).ToArray();
        return asks.Length > 0 && asks.All(layer => layer.Layer is "UserPolicy" or "user-policy"
            or "profile" or "mode-defaults");
    }

    private static string Esc(string value) => value.Replace("\"", "\\\"").Replace("\n", "\\n");
}

/// <summary>Resultado no-null de los callbacks de emisión de eventos.</summary>
public sealed class VoidBox
{
    public static readonly VoidBox Instance = new VoidBox();
}
