using System.Net;
using System.Text;
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
/// Offline contract for preflight accounting of the configured adapter's known HTTP attempts.
/// The handler is an in-memory fixture; these tests do not claim that failed attempts were billed.
/// </summary>
public sealed class M5QualificationRetryExposureTests
{
    private const string ModelId = "m5-retry-exposure-model";
    private const string ProviderId = "m5-retry-exposure-provider";

    [Fact]
    public async Task Cap_that_covers_one_attempt_but_not_three_rejects_before_http_or_persistence()
    {
        var directory = TempDir();
        try
        {
            WriteConfiguration(directory);
            var handler = new RetryHandler(Success(), ServiceUnavailable(), ServiceUnavailable());
            var provider = CreateProvider(handler);
            using var host = ModelQualificationHost.Create(directory);

            var exception = await Assert.ThrowsAsync<ModelQualificationCostCapException>(() =>
                host.QualifyAsync(ModelId, Options(provider, 0.05m), CancellationToken.None));

            Assert.Equal(0.05m, exception.CapUsd);
            Assert.Equal(0.098304m, exception.EstimatedUsd);
            Assert.Equal(0, handler.Calls);
            using var store = OmniHost.CreateModelQualificationStore(directory);
            Assert.Empty(store.List(CancellationToken.None));
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public async Task Sufficient_cap_runs_three_fixture_attempts_and_retains_success_reported_usage()
    {
        var directory = TempDir();
        try
        {
            WriteConfiguration(directory);
            var handler = new RetryHandler(ServiceUnavailable(), ServiceUnavailable(), Success());
            var provider = CreateProvider(handler);
            using var host = ModelQualificationHost.Create(directory);

            var result = await host.QualifyAsync(ModelId, Options(provider, 0.10m), CancellationToken.None);

            Assert.Equal(3, handler.Calls);
            Assert.Equal(ModelQualificationState.ProvisionallyClassified.ToString(), result.NewState);
            Assert.False(result.SuiteComplete);
            Assert.Equal(0.098304m, result.EstimatedCostUsd);
            var outcome = Assert.Single(result.Probes);
            Assert.Equal(ProbeStatus.Passed.ToString(), outcome.Status);
            Assert.Equal(0.000066m, outcome.CostUsd);
            Assert.Equal(new QualificationProbeUsage(17, 4, null, null, null), outcome.Usage);

            using var store = OmniHost.CreateModelQualificationStore(directory);
            var loaded = OmniHost.LoadUserConfiguration(OmniHost.CreatePlatformPaths(directory));
            var model = loaded.Registry.Model(ModelId)!;
            var descriptor = loaded.Registry.Provider(ProviderId)!;
            var profile = store.Get(ModelQualificationHost.QualificationKeyFor(model, descriptor), CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(ModelQualificationState.ProvisionallyClassified, profile!.State);
            Assert.Equal(1L, profile.ProfileRevision);
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    private static string TempDir()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-m5-retry-exposure",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteConfiguration(string dataDirectory)
    {
        var paths = OmniHost.CreatePlatformPaths(dataDirectory);
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

    private static Probe Probe() => new(ProbeId.WellKnown("retry-exposure-reading"), ProbeKind.Reading,
        "Reply with only the word measured.", "measured", 0m);

    private static QualificationOptions Options(IModelProvider provider, decimal cap) => new()
    {
        Suite = "quick",
        ConsentGiven = true,
        MaxTotalCostUsd = cap,
        Probes = [Probe()],
        Provider = provider,
    };

    private static OpenAiChatCompatibleProvider CreateProvider(RetryHandler handler) =>
        new(new ProviderDescriptor(ProviderId, ProviderFamily.OpenAiChatCompatible,
                "https://fixture.invalid/v1", AuthConfig.None(), false, false, true)
            { BillingMode = BillingMode.MeteredCurrency },
            new EmptySecrets(), () => new HttpClient(handler, disposeHandler: false),
            new OpenAiProviderOptions
            {
                MaxRetries = 2,
                CircuitFailureThreshold = 10,
                DelayAsync = static (_, _) => ValueTask.CompletedTask,
                Jitter = static () => 0,
            });

    private static HttpResponseMessage ServiceUnavailable() =>
        new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("fixture transient failure") };

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "data: {\"choices\":[{\"delta\":{\"content\":\"measured\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":17,\"completion_tokens\":4}}\n\n" +
            "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
    };

    private static void DeleteFixtureDirectory(string directory)
    {
        var databasePath = Path.GetFullPath(Path.Combine(directory, "user.db"));
        using (var connection = new SqliteConnection("DataSource=" + databasePath))
            SqliteConnection.ClearPool(connection);
        Directory.Delete(directory, recursive: true);
    }

    private sealed class RetryHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _calls) - 1;
            if ((uint)index >= (uint)responses.Length)
                throw new InvalidOperationException("Fixture received more HTTP attempts than scripted.");
            return System.Threading.Tasks.Task.FromResult(responses[index]);
        }
    }

    private sealed class EmptySecrets : ISecretProvider
    {
        public Secret GetSecret(string reference, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Fixture uses Auth.None and must not request a secret.");
    }
}
