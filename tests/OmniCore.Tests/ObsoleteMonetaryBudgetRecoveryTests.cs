using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class ObsoleteMonetaryBudgetRecoveryTests
{
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

    private static void Reopen(BillingMode mode, string detail, bool retired)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-obsolete-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "journal.db");
        var state = Path.Combine(root, "lastsession.txt");
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
        _ = new OmniServer(reopened, codecs, new InMemoryAuditSink(), state, new FileArtifactStore(root));
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
