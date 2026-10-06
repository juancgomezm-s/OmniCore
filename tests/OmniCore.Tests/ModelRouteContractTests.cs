using OmniCore.Domain;
using OmniCore.Models;
using System.Text.Json;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class ModelRouteContractTests
{
    [Fact]
    public void Route_identity_is_stable_and_canonical_json_round_trips()
    {
        var route = Route();
        var again = Route();

        Assert.Equal(route.Id, again.Id);
        Assert.Equal(route, again);
        var json = route.CanonicalJson();
        using var document = JsonDocument.Parse(json);
        Assert.Equal("provider-a", document.RootElement.GetProperty("providerId").GetString());
        Assert.Equal("https://api.example.test/v1", document.RootElement.GetProperty("endpoint").GetString());
        Assert.Equal("OpenAIResponses", document.RootElement.GetProperty("protocol").GetString());
        Assert.Equal("codex", document.RootElement.GetProperty("profile").GetString());
        Assert.Equal("model-x", document.RootElement.GetProperty("providerModelName").GetString());

        var decoded = document.RootElement;
        var reconstructed = new ModelRoute(decoded.GetProperty("providerId").GetString()!,
            decoded.GetProperty("endpoint").GetString()!,
            Enum.Parse<ProviderFamily>(decoded.GetProperty("protocol").GetString()!),
            decoded.GetProperty("profile").GetString(),
            decoded.GetProperty("providerModelName").GetString()!);
        Assert.Equal(route.Id, reconstructed.Id);

        var serialized = JsonSerializer.Serialize(route);
        var deserialized = JsonSerializer.Deserialize<ModelRoute>(serialized);
        Assert.NotNull(deserialized);
        Assert.Equal(route, deserialized);
        Assert.Equal(route.Id, deserialized.Id);
    }

    [Fact]
    public void Legacy_model_definition_maps_to_same_default_model_id()
    {
        var definition = new ModelDefinition("legacy-model", "provider-a", 8192, 7000, 2048);
        var route = ModelRoute.DefaultForModel(definition.Id, definition.ProviderId,
            "https://api.example.test/v1", ProviderFamily.OpenAIResponses, "codex");

        Assert.Equal(definition.Id, route.Id.Value);
        Assert.Equal(definition.ProviderId, route.ProviderId);
        Assert.Equal(definition.Id, route.ProviderModelName);

        var persisted = JsonSerializer.Deserialize<ModelRoute>(JsonSerializer.Serialize(route));
        Assert.NotNull(persisted);
        Assert.Equal(definition.Id, persisted.Id.Value);
    }

    [Theory]
    [InlineData("provider-b", "https://api.example.test/v1", "model-x")]
    [InlineData("provider-a", "https://other.example.test/v1", "model-x")]
    [InlineData("provider-a", "https://api.example.test/v1", "model-y")]
    public void Changes_to_provider_endpoint_or_model_produce_distinct_route(string provider, string endpoint, string model)
    {
        Assert.NotEqual(Route().Id, Route(provider, endpoint, model).Id);
    }

    [Fact]
    public void Required_identity_fields_are_validated()
    {
        Assert.Throws<ArgumentException>(() => new RouteId(" "));
        Assert.Throws<ArgumentException>(() => new ModelRoute(" ", "endpoint", ProviderFamily.OpenAIResponses, null, "model"));
        Assert.Throws<ArgumentException>(() => new ModelRoute("provider", " ", ProviderFamily.OpenAIResponses, null, "model"));
        Assert.Throws<ArgumentException>(() => new ModelRoute("provider", "endpoint", ProviderFamily.OpenAIResponses, null, " "));
    }

    [Fact]
    public void Protocol_and_profile_are_part_of_route_identity()
    {
        var baseline = Route();
        Assert.NotEqual(baseline.Id, new ModelRoute(baseline.ProviderId, baseline.Endpoint,
            ProviderFamily.AnthropicMessages, baseline.Profile, baseline.ProviderModelName).Id);
        Assert.NotEqual(baseline.Id, new ModelRoute(baseline.ProviderId, baseline.Endpoint,
            baseline.Protocol, "api", baseline.ProviderModelName).Id);
    }

    [Fact]
    public void Selection_legacy_default_and_explicit_route_survive_typed_journal_codec()
    {
        var legacy = new ModelSelection(new ModelIdValue("model-x"), 1000, ToolMode.Direct, null);
        Assert.Equal("model-x", legacy.RouteId.Value);
        var selection = new ModelSelection(legacy.Model, 1000, ToolMode.Direct, null, Route().Id);
        var payload = new ModelStepStarted(TurnId.New(), 0, selection.Model.ToString(), selection.ContextBudget,
            selection.ToolMode.ToString(), null, null, null, null, selection.RouteId);
        var codecs = EventCodecs.Create();
        var json = codecs.CodecFor(payload.Type()).Encode(payload);
        var envelope = DomainEvent.Create(SessionId.New(), payload.Type(), payload.SchemaVersion(),
            null, null, null, null, null, null, null, null, Array.Empty<ArtifactRef>(), json);
        var decoded = Assert.IsType<ModelStepStarted>(codecs.Decode(envelope));
        Assert.Equal(selection.RouteId, decoded.RouteId);
    }

    private static ModelRoute Route(string provider = "provider-a", string endpoint = "https://api.example.test/v1",
        string model = "model-x") =>
        new(provider, endpoint, ProviderFamily.OpenAIResponses, "codex", model);
}
