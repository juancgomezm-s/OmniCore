using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Models;
using OmniCore.Qualification;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>
/// Fixture offline del preflight de costo del Host: el cap se valida antes de iniciar cualquier
/// StreamAsync. La API actual permite inyectar un provider ya creado, no contar ConnectProvider.
/// </summary>
public sealed class M5QualificationSpendPreflightTests
{
    // Counting fixture never delegates to a transport and has one generation response per invocation.
    // Its bound is local test scope only, not a billing guarantee.
    private sealed class CountingProvider : IModelProvider, IModelRequestAttemptBound
    {
        public int StreamCalls { get; private set; }
        public long? MaximumGenerationRequestAttempts => 1;

        public ProviderCapabilities Capabilities => new(true, false, false);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            StreamCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ResponseCompleted(new ModelResponse(
                [new TextBlock("ok")], StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
                new ProviderMetadata("fixture", "fixture-model", null)));
        }
    }

    private static string TempDir()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-m5-spend-preflight", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static IReadOnlyList<ModelRegistryModelDescriptor> Registry() =>
    [
        new ModelRegistryModelDescriptor("fixture-model", "local", 8192, 8192, 2048),
    ];

    [Fact]
    public async Task Host_rejects_cap_exceeded_before_streaming_any_probe()
    {
        var directory = TempDir();
        try
        {
            var provider = new CountingProvider();
            using var host = ModelQualificationHost.Create(directory, Registry());
            var options = new QualificationOptions
            {
                Suite = "quick",
                ConsentGiven = true,
                MaxTotalCostUsd = 1m,
                PerProbeTimeout = TimeSpan.Zero,
                Provider = provider,
                Probes = [new Probe(ProbeId.WellKnown("over-cap"), ProbeKind.Reading, "prompt", "ok", 2m)],
            };

            // El rechazo de costo debe preceder a construir ProbeRunner, que también valida timeout.
            var exception = await Assert.ThrowsAsync<ModelQualificationCostCapException>(() =>
                host.QualifyAsync("fixture-model", options, CancellationToken.None));

            Assert.Equal(2m, exception.EstimatedUsd);
            Assert.Equal(1m, exception.CapUsd);
            Assert.Equal(0, provider.StreamCalls);
            using var store = OmniHost.CreateModelQualificationStore(directory);
            Assert.Empty(store.List(CancellationToken.None));
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task Host_streams_fixture_when_known_estimate_fits_cap()
    {
        var directory = TempDir();
        try
        {
            var provider = new CountingProvider();
            using var host = ModelQualificationHost.Create(directory, Registry());
            var options = new QualificationOptions
            {
                Suite = "quick",
                ConsentGiven = true,
                MaxTotalCostUsd = 1m,
                Provider = provider,
                Probes = [new Probe(ProbeId.WellKnown("within-cap"), ProbeKind.Reading, "prompt", "ok", 0.5m)],
            };

            await host.QualifyAsync("fixture-model", options, CancellationToken.None);

            Assert.Equal(1, provider.StreamCalls);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static void DeleteTempDirectory(string directory)
    {
        var userDatabasePath = Path.GetFullPath(Path.Combine(directory, "user.db"));
        using (var connection = new SqliteConnection("DataSource=" + userDatabasePath))
        {
            SqliteConnection.ClearPool(connection);
        }

        Directory.Delete(directory, recursive: true);
    }
}
