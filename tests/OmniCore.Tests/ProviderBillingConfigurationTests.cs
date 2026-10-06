using System.Reflection;
using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Models;
using OmniCore.Abstractions;

namespace OmniCore.Tests;

public sealed class ProviderBillingConfigurationTests
{
    [Theory]
    [InlineData(ProviderFamily.OpenAiChatCompatible)]
    [InlineData(ProviderFamily.AnthropicMessages)]
    [InlineData(ProviderFamily.OpenAIResponses)]
    public void Host_adapter_factory_preserves_explicit_billing_and_profile(ProviderFamily family)
    {
        var declared = new ProviderDescriptor("synthetic", family, "https://example.test/v1",
            AuthConfig.None(), true, true, true)
        { BillingMode = BillingMode.MeteredCurrency, Profile = "api" };
        // Construction only: no authenticated request, no network, no real consumption.
        var adapter = OmniHost.ConnectProvider(declared, declared.BaseUrl, "synthetic", "");
        var descriptor = (ProviderDescriptor)adapter.GetType()
            .GetField("_descriptor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(adapter)!;

        Assert.Equal(BillingMode.MeteredCurrency, descriptor.BillingMode);
        Assert.Equal(declared.Profile, descriptor.Profile);
    }

    private static ProviderDescriptor LoadProvider(string entry)
    {
        var loaded = new ConfigLoader().Load("providers:\n" + entry, null);
        return loaded.Registry.Provider(entry.Trim().Split(':')[0])!;
    }

    [Theory]
    [InlineData("Unknown", BillingMode.Unknown)]
    [InlineData("Local", BillingMode.Local)]
    [InlineData("IncludedQuota", BillingMode.IncludedQuota)]
    [InlineData("CreditBalance", BillingMode.CreditBalance)]
    [InlineData("MeteredCurrency", BillingMode.MeteredCurrency)]
    public void Explicit_billing_modes_load_exactly(string mode, BillingMode expected)
    {
        var provider = LoadProvider($"  paid: {{ baseUrl: https://api.example.test, billingMode: {mode} }}\n");

        Assert.Equal(expected, provider.BillingMode);
    }

    [Fact]
    public void Quoted_billing_mode_is_a_valid_yaml_string()
    {
        var provider = LoadProvider("  paid: { baseUrl: https://api.example.test, billingMode: \"MeteredCurrency\" }\n");

        Assert.Equal(BillingMode.MeteredCurrency, provider.BillingMode);
    }

    [Fact]
    public void Omitted_billing_mode_stays_unknown_without_inference()
    {
        var providers = new ConfigLoader().Load("""
providers:
  paid-key: { baseUrl: https://api.example.test, authRef: synthetic-key-reference }
  oauth-profile: { family: OpenAIResponses, baseUrl: https://api.example.test, profile: codex, auth: none }
  private-endpoint: { baseUrl: http://127.0.0.1:8080, auth: none }
""", null);

        Assert.Equal(BillingMode.Unknown, providers.Registry.Provider("paid-key")!.BillingMode);
        Assert.Equal(BillingMode.Unknown, providers.Registry.Provider("oauth-profile")!.BillingMode);
        Assert.Equal(BillingMode.Unknown, providers.Registry.Provider("private-endpoint")!.BillingMode);

        var builtIn = new ConfigLoader().Load(null, null);
        Assert.Equal(BillingMode.Local, builtIn.Registry.Provider("local")!.BillingMode);
    }

    [Theory]
    [InlineData("billingMode: 1", "config.wrongType")]
    [InlineData("billingMode: local", "config.outOfRange")]
    [InlineData("billingMode: Unknownish", "config.outOfRange")]
    public void Invalid_billing_mode_reports_provider_path_and_source_location(string property, string key)
    {
        var error = Assert.Throws<ConfigValidationException>(() => LoadProvider(
            "  paid: { baseUrl: https://api.example.test, " + property + " }\n"));

        var diagnostic = Assert.Single(error.Diagnostics);
        Assert.Equal("providers.paid.billingMode", diagnostic.KeyPath);
        Assert.Equal(key, diagnostic.Message.Key);
        Assert.True(diagnostic.Line > 0);
        Assert.True(diagnostic.Column > 0);
    }

    [Fact]
    public void Provider_schema_enum_matches_the_domain_billing_modes()
    {
        var assembly = typeof(ConfigLoader).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("providers.schema.json", StringComparison.Ordinal));
        using var schemaStream = assembly.GetManifestResourceStream(resourceName)!;
        using var schema = JsonDocument.Parse(schemaStream);
        var schemaModes = schema.RootElement.GetProperty("properties").GetProperty("providers")
            .GetProperty("additionalProperties").GetProperty("properties").GetProperty("billingMode")
            .GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToArray();

        Assert.Equal(Enum.GetNames<BillingMode>(), schemaModes);
    }
}
