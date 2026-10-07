using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class ReasoningCapabilityConfigurationTests
{
    private const string Providers = "providers:\n  synthetic: {baseUrl: https://synthetic.invalid/v1, billingMode: Local}\n";

    [Fact]
    public void Declared_yaml_reaches_route_profile_and_fingerprint_without_enabling_a_request()
    {
        var loaded = Load("reasoning: {supported: true, effortLevels: [high, adaptive], replayPolicy: RequiredWithTools}");
        var model = loaded.Registry.Model("synthetic-model")!;
        var provider = loaded.Registry.Provider(model.ProviderId);
        var route = ModelRoutingHost.RouteFor(model, provider, provider!.BaseUrl);
        var profile = new ModelProfileResolver().Resolve(model, provider, route: route);
        Assert.True(profile.ReasoningCapability.Supported);
        Assert.Equal(new[] { "high", "adaptive" }, profile.ReasoningCapability.EffortLevels);
        Assert.Equal(ReasoningReplayPolicy.RequiredWithTools, profile.ReasoningCapability.ReplayPolicy);
        var selection = new ModelSelection(new ModelIdValue(model.Id), 1000, ToolMode.Direct, null, route.Id, route);
        var fingerprint = RuntimeFingerprintFactory.Create(model, profile, new HarnessPolicyResolver().Resolve(profile),
            selection, "harness", "context", "policy", "counter");
        Assert.Single(fingerprint.Components, c => c.Name == "model.reasoning.declared");
        Assert.Null(selection.Reasoning);
        Assert.Null(new ModelProfileResolver().Resolve(model, provider,
            route: ModelRoutingHost.RouteFor(model, provider, "https://other.invalid/v1")).ReasoningCapability.Supported);
    }

    [Theory]
    [InlineData("")]
    [InlineData("reasoning: {}")]
    public void Legacy_or_empty_declaration_remains_unknown(string entry)
    {
        var capability = Load(entry).Registry.Model("synthetic-model")!.ReasoningCapability;
        Assert.Null(capability.Supported);
        Assert.Null(capability.EffortLevels);
        Assert.Null(capability.ReplayPolicy);
    }

    [Theory]
    [InlineData("None", ReasoningReplayPolicy.None)]
    [InlineData("ProviderManaged", ReasoningReplayPolicy.ProviderManaged)]
    [InlineData("RequiredWithTools", ReasoningReplayPolicy.RequiredWithTools)]
    [InlineData("PreserveAcrossSteps", ReasoningReplayPolicy.PreserveAcrossSteps)]
    public void Replay_policy_is_exact_not_inferred(string text, ReasoningReplayPolicy expected)
    {
        Assert.Equal(expected, Load("reasoning: {replayPolicy: " + text + "}")
            .Registry.Model("synthetic-model")!.ReasoningCapability.ReplayPolicy);
    }

    [Theory]
    [InlineData("reasoning: true", "reasoning", "config.wrongType")]
    [InlineData("reasoning: {surprise: true}", "reasoning.surprise", "config.unknownKey")]
    [InlineData("reasoning: {supported: 'true'}", "reasoning.supported", "config.wrongType")]
    [InlineData("reasoning: {supported: 1}", "reasoning.supported", "config.wrongType")]
    [InlineData("reasoning: {replayPolicy: 1}", "reasoning.replayPolicy", "config.wrongType")]
    [InlineData("reasoning: {replayPolicy: Other}", "reasoning.replayPolicy", "config.outOfRange")]
    [InlineData("reasoning: {effortLevels: [high]}", "reasoning.effortLevels", "config.outOfRange")]
    [InlineData("reasoning: {supported: false, effortLevels: [high]}", "reasoning.effortLevels", "config.outOfRange")]
    [InlineData("reasoning: {supported: true, effortLevels: high}", "reasoning.effortLevels", "config.wrongType")]
    [InlineData("reasoning: {supported: true, effortLevels: [high, high]}", "reasoning.effortLevels", "config.outOfRange")]
    [InlineData("reasoning: {supported: true, effortLevels: [' ']}", "reasoning.effortLevels", "config.outOfRange")]
    public void Invalid_declarations_report_exact_path_and_location(string entry, string path, string key)
    {
        var error = Assert.Throws<ConfigValidationException>(() => Load(entry));
        Assert.Contains(error.Diagnostics, d => d.KeyPath == "models.synthetic-model." + path
            && d.Message.Key == key && d.Line > 0 && d.Column > 0);
    }

    [Fact]
    public void Embedded_schema_exposes_replay_enum_and_unknown_by_omission()
    {
        var assembly = typeof(ConfigLoader).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("models.schema.json", StringComparison.Ordinal)))!;
        using var schema = JsonDocument.Parse(stream);
        var reasoning = schema.RootElement.GetProperty("properties").GetProperty("models")
            .GetProperty("additionalProperties").GetProperty("properties").GetProperty("reasoning");
        Assert.False(reasoning.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(Enum.GetNames<ReasoningReplayPolicy>(), reasoning.GetProperty("properties")
            .GetProperty("replayPolicy").GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
        Assert.False(reasoning.TryGetProperty("required", out _));
    }

    private static LoadedUserConfiguration Load(string entry) => new ConfigLoader().Load(Providers,
        "models:\n  synthetic-model:\n    provider: synthetic\n    " + entry + "\n");
}
