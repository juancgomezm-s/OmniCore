using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>Private SQLite/CAS journal fixtures; no authenticated provider or billing claims.</summary>
public sealed class ConversationUsageSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Identical_terminal_receipts_and_repeated_queries_do_not_duplicate_spending(bool meta)
    {
        using var fixture = new Fixture();
        var turn = TurnId.New();
        fixture.Start(meta, turn);
        fixture.Complete(meta, turn);
        fixture.Complete(meta, turn);
        var first = fixture.Read();
        Assert.Equal(21L, first.Total.Value);
        Assert.Equal(new Money(.000021m, "USD"), first.Cost.Value);
        Assert.Equal(1, first.ModelInvocations);
        Assert.Equal(0, first.IncompleteInvocations);
        Assert.Equal(first, fixture.Read());
        fixture.Reopen();
        Assert.Equal(first, fixture.Read());
    }

    [Fact]
    public void Other_session_receipts_with_the_same_invocation_identity_cannot_contaminate_measurement()
    {
        using var fixture = new Fixture();
        var turn = TurnId.New();
        fixture.Start(false, turn);
        fixture.Complete(false, turn);
        var first = fixture.Read();
        fixture.ForeignReceipt(turn);
        Assert.Equal(first, fixture.Read());
        fixture.Reopen();
        Assert.Equal(first, fixture.Read());
        Assert.Equal(21L, first.Total.Value);
        Assert.Equal(1, first.ModelInvocations);
    }

    [Fact]
    public async System.Threading.Tasks.Task Actual_meta_predispatch_failure_keeps_zero_provider_calls_and_no_unknown_spending()
    {
        using var fixture = new Fixture();
        var provider = new NeverCalledProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.FailMetaBeforeDispatch(provider));
        Assert.Equal(0, provider.Calls);
        var first = fixture.Read();
        Assert.Equal(0L, first.Total.Value);
        Assert.Equal(0, first.ModelInvocations);
        Assert.Equal(0, first.IncompleteInvocations);
        fixture.Reopen();
        Assert.Equal(first, fixture.Read());
    }

    private sealed class NeverCalledProvider : IModelProvider
    {
        public int Calls;
        public ProviderCapabilities Capabilities { get; } = ProviderCapabilities.Local();
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await System.Threading.Tasks.Task.Yield();
            yield break;
        }
    }

    [Theory]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("cacheRead")]
    [InlineData("cacheWrite")]
    [InlineData("total")]
    public void Unrepresentable_token_sum_remains_unknown_and_queryable_after_reopen(string field)
    {
        using var fixture = new Fixture();
        var usage = field switch
        {
            "input" => new TokenUsage(long.MaxValue, 0, 0, 0, 0),
            "output" => new TokenUsage(0, long.MaxValue, 0, 0, 0),
            "cacheRead" => new TokenUsage(long.MaxValue, 0, long.MaxValue, 0, 0),
            "cacheWrite" => new TokenUsage(long.MaxValue, 0, 0, long.MaxValue, 0),
            _ => new TokenUsage(long.MaxValue, 1, 0, 0, 0),
        };
        fixture.Spend(usage, 0m);
        if (field != "total") fixture.Spend(usage, 0m);
        var first = fixture.Read();
        Assert.Equal(MetricAvailability.Unknown, first.Total.Availability);
        Assert.Null(first.Total.Value);
        Assert.Equal(MetricAvailability.Unknown, first.Tokens.Availability);
        Assert.Null(first.Tokens.Value);
        Assert.Null(first.Cost.Value);
        var snapshot = SessionUsageReporter.Build(new TokenTotals(0, 0, 0, 0), 0m, false, true, false,
            [], first.AsOf, first.Tokens);
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<UsageSnapshot>(
            System.Text.Json.JsonSerializer.Serialize(snapshot))!;
        Assert.Equal(first.Tokens, roundTrip.SessionTokenMeasurement);
        var status = OmniCore.Cli.CliApp.StatusLineText(roundTrip);
        Assert.Contains("session — tok", status);
        Assert.DoesNotContain("session 0 tok", status);
        fixture.Reopen();
        Assert.Equal(first, fixture.Read());
        Assert.Equal(first, fixture.Read());
    }

    [Fact]
    public void Cost_overflow_does_not_destroy_representable_token_measurements()
    {
        using var fixture = new Fixture();
        fixture.Spend(new TokenUsage(17, 4, 0, 0, 0), decimal.MaxValue);
        fixture.Spend(new TokenUsage(17, 4, 0, 0, 0), 1m);
        var first = fixture.Read();
        Assert.Equal(42L, first.Total.Value);
        Assert.Equal(MetricAvailability.Reported, first.Total.Availability);
        Assert.Equal(MetricAvailability.Unknown, first.Cost.Availability);
        Assert.Null(first.Cost.Value);
        fixture.Reopen();
        Assert.Equal(first, fixture.Read());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Confirmed_no_dispatch_does_not_leave_fictitious_unknown_spending(bool meta)
    {
        using var fixture = new Fixture();
        fixture.Spend(new TokenUsage(17, 4, 0, 0, 0), 0.000021m);
        var turn = TurnId.New();
        fixture.Start(meta, turn);
        fixture.NotDispatched(meta, turn);
        fixture.NotDispatched(meta, turn); // repeated receipt is idempotent
        var first = fixture.Read();
        Assert.Equal(21L, first.Total.Value);
        Assert.Equal(new Money(0.000021m, "USD"), first.Cost.Value);
        Assert.Equal(1, first.ModelInvocations); // a request that never entered the provider is not spending
        Assert.Equal(0, first.IncompleteInvocations);
        fixture.Reopen();
        Assert.Equal(first, fixture.Read());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void No_dispatch_cannot_erase_a_conflicting_reported_receipt(bool meta, bool terminalFirst)
    {
        using var fixture = new Fixture();
        var turn = TurnId.New();
        fixture.Start(meta, turn);
        if (terminalFirst) fixture.Complete(meta, turn);
        fixture.NotDispatched(meta, turn);
        if (!terminalFirst) fixture.Complete(meta, turn);
        var first = fixture.Read();
        Assert.Null(first.Total.Value);
        Assert.Null(first.Cost.Value);
        Assert.Equal(1, first.ModelInvocations);
        fixture.Reopen();
        Assert.Equal(first, fixture.Read());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unmatched_no_dispatch_marker_is_not_proof_of_known_zero(bool meta)
    {
        using var fixture = new Fixture();
        fixture.NotDispatched(meta, TurnId.New());
        var first = fixture.Read();
        Assert.Null(first.Total.Value);
        Assert.Null(first.Cost.Value);
        Assert.Equal(1, first.IncompleteInvocations);
        fixture.Reopen();
        Assert.Equal(first, fixture.Read());
    }

    [Fact]
    public void Conflicting_duplicate_without_usage_cannot_be_replaced_by_a_successful_snapshot()
    {
        using var fixture = new Fixture();
        var turn = TurnId.New();
        fixture.Start(true, turn);
        fixture.FailWithoutUsage(turn);
        fixture.Complete(true, turn);
        var first = fixture.Read();
        Assert.Null(first.Total.Value);
        Assert.Null(first.Cost.Value);
        fixture.Reopen();
        Assert.Equal(first, fixture.Read());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-conversation-safety-" + Guid.NewGuid().ToString("N"));
        private readonly EventCodecs _codecs = EventCodecs.Create();
        private readonly SessionId _session = SessionId.New();
        private readonly RunId _run = RunId.New();
        private readonly FileArtifactStore _artifacts;
        private SqliteEventStore _store;
        public Fixture()
        {
            Directory.CreateDirectory(_root);
            _store = new SqliteEventStore(Path.Combine(_root, "journal.db"));
            _artifacts = new FileArtifactStore(Path.Combine(_root, "cas"));
        }
        private void Add(DomainEventPayload payload) => _store.Append(_session,
            DomainEvent.Create(_session, payload.Type(), payload.SchemaVersion(), null, _run, _run,
                null, null, null, null, null, [], _codecs.CodecFor(payload.Type()).Encode(payload)),
            DurabilityClass.Barrier, TestContext.Current.CancellationToken);
        public void Spend(TokenUsage usage, decimal cost)
        {
            var turn = TurnId.New();
            Start(false, turn);
            Add(new ModelStepCompleted(turn, 0, usage, StopReason.EndTurn, null, "2026-10-07",
                cost, TokenUsageFields.All, new GenerationRequestAttemptEvidence(1, 1)));
        }
        public void Start(bool meta, TurnId turn)
        {
            if (meta) Add(new MetaModelInvocationStarted(turn.ToString(), _run, "compact", "fixture",
                _artifacts.PutText("fixture", "text/plain", ArtifactKind.Other, Sensitivity.Normal)));
            else Add(new ModelStepStarted(turn, 0, "fixture", 8192, "Direct", null, null, null));
        }
        public void NotDispatched(bool meta, TurnId turn)
        {
            if (meta) Add(new MetaModelInvocationNotDispatched(turn.ToString(), _run, "compact", "fixture"));
            else Add(new ModelStepNotDispatched(turn, 0));
        }
        public void Complete(bool meta, TurnId turn)
        {
            if (meta) Add(new MetaModelInvocationCompleted(turn.ToString(), _run, "compact", "fixture",
                _artifacts.PutText("result", "text/plain", ArtifactKind.ModelResponse, Sensitivity.Normal),
                new TokenUsage(17, 4, 0, 0, 0), .000021m, TokenUsageFields.All,
                new GenerationRequestAttemptEvidence(1, 1)));
            else Add(new ModelStepCompleted(turn, 0, new TokenUsage(17, 4, 0, 0, 0), StopReason.EndTurn,
                null, "2026-10-07", .000021m, TokenUsageFields.All, new GenerationRequestAttemptEvidence(1, 1)));
        }
        public void FailWithoutUsage(TurnId turn) => Add(new MetaModelInvocationFailed(turn.ToString(), _run,
            "compact", "fixture", "fixture transport failure", null, null, TokenUsageFields.None,
            new GenerationRequestAttemptEvidence(1, 1)));
        public void ForeignReceipt(TurnId turn)
        {
            var otherSession = SessionId.New();
            var otherRun = RunId.New();
            var payload = new ModelStepCompleted(turn, 0, new TokenUsage(999, 999, 0, 0, 0),
                StopReason.EndTurn, null, "2026-10-07", 9m, TokenUsageFields.All,
                new GenerationRequestAttemptEvidence(1, 1));
            _store.Append(otherSession, DomainEvent.Create(otherSession, payload.Type(), payload.SchemaVersion(),
                null, otherRun, otherRun, null, null, null, null, null, [],
                _codecs.CodecFor(payload.Type()).Encode(payload)), DurabilityClass.Barrier,
                TestContext.Current.CancellationToken);
        }
        public ConversationUsageMeasurement Read() => SessionUsageReporter.ReadConversation(_store, _codecs, _artifacts, _session);
        public System.Threading.Tasks.Task<string> FailMetaBeforeDispatch(IModelProvider provider)
        {
            var service = new MetaModelService(provider, _artifacts, new FixtureContextSink(this),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
                dispatch: _ => throw new InvalidOperationException("Fixture pre-dispatch rejection."));
            return service.SummarizeAsync(_run, "compact", "fixture context", 1000, TestContext.Current.CancellationToken);
        }
        private sealed class FixtureContextSink(Fixture fixture) : IContextEventSink
        {
            public ValueTask AppendAsync(DomainEventPayload payload, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fixture.Add(payload);
                return ValueTask.CompletedTask;
            }
        }
        private void Close()
        {
            var connection = (SqliteConnection)_store.Connection;
            _store.Close();
            SqliteConnection.ClearPool(connection);
            connection.Dispose();
        }
        public void Reopen()
        {
            Close();
            _store = new SqliteEventStore(Path.Combine(_root, "journal.db"));
        }
        public void Dispose()
        {
            Close();
            Directory.Delete(_root, recursive: true);
        }
    }
}
