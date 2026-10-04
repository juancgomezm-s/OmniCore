namespace OmniCore.Tests;

using OmniCore.Host;

public sealed class EscalationIncompletePriceRegressionTests
{
    [Theory]
    [InlineData("inputPricePerMillionUsd: 3")]
    [InlineData("outputPricePerMillionUsd: 15")]
    public void Automatic_api_key_escalation_requires_both_price_components(string declaredPrice)
    {
        const string providers = """
providers:
  local: { baseUrl: http://127.0.0.1:8080, auth: none }
  paid: { family: AnthropicMessages, baseUrl: https://api.example.test, authRef: synthetic-key-reference }
""";
        var models = "models:\n  worker-model: { provider: local, context: 32768, aliases: [worker] }\n"
            + "  target-model: { provider: paid, context: 200000, aliases: [target], " + declaredPrice + " }\n"
            + "routing:\n  escalation: { mode: auto, chain: [worker, target] }\n";
        var loaded = new ConfigLoader().Load(providers, models);
        var pricing = loaded.Pricing("target-model");
        Assert.NotNull(pricing);
        Assert.False(pricing.IsComplete);
        Assert.Null(ModelRoutingHost.NextEscalation(loaded, "worker-model", false, 40_000, _ => true));
    }
}
