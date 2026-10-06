namespace OmniCore.Tests;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;

public sealed class NativeOutputTokenLimitTests
{
    [Theory]
    [InlineData(16L, null, 16L)]
    [InlineData(4096L, 16, 16L)]
    [InlineData(16L, 4096, 16L)]
    [InlineData(null, 4096, 4096L)]
    [InlineData(4096L, null, 4096L)]
    [InlineData(long.MaxValue, null, long.MaxValue)]
    public void Responses_api_uses_smallest_explicit_limit_without_truncating(
        long? selectionLimit, int? optionLimit, long expected)
    {
        var options = new OpenAIResponsesOptions { MaxOutputTokens = optionLimit };

        using var body = JsonDocument.Parse(OpenAIResponsesProvider.BuildBody(Request(selectionLimit), options));

        Assert.Equal(expected, body.RootElement.GetProperty("max_output_tokens").GetInt64());
    }

    [Fact]
    public void Responses_api_omits_output_limit_when_neither_selection_nor_options_specify_one()
    {
        using var body = JsonDocument.Parse(OpenAIResponsesProvider.BuildBody(Request(), new OpenAIResponsesOptions()));

        Assert.False(body.RootElement.TryGetProperty("max_output_tokens", out _));
    }

    [Theory]
    [InlineData(15L, null)]
    [InlineData(null, 15)]
    [InlineData(4096L, 15)]
    public void Responses_api_rejects_limits_below_the_official_minimum(long? selectionLimit, int? optionLimit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OpenAIResponsesProvider.BuildBody(
            Request(selectionLimit), new OpenAIResponsesOptions { MaxOutputTokens = optionLimit }));
    }

    [Theory]
    [InlineData(16L, null)]
    [InlineData(null, 16)]
    public void Codex_profile_rejects_explicit_api_output_limits(long? selectionLimit, int? optionLimit)
    {
        Assert.Throws<NotSupportedException>(() => OpenAIResponsesProvider.BuildBody(Request(selectionLimit),
            new OpenAIResponsesOptions { Profile = ResponsesProfile.Codex, MaxOutputTokens = optionLimit }));
    }

    [Fact]
    public void Codex_profile_keeps_its_existing_body_without_an_api_output_parameter()
    {
        using var body = JsonDocument.Parse(OpenAIResponsesProvider.BuildBody(Request(),
            new OpenAIResponsesOptions { Profile = ResponsesProfile.Codex }));

        Assert.False(body.RootElement.TryGetProperty("max_output_tokens", out _));
    }

    [Fact]
    public void Anthropic_selection_limit_is_exact_and_not_raised_to_reasoning_budget_plus_margin()
    {
        var request = Request(2048, modelReasoning: new ReasoningRequest("budget", 1024));

        using var body = JsonDocument.Parse(AnthropicMessagesProvider.BuildBody(request, new AnthropicProviderOptions()));

        Assert.Equal(2048, body.RootElement.GetProperty("max_tokens").GetInt64());
        Assert.Equal(1024, body.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
    }

    [Theory]
    [InlineData(1024L)]
    [InlineData(1023L)]
    public void Anthropic_limit_must_exceed_the_reasoning_budget(long maxOutputTokens)
    {
        var request = Request(maxOutputTokens, modelReasoning: new ReasoningRequest("budget", 1024));

        Assert.Throws<ArgumentOutOfRangeException>(() => AnthropicMessagesProvider.BuildBody(
            request, new AnthropicProviderOptions()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1023)]
    public void Anthropic_rejects_thinking_budgets_below_1024(int thinkingBudget)
    {
        var request = Request(modelReasoning: new ReasoningRequest("budget", thinkingBudget));

        Assert.Throws<ArgumentOutOfRangeException>(() => AnthropicMessagesProvider.BuildBody(
            request, new AnthropicProviderOptions()));
    }

    [Fact]
    public void Anthropic_legacy_fallbacks_preserve_default_and_budget_plus_margin()
    {
        using var defaultBody = JsonDocument.Parse(AnthropicMessagesProvider.BuildBody(Request(),
            new AnthropicProviderOptions()));
        using var reasoningBody = JsonDocument.Parse(AnthropicMessagesProvider.BuildBody(
            Request(modelReasoning: new ReasoningRequest("budget", 1024)), new AnthropicProviderOptions()));

        Assert.Equal(8192, defaultBody.RootElement.GetProperty("max_tokens").GetInt64());
        Assert.Equal(5120, reasoningBody.RootElement.GetProperty("max_tokens").GetInt64());
    }

    [Fact]
    public void Anthropic_reasoning_fallback_adds_margin_without_int_overflow_and_request_reasoning_wins()
    {
        var largeBudget = Request(modelReasoning: new ReasoningRequest("budget", int.MaxValue));
        using var largeBody = JsonDocument.Parse(AnthropicMessagesProvider.BuildBody(largeBudget,
            new AnthropicProviderOptions()));
        Assert.Equal((long)int.MaxValue + 4096L, largeBody.RootElement.GetProperty("max_tokens").GetInt64());

        var requestReasoning = Request(modelReasoning: new ReasoningRequest("budget", 1024),
            requestReasoning: new ReasoningRequest("budget", 2048));
        using var overrideBody = JsonDocument.Parse(AnthropicMessagesProvider.BuildBody(requestReasoning,
            new AnthropicProviderOptions()));
        Assert.Equal(6144, overrideBody.RootElement.GetProperty("max_tokens").GetInt64());
        Assert.Equal(2048, overrideBody.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
    }

    private static ModelRequest Request(long? selectionLimit = null, ReasoningRequest? modelReasoning = null,
        ReasoningRequest? requestReasoning = null) => new(
        new ModelSelection(new ModelIdValue("fixture-model"), 4096, ToolMode.Direct, modelReasoning,
            maxOutputTokens: selectionLimit),
        Array.Empty<ModelMessage>(), null, Array.Empty<ToolDefinition>(), ToolChoice.Auto(), null,
        requestReasoning, null, null);
}
