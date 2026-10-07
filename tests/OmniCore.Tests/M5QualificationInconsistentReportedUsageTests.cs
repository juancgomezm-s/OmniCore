using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Qualification;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>
/// Proposed offline Host contracts for contradictory reported usage detail. The provider is an
/// injected fixture with a known single generation attempt; configuration is explicitly metered
/// with complete prices only to exercise configured Host wiring. This is not a real provider call,
/// invoice, or assertion about a billed amount. Expected validation uses the existing
/// ModelQualificationSuiteIncompleteException; no new public exception is proposed.
/// </summary>
public sealed class M5QualificationInconsistentReportedUsageTests
{
    private const string ModelId = "m5-inconsistent-usage-model";
    private const string ProviderId = "m5-inconsistent-usage-provider";

    [Theory]
    [InlineData(17, 4, 18, 0, 1, TokenUsageFields.Input | TokenUsageFields.Output | TokenUsageFields.CacheRead)]
    [InlineData(17, 4, 0, 18, 1, TokenUsageFields.Input | TokenUsageFields.Output | TokenUsageFields.CacheWrite)]
    [InlineData(17, 4, 3, 2, 5, TokenUsageFields.Input | TokenUsageFields.Output | TokenUsageFields.Reasoning)]
    public async Task Reported_subcounter_exceeding_its_reported_aggregate_rejects_without_persisting(
        long input, long output, long cacheRead, long cacheWrite, long reasoning,
        TokenUsageFields reportedFields)
    {
        using var fixture = new Fixture();
        var provider = new FixtureProvider(fixture.Probe.Expected,
            new TokenUsage(input, output, cacheRead, cacheWrite, reasoning), reportedFields);
        using var host = ModelQualificationHost.Create(fixture.DirectoryPath);

        await Assert.ThrowsAsync<ModelQualificationSuiteIncompleteException>(() =>
            host.QualifyAsync(ModelId, fixture.Options(provider), CancellationToken.None));

        Assert.Equal(1, provider.Calls);
        using var store = OmniHost.CreateModelQualificationStore(fixture.DirectoryPath);
        Assert.Empty(store.List(CancellationToken.None));
        var receipt = Assert.Single(store.ProbeReceipts(CancellationToken.None));
        Assert.Null(receipt.CostUsd);
        Assert.Null(receipt.Usage);
        Assert.Equal(TokenUsageFields.None, receipt.ReportedUsageFields);
        Assert.True(new SqliteSpendReservationStore(Path.Combine(fixture.DirectoryPath, "spend-reservations.db"))
            .HasFullDispatchedBound(receipt.ReservationId, receipt.MaximumUsd));
        using var evidence = JsonDocument.Parse(new FileArtifactStore(fixture.DirectoryPath).GetText(receipt.Evidence.Hash)!);
        Assert.True(evidence.RootElement.TryGetProperty("invalidReportedUsage", out _));
    }

    [Fact]
    public async Task Unreported_detail_slots_remain_unknown_not_zero_or_evidence()
    {
        using var fixture = new Fixture();
        var provider = new FixtureProvider(fixture.Probe.Expected,
            new TokenUsage(17, 4, 99, 88, 77), TokenUsageFields.Input | TokenUsageFields.Output);
        QualificationRunResult result;
        using (var host = ModelQualificationHost.Create(fixture.DirectoryPath))
        {
            result = await host.QualifyAsync(ModelId, fixture.Options(provider), CancellationToken.None);
        }

        Assert.Equal(1, provider.Calls);
        Assert.Equal(ModelQualificationState.ProvisionallyClassified.ToString(), result.NewState);
        Assert.False(result.SuiteComplete);
        var outcome = Assert.Single(result.Probes);
        Assert.Equal(new QualificationProbeUsage(17, 4, null, null, null), outcome.Usage);

        var hash = ContentHash.Sha256(result.EvidenceHash!["sha256:".Length..]);
        using var json = JsonDocument.Parse(new FileArtifactStore(fixture.DirectoryPath).GetText(hash)!);
        var evidenceProbe = Assert.Single(json.RootElement.GetProperty("probes").EnumerateArray());
        Assert.Equal((int)(TokenUsageFields.Input | TokenUsageFields.Output),
            evidenceProbe.GetProperty("reportedUsageFields").GetInt32());
        var usage = evidenceProbe.GetProperty("usage");
        Assert.Equal(17, usage.GetProperty("input").GetInt64());
        Assert.Equal(4, usage.GetProperty("output").GetInt64());
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("cacheRead").ValueKind);
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("cacheWrite").ValueKind);
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("reasoning").ValueKind);

        using var store = OmniHost.CreateModelQualificationStore(fixture.DirectoryPath);
        var profile = Assert.Single(store.List(CancellationToken.None));
        Assert.Equal(ModelQualificationState.ProvisionallyClassified, profile.State);
    }

    [Fact]
    public async Task Coherent_reported_subcounters_preserve_normal_provisional_classification()
    {
        using var fixture = new Fixture();
        var fields = TokenUsageFields.Input | TokenUsageFields.Output | TokenUsageFields.CacheRead
            | TokenUsageFields.CacheWrite | TokenUsageFields.Reasoning;
        var provider = new FixtureProvider(fixture.Probe.Expected,
            new TokenUsage(17, 4, 3, 2, 1), fields);
        QualificationRunResult result;
        using (var host = ModelQualificationHost.Create(fixture.DirectoryPath))
        {
            result = await host.QualifyAsync(ModelId, fixture.Options(provider), CancellationToken.None);
        }

        Assert.Equal(1, provider.Calls);
        Assert.Equal(ModelQualificationState.ProvisionallyClassified.ToString(), result.NewState);
        Assert.False(result.SuiteComplete);
        var outcome = Assert.Single(result.Probes);
        Assert.Equal(0.000066m, outcome.CostUsd);
        Assert.Equal(new QualificationProbeUsage(17, 4, 3, 2, 1), outcome.Usage);
        using var store = OmniHost.CreateModelQualificationStore(fixture.DirectoryPath);
        Assert.Equal(ModelQualificationState.ProvisionallyClassified,
            Assert.Single(store.List(CancellationToken.None)).State);
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(),
            "omnicore-m5-inconsistent-usage-" + Guid.NewGuid().ToString("N"));

        public Probe Probe { get; } = new(ProbeId.WellKnown("m5-inconsistent-usage-reading"),
            ProbeKind.Reading, "Reply with only the word measured.", "measured", 0m);

        public Fixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            var paths = OmniHost.CreatePlatformPaths(DirectoryPath);
            Directory.CreateDirectory(paths.ConfigDirectory);
            File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), $$"""
                providers:
                  {{ProviderId}}:
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
        }

        public QualificationOptions Options(IModelProvider provider) => new()
        {
            Suite = "quick",
            ConsentGiven = true,
            MaxTotalCostUsd = 1m,
            Probes = [Probe],
            Provider = provider,
        };

        public void Dispose()
        {
            var databasePath = Path.GetFullPath(Path.Combine(DirectoryPath, "user.db"));
            using (var connection = new SqliteConnection("DataSource=" + databasePath))
            {
                SqliteConnection.ClearPool(connection);
            }

            Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private sealed class FixtureProvider : IModelProvider, IModelRequestAttemptBound
    {
        private readonly string _answer;
        private readonly TokenUsage _usage;
        private readonly TokenUsageFields _reportedFields;

        public int Calls { get; private set; }
        public long? MaximumGenerationRequestAttempts => 1;
        public ProviderCapabilities Capabilities => new(reportsUsage: true, reportsCost: false,
            reportsQuota: false);

        public FixtureProvider(string answer, TokenUsage usage, TokenUsageFields reportedFields)
        {
            _answer = answer;
            _usage = usage;
            _reportedFields = reportedFields;
        }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            await Task.Yield();
            yield return new ResponseCompleted(new ModelResponse(
                [new TextBlock(_answer)], StopReason.EndTurn, _usage, null,
                new ProviderMetadata("m5-inconsistent-usage-fixture", ModelId, null), _reportedFields));
        }
    }
}
