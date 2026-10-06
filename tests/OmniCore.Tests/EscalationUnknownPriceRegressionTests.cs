namespace OmniCore.Tests;

using OmniCore.Host;
using OmniCore.Models;

public sealed class EscalationUnknownPriceRegressionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Auto_escalation_requires_declared_price_before_selecting_cloud_candidate(bool knownPrice)
    {
        const string providers = """
providers:
  local: { baseUrl: http://127.0.0.1:8080, auth: none }
  cloud: { family: AnthropicMessages, baseUrl: https://api.example.test, authRef: synthetic-key-reference }
""";
        const string models = """
models:
  worker-model: { provider: local, context: 32768, aliases: [worker] }
  cloud-model: { provider: cloud, context: 200000, aliases: [frontier] }
routing:
  escalation: { mode: auto, chain: [worker, frontier] }
""";
        var configuredModels = knownPrice
            ? models.Replace("aliases: [frontier] }", "aliases: [frontier], inputPricePerMillionUsd: 3, outputPricePerMillionUsd: 15 }", StringComparison.Ordinal)
            : models;
        var loaded = new ConfigLoader().Load(providers, configuredModels);
        Assert.Equal("auto", ModelRoutingHost.EscalationMode(loaded));
        var candidate = Assert.Single(ModelRoutingHost.Candidates(loaded, _ => true), c => c.ModelId == "cloud-model");
        Assert.True(candidate.Available);
        if (knownPrice)
            Assert.Equal(3m, candidate.PricePerMillionTokensUsd);
        else
            Assert.Null(candidate.PricePerMillionTokensUsd);

        var next = ModelRoutingHost.NextEscalation(loaded, "worker-model", false, 40_000, _ => true);
        if (knownPrice)
        {
            Assert.NotNull(next);
            Assert.Equal("cloud-model", next.ModelId);
        }
        else
            Assert.Null(next);
    }
}
