namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Xunit;

public sealed class M5ConversationUsageConsistencyTests
{
    public enum Counter { CacheRead, CacheWrite, Reasoning }
    public enum InvocationSource { ModelStep, MetaModel }

    [Theory]
    [InlineData(Counter.CacheRead, InvocationSource.ModelStep)]
    [InlineData(Counter.CacheRead, InvocationSource.MetaModel)]
    [InlineData(Counter.CacheWrite, InvocationSource.ModelStep)]
    [InlineData(Counter.CacheWrite, InvocationSource.MetaModel)]
    [InlineData(Counter.Reasoning, InvocationSource.ModelStep)]
    [InlineData(Counter.Reasoning, InvocationSource.MetaModel)]
    public void Contradictory_reported_breakdown_makes_the_whole_session_measurement_unknown(
        Counter counter, InvocationSource source)
    {
        using var fixture = new Fixture();
        var usage = ContradictoryUsage(counter);
        fixture.AddInvocation(source, "same-invocation", usage, TokenUsageFields.All, 0.000014m);
        fixture.AddInvocation(source, "same-invocation", usage, TokenUsageFields.All, 0.000014m);
        if (source == InvocationSource.ModelStep)
            fixture.Add(new ModelCompleted(fixture.Turn, null), fixture.Session, fixture.Turn);

        // A valid record in another session must not repair, contaminate, or add to this session.
        var otherSession = SessionId.New();
        fixture.Add(new ModelStepCompleted(TurnId.New(), 0, new(2, 1, 0, 0, 0), StopReason.EndTurn,
            null, "2026-10-06", 0.000003m, TokenUsageFields.Input | TokenUsageFields.Output), otherSession);

        var measurement = fixture.Snapshot().Consumption;

        Assert.Equal(fixture.Session.ToString(), measurement.SessionId);
        Assert.Equal(MetricAvailability.Unknown, measurement.Tokens.Availability);
        Assert.Null(measurement.Tokens.Value);
        Assert.Equal(MetricAvailability.Unknown, measurement.Total.Availability);
        Assert.Null(measurement.Total.Value);
        Assert.Equal(MetricAvailability.Unknown, measurement.Cost.Availability);
        Assert.Null(measurement.Cost.Value);
        Assert.Equal(1, measurement.ModelInvocations); // duplicate completion is idempotent
        Assert.Equal(1, measurement.IncompleteInvocations);
        Assert.Equal(fixture.LastSequence(fixture.Session), measurement.BasedOnJournalSequence);
        Assert.Equal(measurement, fixture.Snapshot().Consumption);
        AssertUnknown(measurement.Breakdown!.Input);
        AssertUnknown(measurement.Breakdown.Output);
        AssertUnknown(measurement.Breakdown.CacheRead);
        AssertUnknown(measurement.Breakdown.CacheWrite);
    }

    [Theory]
    [InlineData(Counter.CacheRead)]
    [InlineData(Counter.CacheWrite)]
    [InlineData(Counter.Reasoning)]
    public void Equality_with_reported_aggregate_is_valid_and_breakdowns_are_not_double_counted(Counter counter)
    {
        using var fixture = new Fixture();
        var usage = BoundaryUsage(counter);
        fixture.AddInvocation(InvocationSource.ModelStep, "boundary", usage, TokenUsageFields.All, 0.000014m);

        var measurement = fixture.Snapshot().Consumption;

        Assert.Equal(MetricAvailability.Reported, measurement.Tokens.Availability);
        Assert.Equal(new TokenTotals(10, 4, usage.CacheRead, usage.CacheWrite), measurement.Tokens.Value);
        Assert.Equal(MetricAvailability.Reported, measurement.Total.Availability);
        Assert.Equal(14, measurement.Total.Value); // cache and reasoning remain included in aggregates
        Assert.Equal(MetricAvailability.Estimated, measurement.Cost.Availability);
        Assert.Equal(new Money(0.000014m, "USD"), measurement.Cost.Value);
        Assert.Equal(1, measurement.ModelInvocations);
        Assert.Equal(0, measurement.IncompleteInvocations);
        AssertReported(measurement.Breakdown!.Input, 10);
        AssertReported(measurement.Breakdown.Output, 4);
        AssertReported(measurement.Breakdown.CacheRead, usage.CacheRead);
        AssertReported(measurement.Breakdown.CacheWrite, usage.CacheWrite);
    }

    [Theory]
    [InlineData(Counter.CacheRead)]
    [InlineData(Counter.CacheWrite)]
    [InlineData(Counter.Reasoning)]
    public void Unreported_auxiliary_detail_does_not_invalidate_reported_aggregates_or_become_zero(
        Counter counter)
    {
        using var fixture = new Fixture();
        var usage = ContradictoryUsage(counter);
        fixture.AddInvocation(InvocationSource.ModelStep, "auxiliary-unreported", usage,
            TokenUsageFields.Input | TokenUsageFields.Output, 0.000014m);

        var measurement = fixture.Snapshot().Consumption;

        Assert.Equal(MetricAvailability.Reported, measurement.Tokens.Availability);
        Assert.Equal(MetricAvailability.Reported, measurement.Total.Availability);
        Assert.Equal(14, measurement.Total.Value);
        Assert.Equal(MetricAvailability.Estimated, measurement.Cost.Availability);
        Assert.Equal(new Money(0.000014m, "USD"), measurement.Cost.Value);
        Assert.Equal(1, measurement.ModelInvocations);
        Assert.Equal(0, measurement.IncompleteInvocations);
        AssertReported(measurement.Breakdown!.Input, 10);
        AssertReported(measurement.Breakdown.Output, 4);
        if (counter == Counter.CacheRead) AssertUnknown(measurement.Breakdown.CacheRead);
        else AssertUnknown(measurement.Breakdown.CacheWrite);
    }

    private static TokenUsage ContradictoryUsage(Counter counter) => counter switch
    {
        Counter.CacheRead => new(10, 4, 11, 0, 0),
        Counter.CacheWrite => new(10, 4, 0, 11, 0),
        Counter.Reasoning => new(10, 4, 0, 0, 5),
        _ => throw new ArgumentOutOfRangeException(nameof(counter)),
    };

    private static TokenUsage BoundaryUsage(Counter counter) => counter switch
    {
        Counter.CacheRead => new(10, 4, 10, 0, 0),
        Counter.CacheWrite => new(10, 4, 0, 10, 0),
        Counter.Reasoning => new(10, 4, 0, 0, 4),
        _ => throw new ArgumentOutOfRangeException(nameof(counter)),
    };

    private static void AssertUnknown(Metric<long?> metric)
    {
        Assert.Equal(MetricAvailability.Unknown, metric.Availability);
        Assert.Null(metric.Value);
    }

    private static void AssertReported(Metric<long?> metric, long value)
    {
        Assert.Equal(MetricAvailability.Reported, metric.Availability);
        Assert.Equal(value, metric.Value);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly InMemoryEventStore _store = new();
        private readonly IEventCodecRegistry _codecs = EventCodecs.Create();
        private readonly string _tempRoot = Path.Combine(Path.GetTempPath(),
            "omnicore-m5-conversation-usage-" + Guid.NewGuid().ToString("N"));
        private readonly RunId _run = RunId.New();
        private readonly LaneId _lane = LaneId.New();
        public SessionId Session { get; } = SessionId.New();
        public TurnId Turn { get; } = TurnId.New();
        public FileArtifactStore Artifacts { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_tempRoot);
            Artifacts = new FileArtifactStore(Path.Combine(_tempRoot, "artifacts"));
        }

        public void Add(DomainEventPayload payload, SessionId? session = null, TurnId? turn = null)
        {
            var owner = session ?? Session;
            _store.Append(owner, DomainEvent.Create(owner, payload.Type(), payload.SchemaVersion(), null,
                _run, _run, null, _lane, turn, null, null, [], _codecs.CodecFor(payload.Type()).Encode(payload)),
                DurabilityClass.Barrier, CancellationToken.None);
        }

        public void AddInvocation(InvocationSource source, string invocationId, TokenUsage usage,
            TokenUsageFields fields, decimal cost)
        {
            if (source == InvocationSource.ModelStep)
            {
                Add(new ModelStepStarted(Turn, 0, "fixture-model", 128, "Direct", null, null, null), Session, Turn);
                Add(new ModelStepCompleted(Turn, 0, usage, StopReason.EndTurn, null, "2026-10-06", cost, fields), Session, Turn);
                return;
            }

            var input = Artifacts.PutText("fixture input", "text/plain", ArtifactKind.Other, Sensitivity.Sensitive);
            var output = Artifacts.PutText("fixture output", "text/plain", ArtifactKind.Other, Sensitivity.Sensitive);
            Add(new MetaModelInvocationStarted(invocationId, _run, "fixture-operation", "fixture-fingerprint", input));
            Add(new MetaModelInvocationCompleted(invocationId, _run, "fixture-operation", "fixture-fingerprint",
                output, usage, cost, fields));
        }

        public SessionObservabilitySnapshot Snapshot() => new SessionObservationHub(_store, _codecs, Artifacts).Snapshot(Session);

        public long LastSequence(SessionId session) => _store.ReadFrom(session, 1).LastOrDefault()?.Sequence ?? 0;

        public void Dispose()
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
