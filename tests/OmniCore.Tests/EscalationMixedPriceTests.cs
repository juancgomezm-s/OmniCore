namespace OmniCore.Tests;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Models;
public sealed class EscalationMixedPriceTests
{
    [Fact]
    public void Explicit_zero_input_preserves_inherited_output_for_automatic_selection()
    {
        const string providers = """
providers:
  local: { baseUrl: http://127.0.0.1:8080, auth: none }
  paid: { family: AnthropicMessages, baseUrl: https://api.example.test, authRef: synthetic-reference, inputPricePerMillionUsd: 3, outputPricePerMillionUsd: 15 }
""";
        const string models = """
models:
  worker-model: { provider: local, context: 32768, aliases: [worker] }
  target-model: { provider: paid, context: 200000, aliases: [target], inputPricePerMillionUsd: 0 }
routing:
  escalation: { mode: auto, chain: [worker, target] }
""";
        var loaded = new ConfigLoader().Load(providers, models);
        var price = loaded.Pricing("target-model");
        Assert.NotNull(price);
        Assert.True(price.IsComplete);
        Assert.Equal(0m, price.InputPricePerMillionUsd);
        Assert.Equal(15m, price.OutputPricePerMillionUsd);
        Assert.Equal(15m, price.CostUsd(new TokenUsage(1_000_000, 1_000_000, 0, 0, 0)));
        var next = ModelRoutingHost.NextEscalation(loaded, "worker-model", false, 40_000, _ => true);
        Assert.NotNull(next);
        Assert.Equal("target-model", next.ModelId);
    }
}
