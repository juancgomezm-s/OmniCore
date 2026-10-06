namespace OmniCore.Tests;

using OmniCore.Domain;

public sealed class ModelSelectionOutputLimitContractTests
{
    [Fact]
    public void Legacy_selection_has_no_explicit_output_limit()
    {
        var selection = new ModelSelection(new ModelIdValue("model-x"), 4096, ToolMode.Direct, null);

        Assert.Null(selection.MaxOutputTokens);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(4096L)]
    [InlineData(long.MaxValue)]
    public void Positive_output_limit_is_preserved_without_truncation(long maxOutputTokens)
    {
        var selection = new ModelSelection(new ModelIdValue("model-x"), 1024, ToolMode.Direct, null,
            maxOutputTokens: maxOutputTokens);

        Assert.Equal(maxOutputTokens, selection.MaxOutputTokens);
        Assert.Equal(1024, selection.ContextBudget);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void Non_positive_output_limit_is_rejected(long maxOutputTokens)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModelSelection(
            new ModelIdValue("model-x"), 4096, ToolMode.Direct, null, maxOutputTokens: maxOutputTokens));
    }

    [Fact]
    public void Adding_output_limit_preserves_route_identity_and_reasoning_selection()
    {
        var route = new ModelRoute("provider-a", "https://api.example.test/v1",
            ProviderFamily.OpenAIResponses, "codex", "model-x");
        var reasoning = new ReasoningRequest("high", 128);
        var original = new ModelSelection(new ModelIdValue("model-x"), 4096, ToolMode.Direct,
            reasoning, route.Id, route);
        var limited = new ModelSelection(new ModelIdValue("model-x"), 4096, ToolMode.Direct,
            reasoning, route.Id, route, maxOutputTokens: 2048);

        Assert.Equal(original.RouteId, limited.RouteId);
        Assert.Equal(original.RouteIdentityHash, limited.RouteIdentityHash);
        Assert.Same(route, limited.Route);
        Assert.Same(reasoning, limited.Reasoning);
        Assert.Equal(4096, limited.ContextBudget);
        Assert.Equal(2048, limited.MaxOutputTokens);
    }
}
