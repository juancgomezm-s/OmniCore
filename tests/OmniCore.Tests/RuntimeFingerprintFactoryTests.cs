using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Models;

namespace OmniCore.Tests;

public sealed class RuntimeFingerprintFactoryTests
{
    [Fact]
    public void Trait_insertion_order_does_not_change_the_resolved_profile_fingerprint()
    {
        var first = Create(traits: new Dictionary<string, double> { ["B"] = .8, ["A"] = .4 });
        var second = Create(traits: new Dictionary<string, double> { ["A"] = .4, ["B"] = .8 });
        Assert.Equal(first.Hash(), second.Hash());
        Assert.Equal(first.Components, second.Components);
    }

    [Fact]
    public void Effective_trait_change_is_recorded_even_when_harness_selection_is_unchanged()
    {
        var first = Create(traits: new Dictionary<string, double> { ["fixture-trait"] = .4 });
        var second = Create(traits: new Dictionary<string, double> { ["fixture-trait"] = .5 });
        Assert.NotEqual(first.Hash(), second.Hash());
        Assert.NotEqual(Part(first, "model.profile"), Part(second, "model.profile"));
        Assert.Equal(Part(first, "model.harness"), Part(second, "model.harness"));
    }

    [Fact]
    public void Physical_endpoint_change_with_same_route_id_changes_adapter_digest()
    {
        var first = Create(endpoint: "http://fixture-one.invalid/v1");
        var second = Create(endpoint: "http://fixture-two.invalid/v1");
        Assert.NotEqual(first.Hash(), second.Hash());
        Assert.NotEqual(Part(first, "provider.adapter"), Part(second, "provider.adapter"));
        Assert.Equal(Part(first, "model.descriptor"), Part(second, "model.descriptor"));
        Assert.All(second.Components, part => Assert.Null(part.Content));
    }

    [Fact]
    public void Context_budget_and_tokenizer_change_are_distinct_from_cumulative_usage()
    {
        var first = Create();
        var budget = Create(budget: 4096);
        var tokenizer = Create(tokenizer: "fixture-tokenizer/2");
        Assert.NotEqual(Part(first, "context.policy"), Part(budget, "context.policy"));
        Assert.NotEqual(Part(first, "context.policy"), Part(tokenizer, "context.policy"));
        Assert.Equal(Part(first, "runtime.build"), Part(budget, "runtime.build"));
    }

    private static FingerprintComponent Part(ExecutionFingerprint value, string name) =>
        Assert.Single(value.Components, component => component.Name == name);

    private static ExecutionFingerprint Create(string endpoint = "http://fixture-one.invalid/v1",
        IReadOnlyDictionary<string, double>? traits = null, long budget = 7000,
        string tokenizer = "fixture-tokenizer/1")
    {
        var model = new ModelDefinition("fixture-model", "fixture-provider", 8192, 7000, 1024);
        var route = new ModelRoute(model.ProviderId, endpoint, ProviderFamily.OpenAiChatCompatible,
            null, model.Id, new RouteId("fixture-route"));
        var selection = new ModelSelection(new ModelIdValue(model.Id), budget, ToolMode.Direct, null,
            route.Id, route);
        var profile = new EffectiveModelProfile(model.Id, 8192, 7000, 1024,
            new[] { "text" }, new[] { ToolCallFormat.Native }, false,
            traits ?? new Dictionary<string, double>(), route.Id);
        var harness = new HarnessPolicyResolver().Resolve(profile);
        return RuntimeFingerprintFactory.Create(model, profile, harness, selection,
            "fixture-harness-hash", "fixture-context-hash", "fixture-policy-hash", tokenizer);
    }
}
