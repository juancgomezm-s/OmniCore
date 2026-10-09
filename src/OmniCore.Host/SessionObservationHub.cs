using System.Text.RegularExpressions;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Protocol;

namespace OmniCore.Host;

/// <summary>Host-only, bounded transient observations. Durable lifecycle and spend remain in the journal.</summary>
public sealed class SessionObservationHub(IEventStore store, IEventCodecRegistry codecs, IArtifactStore? artifacts)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Reading> _sessions = new(StringComparer.Ordinal);
    private sealed class Reading
    {
        public long JournalSequence;
        public long OpenedJournalSequence;
        public long ActivitySequence;
        public readonly Dictionary<string, ChatActivityEvent> Activity = new();
        public readonly List<ChatActivityEvent> Events = new();
        public SessionContextMeasurement? Context;
        public DateTimeOffset ContextSubmittedAt;
        public SessionContextMeasurement? Pending;
        public readonly Dictionary<string, SessionContextMeasurement> ContextByTurn = new(StringComparer.Ordinal);
        public readonly Dictionary<string, SessionContextMeasurement> PendingByTurn = new(StringComparer.Ordinal);
        public readonly Dictionary<string, ProviderQuotaSnapshot> Quotas = new();
    }
    private Reading State(SessionId session)
    {
        if (!_sessions.TryGetValue(session.ToString(), out var state))
        {
            var sequence = store.CurrentSequence(session);
            _sessions[session.ToString()] = state = new() { OpenedJournalSequence = sequence };
        }
        return state;
    }
    private static bool Terminal(ChatActivityPhase phase) => phase is ChatActivityPhase.Completed or ChatActivityPhase.Cancelled or ChatActivityPhase.Failed;
    private static void Publish(Reading state, string session, string turn, string? run, string? lane,
        ChatActivityPhase phase, DateTimeOffset date, string source, bool answer = false, bool observedInProcess = false)
    {
        state.Activity.TryGetValue(turn, out var prior);
        if (prior is not null && Terminal(prior.Phase)
            && (prior.Source.StartsWith("journal:", StringComparison.Ordinal) || !source.StartsWith("journal:", StringComparison.Ordinal))) return;
        answer |= prior?.FirstAnswerTextReceived == true;
        if (prior?.Phase == phase && prior.FirstAnswerTextReceived == answer)
        {
            state.Activity[turn] = prior with { AsOf = date, Source = source,
                ObservedInProcess = prior.ObservedInProcess || observedInProcess };
            return;
        }
        var item = new ChatActivityEvent(session, turn, run, lane, ++state.ActivitySequence, phase,
            answer, date, source, observedInProcess || prior?.ObservedInProcess == true);
        state.Activity[turn] = item;
        if (state.Activity.Count > 128)
        {
            foreach (var completedTurn in state.Activity.Values.Where(activity => Terminal(activity.Phase))
                .OrderBy(activity => activity.Sequence).Take(state.Activity.Count - 128)
                .Select(activity => activity.TurnId).ToArray())
                state.Activity.Remove(completedTurn);
        }
        if (Terminal(phase))
        {
            state.PendingByTurn.Remove(turn);
            if (state.Pending?.TurnId == turn) state.Pending = null;
        }
        state.Events.Add(item);
        if (state.Events.Count > 1024) state.Events.RemoveAt(0);
    }
    private void Synchronize(SessionId session, Reading state)
    {
        foreach (var e in store.ReadFrom(session, state.JournalSequence + 1))
        {
            state.JournalSequence = e.Sequence;
            var turn = e.TurnId?.ToString();
            var payload = codecs.Decode(e);
            if (payload is RunCancelled or LaneCancelled or RunFailed or LaneFailed)
            {
                foreach (var active in state.Activity.Values.Where(a => !Terminal(a.Phase)
                    && (payload switch {
                        RunCancelled r => a.RunId == r.RunId.ToString(), RunFailed r => a.RunId == r.RunId.ToString(),
                        LaneCancelled l => a.LaneId == l.LaneId.ToString(), LaneFailed l => a.LaneId == l.LaneId.ToString(), _ => false })).ToArray())
                    Publish(state, session.ToString(), active.TurnId, active.RunId, active.LaneId,
                        payload is RunFailed or LaneFailed ? ChatActivityPhase.Failed : ChatActivityPhase.Cancelled,
                        e.Timestamp, "journal:" + e.Type, observedInProcess: e.Sequence > state.OpenedJournalSequence);
                var affectedTurns = state.Activity.Values.Where(activity => payload switch
                {
                    RunCancelled cancelled => activity.RunId == cancelled.RunId.ToString(),
                    RunFailed failed => activity.RunId == failed.RunId.ToString(),
                    LaneCancelled cancelled => activity.LaneId == cancelled.LaneId.ToString(),
                    LaneFailed failed => activity.LaneId == failed.LaneId.ToString(),
                    _ => false,
                }).Select(activity => activity.TurnId).ToHashSet(StringComparer.Ordinal);
                foreach (var affectedTurn in affectedTurns) state.PendingByTurn.Remove(affectedTurn);
            }
            ChatActivityPhase? phase = payload switch
            {
                TurnStarted => ChatActivityPhase.WaitingForResponse,
                ModelStepStarted => ChatActivityPhase.WaitingForResponse,
                ToolCallRequested => ChatActivityPhase.Tools,
                InteractionRequested { Kind: InteractionKind.BudgetExceeded } => ChatActivityPhase.Cancelled,
                InteractionRequested => ChatActivityPhase.WaitingForApproval,
                InteractionResolved { State: "Cancelled" or "Expired" } => ChatActivityPhase.Cancelled,
                InteractionResolved => ChatActivityPhase.Tools,
                InteractionExpired => ChatActivityPhase.Cancelled,
                TurnCompleted => ChatActivityPhase.Completed,
                TurnInterrupted => ChatActivityPhase.Cancelled,
                TurnAbandoned => ChatActivityPhase.Failed,
                _ => null,
            };
            if (turn is not null && phase is not null)
                Publish(state, session.ToString(), turn, e.RunId?.ToString(), e.LaneId?.ToString(), phase.Value,
                    e.Timestamp, "journal:" + e.Type, observedInProcess: e.Sequence > state.OpenedJournalSequence);
        }
    }
    public ModelResponse Complete(SessionId session, IModelProvider provider, ModelRequest request, long? modelCapacity, CancellationToken ct)
    {
        var scope = ExecutionScope.Current ?? throw new InvalidOperationException("Model observation requires execution attribution.");
        var turn = scope.TurnId ?? throw new InvalidOperationException("Model observation requires a Turn.");
        SessionContextMeasurement submitted;
        lock (_gate)
        {
            var state = State(session);
            Synchronize(session, state);
            var start = store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelStepStarted>().Last(s => s.TurnId == turn);
            state.PendingByTurn[turn.ToString()] = submitted = Estimate(session, turn, start.StepIndex, request, modelCapacity);
            if (IsPrimaryLane(session, scope.RunId, scope.LaneId)) state.Pending = submitted;
            Publish(state, session.ToString(), turn.ToString(), scope.RunId?.ToString(), scope.LaneId?.ToString(),
                ChatActivityPhase.WaitingForResponse, DateTimeOffset.UtcNow, "provider:request-submitted",
                observedInProcess: true);
        }
        ModelResponse response;
        try { response = new TelemetryObservingModelProvider(provider, new Sink(this, session)).Complete(request, ct); }
        catch
        {
            lock (_gate)
            {
                var state = State(session);
                state.PendingByTurn.Remove(turn.ToString());
                if (IsPrimaryLane(session, scope.RunId, scope.LaneId) && state.Pending?.TurnId.ToString() == turn.ToString())
                    state.Pending = null;
                Publish(state, session.ToString(), turn.ToString(), scope.RunId?.ToString(), scope.LaneId?.ToString(),
                    ct.IsCancellationRequested ? ChatActivityPhase.Cancelled : ChatActivityPhase.Failed,
                    DateTimeOffset.UtcNow, ct.IsCancellationRequested ? "provider:request-cancelled" : "provider:request-failed",
                    observedInProcess: true);
            }
            throw;
        }
        lock (_gate)
        {
            var state = State(session);
            if (response.Content.OfType<TextBlock>().Any(t => t.Text.Length > 0))
                Publish(state, session.ToString(), turn.ToString(), scope.RunId?.ToString(), scope.LaneId?.ToString(),
                    ChatActivityPhase.AnswerText, DateTimeOffset.UtcNow, "provider:complete-text", true, true);
            state.ContextByTurn[turn.ToString()] = submitted;
            if (state.ContextByTurn.Count > 128)
            {
                foreach (var oldTurn in state.ContextByTurn.OrderBy(pair => pair.Value.AsOf)
                    .Take(state.ContextByTurn.Count - 128).Select(pair => pair.Key).ToArray())
                    state.ContextByTurn.Remove(oldTurn);
            }
            var primaryLane = IsPrimaryLane(session, scope.RunId, scope.LaneId);
            if (primaryLane && (state.Context is null || submitted.AsOf >= state.ContextSubmittedAt))
            {
                state.ContextSubmittedAt = submitted.AsOf;
                var date = DateTimeOffset.UtcNow;
                var validUsage = !TokenUsageValidation.IsInvalid(response.Usage, response.ReportedUsageFields);
                var tokens = validUsage && provider.Capabilities.ReportsUsage && response.ReportedUsageFields.HasFlag(TokenUsageFields.Input)
                    ? new Metric<long?>(MetricAvailability.Reported, response.Usage.Input, "provider:submitted-request-input", date)
                    : submitted.Tokens;
                state.Context = submitted with { Tokens = tokens,
                    UsedPercent = Percent(tokens, submitted.Capacity, date),
                    ReportedReasoningTokens = new(validUsage && response.ReportedUsageFields.HasFlag(TokenUsageFields.Reasoning) ? MetricAvailability.Reported : MetricAvailability.Unknown,
                        validUsage && response.ReportedUsageFields.HasFlag(TokenUsageFields.Reasoning) ? response.Usage.Reasoning : null, "provider:output-reasoning;not-context-occupancy", date), AsOf = date };
            }
            if (state.PendingByTurn.TryGetValue(turn.ToString(), out var pending)
                && pending.StepIndex == submitted.StepIndex) state.PendingByTurn.Remove(turn.ToString());
            if (primaryLane && state.Pending?.TurnId == submitted.TurnId
                && state.Pending.StepIndex == submitted.StepIndex) state.Pending = null;
        }
        return response;
    }
    private sealed class Sink(SessionObservationHub owner, SessionId session) : ITelemetrySink
    { public void Record(TelemetryRecord record) => owner.Observe(session, record); }
    public void Observe(SessionId session, TelemetryRecord record)
    {
        if (record.TurnId is null) return;
        lock (_gate)
        {
            var state = State(session);
            Synchronize(session, state);
            var phase = record.Signal switch
            {
                TelemetrySignal.TextDeltaCharacters when record.Value > 0 => ChatActivityPhase.AnswerText,
                TelemetrySignal.ReasoningDeltaCharacters when record.Value > 0 => ChatActivityPhase.Reasoning,
                TelemetrySignal.ResponseFailedCount => ChatActivityPhase.Failed,
                _ => (ChatActivityPhase?)null,
            };
            if (phase is not null) Publish(state, session.ToString(), record.TurnId.ToString(), record.RunId?.ToString(),
                record.LaneId?.ToString(), phase.Value, record.TimestampUtc, "provider-stream:" + record.Signal,
                phase == ChatActivityPhase.AnswerText, observedInProcess: true);
        }
    }
    internal ProviderQuotaSnapshot? Quota(SessionId session, string provider)
    {
        lock (_gate) return State(session).Quotas.GetValueOrDefault(provider);
    }
    internal IReadOnlyList<ChatActivityEvent> Activities(SessionId session)
    {
        lock (_gate)
        {
            var state = State(session);
            Synchronize(session, state);
            return state.Activity.Values.OrderBy(item => item.Sequence).ToArray();
        }
    }
    public void SetQuota(SessionId session, ProviderQuotaSnapshot quota)
    {
        lock (_gate)
        {
            var state = State(session);
            if (quota.Availability == MetricAvailability.Unknown && state.Quotas.TryGetValue(quota.ProviderId, out var prior)
                && prior.Availability is MetricAvailability.Reported or MetricAvailability.Stale && prior.Windows.Count > 0)
                quota = prior with { Availability = MetricAvailability.Stale, Limitation = quota.Limitation, LastQueryAttemptAt = quota.AsOf };
            state.Quotas[quota.ProviderId] = quota;
        }
    }
    public SessionObservabilitySnapshot Snapshot(SessionId session, long afterActivitySequence = 0)
    {
        lock (_gate)
        {
            var state = State(session);
            Synchronize(session, state);
            if (state.Context is null && RecoverContext(session) is { } recovered)
            {
                state.Context = recovered;
                state.ContextSubmittedAt = recovered.AsOf;
            }
            var consumption = artifacts is null
                ? new ConversationUsageMeasurement(session.ToString(), new(MetricAvailability.Unknown, null, "artifact-store-unavailable"),
                    new(MetricAvailability.Unknown, null, null), new(MetricAvailability.Unknown, null, null), state.JournalSequence, 0, 0, DateTimeOffset.UtcNow)
                : SessionUsageReporter.ReadConversation(store, codecs, artifacts, session);
            var activitiesByTurn = state.Activity.Values.ToDictionary(item => item.TurnId, StringComparer.Ordinal);
            var laneContexts = state.ContextByTurn.Keys.Concat(state.PendingByTurn.Keys).Distinct(StringComparer.Ordinal)
                .Where(turn => activitiesByTurn.TryGetValue(turn, out var activity) && activity.LaneId is not null)
                .Select(turn =>
                {
                    var activity = activitiesByTurn[turn];
                    return new LaneContextSnapshot(turn, activity.RunId, activity.LaneId,
                        state.ContextByTurn.GetValueOrDefault(turn), state.PendingByTurn.GetValueOrDefault(turn));
                }).ToArray();
            return new(session.ToString(), state.JournalSequence, DateTimeOffset.UtcNow,
                state.Activity.Values.Any(a => a.ObservedInProcess && !Terminal(a.Phase)), state.Context, state.Pending, consumption,
                state.Quotas.Values.ToArray(), state.Events.Where(e => e.Sequence > afterActivitySequence)
                    .OrderBy(e => e.Sequence).ToArray(),
                state.ActivitySequence, laneContexts);
        }
    }
    private SessionContextMeasurement? RecoverContext(SessionId session)
    {
        // Recover facts from the journal after restart. A selection budget is NOT model capacity.
        var journal = store.ReadFrom(session, 1);
        var completed = journal.Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
            .Where(item => item.Payload is ModelStepCompleted && IsPrimaryTurnEvent(journal, item.Event))
            .OrderByDescending(item => item.Event.Sequence).FirstOrDefault();
        if (completed.Payload is not ModelStepCompleted step) return null;
        var primaryLane = PrimaryLane(journal, completed.Event.RunId);
        if (primaryLane is null) return null;
        var starts = journal.Where(evt => evt.RunId == completed.Event.RunId
                && evt.LaneId == primaryLane && evt.TurnId == step.TurnId)
            .Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
            .Where(item => item.Payload is ModelStepStarted started && started.StepIndex == step.StepIndex)
            .ToArray();
        if (starts.Length == 0 && completed.Event.LaneId is null)
        {
            var legacyStarts = journal.Where(evt => evt.RunId == completed.Event.RunId && evt.TurnId == step.TurnId)
                .Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
                .Where(item => item.Payload is ModelStepStarted started && started.StepIndex == step.StepIndex)
                .ToArray();
            if (legacyStarts.Length == 1 && journal.Where(evt => evt.RunId == completed.Event.RunId
                    && evt.TurnId == step.TurnId && codecs.Decode(evt) is TurnStarted).Select(evt => evt.LaneId)
                .Distinct().Count() <= 1)
                starts = legacyStarts;
        }
        if (starts.Length != 1) return null;
        var start = (ModelStepStarted)starts[0].Payload;
        var date = completed.Event.Timestamp;
        var components = new List<ContextComponent>();
        if (artifacts is not null && start.ContextSnapshotRef is { } reference)
        {
            try
            {
                using var document = JsonDocument.Parse(artifacts.GetText(reference.Hash) ?? "{}");
                if (document.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var group in items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object)
                        .GroupBy(i => i.TryGetProperty("kind", out var kind) ? kind.GetString() ?? "unknown" : "unknown"))
                        components.Add(new(group.Key, new(MetricAvailability.Estimated,
                            group.Sum(i => i.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? Count(content.GetString()!) : 0),
                            "journal:context-snapshot:chars/4;no-base64", date)));
            }
            catch (Exception ex) when (ex is JsonException or IOException or ArgumentException) { }
        }
        var reportedFields = step.ReportedUsageFields ?? (TokenUsageFields.Input | TokenUsageFields.Output);
        var validUsage = !TokenUsageValidation.IsInvalid(step.Usage, reportedFields);
        var inputReported = validUsage && reportedFields.HasFlag(TokenUsageFields.Input);
        var tokens = new Metric<long?>(inputReported ? MetricAvailability.Reported : MetricAvailability.Unknown,
            inputReported ? step.Usage.Input : null, "journal:model-step-input", date);
        var capacity = new Metric<long?>(start.ModelContextCapacity is > 0 ? MetricAvailability.Estimated : MetricAvailability.Unknown,
            start.ModelContextCapacity is > 0 ? start.ModelContextCapacity : null, "journal:declared-model-capacity;legacy-may-be-unavailable", date);
        var reasoningReported = validUsage && reportedFields.HasFlag(TokenUsageFields.Reasoning);
        return new(session.ToString(), step.TurnId.ToString(), step.StepIndex, start.ModelId, tokens, capacity,
            new(MetricAvailability.Estimated, start.ContextBudget, "journal:effective-selection-budget", date), Percent(tokens, capacity, date), components,
            new(reasoningReported ? MetricAvailability.Reported : MetricAvailability.Unknown, reasoningReported ? step.Usage.Reasoning : null,
                "journal:output-reasoning;not-context-occupancy", date), date);
    }
    private static Metric<double?> Percent(Metric<long?> tokens, Metric<long?> capacity, DateTimeOffset date) =>
        tokens.Value is { } count && capacity.Value is > 0
            ? new(MetricAvailability.Estimated, 100d * count / capacity.Value.Value, "submitted-input/declared-model-capacity", date)
            : new(MetricAvailability.Unknown, null, "capacity-or-input-unavailable", date);
    internal static SessionContextMeasurement Estimate(SessionId session, TurnId turn, int index, ModelRequest request, long? capacity)
    {
        var date = DateTimeOffset.UtcNow;
        var parts = new Dictionary<string, long> { ["instructions"] = Count(request.Instructions ?? ""), ["history"] = 0,
            ["toolArguments"] = 0, ["toolResults"] = 0, ["reportedReasoningText"] = 0,
            ["toolDefinitions"] = request.Tools.Sum(t => Count(t.Name + t.Description + t.InputSchemaJson)) };
        var opaque = false;
        void Add(ContentBlock block, string category)
        {
            switch (block)
            {
                case TextBlock t: parts[category] += Count(t.Text); break;
                case CitationBlock c: parts[category] += Count(c.Text); break;
                case ToolCallBlock t: parts["toolArguments"] += Count(t.ArgumentsJson); break;
                case ToolResultBlock t: foreach (var child in t.Content) Add(child, "toolResults"); break;
                case ReasoningBlock r:
                    parts["reportedReasoningText"] += Count(r.VisibleText ?? ""); opaque |= r.OpaquePayload is not null; break;
                case ProviderOpaqueBlock: opaque = true; break;
            }
        }
        foreach (var message in request.Messages) foreach (var block in message.Content) Add(block, "history");
        var tokens = new Metric<long?>(MetricAvailability.Estimated, parts.Values.Sum(), "request-shape:chars/4;no-base64-or-opaque", date);
        var capacityMetric = new Metric<long?>(capacity is > 0 ? MetricAvailability.Estimated : MetricAvailability.Unknown,
            capacity is > 0 ? capacity : null, "model-configuration:contextWindow", date);
        var components = parts.Select(p => new ContextComponent(p.Key, new(MetricAvailability.Estimated, p.Value, tokens.Source, date))).ToList();
        // Current Domain has no typed attachments; file content inside instructions/results is not separable reliably.
        components.Add(new("files", new(MetricAvailability.Unknown, null, "embedded-content-not-separately-attributed", date)));
        components.Add(new("attachments", new(opaque ? MetricAvailability.Unknown : MetricAvailability.NotApplicable, null, "opaque-content-not-tokenized", date)));
        return new(session.ToString(), turn.ToString(), index, request.Model.Model.ToString(), tokens, capacityMetric,
            new(MetricAvailability.Estimated, request.Model.ContextBudget, "effective-selection-budget", date), Percent(tokens, capacityMetric, date),
            components, new(MetricAvailability.Unknown, null, "awaiting-provider-usage", date), date);
    }
    private static long Count(string text)
    {
        text = Regex.Replace(text, @"data:[^\s;,]+;base64,[A-Za-z0-9+/=]+", "");
        text = Regex.Replace(text, "\"(?:data|base64|encrypted_content)\"\\s*:\\s*\"[^\"]*\"", "");
        return (text.Length + 3L) / 4;
    }

    private bool IsPrimaryLane(SessionId session, RunId? run, LaneId? lane) =>
        run is not null && lane is not null && PrimaryLane(store.ReadFrom(session, 1), run) == lane;

    private LaneId? PrimaryLane(IReadOnlyList<DomainEvent> journal, RunId? onlyRun = null)
    {
        var candidates = journal.Where(evt => onlyRun is null || evt.RunId == onlyRun).ToArray();
        var rootTasks = candidates.Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
            .Where(item => item.Payload is TaskCreated { ParentTaskId: null })
            .Select(item => (item.Event.RunId, Task: (TaskCreated)item.Payload)).ToArray();
        if (rootTasks.Length == 0)
        {
            // Legacy journals may predate TaskCreated/LaneCreated. Use only explicit lane
            // envelopes from root-turn/model-step facts and infer a primary lane only when the
            // whole Run has exactly one such owner; never pick a sibling by chronology.
            var legacyLanes = candidates.Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
                .Where(item => item.Payload is LaneCreated)
                .Select(item => (item.Event.RunId, Lane: (LaneCreated)item.Payload)).ToArray();
            if (legacyLanes.Length == 1) return legacyLanes[0].Lane.LaneId;
            if (legacyLanes.Length > 1) return null;
            var observedLanes = candidates.Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
                .Where(item => item.Event.LaneId is not null
                    && item.Payload is TurnStarted or ModelStepStarted or ModelStepCompleted)
                .Select(item => item.Event.LaneId!).Distinct().ToArray();
            return observedLanes.Length == 1 ? observedLanes[0] : null;
        }
        if (rootTasks.Length != 1) return null;
        var rootTask = rootTasks[0];
        var lanes = candidates.Select(evt => (Event: evt, Payload: codecs.Decode(evt)))
            .Where(item => item.Payload is LaneCreated lane && lane.TaskId == rootTask.Task.TaskId
                && item.Event.RunId == rootTask.RunId)
            .Select(item => (LaneCreated)item.Payload).ToArray();
        return lanes.Length == 1 ? lanes[0].LaneId : null;
    }

    private bool IsPrimaryTurnEvent(IReadOnlyList<DomainEvent> journal, DomainEvent evt)
    {
        var primaryLane = PrimaryLane(journal, evt.RunId);
        if (primaryLane is null) return false;
        if (evt.LaneId is { } lane) return lane == primaryLane;
        if (evt.TaskId is { } task)
        {
            var rootTask = journal.Where(candidate => candidate.RunId == evt.RunId)
                .Select(codecs.Decode).OfType<TaskCreated>().SingleOrDefault(item => item.ParentTaskId is null);
            return rootTask?.TaskId == task;
        }
        // A lane-less legacy completion is only attributable if its turn has one start and
        // every start in this Run belongs to the unique primary lane.
        var starts = journal.Where(candidate => candidate.RunId == evt.RunId && candidate.TurnId == evt.TurnId
                && codecs.Decode(candidate) is TurnStarted)
            .ToArray();
        return evt.TurnId is not null && starts.Length == 1
            && (starts[0].LaneId is null || starts[0].LaneId == primaryLane);
    }
}
