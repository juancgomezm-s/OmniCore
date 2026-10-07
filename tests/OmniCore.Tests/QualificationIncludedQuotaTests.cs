using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Qualification;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>
/// Offline Host-level checks for IncludedQuota qualification consent. Quota snapshots and
/// provider responses are synthetic fixtures: they do not query an account or certify billing.
/// </summary>
public sealed class QualificationIncludedQuotaTests
{
    private const string ModelId = "qualification-included-quota-fixture";
    private const string ProviderId = "qualification-included-quota-provider";

    [Fact]
    public async Task ConsentGiven_alone_does_not_authorize_a_reported_low_included_quota()
    {
        var directory = CreatePrivateDirectory();
        try
        {
            var paths = WriteConfiguration(directory);
            var low = LowQuota(5);
            var provider = ProviderFor(QuickProbes(1));
            var queriedProviderIds = new List<string>();
            using var host = ModelQualificationHost.Create(directory);
            var loaded = OmniHost.LoadUserConfiguration(paths);
            var model = Assert.IsType<ModelDefinition>(loaded.Registry.Model(ModelId));
            var descriptor = Assert.IsType<ProviderDescriptor>(loaded.Registry.Provider(ProviderId));
            var options = Options(provider, QuickProbes(1), queriedProviderIds,
                (_, _) => Task.FromResult(low));

            await Assert.ThrowsAsync<ModelQualificationConsentException>(() =>
                host.QualifyAsync(ModelId, options, CancellationToken.None));

            Assert.Equal(new[] { ProviderId }, queriedProviderIds);
            Assert.Equal(0, provider.StreamCalls);
            AssertNoProfileOrEvidence(directory, ModelQualificationHost.QualificationKeyFor(model, descriptor));
        }
        finally
        {
            DeletePrivateFixture(directory);
        }
    }

    [Fact]
    public async Task Explicit_confirmation_receives_the_exact_low_quota_snapshot_and_allows_one_probe()
    {
        var directory = CreatePrivateDirectory();
        try
        {
            var paths = WriteConfiguration(directory);
            var low = LowQuota(5);
            var probes = QuickProbes(1);
            var provider = ProviderFor(probes);
            var queriedProviderIds = new List<string>();
            var confirmationSnapshots = new List<ProviderQuotaSnapshot>();
            using var host = ModelQualificationHost.Create(directory);
            var loaded = OmniHost.LoadUserConfiguration(paths);
            var model = Assert.IsType<ModelDefinition>(loaded.Registry.Model(ModelId));
            var descriptor = Assert.IsType<ProviderDescriptor>(loaded.Registry.Provider(ProviderId));

            var result = await host.QualifyAsync(ModelId, Options(provider, probes, queriedProviderIds,
                (_, _) => Task.FromResult(low), (snapshot, _) =>
                {
                    confirmationSnapshots.Add(snapshot);
                    return Task.FromResult(true);
                }), CancellationToken.None);

            Assert.Equal(new[] { ProviderId }, queriedProviderIds);
            Assert.Equal(1, provider.StreamCalls);
            var shown = Assert.Single(confirmationSnapshots);
            Assert.Equal(low, shown);
            Assert.Equal("synthetic-included-quota-account", shown.AccountId);
            Assert.Equal("private-fixture-quota", shown.Source);
            Assert.Equal(MetricAvailability.Reported, shown.Availability);
            var window = Assert.Single(shown.Windows);
            Assert.Equal("fixture-five-hour-window", window.Id);
            Assert.Equal(5d, window.RemainingPercent.Value);
            Assert.True(window.ResetsAt > DateTimeOffset.UtcNow);
            Assert.Equal("ProvisionallyClassified", result.NewState);
            Assert.False(result.SuiteComplete);

            using var store = OmniHost.CreateModelQualificationStore(directory);
            var profile = Assert.IsType<ModelQualificationProfile>(store.Get(
                ModelQualificationHost.QualificationKeyFor(model, descriptor), CancellationToken.None));
            Assert.Equal(ModelQualificationState.ProvisionallyClassified, profile.State);
            var evidence = Assert.IsType<ModelQualificationEvidence>(
                store.Evidence(ModelQualificationHost.QualificationKeyFor(model, descriptor),
                    profile.ProfileRevision, CancellationToken.None));
            Assert.Equal(result.EvidenceHash, evidence.Artifact.Hash.ToString());
        }
        finally
        {
            DeletePrivateFixture(directory);
        }
    }

    [Fact]
    public async Task Declining_low_quota_confirmation_stops_before_provider_and_persistence()
    {
        var directory = CreatePrivateDirectory();
        try
        {
            var paths = WriteConfiguration(directory);
            var low = LowQuota(5);
            var probes = QuickProbes(1);
            var provider = ProviderFor(probes);
            var queriedProviderIds = new List<string>();
            var shown = new List<ProviderQuotaSnapshot>();
            using var host = ModelQualificationHost.Create(directory);
            var loaded = OmniHost.LoadUserConfiguration(paths);
            var model = loaded.Registry.Model(ModelId)!;
            var descriptor = loaded.Registry.Provider(ProviderId)!;

            await Assert.ThrowsAsync<ModelQualificationConsentException>(() => host.QualifyAsync(ModelId,
                Options(provider, probes, queriedProviderIds, (_, _) => Task.FromResult(low), (snapshot, _) =>
                {
                    shown.Add(snapshot);
                    return Task.FromResult(false);
                }), CancellationToken.None));

            Assert.Equal(new[] { ProviderId }, queriedProviderIds);
            Assert.Equal(low, Assert.Single(shown));
            Assert.Equal(0, provider.StreamCalls);
            AssertNoProfileOrEvidence(directory, ModelQualificationHost.QualificationKeyFor(model, descriptor));
        }
        finally
        {
            DeletePrivateFixture(directory);
        }
    }

    [Theory]
    [MemberData(nameof(NonPromptingQuotaSnapshots))]
    public async Task Ten_percent_unknown_null_or_expired_quota_does_not_prompt_or_become_zero(
        ProviderQuotaSnapshot quota)
    {
        var directory = CreatePrivateDirectory();
        try
        {
            var paths = WriteConfiguration(directory);
            var probes = QuickProbes(1);
            var provider = ProviderFor(probes);
            var queriedProviderIds = new List<string>();
            var confirmationCalls = 0;
            using var host = ModelQualificationHost.Create(directory);
            var loaded = OmniHost.LoadUserConfiguration(paths);
            var model = loaded.Registry.Model(ModelId)!;
            var descriptor = loaded.Registry.Provider(ProviderId)!;

            var result = await host.QualifyAsync(ModelId, Options(provider, probes, queriedProviderIds,
                (_, _) => Task.FromResult(quota), (_, _) =>
                {
                    confirmationCalls++;
                    return Task.FromResult(false);
                }), CancellationToken.None);

            Assert.Equal(new[] { ProviderId }, queriedProviderIds);
            Assert.Equal(0, confirmationCalls);
            Assert.Equal(1, provider.StreamCalls);
            Assert.Equal("ProvisionallyClassified", result.NewState);
            Assert.False(result.SuiteComplete);
            AssertNoArtificialQuotaValue(quota);
            AssertProfileAndEvidence(directory, ModelQualificationHost.QualificationKeyFor(model, descriptor), result);
        }
        finally
        {
            DeletePrivateFixture(directory);
        }
    }

    [Fact]
    public async Task Fresh_low_quota_is_rechecked_between_probes_and_a_second_decline_sends_nothing_more()
    {
        var directory = CreatePrivateDirectory();
        try
        {
            var paths = WriteConfiguration(directory);
            var probes = QuickProbes(2);
            var provider = ProviderFor(probes);
            var queryIndex = 0;
            var queriedProviderIds = new List<string>();
            var snapshots = new[] { LowQuota(5), LowQuota(4) };
            var confirmations = new List<ProviderQuotaSnapshot>();
            using var host = ModelQualificationHost.Create(directory);
            var loaded = OmniHost.LoadUserConfiguration(paths);
            var model = loaded.Registry.Model(ModelId)!;
            var descriptor = loaded.Registry.Provider(ProviderId)!;

            await Assert.ThrowsAsync<ModelQualificationConsentException>(() => host.QualifyAsync(ModelId,
                Options(provider, probes, queriedProviderIds, (providerId, _) =>
                {
                    var index = Math.Min(queryIndex++, snapshots.Length - 1);
                    return Task.FromResult(snapshots[index]);
                }, (snapshot, _) =>
                {
                    confirmations.Add(snapshot);
                    return Task.FromResult(confirmations.Count == 1);
                }), CancellationToken.None));

            Assert.Equal(new[] { ProviderId, ProviderId }, queriedProviderIds);
            Assert.Equal(2, confirmations.Count);
            Assert.Equal(snapshots[0], confirmations[0]);
            Assert.Equal(snapshots[1], confirmations[1]);
            Assert.Equal(1, provider.StreamCalls);
            AssertNoProfileOrEvidence(directory, ModelQualificationHost.QualificationKeyFor(model, descriptor));
            // Reopen the canonical User store after interruption: profile publication
            // is independent from consumption already observed at the first probe.
            using var reopened = OmniHost.CreateModelQualificationStore(directory);
            var receipt = Assert.Single(reopened.ProbeReceipts(CancellationToken.None));
            Assert.Equal(probes[0].Id.ToString(), receipt.ProbeId);
            Assert.Equal(BillingMode.IncludedQuota, receipt.BillingMode);
            Assert.Equal(ProbeExecutionTermination.Completed, receipt.Termination);
            Assert.Equal(17, receipt.Usage!.Input);
            Assert.Equal(4, receipt.Usage.Output);
            Assert.Equal(TokenUsageFields.Input | TokenUsageFields.Output, receipt.ReportedUsageFields);
            Assert.Equal(1, receipt.ObservedGenerationSends);
            Assert.Null(receipt.CostUsd);
            Assert.Equal(receipt, Assert.Single(reopened.ProbeReceipts(CancellationToken.None)));
            reopened.RecordProbeReceipt(receipt, CancellationToken.None);
            Assert.Equal(receipt, Assert.Single(reopened.ProbeReceipts(CancellationToken.None)));
        }
        finally
        {
            DeletePrivateFixture(directory);
        }
    }

    public static IEnumerable<object[]> NonPromptingQuotaSnapshots()
    {
        var now = DateTimeOffset.UtcNow;
        yield return [Quota(now, MetricAvailability.Reported, 10, now.AddHours(1))];
        yield return [Quota(now, MetricAvailability.Unknown, null, now.AddHours(1))];
        yield return [Quota(now, MetricAvailability.Reported, null, now.AddHours(1))];
        yield return [Quota(now, MetricAvailability.Reported, 5, now.AddSeconds(-1))];
        yield return [Quota(now.AddHours(1), MetricAvailability.Reported, 5, now.AddHours(2))];
        yield return [Quota(now, MetricAvailability.Reported, -1, now.AddHours(1))];
        yield return [Quota(now, MetricAvailability.Reported, double.NaN, now.AddHours(1))];
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("invalid-usage")]
    [InlineData("excess-sends")]
    public async Task Interrupted_or_invalid_probe_keeps_durable_evidence_without_inventing_cost(string scenario)
    {
        var directory = CreatePrivateDirectory();
        try
        {
            var paths = WriteConfiguration(directory);
            var probes = QuickProbes(2);
            using var cancellation = new CancellationTokenSource();
            var provider = new FixtureProvider(probes.Select(p => p.Expected).ToArray(),
                scenario == "cancel" ? cancellation : null, scenario == "invalid-usage", scenario == "excess-sends" ? 2 : 1);
            using var host = ModelQualificationHost.Create(directory);
            var options = Options(provider, probes, [], (_, _) => Task.FromResult(LowQuota(50)));
            if (scenario == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.QualifyAsync(ModelId, options, cancellation.Token));
            else if (scenario == "excess-sends")
                await Assert.ThrowsAsync<ModelQualificationProbeBoundExceededException>(() => host.QualifyAsync(ModelId, options, cancellation.Token));
            else
                await Assert.ThrowsAsync<ModelQualificationSuiteIncompleteException>(() => host.QualifyAsync(ModelId, options, cancellation.Token));
            using var reopened = OmniHost.CreateModelQualificationStore(directory);
            var receipts = reopened.ProbeReceipts(CancellationToken.None);
            Assert.Equal(scenario == "invalid-usage" ? 2 : 1, receipts.Count);
            Assert.All(receipts, receipt =>
            {
                Assert.Null(receipt.CostUsd);
                Assert.Equal(BillingMode.IncludedQuota, receipt.BillingMode);
                Assert.Equal(0m, receipt.MaximumUsd); // No monetary authority, NOT measured zero cost.
                Assert.Equal(1, receipt.MaximumGenerationAttempts);
                Assert.Equal(scenario == "excess-sends" ? 2 : 1, receipt.ObservedGenerationSends);
                var evidence = new FileArtifactStore(paths.DataDirectory).GetText(receipt.Evidence.Hash);
                Assert.NotNull(evidence);
                if (scenario == "invalid-usage")
                {
                    Assert.Null(receipt.Usage);
                    Assert.Equal(TokenUsageFields.None, receipt.ReportedUsageFields);
                    Assert.Contains("invalidReportedUsage", evidence);
                }
                else Assert.Equal(17, receipt.Usage!.Input);
            });
            Assert.Equal(scenario == "cancel" ? ProbeExecutionTermination.Cancelled :
                scenario == "invalid-usage" ? ProbeExecutionTermination.Failed : ProbeExecutionTermination.Completed,
                receipts[0].Termination);
            var loaded = OmniHost.LoadUserConfiguration(paths);
            AssertNoProfileOrEvidence(directory, ModelQualificationHost.QualificationKeyFor(
                loaded.Registry.Model(ModelId)!, loaded.Registry.Provider(ProviderId)!));
        }
        finally { DeletePrivateFixture(directory); }
    }

    [Theory]
    [InlineData("wrong-provider")]
    [InlineData("query-failed")]
    [InlineData("cancel-during-confirmation")]
    [InlineData("cancel-during-query")]
    public async Task Invalid_origin_query_failure_or_cancelled_consent_never_dispatches(string scenario)
    {
        var directory = CreatePrivateDirectory();
        try
        {
            var paths = WriteConfiguration(directory);
            var probes = QuickProbes(1);
            var provider = ProviderFor(probes);
            using var cancellation = new CancellationTokenSource();
            using var host = ModelQualificationHost.Create(directory);
            var configuration = OmniHost.LoadUserConfiguration(paths);
            var model = configuration.Registry.Model(ModelId)!;
            var descriptor = configuration.Registry.Provider(ProviderId)!;
            var confirmations = 0;
            var options = Options(provider, probes, [], (_, _) =>
            {
                if (scenario == "query-failed") throw new IOException("offline quota fixture unavailable");
                if (scenario == "cancel-during-query") cancellation.Cancel();
                return Task.FromResult(scenario == "wrong-provider"
                    ? LowQuota(5) with { ProviderId = "foreign-provider" } : LowQuota(5));
            }, (_, _) =>
            {
                confirmations++;
                cancellation.Cancel();
                return Task.FromResult(true);
            });
            if (scenario is "cancel-during-confirmation" or "cancel-during-query")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    host.QualifyAsync(ModelId, options, cancellation.Token));
            else if (scenario == "wrong-provider")
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    host.QualifyAsync(ModelId, options, cancellation.Token));
            else
                await Assert.ThrowsAsync<IOException>(() =>
                    host.QualifyAsync(ModelId, options, cancellation.Token));
            Assert.Equal(scenario == "cancel-during-confirmation" ? 1 : 0, confirmations);
            Assert.Equal(0, provider.StreamCalls);
            AssertNoProfileOrEvidence(directory, ModelQualificationHost.QualificationKeyFor(model, descriptor));
        }
        finally { DeletePrivateFixture(directory); }
    }

    private static QualificationOptions Options(IModelProvider provider, IReadOnlyList<Probe> probes,
        List<string> queriedProviderIds,
        Func<string, CancellationToken, Task<ProviderQuotaSnapshot>> queryQuota,
        Func<ProviderQuotaSnapshot, CancellationToken, Task<bool>>? confirmLowQuota = null) => new()
    {
        Suite = "quick",
        ConsentGiven = true,
        MaxTotalCostUsd = 1m,
        Probes = probes,
        Provider = provider,
        QueryQuota = (providerId, token) =>
        {
            queriedProviderIds.Add(providerId);
            return queryQuota(providerId, token);
        },
        ConfirmLowQuota = confirmLowQuota,
    };

    [Fact]
    public async Task Input_only_usage_does_not_become_measured_zero_output_after_reopen()
    {
        var directory = CreatePrivateDirectory();
        try
        {
            WriteConfiguration(directory);
            var probes = QuickProbes(1);
            var provider = new FixtureProvider(probes.Select(p => p.Expected).ToArray(), inputOnly: true);
            using var host = ModelQualificationHost.Create(directory);
            await host.QualifyAsync(ModelId, Options(provider, probes, [],
                (_, _) => Task.FromResult(LowQuota(50))), CancellationToken.None);
            using var reopened = OmniHost.CreateModelQualificationStore(directory);
            var receipt = Assert.Single(reopened.ProbeReceipts(CancellationToken.None));
            Assert.Equal(TokenUsageFields.Input, receipt.ReportedUsageFields);
            Assert.Equal(17, receipt.Usage!.Input);
            Assert.Null(receipt.CostUsd);
            using var json = System.Text.Json.JsonDocument.Parse(receipt.CanonicalObservationJson());
            var usage = json.RootElement.GetProperty("usage");
            Assert.Equal(System.Text.Json.JsonValueKind.Null, usage.GetProperty("output").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, usage.GetProperty("cacheRead").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, usage.GetProperty("cacheWrite").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, usage.GetProperty("reasoning").ValueKind);
        }
        finally { DeletePrivateFixture(directory); }
    }

    private static Probe[] QuickProbes(int count) => QuickProbeSuite.Probes().Take(count).ToArray();

    private static FixtureProvider ProviderFor(IReadOnlyList<Probe> probes) =>
        new(probes.Select(probe => probe.Expected).ToArray());

    private static ProviderQuotaSnapshot LowQuota(double remainingPercent)
    {
        var now = DateTimeOffset.UtcNow;
        return Quota(now, MetricAvailability.Reported, remainingPercent, now.AddHours(2));
    }

    private static ProviderQuotaSnapshot Quota(DateTimeOffset asOf, MetricAvailability availability,
        double? remainingPercent, DateTimeOffset reset) => new(ProviderId,
        "synthetic-included-quota-account", "private-fixture-quota", asOf, availability,
        [new ProviderUsageWindow("fixture-five-hour-window", "fixture-limit", 300,
            new Metric<double?>(availability, remainingPercent is { } remaining ? 100d - remaining : null,
                "synthetic-fixture", asOf),
            new Metric<double?>(availability, remainingPercent, "synthetic-fixture", asOf),
            reset, "fixture reset")], [], "synthetic quota only");

    private static IPlatformPaths WriteConfiguration(string dataDirectory)
    {
        var paths = OmniHost.CreatePlatformPaths(dataDirectory);
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), $$"""
            providers:
              {{ProviderId}}:
                family: OpenAiChatCompatible
                baseUrl: https://quota-fixture.invalid/v1
                auth: none
                billingMode: IncludedQuota
            """);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "models.yaml"), $$"""
            models:
              {{ModelId}}:
                provider: {{ProviderId}}
                context: 8192
                recommendedUsableContext: 8192
                maxOutput: 2048
            """);
        return paths;
    }

    private static void AssertNoArtificialQuotaValue(ProviderQuotaSnapshot input)
    {
        var window = Assert.Single(input.Windows);
        if (input.Availability == MetricAvailability.Unknown)
            Assert.Equal(MetricAvailability.Unknown, window.RemainingPercent.Availability);
        if (window.RemainingPercent.Value is null)
            Assert.Null(window.RemainingPercent.Value);
        else
            Assert.NotEqual(0d, window.RemainingPercent.Value);
    }

    private static void AssertProfileAndEvidence(string directory, ModelQualificationKey key,
        QualificationRunResult result)
    {
        using var store = OmniHost.CreateModelQualificationStore(directory);
        var profile = Assert.IsType<ModelQualificationProfile>(store.Get(key, CancellationToken.None));
        Assert.Equal(ModelQualificationState.ProvisionallyClassified, profile.State);
        var evidence = Assert.IsType<ModelQualificationEvidence>(
            store.Evidence(key, profile.ProfileRevision, CancellationToken.None));
        Assert.Equal(result.EvidenceHash, evidence.Artifact.Hash.ToString());
    }

    private static void AssertNoProfileOrEvidence(string directory, ModelQualificationKey key)
    {
        using var store = OmniHost.CreateModelQualificationStore(directory);
        Assert.Null(store.Get(key, CancellationToken.None));
        Assert.Null(store.Evidence(key, 1, CancellationToken.None));
        Assert.Empty(store.List(CancellationToken.None));
    }

    private static string CreatePrivateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-included-quota-qualification-",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeletePrivateFixture(string directory)
    {
        var paths = OmniHost.CreatePlatformPaths(directory);
        ClearPrivatePool(Path.Combine(paths.DataDirectory, "user.db"));
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

    private sealed class FixtureProvider(IReadOnlyList<string> answers, CancellationTokenSource? cancelAfterResponse = null,
        bool invalidUsage = false, int sends = 1, bool inputOnly = false) : IModelProvider, IModelRequestAttemptBound
    {
        private int _streamCalls;
        public int StreamCalls => Volatile.Read(ref _streamCalls);
        public long? MaximumGenerationRequestAttempts => 1;
        public ProviderCapabilities Capabilities => new(reportsUsage: true, reportsCost: false,
            reportsQuota: false);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _streamCalls) - 1;
            for (var send = 0; send < sends; send++)
                GenerationRequestAttemptScope.RecordGenerationSend(); // Synthetic fixture, not account consumption.
            if ((uint)index >= (uint)answers.Count)
                throw new InvalidOperationException("The fixture received more model invocations than expected.");
            await Task.Yield();
            yield return new ResponseCompleted(new ModelResponse([new TextBlock(answers[index])],
                StopReason.EndTurn, new TokenUsage(invalidUsage ? -1 : 17, inputOnly ? 0 : 4, 0, 0, 0), null,
                new ProviderMetadata("included-quota-fixture", ModelId, null),
                inputOnly ? TokenUsageFields.Input : TokenUsageFields.Input | TokenUsageFields.Output));
            if (cancelAfterResponse is not null)
            {
                cancelAfterResponse.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }
}
