namespace OmniCore.Tests;

using OmniCore.Domain;
using OmniCore.Abstractions;
using OmniCore.Host;
using OmniCore.Models;

public sealed class HostOutputTokenLimitTests
{
    [Theory]
    [InlineData(ProviderFamily.OpenAIResponses, null, 2048L)]
    [InlineData(ProviderFamily.OpenAIResponses, "api", 2048L)]
    [InlineData(ProviderFamily.OpenAIResponses, "codex", null)]
    [InlineData(ProviderFamily.OpenAIResponses, "CODEX", null)]
    [InlineData(ProviderFamily.AnthropicMessages, null, 2048L)]
    [InlineData(ProviderFamily.OpenAiChatCompatible, null, null)]
    public void Host_only_declares_a_bound_on_wired_native_api_routes(
        ProviderFamily family, string? profile, long? expected)
    {
        var model = new ModelDefinition("fixture", "p", 4096, 4096, 2048);
        var provider = new ProviderDescriptor("p", family, "https://fixture.test", AuthConfig.None(),
            false, false, true) { Profile = profile };
        Assert.Equal(expected, ModelRoutingHost.OutputTokenLimit(model, provider));
    }

    [Fact]
    public void Missing_provider_or_nonpositive_descriptor_does_not_fabricate_a_bound()
    {
        var provider = new ProviderDescriptor("p", ProviderFamily.OpenAIResponses,
            "https://fixture.test", AuthConfig.None(), false, false, true);
        Assert.Null(ModelRoutingHost.OutputTokenLimit(new ModelDefinition("m", "p", 4096, 4096, 2048), null));
        Assert.Null(ModelRoutingHost.OutputTokenLimit(new ModelDefinition("m", "p", 4096, 4096, 0), provider));
        Assert.Null(ModelRoutingHost.OutputTokenLimit(new ModelDefinition("m", "p", 4096, 4096, -1), provider));
    }
}
