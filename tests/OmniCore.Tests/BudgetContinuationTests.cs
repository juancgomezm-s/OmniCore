using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class BudgetContinuationTests
{
    private const string Options = "[{\"id\":\"deny\"},{\"id\":\"allow_plus\",\"value\":10}]";
    private static InteractionRequested Request(BudgetContinuationOffer offer) => new(
        InteractionId.New(), InteractionKind.BudgetExceeded, BudgetContinuation.Context("limit", offer),
        Options, "deny", null, null, null, null, 0, 1);

    [Theory]
    [InlineData("run")]
    [InlineData("session")]
    [InlineData("daily")]
    public void Only_user_resolution_increases_matching_limit_idempotently(string scope)
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = TestRun.OpenRun(store, session);
        var stream = new EventStream(store, codecs, session);
        var request = Request(new(scope, 5m, 5m, scope == "run" ? run.ToString() : null,
            scope == "daily" ? "2026-10-06" : null));
        stream.Append(request);
        decimal Limit() => BudgetContinuation.Limit(store.ReadFrom(session, 1), codecs, session, run,
            "2026-10-06", scope, 5m);
        Assert.Equal(5m, Limit());
        new RunControlService(store, codecs).Respond(session, request.InteractionId, "allow_plus");
        Assert.Equal(15m, Limit());
        Assert.Equal(15m, Limit());
        Assert.Throws<InteractionNotPendingException>(() => new RunControlService(store, codecs)
            .Respond(session, request.InteractionId, "allow_plus"));
        Assert.Equal(2m, BudgetContinuation.Limit(store.ReadFrom(session, 1), codecs, session, run,
            "2026-10-06", scope, 2m)); // A changed restrictive config invalidates the old grant.
        if (scope == "run") Assert.Equal(5m, BudgetContinuation.Limit(store.ReadFrom(session, 1), codecs,
            session, RunId.New(), "2026-10-06", scope, 5m));
        if (scope == "daily") Assert.Equal(5m, BudgetContinuation.Limit(store.ReadFrom(session, 1), codecs,
            session, run, "2026-10-07", scope, 5m));
    }

    [Theory]
    [InlineData("deny", InteractionCause.User)]
    [InlineData("allow_plus", InteractionCause.Timeout)]
    [InlineData("allow_plus", InteractionCause.NoClient)]
    public void Non_consent_resolutions_never_raise_the_limit(string choice, InteractionCause cause)
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = TestRun.OpenRun(store, session);
        var stream = new EventStream(store, codecs, session);
        var request = Request(new("session", 5m, 5m, null, null));
        stream.Append(request);
        stream.Append(new InteractionResolved(request.InteractionId, choice, cause));
        Assert.Equal(5m, BudgetContinuation.Limit(store.ReadFrom(session, 1), codecs,
            session, run, "2026-10-06", "session", 5m));
    }

    [Fact]
    public void Legacy_untyped_allow_plus_is_rejected_without_resolving_the_interaction()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        TestRun.OpenRun(store, session);
        var request = Request(new("session", 5m, 5m, null, null)) with { SubjectJson = "{\"detail\":\"legacy\"}" };
        new EventStream(store, codecs, session).Append(request);
        Assert.Throws<InvalidInteractionOptionException>(() => new RunControlService(store, codecs)
            .Respond(session, request.InteractionId, "allow_plus"));
        Assert.DoesNotContain(store.ReadFrom(session, 1).Select(codecs.Decode), e => e is InteractionResolved);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"budgetContinuation\":2}")]
    [InlineData("{\"budgetContinuation\":1,\"scope\":\"session\",\"baselineUsd\":5,\"currentUsd\":5,\"newLimitUsd\":1000,\"runId\":null,\"day\":null}")]
    public void Malformed_or_modified_offer_does_not_authorize_increase(string subject)
    {
        var request = Request(new("session", 5m, 5m, null, null)) with { SubjectJson = subject };
        Assert.Null(BudgetContinuation.Offer(request));
    }
}
