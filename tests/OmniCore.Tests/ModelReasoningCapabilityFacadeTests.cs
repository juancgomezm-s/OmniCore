namespace OmniCore.Tests;

using OmniCore.Host;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class ModelReasoningCapabilityFacadeTests
{
    [Fact]
    public void Facade_reads_exact_declared_route_levels_and_endpoint_override_remains_unknown()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-reasoning-facade-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "data", "config");
        var previousConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var previousEndpoint = Environment.GetEnvironmentVariable("OMNI_BASE_URL");
        Directory.CreateDirectory(config);
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", null);
            File.WriteAllText(Path.Combine(config, "providers.yaml"), "providers:\n  fixture: {family: OpenAIResponses, baseUrl: https://fixture.invalid/v1, billingMode: Local}\n");
            File.WriteAllText(Path.Combine(config, "models.yaml"), "models:\n  declared:\n    provider: fixture\n    context: 8192\n    maxOutput: 4096\n    reasoning: {supported: true, effortLevels: [low, high, budget]}\n  legacy:\n    provider: fixture\n    context: 8192\n    maxOutput: 4096\n");
            using var host = ModelPolicyHost.Create(Path.Combine(root, "data"));
            var capability = Assert.IsType<ModelReasoningCapabilityDto>(host.ReadReasoningCapability("declared"));
            Assert.True(capability.Supported);
            Assert.Equal(new[] { "low", "high", "budget" }, capability.EffortLevels);
            Assert.Null(host.ReadReasoningCapability("legacy")!.Supported);
            Assert.Null(host.ReadReasoningCapability("missing"));
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", "https://other.invalid/v1");
            var changed = host.ReadReasoningCapability("declared")!;
            Assert.NotEqual(capability.RouteId, changed.RouteId);
            Assert.Null(changed.Supported);
            Assert.Null(changed.EffortLevels);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", previousConfig);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", previousEndpoint);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
