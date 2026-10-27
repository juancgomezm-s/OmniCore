using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Abstractions;

namespace OmniCore.Tests;

public sealed class ObsoleteMonetaryBudgetRecoveryTests
{
    [Fact]
    public void Startup_exposes_failed_budget_expiration_without_mutating_the_journal()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-budget-failed-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var codecs = EventCodecs.Create(); var inner = new InMemoryEventStore();
            var session = SessionId.New(); var run = RunId.New();
            var stream = new EventStream(inner, codecs, session);
            stream.Append(new SessionCreated(session, WorkspaceId.Of(root).ToString(), root, ProfileId.New(), DateTimeOffset.UtcNow));
            var route = ModelRoute.DefaultForModel("probe", "provider", "https://provider.example/v1", ProviderFamily.OpenAiChatCompatible);
            stream.Append(new SessionRoutingPolicySet(session, new SessionRoutingPolicy(1,
                [AuthorizedModelRoute.From(route, BillingMode.Local)], [BillingMode.Local], false, 5m, "provider")));
            stream.Append(new RunCreated(run, session, "keep original input", RunMode.Act, ExecutionStrategy.Direct,
                FailurePolicy.BlockDependents, new TaskBudget(null, null, null, null), TaskId.New(), DateTimeOffset.UtcNow));
            stream.Append(new RunStarted(run));
            stream.Append(new InteractionRequested(InteractionId.New(), InteractionKind.BudgetExceeded,
                BudgetContinuation.Context("uso histórico incompleto: no se puede hacer cumplir el tope", null),
                "[{\"id\":\"deny\",\"intent\":\"deny\"}]", "deny", null, null, null, null, 0, 1));
            var original = inner.ReadFrom(session, 1).ToArray();
            var state = Path.Combine(root, "state.txt"); File.WriteAllText(state, session + "\n" + run);
            var server = new OmniServer(new FailedBudgetExpirationStore(inner), codecs, new InMemoryAuditSink(), state);
            Assert.Contains("fixture_expiration_write_failed", server.LastRecoveryProblem());
            Assert.Contains("blocked", server.Query("state", CancellationToken.None)!.Json);
            Assert.Equal(original.Select(evt => (evt.EventId, evt.Sequence, evt.PayloadJson)),
                inner.ReadFrom(session, 1).Select(evt => (evt.EventId, evt.Sequence, evt.PayloadJson)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class FailedBudgetExpirationStore(IEventStore inner) : IEventStore
    {
        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability, CancellationToken token)
        {
            if (evt.Type.ToString() == "interaction.expired") throw new IOException("fixture_expiration_write_failed");
            inner.Append(sessionId, evt, durability, token);
        }
        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events, DurabilityClass durability, CancellationToken token)
        {
            if (events.Any(evt => evt.Type.ToString() == "interaction.expired")) throw new IOException("fixture_expiration_write_failed");
            inner.AppendBatch(sessionId, events, durability, token);
        }
        public long CurrentSequence(SessionId sessionId) => inner.CurrentSequence(sessionId);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) => inner.ReadFrom(sessionId, fromSequenceInclusive);
    }

    [Theory]
    [InlineData(BillingMode.Local, true)]
    [InlineData(BillingMode.IncludedQuota, true)]
    [InlineData(BillingMode.MeteredCurrency, false)]
    [InlineData(BillingMode.Unknown, false)]
    [InlineData(BillingMode.CreditBalance, false)]
    public void Reopening_sqlite_retires_only_obsolete_non_monetary_prompts(BillingMode mode, bool retired)
        => Reopen(mode, "uso histórico incompleto: no se puede hacer cumplir el tope", retired);

    [Theory]
    [InlineData("uso de tokens histórico desconocido: missing usage")]
    [InlineData("límite de herramientas del Run alcanzado")]
    [InlineData("límite de turnos del Run alcanzado")]
    public void Non_monetary_recovery_keeps_non_monetary_limits(string detail)
        => Reopen(BillingMode.Local, detail, false);

    [Theory]
    [InlineData("0", false, true)]
    [InlineData("0", true, false)]
    [InlineData("1", false, false)]
    public void Reopening_retires_zero_tariff_prompt_only_for_the_exact_still_free_authorized_route(
        string price, bool changedEndpoint, bool retired)
        => Reopen(BillingMode.MeteredCurrency, "uso histórico incompleto: no se puede hacer cumplir el tope",
            retired, price, changedEndpoint);

    private static void Reopen(BillingMode mode, string detail, bool retired, string? price = null, bool changedEndpoint = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-obsolete-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "journal.db");
        var state = Path.Combine(root, "lastsession.txt");
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(config);
        var paths = new DefaultPlatformPaths(root, config);
        if (price is not null)
        {
            File.WriteAllText(Path.Combine(config, "providers.yaml"), $$"""
                providers:
                  provider:
                    family: OpenAiChatCompatible
                    baseUrl: https://{{(changedEndpoint ? "changed" : "provider")}}.example/v1
                    auth: none
                    billingMode: MeteredCurrency
                """);
            File.WriteAllText(Path.Combine(config, "models.yaml"), $$"""
                models:
                  probe:
                    provider: provider
                    context: 8192
                    maxOutput: 100
                    inputPricePerMillionUsd: {{price}}
                    outputPricePerMillionUsd: {{price}}
                """);
        }
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = RunId.New();
        var interaction = InteractionId.New();
        using var store = new SqliteEventStore(db);
        var stream = new EventStream(store, codecs, session);
        stream.Append(new SessionCreated(session, WorkspaceId.Of(root).ToString(), root,
            ProfileId.New(), DateTimeOffset.UtcNow));
        var route = ModelRoute.DefaultForModel("probe", "provider", "https://provider.example/v1",
            ProviderFamily.OpenAiChatCompatible);
        stream.Append(new SessionRoutingPolicySet(session, new SessionRoutingPolicy(1,
            [AuthorizedModelRoute.From(route, mode)], [mode], false, 5m, "provider")));
        stream.Append(new WorkspaceRootEstablished(session, root, DateTimeOffset.UtcNow,
            WorkspaceRootIdentity.Establish(root)));
        stream.Append(new RunCreated(run, session, "literal original input", RunMode.Act,
            ExecutionStrategy.Direct, FailurePolicy.BlockDependents,
            new TaskBudget(null, null, null, null), TaskId.New(), DateTimeOffset.UtcNow));
        stream.Append(new RunStarted(run));
        stream.Append(new InteractionRequested(interaction, InteractionKind.BudgetExceeded,
            BudgetContinuation.Context(detail, null), "[{\"id\":\"deny\",\"intent\":\"deny\"}]",
            "deny", null, null, null, null, 0, 1));
        var original = store.ReadFrom(session, 1).ToArray();
        File.WriteAllText(state, session + "\n" + run);
        store.Close();

        using var reopened = new SqliteEventStore(db);
        _ = new OmniServer(reopened, codecs, new InMemoryAuditSink(), state, new FileArtifactStore(root),
            platformPathsFactory: () => paths);
        var events = reopened.ReadFrom(session, 1).ToArray();
        Assert.Equal(original.Select(evt => (evt.EventId, evt.Sequence, evt.PayloadJson)),
            events.Take(original.Length).Select(evt => (evt.EventId, evt.Sequence, evt.PayloadJson)));
        var expired = events.Select(codecs.Decode).OfType<InteractionExpired>().ToArray();
        if (retired) Assert.Equal(interaction, Assert.Single(expired).InteractionId);
        else Assert.Empty(expired);
        Assert.DoesNotContain(events.Select(codecs.Decode), evt => evt is InteractionResolved or RunFailed or RunCancelled);
        Assert.False(RunProjection.Replay(session, run, codecs, events).IsTerminal());
        Assert.Equal(0, ObsoleteMonetaryBudgetRecovery.Reconcile(reopened, codecs, session));
    }
}
