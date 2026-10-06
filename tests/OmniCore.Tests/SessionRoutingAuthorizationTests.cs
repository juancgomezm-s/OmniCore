namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

public sealed class SessionRoutingAuthorizationTests
{
    private static readonly IEventCodecRegistry Codecs = EventCodecs.Create();
    private const string Options = "[{\"id\":\"allow_route\"},{\"id\":\"deny\"}]";

    private sealed record Fixture(InMemoryEventStore Store, SessionId Session, EventStream Stream,
        TestRun.Opened Run, SessionRoutingPolicy InitialPolicy);

    private static ModelRoute Route(string provider, string endpoint, string model) =>
        ModelRoute.DefaultForModel(model, provider, endpoint, ProviderFamily.OpenAiChatCompatible);

    private static Fixture OpenFixture(SessionRoutingPolicy? policy = null)
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var stream = new EventStream(store, Codecs, session);
        stream.Append(new SessionCreated(session, "workspace", "workspace", ProfileId.New(), DateTimeOffset.UtcNow));
        var run = TestRun.Open(stream, session);
        var initial = policy ?? LocalPolicy();
        stream.Append(new SessionRoutingPolicySet(session, initial));
        return new Fixture(store, session, stream, run, initial);
    }

    private static SessionRoutingPolicy LocalPolicy()
    {
        var local = Route("local", "http://127.0.0.1:8080/v1", "local-model");
        return new SessionRoutingPolicy(1, [AuthorizedModelRoute.From(local, BillingMode.Local)],
            [BillingMode.Local], false, 5m, "local").Freeze();
    }

    private static InteractionId Offer(Fixture fixture, ModelRoute route, BillingMode mode, int? revision = null,
        string? subject = null)
    {
        var id = InteractionId.New();
        var offer = new SessionRoutingAuthorization.Offer(revision ?? fixture.InitialPolicy.Revision,
            AuthorizedModelRoute.From(route, mode));
        fixture.Stream.Append(new InteractionRequested(id, InteractionKind.ModelRouteConsent,
            subject ?? SessionRoutingAuthorization.Context(offer), Options, "deny", null,
            fixture.Run.RootLane, null, null, 0, 1));
        return id;
    }

    private static IReadOnlyList<DomainEvent> Events(Fixture fixture) => fixture.Store.ReadFrom(fixture.Session, 1);

    private static SessionRoutingPolicy ReadPolicy(Fixture fixture) =>
        SessionRoutingAuthorization.Read(Events(fixture), Codecs, fixture.Session)!;

    [Theory]
    [InlineData(BillingMode.MeteredCurrency)]
    [InlineData(BillingMode.Unknown)]
    public void User_allow_grants_exact_metered_or_unknown_route_once(BillingMode mode)
    {
        var fixture = OpenFixture();
        var requestedRoute = Route("paid", "https://paid.example/v1", "paid-model");
        var changedEndpoint = Route("paid", "https://other-paid.example/v1", "paid-model");
        var interaction = Offer(fixture, requestedRoute, mode);
        var control = new RunControlService(fixture.Store, Codecs);

        Assert.False(ReadPolicy(fixture).Allows(requestedRoute, mode));
        var requestEvent = Assert.Single(Events(fixture), evt => Codecs.Decode(evt) is InteractionRequested item
            && item.InteractionId == interaction);
        var request = (InteractionRequested)Codecs.Decode(requestEvent);
        var resolvedOffer = SessionRoutingAuthorization.Resolve(Events(fixture), Codecs, fixture.Session, request);
        Assert.True(resolvedOffer.Allows(requestedRoute, mode));
        control.Respond(fixture.Session, interaction, "allow_route");

        var policy = ReadPolicy(fixture);
        Assert.Equal(2, policy.Revision);
        Assert.True(policy.CrossProviderRouting);
        Assert.True(policy.Allows(requestedRoute, mode));
        Assert.False(policy.Allows(changedEndpoint, mode));
        Assert.False(policy.Allows(Route("unrelated", "https://unrelated.example/v1", "other"), mode));
        Assert.Single(Events(fixture), evt => Codecs.Decode(evt) is SessionRoutingPolicyRevised revised
            && revised.InteractionId == interaction && revised.Policy.Revision == 2);
        Assert.Single(Events(fixture), evt => Codecs.Decode(evt) is InteractionResolved resolved
            && resolved.InteractionId == interaction && resolved.Cause == InteractionCause.User
            && resolved.OptionId == "allow_route");

        Assert.Throws<InteractionNotPendingException>(() => control.Respond(fixture.Session, interaction, "allow_route"));
        Assert.Single(Events(fixture), evt => Codecs.Decode(evt) is SessionRoutingPolicyRevised revised
            && revised.InteractionId == interaction);
        Assert.Equal(2, ReadPolicy(fixture).Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deny_or_no_client_resolves_without_expanding_policy(bool noClient)
    {
        var fixture = OpenFixture();
        var requestedRoute = Route("paid", "https://paid.example/v1", "paid-model");
        var interaction = Offer(fixture, requestedRoute, BillingMode.MeteredCurrency);
        var control = new RunControlService(fixture.Store, Codecs);

        if (noClient) control.ResolveModelRouteWithoutClient(fixture.Session, interaction);
        else control.Respond(fixture.Session, interaction, "deny");

        Assert.Equal(fixture.InitialPolicy.Revision, ReadPolicy(fixture).Revision);
        Assert.False(ReadPolicy(fixture).Allows(requestedRoute, BillingMode.MeteredCurrency));
        Assert.DoesNotContain(Events(fixture), evt => Codecs.Decode(evt) is SessionRoutingPolicyRevised);
        var resolution = Assert.Single(Events(fixture), evt => Codecs.Decode(evt) is InteractionResolved item
            && item.InteractionId == interaction);
        var decoded = (InteractionResolved)Codecs.Decode(resolution);
        Assert.Equal("deny", decoded.OptionId);
        Assert.Equal(noClient ? InteractionCause.NoClient : InteractionCause.User, decoded.Cause);
    }

    [Fact]
    public void Stale_revision_offer_is_rejected_without_resolving_it()
    {
        var fixture = OpenFixture();
        var firstRoute = Route("paid", "https://paid.example/v1", "paid-model");
        var first = Offer(fixture, firstRoute, BillingMode.MeteredCurrency);
        var control = new RunControlService(fixture.Store, Codecs);
        control.Respond(fixture.Session, first, "allow_route");

        var staleRoute = Route("other", "https://other.example/v1", "other-model");
        var stale = Offer(fixture, staleRoute, BillingMode.Unknown, revision: 1);
        var before = Events(fixture).Count;

        Assert.Throws<InvalidInteractionOptionException>(() => control.Respond(fixture.Session, stale, "allow_route"));

        Assert.Equal(before, Events(fixture).Count);
        Assert.Equal(2, ReadPolicy(fixture).Revision);
        Assert.False(ReadPolicy(fixture).Allows(staleRoute, BillingMode.Unknown));
        Assert.DoesNotContain(Events(fixture), evt => Codecs.Decode(evt) is InteractionResolved resolved
            && resolved.InteractionId == stale);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replay_rejects_revision_without_matching_user_consent(bool extraRoute)
    {
        var fixture = OpenFixture();
        var target = Route("paid", "https://paid.example/v1", "paid-model");
        var interaction = Offer(fixture, target, BillingMode.MeteredCurrency);
        var request = Assert.Single(Events(fixture), evt => Codecs.Decode(evt) is InteractionRequested item
            && item.InteractionId == interaction);
        var requestPayload = (InteractionRequested)Codecs.Decode(request);
        var offer = SessionRoutingAuthorization.Parse(requestPayload)!;
        var expected = fixture.InitialPolicy.Grant(offer.Route);
        var revised = extraRoute
            ? expected.Grant(AuthorizedModelRoute.From(Route("third", "https://third.example/v1", "third-model"), BillingMode.Local))
            : expected;
        var cause = extraRoute ? InteractionCause.User : InteractionCause.NoClient;
        fixture.Stream.Append(new InteractionResolved(interaction, "allow_route", cause));
        fixture.Stream.Append(new SessionRoutingPolicyRevised(fixture.Session, revised, interaction));

        Assert.Throws<InvalidDataException>(() => ReadPolicy(fixture));
    }

    [Fact]
    public void Session_policy_is_not_inherited_by_another_session()
    {
        var fixture = OpenFixture();
        var otherSession = SessionId.New();
        var otherStream = new EventStream(fixture.Store, Codecs, otherSession);
        otherStream.Append(new SessionCreated(otherSession, "workspace", "workspace", ProfileId.New(), DateTimeOffset.UtcNow));

        Assert.NotNull(ReadPolicy(fixture));
        Assert.Null(SessionRoutingAuthorization.Read(fixture.Store.ReadFrom(otherSession, 1), Codecs, otherSession));
    }

    [Fact]
    public void Malformed_consent_subject_fails_closed_to_null_offer()
    {
        var request = new InteractionRequested(InteractionId.New(), InteractionKind.ModelRouteConsent,
            "{not-json", Options, "deny", null, null, null, null, 0, 1);
        Assert.Null(SessionRoutingAuthorization.Parse(request));

        request = request with { SubjectJson = "{\"routingConsent\":1,\"policyRevision\":1,\"routeId\":\"r\",\"providerId\":\"p\",\"identityHash\":\"BAD\",\"billingMode\":\"Unknown\"}" };
        Assert.Null(SessionRoutingAuthorization.Parse(request));
    }
}
