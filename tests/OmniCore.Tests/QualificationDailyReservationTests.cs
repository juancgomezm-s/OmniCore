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
/// Proposed offline integration coverage for qualification against the shared User daily-spend
/// reservation ledger. The scripted provider is an in-memory fixture, not a billing guarantee.
/// </summary>
public sealed class QualificationDailyReservationTests
{
    private const string ModelId = "qualification-daily-reservation-fixture";
    private const string ProviderId = "qualification-daily-reservation-provider";
    private const decimal DailyCapUsd = 0.05m;

    private static string TempDir()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-qualification-daily-reservation",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static IPlatformPaths WriteConfiguration(string dataDirectory, decimal daily = DailyCapUsd)
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
              daily: {{daily.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
            """);
        return paths;
    }

    private static Probe Probe() => new(ProbeId.WellKnown("qualification-daily-reservation-reading"),
        ProbeKind.Reading, "Reply with only the word measured.", "measured", 0m);

    private static QualificationOptions Options(IModelProvider provider, Probe probe) => new()
    {
        Suite = "quick",
        ConsentGiven = true,
        // The explicitly accepted per-suite limit is intentionally above the configured estimate.
        // The independently exhausted User daily cap is the admission condition under test.
        MaxTotalCostUsd = 1m,
        Probes = [probe],
        Provider = provider,
    };

    [Fact]
    public async Task Metered_qualification_is_blocked_before_provider_when_user_daily_reservation_is_full()
    {
        var directory = TempDir();
        try
        {
            var paths = WriteConfiguration(directory);
            var reservations = new SqliteSpendReservationStore(
                Path.Combine(paths.DataDirectory, "spend-reservations.db"));
            Assert.Equal(SqliteSpendReservationStore.Admission.Reserved,
                reservations.TryReserve("preexisting-daily-cap-fixture", DailyCapUsd,
                    () => [new SqliteSpendReservationStore.Limit("daily", "user", DailyCapUsd, 0m)]));

            var probe = Probe();
            var provider = new FixtureProvider(probe.Expected);
            using var host = ModelQualificationHost.Create(directory);
            var loaded = OmniHost.LoadUserConfiguration(paths);
            var model = loaded.Registry.Model(ModelId)!;
            var descriptor = loaded.Registry.Provider(ProviderId)!;
            Assert.Equal(BillingMode.MeteredCurrency, descriptor.BillingMode);
            Assert.True(loaded.Pricing(ModelId)!.IsComplete);

            // Deliberately don't require a particular rejection exception: the current public API
            // has no daily-cap exception. Record any rejection, then assert the actual safety
            // effects: no provider stream and no profile/evidence publication. The available-day
            // control below proves the fixture itself can reach the provider and commit.
            var rejection = await Record.ExceptionAsync(() =>
                host.QualifyAsync(ModelId, Options(provider, probe), CancellationToken.None));

            Assert.Equal(0, provider.StreamCalls);
            Assert.NotNull(rejection);
            var key = ModelQualificationHost.QualificationKeyFor(model, descriptor);
            using var store = OmniHost.CreateModelQualificationStore(directory);
            Assert.Null(store.Get(key, CancellationToken.None));
            Assert.Null(((IModelQualificationEvidenceStore)store).Evidence(key, 1,
                CancellationToken.None));
            Assert.Empty(store.List(CancellationToken.None));
        }
        finally
        {
            DeletePrivateFixture(directory);
        }
    }

    [Fact]
    public async Task Metered_qualification_runs_and_publishes_verified_evidence_when_daily_capacity_is_available()
    {
        var directory = TempDir();
        try
        {
            var paths = WriteConfiguration(directory);
            var probe = Probe();
            var provider = new FixtureProvider(probe.Expected);
            using var host = ModelQualificationHost.Create(directory);
            var result = await host.QualifyAsync(ModelId, Options(provider, probe), CancellationToken.None);

            Assert.Equal(1, provider.StreamCalls);
            Assert.Equal(ModelQualificationState.ProvisionallyClassified.ToString(), result.NewState);
            Assert.False(result.SuiteComplete);
            Assert.Equal(0.000066m, Assert.Single(result.Probes).CostUsd);

            var loaded = OmniHost.LoadUserConfiguration(paths);
            var model = loaded.Registry.Model(ModelId)!;
            var descriptor = loaded.Registry.Provider(ProviderId)!;
            var key = ModelQualificationHost.QualificationKeyFor(model, descriptor);
            using var store = OmniHost.CreateModelQualificationStore(directory);
            var profile = store.Get(key, CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(1L, profile!.ProfileRevision);
            var evidence = ((IModelQualificationEvidenceStore)store).Evidence(key,
                profile.ProfileRevision, CancellationToken.None);
            Assert.NotNull(evidence);
            Assert.Equal(1L, evidence!.SourceRunRevision);
            Assert.Equal(result.EvidenceHash, evidence.Artifact.Hash.ToString());
            Assert.True(new FileArtifactStore(paths.DataDirectory).Verify(
                evidence.Artifact.Hash, evidence.Artifact.Size));
            var receipt = Assert.Single(store.ProbeReceipts(CancellationToken.None));
            Assert.Equal(0.000066m, receipt.CostUsd);
            Assert.Equal(ProbeExecutionTermination.Completed, receipt.Termination);
            Assert.Equal(new TokenUsage(17, 4, 0, 0, 0), receipt.Usage);
            // A settled bound must not be held forever or counted as consumption twice.
            await host.QualifyAsync(ModelId, Options(provider, probe), CancellationToken.None);
            Assert.Equal(2, provider.StreamCalls);
            Assert.Equal(2, store.ProbeReceipts(CancellationToken.None).Count);
        }
        finally
        {
            DeletePrivateFixture(directory);
        }
    }

    // One deterministic response per StreamAsync; the bound is only for this test fixture.
    [Theory]
    [InlineData("excess-sends")]
    [InlineData("excess-cost")]
    public async Task Provider_contradicting_finite_bound_preserves_receipt_and_stops_before_second_probe(string mode)
    {
        var directory = TempDir();
        try
        {
            var paths = WriteConfiguration(directory, 0.08m);
            var first = Probe();
            var second = new Probe(ProbeId.WellKnown("second-reading"), ProbeKind.Reading,
                first.Prompt, first.Expected, 0m);
            var provider = new FixtureProvider(first.Expected, mode);
            var options = new QualificationOptions { ConsentGiven = true, MaxTotalCostUsd = 1m,
                Probes = [first, second], Provider = provider };
            using var host = ModelQualificationHost.Create(directory);
            await Assert.ThrowsAsync<ModelQualificationProbeBoundExceededException>(() =>
                host.QualifyAsync(ModelId, options, CancellationToken.None));
            Assert.Equal(1, provider.StreamCalls);
            using var store = OmniHost.CreateModelQualificationStore(directory);
            var receipt = Assert.Single(store.ProbeReceipts(CancellationToken.None));
            if (mode == "excess-sends") Assert.True(receipt.ObservedGenerationSends > receipt.MaximumGenerationAttempts);
            else Assert.True(receipt.CostUsd > receipt.MaximumUsd);
            Assert.True(new SqliteSpendReservationStore(Path.Combine(paths.DataDirectory, "spend-reservations.db"))
                .HasFullDispatchedBound(receipt.ReservationId, receipt.MaximumUsd));
            Assert.Empty(store.List(CancellationToken.None));
            var next = new FixtureProvider(first.Expected);
            await Assert.ThrowsAsync<ModelQualificationProbeBoundExceededException>(() =>
                host.QualifyAsync(ModelId, Options(next, first), CancellationToken.None));
            Assert.Equal(0, next.StreamCalls);
        }
        finally { DeletePrivateFixture(directory); }
    }

    [Theory]
    [InlineData("failed", ProbeExecutionTermination.Failed)]
    [InlineData("cancelled", ProbeExecutionTermination.Cancelled)]
    [InlineData("timedout", ProbeExecutionTermination.TimedOut)]
    [InlineData("unknown", ProbeExecutionTermination.Completed)]
    [InlineData("retry", ProbeExecutionTermination.Completed)]
    public async Task Partial_or_unknown_consumption_survives_without_profile_and_retains_coverage(
        string mode, ProbeExecutionTermination termination)
    {
        var directory = TempDir();
        try
        {
            var paths = WriteConfiguration(directory, mode == "retry" ? 0.09m : DailyCapUsd);
            var probe = Probe();
            using var cancellation = new CancellationTokenSource();
            var provider = new FixtureProvider(probe.Expected, mode, cancellation);
            using var host = ModelQualificationHost.Create(directory);
            var failure = await Record.ExceptionAsync(() => host.QualifyAsync(ModelId,
                Options(provider, probe), cancellation.Token));
            Assert.Equal(1, provider.StreamCalls);
            if (mode is "unknown" or "retry") Assert.Null(failure); else Assert.NotNull(failure);
            using var store = OmniHost.CreateModelQualificationStore(directory);
            var receipt = Assert.Single(store.ProbeReceipts(CancellationToken.None));
            Assert.Equal(termination, receipt.Termination);
            Assert.True(new FileArtifactStore(paths.DataDirectory).Verify(receipt.Evidence.Hash, receipt.Evidence.Size));
            if (mode == "retry")
            {
                Assert.Equal(2, receipt.ObservedGenerationSends);
                Assert.Equal(0.000066m, receipt.CostUsd);
            }
            else Assert.Null(receipt.CostUsd);
            if (mode is not ("unknown" or "retry")) Assert.Empty(store.List(CancellationToken.None));
            var next = new FixtureProvider(probe.Expected);
            await Assert.ThrowsAsync<ModelQualificationBudgetAdmissionException>(() =>
                host.QualifyAsync(ModelId, Options(next, probe), CancellationToken.None));
            Assert.Equal(0, next.StreamCalls);
            Assert.Single(store.ProbeReceipts(CancellationToken.None));
        }
        finally { DeletePrivateFixture(directory); }
    }

    [Fact]
    public async Task Host_receipt_redacts_response_before_publishing_canonical_consumption()
    {
        var directory = TempDir();
        try
        {
            var paths = WriteConfiguration(directory);
            var probe = Probe();
            const string sentinel = "fixture-only-not-a-real-key";
            var provider = new FixtureProvider("Authorization: Bearer " + sentinel);
            using var host = ModelQualificationHost.Create(directory);
            await host.QualifyAsync(ModelId, Options(provider, probe), CancellationToken.None);
            using var store = OmniHost.CreateModelQualificationStore(directory);
            var receipt = Assert.Single(store.ProbeReceipts(CancellationToken.None));
            Assert.True(receipt.Evidence.Redacted);
            Assert.DoesNotContain(sentinel, new FileArtifactStore(paths.DataDirectory).GetText(receipt.Evidence.Hash)!);
            Assert.Equal(0.000066m, receipt.CostUsd);
        }
        finally { DeletePrivateFixture(directory); }
    }

    [Fact]
    public async Task Concurrent_qualification_hosts_share_admission_before_either_response_is_persisted()
    {
        var directory = TempDir();
        try
        {
            var paths = WriteConfiguration(directory);
            var probe = Probe();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = new FixtureProvider(probe.Expected, entered: entered, release: release.Task);
            var second = new FixtureProvider(probe.Expected);
            using var hostA = ModelQualificationHost.Create(directory);
            using var hostB = ModelQualificationHost.Create(directory);
            var firstResult = hostA.QualifyAsync(ModelId, Options(first, probe), CancellationToken.None);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                await Assert.ThrowsAsync<ModelQualificationBudgetAdmissionException>(() =>
                    hostB.QualifyAsync(ModelId, Options(second, probe), CancellationToken.None));
                Assert.Equal(0, second.StreamCalls);
            }
            finally
            {
                release.TrySetResult();
                await firstResult.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
            Assert.Equal(1, first.StreamCalls);
            Assert.Single(SqliteModelQualificationStore.ReadCanonicalProbeReceipts(paths.DataDirectory, CancellationToken.None));
        }
        finally { DeletePrivateFixture(directory); }
    }

    private sealed class FixtureProvider(string answer, string mode = "normal",
        CancellationTokenSource? cancellation = null, TaskCompletionSource? entered = null,
        Task? release = null) : IModelProvider, IModelRequestAttemptBound
    {
        public int StreamCalls { get; private set; }
        public long? MaximumGenerationRequestAttempts => mode == "retry" ? 2 : 1;
        public ProviderCapabilities Capabilities => new(reportsUsage: true, reportsCost: false,
            reportsQuota: false);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StreamCalls++;
            entered?.TrySetResult();
            if (release is not null) await release.WaitAsync(cancellationToken);
            await Task.Yield();
            if (mode == "failed")
            {
                yield return new ResponseFailed("fixture-failure", "fixture provider failed");
                yield break;
            }
            if (mode == "cancelled")
            {
                cancellation!.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (mode == "timedout") throw new OperationCanceledException("fixture timeout");
            if (mode is "retry" or "excess-sends")
            {
                GenerationRequestAttemptScope.RecordGenerationSend();
                GenerationRequestAttemptScope.RecordGenerationSend();
            }
            yield return new ResponseCompleted(new ModelResponse(
                [new TextBlock(answer)], StopReason.EndTurn,
                mode == "excess-cost" ? new TokenUsage(20_000, 4, 0, 0, 0) : new TokenUsage(17, 4, 3, 2, 1), null,
                new ProviderMetadata("qualification-daily-reservation-fixture", ModelId, null),
                mode == "unknown" ? TokenUsageFields.None : TokenUsageFields.Input | TokenUsageFields.Output));
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
