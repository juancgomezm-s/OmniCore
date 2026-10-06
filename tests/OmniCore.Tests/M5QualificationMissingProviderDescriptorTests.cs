using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Qualification;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class M5QualificationMissingProviderDescriptorTests
{
    private const string ModelId = "m5-missing-provider-descriptor-model";
    private const string MissingProviderId = "m5-unmapped-provider";
    private const string FixtureProviderId = "m5-explicit-local-provider";
    private const string ClosedLoopbackBaseUrl = "http://127.0.0.1:1/v1";

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Missing_descriptor_is_unavailable_before_connect_for_zero_or_positive_cap(int capDollars)
    {
        var directory = TempDirectory();
        using var endpoint = new EnvironmentVariableScope("OMNI_BASE_URL", ClosedLoopbackBaseUrl);
        try
        {
            var descriptor = new ModelRegistryModelDescriptor(ModelId, MissingProviderId, 8192, 8192, 2048);
            using var host = ModelQualificationHost.Create(directory, [descriptor]);
            var preview = host.PreviewSuiteCost(ModelId, "quick");
            Assert.Null(preview.Usd);
            Assert.Equal("unavailable", preview.Source);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await Assert.ThrowsAsync<ModelQualificationCostEvidenceUnavailableException>(() =>
                host.QualifyAsync(ModelId, new QualificationOptions
                {
                    ConsentGiven = true,
                    MaxTotalCostUsd = capDollars,
                    Probes = [Probe()],
                    // No descriptor and no injected provider: only loopback can be selected by fallback.
                }, timeout.Token));

            using var store = OmniHost.CreateModelQualificationStore(directory);
            var key = Key(null);
            Assert.Null(store.Get(key, CancellationToken.None));
            Assert.Empty(store.List(CancellationToken.None));
            Assert.Empty(store.Traits(key, 1, CancellationToken.None));
            Assert.Null(store.Evidence(key, 1, CancellationToken.None));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public async Task Explicitly_injected_fake_provider_remains_an_offline_partial_suite_control()
    {
        var directory = TempDirectory();
        using var endpoint = new EnvironmentVariableScope("OMNI_BASE_URL", ClosedLoopbackBaseUrl);
        try
        {
            var provider = new PassingFixtureProvider();
            using var host = ModelQualificationHost.Create(directory,
                [new ModelRegistryModelDescriptor(ModelId, MissingProviderId, 8192, 8192, 2048)]);
            var result = await host.QualifyAsync(ModelId, new QualificationOptions
            {
                ConsentGiven = true,
                MaxTotalCostUsd = 0m,
                Probes = QuickProbeSuite.Probes().Take(2).ToArray(),
                Provider = provider,
            }, CancellationToken.None);

            Assert.Equal(2, provider.Calls);
            Assert.False(result.SuiteComplete);
            Assert.Equal(ModelQualificationState.ProvisionallyClassified.ToString(), result.NewState);
            Assert.Equal(2, result.Probes.Count);
            Assert.All(result.Probes, probe => Assert.Equal(ProbeStatus.Passed.ToString(), probe.Status));
            using var store = OmniHost.CreateModelQualificationStore(directory);
            var profile = store.Get(Key(null), CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(ModelQualificationState.ProvisionallyClassified, profile!.State);
            Assert.NotNull(store.Evidence(Key(null), profile.ProfileRevision, CancellationToken.None));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public async Task Explicit_local_descriptor_keeps_zero_declared_cap_compatibility()
    {
        var directory = TempDirectory();
        try
        {
            WriteLocalConfiguration(directory);
            var provider = new PassingFixtureProvider();
            using var host = ModelQualificationHost.Create(directory);
            var preview = host.PreviewSuiteCost(ModelId, "quick");
            Assert.Equal(0m, preview.Usd);
            Assert.Equal("declared-local-probe-maxima", preview.Source);
            var result = await host.QualifyAsync(ModelId, new QualificationOptions
            {
                ConsentGiven = true,
                MaxTotalCostUsd = 0m,
                Probes = QuickProbeSuite.Probes().Take(2).ToArray(),
                Provider = provider,
            }, CancellationToken.None);

            Assert.Equal(2, provider.Calls);
            Assert.False(result.SuiteComplete);
            Assert.Equal(ModelQualificationState.ProvisionallyClassified.ToString(), result.NewState);
            Assert.Equal(0m, result.EstimatedCostUsd);
            using var store = OmniHost.CreateModelQualificationStore(directory);
            var profile = Assert.Single(store.List(CancellationToken.None));
            Assert.Equal(ModelQualificationState.ProvisionallyClassified, profile.State);
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public void Config_loader_rejects_model_whose_provider_descriptor_is_missing()
    {
        const string models = """
            models:
              m5-missing-provider-descriptor-model:
                provider: m5-unmapped-provider
                context: 8192
                recommendedUsableContext: 8192
                maxOutput: 2048
            """;

        var error = Assert.Throws<ConfigValidationException>(() => new ConfigLoader().Load(null, models));

        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.File == "models.yaml"
            && diagnostic.KeyPath == "models." + ModelId + ".provider");
    }

    private static Probe Probe() => new(ProbeId.WellKnown("missing-descriptor-probe"), ProbeKind.Reading,
        "Reply with only the word local.", "local", 0m);

    private static ModelQualificationKey Key(ProviderDescriptor? provider)
    {
        var model = new ModelDefinition(ModelId,
            provider?.Id ?? MissingProviderId, 8192, 8192, 2048);
        return ModelQualificationHost.QualificationKeyFor(model, provider);
    }

    private static void WriteLocalConfiguration(string directory)
    {
        var paths = OmniHost.CreatePlatformPaths(directory);
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), $$"""
            providers:
              {{FixtureProviderId}}:
                family: OpenAiChatCompatible
                baseUrl: {{ClosedLoopbackBaseUrl}}
                auth: none
                billingMode: Local
            """);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "models.yaml"), $$"""
            models:
              {{ModelId}}:
                provider: {{FixtureProviderId}}
                context: 8192
                recommendedUsableContext: 8192
                maxOutput: 2048
            """);
    }

    // Scripted fixture has one generation response per StreamAsync invocation; this is not a billing guarantee.
    private sealed class PassingFixtureProvider : IModelProvider, IModelRequestAttemptBound
    {
        private readonly Dictionary<string, string> _answers = QuickProbeSuite.Probes()
            .ToDictionary(probe => probe.Prompt, probe => probe.Expected, StringComparer.Ordinal);

        public int Calls { get; private set; }
        public long? MaximumGenerationRequestAttempts => 1;
        public ProviderCapabilities Capabilities => new(true, false, false);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var prompt = ((TextBlock)request.Messages[0].Content[0]).Text;
            await Task.Yield();
            yield return new ResponseCompleted(new ModelResponse([new TextBlock(_answers[prompt])],
                StopReason.EndTurn, new TokenUsage(10, 5, 0, 0, 0), null,
                new ProviderMetadata("missing-descriptor-fixture", ModelId, null)));
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-m5-missing-provider-descriptor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteFixtureDirectory(string directory)
    {
        var databasePath = Path.GetFullPath(Path.Combine(directory, "user.db"));
        if (File.Exists(databasePath))
        {
            using var connection = new SqliteConnection("DataSource=" + databasePath);
            SqliteConnection.ClearPool(connection);
        }

        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
