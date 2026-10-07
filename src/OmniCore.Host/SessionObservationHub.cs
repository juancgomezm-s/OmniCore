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
        public long ActivitySequence;
        public readonly Dictionary<string, ChatActivityEvent> Activity = new();
        public readonly List<ChatActivityEvent> Events = new();
        public SessionContextMeasurement? Context;
        public DateTimeOffset ContextSubmittedAt;
        public SessionContextMeasurement? Pending;
        public readonly Dictionary<string, ProviderQuotaSnapshot> Quotas = new();
    }
    private Reading State(SessionId session)
    {
        if (!_sessions.TryGetValue(session.ToString(), out var state)) _sessions[session.ToString()] = state = new();
        return state;
    }
    private static bool Terminal(ChatActivityPhase phase) => phase is ChatActivityPhase.Completed or ChatActivityPhase.Cancelled or ChatActivityPhase.Failed;
    private static void Publish(Reading state, string session, string turn, string? run, string? lane,
        ChatActivityPhase phase, DateTimeOffset date, string source, bool answer = false)
    {
        state.Activity.TryGetValue(turn, out var prior);
        if (prior is not null && Terminal(prior.Phase)
            && (prior.Source.StartsWith("journal:", StringComparison.Ordinal) || !source.StartsWith("journal:", StringComparison.Ordinal))) return;
        answer |= prior?.FirstAnswerTextReceived == true;
        if (prior?.Phase == phase && prior.FirstAnswerTextReceived == answer) return;
        var item = new ChatActivityEvent(session, turn, run, lane, ++state.ActivitySequence, phase, answer, date, source);
        state.Activity[turn] = item;
        if (Terminal(phase) && state.Pending?.TurnId == turn) state.Pending = null;
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
                        payload is RunFailed or LaneFailed ? ChatActivityPhase.Failed : ChatActivityPhase.Cancelled, e.Timestamp, "journal:" + e.Type);
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
                Publish(state, session.ToString(), turn, e.RunId?.ToString(), e.LaneId?.ToString(), phase.Value, e.Timestamp, "journal:" + e.Type);
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
            state.Pending = submitted = Estimate(session, turn, start.StepIndex, request, modelCapacity);
        }
        var response = new TelemetryObservingModelProvider(provider, new Sink(this, session)).Complete(request, ct);
        lock (_gate)
        {
            var state = State(session);
            if (response.Content.OfType<TextBlock>().Any(t => t.Text.Length > 0))
                Publish(state, session.ToString(), turn.ToString(), scope.RunId?.ToString(), scope.LaneId?.ToString(),
                    ChatActivityPhase.AnswerText, DateTimeOffset.UtcNow, "provider:complete-text", true);
            if (state.Context is null || submitted.AsOf >= state.ContextSubmittedAt)
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
            if (state.Pending?.TurnId == submitted.TurnId && state.Pending.StepIndex == submitted.StepIndex) state.Pending = null;
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
                record.LaneId?.ToString(), phase.Value, record.TimestampUtc, "provider-stream:" + record.Signal, phase == ChatActivityPhase.AnswerText);
        }
    }
    internal ProviderQuotaSnapshot? Quota(SessionId session, string provider)
    {
        lock (_gate) return State(session).Quotas.GetValueOrDefault(provider);
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
            return new(session.ToString(), state.JournalSequence, DateTimeOffset.UtcNow,
                state.Activity.Values.Any(a => !Terminal(a.Phase)), state.Context, state.Pending, consumption,
                state.Quotas.Values.ToArray(), state.Events.Where(e => e.Sequence > afterActivitySequence)
                    .Concat(state.Activity.Values.Where(e => e.Sequence > afterActivitySequence))
                    .DistinctBy(e => e.Sequence).OrderBy(e => e.Sequence).ToArray(), state.ActivitySequence);
        }
    }
    private SessionContextMeasurement? RecoverContext(SessionId session)
    {
        // Recover facts from the journal after restart. A selection budget is NOT model capacity.
        var journal = store.ReadFrom(session, 1);
        var completed = journal.Select(e => (Event: e, Payload: codecs.Decode(e))).LastOrDefault(p => p.Payload is ModelStepCompleted);
        if (completed.Payload is not ModelStepCompleted step) return null;
        var start = journal.Select(codecs.Decode).OfType<ModelStepStarted>().LastOrDefault(s => s.TurnId == step.TurnId && s.StepIndex == step.StepIndex);
        if (start is null) return null;
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
}
