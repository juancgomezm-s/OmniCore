using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Models;

namespace OmniCore.Tests;

public sealed class ReasoningCapabilityContractTests
{
    [Fact]
    public void Legacy_and_suggestive_model_names_do_not_declare_support()
    {
        var model = Model();
        var profile = new ModelProfileResolver().Resolve(model, null);
        Assert.Null(model.ReasoningCapability.Supported);
        Assert.Null(profile.ReasoningCapability.Supported);
        Assert.Null(profile.ReasoningCapability.EffortLevels);
        Assert.Null(profile.ReasoningCapability.ReplayPolicy);
    }

    [Fact]
    public void Declared_capability_is_immutable_and_round_trips_without_ranking_labels()
    {
        var levels = new[] { "adaptive", "vendor-specific" };
        var declared = new ReasoningCapability(true, levels, ReasoningReplayPolicy.RequiredWithTools);
        levels[0] = "mutated";
        Assert.Equal("adaptive", declared.EffortLevels![0]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)declared.EffortLevels)[0] = "mutated");
        var decoded = JsonSerializer.Deserialize<ReasoningCapability>(JsonSerializer.Serialize(declared))!;
        Assert.True(decoded.Supported);
        Assert.Equal(declared.EffortLevels, decoded.EffortLevels);
        Assert.Equal(declared.ReplayPolicy, decoded.ReplayPolicy);
        Assert.Null(JsonSerializer.Deserialize<ReasoningCapability>("{}")!.Supported);
    }

    [Fact]
    public void Unknown_unsupported_and_unreported_effort_are_distinct()
    {
        var unsupported = new ReasoningCapability(false);
        var supported = new ReasoningCapability(true);
        Assert.False(unsupported.Supported);
        Assert.True(supported.Supported);
        Assert.Null(supported.EffortLevels);
        Assert.Null(unsupported.ReplayPolicy);
        Assert.Empty(new ReasoningCapability(true, []).EffortLevels!);
    }

    [Fact]
    public void Contradictory_or_invalid_declarations_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new ReasoningCapability(false, ["high"]));
        Assert.Throws<ArgumentException>(() => new ReasoningCapability(null, ["high"]));
        Assert.Throws<ArgumentException>(() => new ReasoningCapability(true, ["high", "high"]));
        Assert.Throws<ArgumentException>(() => new ReasoningCapability(true, [" "]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReasoningCapability(true, null, (ReasoningReplayPolicy)999));
    }

    [Fact]
    public void Explicit_route_metadata_wins_and_does_not_leak_to_another_endpoint()
    {
        var model = Model(new ReasoningCapability(true, ["high"], ReasoningReplayPolicy.PreserveAcrossSteps));
        var provider = new ProviderDescriptor("provider", ProviderFamily.OpenAIResponses,
            "https://synthetic.invalid/v1", AuthConfig.None(), true, true, true);
        var defaultRoute = ModelRoutingHost.RouteFor(model, provider, provider.BaseUrl);
        var other = ModelRoutingHost.RouteFor(model, provider, "https://other.invalid/v1");
        var resolver = new ModelProfileResolver();
        Assert.True(resolver.Resolve(model, provider, route: defaultRoute).ReasoningCapability.Supported);
        Assert.Null(resolver.Resolve(model, provider, route: other).ReasoningCapability.Supported);
        var explicitOther = new ModelRoute(model.ProviderId, other.Endpoint, other.Protocol, null, model.Id,
            reasoningCapability: new ReasoningCapability(false));
        Assert.False(resolver.Resolve(model, provider, route: explicitOther).ReasoningCapability.Supported);
        var decoded = JsonSerializer.Deserialize<ModelRoute>(JsonSerializer.Serialize(defaultRoute))!;
        Assert.Equal(defaultRoute.Id, decoded.Id);
        Assert.True(decoded.ReasoningCapability.Supported);
        Assert.Equal(defaultRoute.ReasoningCapability.EffortLevels, decoded.ReasoningCapability.EffortLevels);
    }

    [Fact]
    public void Declaration_changes_fingerprint_but_not_physical_route_identity_or_permissions()
    {
        static ExecutionFingerprint Fingerprint(ReasoningCapability? capability)
        {
            var model = Model(capability);
            var route = ModelRoute.DefaultForModel(model.Id, model.ProviderId, "https://synthetic.invalid/v1",
                ProviderFamily.OpenAIResponses, reasoningCapability: capability);
            var profile = new ModelProfileResolver().Resolve(model, null, route: route);
            var selection = new ModelSelection(new ModelIdValue(model.Id), 1000, ToolMode.Direct, null, route.Id, route);
            return RuntimeFingerprintFactory.Create(model, profile, new HarnessPolicyResolver().Resolve(profile),
                selection, "harness", "context", "policy", "counter");
        }
        var unknown = Fingerprint(null);
        var first = Fingerprint(new ReasoningCapability(true, ["high"], ReasoningReplayPolicy.ProviderManaged));
        var second = Fingerprint(new ReasoningCapability(true, ["high"], ReasoningReplayPolicy.PreserveAcrossSteps));
        Assert.DoesNotContain(unknown.Components, c => c.Name == "model.reasoning.declared");
        Assert.NotEqual(unknown.Hash(), first.Hash());
        Assert.NotEqual(first.Hash(), second.Hash());
        Assert.Equal(Assert.Single(first.Components, c => c.Name == "provider.adapter"),
            Assert.Single(second.Components, c => c.Name == "provider.adapter"));
        Assert.Equal(Assert.Single(first.Components, c => c.Name == "model.harness"),
            Assert.Single(second.Components, c => c.Name == "model.harness"));
    }

    private static ModelDefinition Model(ReasoningCapability? capability = null) =>
        new("reasoning-ultracode-high", "provider", 8192, 7000, 2048, 1000, reasoningCapability: capability);
}
