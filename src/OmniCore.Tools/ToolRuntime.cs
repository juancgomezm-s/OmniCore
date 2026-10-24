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

    private readonly IReadOnlyList<IToolIntentBoundary> _additionalBoundaries;

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
        : this(catalog, policy, emit, boundary, executableResolver, [])
    {
    }

    public ToolRuntime(FakeCatalog catalog, IPermissionPolicy policy, Func<DomainEventPayload, VoidBox> emit,
        ModelCapabilityBoundary? boundary, IExecutableResolver? executableResolver,
        IReadOnlyList<IToolIntentBoundary> additionalBoundaries)
    {
        _catalog = catalog;
        _policy = policy;
        _emit = emit;
        _boundary = boundary;
        _executableResolver = executableResolver;
        _additionalBoundaries = additionalBoundaries ?? [];
    }

    /// <summary>Resultado de ejecutar o rechazar una raw tool call del modelo.</summary>
    public sealed class Outcome
    {
        public bool Succeeded { get; }

        public string? Summary { get; }

        /// <summary>Contenido de la herramienta (Preview), p. ej. el archivo leído — vuelve al modelo.</summary>
        public string? Preview { get; }

        public ArtifactRef? Artifact { get; }

        public ToolCallState FinalState { get; }

        public EffectOutcome Effect { get; }

        public InteractionId? PendingInteractionId { get; }

        public Outcome(bool succeeded, string? summary, string? preview, ToolCallState finalState,
            EffectOutcome effect, InteractionId? pendingInteractionId = null, ArtifactRef? artifact = null)
        {
            Succeeded = succeeded;
            Summary = summary;
            Preview = preview;
            FinalState = finalState;
            Effect = effect;
            PendingInteractionId = pendingInteractionId;
            Artifact = artifact;
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

    /// <summary>Rehydrates one durable, explicitly approved Permission request. The original
    /// request/prepared/interaction events remain authoritative; this path re-prepares and checks
    /// the exact persisted arguments before recording grant/authorization and crossing Started.</summary>
    public Outcome ResumeApprovedPermission(ValidatedToolCall validated, ToolPreparationContext prepContext,
        ToolExecutionContext execContext, CancellationToken cancellationToken, InteractionId interaction,
        string optionId, string expectedPreparedArguments, string expectedIntentFingerprint)
    {
        if (optionId is not ("allow_once" or "allow_run" or "allow_workspace" or "deny"))
            throw new InvalidOperationException("Only a server-defined Permission choice can resume this call.");
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPreparedArguments);
        return RunCore(validated, prepContext, execContext, userApprovesAsk: false, cancellationToken,
            GrantLifetime.Once, requestReceipt: null, resumedPermission: interaction,
            resumedPermissionOption: optionId, expectedPreparedArguments: expectedPreparedArguments,
            expectedIntentFingerprint: expectedIntentFingerprint);
    }

    /// <summary>Consumes a server-validated, exact one-call Weak sandbox choice. This choice is
    /// not a Permission grant: current policy must still return Allow and the prepared intent
    /// must remain byte-for-byte/fingerprint identical before the same call can execute.</summary>
    public Outcome ResumeWeakSandboxConsent(ValidatedToolCall validated, ToolPreparationContext prepContext,
        ToolExecutionContext execContext, CancellationToken cancellationToken, InteractionId interaction,
        string optionId, string expectedPreparedArguments, string expectedIntentFingerprint)
    {
        if (optionId is not ("consent_once" or "deny"))
            throw new InvalidOperationException("Only the server-defined one-call Weak sandbox choice can be resumed.");
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPreparedArguments);
        return RunCore(validated, prepContext, execContext, userApprovesAsk: false, cancellationToken,
            GrantLifetime.Once, requestReceipt: null, resumedWeakSandboxConsent: interaction,
            resumedWeakSandboxOption: optionId, expectedPreparedArguments: expectedPreparedArguments,
            expectedIntentFingerprint: expectedIntentFingerprint);
    }

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
        GrantLifetime approvalLifetime, ToolCallRequestReceipt? requestReceipt,
        InteractionId? resumedPermission = null, string? resumedPermissionOption = null,
        InteractionId? resumedWeakSandboxConsent = null, string? resumedWeakSandboxOption = null,
        string? expectedPreparedArguments = null, string? expectedIntentFingerprint = null,
        bool automaticApply = false, ToolCallId? parentToolCallId = null,
        ToolIntent? automaticSourceIntent = null)
    {
        void RejectBeforeAuthorization(string reason, ToolErrorCode errorCode) =>
            _emit(resumedPermission is not null || resumedWeakSandboxConsent is not null
                ? new PermissionDenied(validated.ToolCallId, reason)
                : new ToolCallRejected(validated.ToolCallId, reason, errorCode));
        if (requestReceipt is null && resumedPermission is null && resumedWeakSandboxConsent is null)
            _emit(new ToolCallRequested(validated.ToolCallId, validated.ProviderCallId,
                validated.ToolId.ToString(), validated.NormalizedArgumentsJson)
            { ParentToolCallId = parentToolCallId });
        var tool = _catalog.Find(validated.ToolId);
        if (tool is null)
        {
            RejectBeforeAuthorization("tool no encontrada: " + validated.ToolId, ToolErrorCode.UnknownTool);
            return new Outcome(false, "tool no encontrada", ToolCallState.Rejected, EffectOutcome.None);
        }
        if (tool is INativeUserActionOnlyTool && !prepContext.IsNativeUserAction)
        {
            RejectBeforeAuthorization("tool requires an admitted native user action", ToolErrorCode.PermissionDenied);
            return new Outcome(false, "native user action required", ToolCallState.Rejected, EffectOutcome.None);
        }

        // 0. Validación de argumentos contra el InputSchema declarado (ADR-0014 §1, INV-001):
        // campos requeridos, tipos y sin campos desconocidos. La tool NUNCA llega a Prepare ni
        // a ejecutarse con argumentos que no conforman su schema; el rechazo es un error tipado
        // que alimenta el repair loop (spec §71). El estado sigue en Requested → Rejected.
        var schemaError = ToolSchemaValidator.Validate(validated.NormalizedArgumentsJson, tool.Descriptor.InputSchema,
            allowAdditionalPropertiesByDefault: tool.Descriptor.Source.Kind is SourceKind.Mcp or SourceKind.Extension);
        if (schemaError is not null)
        {
            var reason = ToolSchemaValidator.InvalidArgumentsCode + ": " + schemaError;
            RejectBeforeAuthorization(reason, ToolErrorCode.InvalidArguments);
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
            RejectBeforeAuthorization("prepare falló: " + ex.Message, ToolErrorCode.ToolFailure);
            return new Outcome(false, "prepare falló: " + ex.Message,
                ToolCallState.Rejected, EffectOutcome.None);
        }
        if (preparation is PreparationRejected rejected)
        {
            // Invariante de código tipado (spec §71): todo rechazo persistido lleva código. Una
            // tool que no fijó el suyo se clasifica TOOL_FAILURE al persistir, igual que
            // ToolResult.Error normaliza a falta de código.
            RejectBeforeAuthorization(rejected.Reason, rejected.ErrorCode ?? ToolErrorCode.ToolFailure);
            return new Outcome(false, rejected.Reason, ToolCallState.Rejected, EffectOutcome.None);
        }

        var intent = ((Prepared) preparation).Intent;
        if ((resumedPermission is not null || resumedWeakSandboxConsent is not null)
            && !string.Equals(intent.NormalizedArgumentsJson, expectedPreparedArguments, StringComparison.Ordinal))
        {
            _emit(new PermissionDenied(validated.ToolCallId,
                "The prepared tool arguments changed after the approval request."));
            return new Outcome(false, "approved tool intent changed", ToolCallState.Rejected, EffectOutcome.None);
        }

        // Existing writes retain the model's filesystem.write request/permission identity, but
        // their capability classification is derived from target metadata and a second effective
        // permission identity (filesystem.patch). This happens after pure Prepare and before any
        // content read or filesystem effect.
        ToolIntent? effectiveIntent = null;
        if (tool is FilesystemWriteTool writeTool)
        {
            intent = writeTool.ClassifyTargetForAuthorization(intent, prepContext.WorkspaceRoot,
                execContext.MutationHistory);
            effectiveIntent = FilesystemWriteTool.EffectivePatchAuthorizationIntent(intent);
        }
        if (tool is IHostScopedPrivateProcessControl processControl
            && tool.Descriptor.Id.ToString() is "process.send_input" or "process.stop")
        {
            if (!processControl.TryBindProcessControlIntent(intent, execContext, out var boundIntent))
            {
                RejectBeforeAuthorization("Process handle is not owned by the active Host scope.", ToolErrorCode.PermissionDenied);
                return new Outcome(false, "process.handle_unavailable", ToolCallState.Rejected, EffectOutcome.None);
            }
            intent = boundIntent;
        }
        IReadOnlyList<ToolIntent> additionalEffectiveIntents = [];
        if (tool is IHostResolvedAuthorizationIntent hostResolver)
        {
            try
            {
                var resolvedIntent = hostResolver.ResolveHostAuthorizationIntent(intent,
                    prepContext, execContext, cancellationToken);
                ValidateHostResolvedAuthorizationIntent(intent, resolvedIntent);
                // Host-resolved ownership is the exact primary resource intent. Keeping an
                // empty primary beside an additional claim would authorize only a shadow intent
                // while DescribeReconciliation receives the unbound primary.
                intent = resolvedIntent;
            }
            catch (ToolPreflightException ex)
            {
                RejectBeforeAuthorization(ex.Message, ex.ErrorCode);
                return new Outcome(false, ex.Message, ToolCallState.Failed, EffectOutcome.None);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
            {
                RejectBeforeAuthorization("Host resource authorization could not be resolved.", ToolErrorCode.StaleWrite);
                return new Outcome(false, "Host resource authorization could not be resolved.",
                    ToolCallState.Rejected, EffectOutcome.None);
            }
        }
        else if (tool is IEffectiveToolAuthorizationIntents batchResolver)
        {
            try
            {
                additionalEffectiveIntents = batchResolver.ResolveEffectiveAuthorizationIntents(intent,
                    prepContext, execContext, cancellationToken);
                ValidateEffectiveBatchIntents(intent, additionalEffectiveIntents, execContext.WorkspaceRoot);
            }
            catch (ToolPreflightException ex)
            {
                RejectBeforeAuthorization(ex.Message, ex.ErrorCode);
                return new Outcome(false, ex.Message, ToolCallState.Rejected, EffectOutcome.None);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
            {
                RejectBeforeAuthorization("Effective batch authorization could not be resolved.", ToolErrorCode.StaleWrite);
                return new Outcome(false, "Effective batch authorization could not be resolved.",
                    ToolCallState.Rejected, EffectOutcome.None);
            }
        }

        // La resolución se realiza entre Prepare (puro) y la evaluación del Permission Engine,
        // para que Security autorice el ejecutable real, nunca el nombre solicitado (ADR-0015).
        if (intent.Claims.Process is { } processClaim
            && !(tool is IHostScopedPrivateProcessControl
                && tool.Descriptor.Id.ToString() is ("process.send_input" or "process.stop"))
            && !(tool is IHostResolvedProcessExecutable resolvedExecutable
                && resolvedExecutable.IsHostResolvedProcessExecutable(intent)))
        {
            if (tool.Descriptor.Source.Kind == SourceKind.Extension)
            {
                // ExtensionTool resolves its executable from the hash-bound package and the Host
                // revalidates that identity before Strong launch. SystemExecutableResolver
                // intentionally rejects workspace-local executables, so do not search PATH or
                // relax that resolver for ordinary tools.
                if (!Path.IsPathRooted(processClaim.Executable))
                {
                    const string reason = "Extension process claims must name the resolved package executable";
                    RejectBeforeAuthorization(reason, ToolErrorCode.PermissionDenied);
                    return new Outcome(false, reason, ToolCallState.Rejected, EffectOutcome.None);
                }
            }
            else
            {
            if (_executableResolver is null)
            {
                const string reason = "No hay resolver de ejecutables configurado; proceso rechazado por seguridad";
                RejectBeforeAuthorization(reason, ToolErrorCode.PermissionDenied);
                return new Outcome(false, reason, ToolCallState.Rejected, EffectOutcome.None);
            }
            try
            {
                var resolved = _executableResolver.Resolve(processClaim.Executable, prepContext.WorkspaceRoot);
                var claims = new ResourceClaims(intent.Claims.Reads, intent.Claims.Writes, intent.Claims.Network,
                    new ProcessClaim(resolved.ResolvedPath, processClaim.Args, processClaim.EffectClass,
                        processClaim.NetworkRequired, processClaim.WorkingDirectory), intent.Claims.Secrets,
                    intent.Claims.WorktreeDisposition);
                intent = new ToolIntent(intent.ToolCallId, intent.ToolId, intent.NormalizedArgumentsJson,
                    intent.Effect, claims, intent.Risk, intent.Reconciliation,
                    intent.EffectiveMutationOperation, intent.RequiresPhysicalCreateCapability,
                    intent.EffectiveMutationHistoryRevision);
            }
            catch (ExecutableNotFoundException ex)
            {
                RejectBeforeAuthorization(ex.Message, ToolErrorCode.ProcessFailure);
                return new Outcome(false, ex.Message, ToolCallState.Rejected, EffectOutcome.None);
            }
            }
        }

        // A durable Permission binds the effective intent after executable resolution, not
        // merely the provider arguments. Catalog or resolver changes cannot substitute another
        // process or resource target under an earlier human approval.
        if ((resumedPermission is not null || resumedWeakSandboxConsent is not null)
            && (expectedIntentFingerprint is not { Length: 64 }
                || !expectedIntentFingerprint.All(Uri.IsHexDigit)
                || !string.Equals(IntentFingerprint(intent, effectiveIntent, additionalEffectiveIntents), expectedIntentFingerprint,
                    StringComparison.OrdinalIgnoreCase)))
        {
            _emit(new PermissionDenied(validated.ToolCallId,
                "The effective tool intent changed after the approval request."));
            return new Outcome(false, "approved tool intent changed", ToolCallState.Rejected, EffectOutcome.None);
        }

        // 1b. Frontera de capacidad del modelo (ADR-0044 §5): antes del Permission Engine. Con
        // el estado aún en Requested el rechazo es Rejected en el ciclo durable (ADR-0036 §5).
        // La frontera restringe; jamás autoriza (INV-018): una tool fuera del techo de la
        // categoría muere aquí aunque el Permission Engine la permitiría.
        var boundaryAsk = false;
        IReadOnlyList<ToolIntent> authorizationIntents = new[] { intent }
            .Concat(effectiveIntent is null ? [] : [effectiveIntent]).Concat(additionalEffectiveIntents).ToArray();
        if (intent.ToolId.ToString() == "extension.batch.apply" && additionalEffectiveIntents.Count > 0)
            authorizationIntents = additionalEffectiveIntents;
        foreach (var activeBoundary in ActiveBoundaries())
        foreach (var boundaryIntent in authorizationIntents)
        {
            var boundaryDecision = EvaluateBoundary(activeBoundary, tool, boundaryIntent);
            boundaryAsk |= boundaryDecision.RequiresAsk;
            if (!boundaryDecision.Allowed)
            {
                RejectBeforeAuthorization("frontera de capacidad: " + boundaryDecision.Reason,
                    ToolErrorCode.CapabilityRefused);
                return new Outcome(false, boundaryDecision.Reason, ToolCallState.Rejected,
                    EffectOutcome.None);
            }
        }

        if (resumedPermission is null && resumedWeakSandboxConsent is null)
            _emit(new ToolCallPrepared(validated.ToolCallId, intent.NormalizedArgumentsJson));

        var automaticSourceRequested = tool is IStagedExtensionBatchProducer stagedSource
            && stagedSource.IsTrustedPrivateWorkspaceWriter(intent)
            && _policy is IAutomaticExtensionBatchAuthority sourceAuthority
            && sourceAuthority.AllowsAutomaticExtensionBatch(intent);
        var automaticNoPromptRequested = automaticSourceRequested
            || tool is ITrustedBatchPublicationTool && _policy is IAutomaticExtensionBatchAuthority publicationAuthority
                && publicationAuthority.AllowsAutomaticExtensionBatchPublicationCurrently();

        // 2. Permission Engine (ADR-0037 §2): traza siempre; Ask según flujo de interacción.
        PermissionDecisionRecord decision;
        try
        {
            decision = _policy.Evaluate(intent);
            if (effectiveIntent is not null)
                decision = CombinePermissionDecisions(decision, _policy.Evaluate(effectiveIntent), "effective filesystem.patch");
            foreach (var batchIntent in additionalEffectiveIntents)
                decision = CombinePermissionDecisions(decision, _policy.Evaluate(batchIntent), "effective batch operation");
        }
        catch (Exception ex)
        {
            _emit(new ToolCallRejected(validated.ToolCallId, "política falló: " + ex.Message,
                ToolErrorCode.ToolFailure));
            return new Outcome(false, "política falló: " + ex.Message,
                ToolCallState.Rejected, EffectOutcome.None);
        }
        if (execContext.RequiresSubstitutionAsk && decision.Final == PermissionDecision.Allow)
        {
            var layers = new List<LayerDecision>(decision.Layers)
            {
                // The Ask is grantable only because the user explicitly chose this protected
                // substitution. A saved tool preference still does not authorize the intent.
                new("UserPolicy", PermissionDecision.Ask, "tool-preference-substitution"),
            };
            decision = new PermissionDecisionRecord(PermissionDecision.Ask, layers, null);
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
        if (_policy is IAutomaticExtensionBatchAuthority currentAuthority)
        {
            if (automaticNoPromptRequested && !automaticApply)
            {
                var current = currentAuthority.EvaluateCurrentPermission(intent);
                decision = CombinePermissionDecisions(decision,
                    new PermissionDecisionRecord(current,
                        [new LayerDecision("current-user-permissions", current, "permissions.yaml")], null),
                    "current user permission");
            }
            if (automaticApply)
            {
                if (automaticSourceIntent is not null)
                {
                    var sourceCurrent = currentAuthority.EvaluateCurrentPermission(automaticSourceIntent);
                    decision = CombinePermissionDecisions(decision,
                        new PermissionDecisionRecord(sourceCurrent,
                            [new LayerDecision("current-user-permissions", sourceCurrent, "permissions.yaml")], null),
                        "current extension source permission");
                    if (!currentAuthority.AllowsAutomaticExtensionBatchCurrently(automaticSourceIntent))
                        decision = CombinePermissionDecisions(decision,
                            new PermissionDecisionRecord(PermissionDecision.Deny,
                                [new LayerDecision("current-user-permissions", PermissionDecision.Deny, "automatic-source-revoked")], null),
                            "current automatic source authority");
                }
                foreach (var effective in additionalEffectiveIntents)
                {
                    var current = currentAuthority.EvaluateCurrentPermission(effective);
                    decision = CombinePermissionDecisions(decision,
                        new PermissionDecisionRecord(current,
                            [new LayerDecision("current-user-permissions", current, "permissions.yaml")], null),
                        "current effective file permission");
                }
            }
        }
        _emit(new PermissionEvaluated(validated.ToolCallId, decision.Final, LayersToJson(decision), null));
        if (resumedPermission is not null && resumedPermissionOption == "deny")
        {
            _emit(new PermissionDenied(validated.ToolCallId, "Permission denied by the user."));
            return new Outcome(false, "permission denied by the user", ToolCallState.Rejected, EffectOutcome.None);
        }
        if (automaticNoPromptRequested
            && (decision.Final != PermissionDecision.Allow || execContext.RequiresSubstitutionAsk))
        {
            const string cause = "Automatic extension execution is blocked by current permission or model policy.";
            _emit(new PermissionDenied(validated.ToolCallId, cause));
            return new Outcome(false, cause, ToolCallState.Rejected, EffectOutcome.None);
        }
        if (automaticApply && decision.Final != PermissionDecision.Allow)
        {
            const string cause = "Automatic extension publication is blocked by current permission or model policy.";
            _emit(new PermissionDenied(validated.ToolCallId, cause));
            return new Outcome(false, cause, ToolCallState.Rejected, EffectOutcome.None);
        }
        if (decision.Final == PermissionDecision.Deny)
        {
            _emit(new PermissionDenied(validated.ToolCallId, "deny por política"));
            return new Outcome(false, "denegado", ToolCallState.Rejected, EffectOutcome.None);
        }

        if (resumedWeakSandboxConsent is { } weakInteraction)
        {
            if (resumedWeakSandboxOption == "deny")
            {
                _emit(new PermissionDenied(validated.ToolCallId, "Weak sandbox consent was denied."));
                return new Outcome(false, "Weak sandbox consent denied", ToolCallState.Rejected, EffectOutcome.None);
            }
            if (resumedWeakSandboxOption != "consent_once" || decision.Final != PermissionDecision.Allow)
            {
                _emit(new PermissionDenied(validated.ToolCallId,
                    "The authorized tool policy changed before the one-call sandbox choice was resumed."));
                return new Outcome(false, "Weak sandbox consent is no longer applicable", ToolCallState.Rejected,
                    EffectOutcome.None);
            }
            try
            {
                var authorized = _policy.Authorize(intent);
                _emit(new ToolCallAuthorized(validated.ToolCallId));
                return Execute(validated, authorized, intent, tool, execContext, cancellationToken,
                    boundaryAskApproved: false);
            }
            catch (OmniCore.Domain.PermissionDeniedException ex)
            {
                _emit(new PermissionDenied(validated.ToolCallId, ex.Reason));
                return new Outcome(false, ex.Reason, ToolCallState.Rejected, EffectOutcome.None);
            }
        }

        if (resumedPermission is { } resumedInteraction)
        {
            // The human choice is already durable. Re-evaluate the current policy and Session
            // boundary, then apply only the exact server option; never mint a new interaction or
            // silently turn a prior approval into a broader grant.
            var option = resumedPermissionOption!;
            var lifetime = option switch
            {
                "allow_once" => GrantLifetime.Once,
                "allow_run" => GrantLifetime.Run,
                "allow_workspace" => GrantLifetime.Workspace,
                _ => throw new InvalidOperationException("Unsupported Permission choice."),
            };
            if ((effectiveIntent is not null || additionalEffectiveIntents.Count > 0) && lifetime != GrantLifetime.Once)
            {
                _emit(new PermissionDenied(validated.ToolCallId,
                    "A normalized existing-file write requires a one-call approval."));
                return new Outcome(false, "persistent grant unavailable for normalized write",
                    ToolCallState.Rejected, EffectOutcome.None);
            }
            GrantId? grant = null;
            if (lifetime != GrantLifetime.Once)
            {
                if (_policy is not IGrantablePermissionPolicy grantable)
                {
                    _emit(new PermissionDenied(validated.ToolCallId,
                        "The active policy does not allow persistent grants."));
                    return new Outcome(false, "persistent grant unavailable", ToolCallState.Rejected, EffectOutcome.None);
                }
                try { grant = grantable.RecordApprovedGrant(intent, lifetime, cancellationToken); }
                catch (Exception ex)
                {
                    _emit(new PermissionDenied(validated.ToolCallId,
                        "The selected grant could not be recorded: " + ex.GetType().Name));
                    return new Outcome(false, "grant unavailable", ToolCallState.Rejected, EffectOutcome.None);
                }
            }
            _emit(new PermissionGranted(validated.ToolCallId, grant, lifetime));
            try
            {
                var authorized = AuthorizeIntents(intent, effectiveIntent, grant, approved: true, additionalEffectiveIntents);
                _emit(new ToolCallAuthorized(validated.ToolCallId));
                return Execute(validated, authorized, intent, tool, execContext, cancellationToken,
                    boundaryAskApproved: decision.Final == PermissionDecision.Ask,
                    effectiveIntent: effectiveIntent, permissionAskApproved: true,
                    additionalEffectiveIntents: additionalEffectiveIntents);
            }
            catch (OmniCore.Domain.PermissionDeniedException ex)
            {
                _emit(new PermissionDenied(validated.ToolCallId, ex.Reason));
                return new Outcome(false, ex.Reason, ToolCallState.Rejected, EffectOutcome.None);
            }
        }

        if (decision.Final == PermissionDecision.Ask)
        {
            var interactionId = InteractionId.New();
            _emit(new PermissionRequested(validated.ToolCallId, interactionId));
            // ADR-0034/0036 §5: el Ask abre un InteractionRequest con sujeto y opciones del
            // servidor. El default es Deny (más restrictiva, ADR-0034 §1).
            var interaction = new InteractionRequested(interactionId, InteractionKind.Permission,
                SubjectJson(intent, effectiveIntent, execContext.ProtectedSubstitution, additionalEffectiveIntents),
                OptionsJson(effectiveIntent is null && additionalEffectiveIntents.Count == 0
                    && _policy is IGrantablePermissionPolicy { CanCreatePersistentGrants: true }
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
            if (!execContext.IsInteractive && execContext.AllowDeferredInteraction && selectedOption is null)
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
            var effectiveLifetime = effectiveIntent is not null || additionalEffectiveIntents.Count > 0 ? GrantLifetime.Once : optionId switch
            {
                "allow_once" => GrantLifetime.Once,
                "allow_run" => GrantLifetime.Run,
                "allow_workspace" => GrantLifetime.Workspace,
                _ => GrantLifetime.Once,
            };
            GrantId? approvedGrant = effectiveLifetime == GrantLifetime.Once ? null : GrantId.New();
            if (effectiveLifetime != GrantLifetime.Once && effectiveIntent is null && additionalEffectiveIntents.Count == 0)
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
            if (execContext.ProtectedSubstitution is { } substitution)
                _emit(new ToolPreferenceSubstitutionApproved(validated.ToolCallId, interactionId,
                    substitution.SourceToolId, substitution.TargetToolId, substitution.IdentityHash));
            _emit(new PermissionGranted(validated.ToolCallId, approvedGrant, effectiveLifetime));
            // INV-002: la política ya quedó evaluada con la aprobación humana; no se pide un
            // nuevo permiso. La política de Security comprueba de nuevo que no exista Deny.
            try
            {
                var approved = AuthorizeIntents(intent, effectiveIntent, approvedGrant, approved: true, additionalEffectiveIntents);
                _emit(new ToolCallAuthorized(validated.ToolCallId));
                return Execute(validated, approved, intent, tool, execContext, cancellationToken,
                    boundaryAskApproved: boundaryAsk, effectiveIntent: effectiveIntent,
                    permissionAskApproved: true, additionalEffectiveIntents: additionalEffectiveIntents);
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
            var authorized = AuthorizeIntents(intent, effectiveIntent, null, approved: false, additionalEffectiveIntents);
            _emit(new ToolCallAuthorized(validated.ToolCallId));
        return Execute(validated, authorized, intent, tool, execContext, cancellationToken,
                boundaryAskApproved: boundaryAsk, effectiveIntent: effectiveIntent,
                permissionAskApproved: false, additionalEffectiveIntents: additionalEffectiveIntents,
                automaticApply: automaticApply,
                automaticApplyEligible: decision.Final == PermissionDecision.Allow
                    && automaticSourceRequested && !execContext.RequiresSubstitutionAsk,
                automaticSourceIntent: automaticSourceIntent);
        }
        catch (OmniCore.Domain.PermissionDeniedException ex)
        {
            _emit(new PermissionDenied(validated.ToolCallId, ex.Reason));
            return new Outcome(false, ex.Reason, ToolCallState.Rejected, EffectOutcome.None);
        }
    }

    private Outcome Execute(ValidatedToolCall call, AuthorizedToolIntent authorized, ToolIntent intent, ITool tool,
        ToolExecutionContext execContext, CancellationToken cancellationToken, bool boundaryAskApproved,
        ToolIntent? effectiveIntent = null, bool permissionAskApproved = false,
        IReadOnlyList<ToolIntent>? additionalEffectiveIntents = null,
        bool automaticApplyEligible = false, bool automaticApply = false,
        ToolIntent? automaticSourceIntent = null)
    {
        if (tool is FilesystemWriteTool writeTool)
        {
            if (!writeTool.TargetMatchesAuthorizedClassification(intent, execContext.WorkspaceRoot,
                    execContext.MutationHistory))
            {
                const string cause = "filesystem.write target changed after its effective operation was authorized.";
                _emit(new ToolCallFailed(call.ToolCallId, cause, EffectOutcome.None, ToolErrorCode.StaleWrite));
                return new Outcome(false, cause, null, ToolCallState.Failed, EffectOutcome.None);
            }
            if (!writeTool.TryDetermineAuthorizedOperation(intent, execContext, cancellationToken,
                    out var actualOperation, out var preflightError, out var preflightReason))
            {
                var cause = preflightReason ?? "filesystem.write normalization preflight failed.";
                _emit(new ToolCallFailed(call.ToolCallId, cause, EffectOutcome.None,
                    preflightError ?? ToolErrorCode.ToolFailure));
                return new Outcome(false, cause, null, ToolCallState.Failed, EffectOutcome.None);
            }
            intent = new ToolIntent(intent.ToolCallId, intent.ToolId, intent.NormalizedArgumentsJson,
                intent.Effect, intent.Claims, intent.Risk, intent.Reconciliation, actualOperation,
                intent.RequiresPhysicalCreateCapability, intent.EffectiveMutationHistoryRevision);
        }

        additionalEffectiveIntents ??= [];
        if (tool is IHostResolvedAuthorizationIntent hostResolver)
        {
            try
            {
                var refreshed = hostResolver.ResolveHostAuthorizationIntent(intent,
                    new ToolPreparationContext(execContext.WorkspaceRoot, DateTimeOffset.UtcNow,
                        execContext.IsNativeUserAction),
                    execContext, cancellationToken);
                ValidateHostResolvedAuthorizationIntent(intent, refreshed);
                if (!string.Equals(IntentFingerprint(intent, effectiveIntent),
                        IntentFingerprint(refreshed, effectiveIntent), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Host resource authorization changed before execution.");
                intent = refreshed;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
            {
                _emit(new ToolCallFailed(call.ToolCallId, "Host resource authorization changed before execution.",
                    EffectOutcome.None, ToolErrorCode.StaleWrite));
                return new Outcome(false, "Host resource authorization changed before execution.",
                    null, ToolCallState.Failed, EffectOutcome.None);
            }
        }
        else if (tool is IEffectiveToolAuthorizationIntents batchResolver)
        {
            IReadOnlyList<ToolIntent> refreshed;
            try
            {
                refreshed = batchResolver.ResolveEffectiveAuthorizationIntents(intent,
                    new ToolPreparationContext(execContext.WorkspaceRoot, DateTimeOffset.UtcNow), execContext, cancellationToken);
                ValidateEffectiveBatchIntents(intent, refreshed, execContext.WorkspaceRoot);
                if (PreflightEffectiveMutationBatch(refreshed, execContext) is { } refusal)
                    throw new ToolPreflightException(refusal.Code, refusal.Message);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                or System.Text.Json.JsonException or InvalidOperationException or ArgumentException
                or ToolPreflightException)
            {
                var code = ex is ToolPreflightException preflight ? preflight.ErrorCode : ToolErrorCode.StaleWrite;
                var message = ex is ToolPreflightException ? ex.Message : "Batch authorization changed before execution.";
                _emit(new ToolCallFailed(call.ToolCallId, message, EffectOutcome.None, code));
                return new Outcome(false, message, null, ToolCallState.Failed, EffectOutcome.None);
            }
            if (!string.Equals(IntentFingerprint(intent, effectiveIntent, refreshed),
                    IntentFingerprint(intent, effectiveIntent, additionalEffectiveIntents), StringComparison.OrdinalIgnoreCase))
            {
                _emit(new ToolCallFailed(call.ToolCallId, "Batch targets changed after authorization.",
                    EffectOutcome.None, ToolErrorCode.StaleWrite));
                return new Outcome(false, "Batch targets changed after authorization.", null,
                    ToolCallState.Failed, EffectOutcome.None);
            }
            additionalEffectiveIntents = refreshed;
        }
        IReadOnlyList<ToolIntent> effectiveIntents = new[] { intent }
            .Concat(effectiveIntent is null ? [] : [effectiveIntent]).Concat(additionalEffectiveIntents).ToArray();
        if (intent.ToolId.ToString() == "extension.batch.apply" && additionalEffectiveIntents.Count > 0)
            effectiveIntents = additionalEffectiveIntents;
        var currentPermission = _policy.Evaluate(automaticApply && IsTrustedBatchPublicationTool(tool)
            ? authorized.Intent : intent);
        if (effectiveIntent is not null)
            currentPermission = CombinePermissionDecisions(currentPermission,
                _policy.Evaluate(effectiveIntent), "effective filesystem.patch");
        foreach (var batchIntent in additionalEffectiveIntents)
            currentPermission = CombinePermissionDecisions(currentPermission,
                _policy.Evaluate(batchIntent), "effective batch operation");
        if (currentPermission.Final == PermissionDecision.Deny
            || currentPermission.Final == PermissionDecision.Ask && !permissionAskApproved)
        {
            var cause = currentPermission.Final == PermissionDecision.Deny
                ? "Permission was denied before the effect barrier."
                : "Permission approval is required before the effect barrier.";
            _emit(new ToolCallFailed(call.ToolCallId, cause, EffectOutcome.None, ToolErrorCode.PermissionDenied));
            return new Outcome(false, cause, null, ToolCallState.Failed, EffectOutcome.None);
        }

        // 4a. Frontera de capacidad otra vez antes de ejecutar (ADR-0044 §5): el intent ya está
        // autorizado; si el techo cambió o la primera evaluación se saltó, Authorized → Failed.
        foreach (var activeBoundary in ActiveBoundaries())
        foreach (var boundaryIntent in effectiveIntents)
        {
            var boundaryDecision = EvaluateBoundary(activeBoundary, tool, boundaryIntent);
            if (!boundaryDecision.Allowed)
            {
                _emit(new ToolCallFailed(call.ToolCallId,
                    "frontera de capacidad: " + boundaryDecision.Reason, EffectOutcome.None,
                    ToolErrorCode.CapabilityRefused));
                return new Outcome(false, boundaryDecision.Reason, null, ToolCallState.Failed,
                    EffectOutcome.None);
            }
        }

        // A native child lane may suspend only when the registered process tool proves Strong is
        // unavailable before invoking any launcher. The interaction is durable while the call is
        // still Authorized; no ToolCallStarted or process effect has occurred.
        if (execContext.AllowDeferredWeakSandboxConsent && !execContext.WeakSandboxConsentForThisCall
            && tool is IWeakSandboxConsentPreflightTool preflightTool)
        {
            WeakSandboxConsentPreflight? preflight;
            try
            {
                preflight = preflightTool.CheckWeakSandboxConsentPreflight(intent,
                    new ToolPreparationContext(execContext.WorkspaceRoot, DateTimeOffset.UtcNow), cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // An inconclusive preflight is not permission to downgrade. The normal Strong
                // attempt will remain subject to the started/unknown path below.
                preflight = null;
            }
            if (preflight is { } requestDetails)
            {
                using var payloadBuffer = new System.IO.MemoryStream();
                using (var payloadWriter = new System.Text.Json.Utf8JsonWriter(payloadBuffer))
                {
                    payloadWriter.WriteStartObject();
                    payloadWriter.WriteString("operation", "sandbox.weak_consent");
                    payloadWriter.WriteString("reason", requestDetails.ReasonCode);
                    payloadWriter.WriteString("risk", "high");
                    payloadWriter.WriteString("executable", requestDetails.Executable);
                    payloadWriter.WriteString("workingDirectory", requestDetails.WorkingDirectory);
                    payloadWriter.WriteString("intentFingerprint", IntentFingerprint(intent));
                    payloadWriter.WriteEndObject();
                }
                var interaction = new InteractionRequested(InteractionId.New(),
                    InteractionKind.WeakSandboxConsent,
                    System.Text.Encoding.UTF8.GetString(payloadBuffer.ToArray()),
                    "[{\"id\":\"consent_once\",\"intent\":\"allow\",\"lifetime\":\"once\"},"
                        + "{\"id\":\"deny\",\"intent\":\"deny\"}]",
                    "deny", null, null, null, null, 0, 1);
                _emit(interaction);
                return new Outcome(false, "awaiting input", null, ToolCallState.Authorized,
                    EffectOutcome.None, interaction.InteractionId);
            }
        }

        // 4. Ejecución (Barrier si hay efecto; ADR-0002 §2, ADR-0004 §2). El Started lleva, cuando el
        // intent declara una reconciliación con pre/post y EXACTAMENTE un write target, la serialización
        // canónica de los metadatos (ruta + hashes) para que un crash posterior pueda reconciliar el
        // efecto desde el journal sin re-ejecutar (ADR-0004 §4). Si la tool calcula esos metadatos
        // (IReconcilableTool), se piden aquí: ya autorizada y antes del efecto, nunca en Prepare.
        IDisposable? workspaceWriteLease = null;
        var privateStagedWriter = tool is IStagedExtensionBatchProducer stagedWriter
            && stagedWriter.IsTrustedPrivateWorkspaceWriter(intent)
            && intent.Claims.Process?.Isolation is ProcessIsolation.PrivateExtensionWorkspace or ProcessIsolation.PrivateProcessWorkspace;
        try
        {
            if (intent.Effect != EffectClass.None && (intent.Claims.Writes.Count > 0
                || additionalEffectiveIntents.Any(effective => effective.Claims.Writes.Count > 0))
                && !privateStagedWriter)
            {
                if (execContext.WorkspaceWriteLeases is not null)
                    workspaceWriteLease = execContext.WorkspaceWriteLeases.Acquire(execContext.WorkspaceRoot, cancellationToken);
                else if (tool is IRequiresReconciliationTool)
                {
                    const string cause = "Workspace write exclusion is unavailable.";
                    _emit(new ToolCallFailed(call.ToolCallId, cause, EffectOutcome.None, ToolErrorCode.ToolFailure));
                    return new Outcome(false, cause, ToolCallState.Failed, EffectOutcome.None);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            const string cause = "Workspace write exclusion could not be acquired.";
            _emit(new ToolCallFailed(call.ToolCallId, cause, EffectOutcome.None, ToolErrorCode.ToolFailure));
            return new Outcome(false, cause, ToolCallState.Failed, EffectOutcome.None);
        }
        using var heldWorkspaceWrite = new IdempotentLease(workspaceWriteLease);

        IDisposable? durableWorkspaceFence = null;
        try
        {
            var toolName = tool.Descriptor.Id.ToString();
            if (execContext.MutationHistory is not null && intent.Effect != EffectClass.None
                && (intent.Claims.Writes.Count > 0
                    || additionalEffectiveIntents.Any(effective => effective.Claims.Writes.Count > 0))
                && toolName is ("filesystem.write" or "filesystem.patch" or "worktree.integrate" or "extension.batch.apply"))
            {
                durableWorkspaceFence = execContext.MutationHistory.AcquireFence(execContext.WorkspaceRoot,
                    cancellationToken);
                execContext.MutationHistory.RecoverPending(execContext.WorkspaceRoot, cancellationToken);
                RefreshMutationBudgetUnderFence(execContext);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            const string cause = "Durable workspace mutation exclusion could not be acquired.";
            _emit(new ToolCallFailed(call.ToolCallId, cause, EffectOutcome.None, ToolErrorCode.ToolFailure));
            return new Outcome(false, cause, ToolCallState.Failed, EffectOutcome.None);
        }
        using var heldDurableWorkspaceFence = durableWorkspaceFence;

        // Authorization may have paused for a human Ask before this OS fence was acquired.
        // Rebind the write's physical/logical target and diff-derived operation under the fence
        // so a tombstone cannot become a free create (or vice versa) while consent is pending.
        if (tool is FilesystemWriteTool fencedWrite)
        {
            if (!fencedWrite.TargetMatchesAuthorizedClassification(intent, execContext.WorkspaceRoot,
                    execContext.MutationHistory)
                || !fencedWrite.TryDetermineAuthorizedOperation(intent, execContext, cancellationToken,
                    out var fencedOperation, out _, out _)
                || fencedOperation != intent.EffectiveMutationOperation)
            {
                const string cause = "filesystem.write target changed while waiting for its durable mutation fence.";
                _emit(new ToolCallFailed(call.ToolCallId, cause, EffectOutcome.None, ToolErrorCode.StaleWrite));
                return new Outcome(false, cause, null, ToolCallState.Failed, EffectOutcome.None);
            }
        }

        IDisposable? publication;
        try
        {
            publication = tool is IReconcilableTool && intent.Effect != EffectClass.None && execContext.Artifacts is not null
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
            var trustedIntegrationTool = IsTrustedIntegrationTool(tool);
            try
            {
                reconciliation = reconcilable.DescribeReconciliation(authorized, execContext, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (ToolPreflightException ex)
            {
                _emit(new ToolCallFailed(call.ToolCallId, ex.Message, EffectOutcome.None, ex.ErrorCode));
                return new Outcome(false, ex.Message, null, ToolCallState.Failed, EffectOutcome.None);
            }
            catch (FilesystemPreimageException)
            {
                const string cause = "Filesystem pre-image could not be captured faithfully and safely.";
                _emit(new ToolCallFailed(call.ToolCallId, cause, EffectOutcome.None, ToolErrorCode.ToolFailure));
                return new Outcome(false, cause, ToolCallState.Failed, EffectOutcome.None);
            }
            catch (Exception)
            {
                if (tool is IRequiresReconciliationTool)
                {
                    const string cause = "Required effect metadata could not be prepared safely.";
                    _emit(new ToolCallFailed(call.ToolCallId, cause, EffectOutcome.None, ToolErrorCode.ToolFailure));
                    return new Outcome(false, cause, ToolCallState.Failed, EffectOutcome.None);
                }
                reconciliation = null; // legado no requerido: reconciliación conservadora
            }
        }

        var trustedIntegration = IsTrustedIntegrationTool(tool);

        if (tool is IRequiresReconciliationTool && (reconciliation is null
            || execContext.Artifacts is null || reconciliation.BeforeStateRef is null
            || string.IsNullOrWhiteSpace(reconciliation.DurableMetadataJson)))
        {
            const string cause = "Required effect metadata is unavailable.";
            _emit(new ToolCallFailed(call.ToolCallId, cause, EffectOutcome.None, ToolErrorCode.ToolFailure));
            return new Outcome(false, cause, ToolCallState.Failed, EffectOutcome.None);
        }

        var trustedBatchPublication = IsTrustedBatchPublicationTool(tool);
        var trustedDisposition = IsTrustedWorktreeDispositionTool(tool);
        var validRequiredEnvelope = reconciliation is not null && (IsTrustedIntegrationTool(tool)
            ? ValidateIntegrationEnvelope(intent, execContext, reconciliation)
            : trustedBatchPublication && ((ITrustedBatchPublicationTool)tool)
                .ValidatePublicationEnvelope(intent, effectiveIntents, execContext, reconciliation)
            || trustedDisposition && ((ITrustedWorktreeDispositionTool)tool)
                .ValidateWorktreeDispositionEnvelope(intent, execContext, reconciliation));
        if (tool is IRequiresReconciliationTool && !validRequiredEnvelope)
        {
            const string cause = "Required batch metadata did not match its authorized targets.";
            _emit(new ToolCallFailed(call.ToolCallId, cause, EffectOutcome.None, ToolErrorCode.ToolFailure));
            return new Outcome(false, cause, ToolCallState.Failed, EffectOutcome.None);
        }

        if (reconciliation?.DurableMetadataJson is not null && !trustedIntegration && !trustedBatchPublication
            && !trustedDisposition)
        {
            const string cause = "Custom reconciliation metadata is restricted to the registered integration tool.";
            _emit(new ToolCallFailed(call.ToolCallId, cause, EffectOutcome.None, ToolErrorCode.ToolFailure));
            return new Outcome(false, cause, ToolCallState.Failed, EffectOutcome.None);
        }

        if (reconciliation?.StartedEntityEvent is not null && !trustedIntegration)
        {
            const string cause = "Custom start events are restricted to the registered integration tool.";
            _emit(new ToolCallFailed(call.ToolCallId, cause, EffectOutcome.None, ToolErrorCode.ToolFailure));
            return new Outcome(false, cause, ToolCallState.Failed, EffectOutcome.None);
        }

        var durableReconciliation = reconciliation?.DurableMetadataJson
            ?? ReconciliationJsonFor(intent.Claims, reconciliation);
        // Serialize the last Session-ceiling read with a policy revision and the Started Barrier.
        // Once Started is durable, a concurrent policy downgrade sees the open effect and defers.
        using (intent.Effect != EffectClass.None || call.ToolId.ToString() == "core.agents.mailbox.receive"
            ? execContext.AcquireSessionSecurityAdmission?.Invoke() : null)
        {
            if (PreflightEffectiveMutationBatch(additionalEffectiveIntents, execContext) is { } budgetRefusal)
            {
                _emit(new ToolCallFailed(call.ToolCallId, budgetRefusal.Message, EffectOutcome.None, budgetRefusal.Code));
                return new Outcome(false, budgetRefusal.Message, null, ToolCallState.Failed, EffectOutcome.None);
            }
            var finalPermission = _policy.Evaluate(automaticApply && IsTrustedBatchPublicationTool(tool)
                ? authorized.Intent : intent);
            if (effectiveIntent is not null)
                finalPermission = CombinePermissionDecisions(finalPermission,
                    _policy.Evaluate(effectiveIntent), "effective filesystem.patch");
            foreach (var batchIntent in additionalEffectiveIntents)
                finalPermission = CombinePermissionDecisions(finalPermission,
                    _policy.Evaluate(batchIntent), "effective batch operation");
            if (_policy is IAutomaticExtensionBatchAuthority currentAuthority)
            {
                var hostScopedProcessEffect = tool is IHostScopedPrivateProcessControl processControl
                    && processControl.IsTrustedPrivateProcessControl(intent)
                    || tool is IStagedExtensionBatchProducer processWriter
                    && processWriter.IsTrustedPrivateWorkspaceWriter(intent)
                    && (intent.ToolId.ToString() is "process.start" or "process.exec" or "shell.exec");
                if (hostScopedProcessEffect)
                {
                    var current = currentAuthority.EvaluateCurrentPermission(intent);
                    finalPermission = CombinePermissionDecisions(finalPermission,
                        new PermissionDecisionRecord(current,
                            [new LayerDecision("current-user-permissions", current, "permissions.yaml")], null),
                        "current process permission");
                }
                if (automaticApply && automaticSourceIntent is not null)
                {
                    var sourceCurrent = currentAuthority.EvaluateCurrentPermission(automaticSourceIntent);
                    finalPermission = CombinePermissionDecisions(finalPermission,
                        new PermissionDecisionRecord(sourceCurrent,
                            [new LayerDecision("current-user-permissions", sourceCurrent, "permissions.yaml")], null),
                        "current extension source permission");
                    if (!currentAuthority.AllowsAutomaticExtensionBatchCurrently(automaticSourceIntent))
                        finalPermission = CombinePermissionDecisions(finalPermission,
                            new PermissionDecisionRecord(PermissionDecision.Deny,
                                [new LayerDecision("current-user-permissions", PermissionDecision.Deny, "automatic-source-revoked")], null),
                            "current automatic source authority");
                }
                foreach (var effective in additionalEffectiveIntents)
                {
                    var current = currentAuthority.EvaluateCurrentPermission(effective);
                    finalPermission = CombinePermissionDecisions(finalPermission,
                        new PermissionDecisionRecord(current,
                            [new LayerDecision("current-user-permissions", current, "permissions.yaml")], null),
                        "current effective file permission");
                }
            }
            if (finalPermission.Final == PermissionDecision.Deny
                || finalPermission.Final == PermissionDecision.Ask && !permissionAskApproved)
            {
                _emit(new ToolCallFailed(call.ToolCallId,
                    "Permission changed before the effect barrier.", EffectOutcome.None,
                    ToolErrorCode.PermissionDenied));
                return new Outcome(false, "permission changed before effect", null, ToolCallState.Failed,
                    EffectOutcome.None);
            }
            foreach (var activeBoundary in ActiveBoundaries())
            foreach (var boundaryIntent in effectiveIntents)
            {
                var boundaryDecision = EvaluateBoundary(activeBoundary, tool, boundaryIntent);
                if (!boundaryDecision.Allowed || (boundaryDecision.RequiresAsk && !boundaryAskApproved))
                {
                    _emit(new ToolCallFailed(call.ToolCallId,
                        boundaryDecision.RequiresAsk && !boundaryAskApproved
                            ? "session_security.approval_required"
                            : "frontera de capacidad: " + boundaryDecision.Reason, EffectOutcome.None,
                        ToolErrorCode.CapabilityRefused));
                    return new Outcome(false, boundaryDecision.Reason, null, ToolCallState.Failed,
                        EffectOutcome.None);
                }
            }
            execContext.BeforeEffect?.Invoke(intent);
            if (reconciliation?.StartedEntityEvent is { } entityEvent) _emit(entityEvent);
            _emit(new ToolCallStarted(call.ToolCallId, intent.Effect, durableReconciliation)
            {
                Attribution = new ToolCallAttribution(tool.Descriptor.Id.ToString(), tool.Descriptor.Source.Kind.ToString(),
                    tool.Descriptor.Source.Kind == SourceKind.Extension ? tool.Descriptor.Source.Owner : null,
                    tool.Descriptor.Source.Version, tool.Descriptor.Source.ContentIdentity),
                // Attribution is not proof of reversibility: pre/post hashes cannot restore bytes.
                TargetRef = RelativeTargetRef(intent.Claims.Writes, execContext.WorkspaceRoot),
                Reversibility = reconciliation?.Reversibility ?? Reversibility.Unknown,
                BeforeStateRef = reconciliation?.BeforeStateRef,
            });
        }

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
                result.ErrorCode) { ActivityEvidence = result.ActivityEvidence });
            return new Outcome(false, result.Summary, result.Preview, ToolCallState.Failed, result.EffectOutcome);
        }

        var effect = result.EffectOutcome;
        if (effect == EffectOutcome.Unknown)
        {
            _emit(new ToolCallFailed(call.ToolCallId, "efecto desconocido", effect, ToolErrorCode.UnknownEffect));
            return new Outcome(false, result.Summary, result.Preview, ToolCallState.Failed, effect);
        }

        if (automaticApplyEligible && tool is IStagedExtensionBatchProducer stagedProducer
            && stagedProducer.IsTrustedPrivateWorkspaceWriter(intent) && result.Artifact is { } proposalArtifact
            && result.Preview is { Length: > 0 } proposalPreview
            && _catalog.Find(new ToolId("extension.batch.apply")) is { } applyTool)
        {
            try
            {
                using var proposalDocument = System.Text.Json.JsonDocument.Parse(proposalPreview);
                var proposalRoot = proposalDocument.RootElement;
                var proposalId = proposalRoot.GetProperty("proposalId").GetString()!;
                var artifactHash = proposalRoot.GetProperty("artifactHash").GetString()!;
                var artifactSize = proposalRoot.GetProperty("artifactSize").GetInt64();
                if (artifactHash == proposalArtifact.Hash.Value && artifactSize == proposalArtifact.Size)
                {
                    var encodedProposalId = System.Text.Json.JsonEncodedText.Encode(proposalId).ToString();
                    var encodedArtifactHash = System.Text.Json.JsonEncodedText.Encode(artifactHash).ToString();
                    var arguments = $"{{\"proposalId\":\"{encodedProposalId}\",\"artifactHash\":\"{encodedArtifactHash}\",\"artifactSize\":{artifactSize}}}";
                    var internalCall = new ValidatedToolCall(ToolCallId.New(), applyTool.Descriptor.Id,
                        "internal-extension-batch-" + call.ToolCallId, arguments);
                    // The private stage has produced a protected proposal. Release its write
                    // lease before the publisher acquires the same workspace lease; publication
                    // revalidates the complete preimage and takes its own lease/fence.
                    heldWorkspaceWrite.Dispose();
                    var applied = RunCore(internalCall,
                        new ToolPreparationContext(execContext.WorkspaceRoot, DateTimeOffset.UtcNow),
                        execContext, userApprovesAsk: false, cancellationToken, GrantLifetime.Once,
                        requestReceipt: null, automaticApply: true, parentToolCallId: call.ToolCallId,
                        automaticSourceIntent: intent);
                    if (applied.Effect == EffectOutcome.Unknown || applied.FinalState == ToolCallState.EffectUnknown)
                    {
                        const string uncertain = "Extension process completed; batch publication is uncertain and requires reconciliation. Do not rerun the extension.";
                        _emit(new ToolCallEffectUnknown(call.ToolCallId, intent.Effect));
                        return new Outcome(false, uncertain, ExtensionPublicationPreview(proposalPreview, "unknown"), ToolCallState.EffectUnknown,
                            EffectOutcome.Unknown, artifact: result.Artifact);
                    }
                    result = applied.Succeeded
                        ? new ToolResult("Extension execution and its complete write batch were automatically published.",
                            ExtensionPublicationPreview(proposalPreview, "applied"), result.Artifact, result.OriginalSize,
                            result.WasExternalized, EffectOutcome.Applied)
                        : new ToolResult("Extension proposal is retained; automatic publication was blocked by current policy or a stale target.",
                            ExtensionPublicationPreview(proposalPreview, "blocked"), result.Artifact, result.OriginalSize,
                            result.WasExternalized, EffectOutcome.Applied);
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
            {
                result = new ToolResult("Extension proposal is retained; automatic publication could not be resolved safely.",
                    ExtensionPublicationPreview(result.Preview, "blocked"), result.Artifact, result.OriginalSize,
                    result.WasExternalized, EffectOutcome.Applied);
            }
        }
        var processReadThrough = tool.Descriptor.Id.ToString() == "process.read"
            ? ProcessReadThrough(result.Preview) : null;
        _emit(new ToolCallSucceeded(call.ToolCallId, "{\"summary\":\"" + Esc(result.Summary) + "\""
            + (processReadThrough is { } delivered ? ",\"displayOutputThrough\":"
                + delivered.ToString(System.Globalization.CultureInfo.InvariantCulture) : "") + "}")
        { AfterStateRef = tool is FilesystemWriteTool or FilesystemPatchTool && result.AfterStateBytes is { } bytes
            && reconciliation?.ExpectedPostHash == FileVersion.VersionToken(bytes)
                ? FilesystemPreimage.CaptureAfter(execContext.Artifacts, bytes) : null,
            ResultArtifact = result.Artifact, ActivityEvidence = result.ActivityEvidence,
            DisplayOutput = tool.Descriptor.Id.ToString() == "process.read"
                ? BoundedProcessDisplayOutput(result.Preview) : null });
        // Preview: contenido real de la herramienta (p. ej. el archivo leído) → vuelve al modelo.
        return new Outcome(true, result.Summary, result.Preview, ToolCallState.Succeeded, effect,
            artifact: result.Artifact);
    }

    private sealed class IdempotentLease(IDisposable? inner) : IDisposable
    {
        private IDisposable? _inner = inner;
        public void Dispose() => Interlocked.Exchange(ref _inner, null)?.Dispose();
    }

    private static string? BoundedProcessDisplayOutput(string? preview)
    {
        if (string.IsNullOrEmpty(preview) || System.Text.Encoding.UTF8.GetByteCount(preview) > 256 * 1024) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(preview);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("output", out var chunks) || chunks.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;
            var result = new System.Text.StringBuilder();
            if (root.TryGetProperty("gap", out var gap) && gap.ValueKind == System.Text.Json.JsonValueKind.True)
                result.Append("[output gap]\n");
            foreach (var chunk in chunks.EnumerateArray())
            {
                if (chunk.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !chunk.TryGetProperty("text", out var textValue) || textValue.ValueKind != System.Text.Json.JsonValueKind.String)
                    continue;
                var text = textValue.GetString() ?? string.Empty;
                var stream = chunk.TryGetProperty("stream", out var streamValue)
                    && streamValue.ValueKind == System.Text.Json.JsonValueKind.String ? streamValue.GetString() : null;
                if (result.Length > 0 && result[^1] != '\n') result.AppendLine();
                if (stream is "stdout" or "stderr") result.Append('[').Append(stream).Append("] ");
                var remaining = 16 * 1024 - result.Length;
                if (remaining <= 0) break;
                result.Append(text.AsSpan(0, Math.Min(text.Length, remaining)));
                if (result.Length >= 16 * 1024) break;
            }
            return result.Length == 0 ? null : result.ToString();
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static long? ProcessReadThrough(string? preview)
    {
        if (string.IsNullOrEmpty(preview) || System.Text.Encoding.UTF8.GetByteCount(preview) > 256 * 1024)
            return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(preview);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("deliveredThrough", out var cursor)
                && cursor.TryGetInt64(out var value) && value >= 0 ? value : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string ExtensionPublicationPreview(string? proposalPreview, string status)
    {
        try
        {
            using var source = System.Text.Json.JsonDocument.Parse(proposalPreview ?? "{}");
            var root = source.RootElement;
            var proposalId = default(System.Text.Json.JsonElement);
            var artifactHash = default(System.Text.Json.JsonElement);
            var artifactSize = default(System.Text.Json.JsonElement);
            var hasProtectedReference = root.ValueKind == System.Text.Json.JsonValueKind.Object
                && root.TryGetProperty("proposalId", out proposalId)
                && proposalId.ValueKind == System.Text.Json.JsonValueKind.String
                && Guid.TryParseExact(proposalId.GetString(), "N", out _)
                && root.TryGetProperty("artifactHash", out artifactHash)
                && artifactHash.ValueKind == System.Text.Json.JsonValueKind.String
                && IsSha256Hex(artifactHash.GetString())
                && root.TryGetProperty("artifactSize", out artifactSize)
                && artifactSize.ValueKind == System.Text.Json.JsonValueKind.Number
                && artifactSize.TryGetInt64(out var artifactSizeValue)
                && artifactSizeValue is > 0 and <= 16 * 1024 * 1024;
            using var buffer = new System.IO.MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("publication", status);
                if (hasProtectedReference)
                {
                    writer.WriteString("proposalId", proposalId.GetString());
                    writer.WriteString("artifactHash", artifactHash.GetString());
                    writer.WriteNumber("artifactSize", artifactSize.GetInt64());
                }
                if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                    && root.TryGetProperty("files", out var files)
                    && files.ValueKind == System.Text.Json.JsonValueKind.Array
                    && files.GetArrayLength() is > 0 and <= 64
                    && files.EnumerateArray().All(file => file.ValueKind == System.Text.Json.JsonValueKind.String
                        && IsSafeExtensionBatchPreviewPath(file.GetString())))
                {
                    writer.WritePropertyName("files");
                    files.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (System.Text.Json.JsonException)
        {
            return "{\"publication\":\"" + status + "\"}";
        }
    }

    private static bool IsSha256Hex(string? value) => value is { Length: 64 }
        && value.All(Uri.IsHexDigit);

    private static bool IsSafeExtensionBatchPreviewPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) return false;
        var normalized = value.Replace('\\', '/');
        var parts = normalized.Split('/');
        return !Path.IsPathRooted(normalized) && !normalized.Contains(':')
            && parts.All(part => part is not ("" or "." or ".."))
            && !parts[0].Equals(".git", StringComparison.OrdinalIgnoreCase)
            && !new RedactionPolicy().IsSecretPath(normalized);
    }

    /// <summary>Rebuilds the model's Run/Turn mutation counters from durable receipts while
    /// holding the cross-process workspace fence. A second Host/Lane can therefore not spend a
    /// stale in-memory budget snapshot after waiting for this same fence.</summary>
    private static void RefreshMutationBudgetUnderFence(ToolExecutionContext context)
    {
        var ledger = context.ReadRegistry?.Ledger;
        var history = context.MutationHistory;
        if (ledger is null || history is null || string.IsNullOrWhiteSpace(context.MutationRunId)
            || string.IsNullOrWhiteSpace(context.MutationTurnId)) return;

        var receipts = history.ReadBudgetChargesForRun(context.WorkspaceRoot, context.MutationRunId);
        foreach (var receipt in receipts)
        {
            if (receipt.State != WorkspaceMutationReceiptState.Applied || receipt.DeletedLines < 0
                || receipt.InsertedLines < 0 || string.IsNullOrWhiteSpace(receipt.RelativePath))
            {
                ledger.MarkRunBudgetUnknown();
                if (string.Equals(receipt.TurnId, context.MutationTurnId, StringComparison.Ordinal))
                    ledger.MarkTurnBudgetUnknown();
                continue;
            }

            int changedLines;
            try { changedLines = checked(receipt.DeletedLines + receipt.InsertedLines); }
            catch (OverflowException)
            {
                ledger.MarkRunBudgetUnknown();
                if (string.Equals(receipt.TurnId, context.MutationTurnId, StringComparison.Ordinal))
                    ledger.MarkTurnBudgetUnknown();
                continue;
            }

            var targetKey = context.ReadRegistry!.MutationBudgetKey(receipt.RelativePath);
            ledger.RestoreDurableBudgetCharge(receipt.ToolCallId, targetKey, changedLines,
                string.Equals(receipt.TurnId, context.MutationTurnId, StringComparison.Ordinal));
        }
    }

    private static MutationRefusal? PreflightEffectiveMutationBatch(IReadOnlyList<ToolIntent> intents,
        ToolExecutionContext context)
    {
        if (intents.Count == 0) return null;
        var proposals = intents.Select(item => item.EffectiveMutationProposal).ToArray();
        if (proposals.Any(item => item is null))
            return new MutationRefusal(ToolErrorCode.LimitExceeded,
                "Every file in a published batch must have a complete mutation budget proposal.");
        if (context.ReadRegistry is null)
            return new MutationRefusal(ToolErrorCode.LimitExceeded,
                "Mutation budget state is unavailable for the complete batch.");
        return context.ReadRegistry.Ledger.RefuseMutationBatch(proposals.Select(item => item!).ToArray());
    }

    private static void ValidateEffectiveBatchIntents(ToolIntent parent, IReadOnlyList<ToolIntent> intents,
        string workspaceRoot)
    {
        if (intents is null || intents.Count is < 1 or > 64)
            throw new InvalidDataException("Effective batch intent count is outside its safe bound.");
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var targets = new HashSet<string>(comparer);
        foreach (var item in intents)
        {
            if (item is null || item.ToolCallId != parent.ToolCallId || item.Claims.Writes.Count != 1
                || item.EffectiveMutationProposal is null)
                throw new InvalidDataException("Effective batch intent identity or mutation proposal is invalid.");
            var path = Path.GetFullPath(item.Claims.Writes[0], workspaceRoot);
            if (!targets.Add(path)) throw new InvalidDataException("Effective batch contains duplicate targets.");
        }
    }

    private static void ValidateHostResolvedAuthorizationIntent(ToolIntent parent, ToolIntent resolved)
    {
        var claims = resolved.Claims;
        if (resolved.ToolCallId != parent.ToolCallId || resolved.ToolId != parent.ToolId
            || resolved.NormalizedArgumentsJson != parent.NormalizedArgumentsJson
            || resolved.Effect != parent.Effect || resolved.Risk != parent.Risk
            || claims.WorktreeDisposition is null || claims.Reads.Count != 0 || claims.Writes.Count != 0
            || claims.Network.Count != 0 || claims.Process is not null || claims.Secrets.Count != 0)
            throw new InvalidDataException("Host-resolved resource intent does not match its exact native tool call.");
    }
    private IEnumerable<IToolIntentBoundary> ActiveBoundaries()
    {
        if (_boundary is not null) yield return _boundary;
        foreach (var boundary in _additionalBoundaries) yield return boundary;
    }

    private static ModelCapabilityDecision EvaluateBoundary(IToolIntentBoundary boundary, ITool tool,
        ToolIntent intent)
    {
        if (boundary is ModelCapabilityBoundary
            && ((tool is IStagedExtensionBatchProducer producer
                 && producer.IsTrustedPrivateWorkspaceWriter(intent)
                 && intent.Claims.Process?.Isolation is ProcessIsolation.PrivateExtensionWorkspace or ProcessIsolation.PrivateProcessWorkspace
                 && intent.Claims.Writes.Count > 0)
                || (tool is IHostScopedPrivateProcessControl processControl
                    && processControl.IsTrustedPrivateProcessControl(intent)
                    && intent.Claims.Process?.Isolation == ProcessIsolation.PrivateProcessWorkspace)))
        {
            // Process writes land only in the Host-created private copy. The checkout mutations
            // are separately resolved as native filesystem intents by extension.batch.apply.
            var stagedClaims = new ResourceClaims(intent.Claims.Reads, [], intent.Claims.Network,
                null,
                intent.Claims.Secrets, intent.Claims.WorktreeDisposition);
            var stagedIntent = new ToolIntent(intent.ToolCallId, intent.ToolId, intent.NormalizedArgumentsJson,
                intent.Effect, stagedClaims, intent.Risk, intent.Reconciliation,
                intent.EffectiveMutationOperation, intent.RequiresPhysicalCreateCapability,
                intent.EffectiveMutationHistoryRevision, intent.EffectiveMutationProposal);
            return boundary.Evaluate(stagedIntent);
        }
        return boundary.Evaluate(intent);
    }

    private static bool IsTrustedIntegrationTool(ITool tool) =>
        tool is WorktreeIntegrationApplyTool
        && tool.Descriptor.Id.ToString() == "worktree.integrate"
        && tool.Descriptor.Source.Kind == SourceKind.BuiltIn
        && tool.Descriptor.Source.Trust == TrustLevel.Core
        && tool.Descriptor.Protection == ToolProtection.Protected;

    private static bool IsTrustedBatchPublicationTool(ITool tool) =>
        tool is ITrustedBatchPublicationTool
        && tool.Descriptor.Id.ToString() == "extension.batch.apply"
        && tool.Descriptor.Source.Kind == SourceKind.BuiltIn
        && tool.Descriptor.Source.Trust == TrustLevel.Core
        && tool.Descriptor.Protection == ToolProtection.Protected;

    private static bool IsTrustedWorktreeDispositionTool(ITool tool) =>
        tool is WorktreeDispositionTool && tool is ITrustedWorktreeDispositionTool
        && tool.Descriptor.Id.ToString() is "worktree.cleanup" or "worktree.discard"
        && tool.Descriptor.Source.Kind == SourceKind.BuiltIn
        && tool.Descriptor.Source.Trust == TrustLevel.Core
        && tool.Descriptor.Protection == ToolProtection.Protected;

    private static bool ValidateIntegrationEnvelope(ToolIntent intent, ToolExecutionContext context,
        ReconciliationSpec reconciliation)
    {
        try
        {
            if (reconciliation.BeforeStateRef is not { } artifact || context.Artifacts is null
                || !context.Artifacts.Verify(artifact.Hash, artifact.Size)
                || reconciliation.StartedEntityEvent is not WorktreeIntegrationStateRecorded state
                || state.ToolCallId != intent.ToolCallId || state.State != WorktreeIntegrationState.Started
                || state.Files is null || string.IsNullOrWhiteSpace(reconciliation.DurableMetadataJson)) return false;
            using var document = System.Text.Json.JsonDocument.Parse(reconciliation.DurableMetadataJson);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || root.GetProperty("kind").GetString() != "worktree.integration.batch"
                || !root.TryGetProperty("version", out var versionElement)
                || versionElement.ValueKind != System.Text.Json.JsonValueKind.Number
                || !versionElement.TryGetInt32(out var version) || version is not (1 or 2)
                || root.EnumerateObject().Count() != (version == 1 ? 12 : 14)
                || root.GetProperty("toolCallId").GetString() != intent.ToolCallId.ToString()
                || root.GetProperty("ownershipId").GetString() != state.OwnershipId
                || root.GetProperty("proposalId").GetString() != state.ProposalId
                || !Guid.TryParseExact(state.OwnershipId, "N", out _)
                || state.ProposalId.Length != 64 || state.ProposalId.Any(ch => !Uri.IsHexDigit(ch))) return false;
            var expectedMetadataFields = version == 1
                ? new[] { "kind", "version", "toolCallId", "ownershipId", "proposalId", "workspaceRoot",
                    "gitCommonDirectory", "workspaceHead", "workspaceBranch", "laneHead", "laneBranch", "files" }
                : new[] { "kind", "version", "toolCallId", "ownershipId", "proposalId", "workspaceRoot",
                    "gitCommonDirectory", "workspaceHead", "workspaceBranch", "laneHead", "laneBranch", "files",
                    "laneTreeAtCapture", "completeLaneResult" };
            if (!root.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
                .SetEquals(expectedMetadataFields)) return false;
            if (version == 2
                && (!root.TryGetProperty("laneTreeAtCapture", out var laneTree)
                    || laneTree.ValueKind != System.Text.Json.JsonValueKind.String
                    || !IsGitObjectId(laneTree.GetString())
                    || !root.TryGetProperty("completeLaneResult", out var completeLaneResult)
                    || completeLaneResult.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                    || state.LaneTreeAtCapture != laneTree.GetString()
                    || state.CompleteLaneResult != completeLaneResult.GetBoolean())) return false;
            var claims = new HashSet<string>(PathComparer);
            var rootPath = Path.GetFullPath(context.WorkspaceRoot);
            foreach (var target in intent.Claims.Writes)
            {
                var full = Path.GetFullPath(target, rootPath);
                var relative = Path.GetRelativePath(rootPath, full).Replace(Path.DirectorySeparatorChar, '/');
                if (Path.IsPathRooted(relative) || relative == ".."
                    || relative.StartsWith("../", StringComparison.Ordinal) || !claims.Add(relative)) return false;
            }
            if (!root.TryGetProperty("files", out var files) || files.ValueKind != System.Text.Json.JsonValueKind.Array
                || files.GetArrayLength() != claims.Count || claims.Count is < 1 or > 64) return false;
            var durablePaths = new HashSet<string>(PathComparer);
            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind != System.Text.Json.JsonValueKind.Object || file.EnumerateObject().Count() != 5
                    || !file.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
                        .SetEquals(["relativePath", "expectedPreSha256", "expectedPostSha256", "expectedPreMode", "expectedPostMode"])
                    || !file.TryGetProperty("relativePath", out var path) || path.ValueKind != System.Text.Json.JsonValueKind.String
                    || !durablePaths.Add(path.GetString()!)) return false;
            }
            if (!claims.SetEquals(durablePaths) || !state.Files.Select(file => file.RelativePath)
                .ToHashSet(PathComparer).SetEquals(claims)
                || state.Files.Any(file => file.Outcome != ReconciliationOutcome.NotApplied)) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool IsGitObjectId(string? value) => value is { Length: 40 or 64 }
        && value.All(Uri.IsHexDigit);

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
    private static string SubjectJson(ToolIntent intent, ToolIntent? effectiveIntent = null,
        ProtectedToolSubstitution? substitution = null, IReadOnlyList<ToolIntent>? additionalEffectiveIntents = null)
    {
        var reason = substitution?.SourceToolId == "mcp.first-effect"
            ? "mcp.first_effect" : "permission.required";
        var details = new List<string>();
        foreach (var network in intent.Claims.Network)
            details.Add("network: " + network.Host + (network.Port is { } port ? ":" + port : ""));
        if (intent.Claims.Process is { } process)
            details.Add("process: " + process.Executable);
        foreach (var read in intent.Claims.Reads) details.Add("read: " + read);
        foreach (var write in intent.Claims.Writes) details.Add("write: " + write);
        foreach (var effective in additionalEffectiveIntents ?? [])
        {
            foreach (var read in effective.Claims.Reads) details.Add("read: " + read);
            foreach (var write in effective.Claims.Writes) details.Add("write: " + write);
        }
        using var output = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", intent.ToolId.ToString());
            writer.WriteString("risk", intent.Risk.ToString().ToLowerInvariant());
            writer.WriteString("reason", reason);
            writer.WriteString("intentFingerprint", IntentFingerprint(intent, effectiveIntent, additionalEffectiveIntents));
            if (effectiveIntent is not null)
                writer.WriteString("effectiveOperation", effectiveIntent.ToolId.ToString());
            if (additionalEffectiveIntents is { Count: > 0 })
            {
                writer.WriteStartArray("effectiveOperations");
                foreach (var effective in additionalEffectiveIntents) writer.WriteStringValue(effective.ToolId.ToString());
                writer.WriteEndArray();
            }
            if (substitution is not null && substitution.SourceToolId != "mcp.first-effect")
            {
                writer.WriteStartObject("toolPreferenceSubstitution");
                writer.WriteString("sourceToolId", substitution.SourceToolId);
                writer.WriteString("targetToolId", substitution.TargetToolId);
                writer.WriteEndObject();
            }
            writer.WriteStartArray("details");
            foreach (var detail in details)
            {
                writer.WriteStartObject();
                writer.WriteString("value", SecretRedactorRegistry.Current?.Redact(detail) ?? detail);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    internal static string IntentFingerprint(ToolIntent intent) => IntentFingerprint(intent, null);

    internal static string IntentFingerprint(ToolIntent intent, ToolIntent? effectiveIntent,
        IReadOnlyList<ToolIntent>? additionalEffectiveIntents = null)
    {
        using var output = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("toolId", intent.ToolId.ToString());
            writer.WriteString("arguments", intent.NormalizedArgumentsJson);
            writer.WriteString("effect", intent.Effect.ToString());
            writer.WriteString("risk", intent.Risk.ToString());
            if (intent.EffectiveMutationOperation is { } operation)
                writer.WriteString("effectiveMutationOperation", operation.ToString());
            if (intent.RequiresPhysicalCreateCapability)
                writer.WriteBoolean("requiresPhysicalCreateCapability", true);
            if (intent.EffectiveMutationHistoryRevision is { } historyRevision)
                writer.WriteNumber("effectiveMutationHistoryRevision", historyRevision);
            if (intent.EffectiveMutationProposal is { } proposal)
            {
                writer.WriteStartObject("effectiveMutationProposal");
                writer.WriteString("targetPath", proposal.Path);
                writer.WriteString("operation", proposal.Operation.ToString());
                writer.WriteBoolean("existedBefore", proposal.TargetExists);
                writer.WriteNumber("deletedLines", proposal.DeletedLines);
                writer.WriteNumber("insertedLines", proposal.InsertedLines);
                writer.WriteNumber("priorLineCount", proposal.OriginalLines);
                writer.WriteEndObject();
            }
            WriteStrings(writer, "reads", intent.Claims.Reads);
            WriteStrings(writer, "writes", intent.Claims.Writes);
            writer.WriteStartArray("network");
            foreach (var item in intent.Claims.Network)
            {
                writer.WriteStartObject();
                writer.WriteString("host", item.Host);
                if (item.Port is { } port) writer.WriteNumber("port", port);
                else writer.WriteNull("port");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            WriteStrings(writer, "secrets", intent.Claims.Secrets);
            if (intent.Claims.Process is { } process)
            {
                writer.WriteStartObject("process");
                writer.WriteString("executable", process.Executable);
                WriteStrings(writer, "args", process.Args);
                writer.WriteString("effectClass", process.EffectClass);
                writer.WriteBoolean("networkRequired", process.NetworkRequired);
                if (process.WorkingDirectory is { } cwd) writer.WriteString("workingDirectory", cwd);
                else writer.WriteNull("workingDirectory");
                if (process.Isolation != ProcessIsolation.None)
                    writer.WriteString("isolation", process.Isolation.ToString());
                writer.WriteEndObject();
            }
            else writer.WriteNull("process");
            if (intent.Claims.WorktreeDisposition is { } worktree)
            {
                writer.WriteStartObject("worktreeDisposition");
                writer.WriteString("workspaceId", worktree.WorkspaceId);
                writer.WriteString("workspaceRoot", worktree.WorkspaceRoot);
                writer.WriteString("laneId", worktree.LaneId);
                writer.WriteString("ownershipId", worktree.OwnershipId);
                writer.WriteString("operation", worktree.Operation.ToString());
                writer.WriteString("laneHead", worktree.LaneHead);
                writer.WriteString("laneTree", worktree.LaneTree);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        var primary = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(output.ToArray()));
        var fingerprint = effectiveIntent is null ? primary : Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(
                primary + ":" + IntentFingerprint(effectiveIntent))));
        if (additionalEffectiveIntents is null || additionalEffectiveIntents.Count == 0) return fingerprint;
        foreach (var effective in additionalEffectiveIntents)
            fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.ASCII.GetBytes(fingerprint + ":" + IntentFingerprint(effective))));
        return fingerprint;
    }

    private static PermissionDecisionRecord CombinePermissionDecisions(PermissionDecisionRecord primary,
        PermissionDecisionRecord effective, string label)
    {
        static PermissionDecision Min(PermissionDecision left, PermissionDecision right) =>
            left == PermissionDecision.Deny || right == PermissionDecision.Deny ? PermissionDecision.Deny
            : left == PermissionDecision.Ask || right == PermissionDecision.Ask ? PermissionDecision.Ask
            : PermissionDecision.Allow;
        var layers = primary.Layers.Concat(effective.Layers.Select(layer =>
            new LayerDecision(label + ":" + layer.Layer, layer.Decision, layer.Rule))).ToArray();
        return new PermissionDecisionRecord(Min(primary.Final, effective.Final), layers,
            primary.AppliedGrant ?? effective.AppliedGrant);
    }

    private AuthorizedToolIntent AuthorizeIntents(ToolIntent primary, ToolIntent? effective,
        GrantId? approvedGrant, bool approved, IReadOnlyList<ToolIntent>? additionalEffectiveIntents = null)
    {
        var authorized = approved ? _policy.AuthorizeApproved(primary, approvedGrant) : _policy.Authorize(primary);
        if (effective is not null)
        {
            if (approved) _ = _policy.AuthorizeApproved(effective, approvedGrant);
            else _ = _policy.Authorize(effective);
        }
        foreach (var batchIntent in additionalEffectiveIntents ?? [])
        {
            if (approved) _ = _policy.AuthorizeApproved(batchIntent, approvedGrant);
            else _ = _policy.Authorize(batchIntent);
        }
        return authorized;
    }

    private static void WriteStrings(System.Text.Json.Utf8JsonWriter writer, string name,
        IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
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
