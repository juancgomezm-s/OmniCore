using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using YamlDotNet.RepresentationModel;

namespace OmniCore.Tests;

public sealed class ClaudeAccountConfigurationTests : IDisposable
{
    [Fact]
    public void Agent_system_message_round_trips_exactly_and_empty_restores_the_default_without_changing_OAuth()
    {
        const string message = "Use a concise teaching role.\nRespond in Spanish; preserve accents: explicación.\nKeep {version} literal here.";
        var setup = _host.ReadConfiguration(null, _token);
        _host.SaveConfiguration(setup.Revision, null, setup.Draft with { AgentSystemPrompt = message }, _token);
        var saved = _host.ReadConfiguration("anthropic", _token);
        Assert.Equal(message, saved.Draft.AgentSystemPrompt);
        Assert.Equal(setup.Draft.ClientId, saved.Draft.ClientId);
        Assert.Equal(setup.Draft.UserAgent, saved.Draft.UserAgent);
        Assert.Equal(setup.Draft.Scopes, saved.Draft.Scopes);
        var loaded = OmniHost.LoadUserConfiguration(_paths);
        Assert.Equal(message, loaded.Providers!.Providers!["anthropic"].AgentSystemPrompt);
        var identity = OmniHost.CreateClaudeOAuthSession(_paths, "anthropic")!.Identity;
        Assert.Equal(setup.Draft.UserAgent, identity.UserAgentTemplate);
        _host.SaveConfiguration(saved.Revision, "anthropic", saved.Draft with { AgentSystemPrompt = "" }, _token);
        Assert.Equal("", _host.ReadConfiguration("anthropic", _token).Draft.AgentSystemPrompt);
        Assert.DoesNotContain("agentSystemPrompt", File.ReadAllText(Path.Combine(_paths.ConfigDirectory, "providers.yaml")));
    }

    [Theory]
    [InlineData(8193, false)]
    [InlineData(32, true)]
    public void Invalid_agent_system_message_is_rejected_without_writing_configuration(int length, bool control)
    {
        var setup = _host.ReadConfiguration(null, _token);
        var prompt = control ? "bad\0control" : new string('x', length);
        var error = Assert.Throws<ArgumentException>(() => _host.SaveConfiguration(setup.Revision, null,
            setup.Draft with { AgentSystemPrompt = prompt }, _token));
        Assert.Equal("claudeSetup.system_invalid", error.Message);
        Assert.False(File.Exists(Path.Combine(_paths.ConfigDirectory, "providers.yaml")));
    }

    [Theory]
    [InlineData("[one, two]")]
    [InlineData("{text: bad}")]
    [InlineData("42")]
    public void Agent_system_message_YAML_rejects_non_text_values(string value)
    {
        Assert.Throws<ConfigValidationException>(() => new ConfigLoader().Load(
            "providers:\n  fixture:\n    family: AnthropicMessages\n    baseUrl: https://fixture.invalid\n    auth: none\n    agentSystemPrompt: " + value + "\n", null));
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-claude-setup-" + Guid.NewGuid().ToString("N"));
    private readonly DefaultPlatformPaths _paths;
    private readonly TuiClaudeAccountHost _host;
    private readonly CancellationToken _token = TestContext.Current.CancellationToken;
    public ClaudeAccountConfigurationTests()
    {
        _paths = new DefaultPlatformPaths(_root);
        _host = new TuiClaudeAccountHost(_paths, _ => null);
    }

    [Fact]
    public void Empty_installation_can_review_and_explicitly_save_the_shipped_OAuth_template()
    {
        Assert.Empty(_host.List(_token));
        var setup = _host.ReadConfiguration(null, _token);
        Assert.Equal("anthropic", setup.Draft.ProviderId);
        Assert.Contains("{version}", setup.Draft.UserAgent);
        Assert.False(File.Exists(Path.Combine(_paths.ConfigDirectory, "providers.yaml")));
        _host.SaveConfiguration(setup.Revision, null, setup.Draft, _token);
        var loaded = OmniHost.LoadUserConfiguration(_paths);
        Assert.Equal(AuthKind.OAuth, loaded.Registry.Provider("anthropic")!.Auth.Kind);
        Assert.Equal(BillingMode.IncludedQuota, loaded.Registry.Provider("anthropic")!.BillingMode);
        Assert.NotNull(OmniHost.CreateClaudeOAuthSession(_paths, "anthropic"));
        Assert.Single(_host.List(_token));
    }

    [Fact]
    public void Existing_API_provider_and_models_are_preserved_and_duplicate_ids_are_rejected()
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        var file = Path.Combine(_paths.ConfigDirectory, "providers.yaml");
        const string original = "providers:\n  anthropic:\n    family: AnthropicMessages\n    baseUrl: https://api.anthropic.com\n    authRef: user-api-key\n";
        File.WriteAllText(file, original);
        var models = Path.Combine(_paths.ConfigDirectory, "models.yaml");
        const string modelText = "models:\n  existing-model: {provider: anthropic, context: 8192, maxOutput: 1024}\n";
        File.WriteAllText(models, modelText);
        var setup = _host.ReadConfiguration(null, _token);
        Assert.NotEqual("anthropic", setup.Draft.ProviderId);
        Assert.Throws<InvalidOperationException>(() => _host.SaveConfiguration(setup.Revision, null,
            setup.Draft with { ProviderId = "anthropic" }, _token));
        Assert.Equal(original, File.ReadAllText(file));
        _host.SaveConfiguration(setup.Revision, null, setup.Draft, _token);
        var loaded = OmniHost.LoadUserConfiguration(_paths);
        Assert.Equal(AuthKind.ApiKey, loaded.Registry.Provider("anthropic")!.Auth.Kind);
        Assert.NotNull(loaded.Registry.Model("existing-model"));
        Assert.Equal(modelText, File.ReadAllText(models));
    }

    [Fact]
    public void Concurrent_changes_are_not_overwritten_and_invalid_configuration_is_not_saved()
    {
        var setup = _host.ReadConfiguration(null, _token);
        Directory.CreateDirectory(_paths.ConfigDirectory);
        var file = Path.Combine(_paths.ConfigDirectory, "providers.yaml");
        const string concurrent = "providers: {}\n";
        File.WriteAllText(file, concurrent);
        Assert.Equal("claudeSetup.changed", Assert.Throws<InvalidOperationException>(() =>
            _host.SaveConfiguration(setup.Revision, null, setup.Draft, _token)).Message);
        setup = _host.ReadConfiguration(null, _token);
        Assert.Throws<ConfigValidationException>(() => _host.SaveConfiguration(setup.Revision, null,
            setup.Draft with { TokenUrl = "http://not-https.invalid/token" }, _token));
        Assert.Equal(concurrent, File.ReadAllText(file));
        Assert.Throws<ConfigValidationException>(() => _host.SaveConfiguration(setup.Revision, null,
            setup.Draft with { UserAgent = "no-version" }, _token));
        Assert.Equal(concurrent, File.ReadAllText(file));
    }

    [Fact]
    public void Editing_preserves_the_explicit_credential_reference_and_other_fields()
    {
        var setup = _host.ReadConfiguration(null, _token);
        _host.SaveConfiguration(setup.Revision, null, setup.Draft, _token);
        var file = Path.Combine(_paths.ConfigDirectory, "providers.yaml");
        var yaml = new YamlStream(); yaml.Load(new StringReader(File.ReadAllText(file)));
        var providers = (YamlMappingNode)((YamlMappingNode)yaml.Documents[0].RootNode).Children[new YamlScalarNode("providers")];
        var provider = (YamlMappingNode)providers.Children[new YamlScalarNode("anthropic")];
        provider.Children[new YamlScalarNode("inputPricePerMillionUsd")] = new YamlScalarNode("1");
        var oauth = (YamlMappingNode)provider.Children[new YamlScalarNode("oauth")];
        oauth.Children[new YamlScalarNode("secretRef")] = new YamlScalarNode("explicit-identity");
        using (var writer = new StringWriter()) { yaml.Save(writer, false); File.WriteAllText(file, writer.ToString()); }
        setup = _host.ReadConfiguration("anthropic", _token);
        _host.SaveConfiguration(setup.Revision, "anthropic", setup.Draft with { ProfileUrl = "" }, _token);
        Assert.Contains("explicit-identity", File.ReadAllText(file));
        Assert.Contains("inputPricePerMillionUsd", File.ReadAllText(file));
        Assert.Equal("explicit-identity", OmniHost.CreateClaudeOAuthSession(_paths, "anthropic")!.SecretRef);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
