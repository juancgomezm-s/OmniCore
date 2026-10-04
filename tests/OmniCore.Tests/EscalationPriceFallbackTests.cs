namespace OmniCore.Tests;

using OmniCore.Host;

public sealed class EscalationPriceFallbackTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Auto_escalation_skips_unknown_price_and_keeps_complete_inherited_or_zero_price(bool inheritPrice, bool zeroRates)
    {
        var input = zeroRates ? 0m : 3m;
        var output = zeroRates ? 0m : 15m;
        var rates = zeroRates ? "inputPricePerMillionUsd: 0, outputPricePerMillionUsd: 0" : "inputPricePerMillionUsd: 3, outputPricePerMillionUsd: 15";
        var providers = "providers:\n  local: { baseUrl: http://127.0.0.1:8080, auth: none }\n"
            + "  unknown-paid: { family: AnthropicMessages, baseUrl: https://unknown.example.test, authRef: synthetic-unknown }\n"
            + "  known-paid: { family: AnthropicMessages, baseUrl: https://known.example.test, authRef: synthetic-known"
            + (inheritPrice ? ", " + rates : "") + " }\n";
        var models = "models:\n  worker-model: { provider: local, context: 32768, aliases: [worker] }\n"
            + "  unknown-model: { provider: unknown-paid, context: 200000, aliases: [unknown] }\n"
            + "  known-model: { provider: known-paid, context: 200000, aliases: [known]"
            + (inheritPrice ? "" : ", " + rates) + " }\n"
            + "routing:\n  escalation: { mode: auto, chain: [worker, unknown, known] }\n";
        var loaded = new ConfigLoader().Load(providers, models);
        Assert.Null(loaded.Pricing("unknown-model"));
        var pricing = loaded.Pricing("known-model");
        Assert.NotNull(pricing);
        Assert.True(pricing.IsComplete);
        Assert.Equal(input, pricing.InputPricePerMillionUsd);
        Assert.Equal(output, pricing.OutputPricePerMillionUsd);
        var unknown = Assert.Single(ModelRoutingHost.Candidates(loaded, _ => true), c => c.Alias == "unknown-model");
        Assert.True(unknown.Available);
        var next = ModelRoutingHost.NextEscalation(loaded, "worker-model", false, 40_000, _ => true);
        Assert.NotNull(next);
        Assert.Equal("known-model", next.Alias);
    }
}
