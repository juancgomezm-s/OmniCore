using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
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
    public void Same_runtime_provider_type_and_build_produce_same_adapter_fingerprint()
    {
        var first = Create(provider: new ProviderAdapterOne());
        var second = Create(provider: new ProviderAdapterOne());

        Assert.Equal(first.Hash(), second.Hash());
        Assert.Equal(Part(first, "provider.adapter"), Part(second, "provider.adapter"));
        Assert.Equal("2", Part(first, "provider.adapter").Version);
    }

    [Fact]
    public void Different_provider_adapter_types_on_same_route_change_only_adapter_component()
    {
        var first = Create(provider: new ProviderAdapterOne());
        var second = Create(provider: new ProviderAdapterTwo());

        Assert.NotEqual(first.Hash(), second.Hash());
        Assert.NotEqual(Part(first, "provider.adapter"), Part(second, "provider.adapter"));
        Assert.Equal(Part(first, "model.descriptor"), Part(second, "model.descriptor"));
        Assert.Equal(Part(first, "context.policy"), Part(second, "context.policy"));
        Assert.Null(Part(first, "provider.adapter").Content);
        Assert.Null(Part(second, "provider.adapter").Content);
    }

    [Fact]
    public void Missing_provider_instance_is_explicitly_distinct_from_known_adapter()
    {
        var unknown = Create();
        var known = Create(provider: new ProviderAdapterOne());
        var unknownComponent = Part(unknown, "provider.adapter");
        var knownComponent = Part(known, "provider.adapter");

        Assert.NotEqual(unknown.Hash(), known.Hash());
        Assert.NotEqual(unknownComponent, knownComponent);
        Assert.Equal("2", unknownComponent.Version);
        Assert.Null(unknownComponent.Content);
        using var expectedMetadata = JsonDocument.Parse(ProviderAdapterCanonicalJson(
            new ModelRoute("fixture-provider", "http://fixture-one.invalid/v1",
                ProviderFamily.OpenAiChatCompatible, null, "fixture-model", new RouteId("fixture-route")), null));
        Assert.Equal(JsonValueKind.Null, expectedMetadata.RootElement.GetProperty("providerType").ValueKind);
        Assert.Equal(JsonValueKind.Null, expectedMetadata.RootElement.GetProperty("providerBuild").ValueKind);
        Assert.Equal(ContentHash.Sha256(Digest(ProviderAdapterCanonicalJson(
                new ModelRoute("fixture-provider", "http://fixture-one.invalid/v1",
                    ProviderFamily.OpenAiChatCompatible, null, "fixture-model", new RouteId("fixture-route")), null))),
            unknownComponent.Hash);
    }

    [Fact]
    public void Provider_adapter_component_hashes_route_and_actual_type_build_metadata()
    {
        var route = new ModelRoute("fixture-provider", "http://fixture-one.invalid/v1",
            ProviderFamily.OpenAiChatCompatible, null, "fixture-model", new RouteId("fixture-route"));
        var provider = new ProviderAdapterOne();
        var component = Part(Create(provider: provider), "provider.adapter");
        var expectedJson = ProviderAdapterCanonicalJson(route, provider);

        Assert.Equal("2", component.Version);
        Assert.Equal(ContentHash.Sha256(Digest(expectedJson)), component.Hash);
        using var metadata = JsonDocument.Parse(expectedJson);
        Assert.Equal(typeof(ProviderAdapterOne).FullName,
            metadata.RootElement.GetProperty("providerType").GetString());
        Assert.Equal(RuntimeBuildIdentity.ForAssembly(typeof(ProviderAdapterOne).Assembly),
            metadata.RootElement.GetProperty("providerBuild").GetString());
        Assert.DoesNotContain("fixture-one.invalid", component.Hash.Value, StringComparison.Ordinal);
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

    [Fact]
    public void Requested_output_limit_changes_model_component_without_inventing_an_enforced_legacy_bound()
    {
        var legacy = Create();
        var limited = Create(outputLimit: 512);
        var different = Create(outputLimit: 1024);
        Assert.NotEqual(Part(legacy, "model.descriptor"), Part(limited, "model.descriptor"));
        Assert.NotEqual(Part(limited, "model.descriptor"), Part(different, "model.descriptor"));
        Assert.Equal(Part(legacy, "context.policy"), Part(limited, "context.policy"));
        Assert.Equal(Part(limited, "provider.adapter"), Part(different, "provider.adapter"));
    }

    private static FingerprintComponent Part(ExecutionFingerprint value, string name) =>
        Assert.Single(value.Components, component => component.Name == name);

    private static string ProviderAdapterCanonicalJson(ModelRoute route, IModelProvider? provider)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("route");
            using (var routeDocument = JsonDocument.Parse(route.CanonicalJson()))
                routeDocument.RootElement.WriteTo(writer);
            if (provider is null)
            {
                writer.WriteNull("providerType");
                writer.WriteNull("providerBuild");
            }
            else
            {
                writer.WriteString("providerType", provider.GetType().FullName);
                writer.WriteString("providerBuild", RuntimeBuildIdentity.ForAssembly(provider.GetType().Assembly));
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static ExecutionFingerprint Create(string endpoint = "http://fixture-one.invalid/v1",
        IReadOnlyDictionary<string, double>? traits = null, long budget = 7000,
        string tokenizer = "fixture-tokenizer/1", long? outputLimit = null, IModelProvider? provider = null)
    {
        var model = new ModelDefinition("fixture-model", "fixture-provider", 8192, 7000, 1024);
        var route = new ModelRoute(model.ProviderId, endpoint, ProviderFamily.OpenAiChatCompatible,
            null, model.Id, new RouteId("fixture-route"));
        var selection = new ModelSelection(new ModelIdValue(model.Id), budget, ToolMode.Direct, null,
            route.Id, route, outputLimit);
        var profile = new EffectiveModelProfile(model.Id, 8192, 7000, 1024,
            new[] { "text" }, new[] { ToolCallFormat.Native }, false,
            traits ?? new Dictionary<string, double>(), route.Id);
        var harness = new HarnessPolicyResolver().Resolve(profile);
        return RuntimeFingerprintFactory.Create(model, profile, harness, selection,
            "fixture-harness-hash", "fixture-context-hash", "fixture-policy-hash", tokenizer, provider);
    }

    private sealed class ProviderAdapterOne : IModelProvider
    {
        public ProviderCapabilities Capabilities => new(false, false, false);

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Fingerprint tests never invoke providers.");
    }

    private sealed class ProviderAdapterTwo : IModelProvider
    {
        public ProviderCapabilities Capabilities => new(false, false, false);

        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Fingerprint tests never invoke providers.");
    }
}
