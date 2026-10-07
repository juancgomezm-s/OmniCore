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
        var controls = new RunControlService(store, codecs,
            utcNow: () => new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var request = Request(new(scope, 5m, 5m, scope == "run" ? run.ToString() : null,
            scope == "daily" ? "2026-10-06" : null));
        stream.Append(request);
        decimal Limit() => BudgetContinuation.Limit(store.ReadFrom(session, 1), codecs, session, run,
            "2026-10-06", scope, 5m);
        Assert.Equal(5m, Limit());
        controls.Respond(session, request.InteractionId, "allow_plus");
        Assert.Equal(15m, Limit());
        Assert.Equal(15m, Limit());
        Assert.Throws<InteractionNotPendingException>(() => controls
            .Respond(session, request.InteractionId, "allow_plus"));
        Assert.Equal(2m, BudgetContinuation.Limit(store.ReadFrom(session, 1), codecs, session, run,
            "2026-10-06", scope, 2m)); // A changed restrictive config invalidates the old grant.
        if (scope == "run") Assert.Equal(5m, BudgetContinuation.Limit(store.ReadFrom(session, 1), codecs,
            session, RunId.New(), "2026-10-06", scope, 5m));
        if (scope == "daily") Assert.Equal(5m, BudgetContinuation.Limit(store.ReadFrom(session, 1), codecs,
            session, run, "2026-10-07", scope, 5m));
    }

    [Fact]
    public void Daily_consent_expires_at_utc_midnight_even_when_local_day_has_not_changed()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = TestRun.OpenRun(store, session);
        var now = new DateTimeOffset(2026, 10, 6, 17, 59, 59, TimeSpan.FromHours(-6));
        var controls = new RunControlService(store, codecs, utcNow: () => now);
        var stream = new EventStream(store, codecs, session);
        var first = Request(new("daily", 5m, 5m, null, "2026-10-06"));
        stream.Append(first);
        controls.Respond(session, first.InteractionId, "allow_plus");
        Assert.Equal(15m, BudgetContinuation.Limit(store.ReadFrom(session, 1), codecs, session, run,
            "2026-10-06", "daily", 5m));
        var expired = Request(new("daily", 5m, 15m, null, "2026-10-06"));
        stream.Append(expired);
        var sequence = store.CurrentSequence(session);
        now = now.AddSeconds(1);
        Assert.Equal(6, now.Day); // Still Oct 6 locally, but Oct 7 UTC.
        Assert.Throws<InvalidInteractionOptionException>(() => controls.Respond(session, expired.InteractionId, "allow_plus"));
        Assert.Equal(sequence, store.CurrentSequence(session));
        Assert.Equal(5m, BudgetContinuation.Limit(store.ReadFrom(session, 1), codecs, session, run,
            "2026-10-07", "daily", 5m));
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
