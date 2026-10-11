using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>Codec fixtures only: measured zero, missing data and non-dispatch are distinct.</summary>
public sealed class RunTokenBudgetReaderTests
{
    [Theory]
    [InlineData("missing_input")]
    [InlineData("missing_output")]
    [InlineData("unreported_zero")]
    [InlineData("negative")]
    [InlineData("reasoning_gt_output")]
    [InlineData("cache_gt_input")]
    [InlineData("overflow")]
    [InlineData("unsettled")]
    public void Unknown_invalid_or_unsettled_usage_never_authorizes_another_invocation(string condition)
    {
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = RunId.New();
        var turn = TurnId.New();
        var usage = condition switch
        {
            "negative" => new TokenUsage(-1, 3, 0, 0, 0),
            "reasoning_gt_output" => new TokenUsage(2, 3, 0, 0, 4),
            "cache_gt_input" => new TokenUsage(2, 3, 3, 0, 0),
            "overflow" => new TokenUsage(long.MaxValue, 1, 0, 0, 0),
            "unreported_zero" => new TokenUsage(0, 0, 0, 0, 0),
            _ => new TokenUsage(2, 3, 0, 0, 0),
        };
        var fields = condition switch
        {
            "missing_input" => TokenUsageFields.Output,
            "missing_output" => TokenUsageFields.Input,
            "unreported_zero" => TokenUsageFields.None,
            _ => TokenUsageFields.All,
        };
        var payloads = new List<DomainEventPayload>
        {
            new ModelStepStarted(turn, 1, "fixture", 100, "Direct", null, null, null),
        };
        if (condition != "unsettled")
            payloads.Add(new ModelStepCompleted(turn, 1, usage, StopReason.EndTurn, null, "2026-10-07", null, fields,
                new GenerationRequestAttemptEvidence(1, 1)));
        var result = RunTokenBudgetReader.Read(Encode(payloads, codecs, session, run), codecs, run, 10);
        Assert.Null(result.Remaining);
        Assert.NotNull(result.Limitation);
    }

    [Theory]
    [InlineData("not_dispatched", 10L)]
    [InlineData("reported_zero", 10L)]
    [InlineData("legacy_reported_fields", 5L)]
    [InlineData("overdrawn", -1L)]
    public void Known_usage_and_known_non_dispatch_keep_their_distinct_canonical_meaning(string condition, long remaining)
    {
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = RunId.New();
        var turn = TurnId.New();
        var payloads = new List<DomainEventPayload>
        {
            new ModelStepStarted(turn, 1, "fixture", 100, "Direct", null, null, null),
        };
        if (condition == "not_dispatched") payloads.Add(new ModelStepNotDispatched(turn, 1));
        else payloads.Add(new ModelStepCompleted(turn, 1,
            condition == "reported_zero" ? new TokenUsage(0, 0, 0, 0, 0) : new TokenUsage(2, 3, 0, 0, 0),
            StopReason.EndTurn, null, "2026-10-07", null,
            condition == "legacy_reported_fields" ? null : TokenUsageFields.All,
            new GenerationRequestAttemptEvidence(1, 1)));
        var events = Encode(payloads, codecs, session, run);
        var limit = condition == "overdrawn" ? 4 : 10;
        var result = RunTokenBudgetReader.Read(events, codecs, run, limit);
        Assert.Equal(remaining, result.Remaining);
        Assert.Null(result.Limitation);
        Assert.Equal(result, RunTokenBudgetReader.Read(events.Concat(events).ToArray(), codecs, run, limit));
    }

    private static DomainEvent[] Encode(IEnumerable<DomainEventPayload> payloads, EventCodecs codecs,
        SessionId session, RunId run) => payloads.Select(payload => DomainEvent.Create(session, payload.Type(),
            payload.SchemaVersion(), null, run, run, null, null, null, null, null, [],
            codecs.CodecFor(payload.Type()).Encode(payload))).ToArray();

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(0L, 1L, true)]
    [InlineData(0L, 2L, false)]
    [InlineData(1L, null, false)]
    [InlineData(1L, 3L, true)]
    [InlineData(2L, 3L, false)]
    public void Legacy_and_retry_uncertainty_are_not_reported_as_complete_consumption(long? sends, long? bound, bool known)
    {
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = RunId.New();
        var turn = TurnId.New();
        var attempts = sends is { } count ? new GenerationRequestAttemptEvidence(count, bound) : null;
        var events = Encode([
            new ModelStepStarted(turn, 0, "fixture", 100, "Direct", null, null, null),
            new ModelStepCompleted(turn, 0, new TokenUsage(17, 4, 0, 0, 0), StopReason.EndTurn,
                null, "2026-10-07", null, TokenUsageFields.Input | TokenUsageFields.Output, attempts),
        ], codecs, session, run);
        var result = RunTokenBudgetReader.Read(events, codecs, run, 100);
        Assert.Equal(known ? 79L : (long?)null, result.Remaining);
        Assert.Equal(known, result.Limitation is null);
        Assert.Equal(result, RunTokenBudgetReader.Read(events.Concat(events).ToArray(), codecs, run, 100));
    }

    [Fact]
    public void Claude_aggregate_usage_receipt_accounts_for_cache_tokens_without_fabricating_attempt_evidence()
    {
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = RunId.New();
        var task = TaskId.New();
        var lane = LaneId.New();
        var execution = ExecutionId.New();
        var turn = TurnId.New();
        var result = new ArtifactRef(ArtifactId.New(), ContentHash.Sha256(new string('a', 64)), 128,
            "application/vnd.omnicore.agent-result+json", ArtifactKind.Other, Sensitivity.Sensitive);
        var start = new ModelStepStarted(turn, 0, "claude-haiku-5-5", 1000, "claude-code", null, null, null);
        var receipt = new ExternalLaneUsageRecorded(execution, lane, "session-123", 100, 20, 2, 3,
            0.01m, 1, 8, 2048, result);
        var complete = new ModelStepCompleted(turn, 0, new TokenUsage(105, 20, 2, 3, 0),
            StopReason.EndTurn, null, "2026-10-10", 0.01m,
            TokenUsageFields.Input | TokenUsageFields.Output | TokenUsageFields.CacheRead | TokenUsageFields.CacheWrite);
        var events = new DomainEventPayload[] { start, receipt, complete }.Select(payload => DomainEvent.Create(
            session, payload.Type(), payload.SchemaVersion(), null, run, run, task, lane,
            payload is ModelStepStarted or ModelStepCompleted ? turn : null, null, null, [],
            codecs.CodecFor(payload.Type()).Encode(payload), execution)).ToArray();

        var budget = RunTokenBudgetReader.Read(events, codecs, run, 1000);
        Assert.Equal(875, budget.Remaining);
        Assert.Null(budget.Limitation);
        Assert.Equal(125, budget.ObservedSettledTokens);

        var resumedReceiptEvents = Encode([start,
            receipt with { UsageCoverageComplete = false }, complete], codecs, session, run);
        var resumedBudget = RunTokenBudgetReader.Read(resumedReceiptEvents, codecs, run, 1000);
        Assert.Null(resumedBudget.Remaining);
        Assert.NotNull(resumedBudget.Limitation);
        Assert.Null(resumedBudget.ObservedSettledTokens);

        var missingReceipt = RunTokenBudgetReader.Read(events.Where(evt => codecs.Decode(evt)
            is not ExternalLaneUsageRecorded).ToArray(), codecs, run, 1000);
        Assert.Null(missingReceipt.Remaining);
        Assert.Equal("generation attempt usage coverage unknown", missingReceipt.Limitation);
    }
}
