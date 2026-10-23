using OmniCore.Domain;
using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class LlamaCppRuntimeReasoningTests
{
    [Theory]
    [InlineData("llamaCpp", "off", true)]
    [InlineData(null, "off", false)]
    [InlineData("other", "off", false)]
    [InlineData("llamaCpp", "high", false)]
    public void Runtime_admits_only_requests_supported_by_the_explicit_adapter_profile(
        string? profile, string kind, bool supported)
    {
        var request = new ReasoningRequest(kind, null);
        var capability = new ReasoningCapability(true, ["off", "high"]);
        var boost = Guid.NewGuid();
        ReasoningResolution Resolve() => OmniCliRuntime.ResolveReasoningResolution(null, null,
            capability, request, boost, 8192, ProviderFamily.OpenAiChatCompatible, profile);
        if (!supported)
        {
            Assert.Throws<InvalidOperationException>(Resolve);
            return;
        }

        var result = Resolve();
        Assert.Same(request, result.AppliedRequest);
        Assert.Equal(boost, result.TurnBoostId);
        Assert.Equal(ReasoningSelectionSource.TurnBoost, result.Source);
    }
}
