namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

public sealed class ModelEscalationConsentReplayTests
{
    private static readonly IEventCodecRegistry Codecs = EventCodecs.Create();
    private const string Options = "[{\"id\":\"deny\"},{\"id\":\"allow_route\"}]";

    private sealed record Fixture(InMemoryEventStore Store, SessionId Session, RunId Run,
        EventStream Stream, ModelRoute LocalRoute, SessionRoutingPolicy InitialPolicy);

    private static ModelRoute Route(string provider = "local", string endpoint = "http://127.0.0.1:8080/v1",
        string model = "local-model", ProviderFamily protocol = ProviderFamily.OpenAiChatCompatible) =>
        ModelRoute.DefaultForModel(model, provider, endpoint, protocol);

    private static Fixture OpenFixture()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var stream = new EventStream(store, Codecs, session);
        stream.Append(new SessionCreated(session, "workspace", "workspace", ProfileId.New(), DateTimeOffset.UtcNow));
        var run = TestRun.Open(stream, session);
        var local = Route();
        var policy = new SessionRoutingPolicy(1, [AuthorizedModelRoute.From(local, BillingMode.Local)],
            [BillingMode.Local], false, null, "local").Freeze();
        stream.Append(new SessionRoutingPolicySet(session, policy));
        return new Fixture(store, session, run.RunId, stream, local, policy);
    }

    private static long AppendEscalation(Fixture fixture, string target = "paid-model",
        EscalationCause cause = EscalationCause.ContextLimit)
    {
        fixture.Stream.Append(new ModelEscalationRequested(fixture.Run, "local-model", target, cause));
        return fixture.Store.CurrentSequence(fixture.Session);
    }

    private static InteractionId AppendOffer(Fixture fixture, ModelRoute route, BillingMode mode,
        int? revision = null)
    {
        var policy = SessionRoutingAuthorization.Read(fixture.Store.ReadFrom(fixture.Session, 1), Codecs,
            fixture.Session)!;
        var offer = new SessionRoutingAuthorization.Offer(revision ?? policy.Revision,
            AuthorizedModelRoute.From(route, mode));
        var interaction = InteractionId.New();
        using (ExecutionScope.Begin(new ExecutionScopeState(RunId: fixture.Run)))
            fixture.Stream.Append(new InteractionRequested(interaction, InteractionKind.ModelRouteConsent,
                SessionRoutingAuthorization.Context(offer), Options, "deny", null, null, null, null, 0, 1));
        return interaction;
    }

    private static void ResolveUserAllow(Fixture fixture, InteractionId interaction)
    {
        using (ExecutionScope.Begin(new ExecutionScopeState(RunId: fixture.Run)))
        {
            fixture.Stream.Append(new InteractionResolved(interaction, "allow_route", InteractionCause.User));
            var events = fixture.Store.ReadFrom(fixture.Session, 1);
            var request = events.Select(Codecs.Decode).OfType<InteractionRequested>()
                .Single(item => item.InteractionId == interaction);
            var revised = SessionRoutingAuthorization.Resolve(events, Codecs, fixture.Session, request);
            fixture.Stream.Append(new SessionRoutingPolicyRevised(fixture.Session, revised, interaction));
        }
    }

    private static GrantedPendingModelEscalation? Find(Fixture fixture, string target = "paid-model",
        ModelRoute? route = null, BillingMode mode = BillingMode.MeteredCurrency) =>
        ModelEscalationConsentReplay.FindGrantedPending(fixture.Store.ReadFrom(fixture.Session, 1), Codecs,
            fixture.Session, fixture.Run, target, route ?? Route("paid", "https://paid.example/v1", target), mode);

    [Fact]
    public void User_consent_replays_exact_pending_context_limit_escalation_read_only()
    {
        var fixture = OpenFixture();
        var requestSequence = AppendEscalation(fixture);
        var target = Route("paid", "https://paid.example/v1", "paid-model");
        var interaction = AppendOffer(fixture, target, BillingMode.MeteredCurrency);
        ResolveUserAllow(fixture, interaction);
        var eventCount = fixture.Store.ReadFrom(fixture.Session, 1).Count;

        var result = Find(fixture);
        var repeated = Find(fixture);

        Assert.NotNull(result);
        Assert.Equal("paid-model", result.Request.ToModel);
        Assert.Equal(EscalationCause.ContextLimit, result.Request.Cause);
        Assert.Equal(requestSequence, result.RequestSequence);
        Assert.Equal(interaction, result.InteractionId);
        Assert.Equal(result, repeated);
        Assert.Equal(eventCount, fixture.Store.ReadFrom(fixture.Session, 1).Count);
    }

    [Theory]
    [InlineData(InteractionCause.User, "deny", false, false)]
    [InlineData(InteractionCause.NoClient, "allow_route", false, false)]
    [InlineData(InteractionCause.User, "allow_route", true, false)]
    [InlineData(InteractionCause.User, "allow_route", false, true)]
    public void Denied_expired_or_unrevised_consent_does_not_replay(InteractionCause cause,
        string option, bool expire, bool omitRevision)
    {
        var fixture = OpenFixture();
        AppendEscalation(fixture);
        var target = Route("paid", "https://paid.example/v1", "paid-model");
        var interaction = AppendOffer(fixture, target, BillingMode.MeteredCurrency);
        using (ExecutionScope.Begin(new ExecutionScopeState(RunId: fixture.Run)))
        {
            fixture.Stream.Append(new InteractionResolved(interaction, option, cause));
            if (expire) fixture.Stream.Append(new InteractionExpired(interaction));
            if (!omitRevision && option == "allow_route" && cause == InteractionCause.User)
            {
                var events = fixture.Store.ReadFrom(fixture.Session, 1);
                var request = events.Select(Codecs.Decode).OfType<InteractionRequested>()
                    .Single(item => item.InteractionId == interaction);
                var policy = SessionRoutingAuthorization.Resolve(events, Codecs, fixture.Session, request);
                fixture.Stream.Append(new SessionRoutingPolicyRevised(fixture.Session, policy, interaction));
            }
        }

        Assert.Null(Find(fixture));
    }

    [Fact]
    public void Route_protocol_billing_or_endpoint_change_cannot_reuse_consent()
    {
        var fixture = OpenFixture();
        AppendEscalation(fixture);
        var target = Route("paid", "https://paid.example/v1", "paid-model");
        var interaction = AppendOffer(fixture, target, BillingMode.MeteredCurrency);
        ResolveUserAllow(fixture, interaction);

        Assert.Null(Find(fixture, route: Route("paid", "https://different.example/v1", "paid-model")));
        Assert.Null(Find(fixture, route: Route("paid", "https://paid.example/v1", "paid-model",
            ProviderFamily.AnthropicMessages)));
        Assert.Null(Find(fixture, mode: BillingMode.Unknown));
    }

    [Fact]
    public void Consent_is_scoped_to_exact_session_and_run()
    {
        var fixture = OpenFixture();
        AppendEscalation(fixture);
        var target = Route("paid", "https://paid.example/v1", "paid-model");
        var interaction = AppendOffer(fixture, target, BillingMode.MeteredCurrency);
        ResolveUserAllow(fixture, interaction);
        var events = fixture.Store.ReadFrom(fixture.Session, 1);

        Assert.Null(ModelEscalationConsentReplay.FindGrantedPending(events, Codecs, SessionId.New(),
            fixture.Run, "paid-model", target, BillingMode.MeteredCurrency));
        Assert.Null(ModelEscalationConsentReplay.FindGrantedPending(events, Codecs, fixture.Session,
            RunId.New(), "paid-model", target, BillingMode.MeteredCurrency));
    }

    [Fact]
    public void Newer_request_for_same_target_cannot_rescue_prior_grant()
    {
        var fixture = OpenFixture();
        AppendEscalation(fixture);
        var target = Route("paid", "https://paid.example/v1", "paid-model");
        var oldInteraction = AppendOffer(fixture, target, BillingMode.MeteredCurrency);
        ResolveUserAllow(fixture, oldInteraction);
        var latestRequestSequence = AppendEscalation(fixture);

        Assert.Null(Find(fixture));

        var newInteraction = AppendOffer(fixture, target, BillingMode.MeteredCurrency);
        ResolveUserAllow(fixture, newInteraction);
        var result = Find(fixture);

        Assert.NotNull(result);
        Assert.Equal(latestRequestSequence, result.RequestSequence);
        Assert.Equal(newInteraction, result.InteractionId);
        Assert.NotEqual(oldInteraction, result.InteractionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Approved_or_completed_attempt_is_not_pending(bool completed)
    {
        var fixture = OpenFixture();
        AppendEscalation(fixture);
        var target = Route("paid", "https://paid.example/v1", "paid-model");
        var interaction = AppendOffer(fixture, target, BillingMode.MeteredCurrency);
        ResolveUserAllow(fixture, interaction);
        using (ExecutionScope.Begin(new ExecutionScopeState(RunId: fixture.Run)))
        {
            fixture.Stream.Append(new ModelEscalationApproved(fixture.Run, "paid-model", "interaction:user"));
            if (completed) fixture.Stream.Append(new ModelEscalationCompleted(fixture.Run, "paid-model"));
        }

        Assert.Null(Find(fixture));
    }

    [Fact]
    public void Latest_escalation_request_governs_even_when_target_changes()
    {
        var fixture = OpenFixture();
        AppendEscalation(fixture, "paid-model");
        var target = Route("paid", "https://paid.example/v1", "paid-model");
        var interaction = AppendOffer(fixture, target, BillingMode.MeteredCurrency);
        ResolveUserAllow(fixture, interaction);
        AppendEscalation(fixture, "other-model");

        Assert.Null(Find(fixture, target: "paid-model"));
    }
}
