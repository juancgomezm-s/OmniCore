using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>Real private SQLite and injected command faults; no provider calls or real consumption.</summary>
public sealed class RoutingCommandFailureBoundaryTests
{
    private static ModelRoute PaidRoute() => ModelRoute.DefaultForModel(
        "paid-model", "paid", "https://paid.example/v1", ProviderFamily.OpenAiChatCompatible);

    [Fact]
    public void Consent_append_then_throw_confirms_only_the_persisted_request_and_causal_range()
    {
        using var fixture = Fixture.Open();
        var (session, run) = fixture.StartRun();
        var before = fixture.Store.Inner.CurrentSequence(session);
        fixture.Store.ThrowAfterNextWrite = true;
        var result = fixture.Server.AuthorizeModelRoute(session, run, PaidRoute(), BillingMode.MeteredCurrency);
        Assert.False(result.Authorized);
        Assert.IsType<IOException>(result.Failure);
        Assert.Equal("error", result.Ack.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, result.Ack.Outcome?.Kind);
        var rows = fixture.Store.Inner.ReadFrom(session, before + 1);
        Assert.Equal(new[] { "interaction.requested", "run.awaiting_input" }, rows.Select(evt => evt.Type.ToString()));
        var row = rows[0];
        var request = Assert.IsType<InteractionRequested>(fixture.Codecs.Decode(row));
        Assert.Equal(InteractionKind.ModelRouteConsent, request.Kind);
        Assert.Equal(request.InteractionId, result.Interaction);
        Assert.Equal(row.Sequence, result.Ack.FirstSeq);
        Assert.Equal(rows[1].Sequence, result.Ack.LastSeq);
        Assert.Equal(result.Ack.CommandId,
            Assert.IsType<CommandCausation>(row.Causation).CommandId.Value.ToString());
        Assert.Equal(run, row.RunId);
        Assert.All(rows, item =>
        {
            Assert.Equal(run, item.RunId);
            Assert.Equal(row.Causation, item.Causation);
        });
        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_client_admission_read_failure_returns_unavailable_outcome_without_effects(bool budget)
    {
        using var fixture = Fixture.Open();
        var (session, run) = fixture.StartRun();
        var before = fixture.Store.Inner.CurrentSequence(session);
        fixture.Store.FailNextRead = true;
        var result = budget
            ? fixture.Server.ResolveBudgetWithoutClient(session, run, InteractionId.New())
            : fixture.Server.ResolveModelRouteWithoutClient(session, run, InteractionId.New());
        Assert.IsType<IOException>(result.Failure);
        Assert.Equal("error", result.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, result.Outcome?.Kind);
        Assert.Null(result.FirstSeq);
        Assert.Null(result.LastSeq);
        Assert.Equal(before, fixture.Store.Inner.CurrentSequence(session));
        Assert.Empty(fixture.Store.Inner.ReadFrom(session, before + 1));
        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
    }

    [Fact]
    public void Consent_append_then_throw_keeps_pending_interaction_and_retry_does_not_duplicate()
    {
        using var fixture = Fixture.Open();
        var (session, run) = fixture.StartRun();
        fixture.Store.ThrowAfterNextWrite = true;
        fixture.Store.FailConfirmationReadOnNextWrite = true; // force the ACK classifier to report uncertainty

        var first = fixture.Server.AuthorizeModelRoute(session, run, PaidRoute(), BillingMode.MeteredCurrency);

        Assert.False(first.Authorized);
        Assert.Null(first.Interaction);
        Assert.IsType<IOException>(first.Failure);
        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, first.Ack.Outcome?.Kind);
        Assert.Null(first.Ack.FirstSeq);
        Assert.Null(first.Ack.LastSeq);

        // Once the one-shot read failure is consumed, the actual journal proves that the
        // consent request committed; retry must return this same pending ID, not append again.
        var requests = fixture.Store.Inner.ReadFrom(session, 1)
            .Select(fixture.Codecs.Decode).OfType<InteractionRequested>()
            .Where(item => item.Kind == InteractionKind.ModelRouteConsent).ToArray();
        var interaction = Assert.Single(requests).InteractionId;
        var sequence = fixture.Store.Inner.CurrentSequence(session);
        var retry = fixture.Server.AuthorizeModelRoute(session, run, PaidRoute(), BillingMode.MeteredCurrency);
        Assert.False(retry.Authorized);
        Assert.Equal(interaction, retry.Interaction);
        Assert.Null(retry.Failure);
        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, retry.Ack.Outcome?.Kind);
        Assert.Equal(sequence, fixture.Store.Inner.CurrentSequence(session));
    }

    [Fact]
    public void No_client_route_deny_append_then_throw_reports_durable_range_without_reopening_permission()
    {
        using var fixture = Fixture.Open();
        var (session, run) = fixture.StartRun();
        var consent = fixture.Server.AuthorizeModelRoute(session, run, PaidRoute(), BillingMode.MeteredCurrency);
        var interaction = Assert.IsType<InteractionId>(consent.Interaction);
        var before = fixture.Store.Inner.CurrentSequence(session);
        fixture.Store.ThrowAfterNextWrite = true;

        var result = fixture.Server.ResolveModelRouteWithoutClient(session, run, interaction);

        Assert.IsType<IOException>(result.Failure);
        Assert.Equal("error", result.Ack.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, result.Ack.Outcome?.Kind);
        var durable = fixture.Store.Inner.ReadFrom(session, before + 1);
        var resolved = Assert.Single(durable.Select(fixture.Codecs.Decode).OfType<InteractionResolved>());
        Assert.Equal(interaction, resolved.InteractionId);
        Assert.Equal("deny", resolved.OptionId);
        Assert.Equal(InteractionCause.NoClient, resolved.Cause);
        Assert.Equal(durable.First().Sequence, result.Ack.FirstSeq);
        Assert.Equal(durable.Last().Sequence, result.Ack.LastSeq);
        Assert.DoesNotContain(durable.Select(fixture.Codecs.Decode), item =>
            item is SessionRoutingPolicyRevised);

        var sequence = fixture.Store.Inner.CurrentSequence(session);
        var retry = fixture.Server.ResolveModelRouteWithoutClient(session, run, interaction);
        Assert.Equal("error", retry.Ack.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, retry.Ack.Outcome?.Kind);
        Assert.Equal(sequence, fixture.Store.Inner.CurrentSequence(session));
    }

    [Fact]
    public void Allowed_policy_with_failed_command_outcome_read_is_not_authorized_for_caller()
    {
        using var fixture = Fixture.Open(allowLocalRoute: true);
        var (session, run) = fixture.StartRun();
        var persistedPolicy = SessionRoutingAuthorization.Read(fixture.Store.Inner.ReadFrom(session, 1),
            fixture.Codecs, session);
        Assert.NotNull(persistedPolicy);
        Assert.True(persistedPolicy.Allows(fixture.LocalRoute, BillingMode.Local));
        // Only the range-confirmation ReadFrom starts after the current checkpoint.
        // Fail that query after the policy match, not admission or the sequence counter.
        fixture.Store.FailNextRangeRead = true;

        var result = fixture.Server.AuthorizeModelRoute(session, run, fixture.LocalRoute, BillingMode.Local);

        Assert.False(result.Authorized);
        Assert.Null(result.Interaction);
        Assert.Null(result.Failure);
        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, result.Ack.Outcome?.Kind);
        // OmniCliRuntime.RunTurnAsync checks the returned code before ConnectProvider at
        // OmniCliRuntime.cs around the AuthorizeRouteForInvocation call (lines ~581–590).
        // This direct assertion is the contract that prevents the pre-read policy decision
        // from being used when its command outcome could not be correlated.
        Assert.Null(result.Ack.FirstSeq);
        Assert.Null(result.Ack.LastSeq);
    }

    [Fact]
    public void Budget_denial_append_then_throw_retains_effects_and_original_command_causation()
    {
        using var fixture = Fixture.Open();
        var (session, run) = fixture.StartRun();
        var interaction = fixture.AppendBudgetInteraction(session, run);
        var before = fixture.Store.Inner.CurrentSequence(session);
        fixture.Store.ThrowAfterNextWrite = true;
        fixture.Store.FailConfirmationReadOnNextWrite = true;

        var result = fixture.Server.ResolveBudgetWithoutClient(session, run, interaction);

        Assert.IsType<IOException>(result.Failure);
        Assert.Equal("error", result.Ack.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Deferred, result.Ack.Outcome?.Kind);
        Assert.Null(result.Ack.FirstSeq);
        Assert.Null(result.Ack.LastSeq);

        var durable = fixture.Store.Inner.ReadFrom(session, before + 1);
        Assert.Equal(5, durable.Count);
        Assert.Equal(Enumerable.Range(1, 5).Select(offset => before + offset),
            durable.Select(item => item.Sequence));
        var causation = Assert.IsType<CommandCausation>(durable[0].Causation);
        Assert.Equal(result.Ack.CommandId, causation.CommandId.Value.ToString());
        Assert.All(durable, item => Assert.Equal(causation, item.Causation));
        var payloads = durable.Select(fixture.Codecs.Decode).ToArray();
        Assert.Collection(payloads,
            item => Assert.IsType<InteractionResolved>(item),
            item => Assert.IsType<LaneCancelled>(item),
            item => Assert.IsType<TaskCancelled>(item),
            item => Assert.IsType<PlanItemCancelled>(item),
            item => Assert.IsType<RunFailed>(item));
        Assert.All(durable, item => Assert.Equal(run, item.RunId));
        Assert.Equal(interaction, Assert.Single(payloads.OfType<InteractionResolved>()).InteractionId);
        Assert.Equal("deny", Assert.Single(payloads.OfType<InteractionResolved>()).OptionId);
        Assert.Equal("BudgetExceeded", Assert.Single(payloads.OfType<RunFailed>()).Cause);
        Assert.Equal(RunState.Failed, RunProjection.Replay(session, run, fixture.Codecs,
            fixture.Store.Inner.ReadFrom(session, 1)).State);
        Assert.Null(OmniCliRuntime.Create(fixture.Root)
            .HandlePendingBudget(fixture.Server, session, run, false, _ => { }));
        Assert.Equal(before + 5, fixture.Store.Inner.CurrentSequence(session));
    }

    [Theory]
    [InlineData(false, false, RuntimeCommandOutcomeKind.Rejected)]
    [InlineData(true, false, RuntimeCommandOutcomeKind.Deferred)]
    [InlineData(true, true, RuntimeCommandOutcomeKind.Deferred)]
    public void Prewrite_failure_or_unreadable_outcome_never_claims_zero_effects(
        bool failOutcomeRead, bool commitFirst, RuntimeCommandOutcomeKind expected)
    {
        // The first parameter is deliberately an explicit uncertainty control. A failure to
        // read the post-command range must remain Deferred even if this fixture later observes
        // no rows; production must not infer “no effects” from an unavailable read.
        using var fixture = Fixture.Open();
        var (session, run) = fixture.StartRun();
        var interaction = fixture.AppendBudgetInteraction(session, run);
        var before = fixture.Store.Inner.CurrentSequence(session);
        fixture.Store.ThrowAfterNextWrite = commitFirst;
        fixture.Store.ThrowBeforeNextWrite = !commitFirst;
        fixture.Store.FailConfirmationReadOnNextWrite = failOutcomeRead;

        var result = fixture.Server.ResolveBudgetWithoutClient(session, run, interaction);

        Assert.IsType<IOException>(result.Failure);
        Assert.Equal(expected, result.Ack.Outcome?.Kind);
        Assert.Equal("error", result.Ack.Status);
        if (expected == RuntimeCommandOutcomeKind.Rejected)
        {
            Assert.Null(result.Ack.FirstSeq);
            Assert.Null(result.Ack.LastSeq);
            Assert.Empty(fixture.Store.Inner.ReadFrom(session, before + 1));
        }
        else
        {
            Assert.Null(result.Ack.FirstSeq);
            Assert.Null(result.Ack.LastSeq);
            // The underlying rows are inspected only after ACK formation to prove that
            // Deferred is uncertainty, not a claim that the command wrote nothing.
            var durable = fixture.Store.Inner.ReadFrom(session, before + 1);
            if (commitFirst) Assert.Equal(5, durable.Count);
            else Assert.Empty(durable);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Production_runtime_route_gate_blocks_unconfirmed_authorization_even_with_a_live_client(bool liveClient)
    {
        using var fixture = Fixture.Open(allowLocalRoute: true);
        var (session, run) = fixture.StartRun();
        var before = fixture.Store.Inner.CurrentSequence(session);
        // First range query confirms EnsureSessionRoutingPolicy NoOp; second confirms authorization.
        fixture.Store.FailOnRangeReadCall = 2;
        var runtime = OmniCliRuntime.Create(fixture.Root);
        runtime.UseConsoleInput = false;
        runtime.HasInteractionClient = liveClient;
        var output = new List<string>();
        Assert.Equal(1, runtime.AuthorizeRouteForInvocation(fixture.Server, session, run,
            fixture.LocalRoute, BillingMode.Local, output.Add, "es"));
        Assert.NotEmpty(output);
        Assert.DoesNotContain(output, line => line.Contains("InputRequired", StringComparison.Ordinal));
        Assert.Equal(before, fixture.Store.Inner.CurrentSequence(session));
        Assert.Empty(fixture.Store.Inner.ReadFrom(session, before + 1));
        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
    }

    [Fact]
    public void Budget_denial_cancellation_keeps_ack_token_and_consumer_propagation_without_effects()
    {
        using var fixture = Fixture.Open();
        var (session, run) = fixture.StartRun();
        var interaction = fixture.AppendBudgetInteraction(session, run);
        var before = fixture.Store.Inner.CurrentSequence(session);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var original = new OperationCanceledException(cancellation.Token);
        fixture.Store.WriteFailure = original;
        fixture.Store.ThrowBeforeNextWrite = true;
        var result = fixture.Server.ResolveBudgetWithoutClient(session, run, interaction);
        Assert.Same(original, result.Failure);
        Assert.Equal("error", result.Status);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, result.Outcome?.Kind);
        Assert.Null(result.FirstSeq);
        Assert.Null(result.LastSeq);
        var thrown = Assert.Throws<OperationCanceledException>(() => result.ThrowIfFailure());
        Assert.Same(original, thrown);
        Assert.Equal(cancellation.Token, thrown.CancellationToken);
        Assert.Equal(before, fixture.Store.Inner.CurrentSequence(session));
        Assert.Empty(fixture.Store.Inner.ReadFrom(session, before + 1));
        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteEventStore _sqlite;
        private readonly string _dbPath;
        public string Root { get; }
        public IEventCodecRegistry Codecs { get; }
        public FaultingEventStore Store { get; }
        public OmniServer Server { get; }
        public ModelRoute LocalRoute { get; } = ModelRoute.DefaultForModel(
            "local-model", "local", "http://127.0.0.1:1/v1", ProviderFamily.OpenAiChatCompatible);

        private Fixture(bool allowLocalRoute)
        {
            Root = Path.Combine(Path.GetTempPath(), "omni-routing-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            _dbPath = Path.Combine(Root, "journal.db");
            _sqlite = new SqliteEventStore(_dbPath);
            Store = new FaultingEventStore(_sqlite);
            Codecs = EventCodecs.Create();
            Server = new OmniServer(Store, Codecs, new InMemoryAuditSink(),
                Path.Combine(Root, "lastsession.txt"), new FileArtifactStore(Root));
            if (allowLocalRoute)
            {
                var config = new ConfigLoader().Load("""
                    providers:
                      local: { baseUrl: 'http://127.0.0.1:1/v1', auth: none, billingMode: Local }
                    """, """
                    models:
                      local-model: { provider: local, context: 8192 }
                    """);
                Server.ConfigureNewSessionRoutingPolicy(ModelRoutingHost.InitialSessionPolicy(config, "local"));
            }
        }

        public static Fixture Open(bool allowLocalRoute = false) => new(allowLocalRoute);

        public (SessionId Session, RunId Run) StartRun()
        {
            Assert.Equal("ok", Server.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"session.input\",\"text\":\"command failure probe\"}"),
                CancellationToken.None).Status);
            return (Server.LastSessionId()!, Server.LastRunId()!);
        }

        public InteractionId AppendBudgetInteraction(SessionId session, RunId run)
        {
            var id = InteractionId.New();
            using var execution = ExecutionScope.Begin(new ExecutionScopeState(RunId: run));
            new EventStream(Store, Codecs, session).Append(new InteractionRequested(id,
                InteractionKind.BudgetExceeded,
                BudgetContinuation.Context("limit", new("session", 5m, 5m, null, null)),
                "[{\"id\":\"deny\"},{\"id\":\"allow_plus\",\"value\":10}]", "deny",
                null, null, null, null, 0, 1));
            return id;
        }

        public void Dispose()
        {
            _sqlite.Close();
            SqliteConnection.ClearPool((SqliteConnection)_sqlite.Connection);
            _sqlite.Connection.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FaultingEventStore(IEventStore inner) : IEventStore
    {
        public IEventStore Inner { get; } = inner;
        public bool ThrowAfterNextWrite { get; set; }
        public bool ThrowBeforeNextWrite { get; set; }
        public bool FailNextRead { get; set; }
        public bool FailConfirmationReadOnNextWrite { get; set; }
        public bool FailNextRangeRead { get; set; }
        public int? FailOnRangeReadCall { get; set; }
        public Exception? WriteFailure { get; set; }

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
            if (ThrowBeforeNextWrite) FailBeforeWrite();
            Inner.Append(sessionId, evt, durability, cancellationToken);
            FailAfterWriteIfArmed();
        }

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken cancellationToken)
        {
            if (ThrowBeforeNextWrite) FailBeforeWrite();
            Inner.AppendBatch(sessionId, events, durability, cancellationToken);
            FailAfterWriteIfArmed();
        }

        public long CurrentSequence(SessionId sessionId)
        {
            return Inner.CurrentSequence(sessionId);
        }

        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive)
        {
            if (fromSequenceInclusive > 1 && FailOnRangeReadCall is { } remaining)
            {
                FailOnRangeReadCall = remaining - 1;
                if (remaining == 1) FailNextRangeRead = true;
            }
            if (FailNextRead || (FailNextRangeRead && fromSequenceInclusive > 1))
            {
                FailNextRead = false;
                FailNextRangeRead = false;
                throw new IOException("synthetic command-outcome journal read failure");
            }
            return Inner.ReadFrom(sessionId, fromSequenceInclusive);
        }

        private void ArmConfirmationFault()
        {
            if (!FailConfirmationReadOnNextWrite) return;
            FailConfirmationReadOnNextWrite = false;
            FailNextRead = true;
        }

        private void FailBeforeWrite()
        {
            ThrowBeforeNextWrite = false;
            ArmConfirmationFault();
            throw WriteFailure ?? new IOException("synthetic append-before-commit failure");
        }

        private void FailAfterWriteIfArmed()
        {
            ArmConfirmationFault();
            if (!ThrowAfterNextWrite) return;
            ThrowAfterNextWrite = false;
            throw new IOException("synthetic append-after-commit failure");
        }
    }
}
