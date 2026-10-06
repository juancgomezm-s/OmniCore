using System.Runtime.CompilerServices;
using System.Globalization;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>
/// Offline regression contract for a zero-dollar cap with explicitly metered pricing.
/// Responses and usage come only from an in-memory fixture; this does not claim real billing.
/// </summary>
public sealed class M5QualificationConfiguredCostCapTests
{
    private const string ModelId = "m5-cost-cap-qualification-fixture";
    private const string ProviderId = "m5-cost-cap-qualification-provider";

    private static string TempDir()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-m5-configured-cost-cap",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteConfiguration(string dataDirectory, decimal? inputPrice = 2m,
        decimal? outputPrice = 8m)
    {
        var paths = OmniHost.CreatePlatformPaths(dataDirectory);
        Directory.CreateDirectory(paths.ConfigDirectory);
        var providerLines = new List<string>
        {
            "providers:",
            "  " + ProviderId + ":",
            "    baseUrl: https://fixture.invalid/v1",
            "    auth: none",
            "    billingMode: MeteredCurrency",
        };
        if (inputPrice is { } input)
            providerLines.Add("    inputPricePerMillionUsd: " + input.ToString(CultureInfo.InvariantCulture));
        if (outputPrice is { } output)
            providerLines.Add("    outputPricePerMillionUsd: " + output.ToString(CultureInfo.InvariantCulture));
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

    private static Probe Probe() => new(ProbeId.WellKnown("configured-cost-cap-reading"),
        ProbeKind.Reading, "Reply with only the word measured.", "measured", 0m);

    private static QualificationOptions Options(IModelProvider provider, Probe probe, decimal cap) =>
        Options(provider, [probe], cap);

    private static QualificationOptions Options(IModelProvider provider, IReadOnlyList<Probe> probes, decimal cap) => new()
    {
        Suite = "quick",
        ConsentGiven = true,
        MaxTotalCostUsd = cap,
        Probes = probes,
        Provider = provider,
    };

    [Fact]
    public async Task Zero_cap_rejects_metered_probe_with_zero_declared_max_before_invocation()
    {
        var directory = TempDir();
        try
        {
            WriteConfiguration(directory);
            var probe = Probe();
            var provider = new FixtureProvider(probe.Expected);
            using var host = ModelQualificationHost.Create(directory);

            var exception = await Assert.ThrowsAsync<ModelQualificationCostCapException>(() =>
                host.QualifyAsync(ModelId, Options(provider, probe, 0m), CancellationToken.None));

            Assert.Equal(0m, exception.CapUsd);
            Assert.Equal(0, provider.Calls);
            using var store = OmniHost.CreateModelQualificationStore(directory);
            Assert.Empty(store.List(CancellationToken.None));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public async Task Cap_below_configured_descriptor_estimate_rejects_before_invocation()
    {
        var directory = TempDir();
        try
        {
            WriteConfiguration(directory);
            var probe = Probe();
            var provider = new FixtureProvider(probe.Expected);
            using var host = ModelQualificationHost.Create(directory);

            var exception = await Assert.ThrowsAsync<ModelQualificationCostCapException>(() =>
                host.QualifyAsync(ModelId, Options(provider, probe, 0.01m), CancellationToken.None));

            Assert.Equal(0.01m, exception.CapUsd);
            Assert.Equal(0.032768m, exception.EstimatedUsd);
            Assert.Equal(0, provider.Calls);
            using var store = OmniHost.CreateModelQualificationStore(directory);
            Assert.Empty(store.List(CancellationToken.None));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public async Task Partial_metered_pricing_fails_closed_before_invocation()
    {
        var directory = TempDir();
        try
        {
            WriteConfiguration(directory, inputPrice: 2m, outputPrice: null);
            var probe = Probe();
            var provider = new FixtureProvider(probe.Expected);
            using var host = ModelQualificationHost.Create(directory);

            await Assert.ThrowsAsync<ModelQualificationCostEvidenceUnavailableException>(() =>
                host.QualifyAsync(ModelId, Options(provider, probe, 1m), CancellationToken.None));

            Assert.Equal(0, provider.Calls);
            using var store = OmniHost.CreateModelQualificationStore(directory);
            Assert.Empty(store.List(CancellationToken.None));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public async Task Unrepresentable_sum_of_declared_probe_maxima_fails_closed_before_invocation()
    {
        var directory = TempDir();
        try
        {
            WriteConfiguration(directory);
            var probes = new[]
            {
                new Probe(ProbeId.WellKnown("declared-overflow-first"), ProbeKind.Reading,
                    "Reply with only the word measured.", "measured", decimal.MaxValue),
                new Probe(ProbeId.WellKnown("declared-overflow-second"), ProbeKind.Reading,
                    "Reply with only the word measured.", "measured", decimal.MaxValue),
            };
            var provider = new FixtureProvider("measured");
            using var host = ModelQualificationHost.Create(directory);

            // The declared maxima overflow when summed, even though the accepted cap is decimal.MaxValue.
            // This exercises declared-probe estimate handling, not configured-price arithmetic.
            await Assert.ThrowsAsync<ModelQualificationCostEvidenceUnavailableException>(() =>
                host.QualifyAsync(ModelId, Options(provider, probes, decimal.MaxValue), CancellationToken.None));

            Assert.Equal(0, provider.Calls);
            using var store = OmniHost.CreateModelQualificationStore(directory);
            Assert.Empty(store.List(CancellationToken.None));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public async Task Positive_cap_runs_fixture_and_reports_configured_quote()
    {
        var directory = TempDir();
        try
        {
            WriteConfiguration(directory);
            var probe = Probe();
            var provider = new FixtureProvider(probe.Expected);
            QualificationRunResult result;
            using (var host = ModelQualificationHost.Create(directory))
            {
                result = await host.QualifyAsync(ModelId, Options(provider, probe, 1m), CancellationToken.None);
            }

            Assert.Equal(1, provider.Calls);
            Assert.Equal(ModelQualificationState.ProvisionallyClassified.ToString(), result.NewState);
            Assert.False(result.SuiteComplete);
            Assert.Equal(0.032768m, result.EstimatedCostUsd);
            Assert.Equal("max-declared-and-configured-descriptor-token-estimate", result.EstimatedCostSource);
            var outcome = Assert.Single(result.Probes);
            Assert.Equal(ProbeStatus.Passed.ToString(), outcome.Status);
            Assert.Equal(0.000066m, outcome.CostUsd);
            Assert.Equal(new QualificationProbeUsage(17, 4, null, null, null), outcome.Usage);

            using var store = OmniHost.CreateModelQualificationStore(directory);
            var loaded = OmniHost.LoadUserConfiguration(OmniHost.CreatePlatformPaths(directory));
            var model = loaded.Registry.Model(ModelId)!;
            var providerDescriptor = loaded.Registry.Provider(ProviderId)!;
            var key = ModelQualificationHost.QualificationKeyFor(model, providerDescriptor);
            var profile = store.Get(key, CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(ModelQualificationState.ProvisionallyClassified, profile!.State);
            Assert.Equal(1L, profile.ProfileRevision);
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
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

    // In-memory fixture has one generation response per StreamAsync invocation; this is not a billing guarantee.
    private sealed class FixtureProvider : IModelProvider, IModelRequestAttemptBound
    {
        private readonly string _answer;

        public int Calls { get; private set; }
        public long? MaximumGenerationRequestAttempts => 1;

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
                new ProviderMetadata("configured-cost-cap-fixture", ModelId, null),
                TokenUsageFields.Input | TokenUsageFields.Output));
        }
    }
}
