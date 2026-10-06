using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Engine;
using System.Runtime.CompilerServices;

namespace OmniCore.Tests;

/// <summary>Deterministic fixtures: no authenticated queries or real provider spending.</summary>
public sealed class SessionObservabilityTests
{
    private readonly InMemoryEventStore _store = new();
    private readonly IEventCodecRegistry _codecs = EventCodecs.Create();
    private readonly SessionId _session = SessionId.New();
    private readonly RunId _run = RunId.New();
    private readonly LaneId _lane = LaneId.New();
    private readonly TurnId _turn = TurnId.New();
    private readonly IArtifactStore _artifacts = OmniHost.CreateArtifactStore(Path.Combine(Path.GetTempPath(), "omni-observability-tests-" + Guid.NewGuid().ToString("N")));
    private void Add(DomainEventPayload payload, TurnId? turn = null, SessionId? session = null)
    {
        var owner = session ?? _session;
        _store.Append(owner, DomainEvent.Create(owner, payload.Type(), payload.SchemaVersion(), null,
            _run, _run, null, _lane, turn, null, null, [], _codecs.CodecFor(payload.Type()).Encode(payload)),
            DurabilityClass.Barrier, CancellationToken.None);
    }
    private ModelStepCompleted Spend(int step = 0, TokenUsageFields fields = TokenUsageFields.All) =>
        new(_turn, step, new(100, 20, 30, 10, 5), StopReason.EndTurn, null, "2026-10-05", .01m, fields);
    private ConversationUsageMeasurement Usage() => SessionUsageReporter.ReadConversation(_store, _codecs, _artifacts, _session);
    private SessionObservationHub Hub() => new(_store, _codecs, _artifacts);
    private void Signal(SessionObservationHub hub, TelemetrySignal signal) => hub.Observe(_session,
        new(TelemetryKind.Delta, signal, 1, DateTimeOffset.UtcNow, _run, laneId: _lane, turnId: _turn));

    [Fact]
    public void Spend_is_idempotent_and_final_summary_cache_and_reasoning_are_not_added_twice()
    {
        Add(Spend()); Add(Spend()); Add(new ModelCompleted(_turn, null));
        var first = Usage(); var second = Usage();
        Assert.Equal(120, first.Total.Value); Assert.Equal(1, first.ModelInvocations);
        Assert.Equal(30, first.Breakdown!.CacheRead.Value); Assert.Equal(10, first.Breakdown.CacheWrite.Value);
        Assert.Equal(first, second);
    }
    [Fact]
    public void Separate_invocations_charge_retries_but_other_sessions_do_not()
    {
        Add(Spend(0)); Add(Spend(1)); Add(Spend(2), session: SessionId.New());
        Assert.Equal(240, Usage().Total.Value); Assert.Equal(2, Usage().ModelInvocations);
    }
    [Fact]
    public void Missing_usage_and_cache_fields_are_unknown_not_zero()
    {
        Add(Spend(fields: TokenUsageFields.Input | TokenUsageFields.Output));
        Assert.Equal(120, Usage().Total.Value); Assert.Null(Usage().Breakdown!.CacheRead.Value);
        Assert.Equal(MetricAvailability.Unknown, Usage().Breakdown!.CacheWrite.Availability);
        Add(new ModelStepStarted(_turn, 1, "fixture", 8192, "Direct", null, null, null));
        Assert.Null(Usage().Total.Value); Assert.Null(Usage().Cost.Value);
        Assert.Equal(1, Usage().IncompleteInvocations);
    }
    [Fact]
    public void Conflicting_duplicate_is_not_silently_charged_or_reported_as_complete()
    {
        Add(Spend()); Add(Spend() with { Usage = new(200, 20, 0, 0, 0) });
        Assert.Null(Usage().Total.Value); Assert.Equal(1, Usage().ModelInvocations);
    }
    [Fact]
    public void Reasoning_is_not_first_answer_and_terminal_blocks_late_streams()
    {
        Add(new TurnStarted(_turn, _lane), _turn);
        var hub = Hub(); var projection = new SessionObservabilityProjection(); projection.Activate(_session.ToString());
        projection.Apply(hub.Snapshot(_session)); Assert.True(projection.WaitingForFirstAnswer);
        Signal(hub, TelemetrySignal.ReasoningDeltaCharacters);
        projection.Apply(hub.Snapshot(_session)); Assert.True(projection.Reasoning); Assert.False(projection.AnswerText);
        Assert.False(projection.Activity.Last().FirstAnswerTextReceived);
        Signal(hub, TelemetrySignal.TextDeltaCharacters); projection.Apply(hub.Snapshot(_session)); Assert.True(projection.AnswerText);
        Add(new TurnInterrupted(_turn), _turn); projection.Apply(hub.Snapshot(_session));
        var sequence = projection.ActivitySequence;
        Signal(hub, TelemetrySignal.TextDeltaCharacters); projection.Apply(hub.Snapshot(_session));
        Assert.False(projection.AnswerText); Assert.False(projection.Reasoning); Assert.False(projection.WaitingForFirstAnswer);
        Assert.Equal(sequence, projection.ActivitySequence); Assert.Equal(ChatActivityPhase.Cancelled, projection.Activity.Last().Phase);
    }
    [Fact]
    public void Durable_cancellation_wins_over_provider_failure_and_late_completion()
    {
        Add(new TurnStarted(_turn, _lane), _turn); var hub = Hub();
        Signal(hub, TelemetrySignal.ResponseFailedCount);
        Add(new TurnInterrupted(_turn), _turn); Add(new TurnCompleted(_turn), _turn);
        Assert.Equal(ChatActivityPhase.Cancelled, hub.Snapshot(_session).Activities.Last().Phase);
        Assert.False(hub.Snapshot(_session).Updating);
    }
    [Fact]
    public void Run_cancellation_stops_its_activity_without_a_turn_envelope()
    {
        Add(new TurnStarted(_turn, _lane), _turn); var hub = Hub(); hub.Snapshot(_session);
        Add(new RunCancelled(_run));
        Assert.Equal(ChatActivityPhase.Cancelled, hub.Snapshot(_session).Activities.Last().Phase);
    }
    [Fact]
    public void Projection_rejects_other_session_and_nested_context_owner_and_clears_on_switch()
    {
        var hub = Hub(); var snapshot = hub.Snapshot(_session);
        var projection = new SessionObservabilityProjection(); projection.Activate(_session.ToString());
        Assert.True(projection.Apply(snapshot));
        Assert.False(projection.Apply(snapshot with { Consumption = snapshot.Consumption with { SessionId = "other" } }));
        projection.Activate("other"); Assert.Null(projection.Snapshot); Assert.Empty(projection.Activity);
        Assert.False(projection.Apply(snapshot));
    }
    [Fact]
    public void Json_roundtrip_preserves_unknown_nulls_and_identity()
    {
        var snapshot = Hub().Snapshot(_session);
        var decoded = ObservabilityJson.Decode(ObservabilityJson.Encode(snapshot))!;
        Assert.Equal(snapshot.SessionId, decoded.SessionId); Assert.Null(decoded.Context);
        Assert.Equal(snapshot.Consumption.Total, decoded.Consumption.Total);
    }
    [Fact]
    public void Codex_prefers_limit_map_and_never_fabricates_five_hour_window_or_key_limit()
    {
        using var json = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":90,"windowDurationMins":300}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":17,"windowDurationMins":10080,"resetsAt":1791823562},"credits":{"balance":"0"}}}}""");
        var quota = SubscriptionQuotaParser.Codex(json.RootElement, "fixture-account", DateTimeOffset.UtcNow);
        var window = Assert.Single(quota.Windows);
        Assert.Equal(17, window.UsedPercent.Value); Assert.Equal(83, window.RemainingPercent.Value);
        Assert.Equal(10080, window.DurationMinutes);
        Assert.Equal(CreditScope.AccountBalance, Assert.Single(quota.Credits).Scope);
        Assert.Equal(0m, quota.Credits[0].Amount.Value);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":\"17\"}}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":101}}}")]
    public void Missing_or_malformed_quota_is_unknown(string fixture)
    {
        using var json = JsonDocument.Parse(fixture);
        var result = SubscriptionQuotaParser.Codex(json.RootElement, null, DateTimeOffset.UtcNow);
        Assert.Equal(MetricAvailability.Unknown, result.Availability); Assert.Empty(result.Windows);
    }
    [Fact]
    public void Failed_refresh_preserves_last_valid_quota_date_as_stale()
    {
        var date = DateTimeOffset.UtcNow.AddMinutes(-5);
        using var json = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":17}}}""");
        var hub = Hub(); hub.SetQuota(_session, SubscriptionQuotaParser.Codex(json.RootElement, "fixture", date));
        var failure = new ProviderQuotaSnapshot("chatgpt", null, "fixture", DateTimeOffset.UtcNow, MetricAvailability.Unknown, [], [], "timeout");
        hub.SetQuota(_session, failure);
        var quota = Assert.Single(hub.Snapshot(_session).Quotas);
        Assert.Equal(date, quota.AsOf); Assert.Equal(failure.AsOf, quota.LastQueryAttemptAt);
        Assert.Equal(MetricAvailability.Stale, quota.Availability); Assert.Single(quota.Windows);
    }
    [Fact]
    public void Claude_preserves_published_reset_text_without_inventing_duration_or_account()
    {
        var result = SubscriptionQuotaParser.Claude("Current session: 12% used · resets 3pm", DateTimeOffset.UtcNow);
        var window = Assert.Single(result.Windows); Assert.Null(window.DurationMinutes); Assert.Null(window.ResetsAt);
        Assert.Equal("3pm", window.ResetDescription); Assert.Null(result.AccountId);
    }

    [Fact]
    public void Restart_recovers_measured_input_and_declared_capacity_not_the_selection_budget()
    {
        var context = _artifacts.PutText("""{"items":[{"kind":"File","content":"hello data:image/png;base64,AAAAAA=="}]}""", "application/json", ArtifactKind.ContextSnapshot, Sensitivity.Sensitive);
        Add(new ModelStepStarted(_turn, 0, "fixture", 8192, "Direct", null, null, context, 16000), _turn);
        Add(Spend(), _turn);
        var measurement = Hub().Snapshot(_session).Context!;
        Assert.Equal(100, measurement.Tokens.Value); Assert.Equal(16000, measurement.Capacity.Value);
        Assert.Equal(8192, measurement.UsableBudget.Value); Assert.Equal(.625, measurement.UsedPercent.Value);
        Assert.Equal(2, Assert.Single(measurement.Components).Tokens.Value);
        Assert.Equal(MetricAvailability.Estimated, measurement.Components[0].Tokens.Availability);
    }

    [Fact]
    public void Compaction_is_a_separate_invocation_and_counts_once_even_if_failure_follows_usage()
    {
        Add(Spend());
        Add(new MetaModelInvocationFailed("compact", _run, "compact", "fixture", "error", new(10, 3, 0, 0, 0), .002m, TokenUsageFields.All));
        Assert.Equal(133, Usage().Total.Value); Assert.Equal(2, Usage().ModelInvocations);
        Assert.Equal(.012m, Usage().Cost.Value!.Amount);
    }

    [Fact]
    public void Tool_and_approval_transitions_are_distinct_and_polling_does_not_advance_sequence()
    {
        Add(new TurnStarted(_turn, _lane), _turn);
        Add(new ToolCallRequested(ToolCallId.New(), "fixture-call", "filesystem.read", "{}"), _turn);
        var hub = Hub(); Assert.Equal(ChatActivityPhase.Tools, hub.Snapshot(_session).Activities.Last().Phase);
        var interaction = InteractionId.New();
        Add(new InteractionRequested(interaction, InteractionKind.Permission, "{}", "[]", "deny", null, _lane, null, null, 1, 1), _turn);
        Assert.Equal(ChatActivityPhase.WaitingForApproval, hub.Snapshot(_session).Activities.Last().Phase);
        Add(new InteractionResolved(interaction, "allow", InteractionCause.User), _turn);
        var first = hub.Snapshot(_session); Assert.Equal(ChatActivityPhase.Tools, first.Activities.Last().Phase);
        Assert.Empty(hub.Snapshot(_session, first.ActivitySequence).Activities);
        Assert.Equal(first.ActivitySequence, hub.Snapshot(_session).ActivitySequence);
    }

    [Fact]
    public void Pending_turn_preserves_last_measurement_and_cancelling_one_turn_does_not_stop_another()
    {
        Add(new ModelStepStarted(_turn, 0, "fixture", 8192, "Direct", null, null, null, 16000), _turn);
        Add(Spend(), _turn); Add(new TurnCompleted(_turn), _turn);
        var hub = Hub(); var prior = hub.Snapshot(_session).Context;
        var other = TurnId.New(); Add(new TurnStarted(other, _lane), other);
        var snapshot = hub.Snapshot(_session);
        Assert.True(snapshot.Updating); Assert.Equal(prior, snapshot.Context);
        Add(new TurnInterrupted(_turn), _turn); Assert.True(hub.Snapshot(_session).Updating);
        Add(new TurnCompleted(other), other); Assert.False(hub.Snapshot(_session).Updating);
    }

    [Fact]
    public void Request_estimate_excludes_base64_without_calling_it_measured_text()
    {
        var request = new ModelRequest(new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
            [new ModelMessage(MessageRole.User, [new TextBlock("hello data:image/png;base64," + new string('A', 4000))])],
            null, [], ToolChoice.None(), null, null, null, null);
        var estimate = SessionObservationHub.Estimate(_session, _turn, 0, request, 16000);
        Assert.Equal(2, estimate.Tokens.Value); Assert.Equal(MetricAvailability.Estimated, estimate.Tokens.Availability);
        Assert.Equal(MetricAvailability.Unknown, estimate.Components.Single(c => c.Kind == "files").Tokens.Availability);
    }

    [Fact]
    public async System.Threading.Tasks.Task Concurrent_turns_keep_the_newer_submitted_context_even_when_both_complete_later()
    {
        var other = TurnId.New();
        Add(new ModelStepStarted(_turn, 0, "fixture", 8192, "Direct", null, null, null), _turn);
        Add(new ModelStepStarted(other, 0, "fixture", 8192, "Direct", null, null, null), other);
        var hub = Hub(); var first = new BlockingProvider(100); var second = new BlockingProvider(200);
        var request = new ModelRequest(new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
            [], null, [], ToolChoice.None(), null, null, null, null);
        System.Threading.Tasks.Task Call(TurnId turn, BlockingProvider provider) => System.Threading.Tasks.Task.Run(() => {
            using var scope = ExecutionScope.Begin(new(_run, LaneId: _lane, TurnId: turn));
            hub.Complete(_session, provider, request, 16000, TestContext.Current.CancellationToken);
        });
        var a = Call(_turn, first); await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var b = Call(other, second); await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            first.Release.SetResult(true); await a.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            second.Release.SetResult(true); await b.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(other.ToString(), hub.Snapshot(_session).Context!.TurnId);
            Assert.Equal(200, hub.Snapshot(_session).Context!.Tokens.Value);
        }
        finally { first.Release.TrySetResult(true); second.Release.TrySetResult(true); }
    }

    private sealed class BlockingProvider(long input) : IModelProvider
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderCapabilities Capabilities => new(true, false, false);
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Started.TrySetResult(true); await Release.Task.WaitAsync(ct);
            yield return new ResponseCompleted(new ModelResponse([new TextBlock("fixture")], StopReason.EndTurn,
                new(input, 2, 0, 0, 0), null, new ProviderMetadata("fixture-request", "fixture", null)));
        }
    }
}
