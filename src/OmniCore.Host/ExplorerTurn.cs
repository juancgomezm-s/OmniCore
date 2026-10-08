namespace OmniCore.Host;

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
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IEventStore,
        System.Collections.Concurrent.ConcurrentDictionary<(SessionId, RunId), SemaphoreSlim>> RunExecutionGates = new();
    public static readonly int MaxSteps = 8;

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
    private readonly AgentProfile? _resolvedAgentProfile;

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

    /// <summary>
    /// Frontera de capacidad del modelo (ADR-0044 §5): el ToolPlanner (VisibleTools) oculta las
    /// tools fuera del techo y el ToolRuntime la re-valida antes de permisos y antes de ejecutar.
    /// La frontera restringe; jamás autoriza. null = sin frontera (semántica M2). La política
    /// efectiva ya está intersectada con el harness por el resolver.
    /// </summary>
    private readonly ModelCapabilityBoundary? _boundary;

    private readonly ModelPricing? _pricing;

    private readonly bool _enforceDefaultSpendCaps;
    private readonly Func<SessionId, RunId, LaneId, TurnId, int, InteractionId?>? _quotaAdmission;
    private readonly Func<bool>? _quotaAllowsMeta;

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
        IModelProvider? metaModelProvider = null, long? modelContextCapacity = null,
        bool recordEffectiveFingerprint = false, UserWorkspaceSpendReader? userSpendReader = null,
        IReadOnlyList<ActiveSkillFingerprint>? activeSkills = null,
        Func<SessionId, RunId, LaneId, TurnId, int, InteractionId?>? quotaAdmission = null,
        Func<bool>? quotaAllowsMeta = null,
        SqliteSpendReservationStore? spendReservations = null,
        long? maximumGenerationRequestAttempts = null,
        IReadOnlyList<IPreparedArtifact>? fingerprintArtifacts = null)
    {
        if (sessionCapUsd < 0m) throw new ArgumentOutOfRangeException(nameof(sessionCapUsd));
        if (dailyCapUsd < 0m) throw new ArgumentOutOfRangeException(nameof(dailyCapUsd));
        _complete = complete;
        _tools = tools;
        _resolvedAgentProfile = (tools as ScriptedToolExecutor)?.AgentProfile;
        _catalog = catalog;
        _materializer = materializer;
        _fingerprint = fingerprint;
        _fingerprintArtifacts = fingerprintArtifacts?.ToArray() ?? Array.Empty<IPreparedArtifact>();
        _recordEffectiveFingerprint = recordEffectiveFingerprint;
        _activeSkills = activeSkills?.ToArray();
        _selection = selection;
        _store = store;
        _codecs = codecs;
        _artifacts = artifacts;
        _userSpendReader = userSpendReader;
        _spendReservations = spendReservations ?? (enforceDefaultSpendCaps && userSpendReader is not null
            ? new SqliteSpendReservationStore(Path.Combine(userSpendReader.UserDataDirectory, "spend-reservations.db")) : null);
        _maximumGenerationRequestAttempts = maximumGenerationRequestAttempts;
        _audit = audit;
        _redaction = redaction;
        _harness = harness;
        _boundary = boundary;
        _pricing = pricing;
        _enforceDefaultSpendCaps = enforceDefaultSpendCaps;
        _quotaAdmission = quotaAdmission;
        _quotaAllowsMeta = quotaAllowsMeta;
        _sessionCapUsd = sessionCapUsd;
        _dailyCapUsd = dailyCapUsd;
        _questionnaires = questionnaires;
        _questionnaireResponder = questionnaireResponder;
        _metaModelProvider = metaModelProvider;
        _modelContextCapacity = modelContextCapacity;
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

    /// <summary>
    /// Ejecuta la pregunta y persiste el Turn. turnStarted notifica un nuevo inicio ya durable,
    /// antes del provider; no se invoca al reanudar ni cuando falla la publicación.
    /// El observador interno debe ser no bloqueante y no lanzar excepciones.
    /// </summary>
    public TurnResult Ask(string question, string instruction, SessionId sessionId, RunId runId,
        LaneId laneId, string workingStateText, CancellationToken cancellationToken, string? origin = null,
        TurnInstructionSnapshot? instructionSnapshot = null, Action<TurnStarted>? turnStarted = null)
    {
        // One in-process canonical writer per Run. Independent Explorer instances must not
        // both admit against the same pre-dispatch counters. User commands remain unblocked.
        var gate = RunExecutionGates.GetValue(_store, _ => new()).GetOrAdd((sessionId, runId), _ => new(1, 1));
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
                deadline.Token, origin, instructionSnapshot, start => { exactTurn = start.TurnId; turnStarted?.Invoke(start); });
            return new TurnResult(result.FinalText, result.StopReason, result.Steps, result.Usage,
                result.ToolCalls, result.ResponseArtifactId, result.PendingInteractionId) { TurnId = exactTurn };
        }
        finally { gate.Release(); }
    }

    private TurnResult AskCore(string question, string instruction, SessionId sessionId, RunId runId,
        LaneId laneId, string workingStateText, CancellationToken cancellationToken, string? origin,
        TurnInstructionSnapshot? instructionSnapshot, Action<TurnStarted>? turnStarted)
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
        var originalStart = isResume ? stream.EventsSince(1).LastOrDefault(evt =>
            evt.RunId == runId && _codecs.Decode(evt) is TurnStarted start
                && start.TurnId == resumedTurnId && start.LaneId == laneId) : null;
        var originalConfiguration = originalStart is null ? null : (TurnStarted)_codecs.Decode(originalStart);
        if (instructionSnapshot is not null)
        {
            instructionSnapshot.Validate();
            if (!string.Equals(instructionSnapshot.ResolvedInstruction, instruction, StringComparison.Ordinal))
                throw new ArgumentException("Turn instruction snapshot must match the applied instruction.", nameof(instructionSnapshot));
            // Instructions pass through the same redaction boundary as the materialized prompt.
            // Do not expose an unredacted copy in the canonical event payload.
            instructionSnapshot = instructionSnapshot with { ResolvedInstruction = _redaction.Redact(instruction) };
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
        var preparedTurnFingerprint = _recordEffectiveFingerprint
            ? RuntimeFingerprintFactory.PrepareTurnConfiguration(_fingerprint, _catalog, VisibleTools(),
                EffectiveSystemPrompt(instruction), PlanProjection.Replay(_codecs, initialPlanEvents).Latest(), _artifacts,
                laneProfileId, _activeSkills, _resolvedAgentProfile, fingerprintAuthority, instructionSnapshot,
                reasoningResolution)
            : null;
        var fingerprint = preparedTurnFingerprint?.Fingerprint ?? _fingerprint;
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
        var queuedQuestion = _redaction.Redact(question ?? "").Length > 0
            && FollowUpQueue.TryQueue(_store, _codecs, sessionId, runId, laneId, question ?? "", origin);
        var runEvents = stream.EventsSince(1);
        if (RunProjection.Replay(sessionId, runId, _codecs, runEvents).IsTerminal())
            return new TurnResult("Run is terminal; queued FollowUps remain inert.", StopReason.Error,
                0, new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ToolUseTrace>(), null);
        var turnId = resumedTurnId ?? TurnId.New();
        using var executionScope = ExecutionScope.Begin(new ExecutionScopeState(runId,
            FindTaskForLane(runEvents, runId, laneId), laneId, turnId));
        var nextModelStepIndex = ReadNextModelStepIndex(stream, turnId);
        if (FindPendingRunInteraction(runEvents, runId) is { } pendingInteraction)
        {
            var stop = pendingInteraction.Kind == InteractionKind.BudgetExceeded
                ? StopReason.Cancelled : StopReason.InputRequired;
            return new TurnResult(null, stop, 0,
                new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ToolUseTrace>(), null,
                pendingInteraction.InteractionId);
        }
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var budget = ReadRunBudget(stream, runId);
        // Primary usage for this Ask remains in SpendGuard. Meta usage is reread in full
        // at each boundary, avoiding a gap/overlap between a historical scan and a sequence delta.
        // Known monetary totals must remain representable even without a cap. Only an
        // unpriced session with no historical monetary evidence can omit monetary replay.
        var replayMonetarySpend = _enforceDefaultSpendCaps || budget.MaxCostUsd is not null
            || _pricing is not null || HasHistoricalMonetaryEvidence(runEvents);
        var persistedSpend = replayMonetarySpend
            ? ReadJournalSpend(stream, sessionId, runId, today, _enforceDefaultSpendCaps)
            : new PersistedSpend(0m, 0m, 0m, false);

        List<ModelMessage> messages;
        var messageOwners = new Dictionary<ModelMessage, RunId>(ReferenceEqualityComparer.Instance);
        try
        {
            new RunSummaryService(_store, _codecs, _artifacts, _redaction.Redact).EnsureRecorded(sessionId);
            messages = LoadConversation(stream, runId, isResume ? turnId : null, laneId, messageOwners);
        }
        catch (InvalidDataException)
        {
            return new TurnResult("Persisted visible reasoning is invalid.", StopReason.Error, 0,
                new TokenUsage(0, 0, 0, 0, 0), Array.Empty<ToolUseTrace>(), null);
        }
        var safeQuestion = isResume || queuedQuestion ? "" : _redaction.Redact(question ?? "");
        var pendingFollowUps = isResume || origin == "InteractionResponse(ModelRouteConsent)"
            ? Array.Empty<FollowUpQueue.Item>()
            : FollowUpQueue.Pending(_store, _codecs, sessionId, runId, laneId).Take(1).ToArray();
        foreach (var followUp in pendingFollowUps)
        {
            var text = FollowUpText(followUp.InputPartsJson);
            if (text.Length > 0)
                messages.Add(new ModelMessage(MessageRole.User, new ContentBlock[] { new TextBlock(_redaction.Redact(text)) }));
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
            : CombineSpend(CombineSpend(persistedSpend,
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
            var runCost = AccumulatedSpend(spend.RunUsd, guard.CostUsd(), capped);
            var sessionCost = AccumulatedSpend(spend.SessionUsd, guard.CostUsd(), capped);
            var dailyCost = AccumulatedSpend(spend.DailyUsd, guard.CostUsd(), capped);
            if (budget.MaxCostUsd is not null && runCost >= budget.MaxCostUsd.Value)
                throw new BudgetExceededException("límite de costo de Run alcanzado ($" + budget.MaxCostUsd.Value + ")",
                    new("run", configuredRunCap!.Value, budget.MaxCostUsd.Value, runId.ToString(), null));
            if (_enforceDefaultSpendCaps && sessionCost >= sessionCap)
                throw new BudgetExceededException("límite de sesión alcanzado ($" + sessionCap + ")",
                    new("session", _sessionCapUsd, sessionCap, null, null));
            if (_enforceDefaultSpendCaps && dailyCost >= dailyCap)
                throw new BudgetExceededException("límite diario alcanzado ($" + dailyCap + ")",
                    new("daily", _dailyCapUsd, dailyCap, null, today));
        }

        void ValidateUltraCode(bool invocation = false, ToolCallId? toolCall = null) =>
            UltraCodeExecutionGuard.Validate(stream.EventsSince(1), _codecs, _artifacts,
                sessionId, runId, turnId, newTurn: !isResume && !started, invocation,
                ModelInvocationCostBound.Quote(_selection, _pricing, _modelContextCapacity,
                    _maximumGenerationRequestAttempts), toolCall);

        void ValidateTokenBudget(bool includeNextInvocation)
        {
            if (budget.MaxTokens is null) return;
            var tokens = RunTokenBudgetReader.Read(stream.EventsSince(1), _codecs, runId, budget.MaxTokens);
            if (tokens.Remaining is not { } remaining)
                throw new BudgetExceededException("uso de tokens histórico desconocido: " + tokens.Limitation);
            if (remaining < 0)
                throw new BudgetExceededException("límite de tokens del Run excedido: " + tokens.Limit);
            if (!includeNextInvocation) return;
            var maximum = ModelInvocationCostBound.TokenCeiling(_selection, _modelContextCapacity,
                _maximumGenerationRequestAttempts);
            if (maximum is null)
                throw new BudgetExceededException("cota de tokens de invocación desconocida");
            if (maximum.Value > remaining)
                throw new BudgetExceededException("límite de tokens del Run insuficiente para la siguiente invocación");
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
            if (_spendReservations is null || (budget.MaxCostUsd is null && !_enforceDefaultSpendCaps)) return null;
            if (_enforceDefaultSpendCaps && _userSpendReader is not null)
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
                var limits = new List<SqliteSpendReservationStore.Limit>();
                if (budget.MaxCostUsd is { } runLimit) limits.Add(new("run", runId.ToString(), runLimit, fresh.RunUsd.Value));
                if (_enforceDefaultSpendCaps)
                {
                    var effectiveDaily = _dailyCapUsd;
                    if (_store is IWorkspaceJournalReader journal)
                        effectiveDaily = BudgetContinuation.Limit(journal.ReadEvents(EventType.Of("interaction.requested"))
                            .Concat(journal.ReadEvents(EventType.Of("interaction.resolved")))
                            .Concat(journal.ReadEvents(EventType.Of("interaction.expired"))), _codecs,
                            sessionId, runId, day, "daily", _dailyCapUsd);
                    effectiveDaily = Math.Max(effectiveDaily, UserDailyBudgetContinuation.Limit(_userSpendReader, _codecs, day, _dailyCapUsd));
                    reservationDailyCap = effectiveDaily;
                    limits.Add(new("session", sessionId.ToString(), sessionCap, fresh.SessionUsd.Value));
                    limits.Add(new("daily", "user", effectiveDaily, fresh.DailyUsd.Value));
                }
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

        void FinishReservation(string? id, decimal? cost, string receipt, long observedSends)
        {
            if (id is null || cost is null) return; // Unavailable usage cannot release an in-flight bound.
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
            var preparedContext = MaterializeTurnContext(stream, sessionId, runId, laneId, turnId,
                workingStateText, instruction, messages, messageOwners, fingerprint, cancellationToken, CanInvokeMeta,
                hasReservationBoundary ? ReserveMeta : null,
                hasReservationBoundary ? id => _spendReservations!.MarkDispatched(MetaReservationId(id)) : null,
                hasReservationBoundary ? FinishMeta : null,
                hasReservationBoundary ? id => _spendReservations!.ReleaseBeforeDispatch(MetaReservationId(id)) : null);
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
                stream.AppendBatch(overflowStart, DurabilityClass.Barrier);
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
                stream.AppendBatch(startEvents, DurabilityClass.Barrier);
                started = true;
                turnStarted?.Invoke(startEvents.OfType<TurnStarted>().Single());
                // Límites de mutación por Turn (ADR-0044 §5): un Turn nuevo reinicia el contador del
                // Turn; los totales del Run se conservan. Un Turn reanudado sigue con su contador.
                _boundary?.BeginTurn();
            }
            started = true;

            if (invalidPersistedUsage)
                throw new InvalidDataException("Persisted token usage cannot be represented safely.");

            string? finalText = null;
            var stop = StopReason.EndTurn;
            // Replay is scoped to the same durable invocation destination, never to a session's last model.
            ProviderState? continuation = isResume
                ? ReadProviderContinuation(stream, runId, laneId, turnId) : null;
            for (var step = 0; step < MaxSteps; step++)
            {
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
                        workingStateText, instruction, messages, messageOwners, fingerprint, cancellationToken, CanInvokeMeta,
                        hasReservationBoundary ? ReserveMeta : null,
                        hasReservationBoundary ? id => _spendReservations!.MarkDispatched(MetaReservationId(id)) : null,
                        hasReservationBoundary ? FinishMeta : null,
                        hasReservationBoundary ? id => _spendReservations!.ReleaseBeforeDispatch(MetaReservationId(id)) : null);
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
                    VisibleTools(),
                    ToolChoice.Auto(),
                    null, _selection.Reasoning, new CacheHints(4, "automatic"),
                    // None is an explicit outbound replay prohibition. Keep the checkpoint
                    // and its integrity/usage evidence, but never resend it (including resume).
                    _selection.Route?.ReasoningCapability.ReplayPolicy == ReasoningReplayPolicy.None
                        ? null : continuation);

                ModelResponse resolved;
                string? stepReservation = null;
                var reservationDispatched = false;
                int? durableStepIndex = null;
                var providerEntered = false;
                try
                {
                    var budgeted = budget.MaxCostUsd is not null || _enforceDefaultSpendCaps;
                    ValidateBeforeInvocation();

                    stepReservation = ReserveInvocation($"primary/{sessionId}/{runId}/{laneId}/{turnId}/{nextModelStepIndex}");

                    var stepIndex = nextModelStepIndex++;
                    cancellationToken.ThrowIfCancellationRequested();
                    using (var contextPublication = (_artifacts as IArtifactPublicationLease)
                        ?.AcquirePublicationLease(CancellationToken.None))
                    {
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
                    using var generationAttempts = GenerationRequestAttemptScope.Enter();
                    providerEntered = true;
                    resolved = _complete(request, cancellationToken);
                    continuation = resolved.State;
                    var stepResponse = VisibleAnswerText(resolved.Content) ?? string.Empty;
                    var stepCost = _pricing?.CostUsd(resolved.Usage, resolved.ReportedUsageFields);
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
                        EncodeUsageResponse(stepResponse, resolved.Usage, stepCost, runId, completedDay,
                            stateDescriptor, EncodeVisibleContent(resolved.Content)),
                        "application/vnd.omnicore.model-usage+json", ArtifactKind.ModelResponse,
                        Sensitivity.Sensitive);
                    var completionAfter = stepReservation is not null ? _store.CurrentSequence(sessionId) : 0;
                    stream.Append(new ModelStepCompleted(turnId, stepIndex, resolved.Usage,
                        resolved.StopReason, stepArtifact, completedDay, stepCost, resolved.ReportedUsageFields,
                        new GenerationRequestAttemptEvidence(generationAttempts.ObservedSends,
                            _maximumGenerationRequestAttempts)), DurabilityClass.Barrier);
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
                        FinishReservation(stepReservation, stepCost, completionEvent.EventId.ToString(), generationAttempts.ObservedSends);
                    }
                    // Preserve the individual invocation before attempting aggregate arithmetic.
                    // An unrepresentable total cannot become a wrapped summary or authorize tools.
                    usage = CombineUsage(usage, resolved.Usage);
                    turnUsage = CombineUsage(turnUsage, resolved.Usage);
                    ValidateTokenBudget(includeNextInvocation: false);
                    guard.AdvanceTurn(checked(resolved.Usage.Input + resolved.Usage.Output));
                    if (stepCost is not null)
                    {
                        try { guard.AddCostUsd(stepCost.Value); }
                        catch (OverflowException) { throw UnrepresentableSpend(budgeted); }
                    }
                    if (checkpointFailure is not null) throw checkpointFailure;
                    ValidateUltraCode();
                    // This invocation is already durable, but unreported usage cannot
                    // authorize tools or another invocation within the same Ask. Checking
                    // only the history loaded at entry would defer this guard until resume.
                    if (budgeted && stepCost is null)
                        throw new BudgetExceededException("uso del paso incompleto: no se puede hacer cumplir el tope");
                    var spend = CurrentSpend();
                    var runCost = AccumulatedSpend(spend.RunUsd, guard.CostUsd(), budgeted);
                    var sessionCost = AccumulatedSpend(spend.SessionUsd, guard.CostUsd(), budgeted);
                    var dailyCost = AccumulatedSpend(spend.DailyUsd, guard.CostUsd(), budgeted);
                    if (budget.MaxCostUsd is not null
                        && runCost > budget.MaxCostUsd.Value)
                        throw new BudgetExceededException("límite de costo de Run ($" + budget.MaxCostUsd.Value + ")",
                            new("run", configuredRunCap!.Value, budget.MaxCostUsd.Value, runId.ToString(), null));
                    if (_enforceDefaultSpendCaps)
                    {
                        ValidateSessionDaily(sessionCost, dailyCost, sessionCap, dailyCap, today);
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
                }

                // Cancellation arriving with a completed response must retain its durable
                // usage/state, but cannot publish tool effects or a new approval request.
                cancellationToken.ThrowIfCancellationRequested();
                finalText = VisibleAnswerText(resolved.Content);
                var toolBlocks = new List<ToolCallBlock>();
                foreach (ContentBlock block in resolved.Content)
                {
                    if (block is ToolCallBlock call)
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
                AppendTerminalTurn(stream, sessionId, runId, laneId, turnId,
                    new TurnAbandoned(turnId, "Se agotó el límite de pasos del Explorer"));
                return new TurnResult(null, StopReason.Error, steps, usage, allToolCalls.ToArray(), null);
            }

            // Respuesta como artifact + ModelCompleted (persistencia del Turn).
            string? artifactId = null;
            if (finalText is not null && finalText!.Length > 0)
            {
                // Keep both final CAS objects protected until their canonical references
                // commit together. No provider work runs while this publication lease is held.
                using var finalPublication = (_artifacts as IArtifactPublicationLease)
                    ?.AcquirePublicationLease(CancellationToken.None);
                var safeResponse = _redaction.Redact(finalText!);
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
                var conversation = _artifacts.PutText(safeResponse, "text/markdown",
                    ArtifactKind.ModelResponse, Sensitivity.Sensitive);
                responseEvents.Add(new AssistantMessageRecorded(runId, laneId, turnId, conversation));
                stream.AppendBatch(responseEvents, DurabilityClass.Standard);
            }

            AppendTerminalTurn(stream, sessionId, runId, laneId, turnId, new TurnCompleted(turnId));
            AuditSpend(sessionId, runId, laneId, turnId, stop, turnUsage,
                ReadTurnModelStepCost(stream, turnId), usageAvailable: !invalidUsageObserved);
            AuditPolicy(sessionId, runId, turnId, stop);
            return new TurnResult(finalText is null ? null : _redaction.Redact(finalText), stop, steps,
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

            return new TurnResult(null, ex is OperationCanceledException ? StopReason.Cancelled : StopReason.Error, steps, usage, allToolCalls.ToArray(), null);
        }
    }

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
    }

    private ModelMessage SteeringMessage(string partsJson)
    {
        using var parsed = System.Text.Json.JsonDocument.Parse(partsJson);
        var parts = parsed.RootElement.EnumerateArray().Select(part => part.GetString() ?? "");
        return new ModelMessage(MessageRole.User,
            new ContentBlock[] { new TextBlock(_redaction.Redact(string.Join("\n", parts))) });
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
        if (evidenceEvents is null && includeWorkspaceDaily && _store is IWorkspaceJournalReader store)
        {
            try
            {
                stepStarts = store.ReadEvents(EventType.Of("model_step.started"));
                stepCompletions = store.ReadEvents(EventType.Of("model_step.completed"));
                unsentSteps = store.ReadEvents(EventType.Of("model_step.not_dispatched"));
                completions = store.ReadEvents(EventType.Of("model.completed"));
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
        }

        var snapshot = new CanonicalSpendReader(_codecs, artifacts).ReadPrimary(
            (stepStarts ?? []).Concat(stepCompletions ?? []).Concat(unsentSteps ?? []).Concat(completions),
            sessionId, runId, today);
        return snapshot with { Incomplete = snapshot.Incomplete || incomplete };
    }

    private PersistedSpend ReadOtherWorkspaceSpend(EventStream stream, SessionId sessionId, RunId runId, string today)
    {
        if (!_enforceDefaultSpendCaps || _userSpendReader is null) return new(0m, 0m, 0m, false);
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
        return blocks.Length == 0 ? null : _redaction.Redact(string.Concat(blocks.Select(block => block.Text)));
    }

    private static string EncodeUsageResponse(string response, TokenUsage usage, decimal? cost,
        RunId runId, string day, string? providerStateDescriptor = null, string? visibleContent = null)
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
            if (block is ReasoningBlock or ToolCallBlock) blocks.Add(block);
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
                    writer.WriteString("text", reasoning.VisibleText is null ? null : _redaction.Redact(reasoning.VisibleText));
                    writer.WriteString("visibility", reasoning.Visibility.ToString());
                }
                else if (block is TextBlock text)
                {
                    writer.WriteString("kind", "text");
                    writer.WriteString("text", _redaction.Redact(text.Text));
                }
                else if (block is ToolCallBlock call)
                {
                    // Only an ordering marker. Tool identity/arguments come from its canonical event.
                    writer.WriteString("kind", "tool-call");
                    writer.WriteString("callId", call.Id.ToString());
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
                        blocks.Add(new ToolCallBlock(call.ToolCallId, call.ProviderCallId, call.ToolName, _redaction.Redact(arguments)));
                    }
                    else if (allTurnCalls.ContainsKey(callId)) throw new InvalidDataException(failure);
                    // An unexecuted call has no canonical requested event; don't manufacture one.
                    continue;
                }
                var text = block.GetProperty("text").GetString();
                if (kind == "text" && text is not null)
                {
                    blocks.Add(new TextBlock(_redaction.Redact(text)));
                    continue;
                }
                if (kind != "reasoning") throw new InvalidDataException(failure);
                var visibilityName = block.GetProperty("visibility").GetString();
                if (!Enum.TryParse<ReasoningVisibility>(visibilityName, out var visibility)
                    || !Enum.IsDefined(visibility) || visibility.ToString() != visibilityName)
                    throw new InvalidDataException(failure);
                // Provider signatures and opaque artifact refs are never stored in this projection.
                blocks.Add(new ReasoningBlock(text is null ? null : _redaction.Redact(text), visibility, null));
            }
            return blocks;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
            or IOException or ArgumentException or KeyNotFoundException)
        {
            throw new InvalidDataException(failure);
        }
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
    private void EmitBudgetExceeded(EventStream stream, TurnId turnId, string detail,
        BudgetContinuationOffer? offer = null)
    {
        var interactionId = InteractionId.New();
        stream.Append(new InteractionRequested(interactionId, InteractionKind.BudgetExceeded,
            BudgetContinuation.Context(detail, offer),
            offer is null ? "[{\"id\":\"deny\",\"intent\":\"deny\"}]"
                : "[{\"id\":\"deny\",\"intent\":\"deny\"},{\"id\":\"allow_plus\",\"intent\":\"allow_plus\",\"value\":10}]",
            "deny", null, null, null, null, 0, 1));
    }

    /// <summary>
    /// Acumula costo de sesión/día (ADR-0037 §7: 5/20 USD). SOLO lanza al superar el tope:
    /// la emisión de la interacción la hace el catch del turno (evita la doble emisión).
    /// </summary>
    private void ValidateSessionDaily(decimal totalSessionCost, decimal totalDailyCost,
        decimal sessionCap, decimal dailyCap, string today)
    {
        if (totalSessionCost > sessionCap)
            throw new BudgetExceededException("límite de sesión ($" + sessionCap + ")",
                new("session", _sessionCapUsd, sessionCap, null, null));
        if (totalDailyCost > dailyCap)
            throw new BudgetExceededException("límite diario ($" + dailyCap + ")",
                new("daily", _dailyCapUsd, dailyCap, null, today));
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

    internal List<ModelMessage> LoadConversation(EventStream stream, RunId runId,
        TurnId? activeTurnId = null, LaneId? activeLaneId = null,
        IDictionary<ModelMessage, RunId>? messageOwners = null)
    {
        var history = new List<ModelMessage>();
        var journal = stream.EventsSince(1);
        var conversationScope = new LaneConversationScope(journal, _codecs, runId, activeLaneId);
        var currentHistoryRun = runId;
        void AppendHistory(ModelMessage message)
        {
            history.Add(message);
            messageOwners?.Add(message, currentHistoryRun);
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
            var payload = _codecs.Decode(evt);
            if (payload is AssistantMessageRecorded assistant && evt.RunId == assistant.RunId)
                canonicalAssistantTurns.Add((assistant.RunId, assistant.TurnId));
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
                    if (text.Length > 0)
                        AppendHistory(new ModelMessage(MessageRole.User,
                            new ContentBlock[] { new TextBlock(_redaction.Redact(text)) }));
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
                AppendHistory(new ModelMessage(MessageRole.Tool, new ContentBlock[] {
                    new ToolResultBlock(callId!, new ContentBlock[] { new TextBlock(content) },
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
                        new ContentBlock[] { new TextBlock(_redaction.Redact(text)) }));
            }
            else if (type == "model.completed")
            {
                var completed = _codecs.Decode(evt) as ModelCompleted;
                if (completed?.ResponseArtifact is null) continue;
                if (evt.RunId is { } owner && canonicalAssistantTurns.Contains((owner, completed.TurnId))) continue;
                var text = DecodeUsageResponse(_artifacts.GetText(completed.ResponseArtifact.Hash));
                if (!string.IsNullOrEmpty(text))
                    AppendHistory(new ModelMessage(MessageRole.Assistant,
                        new ContentBlock[] { new TextBlock(_redaction.Redact(text)) }));
            }
        }

        return history;
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

    private string EffectiveSystemPrompt(string instruction) => _redaction.Redact(
        "Contexto del workspace (fuentes del run):\n"
        + (instruction ?? "").Replace("{context}", "", StringComparison.Ordinal).Trim()
        + "\nFingerprint: " + _fingerprint.ModelKey + " · " + _fingerprint.ContextPolicyHash);

    private PreparedTurnContext MaterializeTurnContext(EventStream stream, SessionId sessionId, RunId runId,
        LaneId laneId, TurnId turnId, string workingStateText, string instruction,
        IReadOnlyList<ModelMessage> messages, IReadOnlyDictionary<ModelMessage, RunId> messageOwners,
        ExecutionFingerprint fingerprint, CancellationToken cancellationToken,
        Func<bool>? canInvokeMeta = null, Func<string, bool>? reserveMeta = null,
        Action<string>? dispatchMeta = null, Action<string, decimal?, long>? finishMeta = null,
        Action<string>? releaseMeta = null)
    {
        var contextTask = FindTaskForLane(stream.EventsSince(1), runId, laneId);
        var contributors = new List<IContextContributor>();
        // Only explicit, canonically accepted selected-context packets. No automatic parent
        // transcript import, worker admission, scheduler decision or authority transfer.
        var inherited = new ContextInheritanceService(_store, _codecs, _artifacts)
            .FindDelegationProjection(sessionId, runId, laneId);
        if (inherited is not null) contributors.Add(new RedactingContextContributor(inherited, _redaction));
        var hasWorkingState = false;
        foreach (var contributor in _materializer.Contributors())
        {
            hasWorkingState |= contributor is WorkingStateContributor;
            contributors.Add(new RedactingContextContributor(contributor, _redaction,
                rejectSkills: _activeSkills is { Count: 0 }));
        }

        if (!hasWorkingState && !string.IsNullOrWhiteSpace(workingStateText))
        {
            contributors.Add(new RedactingContextContributor(new WorkingStateContributor(workingStateText),
                _redaction));
        }

        var prompt = EffectiveSystemPrompt(instruction);
        contributors.Add(new RedactingContextContributor(new SystemPromptContributor(prompt), _redaction));
        var entries = new List<ConversationContextEntry>();
        var messageById = new Dictionary<string, ModelMessage>(StringComparer.Ordinal);
        var runById = new Dictionary<string, RunId>(StringComparer.Ordinal);
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
            var safe = RedactMessage(messages[i]);
            var id = "conversation-" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            var kind = safe.Role switch
            {
                MessageRole.User => ContextItemKind.UserMessage,
                MessageRole.Assistant => ContextItemKind.AssistantMessage,
                _ => ContextItemKind.ToolResult,
            };
            // Historical context must never silently evict the user's current intent.
            // If protected input itself cannot fit, the existing overflow barrier stops dispatch.
            var preserve = safe.Role == MessageRole.User && (firstUser || i == latestUserIndex);
            if (safe.Role == MessageRole.User)
            {
                firstUser = false;
            }

            entries.Add(new ConversationContextEntry(id, kind, RenderMessage(safe), preserve));
            messageById.Add(id, safe);
            runById.Add(id, messageOwners.TryGetValue(messages[i], out var owner) ? owner : runId);
        }

        var policy = _harness?.ContextManagement ?? ContextManagementPolicy.Default;
        var summarizedIds = new HashSet<string>(StringComparer.Ordinal);
        var summaryService = new RunSummaryService(_store, _codecs, _artifacts, _redaction.Redact);
        var summaries = summaryService.Read(sessionId, runId);
        if (summaries.Count > 0)
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
        var priorCheckpoint = ReadLatestCheckpoint(stream, runId, laneId);
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
            && !summarizedIds.Contains(e.Id)).ToArray();
        var oldCount = Math.Max(0, candidates.Length - Math.Max(0, policy.RecentTailItems));
        var oldItems = candidates.Take(oldCount).ToArray();
        var newOldItems = oldItems.Where(e => !compactedIds.Contains(e.Id)).ToArray();
        if (newOldItems.Any(entry => !entry.PreserveWhenTrimming)
            && ContextCompaction.ShouldCompact(newOldItems.Length, policy))
        {
            var material = (priorCheckpoint?.Summary is { Length: > 0 } oldSummary
                    ? "Previous checkpoint:\n" + oldSummary + "\n\nNew older history:\n" : "")
                + string.Join("\n", newOldItems.Select(e => e.Content));
            string summary;
            var metaFingerprint = "deterministic-v1";
            MetaModelService? metaModel = null;
            if (_metaModelProvider is not null && (canInvokeMeta?.Invoke() ?? true))
            {
                metaModel = new MetaModelService(_metaModelProvider, _artifacts,
                    new ContextStreamEventSink(stream, _artifacts), _selection, _redaction.Redact, usage => _pricing?.CostUsd(usage),
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

        var filteredEntries = entries.Where(e => !compactedIds.Contains(e.Id) && !summarizedIds.Contains(e.Id)).ToArray();
        contributors.Add(new SessionConversationContributor(filteredEntries));
        if (priorCheckpoint is not null)
        {
            contributors.Add(new ContextCheckpointContributor(priorCheckpoint.Id,
                priorCheckpoint.ThroughSequence, priorCheckpoint.Summary, priorCheckpoint.Artifact));
        }
        var priorDiagnostics = entries.Where(e => compactedIds.Contains(e.Id) || summarizedIds.Contains(e.Id)).Select(e => new ContextDiagnostic(e.Id,
            new ContextProvenance("session-conversation", ContributionCategory.Conversation, "engine",
                ScopeLevel.Session, false), ContextDecision.Compacted, e.Content.Length)).ToArray();
        var materializer = new ContextMaterializer(_materializer.Counter(), contributors, _artifacts, policy);
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

        return new PreparedTurnContext(prepared, selectedMessages.ToArray());
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

    private LoadedCheckpoint? ReadLatestCheckpoint(EventStream stream, RunId runId, LaneId laneId)
    {
        var journal = stream.EventsSince(1);
        var scope = new LaneConversationScope(journal, _codecs, runId, laneId);
        foreach (var evt in journal.Reverse())
        {
            if (evt.RunId != runId || _codecs.Decode(evt) is not ContextCheckpointRecorded latest
                || latest.RunId != runId || !scope.CanRead(evt)) continue;
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
                        ? null : _redaction.Redact(reasoning.VisibleText), reasoning.Visibility,
                        VerifiedOpaqueStateRef(reasoning.OpaquePayload)));
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
            writer.WriteNumber("contextScopeVersion", 1);
            writer.WriteString("sessionId", snapshot.SessionId.ToString());
            writer.WriteString("runId", snapshot.RunId.ToString());
            writer.WriteString("taskId", snapshot.TaskId?.ToString());
            writer.WriteString("laneId", snapshot.LaneId?.ToString());
            writer.WriteString("turnId", snapshot.TurnId?.ToString());
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

    private sealed class RedactingContextContributor : IContextContributor
    {
        private readonly IContextContributor _inner;
        private readonly RedactionPolicy _redaction;
        private readonly bool _rejectSkills;

        public RedactingContextContributor(IContextContributor inner, RedactionPolicy redaction, bool rejectSkills = false)
        {
            _inner = inner;
            _redaction = redaction;
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

    private IReadOnlyList<ToolDefinition> VisibleTools()
    {
        IEnumerable<ToolDefinition> all = _catalog.Definitions();
        if (_resolvedAgentProfile is not null)
        {
            var ranks = _resolvedAgentProfile.PreferredTools.Select((tool, index) => (tool, index))
                .ToDictionary(pair => pair.tool.ToString(), pair => pair.index, StringComparer.Ordinal);
            // A preference changes disclosure order only. It never creates an allowlist or a grant.
            all = all.OrderBy(tool => ranks.GetValueOrDefault(tool.Name, int.MaxValue));
        }
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
}
