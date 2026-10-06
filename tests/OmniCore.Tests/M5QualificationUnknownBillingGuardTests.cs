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
/// Offline contracts for billing mode Unknown: unknown must not be treated as free when no
/// complete configured estimate exists. Local fixtures still report no measured cost without prices.
/// </summary>
public sealed class M5QualificationUnknownBillingGuardTests
{
    private const string ModelId = "m5-unknown-billing-qualification-fixture";
    private const string ProviderId = "m5-unknown-billing-qualification-provider";

    private static string TempDir()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-m5-unknown-billing-guard",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteConfiguration(string dataDirectory, string billingMode, bool withPrices,
        bool withPartialPrices = false)
    {
        var paths = OmniHost.CreatePlatformPaths(dataDirectory);
        Directory.CreateDirectory(paths.ConfigDirectory);
        var providerLines = new List<string>
        {
            "providers:",
            "  " + ProviderId + ":",
            "    family: OpenAiChatCompatible",
            "    baseUrl: https://fixture.invalid/v1",
            "    auth: none",
            "    billingMode: " + billingMode,
        };
        if (withPrices)
        {
            providerLines.Add("    inputPricePerMillionUsd: 2");
            providerLines.Add("    outputPricePerMillionUsd: 8");
        }
        else if (withPartialPrices)
        {
            providerLines.Add("    inputPricePerMillionUsd: 2");
        }

        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"),
            string.Join(Environment.NewLine, providerLines));
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "models.yaml"), $$"""
            models:
              {{ModelId}}:
                provider: {{ProviderId}}
                context: 8192
                recommendedUsableContext: 8192
                maxOutput: 2048
            """);
    }

    private static Probe Probe() => new(ProbeId.WellKnown("unknown-billing-reading"),
        ProbeKind.Reading, "Reply with only the word measured.", "measured", 0m);

    private static QualificationOptions Options(IModelProvider provider, decimal cap, Probe? probe = null) => new()
    {
        Suite = "quick",
        ConsentGiven = true,
        MaxTotalCostUsd = cap,
        Probes = [probe ?? Probe()],
        Provider = provider,
    };

    [Fact]
    public async Task Unknown_without_complete_prices_fails_closed_at_zero_cap_before_call()
    {
        await AssertUnknownUnpricedBlockedAsync(0m);
    }

    [Fact]
    public async Task Unknown_without_complete_prices_fails_closed_at_positive_cap_before_call()
    {
        await AssertUnknownUnpricedBlockedAsync(1m);
    }

    [Fact]
    public async Task Unknown_with_partial_prices_fails_closed_before_call()
    {
        await AssertUnknownUnpricedBlockedAsync(1m, withPartialPrices: true);
    }

    [Fact]
    public async Task Unknown_with_complete_pricing_keeps_the_configured_estimate_and_can_qualify()
    {
        var directory = TempDir();
        try
        {
            WriteConfiguration(directory, "Unknown", withPrices: true);
            var probe = Probe();
            var provider = new FixtureProvider(probe.Expected);
            QualificationRunResult result;
            using (var host = ModelQualificationHost.Create(directory))
            {
                result = await host.QualifyAsync(ModelId, Options(provider, 1m, probe), CancellationToken.None);
            }

            Assert.Equal(1, provider.Calls);
            Assert.Equal(ModelQualificationState.Qualified.ToString(), result.NewState);
            Assert.Equal(0.032768m, result.EstimatedCostUsd);
            Assert.Equal("max-declared-and-configured-descriptor-token-estimate", result.EstimatedCostSource);
            var outcome = Assert.Single(result.Probes);
            Assert.Equal(ProbeStatus.Passed.ToString(), outcome.Status);
            Assert.Equal(1d, outcome.Score);
            Assert.Equal(0.000066m, outcome.CostUsd);
            Assert.Equal(new QualificationProbeUsage(17, 4, null, null, null), outcome.Usage);

            using var store = OmniHost.CreateModelQualificationStore(directory);
            var key = QualificationKey(directory);
            var profile = store.Get(key, CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(ModelQualificationState.Qualified, profile!.State);
            Assert.Equal(1L, profile.ProfileRevision);
            Assert.Contains(store.Traits(key, profile.ProfileRevision, CancellationToken.None),
                trait => trait.Trait == "InstructionFollowing" && trait.Value == 1d
                    && trait.Samples == 1 && trait.Source == "empirical");
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public async Task Explicit_local_without_prices_preserves_unknown_measured_cost()
    {
        var directory = TempDir();
        try
        {
            WriteConfiguration(directory, "Local", withPrices: false);
            var probe = Probe();
            var provider = new FixtureProvider(probe.Expected);
            QualificationRunResult result;
            using (var host = ModelQualificationHost.Create(directory))
            {
                result = await host.QualifyAsync(ModelId, Options(provider, 0m, probe), CancellationToken.None);
            }

            Assert.Equal(1, provider.Calls);
            Assert.Equal(ModelQualificationState.Qualified.ToString(), result.NewState);
            var outcome = Assert.Single(result.Probes);
            Assert.Equal(ProbeStatus.Passed.ToString(), outcome.Status);
            Assert.Equal(1d, outcome.Score);
            Assert.Null(outcome.CostUsd);
            Assert.Equal(new QualificationProbeUsage(17, 4, null, null, null), outcome.Usage);

            using var store = OmniHost.CreateModelQualificationStore(directory);
            var key = QualificationKey(directory);
            var profile = store.Get(key, CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(ModelQualificationState.Qualified, profile!.State);
            Assert.Contains(store.Traits(key, profile.ProfileRevision, CancellationToken.None),
                trait => trait.Trait == "InstructionFollowing" && trait.Value == 1d
                    && trait.Samples == 1 && trait.Source == "empirical");
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    private static async Task AssertUnknownUnpricedBlockedAsync(decimal cap, bool withPartialPrices = false)
    {
        var directory = TempDir();
        try
        {
            WriteConfiguration(directory, "Unknown", withPrices: false,
                withPartialPrices: withPartialPrices);
            var probe = Probe();
            var provider = new FixtureProvider(probe.Expected);
            using var host = ModelQualificationHost.Create(directory);

            await Assert.ThrowsAsync<ModelQualificationCostEvidenceUnavailableException>(() =>
                host.QualifyAsync(ModelId, Options(provider, cap, probe), CancellationToken.None));

            Assert.Equal(0, provider.Calls);
            using var store = OmniHost.CreateModelQualificationStore(directory);
            var key = QualificationKey(directory);
            Assert.Null(store.Get(key, CancellationToken.None));
            Assert.Empty(store.Traits(key, 1, CancellationToken.None));
            Assert.Empty(store.List(CancellationToken.None));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    private static ModelQualificationKey QualificationKey(string directory)
    {
        var loaded = OmniHost.LoadUserConfiguration(OmniHost.CreatePlatformPaths(directory));
        var model = loaded.Registry.Model(ModelId)!;
        var provider = loaded.Registry.Provider(ProviderId)!;
        return ModelQualificationHost.QualificationKeyFor(model, provider);
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

    private sealed class FixtureProvider : IModelProvider
    {
        private readonly string _answer;

        public int Calls { get; private set; }

        public ProviderCapabilities Capabilities => new(reportsUsage: true, reportsCost: false,
            reportsQuota: false);

        public FixtureProvider(string answer) => _answer = answer;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            await Task.Yield();
            yield return new ResponseCompleted(new ModelResponse(
                [new TextBlock(_answer)], StopReason.EndTurn,
                new TokenUsage(17, 4, 3, 2, 1), null,
                new ProviderMetadata("unknown-billing-fixture", ModelId, null),
                TokenUsageFields.Input | TokenUsageFields.Output));
        }
    }
}
