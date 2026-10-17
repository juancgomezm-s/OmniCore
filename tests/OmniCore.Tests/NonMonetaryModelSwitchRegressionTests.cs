using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Protocol;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed partial class CrossWorkspaceDailyCapRegressionTests
{
    [Theory]
    [InlineData("Local", false)]
    [InlineData("IncludedQuota", false)]
    [InlineData("IncludedQuota", true)]
    public async Task Normal_chat_ignores_frozen_USD_cap_on_non_monetary_route_without_erasing_unknown_usage(string billing, bool lowQuota)
    {
        var root = FixtureRoot();
        var data = Path.Combine(root, "data"); var config = Path.Combine(root, "config");
        var workspace = Path.Combine(root, "workspace");
        foreach (var directory in new[] { data, config, workspace }) Directory.CreateDirectory(directory);
        var environment = ProcessEnvironment.Capture();
        var provider = new ScriptedHttpProvider(100);
        var runtime = OmniCliRuntime.Create(workspace, queryQuota: (id, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            return System.Threading.Tasks.Task.FromResult(new ProviderQuotaSnapshot(id, "fixture-account", "fixture", now,
                lowQuota ? MetricAvailability.Reported : MetricAvailability.Unknown,
                lowQuota ? [new ProviderUsageWindow("fixture-window", "fixture", 300,
                    new(MetricAvailability.Reported, 95d, "fixture", now), new(MetricAvailability.Reported, 5d, "fixture", now),
                    now.AddHours(1), null)] : [], [], null));
        });
        try
        {
            SetEnvironment(data, config);
            WriteConfiguration(config, provider.BaseUrl, 20m, billingMode: billing, contextCapacity: 8192);
            var providersFile = Path.Combine(config, "providers.yaml");
            File.WriteAllText(providersFile, File.ReadAllText(providersFile)
                .Replace("inputPricePerMillionUsd: 1", "").Replace("outputPricePerMillionUsd: 1", ""));
            var output = new List<string>();
            var host = new TuiTurnHost(runtime);
            Assert.Equal(0, await host.ExecuteAsync("first", output.Add, TestContext.Current.CancellationToken));
            // Reproduce an old Session snapshot carrying a 5 USD cap after User settings stop imposing one.
            File.WriteAllText(Path.Combine(config, "settings.yaml"), "{}");
            Assert.Equal(0, await host.ExecuteAsync("second", output.Add, TestContext.Current.CancellationToken));
            Assert.Equal(2, provider.RequestCount);
            var events = ReadCurrentEvents(workspace);
            Assert.Equal(5m, events.OfType<SessionRoutingPolicySet>().Single().Policy.SessionSpendLimit);
            Assert.Equal(2, events.OfType<ModelStepCompleted>().Count());
            Assert.All(events.OfType<ModelStepCompleted>(), completion => Assert.Null(completion.CostUsd));
            Assert.DoesNotContain(events.OfType<InteractionRequested>(), request => request.Kind == InteractionKind.BudgetExceeded);
        }
        finally
        {
            await provider.DisposeAsync(); CloseRuntime(runtime);
            ProcessEnvironment.Restore(environment); ClearFixturePools(data, workspace);
            Directory.Delete(root, true);
        }
    }
}
