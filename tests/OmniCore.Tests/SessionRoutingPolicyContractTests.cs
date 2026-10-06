namespace OmniCore.Tests;

using OmniCore.Domain;
using OmniCore.Infrastructure;

public sealed class SessionRoutingPolicyContractTests
{
    private static ModelRoute Route(string provider = "local", string endpoint = "http://127.0.0.1:8080/v1",
        string model = "model-a", ProviderFamily protocol = ProviderFamily.OpenAiChatCompatible) =>
        ModelRoute.DefaultForModel(model, provider, endpoint, protocol);

    [Fact]
    public void Empty_policy_denies_unknown_and_every_route_until_explicit_grant()
    {
        var policy = SessionRoutingPolicy.Empty();
        var route = Route();

        Assert.Equal(1, policy.Revision);
        Assert.Empty(policy.AllowedRoutes);
        Assert.Empty(policy.BillingPolicy);
        Assert.False(policy.Allows(route, BillingMode.Local));
        Assert.False(policy.Allows(route, BillingMode.Unknown));

        var granted = policy.Grant(AuthorizedModelRoute.From(route, BillingMode.Unknown));
        Assert.True(granted.Allows(route, BillingMode.Unknown));
        Assert.False(policy.Allows(route, BillingMode.Unknown));
        Assert.Equal(2, granted.Revision);
    }

    [Fact]
    public void Authorization_binds_legacy_route_id_to_physical_identity_and_billing_mode()
    {
        var original = Route(endpoint: "https://one.example/v1");
        var changedEndpoint = Route(endpoint: "https://two.example/v1");
        Assert.Equal(original.Id, changedEndpoint.Id); // Legacy 1:1 route identity is retained.

        var binding = AuthorizedModelRoute.From(original, BillingMode.MeteredCurrency);
        var policy = new SessionRoutingPolicy(1, [binding], [BillingMode.MeteredCurrency], false, null, "local").Freeze();

        Assert.True(policy.Allows(original, BillingMode.MeteredCurrency));
        Assert.False(policy.Allows(changedEndpoint, BillingMode.MeteredCurrency));
        Assert.False(policy.Allows(original, BillingMode.Unknown));
    }

    [Fact]
    public void Cross_provider_route_requires_a_grant_and_grant_adds_only_that_binding()
    {
        var local = Route();
        var paid = Route("paid", "https://paid.example/v1", "model-b");
        var unrelated = Route("other", "https://other.example/v1", "model-c");
        var policy = new SessionRoutingPolicy(1, [AuthorizedModelRoute.From(local, BillingMode.Local)],
            [BillingMode.Local], false, 12m, "local").Freeze();

        Assert.False(policy.Allows(paid, BillingMode.MeteredCurrency));
        var granted = policy.Grant(AuthorizedModelRoute.From(paid, BillingMode.MeteredCurrency));

        Assert.True(granted.CrossProviderRouting);
        Assert.True(granted.Allows(local, BillingMode.Local));
        Assert.True(granted.Allows(paid, BillingMode.MeteredCurrency));
        Assert.False(granted.Allows(unrelated, BillingMode.MeteredCurrency));
        Assert.Equal(12m, granted.SessionSpendLimit);
        Assert.Single(policy.AllowedRoutes);
        Assert.Equal(2, granted.AllowedRoutes.Count);
    }

    [Fact]
    public void Freeze_copies_inputs_and_rejects_invalid_or_duplicate_contracts()
    {
        var route = Route();
        var routes = new List<AuthorizedModelRoute> { AuthorizedModelRoute.From(route, BillingMode.Local) };
        var modes = new List<BillingMode> { BillingMode.Local };
        var policy = new SessionRoutingPolicy(1, routes, modes, false, null, "local").Freeze();
        routes.Clear();
        modes.Clear();

        Assert.Single(policy.AllowedRoutes);
        Assert.Equal(BillingMode.Local, Assert.Single(policy.BillingPolicy));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionRoutingPolicy(0, [], [], false, null, "").Freeze());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionRoutingPolicy(1, [], [], false, -1m, "").Freeze());
        Assert.Throws<ArgumentException>(() => new SessionRoutingPolicy(1,
            [AuthorizedModelRoute.From(route, BillingMode.Local), AuthorizedModelRoute.From(route, BillingMode.Local)],
            [BillingMode.Local], false, null, "local").Freeze());
    }

    [Fact]
    public void New_policy_events_roundtrip_through_generated_event_codecs()
    {
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var interaction = InteractionId.New();
        var policy = SessionRoutingPolicy.Empty().Grant(
            AuthorizedModelRoute.From(Route(), BillingMode.Local));
        DomainEventPayload[] payloads =
        [
            new SessionRoutingPolicySet(session, policy),
            new SessionRoutingPolicyRevised(session, policy, interaction),
        ];

        foreach (var payload in payloads)
        {
            var codec = codecs.CodecFor(payload.Type());
            var json = codec.Encode(payload);
            var decoded = codec.Decode(payload.Type(), json);
            Assert.Equal(payload.Type(), decoded.Type());
            switch (payload, decoded)
            {
                case (SessionRoutingPolicySet expected, SessionRoutingPolicySet actual):
                    Assert.Equal(expected.SessionId, actual.SessionId);
                    AssertPolicyEqual(expected.Policy, actual.Policy);
                    break;
                case (SessionRoutingPolicyRevised expected, SessionRoutingPolicyRevised actual):
                    Assert.Equal(expected.SessionId, actual.SessionId);
                    Assert.Equal(expected.InteractionId, actual.InteractionId);
                    AssertPolicyEqual(expected.Policy, actual.Policy);
                    break;
                default:
                    Assert.Fail("Expected policy event roundtrip type.");
                    break;
            }
            Assert.Equal(1, codecs.CurrentVersion(payload.Type()));
        }
    }

    private static void AssertPolicyEqual(SessionRoutingPolicy expected, SessionRoutingPolicy actual)
    {
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.CrossProviderRouting, actual.CrossProviderRouting);
        Assert.Equal(expected.SessionSpendLimit, actual.SessionSpendLimit);
        Assert.Equal(expected.OriginProviderId, actual.OriginProviderId);
        Assert.Equal(expected.BillingPolicy, actual.BillingPolicy);
        Assert.Equal(expected.AllowedRoutes, actual.AllowedRoutes);
    }
}
