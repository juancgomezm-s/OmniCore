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
/// Fail-closed coverage for unknown generation-attempt bounds. All providers here are
/// explicit in-memory fixtures; no credentials, remote endpoint, or real billing is involved.
/// </summary>
public sealed class M5QualificationUnknownAttemptBoundTests
{
    private const string ModelId = "m5-unknown-attempt-bound-model";
    private const string ProviderId = "m5-unknown-attempt-bound-provider";

    [Theory]
    [InlineData("Unknown", "null")]
    [InlineData("Unknown", "zero")]
    [InlineData("Unknown", "negative")]
    [InlineData("Unknown", "throws")]
    [InlineData("MeteredCurrency", "null")]
    [InlineData("MeteredCurrency", "zero")]
    [InlineData("MeteredCurrency", "negative")]
    [InlineData("MeteredCurrency", "throws")]
    [InlineData("IncludedQuota", "null")]
    public async Task Unknown_or_invalid_bound_fails_closed_before_stream_or_persistence(
        string billingMode, string boundMode)
    {
        var directory = TempDir();
        try
        {
            WriteConfiguration(directory, billingMode);
            var provider = new BoundedFixtureProvider(boundMode);
            using var host = ModelQualificationHost.Create(directory);

            await Assert.ThrowsAsync<ModelQualificationCostEvidenceUnavailableException>(() =>
                host.QualifyAsync(ModelId, Options(provider, 1m), CancellationToken.None));

            Assert.Equal(0, provider.Calls);
            AssertNoQualificationArtifacts(directory);
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public async Task Provider_without_capability_fails_closed_without_wrapper()
    {
        var directory = TempDir();
        try
        {
            WriteConfiguration(directory, "Unknown");
            var provider = new UnboundedFixtureProvider();
            using var host = ModelQualificationHost.Create(directory);
            await Assert.ThrowsAsync<ModelQualificationCostEvidenceUnavailableException>(() =>
                host.QualifyAsync(ModelId, Options(provider, 1m), CancellationToken.None));
            Assert.Equal(0, provider.Calls);
            AssertNoQualificationArtifacts(directory);
        }
        finally { DeleteFixtureDirectory(directory); }
    }

    [Fact]
    public async Task Explicit_local_provider_can_keep_unknown_attempt_bound_as_null_in_evidence()
    {
        var directory = TempDir();
        try
        {
            WriteConfiguration(directory, "Local");
            var provider = new UnboundedFixtureProvider();
            using var host = ModelQualificationHost.Create(directory);
            var result = await host.QualifyAsync(ModelId, Options(provider, 1m), CancellationToken.None);
            Assert.Equal(1, provider.Calls);
            Assert.False(result.SuiteComplete);
            var hash = ContentHash.Sha256(result.EvidenceHash!["sha256:".Length..]);
            using var evidence = JsonDocument.Parse(new FileArtifactStore(directory).GetText(hash)!);
            Assert.Equal(JsonValueKind.Null,
                evidence.RootElement.GetProperty("maximumGenerationRequestAttempts").ValueKind);
            Assert.Equal("unavailable",
                evidence.RootElement.GetProperty("generationAttemptBoundSource").GetString());
        }
        finally { DeleteFixtureDirectory(directory); }
    }

    [Fact]
    public async Task Telemetry_wrapper_preserves_unknown_bound_instead_of_claiming_one_attempt()
    {
        var directory = TempDir();
        try
        {
            WriteConfiguration(directory, "MeteredCurrency");
            var inner = new UnboundedFixtureProvider();
            var wrapper = new TelemetryObservingModelProvider(inner);
            using var host = ModelQualificationHost.Create(directory);

            await Assert.ThrowsAsync<ModelQualificationCostEvidenceUnavailableException>(() =>
                host.QualifyAsync(ModelId, Options(wrapper, 1m), CancellationToken.None));

            Assert.Equal(0, inner.Calls);
            AssertNoQualificationArtifacts(directory);
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public async Task Telemetry_wrapper_propagates_known_three_attempt_bound_for_preflight()
    {
        var directory = TempDir();
        try
        {
            WriteConfiguration(directory, "MeteredCurrency");
            var inner = new BoundedFixtureProvider(3L);
            var wrapper = new TelemetryObservingModelProvider(inner);
            using var host = ModelQualificationHost.Create(directory);

            var exception = await Assert.ThrowsAsync<ModelQualificationCostCapException>(() =>
                host.QualifyAsync(ModelId, Options(wrapper, 0.05m), CancellationToken.None));

            Assert.Equal(0.05m, exception.CapUsd);
            Assert.Equal(0.098304m, exception.EstimatedUsd);
            Assert.Equal(0, inner.Calls);
            AssertNoQualificationArtifacts(directory);
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public async Task Explicit_single_attempt_fixture_keeps_success_usage_without_inventing_zero_cost()
    {
        var directory = TempDir();
        try
        {
            WriteConfiguration(directory, "Unknown");
            var provider = new BoundedFixtureProvider(1L);
            using var host = ModelQualificationHost.Create(directory);

            var result = await host.QualifyAsync(ModelId, Options(provider, 0.05m), CancellationToken.None);

            Assert.Equal(1, provider.Calls);
            Assert.Equal(ModelQualificationState.ProvisionallyClassified.ToString(), result.NewState);
            Assert.False(result.SuiteComplete);
            Assert.Equal(0.032768m, result.EstimatedCostUsd);
            var outcome = Assert.Single(result.Probes);
            Assert.Equal(ProbeStatus.Passed.ToString(), outcome.Status);
            Assert.Equal(0.000066m, outcome.CostUsd);
            Assert.Equal(new QualificationProbeUsage(17, 4, null, null, null), outcome.Usage);

            var key = Key(directory);
            using var store = OmniHost.CreateModelQualificationStore(directory);
            Assert.Equal(ModelQualificationState.ProvisionallyClassified,
                store.Get(key, CancellationToken.None)!.State);
            Assert.NotNull(store.Evidence(key, 1, CancellationToken.None));
            Assert.Equal(1, provider.BoundReads);
            var hash = ContentHash.Sha256(result.EvidenceHash!["sha256:".Length..]);
            using var evidence = JsonDocument.Parse(new FileArtifactStore(directory).GetText(hash)!);
            Assert.Equal(1, evidence.RootElement.GetProperty("maximumGenerationRequestAttempts").GetInt64());
            Assert.Equal("injected-provider-capability",
                evidence.RootElement.GetProperty("generationAttemptBoundSource").GetString());
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    private static QualificationOptions Options(IModelProvider provider, decimal cap) => new()
    {
        Suite = "quick",
        ConsentGiven = true,
        MaxTotalCostUsd = cap,
        Probes = [new Probe(ProbeId.WellKnown("unknown-attempt-bound-probe"), ProbeKind.Reading,
            "Reply with only the word measured.", "measured", 0m)],
        Provider = provider,
    };

    private static string TempDir()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-m5-unknown-attempt-bound",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteConfiguration(string directory, string billingMode)
    {
        var paths = OmniHost.CreatePlatformPaths(directory);
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), $$"""
            providers:
              {{ProviderId}}:
                family: OpenAiChatCompatible
                baseUrl: https://fixture.invalid/v1
                auth: none
                billingMode: {{billingMode}}
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

    private static ModelQualificationKey Key(string directory)
    {
        var loaded = OmniHost.LoadUserConfiguration(OmniHost.CreatePlatformPaths(directory));
        return ModelQualificationHost.QualificationKeyFor(loaded.Registry.Model(ModelId)!,
            loaded.Registry.Provider(ProviderId));
    }

    private static void AssertNoQualificationArtifacts(string directory)
    {
        var key = Key(directory);
        using var store = OmniHost.CreateModelQualificationStore(directory);
        Assert.Empty(store.List(CancellationToken.None));
        Assert.Null(store.Get(key, CancellationToken.None));
        Assert.Empty(store.Traits(key, 1, CancellationToken.None));
        Assert.Null(store.Evidence(key, 1, CancellationToken.None));
    }

    private static void DeleteFixtureDirectory(string directory)
    {
        var databasePath = Path.GetFullPath(Path.Combine(directory, "user.db"));
        using (var connection = new SqliteConnection("DataSource=" + databasePath))
            SqliteConnection.ClearPool(connection);
        Directory.Delete(directory, recursive: true);
    }

    private sealed class BoundedFixtureProvider : IModelProvider, IModelRequestAttemptBound
    {
        private readonly string? _boundMode;
        private readonly long? _bound;
        private int _calls;
        public int BoundReads { get; private set; }

        public BoundedFixtureProvider(long? bound) => _bound = bound;
        public BoundedFixtureProvider(string boundMode) => _boundMode = boundMode;
        public int Calls => Volatile.Read(ref _calls);

        public long? MaximumGenerationRequestAttempts
        {
            get
            {
                BoundReads++;
                if (Calls > 0) throw new InvalidOperationException("bound must not be reread after invocation");
                return _boundMode switch
                {
            "null" => null,
            "zero" => 0,
            "negative" => -1,
            "throws" => throw new InvalidOperationException("fixture cannot establish an attempt bound"),
            null => _bound,
            _ => throw new InvalidOperationException("unknown fixture bound mode"),
                };
            }
        }

        public ProviderCapabilities Capabilities => new(true, false, false);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            await Task.Yield();
            yield return new ResponseCompleted(new ModelResponse([new TextBlock("measured")],
                StopReason.EndTurn, new TokenUsage(17, 4, 0, 0, 0), null,
                new ProviderMetadata("offline-fixture", ModelId, null),
                TokenUsageFields.Input | TokenUsageFields.Output));
        }
    }

    private sealed class UnboundedFixtureProvider : IModelProvider
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public ProviderCapabilities Capabilities => new(true, false, false);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            await Task.Yield();
            yield return new ResponseCompleted(new ModelResponse([new TextBlock("measured")],
                StopReason.EndTurn, new TokenUsage(17, 4, 0, 0, 0), null,
                new ProviderMetadata("offline-fixture", ModelId, null),
                TokenUsageFields.Input | TokenUsageFields.Output));
        }
    }
}
