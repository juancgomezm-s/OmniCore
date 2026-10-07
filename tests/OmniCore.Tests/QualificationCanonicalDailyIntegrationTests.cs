// Offline integration regression; provider usage and receipts are synthetic fixtures.
// The provider and receipt are synthetic fixtures, not invoices or real account spend.
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class QualificationCanonicalDailyIntegrationTests
{
    private const string ModelId = "qualification-canonical-daily-fixture";
    private const string ProviderId = "qualification-canonical-daily-provider";
    private const decimal DailyCapUsd = 0.05m;

    [Fact]
    public async Task Todays_canonical_user_receipt_is_counted_once_and_blocks_before_provider()
    {
        var directory = TempDir();
        try
        {
            var paths = WriteConfiguration(directory);
            var seeded = SeedReceipt(paths, DateTimeOffset.UtcNow);
            using (var store = OmniHost.CreateModelQualificationStore(directory))
                store.RecordProbeReceipt(seeded, CancellationToken.None);

            var probe = Probe();
            var provider = new FixtureProvider(probe.Expected);
            Exception? rejection;
            var loaded = OmniHost.LoadUserConfiguration(paths);
            var model = loaded.Registry.Model(ModelId)!;
            var descriptor = loaded.Registry.Provider(ProviderId)!;
            Assert.True(loaded.Pricing(ModelId)!.IsComplete);
            using (var host = ModelQualificationHost.Create(directory))
            {
                rejection = await Record.ExceptionAsync(() =>
                    host.QualifyAsync(ModelId, Options(provider, probe), CancellationToken.None));
            }

            // A prior $0.04 receipt leaves $0.01 of the $0.05 daily cap. The explicit
            // descriptor estimate for this fixture is $0.032768; the per-suite cap is $1.
            Assert.Equal(0, provider.StreamCalls);
            Assert.NotNull(rejection);

            var key = ModelQualificationHost.QualificationKeyFor(model, descriptor);
            using var reopened = OmniHost.CreateModelQualificationStore(directory);
            Assert.Null(reopened.Get(key, CancellationToken.None));
            Assert.Null(((IModelQualificationEvidenceStore)reopened).Evidence(key, 1,
                CancellationToken.None));
            Assert.Empty(reopened.List(CancellationToken.None));
            var firstRead = reopened.ProbeReceipts(CancellationToken.None);
            var secondRead = reopened.ProbeReceipts(CancellationToken.None);
            Assert.Equal(new[] { seeded }, firstRead);
            Assert.Equal(firstRead, secondRead);
            Assert.Equal(0.04m, Assert.Single(secondRead).CostUsd);
        }
        finally
        {
            DeletePrivateFixture(directory);
        }
    }

    [Fact]
    public async Task Yesterday_user_receipt_does_not_block_today_and_qualification_persists_profile()
    {
        var directory = TempDir();
        try
        {
            var paths = WriteConfiguration(directory);
            var seeded = SeedReceipt(paths, DateTimeOffset.UtcNow.AddDays(-1));
            using (var store = OmniHost.CreateModelQualificationStore(directory))
                store.RecordProbeReceipt(seeded, CancellationToken.None);

            var probe = Probe();
            var provider = new FixtureProvider(probe.Expected);
            QualificationRunResult result;
            var loaded = OmniHost.LoadUserConfiguration(paths);
            var model = loaded.Registry.Model(ModelId)!;
            var descriptor = loaded.Registry.Provider(ProviderId)!;
            Assert.Equal(BillingMode.MeteredCurrency, descriptor.BillingMode);
            Assert.True(loaded.Pricing(ModelId)!.IsComplete);
            using (var host = ModelQualificationHost.Create(directory))
                result = await host.QualifyAsync(ModelId, Options(provider, probe), CancellationToken.None);

            Assert.Equal(1, provider.StreamCalls);
            Assert.Equal(ModelQualificationState.ProvisionallyClassified.ToString(), result.NewState);
            Assert.False(result.SuiteComplete);
            Assert.Equal(0.000066m, Assert.Single(result.Probes).CostUsd);

            var key = ModelQualificationHost.QualificationKeyFor(model, descriptor);
            using var reopened = OmniHost.CreateModelQualificationStore(directory);
            var profile = reopened.Get(key, CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(1L, profile!.ProfileRevision);
            var evidence = ((IModelQualificationEvidenceStore)reopened).Evidence(key,
                profile.ProfileRevision, CancellationToken.None);
            Assert.NotNull(evidence);
            Assert.Equal(result.EvidenceHash, evidence!.Artifact.Hash.ToString());
            Assert.True(new FileArtifactStore(paths.DataDirectory).Verify(
                evidence.Artifact.Hash, evidence.Artifact.Size));
        }
        finally
        {
            DeletePrivateFixture(directory);
        }
    }

    private static string TempDir()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omni-qualification-canonical-daily",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static IPlatformPaths WriteConfiguration(string dataDirectory)
    {
        var paths = OmniHost.CreatePlatformPaths(dataDirectory);
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), $$"""
            providers:
              {{ProviderId}}:
                family: OpenAiChatCompatible
                baseUrl: https://fixture.invalid/v1
                auth: none
                billingMode: MeteredCurrency
                inputPricePerMillionUsd: 2
                outputPricePerMillionUsd: 8
            """);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "models.yaml"), $$"""
            models:
              {{ModelId}}:
                provider: {{ProviderId}}
                context: 8192
                recommendedUsableContext: 8192
                maxOutput: 2048
            """);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "settings.yaml"), $$"""
            budget:
              session: 5
              daily: {{DailyCapUsd.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
            """);
        return paths;
    }

    private static Probe Probe() => new(ProbeId.WellKnown("qualification-canonical-daily-reading"),
        ProbeKind.Reading, "Reply with only the word measured.", "measured", 0m);

    private static QualificationOptions Options(IModelProvider provider, Probe probe) => new()
    {
        Suite = "quick",
        ConsentGiven = true,
        MaxTotalCostUsd = 1m,
        Probes = [probe],
        Provider = provider,
    };

    private static QualificationProbeReceipt SeedReceipt(IPlatformPaths paths, DateTimeOffset completedAt)
    {
        completedAt = completedAt.ToUniversalTime();
        var executionId = Guid.NewGuid();
        var observation = new QualificationProbeReceipt(executionId, "prior-qualification-probe", 0,
            "qualification/" + executionId.ToString("D") + "/prior-qualification-probe",
            new string('a', 64), "quick", "1.0", new string('b', 64),
            completedAt.AddSeconds(-1), completedAt, ProbeStatus.Passed, ProbeExecutionTermination.Completed,
            BillingMode.MeteredCurrency, 0.10m, 1, 1, 0.04m,
            new TokenUsage(20_000, 0, 0, 0, 0),
            TokenUsageFields.Input | TokenUsageFields.Output, null!);
        const string syntheticOutput = "offline prior qualification fixture";
        var evidenceJson = "{\"schema\":\"" + QualificationProbeReceipt.EvidenceSchema
            + "\",\"receipt\":" + observation.CanonicalObservationJson()
            + ",\"output\":" + JsonSerializer.Serialize(syntheticOutput) + "}";
        var artifact = new FileArtifactStore(paths.DataDirectory).PutText(evidenceJson,
            "application/json", ArtifactKind.Other, Sensitivity.Sensitive);
        return observation with { Evidence = artifact };
    }

    // One deterministic response per StreamAsync; the bound is only for this fixture.
    private sealed class FixtureProvider(string answer) : IModelProvider, IModelRequestAttemptBound
    {
        public int StreamCalls { get; private set; }
        public long? MaximumGenerationRequestAttempts => 1;
        public ProviderCapabilities Capabilities => new(reportsUsage: true, reportsCost: false,
            reportsQuota: false);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StreamCalls++;
            await Task.Yield();
            yield return new ResponseCompleted(new ModelResponse(
                [new TextBlock(answer)], StopReason.EndTurn, new TokenUsage(17, 4, 0, 0, 0), null,
                new ProviderMetadata("qualification-canonical-daily-fixture", ModelId, null),
                TokenUsageFields.Input | TokenUsageFields.Output));
        }
    }

    private static void DeletePrivateFixture(string directory)
    {
        var paths = OmniHost.CreatePlatformPaths(directory);
        ClearPrivatePool(Path.Combine(paths.DataDirectory, "user.db"));
        ClearPrivatePool(Path.Combine(paths.DataDirectory, "spend-reservations.db"));
        Directory.Delete(directory, recursive: true);
    }

    private static void ClearPrivatePool(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Pooling = false,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }
}
