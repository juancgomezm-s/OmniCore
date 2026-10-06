using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>
/// Host integration coverage for qualification cost quotes from private user configuration.
/// Every model response comes from an in-memory provider fixture; no real billing or provider call.
/// </summary>
public sealed class M5QualificationConfiguredPricingTests
{
    private const string ModelId = "m5-priced-qualification-fixture";
    private const string ProviderId = "m5-priced-qualification-provider";

    private static string TempDir()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-m5-configured-qualification-pricing",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteConfiguration(string dataDirectory, string providerPricing,
        string billingMode = "Unknown")
    {
        var paths = OmniHost.CreatePlatformPaths(dataDirectory);
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), $$"""
            providers:
                {{ProviderId}}:
                  baseUrl: https://fixture.invalid/v1
                  auth: none
                  billingMode: {{billingMode}}
            {{providerPricing}}
            """);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "models.yaml"), $$"""
            models:
              {{ModelId}}:
                provider: {{ProviderId}}
                context: 8192
                recommendedUsableContext: 8192
                maxOutput: 2048
            """);
    }

    private static Probe Probe() => new(ProbeId.WellKnown("configured-pricing-reading"),
        ProbeKind.Reading, "Reply with only the word measured.", "measured", 0m);

    private static QualificationOptions Options(IModelProvider provider, Probe probe) => new()
    {
        Suite = "quick",
        ConsentGiven = true,
        MaxTotalCostUsd = 1m,
        Probes = [probe],
        Provider = provider,
    };

    private static (ModelDefinition Model, ProviderDescriptor Provider, ModelQualificationKey Key) Identity(
        string dataDirectory)
    {
        var loaded = OmniHost.LoadUserConfiguration(OmniHost.CreatePlatformPaths(dataDirectory));
        var model = loaded.Registry.Model(ModelId)!;
        var provider = loaded.Registry.Provider(ProviderId)!;
        return (model, provider, ModelQualificationHost.QualificationKeyFor(model, provider));
    }

    private static void DeleteFixtureDirectory(string directory)
    {
        var databasePath = Path.GetFullPath(Path.Combine(directory, "user.db"));
        using (var connection = new SqliteConnection("DataSource=" + databasePath))
        {
            SqliteConnection.ClearPool(connection);
        }

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Host_uses_configured_provider_prices_and_preserves_only_reported_usage_fields()
    {
        var directory = TempDir();
        try
        {
            WriteConfiguration(directory,
                "      inputPricePerMillionUsd: 2\n      outputPricePerMillionUsd: 8");
            var probe = Probe();
            var provider = new FixtureProvider(probe.Expected);
            QualificationRunResult result;
            using (var host = ModelQualificationHost.Create(directory))
            {
                result = await host.QualifyAsync(ModelId, Options(provider, probe), CancellationToken.None);
            }

            Assert.Equal(1, provider.Calls);
            Assert.Equal(ModelQualificationState.ProvisionallyClassified.ToString(), result.NewState);
            Assert.False(result.SuiteComplete);
            var outcome = Assert.Single(result.Probes);
            Assert.Equal(ProbeStatus.Passed.ToString(), outcome.Status);
            Assert.Equal(0.000066m, outcome.CostUsd);
            Assert.Equal(new QualificationProbeUsage(17, 4, null, null, null), outcome.Usage);

            var identity = Identity(directory);
            using var store = OmniHost.CreateModelQualificationStore(directory);
            var persisted = store.Get(identity.Key, CancellationToken.None);
            Assert.NotNull(persisted);
            Assert.Equal(ModelQualificationState.ProvisionallyClassified, persisted!.State);
            Assert.Equal(1L, persisted.ProfileRevision);
            Assert.Single(store.List(CancellationToken.None));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public async Task Host_keeps_cost_unknown_when_configured_price_is_partial()
    {
        var directory = TempDir();
        try
        {
            // This control exercises missing measured cost on an explicitly local fixture.
            // Potentially paid Unknown routes with partial pricing are tested fail-closed separately.
            WriteConfiguration(directory, "      inputPricePerMillionUsd: 2", billingMode: "Local");
            var probe = Probe();
            var provider = new FixtureProvider(probe.Expected);
            QualificationRunResult result;
            using (var host = ModelQualificationHost.Create(directory))
            {
                result = await host.QualifyAsync(ModelId, Options(provider, probe), CancellationToken.None);
            }

            Assert.Equal(1, provider.Calls);
            Assert.Equal(ModelQualificationState.ProvisionallyClassified.ToString(), result.NewState);
            Assert.False(result.SuiteComplete);
            var outcome = Assert.Single(result.Probes);
            Assert.Equal(ProbeStatus.Passed.ToString(), outcome.Status);
            Assert.Null(outcome.CostUsd);
            Assert.Equal(new QualificationProbeUsage(17, 4, null, null, null), outcome.Usage);

            var identity = Identity(directory);
            using var store = OmniHost.CreateModelQualificationStore(directory);
            var persisted = store.Get(identity.Key, CancellationToken.None);
            Assert.NotNull(persisted);
            Assert.Equal(ModelQualificationState.ProvisionallyClassified, persisted!.State);
            Assert.Equal(1L, persisted.ProfileRevision);
            Assert.Single(store.List(CancellationToken.None));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    private sealed class FixtureProvider : IModelProvider
    {
        private readonly string _answer;

        public int Calls { get; private set; }

        public FixtureProvider(string answer) => _answer = answer;

        public ProviderCapabilities Capabilities => new(reportsUsage: true, reportsCost: false,
            reportsQuota: false);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            await Task.Yield();
            yield return new ResponseCompleted(new ModelResponse(
                [new TextBlock(_answer)], StopReason.EndTurn,
                new TokenUsage(17, 4, 3, 2, 1), null,
                new ProviderMetadata("configured-pricing-fixture", ModelId, null),
                TokenUsageFields.Input | TokenUsageFields.Output));
        }
    }
}
