namespace OmniCore.Host;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Tools;
using PersistedSpend = OmniCore.Host.CanonicalSpendReader.Snapshot;

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
    private readonly Action<IReadOnlyList<ContextItem>, SessionId, RunId>? _externalContextAdmission;
    public Action<SessionId, RunId, LaneId, long, string>? WorkspaceCheckpointRecorder { get; init; }
    private const string MemoryProposalJournalArgumentsJson = "{\"screenedByMemoryPolicy\":true}";
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IEventStore,
        System.Collections.Concurrent.ConcurrentDictionary<(SessionId, RunId, LaneId?), SemaphoreSlim>> RunExecutionGates = new();
    public const int MaxSteps = 8;

    private readonly Func<ModelRequest, CancellationToken, ModelResponse> _complete;

    private readonly IModelProvider? _metaModelProvider;
    private readonly long? _modelContextCapacity;

    private readonly IToolExecutor _tools;

    private readonly FakeCatalog _catalog;

    private readonly ContextMaterializer _materializer;

    private readonly ExecutionFingerprint _fingerprint;
    private readonly IReadOnlyList<IPreparedArtifact> _fingerprintArtifacts;

    private readonly bool _recordEffectiveFingerprint;
    private readonly IReadOnlyList<ActiveSkillFingerprint>? _activeSkills;
    private readonly IReadOnlySet<string> _activeSkillToolIds;
    private readonly AgentProfile? _resolvedAgentProfile;
    private readonly IReadOnlyDictionary<string, string> _toolPreferences;
    private readonly IReadOnlyList<IHook> _lifecycleHooks;
    private readonly Func<IReadOnlyList<string>, Func<string, SkillValidationReadResult?>, string?,
        CancellationToken, IReadOnlyList<ExternalCompletionGateResult>>? _resolveLifecycleValidations;
    private readonly SessionMemoryPolicy? _sessionMemoryPolicy;
    private readonly LifecycleHookDispatcher _hookDispatcher = new();
    private readonly Dictionary<string, ProtectedToolSubstitution> _protectedPreferenceSubstitutions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _approvedProtectedPreferenceIdentities = new(StringComparer.Ordinal);

    private readonly ModelSelection _selection;

    private readonly IEventStore _store;

    private readonly IEventCodecRegistry _codecs;

    private readonly IArtifactStore _artifacts;
    private readonly UserWorkspaceSpendReader? _userSpendReader;
    private readonly SqliteSpendReservationStore? _spendReservations;
    private readonly long? _maximumGenerationRequestAttempts;

    private readonly IAuditSink _audit;

    private readonly RedactionPolicy _redaction;

    private readonly HarnessPolicy? _harness;

    private static ExecutionFingerprint WithHookFingerprint(ExecutionFingerprint baseline, IReadOnlyList<IHook> hooks)
    {
        if (hooks.Count == 0) return baseline;
        var descriptor = new System.Text.StringBuilder();
        static void Add(System.Text.StringBuilder target, string? value)
        {
            value ??= "";
            target.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(':').Append(value).Append(';');
        }
        foreach (var hook in hooks.OrderBy(item => item.Registration.Id, StringComparer.Ordinal))
        {
            var registration = hook.Registration;
            Add(descriptor, registration.Id);
            Add(descriptor, registration.Source.Kind.ToString());
            Add(descriptor, registration.Source.Scope.ToString());
            Add(descriptor, registration.Source.Trust.ToString());
            Add(descriptor, registration.Source.Owner);
            Add(descriptor, registration.Source.Version);
            Add(descriptor, registration.Source.ContentIdentity);
            Add(descriptor, registration.Point.ToString());
            Add(descriptor, registration.EffectiveArgumentAccess.ToString());
            Add(descriptor, registration.Timeout?.TotalMilliseconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            Add(descriptor, registration.Enabled.ToString());
            Add(descriptor, registration.WorkspaceTrusted.ToString());
            Add(descriptor, registration.FullArgumentsConsented.ToString());
            foreach (var capability in registration.RequestedCapabilities.Order()) Add(descriptor, "request:" + capability);
            foreach (var capability in registration.GrantedCapabilities.Order()) Add(descriptor, "grant:" + capability);
            foreach (var eventType in (registration.EventTypeFilters ?? new HashSet<string>()).Order(StringComparer.Ordinal))
                Add(descriptor, "event:" + eventType);
            Add(descriptor, "end");
        }
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(descriptor.ToString())));
        var components = baseline.Components.Where(component => component.Name != "lifecycle.hooks")
            .Append(new FingerprintComponent("lifecycle.hooks", "1", ContentHash.Sha256(hash))).ToArray();
        return new ExecutionFingerprint(baseline.ModelKey, baseline.HarnessPolicyHash, baseline.ToolkitHash,
            baseline.ContextPolicyHash, baseline.OverridesHash, baseline.Build, baseline.ModelPolicyHash,
            baseline.TokenizerHash, components);
    }

    private TokenUsage ReadTurnModelStepUsage(EventStream stream, TurnId turnId, out bool invalid)
    {
        invalid = false;
        var total = new TokenUsage(0, 0, 0, 0, 0);
        foreach (var evt in stream.EventsSince(1))
        {
            if (!evt.Type.ToString().Equals("model_step.completed", StringComparison.Ordinal)) continue;
            try
            {
                if (_codecs.Decode(evt) is ModelStepCompleted completed && completed.TurnId == turnId
                    && completed.Usage is not null)
                {
                    if (TokenUsageValidation.IsInvalid(completed.Usage, completed.ReportedUsageFields
                        ?? (TokenUsageFields.Input | TokenUsageFields.Output))) { invalid = true; continue; }
                    try { total = CombineUsage(total, completed.Usage); }
                    catch (OverflowException) { invalid = true; }
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
                or FormatException or ArgumentException)
            {
                // The spend reader marks malformed evidence incomplete; don't make resume fail open.
            }
        }
        return total;
    }

    private decimal? ReadTurnModelStepCost(EventStream stream, TurnId turnId)
    {
        decimal total = 0m;
        var started = new HashSet<int>();
        var completedIndexes = new HashSet<int>();
        foreach (var evt in stream.EventsSince(1))
        {
            if (evt.Type.ToString().Equals("model_step.started", StringComparison.Ordinal))
            {
                try
                {
                    if (_codecs.Decode(evt) is ModelStepStarted start && start.TurnId == turnId
                        && !started.Add(start.StepIndex)) return null;
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
                    or FormatException or ArgumentException)
                {
                    return null;
                }
                continue;
            }
            if (!evt.Type.ToString().Equals("model_step.completed", StringComparison.Ordinal)) continue;
            try
            {
                if (_codecs.Decode(evt) is not ModelStepCompleted completed || completed.TurnId != turnId) continue;
                if (!completedIndexes.Add(completed.StepIndex)) return null;
                if (completed.CostUsd is null || completed.CostUsd < 0m || completed.Usage is null
                    || TokenUsageValidation.IsInvalid(completed.Usage, completed.ReportedUsageFields
                        ?? (TokenUsageFields.Input | TokenUsageFields.Output))) return null;
                total += completed.CostUsd.Value;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
                or FormatException or ArgumentException or OverflowException)
            {
                return null;
            }
        }
        if (!started.SetEquals(completedIndexes)) return null;
        return completedIndexes.Count == 0 ? 0m : total;
    }

    private ProviderState? ReadProviderContinuation(EventStream stream, RunId runId, LaneId laneId,
        TurnId turnId)
    {
        const string failure = "Provider state checkpoint is invalid.";
        var starts = new Dictionary<int, ModelStepStarted>();
        var completions = new Dictionary<int, ModelStepCompleted>();
        foreach (var evt in stream.EventsSince(1))
        {
            if (evt.RunId != runId || evt.LaneId != laneId) continue;
            switch (_codecs.Decode(evt))
            {
                case ModelStepStarted start when start.TurnId == turnId:
                    if (!starts.TryAdd(start.StepIndex, start)) throw new InvalidDataException(failure);
                    break;
                case ModelStepCompleted completion when completion.TurnId == turnId:
                    if (!completions.TryAdd(completion.StepIndex, completion)) throw new InvalidDataException(failure);
                    break;
            }
        }
        if (completions.Count == 0) return null;
        var last = completions.MaxBy(pair => pair.Key);
        if (!starts.TryGetValue(last.Key, out var origin)) throw new InvalidDataException(failure);
        // Legacy evidence without route identity cannot authorize opaque replay.
        if (!Equals(origin.RouteId, _selection.RouteId)
            || !string.Equals(origin.ModelId, _selection.Model.ToString(), StringComparison.Ordinal)) return null;
        var response = last.Value.ResponseArtifact;
        if (response is null) return null;
        try
        {
            if (response.Kind != ArtifactKind.ModelResponse || ! _artifacts.Verify(response.Hash, response.Size))
                throw new InvalidDataException(failure);
            using var document = System.Text.Json.JsonDocument.Parse(_artifacts.GetText(response.Hash)!);
            if (!document.RootElement.TryGetProperty("providerState", out var descriptor)
                || descriptor.ValueKind == System.Text.Json.JsonValueKind.Null) return null;
            return ProviderStateCheckpoint.Restore(_artifacts, descriptor.GetRawText(),
                _selection.Model.ToString(), _selection.RouteId, turnId, last.Key, _selection.RouteIdentityHash);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
            or IOException or ArgumentException)
        {
            throw new InvalidDataException(failure);
        }
    }

    private int ReadNextModelStepIndex(EventStream stream, TurnId turnId)
    {
        var max = -1;
        foreach (var evt in stream.EventsSince(1))
        {
            if (!evt.Type.ToString().Equals("model_step.started", StringComparison.Ordinal)) continue;
            try
            {
                if (_codecs.Decode(evt) is ModelStepStarted started && started.TurnId == turnId
                    && started.StepIndex >= 0)
                    max = Math.Max(max, started.StepIndex);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
                or FormatException or ArgumentException)
            {
                // ReadJournalSpend independently marks malformed invocation evidence incomplete.
            }
        }
        return max + 1;
    }

    private int ReadTurnToolCallCount(EventStream stream, TurnId turnId)
    {
        var ids = new HashSet<ToolCallId>();
        foreach (var evt in stream.EventsSince(1))
        {
            if (evt.TurnId != turnId) continue;
            try
            {
                if (_codecs.Decode(evt) is ToolCallRequested requested)
                    ids.Add(requested.ToolCallId);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
                or FormatException or ArgumentException)
            {
                // The general journal validation reports malformed evidence; do not count it as
                // a valid admission, and keep the boundary fail-closed through that validation.
            }
        }
        return ids.Count;
    }

    /// <summary>
    /// Frontera de capacidad del modelo (ADR-0044 §5): el ToolPlanner (VisibleTools) oculta las
    /// tools fuera del techo y el ToolRuntime la re-valida antes de permisos y antes de ejecutar.
    /// La frontera restringe; jamás autoriza. null = sin frontera (semántica M2). La política
    /// efectiva ya está intersectada con el harness por el resolver.
    /// </summary>
    private readonly ModelCapabilityBoundary? _boundary;
    internal ModelCapabilityBoundary? CapabilityBoundary => _boundary;

    private readonly ModelPricing? _pricing;

    private readonly bool _enforceDefaultSpendCaps;
    private readonly bool _enforceSessionSpendCap;
    private readonly bool _enforceDailySpendCap;
    private readonly Func<SessionId, RunId, LaneId, TurnId, int, InteractionId?>? _quotaAdmission;
    private readonly Func<bool>? _quotaAllowsMeta;

    // ADR-0037 §7. Solo se aplican a providers que requieren API key; se pueden cambiar por
    // constructor/configuración de Host. Las tarifas nunca se inventan localmente.
    private readonly decimal _sessionCapUsd;

    private readonly decimal _dailyCapUsd;
    private readonly bool _monetaryBilling;

    private readonly QuestionnaireInteractionService? _questionnaires;

    private readonly Func<InteractionId, QuestionnaireSchema, QuestionnaireAskOutcome?>? _questionnaireResponder;

    private readonly Action<ToolCallId>? _mailboxDeliveryCompleted;

    // ADR-0048 §2: el Host decide si EscalateModel está disponible (null = disponible).
    private readonly Func<string?>? _stallEscalationUnavailable;

    // ADR-0044 §4: destino de las muestras de mutación observadas por el ledger (telemetría).
    private readonly Action<IReadOnlyList<FileMutationSample>>? _mutationEvidence;

    private readonly string? _forcedInitialToolName;
    private readonly bool _exposeForcedInitialToolInDiscoveredMode;
    private readonly WorkflowStageDefinition? _workflowStageDefinition;
    private readonly int _maxModelStepsPerTurn;
    private readonly int _maxToolCallsPerTurn;
    private readonly int _maxGenerationSeconds;
    private readonly Func<SessionSecurityPolicySet?>? _sessionSecurityPolicy;

    // El watchdog y la directiva se evalúan en cada Turn: cada evento se decodifica una sola vez.
    private readonly MemoizedEventDecoder _stallDecoder;

    public ExplorerTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete, IToolExecutor tools,
        FakeCatalog catalog, ContextMaterializer materializer, ExecutionFingerprint fingerprint,
        ModelSelection selection, IEventStore store, IEventCodecRegistry codecs, IArtifactStore artifacts,
        IAuditSink audit, RedactionPolicy redaction, HarnessPolicy? harness = null,
        ModelCapabilityBoundary? boundary = null, ModelPricing? pricing = null,
        bool enforceDefaultSpendCaps = false, decimal sessionCapUsd = 5m, decimal dailyCapUsd = 20m,
        QuestionnaireInteractionService? questionnaires = null,
        Func<InteractionId, QuestionnaireSchema, QuestionnaireAskOutcome?>? questionnaireResponder = null,
        IModelProvider? metaModelProvider = null, long? modelContextCapacity = null,
        bool recordEffectiveFingerprint = false, UserWorkspaceSpendReader? userSpendReader = null,
        IReadOnlyList<ActiveSkillFingerprint>? activeSkills = null,
        IReadOnlyList<string>? activeSkillToolIds = null,
        Func<SessionId, RunId, LaneId, TurnId, int, InteractionId?>? quotaAdmission = null,
        Func<bool>? quotaAllowsMeta = null,
        SqliteSpendReservationStore? spendReservations = null,
        long? maximumGenerationRequestAttempts = null,
        IReadOnlyList<IPreparedArtifact>? fingerprintArtifacts = null,
        Action<ToolCallId>? mailboxDeliveryCompleted = null,
        Func<string?>? stallEscalationUnavailable = null,
        Action<IReadOnlyList<FileMutationSample>>? mutationEvidence = null,
        string? forcedInitialToolName = null,
        int maxModelStepsPerTurn = MaxSteps, int maxToolCallsPerTurn = 32,
        int maxGenerationSeconds = 300,
        bool? enforceSessionSpendCap = null, bool? enforceDailySpendCap = null,
        IReadOnlyDictionary<string, string>? toolPreferences = null,
        IReadOnlyList<IHook>? lifecycleHooks = null,
        SessionMemoryPolicy? sessionMemoryPolicy = null,
        Func<IReadOnlyList<string>, Func<string, SkillValidationReadResult?>, string?,
            CancellationToken, IReadOnlyList<ExternalCompletionGateResult>>? resolveLifecycleValidations = null,
        BillingMode? billingMode = null,
        Func<SessionSecurityPolicySet?>? sessionSecurityPolicy = null,
        bool exposeForcedInitialToolInDiscoveredMode = false,
        WorkflowStageDefinition? workflowStageDefinition = null,
        Action<IReadOnlyList<ContextItem>, SessionId, RunId>? externalContextAdmission = null)
    {
        if (sessionCapUsd < 0m) throw new ArgumentOutOfRangeException(nameof(sessionCapUsd));
        if (dailyCapUsd < 0m) throw new ArgumentOutOfRangeException(nameof(dailyCapUsd));
        if (maxModelStepsPerTurn is < 1 or > MaxSteps) throw new ArgumentOutOfRangeException(nameof(maxModelStepsPerTurn));
        if (maxToolCallsPerTurn is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(maxToolCallsPerTurn));
        if (maxGenerationSeconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(maxGenerationSeconds));
        _complete = complete;
        _externalContextAdmission = externalContextAdmission;
        _tools = tools;
        _resolvedAgentProfile = (tools as ScriptedToolExecutor)?.AgentProfile;
        _toolPreferences = toolPreferences is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(toolPreferences, StringComparer.Ordinal);
        _lifecycleHooks = lifecycleHooks?.ToArray() ?? Array.Empty<IHook>();
        _resolveLifecycleValidations = resolveLifecycleValidations;
        _sessionMemoryPolicy = sessionMemoryPolicy;
        _catalog = catalog;
        _materializer = materializer;
        _fingerprint = WithHookFingerprint(fingerprint, _lifecycleHooks);
        _fingerprintArtifacts = fingerprintArtifacts?.ToArray() ?? Array.Empty<IPreparedArtifact>();
        _recordEffectiveFingerprint = recordEffectiveFingerprint;
        _activeSkills = activeSkills?.ToArray();
        _activeSkillToolIds = new HashSet<string>(activeSkillToolIds ?? [], StringComparer.Ordinal);
        _selection = selection;
        _store = store;
        _codecs = codecs;
        _artifacts = artifacts;
        _userSpendReader = userSpendReader;
        _spendReservations = spendReservations ?? (userSpendReader is not null
            ? new SqliteSpendReservationStore(Path.Combine(userSpendReader.UserDataDirectory, "spend-reservations.db")) : null);
        _maximumGenerationRequestAttempts = maximumGenerationRequestAttempts;
        _audit = audit;
        _redaction = redaction;
        _harness = harness;
        _boundary = boundary;
        _pricing = pricing;
        // Local execution and included subscription usage have no per-invocation USD charge.
        // Old Session snapshots may still carry a monetary limit; it cannot block these routes.
        // Undeclared billing retains conservative monetary admission for explicitly capped work.
        _monetaryBilling = billingMode is not (BillingMode.Local or BillingMode.IncludedQuota);
        _enforceSessionSpendCap = _monetaryBilling && (enforceSessionSpendCap ?? enforceDefaultSpendCaps);
        _enforceDailySpendCap = _monetaryBilling && (enforceDailySpendCap ?? enforceDefaultSpendCaps);
        _enforceDefaultSpendCaps = _enforceSessionSpendCap || _enforceDailySpendCap;
        _quotaAdmission = quotaAdmission;
        _quotaAllowsMeta = quotaAllowsMeta;
        _sessionCapUsd = sessionCapUsd;
        _dailyCapUsd = dailyCapUsd;
        _questionnaires = questionnaires;
        _questionnaireResponder = questionnaireResponder;
        _mailboxDeliveryCompleted = mailboxDeliveryCompleted;
        _stallEscalationUnavailable = stallEscalationUnavailable;
        _mutationEvidence = mutationEvidence;
        var forcedTool = forcedInitialToolName is null ? null : catalog.Find(new ToolId(forcedInitialToolName));
        if (forcedInitialToolName is not null && forcedTool is null)
            throw new ArgumentException("The forced command tool is not registered in this Turn.", nameof(forcedInitialToolName));
        if (exposeForcedInitialToolInDiscoveredMode && forcedInitialToolName is null)
            throw new ArgumentException("A forced tool name is required for explicit Discovered-mode disclosure.",
                nameof(exposeForcedInitialToolInDiscoveredMode));
        if (exposeForcedInitialToolInDiscoveredMode
            && (forcedTool is null || forcedTool.Descriptor.Source.Kind != SourceKind.Extension
                || forcedTool.Descriptor.Source.Trust is TrustLevel.Untrusted or TrustLevel.ThirdParty))
            throw new ArgumentException("Only a composed extension tool can receive explicit Discovered-mode disclosure.",
                nameof(exposeForcedInitialToolInDiscoveredMode));
        _forcedInitialToolName = forcedInitialToolName;
        _exposeForcedInitialToolInDiscoveredMode = exposeForcedInitialToolInDiscoveredMode;
        _workflowStageDefinition = workflowStageDefinition;
        _maxModelStepsPerTurn = maxModelStepsPerTurn;
        _maxToolCallsPerTurn = maxToolCallsPerTurn;
        _maxGenerationSeconds = maxGenerationSeconds;
        _sessionSecurityPolicy = sessionSecurityPolicy;
        _stallDecoder = new MemoizedEventDecoder(codecs);
        _metaModelProvider = metaModelProvider;
        _modelContextCapacity = modelContextCapacity;
    }

    /// <summary>Journal del Turn (tests: para abrir en él el Run al que pertenece el Turn).</summary>
    internal IEventStore JournalStore => _store;

    /// <summary>Reads skill validator inputs through the same tool, boundary, and permission path
    /// used by the current Turn. A failed or denied read remains fail-closed.</summary>
    internal SkillValidationReadResult? ReadForSkillValidation(string relativePath,
        CancellationToken cancellationToken) =>
        (_tools as ScriptedToolExecutor)?.ReadForSkillValidation(relativePath, cancellationToken);

    /// <summary>Ledger de mutaciones del Run activo, compartido con las tools del boundary.</summary>
    internal MutationLedger? MutationLedger => _boundary?.ReadRegistry().Ledger;

    private SessionSecurityPolicySet? CurrentSessionSecurityPolicy()
    {
        if (_sessionSecurityPolicy is null) return null;
        var policy = _sessionSecurityPolicy()
            ?? throw new InvalidDataException("Durable Session security policy is unavailable.");
        policy.Validate();
        return policy;
    }

    private int EffectiveMaxModelSteps() => Math.Min(_maxModelStepsPerTurn,
        CurrentSessionSecurityPolicy()?.MaxModelStepsPerTurn ?? _maxModelStepsPerTurn);

    private int EffectiveMaxToolCalls() => Math.Min(_maxToolCallsPerTurn,
        CurrentSessionSecurityPolicy()?.MaxToolCallsPerTurn ?? _maxToolCallsPerTurn);

    private int EffectiveMaxGenerationSeconds() => Math.Min(_maxGenerationSeconds,
        CurrentSessionSecurityPolicy()?.MaxGenerationSeconds ?? _maxGenerationSeconds);

    /// <summary>
    /// Entrega las muestras de mutación observadas a su destino de evidencia. Es telemetría: un
    /// fallo aquí nunca rompe el Turn ni el Run.
    /// </summary>
    internal void FlushMutationEvidence()
    {
        if (_mutationEvidence is null || MutationLedger is not { } ledger) return;
        var samples = ledger.TakeObservedSamples();
        if (samples.Count == 0) return;
        try { _mutationEvidence(samples); }
        catch (Exception exception) when (exception is not OperationCanceledException) { }
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
            boundary,
            null,
            false)
    {
    }


    private static PersistedSpend CombineSpend(PersistedSpend left, PersistedSpend right) => new(
        right.SessionUsd is { } session ? AddHistoricalSpend(left.SessionUsd, session) : null,
        right.DailyUsd is { } daily ? AddHistoricalSpend(left.DailyUsd, daily) : null,
        right.RunUsd is { } run ? AddHistoricalSpend(left.RunUsd, run) : null,
        left.Incomplete || right.Incomplete);

    // Meta-model calls are independent billable invocations, not part of ModelCompleted's
    // primary-model summary. Completed followed by Failed for the same invocation is one charge.
    private PersistedSpend ReadMetaSpend(IEnumerable<DomainEvent> events, SessionId sessionId,
        RunId runId, string today, IArtifactStore? evidenceArtifacts = null) =>
        new CanonicalSpendReader(_codecs, evidenceArtifacts ?? _artifacts).ReadMeta(events, sessionId, runId, today);

    // Null is an unrepresentable total, not a measured zero or a saturated amount.
    private static decimal? AddHistoricalSpend(decimal? total, decimal cost) =>
        CanonicalSpendReader.AddHistoricalSpend(total, cost);

    private static Exception UnrepresentableSpend(bool budgeted) => budgeted
        ? new BudgetExceededException("suma monetaria no representable: no se puede hacer cumplir el tope")
        : new InvalidDataException("Monetary total cannot be represented safely.");

    private static decimal AccumulatedSpend(decimal? persisted, decimal current, bool budgeted)
    {
        var total = AddHistoricalSpend(persisted, current);
        return total ?? throw UnrepresentableSpend(budgeted);
    }



    public sealed class TurnResult
    {
        /// <summary>Exact durable Turn produced or resumed by this invocation; null before admission.</summary>
        public TurnId? TurnId { get; init; }
        /// <summary>Redacted local diagnostic when execution fails before producing a result.</summary>
        public string? FailureDiagnostic { get; init; }
        /// <summary>Episodio de estancamiento registrado al cerrar este Turn (ADR-0048), si lo hubo.</summary>
        public StallOutcome? Stall { get; init; }
        public string? FinalText { get; }
        /// <summary>Validated result returned by a non-native Lane executor, when applicable.</summary>
        public AgentResult? ExternalAgentResult { get; init; }
        public string? ExternalResultSchemaId { get; init; }
        public ArtifactRef? ExternalResultRef { get; init; }
        /// <summary>Result artifact preserved while a child lane waits for explicit human acceptance.</summary>
        public ArtifactRef? DurableAcceptedResultRef { get; init; }
        public string? DurableAcceptedResultSchemaId { get; init; }
        internal ClaudeLaneStreamOutcome? ExternalLaneOutcome { get; init; }

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

    /// <summary>
    /// Resultado del watchdog al cerrar un Turn (ADR-0048). <c>Policy</c> nulo sin respuesta
    /// disponible; <c>Failure</c> no nulo cuando el watchdog no pudo registrar el episodio.
    /// </summary>
    public sealed record StallOutcome(PlanItemId? PlanItemId, string ItemDescription, int TurnsWithoutProgress,
        StallPolicy? Policy, IReadOnlyList<string> Skipped, InteractionId? InteractionId, string? Failure)
    {
        public static StallOutcome Failed(string diagnostic) =>
            new(null, "", 0, null, Array.Empty<string>(), null, diagnostic);
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

    /// <summary>
    /// Ejecuta la pregunta y persiste el Turn. turnStarted notifica un nuevo inicio ya durable,
    /// antes del provider; no se invoca al reanudar ni cuando falla la publicación.
    /// El observador interno debe ser no bloqueante y no lanzar excepciones.
    /// </summary>
    public TurnResult Ask(string question, string instruction, SessionId sessionId, RunId runId,
        LaneId laneId, string workingStateText, CancellationToken cancellationToken, string? origin = null,
        TurnInstructionSnapshot? instructionSnapshot = null, Action<TurnStarted>? turnStarted = null,
        Func<bool>? resumePreflightPassed = null)
    {
        // Without a durable shared reservation ledger, keep finite-budget work serialized for
        // compatibility and fail-safe behavior. Production Host installs that ledger for the
        // User profile, which lets independent read-only Lanes execute provider waits in parallel.
        // The ledger transactions now own admission across turns; this semaphore only prevents
        // two workers from re-entering the same Lane concurrently.
        var laneGateKey = _spendReservations is null ? (LaneId?)null : laneId;
        var gate = RunExecutionGates.GetValue(_store, _ => new())
            .GetOrAdd((sessionId, runId, laneGateKey), _ => new(1, 1));
        gate.Wait(cancellationToken);
        try
        {
            var stream = new EventStream(_store, _codecs, sessionId);
            var exactTurn = FindOpenTurn(stream, runId, laneId);
            var grant = RunProjection.Replay(sessionId, runId, _codecs, stream.EventsSince(1)).ModeAuthority?.Authorization;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (grant is not null)
            {
                var remaining = grant.GrantedAtUtc!.Value.AddSeconds(grant.Limits.MaxElapsedSeconds) - DateTimeOffset.UtcNow;
                // Expired admission is diagnosed by the durable guard, not a premature throw.
                if (remaining.TotalMilliseconds is > 0 and <= 4294967294)
                    deadline.CancelAfter(remaining);
            }
            var result = AskCore(question, instruction, sessionId, runId, laneId, workingStateText,
                deadline.Token, origin, instructionSnapshot, start => { exactTurn = start.TurnId; turnStarted?.Invoke(start); },
                resumePreflightPassed);
            // ADR-0048 §1: el watchdog se evalúa al cerrar el Turn, aún dentro del gate de la Lane.
            var stall = exactTurn is { } finished ? ApplyStallWatchdog(sessionId, runId, laneId, finished) : null;
            return new TurnResult(result.FinalText, result.StopReason, result.Steps, result.Usage,
                result.ToolCalls, result.ResponseArtifactId, result.PendingInteractionId)
            { TurnId = exactTurn, FailureDiagnostic = result.FailureDiagnostic, Stall = stall };
        }
        finally { gate.Release(); }
    }

    private TurnResult AskCore(string question, string instruction, SessionId sessionId, RunId runId,
        LaneId laneId, string workingStateText, CancellationToken cancellationToken, string? origin,
        TurnInstructionSnapshot? instructionSnapshot, Action<TurnStarted>? turnStarted,
        Func<bool>? resumePreflightPassed)
    {
        try { _selection.Route?.ReasoningCapability.ValidateRequest(_selection.Reasoning); }
        catch (InvalidOperationException)
        {
            return new TurnResult("Selected reasoning is incompatible with declared route capability.",
                StopReason.Error, 0, new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ToolUseTrace>(), null);
        }
        var stream = new EventStream(_store, _codecs, sessionId);
        var resumedTurnId = FindOpenTurn(stream, runId, laneId);
        var isResume = resumedTurnId is not null;
        var turnId = resumedTurnId ?? TurnId.New();
        RestoreTurnMutationBudget(stream, runId, turnId);
        var originalStart = isResume ? stream.EventsSince(1).LastOrDefault(evt =>
            evt.RunId == runId && _codecs.Decode(evt) is TurnStarted start
                && start.TurnId == resumedTurnId && start.LaneId == laneId) : null;
        var originalConfiguration = originalStart is null ? null : (TurnStarted)_codecs.Decode(originalStart);
        if (instructionSnapshot is not null)
        {
            instructionSnapshot.Validate();
            if (!string.Equals(instructionSnapshot.ResolvedInstruction, instruction, StringComparison.Ordinal))
                throw new ArgumentException("Turn instruction snapshot must match the applied instruction.", nameof(instructionSnapshot));
            // Private instruction snapshots retain exactly what the request applied.
            instructionSnapshot = instructionSnapshot with { ResolvedInstruction = instruction };
        }
        // Historical Turns without this contract must not acquire a current intent snapshot.
        if (isResume) instructionSnapshot = originalConfiguration?.InstructionSnapshot;
        if (originalConfiguration?.InstructionSnapshot is { } originalInstruction)
        {
            originalInstruction.Validate();
            instructionSnapshot = originalInstruction;
            instruction = originalInstruction.ResolvedInstruction;
        }
        var reasoningResolution = _selection.ReasoningResolution;
        if (isResume && (originalConfiguration?.ReasoningResolution is { } originalReasoning
            ? !originalReasoning.IsEquivalentTo(reasoningResolution) : reasoningResolution is not null))
            return new TurnResult("Cannot resume Turn: reasoning resolution differs from its original configuration.",
                StopReason.Error, 0, new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ToolUseTrace>(), null);
        // Freeze the initial plan revision, not later mutations legitimately emitted by this Turn.
        var initialPlanEvents = OwnTail(stream, runId).Where(evt => originalStart is null
            || evt.Sequence <= originalStart.Sequence).ToArray();
        var initialAuthority = RunProjection.Replay(sessionId, runId, _codecs, initialPlanEvents).ModeAuthority;
        // Legacy Runs have no explicit authority selection. Do not upgrade a historical Turn
        // fingerprint on resume; revocation remains effective in the live Run projection.
        var fingerprintAuthority = initialAuthority is { Revision: > 0 }
            && (originalStart is null || _codecs.Decode(originalStart) is TurnStarted
                { Fingerprint: { } existing } && existing.Components.Any(component => component.Name == "run.mode_authority"))
            ? initialAuthority : null;
        var laneConfiguration = FindAgentProfileForLane(stream.EventsSince(1), runId, laneId);
        var laneProfileId = laneConfiguration?.AgentProfile;
        if (_resolvedAgentProfile is not null && laneProfileId != _resolvedAgentProfile.Id)
            return new TurnResult("Cannot execute Turn: applied AgentProfile ceiling differs from the Lane configuration.",
                StopReason.Error, 0, new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ToolUseTrace>(), null);
        if (laneConfiguration is { } configured
            && (configured.AgentProfileRevision is not null || configured.AgentProfileHash is not null)
            && (_resolvedAgentProfile is null || configured.AgentProfileRevision != _resolvedAgentProfile.Revision
                || configured.AgentProfileHash != AgentProfileFingerprint.Hash(_resolvedAgentProfile)))
            return new TurnResult("Cannot execute Turn: durable AgentProfile revision or content differs from its applied configuration.",
                StopReason.Error, 0, new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ToolUseTrace>(), null);
        var discoveredToolIds = new HashSet<string>(StringComparer.Ordinal);
        var discoveredToolOrder = new List<string>();
        var lifecycleAnnotations = new List<ContextItem>();
        var lifecycleAnnotationIds = new HashSet<string>(StringComparer.Ordinal);
        var lifecycleDeniedToolIds = new HashSet<string>(StringComparer.Ordinal);
        var lifecycleDeniedTurn = false;
        var requiredLifecycleValidations = new Dictionary<string, HookRequireValidation>(StringComparer.Ordinal);
        foreach (var persistedHook in stream.EventsSince(1).Where(evt => evt.RunId == runId)
            .Select(_codecs.Decode).OfType<HookApplied>()
            .Where(applied => applied.Result == "validation_required"))
        {
            foreach (var validatorId in persistedHook.DetailIds ?? [])
            {
                if (validatorId.Length is > 0 and <= 160)
                    requiredLifecycleValidations.TryAdd(validatorId,
                        new HookRequireValidation(validatorId, "Previously requested lifecycle validation is unresolved."));
            }
        }
        HookDispatchResult DispatchLifecycle(LifecyclePoint point, string? toolId = null,
            string? redactedArguments = null)
        {
            if (_lifecycleHooks.Count == 0) return HookDispatchResult.Empty;
            var result = _hookDispatcher.DispatchAsync(_lifecycleHooks,
                new HookContext(point, toolId, redactedArguments, point.ToString()), cancellationToken)
                .AsTask().GetAwaiter().GetResult();
            foreach (var annotation in result.Annotations)
                if (!ExtensionContextData.IsExternal(annotation) && lifecycleAnnotationIds.Add(annotation.Id))
                    // Hook annotations are derived context, not canonical conversation.
                    // Keep their existing credential boundary without altering user/model text.
                    lifecycleAnnotations.Add(new ContextItem(annotation.Id, annotation.Kind,
                        _redaction.Redact(annotation.Content), annotation.EstimatedTokens,
                        annotation.Priority, annotation.Retention, annotation.Provenance,
                        annotation.PreserveWhenTrimming, annotation.TokenBudgetOverhead));
            lifecycleDeniedTurn |= result.Denied;
            lifecycleDeniedToolIds.UnionWith(result.DeniedToolIds);
            foreach (var validation in result.Validations)
                requiredLifecycleValidations.TryAdd(validation.ValidatorId, validation);
            foreach (var applied in result.Applications)
                stream.Append(new HookApplied(runId, turnId, applied.HookId, point.ToString(), applied.Result,
                    applied.DetailIds));
            try
            {
                var details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["point"] = point.ToString(),
                    ["hookCount"] = _lifecycleHooks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["denied"] = result.Denied.ToString(),
                    ["deniedToolIds"] = string.Join(",", result.DeniedToolIds.Order(StringComparer.Ordinal)),
                    ["annotationCount"] = result.Annotations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["requiredValidationCount"] = result.Validations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["warningCodes"] = string.Join(",", result.Warnings.Select(warning => warning.Split(':')[0])
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                };
                _audit.Record(new AuditRecord("hook.dispatch", null, sessionId, runId, DateTimeOffset.UtcNow,
                    runId.ToString(), details), CancellationToken.None);
            }
            catch (Exception) { /* diagnostics never change the dispatch decision */ }
            return result;
        }
        bool ResolvePendingLifecycleValidations()
        {
            if (requiredLifecycleValidations.Count == 0) return true;
            if (_resolveLifecycleValidations is null) return false;
            var required = requiredLifecycleValidations.Keys.Order(StringComparer.Ordinal).ToArray();
            var runEvents = stream.EventsSince(1).Where(evt => evt.RunId == runId).ToArray();
            var objective = runEvents.Select(_codecs.Decode).OfType<RunCreated>().LastOrDefault()?.Objective ?? "";
            var validationContext = LifecycleValidationContextBuilder.Build(sessionId, runId, turnId,
                objective, runEvents, _codecs, _redaction);
            IReadOnlyList<ExternalCompletionGateResult> outcomes;
            try
            {
                outcomes = _resolveLifecycleValidations(required,
                    path => ReadForSkillValidation(path, cancellationToken),
                    validationContext, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or InvalidDataException
                or InvalidOperationException or UnauthorizedAccessException or TimeoutException
                or ArgumentException)
            {
                outcomes = required.Select(id => new ExternalCompletionGateResult(
                    "unresolved:" + id, false, "Lifecycle validator failed closed ("
                        + exception.GetType().Name + ").", ValidatorId: id)).ToArray();
            }
            var byKey = outcomes.Where(item => item.ValidatorId is not null)
                .GroupBy(item => item.ValidatorId!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count() == 1 ? group.Single() : null,
                    StringComparer.Ordinal);
            var evidenceHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(validationContext + "\n" + string.Join("|", required))));
            var allPassed = true;
            foreach (var id in required)
            {
                byKey.TryGetValue(id, out var outcome);
                var passed = outcome?.Passed == true;
                var summary = _redaction.Redact(outcome?.Summary ?? "Validator is unknown, unavailable, or missing.");
                if (summary.Length > 512) summary = summary[..512];
                stream.Append(new HookValidationResolved(runId, turnId, id, evidenceHash, passed, summary),
                    DurabilityClass.Barrier);
                if (passed) requiredLifecycleValidations.Remove(id);
                else allPassed = false;
            }
            lifecycleDeniedTurn |= !allPassed;
            return allPassed;
        }
        var preparedTurnFingerprint = _recordEffectiveFingerprint
            ? RuntimeFingerprintFactory.PrepareTurnConfiguration(_fingerprint, _catalog, VisibleTools(discoveredToolIds, discoveredToolOrder),
                EffectiveSystemPrompt(instruction), PlanProjection.Replay(_codecs, initialPlanEvents).Latest(), _artifacts,
                laneProfileId, _activeSkills, _resolvedAgentProfile, fingerprintAuthority, instructionSnapshot,
                reasoningResolution, _toolPreferences)
            : null;
        var fingerprint = preparedTurnFingerprint?.Fingerprint ?? _fingerprint;
        // TurnStarted preserves the original configuration snapshot. A discovered tool plan is
        // represented by later ModelStep context snapshots, never by rewriting that event.
        var stepFingerprint = fingerprint;
        var stepFingerprintArtifacts = preparedTurnFingerprint?.Artifacts;
        var discoveredPlanChanged = false;
        if (originalStart is not null && _codecs.Decode(originalStart) is TurnStarted
            { Fingerprint: { } originalFingerprint })
        {
            if (originalFingerprint.Hash() != fingerprint.Hash())
                return new TurnResult("Cannot resume Turn: effective fingerprint differs from its original configuration.",
                    StopReason.Error, 0, new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ToolUseTrace>(), null);
            // Equal configuration hashes do not make newly allocated artifact receipts durable.
            // Resume retains the fingerprint and exact refs rooted by the original TurnStarted.
            fingerprint = originalFingerprint;
        }
        RuntimeFingerprintFactory.PreparedRuntimeFingerprint? recoveredPlan = null;
        string? recoveredPlanError = null;
        if (isResume && _selection.ToolMode == ToolMode.Discovered
            && TryRecoverDiscoveredToolPlan(stream, new TurnId(resumedTurnId!.Value), instruction, initialPlanEvents, laneProfileId,
                fingerprintAuthority, instructionSnapshot, reasoningResolution, discoveredToolIds, discoveredToolOrder,
                out recoveredPlan, out recoveredPlanError))
        {
            stepFingerprint = recoveredPlan!.Fingerprint;
            stepFingerprintArtifacts = recoveredPlan.Artifacts;
            discoveredPlanChanged = true;
        }
        else if (isResume && recoveredPlanError is not null)
        {
            return new TurnResult(recoveredPlanError, StopReason.Error, 0,
                new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ToolUseTrace>(), null);
        }
        var queuedQuestion = (question ?? "").Length > 0
            && FollowUpQueue.TryQueue(_store, _codecs, sessionId, runId, laneId, question ?? "", origin);
        var runEvents = stream.EventsSince(1);
        _approvedProtectedPreferenceIdentities.Clear();
        foreach (var approval in runEvents.Select(_codecs.Decode).OfType<ToolPreferenceSubstitutionApproved>())
            _approvedProtectedPreferenceIdentities.Add(ApprovalKey(approval.SourceToolId,
                approval.TargetToolId, approval.IdentityHash));
        if (RunProjection.Replay(sessionId, runId, _codecs, runEvents).IsTerminal())
            return new TurnResult("Run is terminal; queued FollowUps remain inert.", StopReason.Error,
                0, new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ToolUseTrace>(), null);
        if (FindPendingRunInteraction(runEvents, runId) is { } pendingInteraction)
        {
            var stop = pendingInteraction.Kind == InteractionKind.BudgetExceeded
                ? StopReason.Cancelled : StopReason.InputRequired;
            return new TurnResult(null, stop, 0,
                new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ToolUseTrace>(), null,
                pendingInteraction.InteractionId);
        }
        var turnTask = FindTaskForLane(runEvents, runId, laneId);
        var executionId = HostAgentInvocation.Resolve(_store, _codecs, sessionId, runId, turnTask, laneId, stream);
        using var executionScope = ExecutionScope.Begin(new ExecutionScopeState(runId,
            turnTask, laneId, turnId, ExecutionId: executionId));
        var nextModelStepIndex = ReadNextModelStepIndex(stream, turnId);
        var dedicatedImageRoute = string.Equals(_selection.Route?.Profile,
            OmniCore.Models.OpenRouterImageGenerationProvider.ProfileName, StringComparison.Ordinal);
        IReadOnlyList<ToolDefinition> RequestTools() => dedicatedImageRoute
            ? Array.Empty<ToolDefinition>()
            : ReadTurnToolCallCount(stream, turnId) >= EffectiveMaxToolCalls()
            ? Array.Empty<ToolDefinition>() : VisibleTools(discoveredToolIds, discoveredToolOrder)
                .Where(tool => !lifecycleDeniedToolIds.Contains(tool.Name)).ToArray();
        void RefreshStepFingerprint(IReadOnlyList<ToolDefinition> requestTools)
        {
            var configured = RuntimeFingerprintFactory.PrepareTurnConfiguration(_fingerprint, _catalog,
                requestTools, EffectiveSystemPrompt(instruction), PlanProjection.Replay(_codecs, initialPlanEvents).Latest(),
                _artifacts, laneProfileId, _activeSkills, _resolvedAgentProfile, fingerprintAuthority,
                instructionSnapshot, reasoningResolution, _toolPreferences);
            if (!string.Equals(configured.Fingerprint.Hash(), stepFingerprint.Hash(), StringComparison.Ordinal))
            {
                stepFingerprint = configured.Fingerprint;
                stepFingerprintArtifacts = configured.Artifacts;
                discoveredPlanChanged = true;
            }
        }
        var forcedToolName = isResume || dedicatedImageRoute ? null : _forcedInitialToolName;
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var budget = ReadRunBudget(stream, runId);
        if (!_monetaryBilling) budget = budget with { MaxCostUsd = null };
        // Primary usage for this Ask remains in SpendGuard. Meta usage is reread in full
        // at each boundary, avoiding a gap/overlap between a historical scan and a sequence delta.
        // Known monetary totals must remain representable even without a cap. Only an
        // unpriced session with no historical monetary evidence can omit monetary replay.
        var replayMonetarySpend = _enforceDefaultSpendCaps || budget.MaxCostUsd is not null
            || _pricing is not null || HasHistoricalMonetaryEvidence(runEvents);
        // A completed image response is already a durable provider result. On resume, validate
        // its exact CAS/journal publication before spend admission so a missing reported image
        // price cannot strand a result that requires no further provider call. The normal cap
        // still applies to every new invocation below.
        string? recoveredImageText = null;
        var recoveredImageImages = new List<ValidatedGeneratedImage>();
        var recoveredImageParts = new List<(string Kind, string? Text, int ImageIndex)>();
        var recoveredImageSteps = 0;
        var recoveredImagePublished = false;
        string? recoveredImageArtifactId = null;
        var recoveredImageCompletion = isResume && TryRecoverCompletedImageResponse(stream, turnId,
            out recoveredImageText, recoveredImageImages, recoveredImageParts, out recoveredImageSteps,
            out recoveredImagePublished, out recoveredImageArtifactId);
        if (replayMonetarySpend)
        {
            var openingSpend = CombineSpend(ReadJournalSpend(stream, sessionId, runId, today, _enforceDefaultSpendCaps),
                ReadMetaJournalSpend(stream, sessionId, runId, today, _enforceDefaultSpendCaps));
            if (!recoveredImageCompletion
                && (_enforceDefaultSpendCaps || budget.MaxCostUsd is not null) && openingSpend.Incomplete)
            {
                const string detail = "uso histórico incompleto: no se puede hacer cumplir el tope";
                var interactionId = EmitBudgetExceeded(stream, turnId, detail);
                return new TurnResult(detail, StopReason.Cancelled, 0,
                    new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ToolUseTrace>(), null, interactionId);
            }
        }

        List<ModelMessage> messages;
        var messageOwners = new Dictionary<ModelMessage, RunId>(ReferenceEqualityComparer.Instance);
        try
        {
            new RunSummaryService(_store, _codecs, _artifacts, static text => text).EnsureRecorded(sessionId);
            messages = LoadConversation(stream, runId, isResume ? turnId : null, laneId, messageOwners);
        }
        catch (InvalidDataException)
        {
            return new TurnResult("Persisted visible reasoning is invalid.", StopReason.Error, 0,
                new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ToolUseTrace>(), null);
        }
        messages = ProjectAssistantImagesForModelContext(messages);
        if (isResume && resumePreflightPassed is not null)
        {
            // The continuation is an effect. First validate the durable Turn fingerprint,
            // discovery plan, lane profile, active Run, pending interaction, and transcript above.
            // Then apply the already-approved tool so its result is part of the rebuilt context.
            if (!resumePreflightPassed())
            {
                return new TurnResult("Approved interaction could not be continued under the current effective policy.",
                    StopReason.Error, 0, new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ToolUseTrace>(), null)
                    { TurnId = turnId };
            }
            try
            {
                messages = ProjectAssistantImagesForModelContext(
                    LoadConversation(stream, runId, turnId, laneId, messageOwners));
            }
            catch (InvalidDataException)
            {
                return new TurnResult("Persisted visible reasoning is invalid.", StopReason.Error, 0,
                    new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ToolUseTrace>(), null);
            }
        }
        var safeQuestion = isResume || queuedQuestion ? "" : (question ?? "");
        var pendingFollowUps = isResume || origin == "InteractionResponse(ModelRouteConsent)"
            ? Array.Empty<FollowUpQueue.Item>()
            : FollowUpQueue.Pending(_store, _codecs, sessionId, runId, laneId).Take(1).ToArray();
        foreach (var followUp in pendingFollowUps)
        {
            var text = FollowUpText(followUp.InputPartsJson);
            if (text.Length > 0)
                messages.Add(new ModelMessage(MessageRole.User, new ContentBlock[] { new TextBlock(text) }));
        }
        if (safeQuestion.Length > 0)
        {
            messages.Add(new ModelMessage(MessageRole.User, new ContentBlock[] { new TextBlock(safeQuestion) }));
        }

        var allToolCalls = new List<ToolUseTrace>();
        var usage = new TokenUsage(0, 0, 0, 0, 0);
        var turnUsage = ReadTurnModelStepUsage(stream, turnId, out var invalidPersistedUsage);
        var invalidUsageObserved = false;

        var configuredRunCap = budget.MaxCostUsd;
        var sessionCap = BudgetContinuation.Limit(stream.EventsSince(1), _codecs, sessionId, runId,
            today, "session", _sessionCapUsd);
        var dailyCap = _dailyCapUsd;
        if (_enforceDefaultSpendCaps && _store is IWorkspaceJournalReader workspaceJournal)
        {
            try
            {
                dailyCap = BudgetContinuation.Limit(workspaceJournal.ReadEvents(EventType.Of("interaction.requested"))
                    .Concat(workspaceJournal.ReadEvents(EventType.Of("interaction.resolved")))
                    .Concat(workspaceJournal.ReadEvents(EventType.Of("interaction.expired"))), _codecs,
                    sessionId, runId, today, "daily", _dailyCapUsd);
                dailyCap = Math.Max(dailyCap, UserDailyBudgetContinuation.Limit(_userSpendReader, _codecs,
                    today, _dailyCapUsd));
            }
            catch (Exception) { dailyCap = _dailyCapUsd; } // No evidence means no increase.
        }
        if (configuredRunCap is not null)
            budget = budget with { MaxCostUsd = BudgetContinuation.Limit(stream.EventsSince(1), _codecs,
                sessionId, runId, today, "run", configuredRunCap.Value) };
        // Host enforces monetary limits against replayed + current usage; the guard owns
        // token/turn/tool limits, not a second monetary counter that omits persisted spend.
        var guard = new SpendGuard(budget with { MaxCostUsd = null });
        var steps = 1;
        var started = false;

        PersistedSpend CurrentSpend() => !replayMonetarySpend
            ? new PersistedSpend(0m, 0m, 0m, false)
            : CombineSpend(CombineSpend(ReadJournalSpend(stream, sessionId, runId, today, _enforceDefaultSpendCaps),
                ReadMetaJournalSpend(stream, sessionId, runId, today, _enforceDefaultSpendCaps)),
                ReadOtherWorkspaceSpend(stream, sessionId, runId, today));

        void ValidateBeforeInvocation()
        {
            ValidateUltraCode(invocation: true);
            ValidateTokenBudget(includeNextInvocation: true);
            var spend = CurrentSpend();
            var capped = budget.MaxCostUsd is not null || _enforceDefaultSpendCaps;
            if (capped && (_pricing is null || !_pricing.IsComplete))
                throw new BudgetExceededException("precio desconocido: no se puede hacer cumplir el tope");
            if (capped && spend.Incomplete)
                throw new BudgetExceededException("uso histórico incompleto: no se puede hacer cumplir el tope");
            var runCost = AccumulatedSpend(spend.RunUsd, 0m, capped);
            var sessionCost = AccumulatedSpend(spend.SessionUsd, 0m, capped);
            var dailyCost = AccumulatedSpend(spend.DailyUsd, 0m, capped);
            if (budget.MaxCostUsd is not null && runCost >= budget.MaxCostUsd.Value)
                throw new BudgetExceededException("límite de costo de Run alcanzado ($" + budget.MaxCostUsd.Value + ")",
                    new("run", configuredRunCap!.Value, budget.MaxCostUsd.Value, runId.ToString(), null));
            if (_enforceSessionSpendCap && sessionCost >= sessionCap)
                throw new BudgetExceededException("límite de sesión alcanzado ($" + sessionCap + ")",
                    new("session", _sessionCapUsd, sessionCap, null, null));
            if (_enforceDailySpendCap && dailyCost >= dailyCap)
                throw new BudgetExceededException("límite diario alcanzado ($" + dailyCap + ")",
                    new("daily", _dailyCapUsd, dailyCap, null, today));
        }

        void ValidateUltraCode(bool invocation = false, ToolCallId? toolCall = null)
        {
            DelegationBudgetGuard.Validate(stream.EventsSince(1), _codecs, _artifacts,
                _store, _spendReservations, sessionId, runId, laneId, turnId,
                !isResume && !started, invocation, toolCall,
                ModelInvocationCostBound.Quote(_selection, _pricing, _modelContextCapacity, _maximumGenerationRequestAttempts),
                ModelInvocationCostBound.TokenCeiling(_selection, _modelContextCapacity, _maximumGenerationRequestAttempts));
            UltraCodeExecutionGuard.Validate(stream.EventsSince(1), _codecs, _artifacts, _store, _spendReservations,
                sessionId, runId, turnId, newTurn: !isResume && !started, invocation,
                ModelInvocationCostBound.Quote(_selection, _pricing, _modelContextCapacity,
                    _maximumGenerationRequestAttempts), toolCall);
        }

        void ValidateCounterBudget(IReadOnlyList<DomainEvent> journal, bool newTurn, ToolCallId? newToolCall)
        {
            var events = journal.Where(evt => evt.RunId == runId || evt.CorrelationId == runId).ToArray();
            var payloads = events.Select(_codecs.Decode).ToArray();
            var runBudget = payloads.OfType<RunCreated>().SingleOrDefault()?.Budget;
            if (runBudget is null) return;
            var turns = payloads.OfType<TurnStarted>().Select(item => item.TurnId).Distinct().ToArray();
            var toolUseCount = checked(payloads.OfType<ToolCallRequested>().Select(item => item.ToolCallId)
                .Distinct().LongCount() + ExternalLaneAccounting.ToolUseCount(
                    payloads.OfType<ExternalLaneUsageRecorded>(), payloads.OfType<ExternalLaneActivityObserved>()));
            var allocations = RunBudgetPool.For(_store).ReadChildAllocations(journal, _codecs,
                _artifacts, _store, _spendReservations, sessionId, runId, laneId);
            var addingTurn = newTurn && !turns.Contains(turnId) ? 1L : 0L;
            var addingTool = newToolCall is { } id
                && !payloads.OfType<ToolCallRequested>().Any(item => item.ToolCallId == id) ? 1L : 0L;
            if (CurrentSessionSecurityPolicy() is { } sessionSecurity)
            {
                var turnToolCalls = journal.Where(evt => evt.TurnId == turnId)
                    .Select(evt => _codecs.Decode(evt)).OfType<ToolCallRequested>()
                    .Select(item => item.ToolCallId).Distinct().LongCount();
                if (checked(turnToolCalls + addingTool) > sessionSecurity.MaxToolCallsPerTurn)
                    throw new BudgetExceededException("Session security tool-call limit for this Turn was reached.");
            }
            if (runBudget.MaxTurns is { } maxTurns
                && checked(turns.LongLength + allocations.Turns + addingTurn) > maxTurns)
                throw new BudgetExceededException("límite de turnos del Run alcanzado");
            if (runBudget.MaxToolCalls is { } maxTools
                && checked(toolUseCount + allocations.ToolCalls + addingTool) > maxTools)
                throw new BudgetExceededException("límite de herramientas del Run alcanzado");
        }

        void ValidateTokenBudget(bool includeNextInvocation)
        {
            if (budget.MaxTokens is null) return;
            var pool = RunBudgetPool.For(_store);
            var tokens = RunTokenBudgetReader.Read(stream.EventsSince(1), _codecs, runId, budget.MaxTokens,
                (evt, step) => pool.OwnsPendingPrimary(evt, step, _spendReservations));
            if (tokens.Remaining is not { } remaining)
                throw new BudgetExceededException("uso de tokens histórico desconocido: " + tokens.Limitation);
            if (remaining < 0)
                throw new BudgetExceededException("límite de tokens del Run excedido: " + tokens.Limit);
            if (!includeNextInvocation) return;
            // When a durable pool is configured, its SQLite write transaction accounts for every
            // sibling ceiling atomically, including pending requests. The replay value alone is
            // only settled usage and must not be mistaken for unreserved availability.
            if (_spendReservations is not null) return;
            var maximum = ModelInvocationCostBound.TokenCeiling(_selection, _modelContextCapacity,
                _maximumGenerationRequestAttempts);
            if (maximum is null)
                throw new BudgetExceededException("cota de tokens de invocación desconocida");
            if (maximum.Value > remaining)
                throw new BudgetExceededException("límite de tokens del Run insuficiente para la siguiente invocación");
        }

        string? ReserveTokenInvocation(string primaryIdentity)
        {
            if (_spendReservations is null) return null;
            var currentTask = stream.EventsSince(1).Select(_codecs.Decode).OfType<TaskCreated>()
                .SingleOrDefault(task => task.TaskId == turnTask);
            var taskTokenLimit = currentTask?.ParentTaskId is not null ? currentTask.Budget.MaxTokens : null;
            if (budget.MaxTokens is null && taskTokenLimit is null) return null;
            var maximum = ModelInvocationCostBound.TokenCeiling(_selection, _modelContextCapacity,
                _maximumGenerationRequestAttempts);
            if (maximum is null || maximum <= 0)
                throw new BudgetExceededException("cota de tokens de invocación desconocida");
            var pool = RunBudgetPool.For(_store);
            var identity = RunBudgetPool.TokenReservationIdentity(primaryIdentity);
            var admission = _spendReservations.TryReserve(identity, maximum.Value,
                () =>
                {
                    // Re-read canonical usage while the SQLite reservation write transaction is
                    // held. A sibling may have completed and settled since the outer guard ran;
                    // stale settled totals plus a now-released pending row would over-admit.
                    var events = stream.EventsSince(1);
                    var limits = new List<SqliteSpendReservationStore.Limit>();
                    if (budget.MaxTokens is { } limit)
                    {
                        var state = RunTokenBudgetReader.Read(events, _codecs, runId, limit,
                            (evt, step) => pool.OwnsPendingPrimary(evt, step, _spendReservations));
                        if (state.Remaining is not { } remaining || remaining < 0)
                            throw new BudgetExceededException("uso de tokens histórico desconocido: " + state.Limitation);
                        var allocations = pool.ReadChildAllocations(events, _codecs, _artifacts, _store,
                            _spendReservations, sessionId, runId, laneId);
                        var held = checked(limit - remaining + allocations.Tokens);
                        limits.Add(new("run_tokens", runId.ToString(), limit, held));
                    }
                    if (taskTokenLimit is { } taskLimit && currentTask is not null)
                    {
                        var own = events.Where(evt => evt.RunId == runId && evt.LaneId == laneId).ToArray();
                        var state = RunTokenBudgetReader.Read(own, _codecs, runId, taskLimit,
                            (evt, step) => pool.OwnsPendingPrimary(evt, step, _spendReservations));
                        if (state.Remaining is not { } remaining || remaining < 0)
                            throw new BudgetExceededException("uso de tokens del Task histórico desconocido: " + state.Limitation);
                        limits.Add(new("task_tokens", laneId.ToString(), taskLimit, taskLimit - remaining));
                    }
                    return limits;
                }, out var blocked);
            if (admission == SqliteSpendReservationStore.Admission.AlreadyExists)
                throw new BudgetExceededException("invocación ya reservada: no se permite repetir el envío");
            if (admission == SqliteSpendReservationStore.Admission.Insufficient)
                throw new BudgetExceededException("límite de tokens del Run insuficiente para la siguiente invocación (" + blocked + ")");
            return identity;
        }

        bool CanInvokeMeta()
        {
            // A primary invocation's consent does not authorize additional compaction calls.
            if (_quotaAllowsMeta?.Invoke() == false) return false;
            try { ValidateBeforeInvocation(); return true; }
            catch (BudgetExceededException) { return false; }
        }

        string? ReserveInvocation(string id)
        {
            if (_spendReservations is null) return null;
            var admissionEvents = stream.EventsSince(1);
            var currentTask = admissionEvents.Select(_codecs.Decode).OfType<TaskCreated>()
                .SingleOrDefault(task => task.TaskId == turnTask);
            var childCostLimit = currentTask?.ParentTaskId is not null ? currentTask.Budget.MaxCostUsd : null;
            var modeAuthorization = RunProjection.Replay(sessionId, runId, _codecs, admissionEvents)
                .ModeAuthority?.Authorization;
            var hasCappedAdmission = budget.MaxCostUsd is not null || _enforceDefaultSpendCaps
                || childCostLimit is not null || modeAuthorization?.Limits.MaxSpendUsd is not null;
            if (!hasCappedAdmission) return null;
            if (_enforceDailySpendCap && _userSpendReader is not null)
                QualificationSpendAccounting.Reconcile(SqliteModelQualificationStore.ReadCanonicalProbeReceipts(
                    _userSpendReader.UserDataDirectory, CancellationToken.None), _spendReservations);
            var maximum = ModelInvocationCostBound.Quote(_selection, _pricing, _modelContextCapacity,
                _maximumGenerationRequestAttempts);
            if (maximum is null) throw new BudgetExceededException("cota monetaria de invocación desconocida");
            var reservationDay = today;
            var reservationDailyCap = dailyCap;
            IReadOnlyList<SqliteSpendReservationStore.Limit> ReadLimits()
            {
                // Fresh canonical totals already contain completed steps of this Ask: do not add guard again.
                var day = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                reservationDay = day;
                var fresh = CombineSpend(CombineSpend(ReadJournalSpend(stream, sessionId, runId, day, _enforceDefaultSpendCaps),
                    ReadMetaJournalSpend(stream, sessionId, runId, day, _enforceDefaultSpendCaps)),
                    ReadOtherWorkspaceSpend(stream, sessionId, runId, day));
                if (fresh.Incomplete || fresh.RunUsd is null || fresh.SessionUsd is null || fresh.DailyUsd is null)
                    throw new BudgetExceededException("uso histórico incompleto para reserva");
                var allocations = RunBudgetPool.For(_store).ReadChildAllocations(stream.EventsSince(1), _codecs,
                    _artifacts, _store, _spendReservations, sessionId, runId, laneId);
                var limits = new List<SqliteSpendReservationStore.Limit>();
                var allocatedRunSpend = checked(fresh.RunUsd.Value + allocations.CostUsd);
                if (budget.MaxCostUsd is { } runLimit)
                    limits.Add(new("run", runId.ToString(), runLimit, allocatedRunSpend));
                if (modeAuthorization?.Limits.MaxSpendUsd is { } coordinationSpendLimit)
                    limits.Add(new("ultra_run", runId.ToString(), coordinationSpendLimit,
                        allocatedRunSpend));
                if (childCostLimit is { } taskLimit && currentTask is not null)
                {
                    var ownEvents = stream.EventsSince(1).Where(evt => evt.RunId == runId && evt.LaneId == laneId).ToArray();
                    var reader = new CanonicalSpendReader(_codecs, _artifacts);
                    var pool = RunBudgetPool.For(_store);
                    var ownPrimary = reader.ReadPrimary(ownEvents, sessionId, runId, day,
                        (evt, step) => pool.OwnsPendingPrimary(evt, step, _spendReservations));
                    var ownMeta = reader.ReadMeta(ownEvents, sessionId, runId, day);
                    if (ownPrimary.Incomplete || ownMeta.Incomplete || ownPrimary.RunUsd is null || ownMeta.RunUsd is null)
                        throw new BudgetExceededException("uso histórico del Task incompleto para reserva");
                    limits.Add(new("task_usd", laneId.ToString(), taskLimit,
                        checked(ownPrimary.RunUsd.Value + ownMeta.RunUsd.Value)));
                }
                if (_enforceDailySpendCap)
                {
                    var effectiveDaily = _dailyCapUsd;
                    if (_store is IWorkspaceJournalReader journal)
                        effectiveDaily = BudgetContinuation.Limit(journal.ReadEvents(EventType.Of("interaction.requested"))
                            .Concat(journal.ReadEvents(EventType.Of("interaction.resolved")))
                            .Concat(journal.ReadEvents(EventType.Of("interaction.expired"))), _codecs,
                            sessionId, runId, day, "daily", _dailyCapUsd);
                    effectiveDaily = Math.Max(effectiveDaily, UserDailyBudgetContinuation.Limit(_userSpendReader, _codecs, day, _dailyCapUsd));
                    reservationDailyCap = effectiveDaily;
                    limits.Add(new("daily", "user", effectiveDaily,
                        checked(fresh.DailyUsd.Value + allocations.CostUsd)));
                }
                if (_enforceSessionSpendCap)
                    limits.Add(new("session", sessionId.ToString(), sessionCap,
                        checked(fresh.SessionUsd.Value + allocations.CostUsd)));
                return limits;
            }
            var admission = _spendReservations.TryReserve(id, maximum.Value, ReadLimits, out var blocked);
            if (admission == SqliteSpendReservationStore.Admission.AlreadyExists)
                throw new BudgetExceededException("invocación ya reservada: no se permite repetir el envío");
            if (admission == SqliteSpendReservationStore.Admission.Insufficient)
                throw new BudgetExceededException("cota de invocación excede presupuesto disponible (" + blocked + ")",
                    blocked switch
                    {
                        "run" => new("run", configuredRunCap!.Value, budget.MaxCostUsd!.Value, runId.ToString(), null),
                        "session" => new("session", _sessionCapUsd, sessionCap, null, null),
                        _ => new("daily", _dailyCapUsd, reservationDailyCap, null, reservationDay),
                    });
            return id;
        }

        void FinishReservation(string? id, decimal? cost, string receipt, long observedSends,
            bool unpricedOutput = false)
        {
            if (id is null || cost is null) return; // Unavailable usage cannot release an in-flight bound.
            if (unpricedOutput)
            {
                _spendReservations!.RecordUncertainCompletion(id, cost.Value, receipt);
                return; // Keep the remaining declared image exposure held; never call it measured spend.
            }
            if (observedSends == 1 || observedSends == 0 && _maximumGenerationRequestAttempts == 1)
                _spendReservations!.Settle(id, cost.Value, receipt);
            else _spendReservations!.RecordUncertainCompletion(id, cost.Value, receipt);
        }

        var hasReservationBoundary = _spendReservations is not null && (budget.MaxCostUsd is not null || _enforceDefaultSpendCaps);
        string MetaReservationId(string invocation) => $"meta/{sessionId}/{runId}/{laneId}/{turnId}/{invocation}";
        bool ReserveMeta(string invocation)
        {
            try { return ReserveInvocation(MetaReservationId(invocation)) is not null; }
            catch (BudgetExceededException) { return false; }
        }
        void FinishMeta(string invocation, decimal? cost, long observedSends)
        {
            if (cost is null) return;
            var receipt = stream.EventsSince(1).First(evt => _codecs.Decode(evt) switch
            {
                MetaModelInvocationCompleted completed => completed.InvocationId == invocation,
                MetaModelInvocationFailed failed => failed.InvocationId == invocation,
                _ => false,
            });
            FinishReservation(MetaReservationId(invocation), cost, receipt.EventId.ToString(), observedSends);
        }

        try
        {
            try
            {
                ValidateUltraCode(invocation: true);
                // Preserve uncapped Turn lifecycle diagnostics; monetary caps must block
                // before context materialization can itself issue a billable request.
                if (budget.MaxTokens is not null || _metaModelProvider is not null
                    && (budget.MaxCostUsd is not null || _enforceDefaultSpendCaps)) ValidateBeforeInvocation();
            }
            catch (BudgetExceededException ex)
            {
                EmitBudgetExceeded(stream, turnId, ex.Detail, ex.Continuation);
                return new TurnResult("Presupuesto agotado: " + ex.Detail, StopReason.Cancelled, 0,
                    usage, allToolCalls.ToArray(), null);
            }
            var initialModelHooks = DispatchLifecycle(LifecyclePoint.BeforeModel);
            if (initialModelHooks.Denied || !ResolvePendingLifecycleValidations())
                throw new LifecycleHookWithheldException("lifecycle.hook.model_invocation_withheld");
            RefreshStepFingerprint(RequestTools());
            var preparedContext = MaterializeTurnContext(stream, sessionId, runId, laneId, turnId,
                workingStateText, instruction, messages, messageOwners, stepFingerprint, cancellationToken, CanInvokeMeta,
                hasReservationBoundary ? ReserveMeta : null,
                hasReservationBoundary ? id => _spendReservations!.MarkDispatched(MetaReservationId(id)) : null,
                hasReservationBoundary ? FinishMeta : null,
                hasReservationBoundary ? id => _spendReservations!.ReleaseBeforeDispatch(MetaReservationId(id)) : null,
                lifecycleAnnotations,
                (point, detail) =>
                {
                    var result = DispatchLifecycle(point, redactedArguments: detail);
                    if (result.Denied || lifecycleDeniedTurn || result.Validations.Count > 0
                        || requiredLifecycleValidations.Count > 0)
                        throw new LifecycleHookWithheldException("lifecycle.hook.compaction_withheld");
                });
            var materialized = preparedContext.Snapshot;

            // ContextOverflow: el contenido protegido no cabe ni después de recortar la conversación.
            if (materialized.Overflowed)
            {
                using var overflowPublication = !isResume
                    ? (_artifacts as IArtifactPublicationLease)?.AcquirePublicationLease(CancellationToken.None) : null;
                var overflowStart = new List<DomainEventPayload>();
                if (!isResume)
                {
                    PublishFingerprintArtifacts(fingerprint, preparedTurnFingerprint?.Artifacts);
                    preparedContext.PublishArtifacts();
                    var snapshotArtifact = PersistContextSnapshot(materialized, _selection.ContextBudget);
                    overflowStart.AddRange(FollowUpQueue.PromotionEvents(pendingFollowUps, runId, laneId, turnId));
                    if (safeQuestion.Length > 0)
                        overflowStart.Add(new UserInputReceived(runId,
                            "\"" + System.Text.Json.JsonEncodedText.Encode(safeQuestion) + "\"", null, origin));
                    overflowStart.Add(new TurnStarted(turnId, laneId, fingerprint, snapshotArtifact,
                        instructionSnapshot, reasoningResolution));
                }
                if (isResume) stream.AppendBatch(overflowStart, DurabilityClass.Barrier);
                else stream.AppendBatchAdmitted(overflowStart, DurabilityClass.Barrier, journal =>
                {
                    ValidateCounterBudget(journal, newTurn: true, newToolCall: null);
                    ValidateUltraCode();
                });
                started = true;
                if (!isResume)
                    turnStarted?.Invoke(overflowStart.OfType<TurnStarted>().Single());
                // La state machine de Turn: Started → … → Abandoned (terminal). NUNCA se emite
                // TurnCompleted tras Abandoned (P1: transición inválida).
                AppendTerminalTurn(stream, sessionId, runId, laneId, turnId,
                    new TurnAbandoned(turnId, "ContextOverflow: el contexto no entra en el presupuesto"));
                return new TurnResult("ContextOverflow: el contexto no cabe en el presupuesto del modelo",
                    StopReason.ContextOverflow, 0, usage, allToolCalls.ToArray(), null);
            }

            if (!isResume)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var contextPublication = (_artifacts as IArtifactPublicationLease)
                    ?.AcquirePublicationLease(CancellationToken.None);
                PublishFingerprintArtifacts(fingerprint, preparedTurnFingerprint?.Artifacts);
                preparedContext.PublishArtifacts();
                var snapshotArtifact = PersistContextSnapshot(materialized, _selection.ContextBudget);
                var startEvents = new List<DomainEventPayload>();
                var recordsUserInput = safeQuestion.Length > 0 || pendingFollowUps.Length == 0
                    && origin is not ("InteractionResponse(ModelRouteConsent)" or "AlreadyPersisted(ConversationInput)");
                // A consent resumes existing intent, not a new user message. Only pair this
                // synthetic input boundary with the input that actually returns it to Running.
                if (recordsUserInput
                    && RunProjection.Replay(sessionId, runId, _codecs, stream.EventsSince(1)).State == RunState.Running)
                    startEvents.Add(new RunAwaitingInput(runId, laneId));
                startEvents.AddRange(FollowUpQueue.PromotionEvents(pendingFollowUps, runId, laneId, turnId));
                var encodedInput = System.Text.Json.JsonEncodedText.Encode(safeQuestion);
                if (recordsUserInput)
                    startEvents.Add(new UserInputReceived(runId, "\"" + encodedInput + "\"", null, origin));
                startEvents.Add(new TurnStarted(turnId, laneId, fingerprint, snapshotArtifact,
                    instructionSnapshot, reasoningResolution));
                stream.AppendBatchAdmitted(startEvents, DurabilityClass.Barrier, journal =>
                {
                    ValidateCounterBudget(journal, newTurn: true, newToolCall: null);
                    ValidateUltraCode();
                });
                started = true;
                turnStarted?.Invoke(startEvents.OfType<TurnStarted>().Single());
                // Límites de mutación por Turn (ADR-0044 §5): un Turn nuevo reinicia el contador del
                // Turn; los totales del Run se conservan. Un Turn reanudado sigue con su contador.
                _boundary?.BeginTurn();
            }
            started = true;

            if (invalidPersistedUsage)
                throw new InvalidDataException("Persisted token usage cannot be represented safely.");

            var finalText = recoveredImageText;
            var finalImages = recoveredImageImages;
            var finalResponseParts = recoveredImageParts;
            var finalImageModelStepIndex = -1;
            var stop = StopReason.EndTurn;
            var recoveredStepCount = 0;
            string? recoveredResponseArtifactId = null;
            var finalResponseAlreadyPublished = false;
            recoveredStepCount = recoveredImageSteps;
            finalResponseAlreadyPublished = recoveredImagePublished;
            recoveredResponseArtifactId = recoveredImageArtifactId;
            if (recoveredImageCompletion)
            {
                // ModelStepCompleted already owns the provider result and its CAS snapshot. Finish
                // publication from those exact bytes; never dispatch the same generation again.
                steps = recoveredStepCount;
                finalImageModelStepIndex = recoveredStepCount - 1;
                usage = turnUsage;
            }
            // Replay is scoped to the same durable invocation destination, never to a session's last model.
            ProviderState? continuation = isResume && !recoveredImageCompletion
                ? ReadProviderContinuation(stream, runId, laneId, turnId) : null;
            // These ceilings are per durable Turn, not per user.ask/resume invocation. Replay
            // admissions already written for this Turn before allowing another model/tool step.
            for (var step = nextModelStepIndex; !recoveredImageCompletion && step < EffectiveMaxModelSteps(); step++)
            {
                var modelHooks = step == nextModelStepIndex ? initialModelHooks : DispatchLifecycle(LifecyclePoint.BeforeModel);
                if (modelHooks.Denied || lifecycleDeniedTurn || !ResolvePendingLifecycleValidations())
                    throw new LifecycleHookWithheldException("lifecycle.hook.model_invocation_withheld");
                var requestTools = RequestTools();
                RefreshStepFingerprint(requestTools);
                if (_quotaAdmission?.Invoke(sessionId, runId, laneId, turnId, nextModelStepIndex) is { } quotaInteraction)
                    return new TurnResult(null, StopReason.InputRequired, steps, usage, allToolCalls.ToArray(), null,
                        quotaInteraction);
                steps = step + 1;
                // Snapshot the FIFO at the boundary, never modify an in-flight provider request.
                // Commit application together with ModelStepStarted only after context/budget guards.
                var steering = SteeringQueue.Pending(_store, _codecs, sessionId, runId, laneId, turnId);
                foreach (var item in steering)
                    messages.Add(SteeringMessage(item.InputPartsJson));
                if (step > 0 || steering.Count > 0)
                {
                    preparedContext = MaterializeTurnContext(stream, sessionId, runId, laneId, turnId,
                        workingStateText, instruction, messages, messageOwners, stepFingerprint, cancellationToken, CanInvokeMeta,
                        hasReservationBoundary ? ReserveMeta : null,
                        hasReservationBoundary ? id => _spendReservations!.MarkDispatched(MetaReservationId(id)) : null,
                        hasReservationBoundary ? FinishMeta : null,
                        hasReservationBoundary ? id => _spendReservations!.ReleaseBeforeDispatch(MetaReservationId(id)) : null,
                        lifecycleAnnotations,
                        (point, detail) =>
                        {
                            var result = DispatchLifecycle(point, redactedArguments: detail);
                            if (result.Denied || lifecycleDeniedTurn || result.Validations.Count > 0
                                || requiredLifecycleValidations.Count > 0)
                                throw new LifecycleHookWithheldException("lifecycle.hook.compaction_withheld");
                        });
                    materialized = preparedContext.Snapshot;
                    if (materialized.Overflowed)
                    {
                        AppendTerminalTurn(stream, sessionId, runId, laneId, turnId,
                            new TurnAbandoned(turnId, "ContextOverflow: los resultados de tools exceden el presupuesto"));
                        return new TurnResult("ContextOverflow: el contexto no cabe en el presupuesto del modelo",
                            StopReason.ContextOverflow, steps, usage, allToolCalls.ToArray(), null);
                    }
                }

                var request = new ModelRequest(
                    _selection,
                    _selection.Route?.ReasoningCapability.ReplayPolicy == ReasoningReplayPolicy.None
                        ? preparedContext.Messages.Select(message => new ModelMessage(message.Role,
                            WithoutOpaqueReplay(message.Content))).ToArray()
                        : preparedContext.Messages,
                    RenderContext(materialized),
                    requestTools,
                    dedicatedImageRoute ? ToolChoice.None()
                        : forcedToolName is { } exactTool ? ToolChoice.Exact(exactTool) : ToolChoice.Auto(),
                    null, _selection.Reasoning, new CacheHints(4, "automatic"),
                    // None is an explicit outbound replay prohibition. Keep the checkpoint
                    // and its integrity/usage evidence, but never resend it (including resume).
                    _selection.Route?.ReasoningCapability.ReplayPolicy == ReasoningReplayPolicy.None
                        ? null : continuation);

                ModelResponse resolved;
                string? stepReservation = null;
                string? tokenReservation = null;
                var reservationDispatched = false;
                var tokenReservationDispatched = false;
                IDisposable? ownedPrimary = null;
                int? durableStepIndex = null;
                var providerEntered = false;
                try
                {
                    var budgeted = budget.MaxCostUsd is not null || _enforceDefaultSpendCaps;
                    var stepIndex = nextModelStepIndex++;
                    var primaryIdentity = RunBudgetPool.PrimaryIdentity(sessionId, runId, laneId, turnId, stepIndex);
                    using (RunBudgetPool.For(_store).EnterAdmission(sessionId, runId))
                    {
                        ValidateBeforeInvocation();
                        if (materialized.Items.Any(ExtensionContextData.IsExternal))
                        {
                            if (_externalContextAdmission is null) throw new UnauthorizedAccessException("extension.context.not_authorized");
                            _externalContextAdmission(materialized.Items, sessionId, runId);
                        }
                        tokenReservation = ReserveTokenInvocation(primaryIdentity);
                        stepReservation = ReserveInvocation(primaryIdentity);
                        cancellationToken.ThrowIfCancellationRequested();
                        using (var contextPublication = (_artifacts as IArtifactPublicationLease)
                            ?.AcquirePublicationLease(CancellationToken.None))
                        {
                            if (discoveredPlanChanged)
                            {
                                PublishFingerprintArtifacts(stepFingerprint, stepFingerprintArtifacts);
                                discoveredPlanChanged = false;
                            }
                            preparedContext.PublishArtifacts();
                            var stepStart = new ModelStepStarted(turnId, stepIndex, _selection.Model.ToString(),
                                _selection.ContextBudget, _selection.ToolMode.ToString(),
                                _selection.Reasoning?.Kind, _selection.Reasoning?.BudgetTokens,
                                PersistContextSnapshot(materialized, _selection.ContextBudget), _modelContextCapacity,
                                _selection.RouteId, reasoningResolution);
                            var stepEvents = SteeringQueue.ApplicationEvents(steering, runId, laneId, turnId, stepIndex).ToList();
                            stepEvents.Add(stepStart);
                            stream.AppendBatch(stepEvents, DurabilityClass.Barrier);
                        }
                        durableStepIndex = stepIndex;
                        cancellationToken.ThrowIfCancellationRequested();
                        if (stepReservation is not null)
                        {
                            _spendReservations!.MarkDispatched(stepReservation);
                            reservationDispatched = true;
                        }
                        if (tokenReservation is not null)
                        {
                            _spendReservations!.MarkDispatched(tokenReservation);
                            tokenReservationDispatched = true;
                        }
                        if (stepReservation is not null || tokenReservation is not null)
                            ownedPrimary = RunBudgetPool.For(_store).TrackOwnedPrimary(primaryIdentity);
                    }
                    using var generationAttempts = GenerationRequestAttemptScope.Enter();
                    providerEntered = true;
                    using var generationDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var generationStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    var generationLimitSeconds = EffectiveMaxGenerationSeconds();
                    generationDeadline.CancelAfter(TimeSpan.FromSeconds(generationLimitSeconds));
                    void CancelGenerationDeadline()
                    {
                        try { generationDeadline.Cancel(); }
                        catch (ObjectDisposedException) { }
                    }
                    using var sessionPolicyDeadline = _sessionSecurityPolicy is null ? null
                        : new Timer(_ =>
                        {
                            try
                            {
                                var currentLimit = EffectiveMaxGenerationSeconds();
                                var appliedLimit = Volatile.Read(ref generationLimitSeconds);
                                if (currentLimit < appliedLimit)
                                    Interlocked.Exchange(ref generationLimitSeconds, currentLimit);
                                if (System.Diagnostics.Stopwatch.GetElapsedTime(generationStartedAt)
                                    >= TimeSpan.FromSeconds(Volatile.Read(ref generationLimitSeconds)))
                                    CancelGenerationDeadline();
                            }
                            catch (Exception) { CancelGenerationDeadline(); }
                        }, null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
                    try
                    {
                        resolved = _complete(request, generationDeadline.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                        && generationDeadline.IsCancellationRequested)
                    {
                        throw new GenerationDeadlineExceededException(Volatile.Read(ref generationLimitSeconds));
                    }
                    forcedToolName = null;
                    continuation = resolved.State;
                    if (resolved.ReportedProviderCostUsd is < 0m or > 1_000_000m)
                        throw new InvalidDataException("Provider-reported USD cost is outside the supported range.");
                    var checkpointImageBlocks = resolved.Content.OfType<ImageBlock>().ToArray();
                    if (checkpointImageBlocks.Length > GeneratedImageValidator.MaximumImagesPerResponse
                        || checkpointImageBlocks.Sum(image => (long)image.Bytes.Length) > GeneratedImageValidator.MaximumResponseBytes)
                        throw new InvalidDataException("Generated image response exceeds the aggregate limit.");
                    var stepResponse = VisibleAnswerText(resolved.Content) ?? string.Empty;
                    // The provider's image upper bound is admission exposure, not measured spend.
                    var stepCost = _pricing?.CostUsd(resolved.Usage, resolved.ReportedUsageFields);
                    var recordedCost = resolved.ReportedProviderCostUsd ?? stepCost;
                    var imageCostIncomplete = resolved.Content.OfType<ImageBlock>().Any()
                        && resolved.ReportedProviderCostUsd is null;
                    var completedDay = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture);
                    // Publish both the sensitive continuation and its response root under
                    // one GC boundary through the completed-step FULL commit. The remote
                    // call has already incurred usage, so cancellation cannot skip rooting.
                    using var stepPublication = (_artifacts as IArtifactPublicationLease)
                        ?.AcquirePublicationLease(CancellationToken.None);
                    string? stateDescriptor = null;
                    InvalidDataException? checkpointFailure = null;
                    try
                    {
                        stateDescriptor = ProviderStateCheckpoint.Persist(_artifacts, resolved.State,
                            _selection.Model.ToString(), _selection.RouteId, turnId, stepIndex, _selection.RouteIdentityHash);
                    }
                    catch (InvalidDataException)
                    {
                        // A provider call already consumed usage. Preserve that evidence even when
                        // its continuation cannot be safely checkpointed; no tools run afterward.
                        checkpointFailure = new InvalidDataException("Provider state checkpoint is invalid.");
                    }
                    var stepArtifact = _artifacts.PutText(
                        EncodeUsageResponse(stepResponse, resolved.Usage, recordedCost, runId, completedDay,
                            stateDescriptor, EncodeVisibleContent(resolved.Content), resolved.ReportedProviderCostUsd),
                        "application/vnd.omnicore.model-usage+json", ArtifactKind.ModelResponse,
                        Sensitivity.Sensitive);
                    var completionAfter = stepReservation is not null || tokenReservation is not null
                        ? _store.CurrentSequence(sessionId) : 0;
                    stream.Append(new ModelStepCompleted(turnId, stepIndex, resolved.Usage,
                        resolved.StopReason, stepArtifact, completedDay, recordedCost, resolved.ReportedUsageFields,
                        new GenerationRequestAttemptEvidence(generationAttempts.ObservedSends,
                            _maximumGenerationRequestAttempts), imageCostIncomplete, resolved.ReportedProviderCostUsd), DurabilityClass.Barrier);
                    RecordUsedSessionMemory(sessionId, turnId, stepIndex, materialized.Items, request.Messages, stream);
                    if (TokenUsageValidation.IsInvalid(resolved.Usage, resolved.ReportedUsageFields))
                    {
                        invalidUsageObserved = true;
                        if (budgeted)
                            throw new BudgetExceededException("uso del paso inválido: no se puede hacer cumplir el tope");
                        throw new InvalidDataException("Provider token usage is invalid.");
                    }
                    if (stepReservation is not null)
                    {
                        var completionEvent = stream.EventsSince(completionAfter + 1).Single(evt =>
                            _codecs.Decode(evt) is ModelStepCompleted completed && completed.TurnId == turnId
                                && completed.StepIndex == stepIndex);
                        FinishReservation(stepReservation, recordedCost, completionEvent.EventId.ToString(),
                            generationAttempts.ObservedSends, imageCostIncomplete);
                    }
                    var tokenUsage = resolved.Usage;
                    var tokenFields = resolved.ReportedUsageFields;
                    var tokenReservationStore = _spendReservations;
                    if (tokenReservation is not null && tokenReservationStore is not null
                        && tokenUsage is not null && !TokenUsageValidation.IsInvalid(tokenUsage, tokenFields)
                        && tokenFields.HasFlag(TokenUsageFields.Input | TokenUsageFields.Output)
                        && new GenerationRequestAttemptEvidence(generationAttempts.ObservedSends,
                            _maximumGenerationRequestAttempts).HasCompleteUsageCoverage)
                    {
                        var completionEvent = stream.EventsSince(completionAfter + 1).Single(evt =>
                            _codecs.Decode(evt) is ModelStepCompleted completed && completed.TurnId == turnId
                                && completed.StepIndex == stepIndex);
                        var usedTokens = checked(tokenUsage.Input + tokenUsage.Output);
                        var maximumTokens = ModelInvocationCostBound.TokenCeiling(_selection,
                            _modelContextCapacity, _maximumGenerationRequestAttempts);
                        if (maximumTokens is { } ceiling && usedTokens <= ceiling)
                            tokenReservationStore.Settle(tokenReservation, usedTokens, completionEvent.EventId.ToString());
                        else
                            tokenReservationStore.RecordUncertainCompletion(tokenReservation, usedTokens,
                                completionEvent.EventId.ToString());
                    }
                    ownedPrimary?.Dispose();
                    ownedPrimary = null;
                    // Preserve the individual invocation before attempting aggregate arithmetic.
                    // An unrepresentable total cannot become a wrapped summary or authorize tools.
                    usage = CombineUsage(usage, resolved.Usage);
                    turnUsage = CombineUsage(turnUsage, resolved.Usage);
                    // A provider can durably return generated bytes without reporting token or
                    // image invoice amounts. Admission already held the configured request cap;
                    // do not suppress that completed response after the external effect. The
                    // incomplete receipt remains conservative and blocks later spend checks.
                    var boundedImageResponse = dedicatedImageRoute
                        && _selection.ImageGenerationMaxCostUsd is > 0m
                        && resolved.StopReason == StopReason.EndTurn
                        && resolved.Content.OfType<ImageBlock>().Any();
                    if (!boundedImageResponse) ValidateTokenBudget(includeNextInvocation: false);
                    guard.AdvanceTurn(checked(resolved.Usage.Input + resolved.Usage.Output));
                    if (recordedCost is not null)
                    {
                        try { guard.AddCostUsd(recordedCost.Value); }
                        catch (OverflowException) { throw UnrepresentableSpend(budgeted); }
                    }
                    if (checkpointFailure is not null) throw checkpointFailure;
                    var afterModelHooks = DispatchLifecycle(LifecyclePoint.AfterModel);
                    if (afterModelHooks.Denied || !ResolvePendingLifecycleValidations())
                        throw new LifecycleHookWithheldException("lifecycle.hook.model_result_withheld");
                    ValidateUltraCode();
                    // This invocation is already durable, but unreported usage cannot
                    // authorize tools or another invocation within the same Ask. Checking
                    // only the history loaded at entry would defer this guard until resume.
                    if (budgeted && recordedCost is null && !boundedImageResponse)
                        throw new BudgetExceededException("uso del paso incompleto: no se puede hacer cumplir el tope");
                    if (!boundedImageResponse)
                    {
                        var spend = CurrentSpend();
                        var runCost = AccumulatedSpend(spend.RunUsd, 0m, budgeted);
                        var sessionCost = AccumulatedSpend(spend.SessionUsd, 0m, budgeted);
                        var dailyCost = AccumulatedSpend(spend.DailyUsd, 0m, budgeted);
                        if (budget.MaxCostUsd is not null
                            && runCost > budget.MaxCostUsd.Value)
                            throw new BudgetExceededException("límite de costo de Run ($" + budget.MaxCostUsd.Value + ")",
                                new("run", configuredRunCap!.Value, budget.MaxCostUsd.Value, runId.ToString(), null));
                        if (_enforceDefaultSpendCaps)
                        {
                            ValidateSessionDaily(sessionCost, dailyCost, sessionCap, dailyCap, today);
                        }
                    }
                }
                catch (BudgetExceededException budgetEx)
                {
                    EmitBudgetExceeded(stream, turnId, budgetEx.Detail, budgetEx.Continuation);
                    stop = StopReason.Cancelled;
                    finalText = "Presupuesto agotado: " + budgetEx.Detail;
                    break;
                }
                finally
                {
                    if (durableStepIndex is { } unsentIndex && !providerEntered)
                        stream.Append(new ModelStepNotDispatched(turnId, unsentIndex), DurabilityClass.Barrier);
                    if (stepReservation is not null && !reservationDispatched)
                        _spendReservations!.ReleaseBeforeDispatch(stepReservation);
                    if (tokenReservation is not null && !tokenReservationDispatched)
                        _spendReservations!.ReleaseBeforeDispatch(tokenReservation);
                    ownedPrimary?.Dispose();
                }

                // Cancellation arriving with a completed response must retain its durable
                // usage/state, but cannot publish tool effects or a new approval request.
                cancellationToken.ThrowIfCancellationRequested();
                finalText = VisibleAnswerText(resolved.Content);
                var generatedImageBlocks = resolved.Content.OfType<ImageBlock>().ToArray();
                var toolBlocks = new List<ToolCallBlock>();
                foreach (ContentBlock block in resolved.Content)
                {
                    if (block is ToolCallBlock call)
                    {
                        toolBlocks.Add(new ToolCallBlock(call.Id, call.ProviderCallId, call.ToolName,
                            call.ArgumentsJson));
                    }
                }

                if (resolved.StopReason != StopReason.ToolUse || toolBlocks.Count == 0)
                {
                    var completionHooks = DispatchLifecycle(LifecyclePoint.BeforeCompletion);
                    if (completionHooks.Denied || !ResolvePendingLifecycleValidations())
                    {
                        stop = StopReason.Error;
                        finalText = "lifecycle.hook.completion_withheld";
                        break;
                    }
                    finalImages.Clear();
                    finalResponseParts.Clear();
                    foreach (var image in generatedImageBlocks)
                        finalImages.Add(GeneratedImageValidator.Decode(image.MediaType, image.Bytes.ToArray()));
                    if (finalImages.Count > 0) finalImageModelStepIndex = step;
                    var imageIndex = 0;
                    foreach (var block in resolved.Content)
                    {
                        if (block is TextBlock text && text.Text.Length > 0)
                            finalResponseParts.Add(("text", text.Text, -1));
                        else if (block is ImageBlock)
                            finalResponseParts.Add(("image", null, imageIndex++));
                    }
                    stop = resolved.StopReason;
                    break;
                }

                // Una respuesta ToolUse conserva sus bloques y orden en un solo mensaje.
                // Se mantiene la redacción vigente; el replay opaco requiere una política aparte.
                messages.Add(PreserveMessage(new ModelMessage(MessageRole.Assistant, resolved.Content)));

                var pendingDiscoveredToolIds = new List<string>();
                var pendingDiscoveredToolIdSet = new HashSet<string>(StringComparer.Ordinal);
                foreach (ToolCallBlock call in toolBlocks)
                {
                    if (lifecycleDeniedTurn)
                        throw new LifecycleHookWithheldException("lifecycle.hook.tool_outcome_withheld");
                    try
                    {
                        ValidateUltraCode(toolCall: call.Id);
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
                    var exposed = ReadTurnToolCallCount(stream, turnId) < EffectiveMaxToolCalls()
                        && !lifecycleDeniedToolIds.Contains(call.ToolName)
                        && VisibleTools(discoveredToolIds, discoveredToolOrder).Any(tool => tool.Name == call.ToolName);
                    var redactedArguments = SafeToolArguments(call.ToolName, call.ArgumentsJson);
                    var hookDecision = exposed
                        ? DispatchLifecycle(LifecyclePoint.BeforeTool, call.ToolName, redactedArguments)
                        : HookDispatchResult.Empty;
                    var permissionDecision = exposed
                        ? DispatchLifecycle(LifecyclePoint.BeforePermission, call.ToolName, redactedArguments)
                        : HookDispatchResult.Empty;
                    ToolCallRequestReceipt? requestReceipt = null;
                    // user.ask has no external effect. Keep its schema in CAS and append its
                    // sanitized request + tool outcome + InteractionRequested atomically only
                    // after the Host has prepared the redacted schema artifact.
                    var deferQuestionRequest = exposed && call.ToolName == "user.ask"
                        && _tools is ScriptedToolExecutor;
                    if (!deferQuestionRequest && (!exposed || _tools is ScriptedToolExecutor))
                    {
                        var persistedArguments = call.ToolName == "user.ask"
                            ? QuestionnaireArgumentDigest(call.ArgumentsJson)
                            : call.ToolName == "memory.propose" ? MemoryProposalJournalArgumentsJson : call.ArgumentsJson;
                        var requested = new ToolCallRequested(call.Id, validated.ProviderCallId,
                            call.ToolName, persistedArguments);
                        requestReceipt = stream.AppendToolCallRequestAdmitted(requested, journal =>
                        {
                            ValidateCounterBudget(journal, newTurn: false, newToolCall: call.Id);
                            ValidateUltraCode(toolCall: call.Id);
                        }, call.ToolName == "memory.propose" ? call.ArgumentsJson : null);
                    }
                    ToolOutcome outcome;
                    if (!exposed)
                        outcome = ToolOutcome.Failed("tool no disponible para este modelo", null,
                            ToolCallState.Rejected, new DomainEventPayload[] {
                                // Para el modelo la tool no existe: la frontera de capacidad no
                                // la expone, así que el rechazo se tipa UNKNOWN_TOOL (spec §71).
                                new ToolCallRejected(call.Id, "tool no disponible para este modelo",
                                    ToolErrorCode.UnknownTool)
                            });
                    else if (hookDecision.Denied || permissionDecision.Denied
                        || !ResolvePendingLifecycleValidations() || lifecycleDeniedToolIds.Contains(call.ToolName))
                        outcome = ToolOutcome.Failed("lifecycle.hook.tool_restricted", null,
                            ToolCallState.Rejected, new DomainEventPayload[] {
                                new ToolCallRejected(call.Id, "lifecycle.hook.tool_restricted",
                                    ToolErrorCode.PermissionDenied)
                            });
                    else if (_tools is ScriptedToolExecutor scripted)
                    {
                        var substitution = _protectedPreferenceSubstitutions.GetValueOrDefault(call.ToolName);
                        var mcpApproval = _catalog.Find(new ToolId(call.ToolName)) switch
                        {
                            McpTool mcp => mcp.FirstEffectApproval,
                            McpWebTool web => web.FirstEffectApproval,
                            _ => null,
                        };
                        var requiredApproval = substitution ?? mcpApproval;
                        var substitutionAsk = requiredApproval is not null
                            && !_approvedProtectedPreferenceIdentities.Contains(ApprovalKey(requiredApproval));
                        outcome = requestReceipt is not null
                            ? scripted.ExecuteTool(validated, false, cancellationToken, stream,
                                requestReceipt, substitutionAsk ? requiredApproval : null)
                            : scripted.ExecuteTool(validated, false, cancellationToken, stream,
                                substitutionAsk ? requiredApproval : null);
                        foreach (var approval in outcome.Events.OfType<ToolPreferenceSubstitutionApproved>())
                            _approvedProtectedPreferenceIdentities.Add(ApprovalKey(approval.SourceToolId,
                                approval.TargetToolId, approval.IdentityHash));
                    }
                    else outcome = _tools.ExecuteTool(validated, false, cancellationToken, stream);
                    if (!outcome.Succeeded)
                        DispatchLifecycle(LifecyclePoint.ToolFailed, call.ToolName, redactedArguments);
                    DispatchLifecycle(LifecyclePoint.AfterTool, call.ToolName, redactedArguments);

                    // Discovery affects only the next request. Every selected tool still has
                    // to pass the ToolRuntime schema, boundary, PermissionPolicy and Execute path.
                    if (call.ToolName == "tool.search" && outcome.Succeeded && outcome.Preview is not null
                        && _selection.ToolMode == ToolMode.Discovered)
                    {
                        try
                        {
                            using var discovery = JsonDocument.Parse(outcome.Preview);
                            if (discovery.RootElement.TryGetProperty("matches", out var matches)
                                && matches.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var match in matches.EnumerateArray())
                                {
                                    if (!match.TryGetProperty("id", out var id)
                                        || id.ValueKind != JsonValueKind.String) continue;
                                    var toolId = id.GetString();
                                    if (string.IsNullOrWhiteSpace(toolId)) continue;
                                    var tool = _catalog.Find(new ToolId(toolId));
                                    if (tool is not null && tool.Descriptor.Source.Trust != TrustLevel.Untrusted
                                        && (_boundary is null || _boundary.IsToolVisible(toolId)))
                                        if (pendingDiscoveredToolIdSet.Add(toolId)) pendingDiscoveredToolIds.Add(toolId);
                                }
                            }
                        }
                        catch (JsonException)
                        {
                            // A malformed discovery result adds no tools; the completed call
                            // remains in the journal and the ordinary tool plan stays intact.
                        }
                    }

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
                        else if (call.ToolName == "memory.propose" && evt is ToolCallRequested memoryRequested)
                        {
                            // The proposal may contain sensitive text. Its screened candidate
                            // event is the durable source; never persist raw tool arguments.
                            toPersist.Add(new ToolCallRequested(memoryRequested.ToolCallId, memoryRequested.ProviderCallId,
                                memoryRequested.ToolName, MemoryProposalJournalArgumentsJson));
                        }
                        else if (call.ToolName == "memory.propose" && evt is ToolCallPrepared memoryPrepared)
                        {
                            toPersist.Add(new ToolCallPrepared(memoryPrepared.ToolCallId,
                                MemoryProposalJournalArgumentsJson));
                        }
                        else
                        {
                            toPersist.Add(evt);
                        }
                    }

                    toPersist.AddRange(planEvents);
                    if (call.ToolName == "mode.propose" && outcome.Succeeded
                        && ModeProposeTool.TryParse(call.ArgumentsJson, out var proposedMode, out var modeReason))
                    {
                        var authority = RunProjection.Replay(sessionId, runId, _codecs,
                            _store.ReadFrom(sessionId, 1)).ModeAuthority!;
                        toPersist.Add(new RunModeProposed(runId, turnId, call.Id, authority.Mode,
                            proposedMode, _redaction.Redact(modeReason), authority.Revision,
                            authority.ObjectiveRevision, authority.ObjectiveDigest, authority.PolicyRevision));
                    }
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
                    if (deferQuestionRequest)
                        stream.AppendBatchAdmitted(toPersist, DurabilityClass.Standard, journal =>
                        {
                            ValidateCounterBudget(journal, newTurn: false, newToolCall: call.Id);
                            ValidateUltraCode(toolCall: call.Id);
                        });
                    else
                    stream.AppendBatch(toPersist, DurabilityClass.Standard);
                    if (outcome.Events.OfType<ToolCallStarted>().Any(evt => evt.EffectClass != EffectClass.None))
                        WorkspaceCheckpointRecorder?.Invoke(sessionId, runId, laneId, _store.CurrentSequence(sessionId), "after-tool");

                    if (!ResolvePendingLifecycleValidations())
                        lifecycleDeniedTurn = true;

                    if (call.ToolName == "process.exec" && outcome.Succeeded && _sessionMemoryPolicy is not null)
                    {
                        try { _sessionMemoryPolicy.ObserveSuccessfulDotnetValidation(call.Id.ToString(), call.ArgumentsJson); }
                        catch (Exception exception) when (exception is IOException or InvalidDataException
                            or UnauthorizedAccessException or ArgumentException) { }
                    }

                    if (lifecycleDeniedTurn)
                    {
                        var completed = outcome.Succeeded && planError is null;
                        allToolCalls.Add(new ToolUseTrace(call.ToolName, completed,
                            _redaction.Redact(planError ?? outcome.Summary ?? ""),
                            SafeToolArguments(call.ToolName, call.ArgumentsJson)));
                        throw new LifecycleHookWithheldException("lifecycle.hook.tool_outcome_withheld");
                    }

                    if (call.ToolName == "core.agents.mailbox.receive")
                        _mailboxDeliveryCompleted?.Invoke(call.Id);

                    if (pendingPermission is { } permissionInteractionId)
                    {
                        allToolCalls.Add(new ToolUseTrace(call.ToolName, true, "awaiting input",
                            SafeToolArguments(call.ToolName, call.ArgumentsJson)));
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
                                SafeToolArguments(call.ToolName, call.ArgumentsJson)));
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
                                    SafeToolArguments(call.ToolName, call.ArgumentsJson)));
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
                            answer.Status, SafeToolArguments(call.ToolName, call.ArgumentsJson)));
                        continue;
                    }

                    // The authorized tool result is conversation data; preserve its original text.
                    var content = outcome.Preview is not null && outcome.Preview!.Length > 0
                        ? outcome.Preview!
                        : (outcome.Summary ?? "ok");
                    var succeeded = outcome.Succeeded && planError is null;
                    var resultText = succeeded ? content : "error: " + _redaction.Redact(planError ?? outcome.Summary ?? "failed");
                    allToolCalls.Add(new ToolUseTrace(call.ToolName, succeeded,
                        _redaction.Redact(planError ?? outcome.Summary ?? ""),
                        SafeToolArguments(call.ToolName, call.ArgumentsJson)));

                    var resultBlocks = new List<ContentBlock> { new TextBlock(resultText) };
                    if (succeeded && outcome.Artifact is { } resultArtifact
                        && resultArtifact.MediaType.StartsWith("image/", StringComparison.Ordinal))
                    {
                        if (!_selection.InputModalities.Contains("image", StringComparer.Ordinal))
                            throw new NotSupportedException("The effective model/provider route does not support image tool results.");
                        resultBlocks.Add(AttachmentContentMaterializer.Read(resultArtifact, _artifacts));
                    }
                    var toolResult = new ModelMessage(MessageRole.Tool, new ContentBlock[] {
                        new ToolResultBlock(call.Id, resultBlocks, !succeeded),
                    });
                    messages.Add(toolResult);
                }

                // Apply catalog selections only after this entire model response has been handled.
                // A tool discovered by one call cannot authorize a sibling call from the same response.
                var addedDiscoveredTool = false;
                var newlyDiscoveredInOrder = new List<string>();
                foreach (var toolId in pendingDiscoveredToolIds)
                {
                    var wasKnown = !discoveredToolIds.Add(toolId);
                    var oldIndex = discoveredToolOrder.IndexOf(toolId);
                    if (wasKnown && oldIndex == 0) continue;
                    if (oldIndex >= 0) discoveredToolOrder.RemoveAt(oldIndex);
                    newlyDiscoveredInOrder.Add(toolId);
                    addedDiscoveredTool = true;
                }
                if (newlyDiscoveredInOrder.Count > 0)
                    discoveredToolOrder.InsertRange(0, newlyDiscoveredInOrder);
                if (addedDiscoveredTool)
                {
                    var replayedPlan = PlanProjection.Replay(_codecs, initialPlanEvents).Latest();
                    var preparedStepFingerprint = RuntimeFingerprintFactory.PrepareTurnConfiguration(_fingerprint,
                        _catalog, VisibleTools(discoveredToolIds, discoveredToolOrder), EffectiveSystemPrompt(instruction), replayedPlan,
                        _artifacts, laneProfileId, _activeSkills, _resolvedAgentProfile, fingerprintAuthority,
                instructionSnapshot, reasoningResolution, _toolPreferences);
                    stepFingerprint = preparedStepFingerprint.Fingerprint;
                    stepFingerprintArtifacts = preparedStepFingerprint.Artifacts;
                    discoveredPlanChanged = true;
                }

                if (stop == StopReason.Cancelled)
                {
                    break;
                }
            }

            if (finalText is null && finalImages.Count == 0 && stop == StopReason.EndTurn)
            {
                AppendTerminalTurn(stream, sessionId, runId, laneId, turnId,
                    new TurnAbandoned(turnId, "Se agotó el límite de pasos del Explorer"));
                return new TurnResult(null, StopReason.Error, steps, usage, allToolCalls.ToArray(), null);
            }

            // Respuesta como artifact + ModelCompleted (persistencia del Turn).
            string? artifactId = recoveredResponseArtifactId;
            if (finalText is not null && finalText.Length > 0 || finalImages.Count > 0)
            {
                // Keep both final CAS objects protected until their canonical references
                // commit together. No provider work runs while this publication lease is held.
                if (!finalResponseAlreadyPublished)
                {
                    using var finalPublication = (_artifacts as IArtifactPublicationLease)
                        ?.AcquirePublicationLease(CancellationToken.None);
                    var safeResponse = finalText ?? "";
                    var responseEvents = new List<DomainEventPayload>();
                    if (!invalidUsageObserved)
                    {
                        var cost = ReadTurnModelStepCost(stream, turnId);
                        var journalRecord = EncodeUsageResponse(safeResponse, turnUsage, cost, runId, today);
                        var artifact = _artifacts.PutText(journalRecord, "application/vnd.omnicore.model-usage+json",
                            ArtifactKind.ModelResponse, Sensitivity.Sensitive);
                        artifactId = artifact.Hash.ToString();
                        responseEvents.Add(new ModelCompleted(turnId, artifact));
                    }
                    if (safeResponse.Length > 0)
                    {
                        var conversation = _artifacts.PutText(safeResponse, "text/markdown",
                            ArtifactKind.ModelResponse, Sensitivity.Sensitive);
                        responseEvents.Add(new AssistantMessageRecorded(runId, laneId, turnId, conversation));
                    }
                    if (finalImages.Count > 0)
                    {
                        if (_artifacts is not IBinaryArtifactStore binaryArtifacts)
                            throw new InvalidOperationException("Binary artifact storage is unavailable for generated images.");
                        var imageOutputs = new List<AssistantImageOutput>(finalImages.Count);
                        for (var index = 0; index < finalImages.Count; index++)
                        {
                            var image = finalImages[index];
                            var reference = binaryArtifacts.PutBytes(image.Bytes, image.MediaType,
                                ArtifactKind.ModelResponse, Sensitivity.Sensitive);
                            imageOutputs.Add(new AssistantImageOutput(reference, image.Width, image.Height,
                                index, _selection.Route?.ProviderId ?? "unknown", _selection.Model.ToString(),
                                finalImageModelStepIndex));
                        }
                        var orderedParts = new List<AssistantResponsePart>(finalResponseParts.Count);
                        for (var index = 0; index < finalResponseParts.Count; index++)
                        {
                            var part = finalResponseParts[index];
                            var artifact = part.Kind == "image"
                                ? imageOutputs[part.ImageIndex].Artifact
                                : _artifacts.PutText(part.Text ?? "", "text/markdown",
                                    ArtifactKind.ModelResponse, Sensitivity.Sensitive);
                            orderedParts.Add(new AssistantResponsePart(part.Kind, artifact, index));
                        }
                        responseEvents.Add(new AssistantImagesRecorded(runId, laneId, turnId, imageOutputs, orderedParts));
                    }
                    stream.AppendBatch(responseEvents, DurabilityClass.Standard);
                }
            }

            AppendTerminalTurn(stream, sessionId, runId, laneId, turnId, new TurnCompleted(turnId));
            AuditSpend(sessionId, runId, laneId, turnId, stop, turnUsage,
                ReadTurnModelStepCost(stream, turnId), usageAvailable: !invalidUsageObserved);
            AuditPolicy(sessionId, runId, turnId, stop);
            return new TurnResult(finalText, stop, steps,
                turnUsage, allToolCalls.ToArray(), artifactId);
        }
        catch (Exception ex)
        {
            try
            {
                // Started → … → Abandoned (terminal); NUNCA Completed tras Abandoned (state machine).
                if (started)
                    AppendTerminalTurn(stream, sessionId, runId, laneId, turnId, ex is OperationCanceledException
                        ? new TurnInterrupted(turnId)
                        : new TurnAbandoned(turnId, "turn falló: " + _redaction.Redact(ex.Message ?? "")));
            }
            catch (Exception)
            {
            }

            return new TurnResult(ex is LifecycleHookWithheldException ? ex.Message : null,
                ex is OperationCanceledException ? StopReason.Cancelled : StopReason.Error,
                steps, usage, allToolCalls.ToArray(), null)
            {
                FailureDiagnostic = ex is LifecycleHookWithheldException hookWithheld
                    ? hookWithheld.Message
                    : ex is GenerationDeadlineExceededException deadline
                    ? "OmniCore cancelled generation after " + deadline.Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " seconds. The provider may continue work if it does not honor cancellation; reasoning is provider-controlled."
                    : _redaction.Redact(ex.GetType().Name + ": " + (ex.Message ?? "turn failed")),
            };
        }
    }

    private sealed class LifecycleHookWithheldException(string message) : InvalidOperationException(message) { }

    /// <summary>Dispatches a lifecycle point owned by the host's Run-validation transition.
    /// The current completed Turn provides attribution; it does not manufacture a new Turn.</summary>
    internal HookDispatchResult DispatchCompletionLifecycle(SessionId sessionId, RunId runId,
        TurnId turnId, LifecyclePoint point, string? boundedRedactedContext,
        CancellationToken cancellationToken = default)
    {
        if (point is not (LifecyclePoint.BeforeValidation or LifecyclePoint.AfterValidation
            or LifecyclePoint.RunCompleted or LifecyclePoint.RunFailed))
            throw new ArgumentOutOfRangeException(nameof(point));
        if (_lifecycleHooks.Count == 0) return HookDispatchResult.Empty;
        var detail = boundedRedactedContext is null ? null : _redaction.Redact(
            boundedRedactedContext.Length <= 12_000 ? boundedRedactedContext : boundedRedactedContext[..12_000]);
        var result = _hookDispatcher.DispatchAsync(_lifecycleHooks,
            new HookContext(point, RedactedArgumentsJson: detail, EventType: point.ToString()),
            cancellationToken).AsTask().GetAwaiter().GetResult();
        var stream = new EventStream(_store, _codecs, sessionId);
        foreach (var applied in result.Applications)
            stream.Append(new HookApplied(runId, turnId, applied.HookId, point.ToString(), applied.Result,
                applied.DetailIds), DurabilityClass.Barrier);
        try
        {
            _audit.Record(new AuditRecord("hook.dispatch", null, sessionId, runId, DateTimeOffset.UtcNow,
                runId.ToString(), new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["point"] = point.ToString(), ["denied"] = result.Denied.ToString(),
                    ["requiredValidationCount"] = result.Validations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["warningCodes"] = string.Join(",", result.Warnings.Select(warning => warning.Split(':')[0])
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                }), CancellationToken.None);
        }
        catch (Exception) { /* audit observers cannot change the hook result */ }
        return result;
    }

    private sealed class GenerationDeadlineExceededException(int seconds)
        : OperationCanceledException("Configured generation deadline elapsed.")
    {
        public int Seconds { get; } = seconds;
    }

    /// <summary>
    /// Watchdog al cerrar un Turn (ADR-0048 §1–§3). Si el item actual de una Lane vigilada lleva el
    /// umbral de <c>HarnessPolicy.StallThresholdTurns</c> sin señal de progreso, registra en un solo
    /// batch <c>ProgressStalled</c>, la respuesta determinista y, para <c>AskUser</c>, la interacción
    /// <c>StallResolution</c> con el Run en <c>AwaitingInput</c>. Un fallo del watchdog no deshace
    /// el Turn: se informa y el siguiente Turn vuelve a evaluar el mismo episodio.
    /// </summary>
    private StallOutcome? ApplyStallWatchdog(SessionId sessionId, RunId runId, LaneId laneId, TurnId turnId)
    {
        try
        {
            var stream = new EventStream(_store, _codecs, sessionId);
            var events = stream.EventsSince(1);
            if (!IsTerminalTurn(events, turnId)) return null; // suspendido: se evalúa cuando termine
            var threshold = _harness?.StallThresholdTurns ?? ProgressReconciler.DefaultStallThresholdTurns;
            if (StallWatch.Observe(_stallDecoder, events, runId, threshold) is not { } stalled
                || !stalled.Lanes.Contains(laneId))
                return null;
            var own = StallWatch.RunEvents(_stallDecoder, events, runId);
            var run = RunProjection.Replay(sessionId, runId, _stallDecoder, own);
            if (run.IsTerminal()) return null;
            var isRoot = run.RootTask is { } root
                && LaneProjection.Replay(_stallDecoder, own).ForTask(root).Any(lane => lane.Id == laneId);
            var askUnavailable = !isRoot ? "DelegatedLane" : run.State != RunState.Running ? "RunNotRunning" : null;
            var escalateUnavailable = !isRoot ? "DelegatedLane"
                : _stallEscalationUnavailable is null ? "NoEscalationHost" : _stallEscalationUnavailable();
            var decision = StallPolicyEvaluator.Select(stalled.LastPolicy,
                new StallAvailability(escalateUnavailable, askUnavailable));

            var task = own.Where(evt => evt.Type.ToString() == "lane.created").Select(_stallDecoder.Decode)
                .OfType<LaneCreated>().FirstOrDefault(created => created.LaneId == laneId)?.TaskId;
            var batch = new List<DomainEventPayload>
            {
                new ProgressStalled(stalled.PlanItemId, stalled.TurnsWithoutProgress, stalled.LastProgressAt),
            };
            InteractionId? interaction = null;
            if (decision.Policy is { } policy)
            {
                batch.Add(new StallResponseSelected(runId, stalled.PlanItemId, policy, stalled.Step, decision.Skipped));
                if (policy == StallPolicy.AskUser)
                {
                    var requested = InteractionId.New();
                    interaction = requested;
                    // ADR-0034: opciones decididas por el servidor; sin respuesta, se detiene.
                    batch.Add(new InteractionRequested(requested, InteractionKind.StallResolution,
                        "{" + JsonObj.Field("operation", "stall.resolution") + ","
                            + JsonObj.Field("target", _redaction.Redact(stalled.ItemDescription)) + ","
                            + JsonObj.Field("detail", stalled.TurnsWithoutProgress.ToString(
                                System.Globalization.CultureInfo.InvariantCulture) + " turns") + ","
                            + JsonObj.Field("reason", "stall.resolution.reason") + "}",
                        "[{\"id\":\"continue\",\"intent\":\"allow\"},{\"id\":\"stop\",\"intent\":\"deny\"}]",
                        "stop", null, laneId, task, stalled.PlanItemId, 0, 1));
                    batch.Add(new RunAwaitingInput(runId, laneId));
                }
            }

            using (ExecutionScope.Begin(new ExecutionScopeState(runId, task, laneId, turnId)))
                stream.AppendBatch(batch, DurabilityClass.Standard);
            return new StallOutcome(stalled.PlanItemId, stalled.ItemDescription, stalled.TurnsWithoutProgress,
                decision.Policy, decision.Skipped, interaction, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return StallOutcome.Failed(_redaction.Redact(ex.GetType().Name + ": " + (ex.Message ?? "stall watchdog failed")));
        }
    }

    private bool IsTerminalTurn(IReadOnlyList<DomainEvent> events, TurnId turnId) =>
        events.Where(evt => evt.Type.ToString() is "turn.completed" or "turn.interrupted" or "turn.abandoned")
            .Select(_stallDecoder.Decode).Any(payload => payload switch
        {
            TurnCompleted completed => completed.TurnId == turnId,
            TurnInterrupted interrupted => interrupted.TurnId == turnId,
            TurnAbandoned abandoned => abandoned.TurnId == turnId,
            _ => false,
        });

    private void AppendTerminalTurn(EventStream stream, SessionId session, RunId run, LaneId lane,
        TurnId turn, DomainEventPayload terminal)
    {
        // Suspension deliberately does not call this. Terminal state and explicit drops share
        // one durable batch so a crash cannot leave a committed terminal Turn without its drops.
        var pending = SteeringQueue.Pending(_store, _codecs, session, run, lane, turn);
        var batch = SteeringQueue.DropEvents(pending, run, lane, turn,
            "turn ended without another model step").ToList();
        batch.Add(terminal);
        stream.AppendBatch(batch, DurabilityClass.Barrier);
        WorkspaceCheckpointRecorder?.Invoke(session, run, lane, _store.CurrentSequence(session), "after-turn");
    }

    private ModelMessage SteeringMessage(string partsJson)
    {
        using var parsed = System.Text.Json.JsonDocument.Parse(partsJson);
        var parts = parsed.RootElement.EnumerateArray().Select(part => part.GetString() ?? "");
        return new ModelMessage(MessageRole.User,
            new ContentBlock[] { new TextBlock(string.Join("\n", parts)) });
    }

    /// <summary>
    /// Registra el gasto del turno en el AUDIT (ADR-0043, INV-012): detalles redactados,
    /// sin secretos. Persiste el gasto real del turno para los reportes y la capa de
    /// presupuesto por sesión/día (P1: el audit sink del turno se usa).
    /// </summary>
    private void AuditSpend(SessionId sessionId, RunId runId, LaneId laneId, TurnId turnId,
        StopReason stop, TokenUsage usage, decimal? confirmedCostUsd, bool usageAvailable = true)
    {
        try
        {
            var details = new Dictionary<string, string>();
            details["stop"] = stop.ToString();
            if (usageAvailable)
            {
                details["inputTokens"] = usage.Input.ToString();
                details["outputTokens"] = usage.Output.ToString();
            }
            else details["usageStatus"] = "invalid";
            if (usageAvailable && confirmedCostUsd is not null)
                details["costUsd"] = confirmedCostUsd.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
    private bool HasHistoricalMonetaryEvidence(IEnumerable<DomainEvent> events)
    {
        var summaries = new HashSet<TurnId>();
        var steps = new HashSet<TurnId>();
        foreach (var evt in events)
        {
            if (evt.Type.ToString() is not ("model.completed" or "model_step.completed"
                or "meta_model.invocation_completed" or "meta_model.invocation_failed")) continue;
            try
            {
                switch (_codecs.Decode(evt))
                {
                    case ModelCompleted summary: summaries.Add(summary.TurnId); break;
                    case ModelStepCompleted step:
                        if (step.CostUsd is not null) return true;
                        steps.Add(step.TurnId);
                        break;
                    case MetaModelInvocationCompleted meta when meta.CostUsd is null: break;
                    case MetaModelInvocationFailed meta when meta.CostUsd is null: break;
                    default: return true;
                }
            }
            catch (Exception) { return true; } // Unreadable evidence must take the validation path.
        }
        // Legacy summaries keep cost inside CAS, not in the event. Never assume it absent.
        return summaries.Any(turn => !steps.Contains(turn));
    }

    private PersistedSpend ReadJournalSpend(EventStream stream, SessionId sessionId, RunId runId, string today,
        bool includeWorkspaceDaily, IReadOnlyList<DomainEvent>? evidenceEvents = null,
        IArtifactStore? evidenceArtifacts = null)
    {
        var artifacts = evidenceArtifacts ?? _artifacts;
        var incomplete = false;
        IReadOnlyList<DomainEvent>? stepStarts = evidenceEvents?.Where(evt => evt.Type.ToString() == "model_step.started").ToArray();
        IReadOnlyList<DomainEvent>? stepCompletions = evidenceEvents?.Where(evt => evt.Type.ToString() == "model_step.completed").ToArray();
        IReadOnlyList<DomainEvent>? unsentSteps = evidenceEvents?.Where(evt => evt.Type.ToString() == "model_step.not_dispatched").ToArray();
        IReadOnlyList<DomainEvent>? completions = evidenceEvents?.Where(evt => evt.Type.ToString() == "model.completed").ToArray();
        IReadOnlyList<DomainEvent>? externalLaneReceipts = evidenceEvents?
            .Where(evt => evt.Type.ToString() == "external_lane.usage_recorded").ToArray();
        if (evidenceEvents is null && includeWorkspaceDaily && _store is IWorkspaceJournalReader store)
        {
            try
            {
                stepStarts = store.ReadEvents(EventType.Of("model_step.started"));
                stepCompletions = store.ReadEvents(EventType.Of("model_step.completed"));
                unsentSteps = store.ReadEvents(EventType.Of("model_step.not_dispatched"));
                completions = store.ReadEvents(EventType.Of("model.completed"));
                externalLaneReceipts = store.ReadEvents(EventType.Of("external_lane.usage_recorded"));
            }
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
            stepStarts = stream.EventsSince(1)
                .Where(evt => evt.Type.ToString().Equals("model_step.started", StringComparison.Ordinal)).ToArray();
            stepCompletions = stream.EventsSince(1)
                .Where(evt => evt.Type.ToString().Equals("model_step.completed", StringComparison.Ordinal)).ToArray();
            unsentSteps = stream.EventsSince(1)
                .Where(evt => evt.Type.ToString() == "model_step.not_dispatched").ToArray();
            externalLaneReceipts = stream.EventsSince(1)
                .Where(evt => evt.Type.ToString() == "external_lane.usage_recorded").ToArray();
        }

        var snapshot = new CanonicalSpendReader(_codecs, artifacts).ReadPrimary(
            (stepStarts ?? []).Concat(stepCompletions ?? []).Concat(unsentSteps ?? []).Concat(completions)
                .Concat(externalLaneReceipts ?? []),
            sessionId, runId, today,
            _spendReservations is not null
                ? (evt, step) => RunBudgetPool.For(_store).OwnsPendingPrimary(evt, step, _spendReservations)
                : null);
        return snapshot with { Incomplete = snapshot.Incomplete || incomplete };
    }

    private PersistedSpend ReadOtherWorkspaceSpend(EventStream stream, SessionId sessionId, RunId runId, string today)
    {
        if (!_enforceDailySpendCap || _userSpendReader is null) return new(0m, 0m, 0m, false);
        decimal? daily = 0m;
        var incomplete = false;
        try
        {
            foreach (var evidence in _userSpendReader.ReadOtherWorkspaces())
            {
                var primary = ReadJournalSpend(stream, sessionId, runId, today, true, evidence.Events, evidence.Artifacts);
                var meta = ReadMetaSpend(evidence.Events, sessionId, runId, today, evidence.Artifacts);
                var combined = CombineSpend(primary, meta);
                daily = combined.DailyUsd is { } value ? AddHistoricalSpend(daily, value) : null;
                incomplete |= combined.Incomplete;
            }
            // One User namespace, not once per workspace and never charged to Session/Run.
            var receipts = SqliteModelQualificationStore.ReadCanonicalProbeReceipts(
                _userSpendReader.UserDataDirectory, CancellationToken.None);
            if (_spendReservations is null && receipts.Any(r => r.CostUsd is null
                && r.BillingMode is BillingMode.MeteredCurrency or BillingMode.CreditBalance or BillingMode.Unknown))
                return new(0m, null, 0m, true);
            if (_spendReservations is not null)
                daily = AddHistoricalSpend(daily, QualificationSpendAccounting.Daily(receipts, today, _spendReservations));
            else
                daily = AddHistoricalSpend(daily, receipts.Where(r => r.CompletedAtUtc.UtcDateTime.ToString("yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture) == today
                    && r.BillingMode is BillingMode.MeteredCurrency or BillingMode.CreditBalance or BillingMode.Unknown)
                    .Aggregate(0m, (sum, r) => checked(sum + r.CostUsd!.Value)));
            // Session and Run budgets remain scoped to their own workspace, even if IDs were copied.
            return new(0m, daily, 0m, incomplete || daily is null);
        }
        catch (Exception) { return new(0m, null, 0m, true); }
    }

    private PersistedSpend ReadMetaJournalSpend(EventStream stream, SessionId sessionId, RunId runId,
        string today, bool includeWorkspaceDaily)
    {
        IEnumerable<DomainEvent> metaEvents;
        try
        {
            metaEvents = includeWorkspaceDaily && _store is IWorkspaceJournalReader reader
                ? new[] { "meta_model.invocation_started", "meta_model.invocation_completed", "meta_model.invocation_failed",
                    "meta_model.invocation_not_dispatched" }
                    .SelectMany(type => reader.ReadEvents(EventType.Of(type))).ToArray()
                : stream.EventsSince(1).Where(e => e.Type.ToString().StartsWith("meta_model.invocation_", StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return new PersistedSpend(null, null, null, true);
        }
        return ReadMetaSpend(metaEvents, sessionId, runId, today);
    }

    // Preserve all answer text exactly, including separators supplied by the provider. Reasoning,
    // citations and tools are independent blocks, not part of the visible answer or its summary.
    private string? VisibleAnswerText(IReadOnlyList<ContentBlock> content)
    {
        var blocks = content.OfType<TextBlock>().ToArray();
        return blocks.Length == 0 ? null : string.Concat(blocks.Select(block => block.Text));
    }

    private static string QuestionnaireArgumentDigest(string argumentsJson)
    {
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(argumentsJson)));
        return "{\"argumentsHash\":\"" + digest + "\"}";
    }

    internal static string EncodeUsageResponse(string response, TokenUsage usage, decimal? cost,
        RunId runId, string day, string? providerStateDescriptor = null, string? visibleContent = null,
        decimal? reportedProviderCostUsd = null)
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
            + ",\"costUsd\":" + costJson
            + ",\"reportedProviderCostUsd\":" + (reportedProviderCostUsd is null ? "null" : "\""
                + reportedProviderCostUsd.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"")
            + ",\"providerState\":" + (providerStateDescriptor ?? "null")
            + (visibleContent is null ? "" : ",\"visibleContent\":" + visibleContent) + "}";
    }

    private string? EncodeVisibleContent(IReadOnlyList<ContentBlock> content)
    {
        // Text fragments are not separate security boundaries: a secret may span adjacent
        // fragments. Redact the complete contiguous text while preserving reasoning/tool order.
        // This is only the visible projection; opaque ProviderState is never normalized here.
        var blocks = new List<ContentBlock>();
        System.Text.StringBuilder? adjacentText = null;
        void FlushText()
        {
            if (adjacentText is null) return;
            blocks.Add(new TextBlock(adjacentText.ToString()));
            adjacentText = null;
        }
        foreach (var block in content)
        {
            if (block is TextBlock text)
            {
                (adjacentText ??= new System.Text.StringBuilder()).Append(text.Text);
                continue;
            }
            FlushText();
            if (block is ReasoningBlock or ToolCallBlock or ImageBlock) blocks.Add(block);
        }
        FlushText();
        if (blocks.Count == 0) return null;
        using var buffer = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("routeIdentityHash", _selection.RouteIdentityHash);
            writer.WriteStartArray("blocks");
            foreach (var block in blocks)
            {
                writer.WriteStartObject();
                if (block is ReasoningBlock reasoning)
                {
                    writer.WriteString("kind", "reasoning");
                    writer.WriteString("text", reasoning.VisibleText);
                    writer.WriteString("visibility", reasoning.Visibility.ToString());
                }
                else if (block is TextBlock text)
                {
                    writer.WriteString("kind", "text");
                    writer.WriteString("text", text.Text);
                }
                else if (block is ToolCallBlock call)
                {
                    // Only an ordering marker. Tool identity/arguments come from its canonical event.
                    writer.WriteString("kind", "tool-call");
                    writer.WriteString("callId", call.Id.ToString());
                }
                else if (block is ImageBlock image)
                {
                    if (_artifacts is not IBinaryArtifactStore binaryArtifacts)
                        throw new InvalidDataException("Binary artifact storage is unavailable for image checkpointing.");
                    var decoded = GeneratedImageValidator.Decode(image.MediaType, image.Bytes.ToArray());
                    var reference = binaryArtifacts.PutBytes(decoded.Bytes, decoded.MediaType,
                        ArtifactKind.ModelResponse, Sensitivity.Sensitive);
                    writer.WriteString("kind", "image");
                    writer.WriteString("hash", reference.Hash.ToString());
                    writer.WriteNumber("size", reference.Size);
                    writer.WriteString("mediaType", decoded.MediaType);
                    writer.WriteNumber("width", decoded.Width);
                    writer.WriteNumber("height", decoded.Height);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private IReadOnlyList<ContentBlock> ReadVisibleContent(ArtifactRef? artifact,
        IReadOnlyDictionary<string, ToolCallRequested> canonicalCalls,
        IReadOnlyDictionary<string, ToolCallRequested> allTurnCalls,
        IReadOnlyDictionary<string, string> questionnaireArguments)
    {
        const string failure = "Persisted visible reasoning is invalid.";
        if (artifact is null) return Array.Empty<ContentBlock>();
        try
        {
            if (artifact.Kind != ArtifactKind.ModelResponse || !_artifacts.Verify(artifact.Hash, artifact.Size))
                throw new InvalidDataException(failure);
            using var document = System.Text.Json.JsonDocument.Parse(_artifacts.GetText(artifact.Hash)!);
            if (!document.RootElement.TryGetProperty("visibleContent", out var visible))
                return Array.Empty<ContentBlock>(); // legacy artifacts remain readable
            if (visible.GetProperty("version").GetInt32() != 1) throw new InvalidDataException(failure);
            var routeHash = visible.GetProperty("routeIdentityHash").GetString();
            if (_selection.RouteIdentityHash is null || routeHash != _selection.RouteIdentityHash)
                return Array.Empty<ContentBlock>();
            var blocks = new List<ContentBlock>();
            foreach (var block in visible.GetProperty("blocks").EnumerateArray())
            {
                var kind = block.GetProperty("kind").GetString();
                if (kind == "tool-call")
                {
                    var callId = block.GetProperty("callId").GetString();
                    if (callId is null || !Guid.TryParse(callId, out _)) throw new InvalidDataException(failure);
                    if (canonicalCalls.TryGetValue(callId, out var call))
                    {
                        var arguments = questionnaireArguments.TryGetValue(callId, out var schema) ? schema : call.ArgumentsJson;
                        blocks.Add(new ToolCallBlock(call.ToolCallId, call.ProviderCallId, call.ToolName, arguments));
                    }
                    else if (allTurnCalls.ContainsKey(callId)) throw new InvalidDataException(failure);
                    // An unexecuted call has no canonical requested event; don't manufacture one.
                    continue;
                }
                if (kind == "image")
                {
                    if (_artifacts is not IBinaryArtifactStore binaryArtifacts)
                        throw new InvalidDataException(failure);
                    var hashText = block.GetProperty("hash").GetString();
                    var hashParts = hashText?.Split(':', 2);
                    if (hashParts is not { Length: 2 } || hashParts[0] != "sha256"
                        || hashParts[1].Length != 64 || !hashParts[1].All(Uri.IsHexDigit))
                        throw new InvalidDataException(failure);
                    var hash = ContentHash.Sha256(hashParts[1].ToLowerInvariant());
                    var size = block.GetProperty("size").GetInt64();
                    var mediaType = block.GetProperty("mediaType").GetString();
                    if (mediaType is null) throw new InvalidDataException(failure);
                    var bytes = binaryArtifacts.GetBytes(hash, size);
                    if (bytes is null) throw new InvalidDataException(failure);
                    var decoded = GeneratedImageValidator.Decode(mediaType, bytes);
                    if (decoded.Width != block.GetProperty("width").GetInt32()
                        || decoded.Height != block.GetProperty("height").GetInt32())
                        throw new InvalidDataException(failure);
                    // A prior assistant image is UI history, not automatically valid context
                    // for an assistant-role input on the next provider request. Keep the
                    // immutable identity visible to the resumed prompt without claiming it was
                    // re-sent as image input.
                    blocks.Add(new ImageBlock(decoded.MediaType, decoded.Bytes));
                    continue;
                }
                var text = block.GetProperty("text").GetString();
                if (kind == "text" && text is not null)
                {
                    blocks.Add(new TextBlock(text));
                    continue;
                }
                if (kind != "reasoning") throw new InvalidDataException(failure);
                var visibilityName = block.GetProperty("visibility").GetString();
                if (!Enum.TryParse<ReasoningVisibility>(visibilityName, out var visibility)
                    || !Enum.IsDefined(visibility) || visibility.ToString() != visibilityName)
                    throw new InvalidDataException(failure);
                // Provider signatures and opaque artifact refs are never stored in this projection.
                blocks.Add(new ReasoningBlock(text, visibility, null));
            }
            return blocks;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
            or IOException or ArgumentException or KeyNotFoundException)
        {
            throw new InvalidDataException(failure, ex);
        }
    }

    private bool TryRecoverCompletedImageResponse(EventStream stream, TurnId turnId,
        out string? finalText, List<ValidatedGeneratedImage> images,
        List<(string Kind, string? Text, int ImageIndex)> orderedParts, out int stepCount,
        out bool publicationAlreadyExists, out string? responseArtifactId)
    {
        finalText = null;
        stepCount = 0;
        publicationAlreadyExists = false;
        responseArtifactId = null;
        var journal = stream.EventsSince(1);
        var decoded = journal.Select(evt => (Event: evt, Payload: _codecs.Decode(evt))).ToArray();
        if (decoded.Any(item => item.Payload switch
            {
                TurnCompleted terminal => terminal.TurnId == turnId,
                TurnInterrupted terminal => terminal.TurnId == turnId,
                TurnAbandoned terminal => terminal.TurnId == turnId,
                _ => false,
            })) return false;
        var publishedImages = decoded.Select(item => item.Payload).OfType<AssistantImagesRecorded>()
            .LastOrDefault(image => image.TurnId == turnId);
        if (publishedImages is not null)
        {
            // Final response events are committed together. If the process stopped before
            // TurnCompleted, finish that terminal transition from the exact published refs.
            // Never regenerate or publish a second copy of an already durable response.
            if (_artifacts is not IBinaryArtifactStore binaryArtifacts || publishedImages.Images.Count == 0)
                throw new InvalidDataException("Persisted generated image publication is incomplete.");
            var completed = decoded.Select(item => item.Payload).OfType<ModelStepCompleted>()
                .LastOrDefault(step => step.TurnId == turnId);
            if (completed is null || completed.StopReason != StopReason.EndTurn)
                throw new InvalidDataException("Persisted generated image has no completed model step.");
            stepCount = checked(completed.StepIndex + 1);
            responseArtifactId = decoded.Select(item => item.Payload).OfType<ModelCompleted>()
                .LastOrDefault(item => item.TurnId == turnId)?.ResponseArtifact?.Hash.ToString();

            var imageIndexes = new Dictionary<int, int>();
            var imageOutputsByHash = publishedImages.Images.OrderBy(image => image.OutputIndex)
                .GroupBy(image => image.Artifact.Hash.Value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key,
                    group => new Queue<AssistantImageOutput>(group), StringComparer.Ordinal);
            foreach (var output in publishedImages.Images.OrderBy(image => image.OutputIndex))
            {
                var bytes = binaryArtifacts.GetBytes(output.Artifact.Hash, output.Artifact.Size)
                    ?? throw new InvalidDataException("Persisted generated image bytes are missing.");
                var image = GeneratedImageValidator.Decode(output.Artifact.MediaType, bytes);
                if (image.Width != output.Width || image.Height != output.Height)
                    throw new InvalidDataException("Persisted generated image dimensions do not match its journal record.");
                imageIndexes.Add(output.OutputIndex, images.Count);
                images.Add(image);
            }

            var publishedText = new List<string>();
            if (publishedImages.OrderedParts is { Count: > 0 } parts)
            {
                foreach (var part in parts.OrderBy(part => part.OutputIndex))
                {
                    if (part.Kind == "image")
                    {
                        if (!imageOutputsByHash.TryGetValue(part.Artifact.Hash.Value, out var outputs)
                            || outputs.Count == 0)
                            throw new InvalidDataException("Persisted response ordering references an unknown image.");
                        var output = outputs.Dequeue();
                        orderedParts.Add(("image", null, imageIndexes[output.OutputIndex]));
                    }
                    else if (part.Kind == "text")
                    {
                        var text = _artifacts.GetText(part.Artifact.Hash)
                            ?? throw new InvalidDataException("Persisted response text is missing.");
                        // The stored canonical text is replayed unchanged.
                        orderedParts.Add(("text", text, -1));
                        publishedText.Add(text);
                    }
                    else
                    {
                        throw new InvalidDataException("Persisted response ordering has an unsupported part.");
                    }
                }
            }
            else
            {
                foreach (var output in publishedImages.Images.OrderBy(image => image.OutputIndex))
                    orderedParts.Add(("image", null, imageIndexes[output.OutputIndex]));
                var assistant = decoded.Select(item => item.Payload).OfType<AssistantMessageRecorded>()
                    .LastOrDefault(item => item.TurnId == turnId);
                if (assistant?.ContentRef is { } textRef && _artifacts.GetText(textRef.Hash) is { } text)
                {
                    // The stored canonical text is replayed unchanged.
                    orderedParts.Insert(0, ("text", text, -1));
                    publishedText.Add(text);
                }
            }
            finalText = publishedText.Count == 0 ? null : string.Concat(publishedText);
            publicationAlreadyExists = true;
            return true;
        }
        if (decoded.Any(item => item.Payload switch
            {
                ModelCompleted completed => completed.TurnId == turnId,
                AssistantMessageRecorded message => message.TurnId == turnId,
                _ => false,
            })) return false;
        var completedStep = decoded.Select(item => item.Payload).OfType<ModelStepCompleted>()
            .LastOrDefault(step => step.TurnId == turnId);
        if (completedStep is not { StopReason: StopReason.EndTurn, ResponseArtifact: not null }) return false;

        var visible = ReadVisibleContent(completedStep.ResponseArtifact,
            new Dictionary<string, ToolCallRequested>(StringComparer.Ordinal),
            new Dictionary<string, ToolCallRequested>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
        if (!visible.OfType<ImageBlock>().Any()) return false;

        var imageIndex = 0;
        foreach (var block in visible)
        {
            if (block is ImageBlock image)
            {
                images.Add(GeneratedImageValidator.Decode(image.MediaType, image.Bytes.ToArray()));
                orderedParts.Add(("image", null, imageIndex++));
            }
            else if (block is TextBlock text)
            {
                orderedParts.Add(("text", text.Text, -1));
            }
        }
        finalText = VisibleAnswerText(visible);
        stepCount = checked(completedStep.StepIndex + 1);
        return images.Count > 0;
    }

    private static List<ModelMessage> ProjectAssistantImagesForModelContext(List<ModelMessage> messages)
    {
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role != MessageRole.Assistant || !message.Content.Any(block => block is ImageBlock)) continue;
            var projected = message.Content.Select(block => block is ImageBlock image
                ? (ContentBlock)new TextBlock("[Generated image omitted from model context; "
                    + image.MediaType + ", " + image.Bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " bytes persisted in conversation history.]")
                : block).ToArray();
            messages[index] = message with { Content = projected };
        }
        return messages;
    }

    private static string DecodeUsageResponse(string? text) =>
        CanonicalSpendReader.TryDecodeUsageEnvelope(text, out var record) ? record.Response : text ?? "";

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
    private InteractionId EmitBudgetExceeded(EventStream stream, TurnId turnId, string detail,
        BudgetContinuationOffer? offer = null)
    {
        var interactionId = InteractionId.New();
        stream.Append(new InteractionRequested(interactionId, InteractionKind.BudgetExceeded,
            BudgetContinuation.Context(detail, offer),
            offer is null ? "[{\"id\":\"deny\",\"intent\":\"deny\"}]"
                : "[{\"id\":\"deny\",\"intent\":\"deny\"},{\"id\":\"allow_plus\",\"intent\":\"allow_plus\",\"value\":10}]",
            "deny", null, null, null, null, 0, 1));
        return interactionId;
    }

    /// <summary>
    /// Acumula costo de sesión/día (ADR-0037 §7: 5/20 USD). SOLO lanza al superar el tope:
    /// la emisión de la interacción la hace el catch del turno (evita la doble emisión).
    /// </summary>
    private void ValidateSessionDaily(decimal totalSessionCost, decimal totalDailyCost,
        decimal sessionCap, decimal dailyCap, string today)
    {
        if (_enforceSessionSpendCap && totalSessionCost > sessionCap)
            throw new BudgetExceededException("límite de sesión ($" + sessionCap + ")",
                new("session", _sessionCapUsd, sessionCap, null, null));
        if (_enforceDailySpendCap && totalDailyCost > dailyCap)
            throw new BudgetExceededException("límite diario ($" + dailyCap + ")",
                new("daily", _dailyCapUsd, dailyCap, null, today));
    }

    /// <summary>Renderiza el snapshot materializado como texto para el system prompt.</summary>
    public static string RenderContext(ContextSnapshot snapshot)
    {
        var parts = new List<string>();
        foreach (ContextItem item in snapshot.Items)
        {
            if (ExtensionContextData.IsReferenceData(item)) continue;
            if (item.Kind == ContextItemKind.WorkingState || item.Kind == ContextItemKind.System
                || item.Kind == ContextItemKind.Task || item.Kind == ContextItemKind.Decision
                || item.Kind == ContextItemKind.Constraint || item.Kind == ContextItemKind.File
                || item.Kind == ContextItemKind.Summary || item.Kind == ContextItemKind.Checkpoint
                || item.Kind == ContextItemKind.Knowledge
                || item.Kind == ContextItemKind.Memory || item.Kind == ContextItemKind.Skill)
            {
                if (item.Content.Length > 0)
                {
                    parts.Add("- " + item.Content.Replace("\n", " "));
                }
            }
        }

        return string.Join("\n", parts.ToArray());
    }

    internal List<ModelMessage> LoadConversation(EventStream stream, RunId runId,
        TurnId? activeTurnId = null, LaneId? activeLaneId = null,
        IDictionary<ModelMessage, RunId>? messageOwners = null)
    {
        var history = new List<ModelMessage>();
        var journal = stream.EventsSince(1);
        var conversationScope = new LaneConversationScope(journal, _codecs, runId, activeLaneId);
        var currentHistoryRun = runId;
        long currentHistorySequence = 0;
        var historySequences = new List<long>();
        void AppendHistory(ModelMessage message)
        {
            history.Add(message);
            historySequences.Add(currentHistorySequence);
            messageOwners?.Add(message, currentHistoryRun);
        }
        if (conversationScope.AllowsSessionHistory)
            foreach (var imported in journal.Select(_codecs.Decode).OfType<ConversationHistoryImported>())
                foreach (var message in imported.Messages)
                {
                    if (message.Role == "tool" && message.CallId is { } callId && message.ToolName is { } tool)
                    {
                        AppendHistory(new ModelMessage(MessageRole.Assistant, new ContentBlock[] { new ToolCallBlock(ToolCallId.Parse(callId), callId, tool, message.Arguments ?? "{}") }));
                        AppendHistory(new ModelMessage(MessageRole.Tool, new ContentBlock[] { new ToolResultBlock(ToolCallId.Parse(callId), new ContentBlock[] { new TextBlock(message.Text) }, message.IsError) }));
                    }
                    else AppendHistory(new ModelMessage(message.Role == "user" ? MessageRole.User : MessageRole.Assistant,
                        new ContentBlock[] { new TextBlock(message.Text) }));
                }
        var steeringInputs = new Dictionary<SteeringId, TurnSteeringReceived>();
        var questionnaireCalls = new HashSet<string>(StringComparer.Ordinal);
        var questionnaireResults = new Dictionary<string, string>(StringComparer.Ordinal);
        var interactionCalls = new Dictionary<string, string>(StringComparer.Ordinal);
        var questionnaireArguments = new Dictionary<string, string>(StringComparer.Ordinal);
        var activeStepStarts = new Dictionary<int, ModelStepStarted>();
        var canonicalCalls = new Dictionary<string, ToolCallRequested>(StringComparer.Ordinal);
        var callsByStep = new Dictionary<int, Dictionary<string, ToolCallRequested>>();
        var restoredCalls = new HashSet<string>(StringComparer.Ordinal);
        // Chat continuity follows the canonical visible message, not the optional usage summary.
        // Old journals may contain only ModelCompleted; retain that fallback per Run/Turn.
        var canonicalAssistantTurns = new HashSet<(RunId Run, TurnId Turn)>();
        int? activeStepIndex = null;
        int? completedStepIndex = null;
        var completedSteps = new HashSet<int>();
        foreach (var evt in journal)
        {
            if (!conversationScope.CanRead(evt)) continue;
            currentHistorySequence = evt.Sequence;
            var payload = _codecs.Decode(evt);
            if (payload is AssistantMessageRecorded assistant && evt.RunId == assistant.RunId)
                canonicalAssistantTurns.Add((assistant.RunId, assistant.TurnId));
            if (payload is AssistantImagesRecorded assistantImages && evt.RunId == assistantImages.RunId)
                canonicalAssistantTurns.Add((assistantImages.RunId, assistantImages.TurnId));
            if (activeTurnId is not null && evt.RunId == runId && evt.LaneId == activeLaneId)
            {
                if (payload is ModelStepStarted stepStart && stepStart.TurnId == activeTurnId)
                {
                    if (!activeStepStarts.TryAdd(stepStart.StepIndex, stepStart))
                        throw new InvalidDataException("Persisted visible reasoning is invalid.");
                    activeStepIndex = stepStart.StepIndex;
                    completedStepIndex = null;
                }
                else if (payload is ModelStepCompleted completion && completion.TurnId == activeTurnId)
                {
                    if (activeStepIndex != completion.StepIndex || !completedSteps.Add(completion.StepIndex))
                        throw new InvalidDataException("Persisted visible reasoning is invalid.");
                    completedStepIndex = completion.StepIndex;
                }
                else if (evt.TurnId == activeTurnId && payload is ToolCallRequested canonicalCall)
                {
                    var callId = canonicalCall.ToolCallId.ToString();
                    if (!canonicalCalls.TryAdd(callId, canonicalCall))
                        throw new InvalidDataException("Persisted visible reasoning is invalid.");
                    if (completedStepIndex is { } owner)
                    {
                        if (!callsByStep.TryGetValue(owner, out var ownedCalls))
                            callsByStep.Add(owner, ownedCalls = new Dictionary<string, ToolCallRequested>(StringComparer.Ordinal));
                        ownedCalls.Add(callId, canonicalCall);
                    }
                }
            }
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

        foreach (var evt in journal)
        {
            if (!conversationScope.CanRead(evt)) continue;
            var type = evt.Type.ToString();
            currentHistorySequence = evt.Sequence;
            currentHistoryRun = evt.RunId ?? runId;
            if (_codecs.Decode(evt) is TurnSteeringReceived steeringReceived)
            {
                steeringInputs.Add(steeringReceived.SteeringId, steeringReceived);
            }
            else if (_codecs.Decode(evt) is TurnSteeringApplied steeringApplied)
            {
                if (!steeringInputs.TryGetValue(steeringApplied.SteeringId, out var received)
                    || received.RunId != steeringApplied.RunId || received.LaneId != steeringApplied.LaneId
                    || received.TurnId != steeringApplied.TurnId)
                    throw new InvalidDataException("Steering application has no matching input.");
                AppendHistory(SteeringMessage(received.InputPartsJson));
            }
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
                    var content = new List<ContentBlock>();
                    if (text.Length > 0) content.Add(new TextBlock(text));
                    foreach (var reference in input.Attachments ?? Array.Empty<ArtifactRef>())
                    {
                        var attachment = AttachmentContentMaterializer.Read(reference, _artifacts);
                        if (attachment is ImageBlock && !_selection.InputModalities.Contains("image", StringComparer.Ordinal))
                            throw new NotSupportedException("The effective model/provider route does not declare image input support.");
                        if (attachment is DocumentBlock document)
                            attachment = document;
                        content.Add(attachment);
                    }
                    if (content.Count > 0) AppendHistory(new ModelMessage(MessageRole.User, content));
                }
                catch (System.Text.Json.JsonException) { }
            }
            else if (type == "model_step.completed" && activeTurnId is not null
                && evt.RunId == runId && evt.LaneId == activeLaneId
                && _codecs.Decode(evt) is ModelStepCompleted stepCompleted
                && stepCompleted.TurnId == activeTurnId)
            {
                if (!activeStepStarts.TryGetValue(stepCompleted.StepIndex, out var stepStart))
                    throw new InvalidDataException("Persisted visible reasoning is invalid.");
                if (Equals(stepStart.RouteId, _selection.RouteId) && stepStart.ModelId == _selection.Model.ToString())
                {
                    var ownedCalls = callsByStep.TryGetValue(stepCompleted.StepIndex, out var calls)
                        ? calls : new Dictionary<string, ToolCallRequested>(StringComparer.Ordinal);
                    var visible = ReadVisibleContent(stepCompleted.ResponseArtifact, ownedCalls, canonicalCalls, questionnaireArguments);
                    foreach (var call in visible.OfType<ToolCallBlock>())
                        if (!restoredCalls.Add(call.Id.ToString()))
                            throw new InvalidDataException("Persisted visible reasoning is invalid.");
                    if (visible.Count > 0) AppendHistory(new ModelMessage(MessageRole.Assistant, visible));
                }
            }
            else if (type == "toolcall.requested")
            {
                var call = _codecs.Decode(evt) as ToolCallRequested;
                if (call is not null)
                {
                    if (restoredCalls.Contains(call.ToolCallId.ToString())) continue;
                    var arguments = questionnaireArguments.TryGetValue(call.ToolCallId.ToString(), out var schemaJson)
                        ? schemaJson : call.ArgumentsJson;
                    AppendHistory(new ModelMessage(MessageRole.Assistant, new ContentBlock[] {
                        new ToolCallBlock(call.ToolCallId, call.ProviderCallId, call.ToolName,
                            arguments)
                    }));
                }
            }
            else if (type == "toolcall.succeeded" || type == "toolcall.failed" || type == "toolcall.rejected"
                || _codecs.Decode(evt) is PermissionDenied)
            {
                var payload = _codecs.Decode(evt);
                var callId = payload switch
                {
                    ToolCallSucceeded e => e.ToolCallId,
                    ToolCallFailed e => e.ToolCallId,
                    ToolCallRejected e => e.ToolCallId,
                    PermissionDenied e => e.ToolCallId,
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
                        ToolCallSucceeded e => e.ResultJson,
                        ToolCallFailed e => "error: " + _redaction.Redact(e.Cause),
                        ToolCallRejected e => "error: " + _redaction.Redact(e.Reason),
                        PermissionDenied e => "error: " + _redaction.Redact(e.Cause),
                        _ => "error",
                    };
                var resultBlocks = new List<ContentBlock> { new TextBlock(content) };
                if (payload is ToolCallSucceeded { ResultArtifact: { } resultArtifact }
                    && resultArtifact.MediaType.StartsWith("image/", StringComparison.Ordinal))
                {
                    if (!_selection.InputModalities.Contains("image", StringComparer.Ordinal))
                        throw new NotSupportedException("The effective model/provider route does not support image tool results.");
                    resultBlocks.Add(AttachmentContentMaterializer.Read(resultArtifact, _artifacts));
                }
                AppendHistory(new ModelMessage(MessageRole.Tool, new ContentBlock[] {
                    new ToolResultBlock(callId!, resultBlocks,
                        isQuestionnaireCall ? content.Contains("\"status\":\"invalid\"", StringComparison.Ordinal)
                            : payload is not ToolCallSucceeded)
                }));
            }
            else if (type == "assistant_message.recorded")
            {
                var assistant = _codecs.Decode(evt) as AssistantMessageRecorded;
                if (assistant?.ContentRef is null || evt.RunId != assistant.RunId) continue;
                var text = _artifacts.GetText(assistant.ContentRef.Hash);
                if (!string.IsNullOrEmpty(text))
                    AppendHistory(new ModelMessage(MessageRole.Assistant,
                        new ContentBlock[] { new TextBlock(text) }));
            }
            else if (type == "assistant_images.recorded")
            {
                var images = _codecs.Decode(evt) as AssistantImagesRecorded;
                if (images is null || evt.RunId != images.RunId || images.Images.Count == 0
                    || images.TurnId == activeTurnId) continue;
                // The image is replayed to UI through the authorized artifact endpoint. Do not
                // silently inject a previous assistant image into the next model context.
            }
            else if (type == "model.completed")
            {
                var completed = _codecs.Decode(evt) as ModelCompleted;
                if (completed?.ResponseArtifact is null) continue;
                if (evt.RunId is { } owner && canonicalAssistantTurns.Contains((owner, completed.TurnId))) continue;
                var text = DecodeUsageResponse(_artifacts.GetText(completed.ResponseArtifact.Hash));
                if (!string.IsNullOrEmpty(text))
                    AppendHistory(new ModelMessage(MessageRole.Assistant,
                        new ContentBlock[] { new TextBlock(text) }));
            }
        }

        var checkpoint = SessionConversationCheckpoint.Read(journal, _codecs, _artifacts, runId, activeLaneId);
        if (checkpoint is null) return history;
        var firstInput = history.FindIndex(message => message.Role == MessageRole.User);
        var firstCanonicalInput = journal.FirstOrDefault(evt => conversationScope.CanRead(evt) && _codecs.Decode(evt) is UserInputReceived)?.Sequence;
        return history.Where((_, index) => index == firstInput || historySequences[index] == firstCanonicalInput
            || historySequences[index] > checkpoint.ThroughSequence).ToList();
    }

    private TurnId? FindOpenTurn(EventStream stream, RunId runId, LaneId laneId)
    {
        TurnId? open = null;
        foreach (var evt in stream.EventsSince(1))
        {
            if (evt.RunId is null || !evt.RunId.Equals(runId)) continue;
            var payload = _codecs.Decode(evt);
            switch (payload)
            {
                case TurnStarted started when started.LaneId == laneId:
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

    /// <summary>Rebuilds Run diagnostics and this logical Turn's mutation budget from durable
    /// charge events. A legacy, malformed, or interrupted file mutation without a complete charge
    /// is fail-closed; no journal path or version token is restored as filesystem authority.</summary>
    private void RestoreTurnMutationBudget(EventStream stream, RunId runId, TurnId turnId)
    {
        var registry = _boundary?.ReadRegistry();
        var ledger = registry?.Ledger;
        if (ledger is null) return;

        ledger.ResetTurnBudgetForReplay();
        ledger.ResetRunBudgetForReplay();
        var requests = new Dictionary<ToolCallId, string>();
        var starts = new Dictionary<ToolCallId, (DomainEvent Envelope, ToolCallStarted Payload)>();
        var terminals = new Dictionary<ToolCallId, (DomainEvent Envelope, DomainEventPayload Payload)>();
        var charges = new Dictionary<ToolCallId, List<(DomainEvent Envelope, MutationBudgetCharged Charge)>>();
        var integrationCalls = new HashSet<ToolCallId>();
        var integrationStates = new Dictionary<ToolCallId, WorktreeIntegrationStateRecorded>();
        var consumedValidationCalls = new HashSet<ToolCallId>();
        IReadOnlyList<WorkspaceMutationReceipt> durableCharges = [];
        try
        {
            if (_tools is ScriptedToolExecutor { MutationHistoryStore: { } history } executor)
                durableCharges = history.ReadBudgetChargesForRun(executor.WorkspaceRoot, runId.ToString());
        }
        catch (Exception)
        {
            ledger.MarkRunBudgetUnknown();
            ledger.MarkTurnBudgetUnknown();
        }
        var durableCalls = durableCharges.Select(charge => charge.ToolCallId).ToHashSet();

        foreach (var envelope in stream.EventsSince(1).Where(item => item.RunId == runId))
        {
            var payload = _codecs.Decode(envelope);
            switch (payload)
            {
                case ToolCallRequested requested:
                    if (envelope.ToolCallId != requested.ToolCallId)
                        ledger.MarkTurnBudgetUnknown();
                    requests[requested.ToolCallId] = requested.ToolName;
                    break;
                case ToolCallStarted started:
                    if (envelope.ToolCallId != started.ToolCallId
                        || !starts.TryAdd(started.ToolCallId, (envelope, started)))
                        ledger.MarkTurnBudgetUnknown();
                    break;
                case MutationBudgetCharged charge:
                    if (envelope.ToolCallId != charge.ToolCallId)
                        ledger.MarkTurnBudgetUnknown();
                    if (!charges.TryGetValue(charge.ToolCallId, out var items))
                        charges[charge.ToolCallId] = items = [];
                    items.Add((envelope, charge));
                    break;
                case ToolCallSucceeded succeeded:
                    if (envelope.ToolCallId != succeeded.ToolCallId)
                        ledger.MarkTurnBudgetUnknown();
                    terminals[succeeded.ToolCallId] = (envelope, payload);
                    break;
                case ToolCallFailed failed:
                    if (envelope.ToolCallId != failed.ToolCallId)
                        ledger.MarkTurnBudgetUnknown();
                    terminals[failed.ToolCallId] = (envelope, payload);
                    break;
                case ToolCallEffectUnknown unknown:
                    if (envelope.ToolCallId != unknown.ToolCallId)
                        ledger.MarkTurnBudgetUnknown();
                    terminals[unknown.ToolCallId] = (envelope, payload);
                    break;
                case ToolCallReconciled reconciled:
                    if (envelope.ToolCallId != reconciled.ToolCallId)
                        ledger.MarkTurnBudgetUnknown();
                    terminals[reconciled.ToolCallId] = (envelope, payload);
                    break;
                case WorktreeIntegrationStateRecorded integration:
                    if (envelope.ToolCallId != integration.ToolCallId)
                        ledger.MarkTurnBudgetUnknown();
                    integrationCalls.Add(integration.ToolCallId);
                    integrationStates[integration.ToolCallId] = integration;
                    break;
                case PostEditValidationConsumed consumed when consumed.RunId == runId:
                    foreach (var editId in consumed.EditIds) consumedValidationCalls.Add(editId);
                    break;
            }
        }

        foreach (var (toolCallId, started) in starts)
        {
            var isCurrentTurn = started.Envelope.TurnId == turnId;
            var hasRequest = requests.TryGetValue(toolCallId, out var toolName);
            if (!hasRequest)
            {
                ledger.MarkRunBudgetUnknown();
                if (isCurrentTurn) ledger.MarkTurnBudgetUnknown();
                continue;
            }
            var governedMutation = toolName is "filesystem.write" or "filesystem.patch" or "worktree.integrate"
                || integrationCalls.Contains(toolCallId);
            if (!governedMutation)
            {
                continue;
            }

            var hasCharges = charges.TryGetValue(toolCallId, out var callCharges) && callCharges.Count > 0;
            terminals.TryGetValue(toolCallId, out var terminal);
            if (terminal is { Payload: ToolCallSucceeded } && hasCharges)
            {
                if (integrationStates.TryGetValue(toolCallId, out var integrationState)
                    && (integrationState.State != WorktreeIntegrationState.Completed
                        || integrationState.Files is null
                        || integrationState.Files.Count(file => file.Outcome == ReconciliationOutcome.Applied) != callCharges!.Count))
                {
                    ledger.MarkRunBudgetUnknown();
                    if (isCurrentTurn) ledger.MarkTurnBudgetUnknown();
                }
                foreach (var (chargeEnvelope, charge) in callCharges!)
                {
                    if (chargeEnvelope.RunId != runId || chargeEnvelope.TurnId != started.Envelope.TurnId
                        || chargeEnvelope.ToolCallId != toolCallId
                        || started.Envelope.RunId != runId
                        || started.Envelope.ToolCallId != toolCallId
                        || terminal.Envelope.RunId != runId
                        || terminal.Envelope.TurnId != started.Envelope.TurnId
                        || terminal.Envelope.ToolCallId != toolCallId)
                    {
                        ledger.MarkRunBudgetUnknown();
                        if (isCurrentTurn) ledger.MarkTurnBudgetUnknown();
                        continue;
                    }
                    ledger.RestoreRunBudgetCharge(charge.TargetKeyHash, charge.ChangedLines, charge.ToolCallId);
                    if (isCurrentTurn)
                        ledger.RestoreTurnBudgetCharge(charge.TargetKeyHash, charge.ChangedLines, charge.ToolCallId);
                }
                continue;
            }

            // A completed non-effecting failure and a proven NotApplied reconciliation consume
            // no budget. Every other uncharged or incomplete mutation is uncertain and must not
            // get a fresh allowance merely because this invocation has a new in-memory boundary.
            if (terminal is { Payload: ToolCallFailed { EffectOutcome: EffectOutcome.None } } && !hasCharges
                || terminal is { Payload: ToolCallReconciled { Outcome: ReconciliationOutcome.NotApplied } } && !hasCharges)
                continue;

            // The workspace receipt is committed before the buffered terminal event. It is the
            // recovery evidence for an effect whose Run crashed after publication but before the
            // journal batch drained; it consumes budget once and avoids replaying the write.
            if (durableCalls.Contains(toolCallId)) continue;

            ledger.MarkRunBudgetUnknown();
            if (isCurrentTurn) ledger.MarkTurnBudgetUnknown();
        }

        // A charge with no matching Started event is malformed durable evidence. Never ignore it
        // and accidentally grant a clean budget on the next tool call.
        foreach (var (toolCallId, callCharges) in charges.Where(pair => !starts.ContainsKey(pair.Key)))
        {
            ledger.MarkRunBudgetUnknown();
            if (callCharges.Any(item => item.Envelope.TurnId == turnId)) ledger.MarkTurnBudgetUnknown();
        }

        foreach (var receipt in durableCharges)
        {
            if (receipt.State != WorkspaceMutationReceiptState.Applied || receipt.DeletedLines < 0
                || receipt.InsertedLines < 0 || !Enum.TryParse<ModelToolCapability>(receipt.Operation, out var operation))
            {
                ledger.MarkRunBudgetUnknown();
                if (receipt.TurnId == turnId.ToString()) ledger.MarkTurnBudgetUnknown();
                continue;
            }
            var expectedKey = registry!.MutationBudgetKey(receipt.RelativePath);
            var eventCharges = charges.TryGetValue(receipt.ToolCallId, out var items)
                ? items.Select(item => item.Charge).ToArray() : [];
            var matching = eventCharges.Where(charge => charge.TargetKeyHash == expectedKey
                && (long)charge.ChangedLines == (long)receipt.DeletedLines + receipt.InsertedLines).ToArray();
            if (matching.Length == 1)
            {
                if (ledger.MutationPolicy?.RequirePostEditValidation == true
                    && !consumedValidationCalls.Contains(receipt.ToolCallId))
                    ledger.RestorePendingValidation(receipt.RelativePath, receipt.ToolCallId);
                continue; // budget charge already restored from the event journal
            }
            if (eventCharges.Length != 0)
            {
                ledger.MarkRunBudgetUnknown();
                if (receipt.TurnId == turnId.ToString()) ledger.MarkTurnBudgetUnknown();
                continue;
            }
            var changedTotal = (long)receipt.DeletedLines + receipt.InsertedLines;
            if (changedTotal > int.MaxValue)
            {
                ledger.MarkRunBudgetUnknown();
                if (receipt.TurnId == turnId.ToString()) ledger.MarkTurnBudgetUnknown();
                continue;
            }
            var changedLines = (int)changedTotal;
            ledger.RestoreDurableBudgetCharge(receipt.ToolCallId, expectedKey, changedLines,
                receipt.TurnId == turnId.ToString());
            if (ledger.MutationPolicy?.RequirePostEditValidation == true
                && !consumedValidationCalls.Contains(receipt.ToolCallId))
                ledger.RestorePendingValidation(receipt.RelativePath, receipt.ToolCallId);
        }
    }

    private TaskId? FindTaskForLane(IReadOnlyList<DomainEvent> events, RunId runId, LaneId laneId)
    {
        foreach (var evt in events)
        {
            if (evt.RunId != runId) continue;
            if (_codecs.Decode(evt) is LaneCreated created && created.LaneId == laneId)
                return created.TaskId;
        }

        return null;
    }

    private void PublishFingerprintArtifacts(ExecutionFingerprint fingerprint,
        IReadOnlyList<IPreparedArtifact>? turnArtifacts)
    {
        var references = fingerprint.Components.Where(component => component.Content is not null)
            .Select(component => component.Content!).ToHashSet();
        foreach (var artifact in _fingerprintArtifacts.Concat(turnArtifacts ?? Array.Empty<IPreparedArtifact>())
            .Where(artifact => references.Contains(artifact.Reference)))
            if (artifact.Publish() != artifact.Reference)
                throw new InvalidDataException("Prepared fingerprint publication changed its reference.");
        foreach (var reference in references)
            if (!_artifacts.Verify(reference.Hash, reference.Size))
                throw new InvalidDataException("Fingerprint component artifact failed publication integrity verification.");
    }

    private bool TryRecoverDiscoveredToolPlan(EventStream stream, TurnId turnId, string instruction,
        IReadOnlyList<DomainEvent> initialPlanEvents, ProfileId? laneProfileId,
        RunModeAuthority? fingerprintAuthority, TurnInstructionSnapshot? instructionSnapshot,
        ReasoningResolution? reasoningResolution, HashSet<string> discoveredToolIds,
        List<string> discoveredToolOrder,
        out RuntimeFingerprintFactory.PreparedRuntimeFingerprint? recovered, out string? error)
    {
        recovered = null;
        error = null;
        try
        {
            var latest = stream.EventsSince(1).Where(evt => evt.TurnId == turnId)
                .Select(evt => _codecs.Decode(evt)).OfType<ModelStepStarted>()
                .OrderByDescending(step => step.StepIndex).FirstOrDefault();
            if (latest?.ContextSnapshotRef is not { } snapshotRef)
                return false;
            if (!_artifacts.Verify(snapshotRef.Hash, snapshotRef.Size)
                || _artifacts.GetText(snapshotRef.Hash) is not { } snapshotText)
                throw new InvalidDataException("Persisted ModelStep snapshot failed integrity verification.");

            using var snapshot = System.Text.Json.JsonDocument.Parse(snapshotText);
            var root = snapshot.RootElement;
            if (!root.TryGetProperty("toolPlan", out var savedPlan)
                || savedPlan.ValueKind != System.Text.Json.JsonValueKind.Object)
                return false; // Legacy context snapshot: there is no safe discovery receipt to replay.
            var savedFingerprint = root.GetProperty("fingerprint").GetString();
            var componentHashText = savedPlan.GetProperty("componentHash").GetString();
            var artifactHashText = savedPlan.GetProperty("artifactHash").GetString();
            var artifactSize = savedPlan.GetProperty("artifactSize").GetInt64();
            if (savedPlan.GetProperty("componentVersion").GetString() != "3")
                throw new InvalidDataException("Persisted discovered tool plan version is unsupported.");
            if (string.IsNullOrWhiteSpace(savedFingerprint) || string.IsNullOrWhiteSpace(componentHashText)
                || !TryContentHash(componentHashText, out var componentHash)
                || !TryContentHash(artifactHashText, out var artifactHash)
                || componentHash != artifactHash || artifactSize < 0
                || !_artifacts.Verify(artifactHash, artifactSize)
                || _artifacts.GetText(artifactHash) is not { } toolPlanText)
                throw new InvalidDataException("Persisted discovered tool plan failed integrity verification.");

            using var planDocument = System.Text.Json.JsonDocument.Parse(toolPlanText);
            var tools = planDocument.RootElement.GetProperty("tools");
            if (tools.ValueKind != System.Text.Json.JsonValueKind.Array)
                throw new InvalidDataException("Persisted discovered tool plan is malformed.");
            var initialIds = VisibleTools(new HashSet<string>(StringComparer.Ordinal))
                .Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
            var savedDefinitions = new List<ToolDefinition>();
            foreach (var entry in tools.EnumerateArray())
            {
                var visibleName = entry.GetProperty("visibleName").GetString();
                var toolId = entry.GetProperty("toolId").GetString();
                var description = entry.GetProperty("description").GetString();
                var schema = entry.GetProperty("inputSchemaJson").GetString();
                if (string.IsNullOrWhiteSpace(toolId) || !string.Equals(visibleName, toolId, StringComparison.Ordinal)
                    || description is null || schema is null)
                    throw new InvalidDataException("Persisted discovered tool mapping is not canonical.");
                var tool = _catalog.Find(new ToolId(toolId));
                if (tool is null || tool.Descriptor.Source.Trust == TrustLevel.Untrusted
                    || _boundary is not null && !_boundary.IsToolVisible(toolId))
                    throw new InvalidDataException("Persisted request tool is no longer eligible.");
                savedDefinitions.Add(new ToolDefinition(visibleName!, description, schema));
                if (!initialIds.Contains(toolId) && discoveredToolIds.Add(toolId)) discoveredToolOrder.Add(toolId);
            }

            // A ModelStep can intentionally receive no tools when the per-Turn call cap is
            // exhausted. Otherwise its persisted request set must still be the exact live plan.
            if (savedDefinitions.Count > 0)
            {
                var expected = VisibleTools(discoveredToolIds, discoveredToolOrder);
                if (!EquivalentToolDefinitions(savedDefinitions, expected))
                    throw new InvalidDataException("Persisted request tool schemas differ from the current effective plan.");
            }
            var plan = RuntimeFingerprintFactory.PrepareTurnConfiguration(_fingerprint, _catalog,
                savedDefinitions, EffectiveSystemPrompt(instruction),
                PlanProjection.Replay(_codecs, initialPlanEvents).Latest(), _artifacts, laneProfileId,
                _activeSkills, _resolvedAgentProfile, fingerprintAuthority, instructionSnapshot, reasoningResolution,
                _toolPreferences);
            var rebuiltToolPlan = plan.Fingerprint.Components.SingleOrDefault(component => component.Name == "tools.plan");
            if (!string.Equals(plan.Fingerprint.Hash(), savedFingerprint, StringComparison.Ordinal)
                || rebuiltToolPlan is null || rebuiltToolPlan.Hash != componentHash)
                throw new InvalidDataException("Persisted discovered tool plan differs from current catalog or effective policy.");
            recovered = plan;
            return discoveredToolIds.Count > 0;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException
            or IOException or ArgumentException or InvalidDataException or FormatException)
        {
            error = "Cannot resume Turn: discovered tool plan failed integrity or eligibility validation.";
            return false;
        }

        static bool TryContentHash(string? text, out ContentHash hash)
        {
            hash = new ContentHash("", "");
            if (text is null) return false;
            var separator = text.IndexOf(':');
            if (separator < 1 || separator == text.Length - 1) return false;
            hash = new ContentHash(text[..separator], text[(separator + 1)..]);
            return hash.Algorithm == "sha256" && hash.Value.Length == 64
                && hash.Value.All(Uri.IsHexDigit);
        }

        static bool EquivalentToolDefinitions(IReadOnlyList<ToolDefinition> left,
            IReadOnlyList<ToolDefinition> right) => left.Count == right.Count
            && left.Zip(right).All(pair => string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
                && string.Equals(pair.First.Description, pair.Second.Description, StringComparison.Ordinal)
                && string.Equals(pair.First.InputSchemaJson, pair.Second.InputSchemaJson, StringComparison.Ordinal));
    }

    private LaneCreated? FindAgentProfileForLane(IReadOnlyList<DomainEvent> events, RunId runId, LaneId laneId)
    {
        foreach (var evt in events)
        {
            if (evt.RunId != runId) continue;
            if (_codecs.Decode(evt) is LaneCreated created && created.LaneId == laneId)
                return created;
        }
        return null;
    }

    private InteractionRequested? FindPendingRunInteraction(IReadOnlyList<DomainEvent> events, RunId runId)
    {
        var pending = new Dictionary<InteractionId, InteractionRequested>();
        foreach (var evt in events)
        {
            var payload = _codecs.Decode(evt);
            switch (payload)
            {
                case InteractionRequested requested when evt.RunId == runId:
                    pending[requested.InteractionId] = requested;
                    break;
                case InteractionResolved resolved:
                    pending.Remove(resolved.InteractionId);
                    break;
                case InteractionExpired expired:
                    pending.Remove(expired.InteractionId);
                    break;
            }
        }
        return pending.Values.FirstOrDefault();
    }

    private static string InputParts(string text) => "[\"" + JsonObj.Escape(text) + "\"]";

    private static string FollowUpText(string inputPartsJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(inputPartsJson);
            var root = document.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.String) return root.GetString() ?? "";
            if (root.ValueKind != System.Text.Json.JsonValueKind.Array) return "";
            return string.Join("\n", root.EnumerateArray()
                .Where(part => part.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(part => part.GetString() ?? "")
                .Where(part => part.Length > 0));
        }
        catch (System.Text.Json.JsonException)
        {
            return "";
        }
    }

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

    private string EffectiveSystemPrompt(string instruction) => (
        "Contexto del workspace (fuentes del run):\n"
        + (instruction ?? "").Replace("{context}", "", StringComparison.Ordinal).Trim()
        + "\nFingerprint: " + _fingerprint.ModelKey + " · " + _fingerprint.ContextPolicyHash);

    private PreparedTurnContext MaterializeTurnContext(EventStream stream, SessionId sessionId, RunId runId,
        LaneId laneId, TurnId turnId, string workingStateText, string instruction,
        IReadOnlyList<ModelMessage> messages, IReadOnlyDictionary<ModelMessage, RunId> messageOwners,
        ExecutionFingerprint fingerprint, CancellationToken cancellationToken,
        Func<bool>? canInvokeMeta = null, Func<string, bool>? reserveMeta = null,
        Action<string>? dispatchMeta = null, Action<string, decimal?, long>? finishMeta = null,
        Action<string>? releaseMeta = null, IReadOnlyList<ContextItem>? hookAnnotations = null,
        Action<LifecyclePoint, string?>? dispatchLifecycle = null)
    {
        var contextTask = FindTaskForLane(stream.EventsSince(1), runId, laneId);
        var contributors = new List<IContextContributor>();
        // Only explicit, canonically accepted selected-context packets. No automatic parent
        // transcript import, worker admission, scheduler decision or authority transfer.
        var inherited = new ContextInheritanceService(_store, _codecs, _artifacts)
            .FindDelegationProjection(sessionId, runId, laneId);
        if (inherited is not null) contributors.Add(new ValidatedContextContributor(inherited));
        if (hookAnnotations is { Count: > 0 })
            contributors.Add(new ValidatedContextContributor(
                new LifecycleAnnotationContributor(hookAnnotations)));
        var hasWorkingState = false;
        foreach (var contributor in _materializer.Contributors())
        {
            hasWorkingState |= contributor is WorkingStateContributor;
            contributors.Add(new ValidatedContextContributor(contributor,
                rejectSkills: _activeSkills is { Count: 0 }));
        }

        if (!hasWorkingState && !string.IsNullOrWhiteSpace(workingStateText))
        {
            contributors.Add(new ValidatedContextContributor(new WorkingStateContributor(workingStateText)));
        }

        // ADR-0048 §3: la directiva de Replan/Diagnose/SplitTask se proyecta del journal en cada Turn.
        if (StallWatch.ActiveDirective(_stallDecoder, stream.EventsSince(1), runId, laneId) is { } directive)
        {
            var canProposePlan = _catalog.Find(new ToolId("plan.propose")) is not null;
            contributors.Add(new ValidatedContextContributor(
                new StallDirectiveContributor(directive.Render(canProposePlan))));
        }

        var prompt = EffectiveSystemPrompt(instruction);
        contributors.Add(new ValidatedContextContributor(new SystemPromptContributor(prompt)));
        var sessionCheckpoint = SessionConversationCheckpoint.Read(stream.EventsSince(1), _codecs, _artifacts, runId, laneId);
        if (sessionCheckpoint is not null)
            contributors.Add(new ContextCheckpointContributor(sessionCheckpoint.Id, sessionCheckpoint.ThroughSequence,
                sessionCheckpoint.Summary, sessionCheckpoint.Artifact));
        // Pressure probes and final planning share one contribution read. An RPC
        // contributor must not execute twice merely to check the context threshold.
        var contributionRequest = new MaterializeRequest(sessionId, runId, contextTask, laneId, turnId,
            _store.CurrentSequence(sessionId), fingerprint);
        contributors = contributors.Select(contributor => (IContextContributor)new TurnContextContribution(
            contributor.GetContextAsync(contributionRequest, cancellationToken).GetAwaiter().GetResult())).ToList();
        var entries = new List<ConversationContextEntry>();
        var messageById = new Dictionary<string, ModelMessage>(StringComparer.Ordinal);
        var runById = new Dictionary<string, RunId>(StringComparer.Ordinal);
        var policy = _harness?.ContextManagement ?? ContextManagementPolicy.Default;
        var firstUser = true;
        var latestUserIndex = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role != MessageRole.User) continue;
            latestUserIndex = i;
            break;
        }
        for (var i = 0; i < messages.Count; i++)
        {
            var safe = PreserveMessage(messages[i]);
            var id = "conversation-" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            var kind = safe.Role switch
            {
                MessageRole.User => ContextItemKind.UserMessage,
                MessageRole.Assistant => ContextItemKind.AssistantMessage,
                _ => ContextItemKind.ToolResult,
            };
            // Historical context must never silently evict the user's current intent.
            // If protected input itself cannot fit, the existing overflow barrier stops dispatch.
            var preserve = !policy.AutomaticCompaction || safe.Role == MessageRole.User && (firstUser || i == latestUserIndex);
            if (safe.Role == MessageRole.User)
            {
                firstUser = false;
            }

            entries.Add(new ConversationContextEntry(id, kind, RenderMessage(safe), preserve,
                ImageBudgetOverhead(safe)));
            messageById.Add(id, safe);
            runById.Add(id, messageOwners.TryGetValue(messages[i], out var owner) ? owner : runId);
        }

        var summarizedIds = new HashSet<string>(StringComparer.Ordinal);
        var summaryService = new RunSummaryService(_store, _codecs, _artifacts, static text => text);
        var summaries = summaryService.Read(sessionId, runId);
        if (policy.AutomaticCompaction && summaries.Count > 0)
        {
            // Probe the existing budget policy, not a new percentage/character heuristic.
            // Prepared artifacts are not published by this probe.
            var probe = new ContextMaterializer(_materializer.Counter(),
                contributors.Append(new SessionConversationContributor(entries)).ToArray(), _artifacts, policy)
                .PrepareWithinBudget(new MaterializeRequest(sessionId, runId, contextTask, laneId, turnId,
                    _store.CurrentSequence(sessionId), fingerprint), cancellationToken, (int)_selection.ContextBudget).Snapshot;
            var affectedRuns = probe.Diagnostics.Where(diagnostic => diagnostic.Decision is ContextDecision.OmittedByBudget or ContextDecision.TruncatedByBudget)
                .Where(diagnostic => runById.TryGetValue(diagnostic.ItemId, out var owner) && owner != runId)
                .Select(diagnostic => runById[diagnostic.ItemId]).ToHashSet();
            foreach (var (summary, artifact) in summaries.Where(item => affectedRuns.Contains(item.Summary.RunId)))
            {
                foreach (var entry in entries.Where(entry => !entry.PreserveWhenTrimming && runById[entry.Id] == summary.RunId))
                    summarizedIds.Add(entry.Id);
                contributors.Add(new RunSummaryContributor(summary, artifact, summaryService.Render(summary)));
            }
        }
        var priorCheckpoint = policy.AutomaticCompaction ? ReadLatestCheckpoint(stream, runId, laneId) : null;
        var compactedThroughIndex = priorCheckpoint?.CompactedThroughItemIndex ?? -1;
        var compactedIds = new HashSet<string>(entries.Where(e => !e.PreserveWhenTrimming
                && ConversationIndex(e.Id) is var index && index >= 0 && index <= compactedThroughIndex)
            .Select(e => e.Id), StringComparer.Ordinal);
        var firstInput = entries.FirstOrDefault(entry => entry.Kind == ContextItemKind.UserMessage);
        var currentInput = entries.LastOrDefault(entry => entry.Kind == ContextItemKind.UserMessage);
        // Current input keeps its recent-tail position and may inform a checkpoint, but
        // its original user message cannot be removed. Preserve the configured cadence.
        var candidates = entries.Where(e => IsConversationKind(e.Kind)
            && (!e.PreserveWhenTrimming || e.Id == currentInput?.Id && e.Id != firstInput?.Id)
            && !compactedIds.Contains(e.Id) && !summarizedIds.Contains(e.Id)).ToArray();
        var oldCount = Math.Max(0, candidates.Length - Math.Max(0, policy.RecentTailItems));
        var oldItems = candidates.Take(oldCount).ToArray();
        var newOldItems = oldItems.Where(e => !compactedIds.Contains(e.Id)).ToArray();
        var probeEntries = entries.Where(entry => !compactedIds.Contains(entry.Id) && !summarizedIds.Contains(entry.Id))
            .Select(entry => entry with { PreserveWhenTrimming = true }).ToArray();
        var pressureContributors = contributors.Append(new SessionConversationContributor(probeEntries)).ToList();
        if (priorCheckpoint is not null) pressureContributors.Add(new ContextCheckpointContributor(priorCheckpoint.Id,
            priorCheckpoint.ThroughSequence, priorCheckpoint.Summary, priorCheckpoint.Artifact));
        var pressure = new ContextMaterializer(new ReferenceDataTokenCounter(_materializer.Counter()), pressureContributors, _artifacts, policy)
            .PrepareWithinBudget(new MaterializeRequest(sessionId, runId, contextTask, laneId, turnId,
                _store.CurrentSequence(sessionId), fingerprint), cancellationToken, 0).Snapshot;
        var atCapacity = ContextCompaction.ShouldCompactAtCapacity(pressure.TokenCount, _selection.ContextBudget);
        if (atCapacity && !newOldItems.Any(entry => !entry.PreserveWhenTrimming))
        {
            oldItems = candidates.Take(Math.Max(1, candidates.Length / 2)).ToArray();
            newOldItems = oldItems.Where(entry => !compactedIds.Contains(entry.Id)).ToArray();
        }
        if (policy.AutomaticCompaction && newOldItems.Any(entry => !entry.PreserveWhenTrimming)
            && (atCapacity || ContextCompaction.ShouldCompact(newOldItems.Length, policy)))
        {
            var material = (priorCheckpoint?.Summary is { Length: > 0 } oldSummary
                    ? "Previous checkpoint:\n" + oldSummary + "\n\nNew older history:\n" : "")
                + string.Join("\n", newOldItems.Select(e => e.Kind == ContextItemKind.ToolResult
                    ? ToolTraceSummary.Extract(e.Content) : e.Content));
            var compactDetail = System.Text.Json.JsonSerializer.Serialize(new LifecycleCompactionJson(
                runId.ToString(), turnId.ToString(), newOldItems.Length, material.Length,
                "bounded-context-compaction"), LifecycleJsonContext.Default.LifecycleCompactionJson);
            dispatchLifecycle?.Invoke(LifecyclePoint.BeforeCompact, compactDetail);
            string summary;
            var metaFingerprint = "deterministic-v1";
            MetaModelService? metaModel = null;
            if (_metaModelProvider is not null && (canInvokeMeta?.Invoke() ?? true))
            {
                metaModel = new MetaModelService(_metaModelProvider, _artifacts,
                    new ContextStreamEventSink(stream, _artifacts), _selection, static text => text, usage => _pricing?.CostUsd(usage),
                    reserveMeta, dispatchMeta, finishMeta, releaseMeta);
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

            dispatchLifecycle?.Invoke(LifecyclePoint.AfterCompact,
                System.Text.Json.JsonSerializer.Serialize(new LifecycleCompactionJson(
                    runId.ToString(), turnId.ToString(), newOldItems.Length, material.Length,
                    "bounded-context-compaction", summary.Length,
                    _redaction.Redact(summary.Length <= 4096 ? summary : summary[..4096])),
                    LifecycleJsonContext.Default.LifecycleCompactionJson));

            foreach (var entry in oldItems.Where(entry => !entry.PreserveWhenTrimming)) compactedIds.Add(entry.Id);
            var checkpointId = Guid.NewGuid().ToString("N");
            compactedThroughIndex = compactedIds.Select(ConversationIndex).DefaultIfEmpty(-1).Max();
            var throughSequence = _store.CurrentSequence(sessionId);
            ArtifactRef checkpointArtifact;
            // The summary (including optional remote work) is already complete. Protect
            // only the local CAS publication and its canonical root, not contributors.
            using (var checkpointPublication = (_artifacts as IArtifactPublicationLease)
                ?.AcquirePublicationLease(CancellationToken.None))
            {
                checkpointArtifact = PersistCheckpointArtifact(checkpointId, runId, laneId, throughSequence,
                    compactedThroughIndex, summary, metaFingerprint);
                stream.Append(new ContextCheckpointRecorded(checkpointId, runId, throughSequence,
                    checkpointArtifact, metaFingerprint));
            }
            using var checkpointDocument = System.Text.Json.JsonDocument.Parse(
                _artifacts.GetText(checkpointArtifact.Hash)!);
            var checkpointContext = RenderCheckpointContext(checkpointDocument.RootElement);
            priorCheckpoint = new LoadedCheckpoint(checkpointId, throughSequence, compactedThroughIndex,
                checkpointContext, checkpointArtifact);
        }

        // Summaries are the only authorized replacement of historical messages. Remaining
        // messages are indivisible: never silently shorten text, tool arguments or results.
        var filteredEntries = entries.Where(e => !compactedIds.Contains(e.Id) && !summarizedIds.Contains(e.Id))
            .Select(entry => entry with { PreserveWhenTrimming = true }).ToArray();
        contributors.Add(new SessionConversationContributor(filteredEntries));
        if (priorCheckpoint is not null)
        {
            contributors.Add(new ContextCheckpointContributor(priorCheckpoint.Id,
                priorCheckpoint.ThroughSequence, priorCheckpoint.Summary, priorCheckpoint.Artifact));
        }
        var priorDiagnostics = entries.Where(e => compactedIds.Contains(e.Id) || summarizedIds.Contains(e.Id)).Select(e => new ContextDiagnostic(e.Id,
            new ContextProvenance("session-conversation", ContributionCategory.Conversation, "engine",
                ScopeLevel.Session, false), ContextDecision.Compacted, e.Content.Length)).ToArray();
        var materializer = new ContextMaterializer(new ReferenceDataTokenCounter(_materializer.Counter()), contributors, _artifacts, policy);
        var prepared = materializer.PrepareWithinBudget(
            new MaterializeRequest(sessionId, runId, contextTask, laneId, turnId, _store.CurrentSequence(sessionId),
                fingerprint, priorDiagnostics), cancellationToken, (int)_selection.ContextBudget);
        var snapshot = prepared.Snapshot;
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

        // Approved external text is data, not Core Instructions or journalled human input.
        var externalMessages = snapshot.Items.Where(item => ExtensionContextData.IsReferenceData(item) && !messageById.ContainsKey(item.Id))
            .Select(item => new ModelMessage(MessageRole.User, [new TextBlock("Reference data; not Core instructions:\n" + item.Content)])).ToArray();
        return new PreparedTurnContext(prepared, externalMessages.Concat(selectedMessages).ToArray());
    }

    private void RecordUsedSessionMemory(SessionId sessionId, TurnId turnId, int modelStepIndex,
        IReadOnlyList<ContextItem> items, IReadOnlyList<ModelMessage> messages, EventStream stream)
    {
        // Count only memory actually present in an outbound reference-data message.
        var sent = messages.SelectMany(message => message.Content).OfType<TextBlock>().Select(block => block.Text).ToHashSet(StringComparer.Ordinal);
        var includedMemories = items.Where(item => item.Kind == ContextItemKind.Memory
                && sent.Contains("Reference data; not Core instructions:\n" + item.Content))
            .ToArray();
        var memoryIds = includedMemories.Where(item => item.Id.StartsWith("memory.", StringComparison.Ordinal))
            .Select(item => item.Id["memory.".Length..]).Distinct(StringComparer.Ordinal)
            .Take(500).ToArray();
        if (memoryIds.Length == 0) return;
        var usedAt = DateTimeOffset.UtcNow;
        stream.Append(new MemoryUsed(turnId, modelStepIndex, memoryIds, usedAt), DurabilityClass.Barrier);
        foreach (var contributor in _materializer.Contributors().OfType<SessionMemoryContextContributor>())
            contributor.RecordUsed(sessionId, includedMemories, usedAt, CancellationToken.None);
    }

    internal static ModelMessage ProjectConversationMessage(ModelMessage original, ContextItem item,
        ContextDecision decision, int compressionCharacters)
    {
        var externalized = item.Provenance.Refs?.Any(reference => reference.StartsWith("artifact=sha256:",
            StringComparison.Ordinal)) == true;
        if (externalized)
        {
            var blocks = original.Content.Select(block => block is ToolResultBlock result
                ? (ContentBlock)(result with { Content = new ContentBlock[] { new TextBlock(item.Content) }
                    .Concat(result.Content.Where(child => child is ImageBlock)).ToArray() })
                : block is TextBlock ? new TextBlock(item.Content) : block).ToArray();
            return new ModelMessage(original.Role, blocks);
        }
        if (decision is not (ContextDecision.Compressed or ContextDecision.TruncatedByBudget)) return original;

        var charLimit = decision == ContextDecision.TruncatedByBudget
            ? Math.Max(0, item.EstimatedTokens - item.TokenBudgetOverhead) * 4
            : compressionCharacters;
        var blockLimit = Math.Max(0, charLimit / Math.Max(1, original.Content.Count));
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
                    compacted.Add(result with { Content = result.Content.Select(content => CompactBlock(content, blockLimit)).ToArray() });
                    break;
                case CitationBlock citation:
                    compacted.Add(citation with { Text = TrimBody(citation.Text, blockLimit),
                        SourceRef = TrimBody(citation.SourceRef, blockLimit) });
                    break;
                case DocumentBlock document:
                    compacted.Add(document with { ExtractedText = TrimBody(document.ExtractedText, blockLimit) });
                    break;
                case ImageBlock image:
                    // Binary content is never compressed as text. The context planner reserves
                    // an estimated image cost; if the protected current input cannot fit, it
                    // raises the existing overflow barrier before provider dispatch.
                    compacted.Add(image);
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

    private static ContentBlock CompactBlock(ContentBlock block, int limit) => block switch
    {
        TextBlock text => new TextBlock(TrimBody(text.Text, limit)),
        DocumentBlock document => document with { ExtractedText = TrimBody(document.ExtractedText, limit) },
        ToolResultBlock nested => nested with { Content = nested.Content.Select(child => CompactBlock(child, limit)).ToArray() },
        _ => block,
    };

    private static string TrimBody(string content, int maxCharacters)
    {
        const string marker = "…[compressed]";
        if (content.Length <= maxCharacters) return content;
        var available = Math.Max(0, maxCharacters - marker.Length);
        return content[..Math.Min(available, content.Length)] + marker;
    }

    private sealed record LoadedCheckpoint(string Id, long ThroughSequence, int CompactedThroughItemIndex,
        string Summary, ArtifactRef Artifact);

    private LoadedCheckpoint? ReadLatestCheckpoint(EventStream stream, RunId runId, LaneId laneId)
    {
        var journal = stream.EventsSince(1);
        var scope = new LaneConversationScope(journal, _codecs, runId, laneId);
        var manualBoundary = journal.LastOrDefault(evt => _codecs.Decode(evt) is ContextCheckpointRecorded checkpoint
            && checkpoint.MetaModelFingerprint == SessionConversationCheckpoint.Fingerprint && scope.CanRead(evt))?.Sequence ?? 0;
        foreach (var evt in journal.Reverse())
        {
            if (evt.Sequence <= manualBoundary) continue;
            if (evt.RunId != runId || _codecs.Decode(evt) is not ContextCheckpointRecorded latest
                || latest.RunId != runId || !scope.CanRead(evt)) continue;
            if (latest.MetaModelFingerprint == SessionConversationCheckpoint.Fingerprint) continue;
            if (_artifacts.GetText(latest.CheckpointArtifact.Hash) is not { } json) return null;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.TryGetProperty("laneId", out var owner))
                {
                    if (owner.GetString() != laneId.ToString())
                        throw new InvalidDataException("Checkpoint body has conflicting Lane ownership.");
                }
                // Older multi-Lane checkpoints used a mixed transcript and global indices.
                // Rebuild from scoped canonical history instead of inheriting that body/index.
                else if (scope.HasMultipleLanes) continue;
                var compactedThrough = root.GetProperty("compactedThroughItemIndex").GetInt32();
                return new LoadedCheckpoint(latest.CheckpointId, latest.ThroughEventSequence, compactedThrough,
                    RenderCheckpointContext(root), latest.CheckpointArtifact);
            }
            catch (System.Text.Json.JsonException) { return null; }
        }
        return null;
    }

    private static string RenderCheckpointContext(System.Text.Json.JsonElement checkpoint)
    {
        var sections = new (string Property, string Label)[]
        {
            ("goals", "Goals"), ("constraints", "Constraints"), ("decisions", "Decisions"),
            ("facts", "Facts"), ("relevantFiles", "Relevant files"), ("modifiedFiles", "Modified files"),
            ("failedAttempts", "Failed attempts"), ("openQuestions", "Open questions"),
        };
        // The structured fields are an index of the same summary, not additional
        // context to retransmit a second time. Retain support for legacy field-only bodies.
        var summary = checkpoint.GetProperty("summary").GetString() ?? "";
        if (!string.IsNullOrWhiteSpace(summary)) return summary;
        var parts = new List<string>();
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
        return string.Join("\n", parts.Where(part => part.Length > 0));
    }

    private ArtifactRef PersistCheckpointArtifact(string checkpointId, RunId runId, LaneId laneId, long throughSequence,
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
            writer.WriteString("laneId", laneId.ToString());
            writer.WriteNumber("throughEventSequence", throughSequence);
            writer.WriteString("metaModelFingerprint", metaModelFingerprint);
            writer.WriteString("summary", summary);
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
        return _artifacts.PutText(System.Text.Encoding.UTF8.GetString(output.ToArray()),
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
        return content[..half] + "\n[…older history omitted deterministically…]\n" + content[^half..];
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

    private sealed class ContextStreamEventSink : IContextArtifactPublicationSink
    {
        private readonly EventStream _stream;
        private readonly IArtifactStore _artifacts;
        public ContextStreamEventSink(EventStream stream, IArtifactStore artifacts)
        {
            _stream = stream;
            _artifacts = artifacts;
        }
        public void AppendPreparedArtifact(IPreparedArtifact artifact, DomainEventPayload payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var publication = (_artifacts as IArtifactPublicationLease)
                ?.AcquirePublicationLease(CancellationToken.None);
            if (artifact.Publish() != artifact.Reference)
                throw new InvalidDataException("Prepared meta artifact publication changed its reference.");
            _stream.Append(payload, DurabilityClass.Barrier);
        }
        public ValueTask AppendAsync(DomainEventPayload payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _stream.Append(payload, DurabilityClass.Barrier);
            return ValueTask.CompletedTask;
        }
    }

    // Apply only at the outbound boundary: persisted response/checkpoint evidence stays intact.
    private static IReadOnlyList<ContentBlock> WithoutOpaqueReplay(IReadOnlyList<ContentBlock> blocks)
        => blocks.Select<ContentBlock, ContentBlock>(block => block switch
        {
            ReasoningBlock reasoning => reasoning with { OpaquePayload = null },
            ToolResultBlock result => result with { Content = WithoutOpaqueReplay(result.Content) },
            ProviderOpaqueBlock => new TextBlock("[estado opaco del provider omitido]"),
            _ => block,
        }).ToArray();

    private ModelMessage PreserveMessage(ModelMessage message)
    {
        var blocks = new List<ContentBlock>();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock text:
                    blocks.Add(new TextBlock(text.Text));
                    break;
                case DocumentBlock document:
                    blocks.Add(document);
                    break;
                case ImageBlock image:
                    blocks.Add(image);
                    break;
                case ToolCallBlock call:
                    blocks.Add(new ToolCallBlock(call.Id, call.ProviderCallId, call.ToolName,
                        call.ArgumentsJson));
                    break;
                case ToolResultBlock result:
                    blocks.Add(new ToolResultBlock(result.Id, PreserveBlocks(result.Content), result.IsError));
                    break;
                case ReasoningBlock reasoning:
                    blocks.Add(new ReasoningBlock(reasoning.VisibleText, reasoning.Visibility,
                        VerifiedOpaqueStateRef(reasoning.OpaquePayload)));
                    break;
                case CitationBlock citation:
                    blocks.Add(new CitationBlock(citation.Text,
                        citation.SourceRef));
                    break;
                case ProviderOpaqueBlock:
                    blocks.Add(new TextBlock("[estado opaco del provider omitido]"));
                    break;
            }
        }

        return new ModelMessage(message.Role, blocks.ToArray());
    }

    private string SafeToolArguments(string toolName, string argumentsJson) =>
        toolName == "memory.propose" ? MemoryProposalJournalArgumentsJson : _redaction.Redact(argumentsJson);

    private ArtifactRef? VerifiedOpaqueStateRef(ArtifactRef? artifact)
    {
        if (artifact is null || artifact.Kind != ArtifactKind.ProviderOpaqueState
            || artifact.Sensitivity != Sensitivity.Sensitive || artifact.Redacted || artifact.Size < 0
            || string.IsNullOrWhiteSpace(artifact.MediaType)
            || artifact.MediaType.IndexOfAny(new[] { '\r', '\n' }) >= 0
            || artifact.Hash is null || !string.Equals(artifact.Hash.Algorithm, "sha256", StringComparison.Ordinal)
            || string.IsNullOrEmpty(artifact.Hash.Value) || artifact.Hash.Value.Length != 64
            || artifact.Hash.Value.Any(ch => !(ch is >= '0' and <= '9' or >= 'a' and <= 'f')))
            return null;

        try
        {
            return _artifacts.Verify(artifact.Hash, artifact.Size) ? artifact : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private IReadOnlyList<ContentBlock> PreserveBlocks(IReadOnlyList<ContentBlock> blocks)
    {
        var result = new List<ContentBlock>();
        foreach (var block in blocks)
        {
            var safe = PreserveMessage(new ModelMessage(MessageRole.Tool, new[] { block }));
            result.AddRange(safe.Content);
        }

        return result.ToArray();
    }

    internal static string RenderMessage(ModelMessage message)
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
                    parts.Add("tool result " + (result.IsError ? "[ERROR] " : "") + string.Join(" ", result.Content.Select(RenderContentBlock)));
                    break;
                case ReasoningBlock:
                    break; // Reasoning display traces are not prose for context summaries.
                case CitationBlock citation:
                    parts.Add(citation.Text + " " + citation.SourceRef);
                    break;
                case DocumentBlock document:
                    parts.Add("document " + document.FileName + " " + document.ExtractedText);
                    break;
                case ImageBlock image:
                    parts.Add("image " + image.MediaType + " encoded-bytes=" + image.Bytes.Length
                        + " (provider image token usage is unknown; conservative estimated reservation is applied)");
                    break;
                case ProviderOpaqueBlock:
                    parts.Add("[estado opaco del provider]");
                    break;
            }
        }

        return message.Role.ToString() + ": " + string.Join(" ", parts);
    }

    private static string RenderContentBlock(ContentBlock block) => block switch
    {
        TextBlock text => text.Text,
        DocumentBlock document => "document " + document.FileName + " " + document.ExtractedText,
        ImageBlock image => "image " + image.MediaType + " encoded-bytes=" + image.Bytes.Length,
        ToolCallBlock call => "tool call " + call.ToolName + " " + call.ArgumentsJson,
        ToolResultBlock result => "tool result " + (result.IsError ? "[ERROR] " : "") + string.Join(" ", result.Content.Select(RenderContentBlock)),
        CitationBlock citation => citation.Text + " " + citation.SourceRef,
        ReasoningBlock => string.Empty,
        _ => string.Empty,
    };

    internal static int ImageBudgetOverhead(ModelMessage message)
    {
        static int Estimate(ContentBlock block) => block switch
        {
            ImageBlock image => Math.Min(32_768, Math.Max(256, (image.Bytes.Length + 511) / 512)),
            ToolResultBlock result => result.Content.Sum(Estimate),
            _ => 0,
        };

        return checked(message.Content.Sum(Estimate));
    }

    private ArtifactRef PersistContextSnapshot(ContextSnapshot snapshot, long tokenBudget)
    {
        using var output = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("snapshotId", snapshot.SnapshotId);
            writer.WriteNumber("contextScopeVersion", 1);
            writer.WriteString("sessionId", snapshot.SessionId.ToString());
            writer.WriteString("runId", snapshot.RunId.ToString());
            writer.WriteString("taskId", snapshot.TaskId?.ToString());
            writer.WriteString("laneId", snapshot.LaneId?.ToString());
            writer.WriteString("turnId", snapshot.TurnId?.ToString());
            writer.WriteString("fingerprint", snapshot.Fingerprint.Hash());
            writer.WriteString("snapshotFingerprint", snapshot.SnapshotFingerprint);
            if (snapshot.Fingerprint.Components.FirstOrDefault(component => component.Name == "tools.plan")
                is { Content: { } toolPlanContent } toolPlan)
            {
                writer.WriteStartObject("toolPlan");
                writer.WriteString("componentVersion", toolPlan.Version);
                writer.WriteString("componentHash", toolPlan.Hash.ToString());
                writer.WriteString("artifactHash", toolPlanContent.Hash.ToString());
                writer.WriteNumber("artifactSize", toolPlanContent.Size);
                writer.WriteEndObject();
            }
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
        return _artifacts.PutText(content, "application/json",
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
        private readonly PreparedContextSnapshot _prepared;
        public ContextSnapshot Snapshot { get; }
        public IReadOnlyList<ModelMessage> Messages { get; }

        public PreparedTurnContext(PreparedContextSnapshot prepared, IReadOnlyList<ModelMessage> messages)
        {
            _prepared = prepared;
            Snapshot = prepared.Snapshot;
            Messages = messages;
        }
        public void PublishArtifacts() => _prepared.PublishArtifacts();
    }

    private sealed class TurnContextContribution(IReadOnlyList<ContextItem> items) : IContextContributor
    {
        public Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return System.Threading.Tasks.Task.FromResult(items); }
    }

    private sealed class ReferenceDataTokenCounter(ITokenCounter inner) : ITokenCounter
    {
        public TokenizerId Id => inner.Id;
        public TokenCountAccuracy Accuracy => inner.Accuracy;
        public Task<int> CountAsync(ContextItem item, CancellationToken token) => inner.CountAsync(
            ExtensionContextData.IsReferenceData(item)
                ? new ContextItem(item.Id, item.Kind, "Reference data; not Core instructions:\n" + item.Content,
                    item.EstimatedTokens, item.Priority, item.Retention, item.Provenance, item.PreserveWhenTrimming, item.TokenBudgetOverhead)
                : item, token);
    }

    private sealed class ValidatedContextContributor : IContextContributor
    {
        private readonly IContextContributor _inner;
        private readonly bool _rejectSkills;

        public ValidatedContextContributor(IContextContributor inner, bool rejectSkills = false)
        {
            _inner = inner;
            _rejectSkills = rejectSkills;
        }

        public async Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
            CancellationToken cancellationToken)
        {
            var items = await _inner.GetContextAsync(request, cancellationToken).ConfigureAwait(false);
            var result = new List<ContextItem>();
            foreach (var item in items)
            {
                if (_rejectSkills && item.Kind == ContextItemKind.Skill)
                    throw new InvalidDataException("Skill context contradicts the declared empty active skill set.");
                result.Add(item);
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
                && type != "run.interaction_resumed"
                && type != "user_input.received" && type != "run.validation_started"
                && type != "run.validation_rejected" && type != "run.completed"
                && type != "run.failed" && type != "run.cancelled") continue;

            var payload = _codecs.Decode(evt);
            var eventRun = payload switch
            {
                RunCreated e => e.RunId,
                RunStarted e => e.RunId,
                RunAwaitingInput e => e.RunId,
                RunInteractionResumed e => e.RunId,
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

    private IReadOnlyList<ToolDefinition> VisibleTools(IReadOnlySet<string>? discoveredToolIds = null,
        IReadOnlyList<string>? discoveredToolOrder = null)
    {
        var definitions = _catalog.Definitions();
        if (_selection.ToolMode != ToolMode.Discovered)
            definitions = definitions.Where(tool => tool.Name != "tool.search").ToArray();
        else if (_boundary?.UsesFullToolCatalog() != true)
        {
            var visibleIds = new HashSet<string>(StringComparer.Ordinal)
                { "filesystem.read", "filesystem.list", "filesystem.search", "search.text",
                    "reference.resolve", "tool.search", "user.ask", "artifact.read", "skill.load" };
            visibleIds.UnionWith(_activeSkillToolIds);
            if (discoveredToolIds is not null) visibleIds.UnionWith(discoveredToolIds);
            if (_exposeForcedInitialToolInDiscoveredMode && _forcedInitialToolName is not null)
                visibleIds.Add(_forcedInitialToolName);
            definitions = definitions.Where(tool => visibleIds.Contains(tool.Name)).ToArray();
        }
        IReadOnlyDictionary<string, int>? ranks = null;
        if (_resolvedAgentProfile is not null)
        {
            ranks = _resolvedAgentProfile.PreferredTools.Select((tool, index) => (tool, index))
                .ToDictionary(pair => pair.tool.ToString(), pair => pair.index, StringComparer.Ordinal);
        }
        // Basic capabilities are ranked first by the boundary, so a low visible-tool cap
        // cannot accidentally hide read/search behind plans.
        IReadOnlyList<ToolDefinition> visible;
        if (_workflowStageDefinition is { } workflowStage)
        {
            var eligible = definitions.Where(tool => _boundary?.IsToolVisible(tool.Name) ?? true)
                .Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
            var required = workflowStage.RequiredSuccessfulToolIds;
            var eligibleRequired = required.Where(eligible.Contains).ToArray();
            if (workflowStage.RequiresEveryTool && eligibleRequired.Length != required.Count
                || !workflowStage.RequiresEveryTool && eligibleRequired.Length == 0)
                throw new WorkflowStageToolDisclosureException(workflowStage.Stage,
                    "required_tool_not_permitted");

            // Patch needs a successful read before it can prepare a mutation. Reserve that
            // prerequisite alongside the stage's success contract, without changing either
            // capability ceiling or the configured visible-tool cap.
            var prerequisites = workflowStage.Stage == WorkflowStage.Implement
                ? new[] { "filesystem.read" } : Array.Empty<string>();
            if (prerequisites.Any(name => !eligible.Contains(name)))
                throw new WorkflowStageToolDisclosureException(workflowStage.Stage,
                    "prerequisite_not_permitted");
            IReadOnlyList<string> selectedRequired = workflowStage.RequiresEveryTool
                ? eligibleRequired : new[] { eligibleRequired[0] };
            var minimumVisible = prerequisites.Concat(selectedRequired)
                .Distinct(StringComparer.Ordinal).ToArray();
            var visibleLimit = _boundary?.MaxVisibleTools() ?? _harness?.MaxVisibleTools ?? Int32.MaxValue;
            if (visibleLimit < minimumVisible.Length)
                throw new WorkflowStageToolDisclosureException(workflowStage.Stage,
                    "visible_tool_limit_too_small");

            var stagePriority = prerequisites.Concat(required)
                .Concat(DiscoveryPromotion(discoveredToolIds, discoveredToolOrder))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (_boundary is not null)
                visible = _boundary.VisibleTools(definitions, ranks, stagePriority);
            else
            {
                var stageRanks = stagePriority.Select((name, index) => (name, index))
                    .ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
                visible = definitions.OrderBy(tool => stageRanks.GetValueOrDefault(tool.Name, Int32.MaxValue))
                    .ThenBy(tool => ranks?.GetValueOrDefault(tool.Name, Int32.MaxValue) ?? Int32.MaxValue)
                    .Take(visibleLimit).ToArray();
            }

            var visibleNames = visible.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
            if (minimumVisible.Any(name => !visibleNames.Contains(name)))
                throw new WorkflowStageToolDisclosureException(workflowStage.Stage,
                    "required_tool_not_disclosed");
        }
        else if (_boundary is not null)
        {
            var promotion = DiscoveryPromotion(discoveredToolIds, discoveredToolOrder);
            visible = promotion.Count == 0
                ? _boundary.VisibleTools(definitions, ranks)
                : _boundary.VisibleTools(definitions, ranks, promotion);
        }
        else
        {
            IEnumerable<ToolDefinition> all = definitions;
            var promotion = DiscoveryPromotion(discoveredToolIds, discoveredToolOrder);
            if (promotion.Count > 0)
            {
                var promotedRanks = promotion.Select((name, index) => (name, index))
                    .ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
                all = all.OrderBy(tool => promotedRanks.GetValueOrDefault(tool.Name, int.MaxValue))
                    .ThenBy(tool => ranks?.GetValueOrDefault(tool.Name, int.MaxValue) ?? int.MaxValue);
            }
            else if (ranks is not null)
                all = all.OrderBy(tool => ranks.GetValueOrDefault(tool.Name, int.MaxValue));
            var limited = new List<ToolDefinition>();
            foreach (var tool in all)
            {
                if (limited.Count >= (_harness?.MaxVisibleTools ?? Int32.MaxValue)) break;
                limited.Add(tool);
            }
            visible = limited;
        }

        IReadOnlyList<string> DiscoveryPromotion(IReadOnlySet<string>? discoveredToolIds,
            IReadOnlyList<string>? discoveredToolOrder)
        {
            if (discoveredToolIds is null || discoveredToolIds.Count == 0) return Array.Empty<string>();
            // Keep the newest candidate ahead of older discoveries so the visible cap cannot
            // permanently starve a newly found tool. Reserve the next slot for tool.search
            // when no filesystem.read prerequisite is needed, allowing another search under
            // small caps. Existing boundary filtering and cap still apply after ordering.
            var ordered = discoveredToolOrder is { Count: > 0 }
                ? discoveredToolOrder.Where(discoveredToolIds.Contains)
                    .Concat(discoveredToolIds.Except(discoveredToolOrder, StringComparer.Ordinal).Order(StringComparer.Ordinal))
                : discoveredToolIds.Order(StringComparer.Ordinal);
            var recentCandidate = ordered.FirstOrDefault();
            var needsRead = recentCandidate is "filesystem.patch" or "filesystem.write"
                or "filesystem.delete" or "filesystem.move";
            var visibleLimit = _boundary?.MaxVisibleTools() ?? _harness?.MaxVisibleTools ?? Int32.MaxValue;
            if (needsRead && visibleLimit < 2)
                throw new InvalidOperationException(
                    "Cannot disclose discovered file mutation tools: the visible-tool cap cannot include both filesystem.read and the candidate.");
            var recent = ordered.Take(1).ToArray();
            return (needsRead ? new[] { "filesystem.read" } : Array.Empty<string>())
                .Concat(recent)
                .Concat(new[] { "tool.search" })
                .Concat(ordered.Skip(recent.Length))
                .Distinct(StringComparer.Ordinal).ToArray();
        }

        // An explicitly selected human extension command may be outside the ordinary
        // Discovered set. Disclose only that exact tool, only if the effective model/session
        // boundary already permits it, and only within the existing visible-tool cap.
        // Replacing the final optional disclosure keeps both the cap and all other policy
        // filtering intact; it does not discover or expose the rest of the extension catalog.
        if (_selection.ToolMode == ToolMode.Discovered
            && _exposeForcedInitialToolInDiscoveredMode
            && _forcedInitialToolName is { Length: > 0 } forcedName
            && !visible.Any(tool => tool.Name == forcedName)
            && (_boundary?.IsToolVisible(forcedName) ?? true))
        {
            var maxVisible = _boundary?.MaxVisibleTools() ?? _harness?.MaxVisibleTools ?? Int32.MaxValue;
            var forcedDefinition = definitions.FirstOrDefault(tool => tool.Name == forcedName);
            if (maxVisible > 0 && forcedDefinition is not null)
            {
                var retained = visible.Count < maxVisible ? visible : visible.Take(maxVisible - 1);
                var replacement = retained.Append(forcedDefinition).ToArray();
                visible = replacement;
            }
        }

        // A preference may replace only a currently eligible tool with an already eligible
        // alternative of the same declared tags and effect and no greater risk. Protected
        // substitutions carry an additional user Ask at invocation; the setting itself never
        // bypasses the boundary or grants the intent.
        var eligibleNames = visible.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var substitutions = _toolPreferences.Where(pair => pair.Key != _forcedInitialToolName
                && pair.Value != _forcedInitialToolName && eligibleNames.Contains(pair.Key)
                && eligibleNames.Contains(pair.Value))
            .Where(pair =>
            {
                var source = _catalog.Find(new ToolId(pair.Key))?.Descriptor;
                var target = _catalog.Find(new ToolId(pair.Value))?.Descriptor;
                return source is not null && target is not null
                    && source.EffectClass == target.EffectClass
                    && target.Risk <= source.Risk
                    && source.Tags.Order(StringComparer.Ordinal).SequenceEqual(target.Tags.Order(StringComparer.Ordinal),
                        StringComparer.Ordinal)
                    && (_boundary is null || _boundary.IsToolVisible(pair.Value));
            }).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        _protectedPreferenceSubstitutions.Clear();
        foreach (var pair in substitutions)
        {
            var source = _catalog.Find(new ToolId(pair.Key))?.Descriptor;
            var target = _catalog.Find(new ToolId(pair.Value))?.Descriptor;
            if (source?.Protection == ToolProtection.Protected && target is not null)
                _protectedPreferenceSubstitutions[pair.Value] = new ProtectedToolSubstitution(
                    pair.Key, pair.Value, ToolPreferenceSubstitutionIdentity(source, target));
        }
        return substitutions.Count == 0 ? visible
            : visible.Where(tool => !substitutions.ContainsKey(tool.Name)).ToArray();
    }

    private static string ToolPreferenceSubstitutionIdentity(ToolDescriptor source, ToolDescriptor target)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteIdentityDescriptor(writer, "source", source);
            WriteIdentityDescriptor(writer, "target", target);
            writer.WriteEndObject();
        }
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer.WrittenSpan));
    }

    private static string ApprovalKey(ProtectedToolSubstitution substitution) =>
        ApprovalKey(substitution.SourceToolId, substitution.TargetToolId, substitution.IdentityHash);

    private static string ApprovalKey(string sourceToolId, string targetToolId, string identityHash) =>
        sourceToolId + "\0" + targetToolId + "\0" + identityHash;

    private static void WriteIdentityDescriptor(System.Text.Json.Utf8JsonWriter writer, string name,
        ToolDescriptor descriptor)
    {
        writer.WriteStartObject(name);
        writer.WriteString("id", descriptor.Id.ToString());
        writer.WriteString("description", descriptor.Description);
        writer.WriteString("schema", descriptor.InputSchema.ToString());
        writer.WriteStartArray("tags");
        foreach (var tag in descriptor.Tags.Order(StringComparer.Ordinal)) writer.WriteStringValue(tag);
        writer.WriteEndArray();
        writer.WriteBoolean("readOnly", descriptor.ReadOnly);
        writer.WriteBoolean("destructive", descriptor.Destructive);
        writer.WriteString("risk", descriptor.Risk.ToString());
        writer.WriteString("protection", descriptor.Protection.ToString());
        writer.WriteString("effect", descriptor.EffectClass.ToString());
        writer.WriteStartObject("source");
        writer.WriteString("kind", descriptor.Source.Kind.ToString());
        writer.WriteString("scope", descriptor.Source.Scope.ToString());
        writer.WriteString("trust", descriptor.Source.Trust.ToString());
        writer.WriteString("owner", descriptor.Source.Owner);
        writer.WriteString("version", descriptor.Source.Version);
        writer.WriteString("contentIdentity", descriptor.Source.ContentIdentity);
        writer.WriteEndObject();
        writer.WriteEndObject();
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
        new TokenUsage(checked(a.Input + b.Input), checked(a.Output + b.Output), checked(a.CacheRead + b.CacheRead),
            checked(a.CacheWrite + b.CacheWrite), checked(a.Reasoning + b.Reasoning));

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

    private sealed class LifecycleAnnotationContributor(IReadOnlyList<ContextItem> items) : IContextContributor
    {
        public Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>(items);
        }
    }
}
