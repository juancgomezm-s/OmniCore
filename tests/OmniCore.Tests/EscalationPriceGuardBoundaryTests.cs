namespace OmniCore.Tests;

using OmniCore.Host;

public sealed class EscalationPriceGuardBoundaryTests
{
    [Theory]
    [InlineData("auto", true)]
    [InlineData("ask", true)]
    [InlineData("auto", false)]
    [InlineData("ask", false)]
    public void Unknown_price_guard_is_limited_to_automatic_api_key_escalation(string mode, bool apiKey)
    {
        const string providers = """
providers:
  local: { baseUrl: http://127.0.0.1:8080, auth: none }
  paid: { family: AnthropicMessages, baseUrl: https://api.example.test, authRef: synthetic-key-reference }
""";
        var provider = apiKey ? "paid" : "local";
        var models = "models:\n  worker-model: { provider: local, context: 32768, aliases: [worker] }\n"
            + "  target-model: { provider: " + provider + ", context: 200000, aliases: [target] }\n"
            + "routing:\n  escalation: { mode: " + mode + ", chain: [worker, target] }\n";
        var loaded = new ConfigLoader().Load(providers, models);
        Assert.Equal(mode, ModelRoutingHost.EscalationMode(loaded));
        Assert.Null(loaded.Pricing("target-model"));
        var next = ModelRoutingHost.NextEscalation(loaded, "worker-model", false, 40_000, _ => true);
        if (mode == "auto" && apiKey)
            Assert.Null(next);
        else
        {
            Assert.NotNull(next);
            Assert.Equal("target-model", next.ModelId);
        }
    }
}
