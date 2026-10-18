namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

/// <summary>
/// Implementa IToolExecutor con el pipeline real de M1 (ADR-0014): FakeTools + Scripted
/// Permission Policy + Tool Runtime. Acumula los eventos emitidos por el runtime y los
/// devuelve al Engine para que los persista (ADR-0036 §5). Composición del Host.
/// </summary>
public sealed class ScriptedToolExecutor : IToolExecutor
{
    private readonly FakeCatalog _catalog;

    private readonly IPermissionPolicy _policy;

    /// <summary>Actual applied ceiling, not a requested label or the identity of an execution.</summary>
    internal AgentProfile? AgentProfile => (_policy as AgentProfilePermissionPolicy)?.Profile;

    private readonly string _workspaceRoot;

    /// <summary>
    /// Frontera de capacidad del modelo activa (ADR-0044 §5). Cuando no es null, su
    /// <c>ReadRegistry</c> por-Run viaja al ToolExecutionContext: filesystem.read registra y
    /// filesystem.patch exige lectura previa. null = semántica M2 (sin política de modelo).
    /// </summary>
    private readonly ModelCapabilityBoundary? _boundary;
    private readonly IReadOnlyList<IToolIntentBoundary> _additionalBoundaries;

    private readonly IExecutableResolver _executableResolver;

    private readonly WeakSandboxConsentState _weakSandboxConsent;

    private readonly IAuditSink? _audit;

    private readonly Func<InteractionRequested, string?>? _interactionResponder;

    private readonly bool _isInteractive;

    private readonly IArtifactStore? _artifacts;
    private readonly Func<ToolCallId, CancellationToken, Task<string?>>? _receiveMailbox;
    private readonly IWorkspaceWriteLeaseProvider? _workspaceWriteLeases;
    private readonly Func<IDisposable>? _acquireSessionSecurityAdmission;
    private readonly bool _allowDeferredInteraction;
    private readonly bool _allowDeferredWeakSandboxConsent;

    public ScriptedToolExecutor(FakeCatalog catalog, IPermissionPolicy policy)
    {
        _catalog = catalog;
        _policy = policy;
        _workspaceRoot = "sim";
        _boundary = null;
        _additionalBoundaries = [];
        _executableResolver = new SystemExecutableResolver();
        _weakSandboxConsent = new WeakSandboxConsentState();
    }

    public ScriptedToolExecutor(FakeCatalog catalog, IPermissionPolicy policy, string workspaceRoot)
    {
        _catalog = catalog;
        _policy = policy;
        _workspaceRoot = workspaceRoot;
        _boundary = null;
        _additionalBoundaries = [];
        _executableResolver = new SystemExecutableResolver();
        _weakSandboxConsent = new WeakSandboxConsentState();
    }

    /// <summary>
    /// Con frontera de capacidad del modelo (ADR-0044 §5): el ToolRuntime evalúa la frontera
    /// tras Prepare (antes de permisos) y de nuevo antes de ejecutar. La frontera restringe;
    /// jamás autoriza. null conserva la semántica de M2 (sin frontera).
    /// </summary>
    public ScriptedToolExecutor(FakeCatalog catalog, IPermissionPolicy policy, string workspaceRoot,
        ModelCapabilityBoundary? boundary)
        : this(catalog, policy, workspaceRoot, boundary, null, null, false)
    {
    }

    public ScriptedToolExecutor(FakeCatalog catalog, IPermissionPolicy policy, string workspaceRoot,
        ModelCapabilityBoundary? boundary, IAuditSink? audit,
        Func<InteractionRequested, string?>? interactionResponder, bool isInteractive,
        WeakSandboxConsentState? weakSandboxConsent = null, IArtifactStore? artifacts = null,
        Func<ToolCallId, CancellationToken, Task<string?>>? receiveMailbox = null,
        IWorkspaceWriteLeaseProvider? workspaceWriteLeases = null,
        IReadOnlyList<IToolIntentBoundary>? additionalBoundaries = null,
        Func<IDisposable>? acquireSessionSecurityAdmission = null,
        bool allowDeferredInteraction = false,
        bool allowDeferredWeakSandboxConsent = false)
    {
        _catalog = catalog;
        _policy = policy;
        _workspaceRoot = workspaceRoot;
        _boundary = boundary;
        _additionalBoundaries = additionalBoundaries ?? [];
        _executableResolver = new SystemExecutableResolver();
        _weakSandboxConsent = weakSandboxConsent ?? new WeakSandboxConsentState();
        _audit = audit;
        _interactionResponder = interactionResponder;
        _isInteractive = isInteractive;
        _artifacts = artifacts;
        _receiveMailbox = receiveMailbox;
        _workspaceWriteLeases = workspaceWriteLeases;
        _acquireSessionSecurityAdmission = acquireSessionSecurityAdmission;
        _allowDeferredInteraction = allowDeferredInteraction;
        _allowDeferredWeakSandboxConsent = allowDeferredWeakSandboxConsent;
    }

    public static ScriptedToolExecutor Default() =>
        new ScriptedToolExecutor(HostTools.Default().Catalog(),
            ScriptedPermissionPolicy.WithTool("fake.write", PermissionDecision.Allow));

    /// <summary>Executor con el catálogo completo (fake + tools Core) y política por modo.</summary>
    public static ScriptedToolExecutor WithCoreTools(FakeCatalog catalog, IPermissionPolicy policy) =>
        new ScriptedToolExecutor(catalog, policy);

    /// <summary>Executor con herramientas Core y la raíz real del workspace (para el Turn).</summary>
    public static ScriptedToolExecutor WithWorkspace(FakeCatalog catalog, IPermissionPolicy policy,
        string workspaceRoot) => new ScriptedToolExecutor(catalog, policy, workspaceRoot);

    /// <summary>
    /// Executor con herramientas Core, raíz real del workspace y la frontera de capacidad del
    /// modelo (ADR-0044 §5). null = sin frontera (semántica M2).
    /// </summary>
    public static ScriptedToolExecutor WithWorkspace(FakeCatalog catalog, IPermissionPolicy policy,
        string workspaceRoot, ModelCapabilityBoundary? boundary,
        Func<ToolCallId, CancellationToken, Task<string?>>? receiveMailbox = null,
        IReadOnlyList<IToolIntentBoundary>? additionalBoundaries = null) =>
        new ScriptedToolExecutor(catalog, policy, workspaceRoot, boundary, null, null, false,
            artifacts: null, receiveMailbox: receiveMailbox, additionalBoundaries: additionalBoundaries);

    /// <summary>
    /// Pipeline sin journal, solo para tests del pipeline: nada se persiste y todos los eventos
    /// vuelven en <c>ToolOutcome.Events</c>. No forma parte del puerto del Engine
    /// (<see cref="IToolExecutor"/>), que exige stream para no perder el Barrier (INV-014).
    /// </summary>
    public ToolOutcome ExecuteToolWithoutJournal(ValidatedToolCall validated, bool userApprovesAsk,
        CancellationToken cancellationToken) =>
        Run(validated, userApprovesAsk, cancellationToken, null);

    /// <summary>Runs a bounded validator read through the same ToolRuntime, path boundary,
    /// model-capability ceiling, and PermissionPolicy as model reads. These system reads never
    /// populate the per-Run model read registry, so they cannot authorize a later patch.</summary>
    internal SkillValidationReadResult? ReadForSkillValidation(string relativePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || _catalog.Find(new ToolId("filesystem.read")) is null)
            return null;
        var safePath = relativePath.Replace('\\', '/');
        var content = new System.Text.StringBuilder();
        string? expectedVersion = null;
        const int pageSize = 500;
        const int maximumPages = 4;
        for (var page = 0; page < maximumPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = page * pageSize + 1;
            var arguments = "{\"path\":\"" + System.Text.Json.JsonEncodedText.Encode(safePath).ToString() + "\""
                + ",\"offset\":" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"limit\":" + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
            var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.read"),
                "skill-validator-" + Guid.NewGuid().ToString("N"), arguments);
            var outcome = Run(call, userApprovesAsk: false, cancellationToken, stream: null,
                recordReadRegistry: false);
            if (!outcome.Succeeded || outcome.Preview is not { } preview) return new(false, null);
            if (!TryAppendSkillValidationPage(preview, content, ref expectedVersion, out var hasMore))
                return new(false, null);
            if (!hasMore)
                return new(true, content.ToString());
        }
        return new(false, null);
    }

    internal static bool TryAppendSkillValidationPage(string preview, System.Text.StringBuilder content,
        ref string? expectedVersion, out bool hasMore)
    {
        hasMore = false;
        var separator = preview.IndexOf('\n');
        var version = preview.LastIndexOf("\n\n[version:", StringComparison.Ordinal);
        if (separator < 0 || version <= separator) return false;
        var versionStart = version + "\n\n[version:".Length;
        var versionEnd = preview.IndexOf(']', versionStart);
        if (versionEnd < 0) return false;
        var pageVersion = preview[versionStart..versionEnd];
        if (pageVersion.Length != 64 || !pageVersion.All(Uri.IsHexDigit)
            || (expectedVersion is not null && !string.Equals(expectedVersion, pageVersion,
                StringComparison.OrdinalIgnoreCase))) return false;
        var marker = preview[..separator];
        if (!marker.StartsWith("[lines:", StringComparison.Ordinal)
            || marker.Contains(";contentTruncated:true]", StringComparison.Ordinal)) return false;
        expectedVersion = pageVersion;
        hasMore = marker.Contains(";hasMore:true;", StringComparison.Ordinal);
        // This is the authorized/redacted content body, not its presentation header. Preserve
        // exact line breaks so multiline file-contains rules have their original semantics.
        content.Append(preview[(separator + 1)..version]);
        return true;
    }

    /// <summary>
    /// Pipeline real con escritura en vivo del journal (ADR-0002 §2): cuando el intent declara un
    /// efecto (EffectClass ≠ None), <c>ToolCallStarted</c> se persiste con commit Barrier ANTES de
    /// que la tool ejecute, y los eventos previos del pipeline se persisten en orden (Standard)
    /// para que la secuencia del Started nunca preceda a la de sus predecesores. Los outcomes
    /// posteriores se devuelven en <c>ToolOutcome.Events</c> para que el Engine los persista
    /// (Standard) tras la ejecución. Con <c>stream == null</c> no se persiste nada aquí y la lista
    /// completa vuelve en <c>Events</c> (semántica previa). Estado siempre local a la llamada: no
    /// hay buffers ni callbacks compartidos entre Runs.
    /// </summary>
    public ToolOutcome ExecuteTool(ValidatedToolCall validated, bool userApprovesAsk,
        CancellationToken cancellationToken, EventStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Run(validated, userApprovesAsk, cancellationToken, stream, null);
    }

    internal ToolOutcome ResumeApprovedPermission(EventStream stream, IEventCodecRegistry codecs, InteractionId interaction,
        string optionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var scope = ExecutionScope.Current;
        if (scope?.RunId is not { } run || scope.TurnId is not { } turn || scope.LaneId is not { } lane)
            throw new InvalidOperationException("Permission continuation is missing its execution scope.");
        var events = stream.EventsSince(1);
        var permissionRequests = events.Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
            .Where(item => item.Event.RunId == run && item.Event.TurnId == turn && item.Event.LaneId == lane
                && item.Payload is PermissionRequested permission && permission.InteractionId == interaction)
            .ToArray();
        if (permissionRequests.Length != 1 || permissionRequests[0].Payload is not PermissionRequested requested)
            throw new InvalidOperationException("The approved Permission request is not uniquely bound to this Turn.");
        var resolved = events.Where(evt => evt.RunId == run)
            .Select(evt => codecs.Decode(evt)).OfType<InteractionResolved>()
            .SingleOrDefault(item => item.InteractionId == interaction);
        if (resolved is null || resolved.IsQuestionnaire || resolved.OptionId != optionId)
            throw new InvalidOperationException("The durable Permission choice does not match this continuation.");
        var requestEvents = events.Where(evt => evt.RunId == run && evt.TurnId == turn && evt.LaneId == lane)
            .Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
            .Where(item => item.Payload is ToolCallRequested call && call.ToolCallId == requested.ToolCallId).ToArray();
        var prepared = events.Where(evt => evt.RunId == run && evt.TurnId == turn && evt.LaneId == lane)
            .Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
            .Where(item => item.Payload is ToolCallPrepared call && call.ToolCallId == requested.ToolCallId).ToArray();
        if (requestEvents.Length != 1 || prepared.Length != 1
            || requestEvents[0].Event.Sequence >= permissionRequests[0].Event.Sequence
            || prepared[0].Event.Sequence >= permissionRequests[0].Event.Sequence
            || events.Any(evt => evt.RunId == run && evt.ToolCallId == requested.ToolCallId
                && evt.Sequence > permissionRequests[0].Event.Sequence
                && codecs.Decode(evt) is ToolCallStarted or ToolCallSucceeded or ToolCallFailed
                    or ToolCallEffectUnknown or ToolCallReconciled))
            throw new InvalidOperationException("The original Permission call is not safe to resume.");
        string? intentFingerprint;
        try
        {
            var interactionEvents = events.Where(evt => evt.RunId == run
                && codecs.Decode(evt) is InteractionRequested item && item.InteractionId == interaction).ToArray();
            if (interactionEvents.Length != 1 || codecs.Decode(interactionEvents[0]) is not InteractionRequested request)
                throw new InvalidOperationException("The approved Permission request has no unique interaction envelope.");
            using var subject = System.Text.Json.JsonDocument.Parse(request.SubjectJson);
            intentFingerprint = subject.RootElement.TryGetProperty("intentFingerprint", out var fingerprint)
                && fingerprint.ValueKind == System.Text.Json.JsonValueKind.String
                ? fingerprint.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { intentFingerprint = null; }
        if (intentFingerprint is null)
            throw new InvalidOperationException("The approved Permission has no effective-intent fingerprint.");
        var call = (ToolCallRequested)requestEvents[0].Payload;
        var preparedCall = (ToolCallPrepared)prepared[0].Payload;
        var providerCallId = call.ProviderCallId;
        var validated = new ValidatedToolCall(requested.ToolCallId, new ToolId(call.ToolName),
            providerCallId, call.ArgumentsJson);
        var outcome = Run(validated, false, cancellationToken, stream, approvedPermission: interaction,
            approvedOption: optionId, expectedPreparedArguments: preparedCall.ToolIntentJson,
            expectedIntentFingerprint: intentFingerprint);
        if (outcome.Events.Count > 0) stream.AppendBatch(outcome.Events, DurabilityClass.Standard);
        return outcome;
    }

    internal ToolOutcome ResumeWeakSandboxConsent(EventStream stream, IEventCodecRegistry codecs,
        InteractionId interaction, string optionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var scope = ExecutionScope.Current;
        if (scope?.RunId is not { } run || scope.TurnId is not { } turn || scope.LaneId is not { } lane)
            throw new InvalidOperationException("Weak sandbox continuation is missing its execution scope.");
        if (optionId is not ("consent_once" or "deny"))
            throw new InvalidOperationException("The Weak sandbox option is not server-defined.");
        var events = stream.EventsSince(1);
        var requestEvents = events.Where(evt => evt.RunId == run && evt.TurnId == turn && evt.LaneId == lane)
            .Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
            .Where(item => item.Payload is ToolCallRequested).ToArray();
        var candidates = new List<(ToolCallRequested Call, DomainEvent Request, string Prepared, string Fingerprint)>();
        foreach (var request in events.Where(evt => evt.RunId == run && evt.TurnId == turn && evt.LaneId == lane
                     && codecs.Decode(evt) is InteractionRequested interactionRequest
                     && interactionRequest.InteractionId == interaction
                     && interactionRequest.Kind == InteractionKind.WeakSandboxConsent))
        {
            if (request.ToolCallId is not { } toolCallId) continue;
            var callEvents = events.Where(evt => evt.RunId == run && evt.TurnId == turn && evt.LaneId == lane
                    && evt.ToolCallId == toolCallId).OrderBy(evt => evt.Sequence)
                .Select(evt => (Event: evt, Payload: codecs.Decode(evt))).ToArray();
            var callRequest = callEvents.SingleOrDefault(item => item.Payload is ToolCallRequested);
            var prepared = callEvents.SingleOrDefault(item => item.Payload is ToolCallPrepared);
            var authorized = callEvents.LastOrDefault(item => item.Payload is ToolCallAuthorized);
            if (callRequest.Event is null || callRequest.Payload is not ToolCallRequested call
                || prepared.Event is null || prepared.Payload is not ToolCallPrepared preparedCall
                || authorized.Event is null || authorized.Event.Sequence >= request.Sequence
                || callRequest.Event.Sequence >= prepared.Event.Sequence || prepared.Event.Sequence >= request.Sequence
                || callEvents.Any(item => item.Event.Sequence > request.Sequence
                    && item.Payload is ToolCallStarted or ToolCallSucceeded or ToolCallFailed or ToolCallEffectUnknown
                        or ToolCallReconciled)) continue;
            string fingerprint;
            try
            {
                using var subject = System.Text.Json.JsonDocument.Parse(
                    ((InteractionRequested)codecs.Decode(request)).SubjectJson);
                fingerprint = subject.RootElement.TryGetProperty("intentFingerprint", out var value)
                    && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() ?? "" : "";
            }
            catch (System.Text.Json.JsonException) { continue; }
            if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)) continue;
            var response = events.SingleOrDefault(evt => evt.RunId == run
                && codecs.Decode(evt) is InteractionResolved resolved && resolved.InteractionId == interaction);
            if (response is null || codecs.Decode(response) is not InteractionResolved choice
                || choice.IsQuestionnaire || choice.Cause != InteractionCause.User || choice.OptionId != optionId)
                continue;
            candidates.Add((call, request, preparedCall.ToolIntentJson, fingerprint));
        }
        if (candidates.Count != 1)
            throw new InvalidOperationException("The Weak sandbox choice is not uniquely bound to this child ToolCall.");
        var candidate = candidates[0];
        var validated = new ValidatedToolCall(candidate.Call.ToolCallId, new ToolId(candidate.Call.ToolName),
            candidate.Call.ProviderCallId, candidate.Call.ArgumentsJson);
        var outcome = Run(validated, userApprovesAsk: false, cancellationToken, stream,
            approvedWeakSandboxInteraction: interaction, approvedWeakSandboxOption: optionId,
            expectedPreparedArguments: candidate.Prepared, expectedIntentFingerprint: candidate.Fingerprint);
        if (outcome.Events.Count > 0) stream.AppendBatch(outcome.Events, DurabilityClass.Standard);
        return outcome;
    }

    internal ToolOutcome ExecuteTool(ValidatedToolCall validated, bool userApprovesAsk,
        CancellationToken cancellationToken, EventStream stream, bool requiresSubstitutionAsk)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Run(validated, userApprovesAsk, cancellationToken, stream, null, requiresSubstitutionAsk);
    }

    internal ToolOutcome ExecuteTool(ValidatedToolCall validated, bool userApprovesAsk,
        CancellationToken cancellationToken, EventStream stream, ProtectedToolSubstitution? protectedSubstitution)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Run(validated, userApprovesAsk, cancellationToken, stream,
            protectedSubstitution: protectedSubstitution);
    }

    internal ToolOutcome ExecuteTool(ValidatedToolCall validated, bool userApprovesAsk,
        CancellationToken cancellationToken, EventStream stream, ToolCallRequestReceipt requestReceipt)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(requestReceipt);
        var scope = ExecutionScope.Current;
        if (!requestReceipt.MatchesCall(validated)
            || !requestReceipt.MatchesScope(scope?.RunId, scope?.TaskId, scope?.LaneId,
                scope?.TurnId, scope?.ExecutionId)
            || !stream.IsCurrentToolCallRequestReceipt(requestReceipt, validated))
            throw new InvalidOperationException("Tool request receipt is not present in this session journal.");
        return Run(validated, userApprovesAsk, cancellationToken, stream, requestReceipt);
    }

    internal ToolOutcome ExecuteTool(ValidatedToolCall validated, bool userApprovesAsk,
        CancellationToken cancellationToken, EventStream stream, ToolCallRequestReceipt requestReceipt,
        bool requiresSubstitutionAsk)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(requestReceipt);
        var scope = ExecutionScope.Current;
        if (!requestReceipt.MatchesCall(validated)
            || !requestReceipt.MatchesScope(scope?.RunId, scope?.TaskId, scope?.LaneId,
                scope?.TurnId, scope?.ExecutionId)
            || !stream.IsCurrentToolCallRequestReceipt(requestReceipt, validated))
            throw new InvalidOperationException("Tool request receipt is not present in this session journal.");
        return Run(validated, userApprovesAsk, cancellationToken, stream, requestReceipt, requiresSubstitutionAsk);
    }

    internal ToolOutcome ExecuteTool(ValidatedToolCall validated, bool userApprovesAsk,
        CancellationToken cancellationToken, EventStream stream, ToolCallRequestReceipt requestReceipt,
        ProtectedToolSubstitution? protectedSubstitution)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(requestReceipt);
        var scope = ExecutionScope.Current;
        if (!requestReceipt.MatchesCall(validated)
            || !requestReceipt.MatchesScope(scope?.RunId, scope?.TaskId, scope?.LaneId,
                scope?.TurnId, scope?.ExecutionId)
            || !stream.IsCurrentToolCallRequestReceipt(requestReceipt, validated))
            throw new InvalidOperationException("Tool request receipt is not present in this session journal.");
        return Run(validated, userApprovesAsk, cancellationToken, stream, requestReceipt,
            protectedSubstitution: protectedSubstitution);
    }

    private ToolOutcome Run(ValidatedToolCall validated, bool userApprovesAsk,
        CancellationToken cancellationToken, EventStream? stream, ToolCallRequestReceipt? requestReceipt = null,
        bool requiresSubstitutionAsk = false, ProtectedToolSubstitution? protectedSubstitution = null,
        bool recordReadRegistry = true, InteractionId? approvedPermission = null,
        string? approvedOption = null, string? expectedPreparedArguments = null,
        string? expectedIntentFingerprint = null, InteractionId? approvedWeakSandboxInteraction = null,
        string? approvedWeakSandboxOption = null)
    {
        // Buffer local a la llamada (evita estado compartido entre Runs). Sin flush, contiene
        // todos los eventos emitidos; tras un flush contiene solo los outcomes.
        var buffered = new List<DomainEventPayload>();

        Func<DomainEventPayload, VoidBox> emit = payload =>
        {
            if (stream is not null && payload is InteractionRequested or InteractionResolved)
            {
                foreach (var evt in buffered) stream.Append(evt);
                buffered.Clear();
                stream.Append(payload);
                return VoidBox.Instance;
            }

            if (stream is not null && payload is ToolCallSucceeded or ToolCallFailed or ToolCallEffectUnknown)
            {
                var callId = payload switch
                {
                    ToolCallSucceeded value => value.ToolCallId,
                    ToolCallFailed value => value.ToolCallId,
                    ToolCallEffectUnknown value => value.ToolCallId,
                    _ => default,
                };
                var integrationState = buffered.OfType<WorktreeIntegrationStateRecorded>()
                    .LastOrDefault(value => value.ToolCallId == callId
                        && value.State != WorktreeIntegrationState.Started);
                if (integrationState is not null)
                {
                    foreach (var evt in buffered.Where(evt => !ReferenceEquals(evt, integrationState)))
                        stream.Append(evt);
                    stream.AppendBatch([integrationState, payload], DurabilityClass.Standard);
                    buffered.Clear();
                    return VoidBox.Instance;
                }
            }

            if (stream is not null && payload is ToolCallStarted started
                && (started.EffectClass != EffectClass.None
                    || validated.ToolId.ToString() == "core.agents.mailbox.receive"))
            {
                // ADR-0004 §2: el Started de un intent con efecto se confirma con commit Barrier
                // ANTES de que la tool ejecute (aquí el ToolRuntime aún no llamó ExecuteAsync).
                // The mailbox receive also needs its exact Started envelope durable before it
                // waits: the Host may accept a scoped wake only while this ToolCall is live.
                // Los eventos previos se escriben antes (Standard, en orden) para no romper la
                // secuencia, y luego este Started como Barrier.
                var integrationStart = buffered.OfType<WorktreeIntegrationStateRecorded>()
                    .LastOrDefault(value => value.ToolCallId == started.ToolCallId
                        && value.State == WorktreeIntegrationState.Started);
                foreach (var evt in buffered.Where(evt => evt is not PostEditValidationPending
                    && !ReferenceEquals(evt, integrationStart)))
                {
                    stream.Append(evt);
                }

                var debtAndStart = buffered.Where(evt => evt is PostEditValidationPending
                    || ReferenceEquals(evt, integrationStart)).ToList();
                if (debtAndStart.Count == 0) stream.Append(started, DurabilityClass.Barrier);
                else
                {
                    // No orphan debt if a crash happens before Started: both commit atomically.
                    debtAndStart.Add(started);
                    stream.AppendBatch(debtAndStart, DurabilityClass.Barrier);
                }
                buffered.Clear();
                return VoidBox.Instance;
            }

            buffered.Add(payload);
            return VoidBox.Instance;
        };

        var runtime = new ToolRuntime(_catalog, _policy, emit, _boundary, _executableResolver, _additionalBoundaries);
        var prepContext = new ToolPreparationContext(_workspaceRoot, DateTimeOffset.Now);
        // ADR-0044 §5: cuando hay frontera de capacidad (política del modelo), el registro de
        // lecturas efectivas por-Run viaja en el contexto para que las tools exijan lectura previa.
        var execContext = new ToolExecutionContext(_workspaceRoot,
            recordReadRegistry ? _boundary?.ReadRegistry() : null,
            payload => emit(payload), _interactionResponder, _audit, _isInteractive, _weakSandboxConsent,
            beforeEffect: intent =>
            {
                // Filesystem patch/write and the host-owned integration tool publish files
                // through the mutation ledger; process write claims are not file-edit debt.
                if (stream is null || intent.ToolId.ToString() is not ("filesystem.patch" or "filesystem.write" or "worktree.integrate")
                    || intent.Claims.Writes.Count == 0 || intent.Effect == EffectClass.None
                    || _boundary?.ReadRegistry().Ledger.MutationPolicy?.RequirePostEditValidation != true)
                    return;
                var runId = stream.EventsSince(1).LastOrDefault()?.RunId;
                // Primitive pipeline tests may have no Run; there is no Run completion to guard.
                if (runId is null) return;
                // Buffered before Started: its Barrier confirms this debt before ExecuteAsync.
                emit(new PostEditValidationPending(runId, intent.ToolCallId, intent.Claims.Writes.ToArray()));
            }, artifacts: _artifacts, receiveMailbox: _receiveMailbox,
            workspaceWriteLeases: _workspaceWriteLeases,
            isModelToolVisible: _boundary is null ? null : _boundary.IsToolVisible,
            requiresSubstitutionAsk: requiresSubstitutionAsk,
            protectedSubstitution: protectedSubstitution,
            acquireSessionSecurityAdmission: _acquireSessionSecurityAdmission,
            allowDeferredInteraction: _allowDeferredInteraction,
            allowDeferredWeakSandboxConsent: _allowDeferredWeakSandboxConsent,
            weakSandboxConsentForThisCall: approvedWeakSandboxInteraction is not null
                && approvedWeakSandboxOption == "consent_once");
        var outcome = approvedWeakSandboxInteraction is { } weakInteraction
            ? runtime.ResumeWeakSandboxConsent(validated, prepContext, execContext, cancellationToken,
                weakInteraction, approvedWeakSandboxOption!, expectedPreparedArguments!, expectedIntentFingerprint!)
            : approvedPermission is { } interaction
            ? runtime.ResumeApprovedPermission(validated, prepContext, execContext, cancellationToken,
                interaction, approvedOption!, expectedPreparedArguments!, expectedIntentFingerprint!)
            : requestReceipt is null
            ? runtime.Run(validated, prepContext, execContext, userApprovesAsk, cancellationToken)
            : runtime.Run(validated, prepContext, execContext, userApprovesAsk, cancellationToken, requestReceipt);
        var events = buffered.ToArray();

        return outcome.Succeeded
            ? new ToolOutcome(true, outcome.Summary ?? "ok", outcome.Preview, outcome.Effect, outcome.FinalState,
                events, artifact: outcome.Artifact)
            : new ToolOutcome(false, outcome.Summary ?? "fallo", outcome.Preview, null, outcome.FinalState,
                events, outcome.PendingInteractionId);
    }
}
