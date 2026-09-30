using OmniCore.Host;
using OmniCore.Models;

namespace OmniCore.Tests;

public sealed class ModelAliasConfigTests
{
    [Fact]
    public void Provider_profile_codex_is_read_from_providers_yaml()
    {
        var providers = """
providers:
  chatgpt: { family: OpenAIResponses, baseUrl: https://chatgpt.com/backend-api, profile: codex, auth: none }
""";
        var registry = new ConfigLoader().BuildRegistry(providers, "models:\n  gpt-test: { provider: chatgpt }\n");

        Assert.Equal("codex", registry.Provider("chatgpt")!.Profile);
    }

    [Fact]
    public void Model_with_aliases_resolves_by_alias()
    {
        var providers = """
providers:
  local: { baseUrl: http://127.0.0.1:8080, auth: none }
""";
        var models = """
models:
  qwen-27b:
    provider: local
    aliases: [fast, local]
""";
        var loader = new ConfigLoader();
        var registry = loader.BuildRegistry(providers, models);

        var resolved = registry.Resolve("fast");

        Assert.Equal("qwen-27b", resolved.Id);
        Assert.Contains("fast", resolved.Aliases);
        Assert.Contains("local", resolved.Aliases);
    }

    [Fact]
    public void Two_models_declaring_the_same_alias_throw_ambiguous()
    {
        var providers = """
providers:
  local: { baseUrl: http://127.0.0.1:8080, auth: none }
""";
        var models = """
models:
  model-a:
    provider: local
    aliases: [shared]
  model-b:
    provider: local
    aliases: [shared]
""";
        var loader = new ConfigLoader();

        var ex = Assert.Throws<AmbiguousModelAliasException>(() => loader.BuildRegistry(providers, models));

        Assert.Equal("shared", ex.Alias);
    }

    [Fact]
    public void Model_without_aliases_still_loads()
    {
        var providers = """
providers:
  local: { baseUrl: http://127.0.0.1:8080, auth: none }
""";
        var models = """
models:
  qwen-27b: { provider: local }
""";
        var loader = new ConfigLoader();
        var registry = loader.BuildRegistry(providers, models);

        var model = registry.Model("qwen-27b");

        Assert.NotNull(model);
        Assert.Empty(model!.Aliases);
    }
}
