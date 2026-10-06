// Proposed offline regression coverage; this file is intentionally outside the repository.
// The provider below is a scripted one-generation fixture, not a billing or remote-provider guarantee.
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Models;
using OmniCore.Qualification;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class M5QualificationCancellationTests
{
    private const string ModelId = "m5-cancel-model";
    private const string ProviderId = "m5-cancel-local";

    [Fact]
    public async Task Cancellation_before_qualification_is_propagated_without_provider_call_or_evidence()
    {
        var directory = CreatePrivateDirectory();
        try
        {
            var paths = ConfigureLocal(directory);
            var loaded = OmniHost.LoadUserConfiguration(paths);
            var model = Assert.IsType<ModelDefinition>(loaded.Registry.Model(ModelId));
            var provider = Assert.IsType<ProviderDescriptor>(loaded.Registry.Provider(ProviderId));
            var key = ModelQualificationHost.QualificationKeyFor(model, provider);
            var fixture = new ScriptedProvider(ScriptBehavior.CompleteNormally);
            using var host = ModelQualificationHost.Create(directory);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => host.QualifyAsync(ModelId,
                Options(fixture), cancellation.Token));

            Assert.Equal(0, fixture.InvocationCount);
            AssertNoQualificationWrites(directory, key);
        }
        finally
        {
            ClearPrivateUserDatabasePool(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_between_probes_is_propagated_instead_of_becoming_suite_incomplete()
    {
        var directory = CreatePrivateDirectory();
        try
        {
            var paths = ConfigureLocal(directory);
            var loaded = OmniHost.LoadUserConfiguration(paths);
            var model = Assert.IsType<ModelDefinition>(loaded.Registry.Model(ModelId));
            var provider = Assert.IsType<ProviderDescriptor>(loaded.Registry.Provider(ProviderId));
            var key = ModelQualificationHost.QualificationKeyFor(model, provider);
            using var cancellation = new CancellationTokenSource();
            var fixture = new ScriptedProvider(ScriptBehavior.CancelAfterFirstCompletedProbe, cancellation);
            using var host = ModelQualificationHost.Create(directory);

            await Assert.ThrowsAsync<OperationCanceledException>(() => host.QualifyAsync(ModelId,
                Options(fixture), cancellation.Token));

            Assert.Equal(1, fixture.InvocationCount);
            AssertNoQualificationWrites(directory, key);
        }
        finally
        {
            ClearPrivateUserDatabasePool(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_during_a_probe_is_propagated_without_profile_or_evidence()
    {
        var directory = CreatePrivateDirectory();
        try
        {
            var paths = ConfigureLocal(directory);
            var loaded = OmniHost.LoadUserConfiguration(paths);
            var model = Assert.IsType<ModelDefinition>(loaded.Registry.Model(ModelId));
            var provider = Assert.IsType<ProviderDescriptor>(loaded.Registry.Provider(ProviderId));
            var key = ModelQualificationHost.QualificationKeyFor(model, provider);
            using var cancellation = new CancellationTokenSource();
            var fixture = new ScriptedProvider(ScriptBehavior.CancelDuringFirstProbe, cancellation);
            using var host = ModelQualificationHost.Create(directory);

            await Assert.ThrowsAsync<OperationCanceledException>(() => host.QualifyAsync(ModelId,
                Options(fixture), cancellation.Token));

            Assert.Equal(1, fixture.InvocationCount);
            AssertNoQualificationWrites(directory, key);
        }
        finally
        {
            ClearPrivateUserDatabasePool(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_after_final_probe_does_not_publish_orphaned_evidence()
    {
        var directory = CreatePrivateDirectory();
        try
        {
            var paths = ConfigureLocal(directory);
            var loaded = OmniHost.LoadUserConfiguration(paths);
            var model = Assert.IsType<ModelDefinition>(loaded.Registry.Model(ModelId));
            var provider = Assert.IsType<ProviderDescriptor>(loaded.Registry.Provider(ProviderId));
            var key = ModelQualificationHost.QualificationKeyFor(model, provider);
            using var cancellation = new CancellationTokenSource();
            var fixture = new ScriptedProvider(ScriptBehavior.CancelAfterFinalCompletedProbe, cancellation);
            using var host = ModelQualificationHost.Create(directory);

            await Assert.ThrowsAsync<OperationCanceledException>(() => host.QualifyAsync(ModelId,
                Options(fixture), cancellation.Token));

            Assert.Equal(QuickProbeSuite.Probes().Count, fixture.InvocationCount);
            AssertNoQualificationWrites(directory, key);
        }
        finally
        {
            ClearPrivateUserDatabasePool(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static QualificationOptions Options(IModelProvider provider) => new()
    {
        Suite = "quick",
        ConsentGiven = true,
        MaxTotalCostUsd = 1m,
        Provider = provider,
    };

    private static string CreatePrivateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-m5-qualification-cancel-",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static IPlatformPaths ConfigureLocal(string directory)
    {
        var paths = OmniHost.CreatePlatformPaths(directory);
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), $$"""
            providers:
              {{ProviderId}}:
                family: OpenAiChatCompatible
                baseUrl: http://127.0.0.1:8080/v1
                auth: none
                billingMode: Local
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

    private static void AssertNoQualificationWrites(string directory, ModelQualificationKey key)
    {
        using var store = OmniHost.CreateModelQualificationStore(directory);
        Assert.Empty(store.List(CancellationToken.None));
        Assert.Null(store.Evidence(key, 1, CancellationToken.None));
        var blobs = Path.Combine(directory, "blobs");
        if (Directory.Exists(blobs))
            Assert.Empty(Directory.EnumerateFiles(blobs, "*", SearchOption.AllDirectories));
    }

    private static void ClearPrivateUserDatabasePool(string directory)
    {
        var databasePath = Path.GetFullPath(Path.Combine(directory, "user.db"));
        using var poolIdentity = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        SqliteConnection.ClearPool(poolIdentity);
    }

    private enum ScriptBehavior
    {
        CompleteNormally,
        CancelAfterFirstCompletedProbe,
        CancelAfterFinalCompletedProbe,
        CancelDuringFirstProbe,
    }

    private sealed class ScriptedProvider : IModelProvider, IModelRequestAttemptBound
    {
        private readonly ScriptBehavior _behavior;
        private readonly CancellationTokenSource? _cancellation;
        private int _invocationCount;

        // One scripted response per invocation only; this is not a provider or billing guarantee.
        public long? MaximumGenerationRequestAttempts => 1;
        public int InvocationCount => Volatile.Read(ref _invocationCount);
        public ProviderCapabilities Capabilities => new(true, false, false);

        public ScriptedProvider(ScriptBehavior behavior, CancellationTokenSource? cancellation = null)
        {
            _behavior = behavior;
            _cancellation = cancellation;
        }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _invocationCount);
            cancellationToken.ThrowIfCancellationRequested();
            if (_behavior == ScriptBehavior.CancelDuringFirstProbe && call == 1)
            {
                yield return new ResponseStarted(0);
                _cancellation!.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                yield break;
            }

            var prompt = (request.Messages[0].Content[0] as TextBlock)?.Text ?? "";
            var expected = QuickProbeSuite.Probes().Single(probe => probe.Prompt == prompt).Expected;
            yield return new ResponseCompleted(new ModelResponse(
                [new TextBlock(expected)], StopReason.EndTurn, new TokenUsage(10, 5, 0, 0, 0),
                State: null, Metadata: new ProviderMetadata("scripted", "m5-cancel-model", null)));

            if (_behavior == ScriptBehavior.CancelAfterFirstCompletedProbe && call == 1)
                _cancellation!.Cancel();
            if (_behavior == ScriptBehavior.CancelAfterFinalCompletedProbe
                && call == QuickProbeSuite.Probes().Count)
                _cancellation!.Cancel();
            await Task.Yield();
        }
    }
}
