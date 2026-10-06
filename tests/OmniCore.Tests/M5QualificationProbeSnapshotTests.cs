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

/// <summary>Qualification must use one immutable probe set from validation through evidence commit.</summary>
public sealed class M5QualificationProbeSnapshotTests
{
    private sealed class BlockingPassingProvider : IModelProvider
    {
        private readonly Dictionary<string, string> _expectedByPrompt = QuickProbeSuite.Probes()
            .ToDictionary(probe => probe.Prompt, probe => probe.Expected, StringComparer.Ordinal);
        private readonly TaskCompletionSource<ModelRequest> _firstRequest =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blocked;

        public ProviderCapabilities Capabilities => new(true, false, false);
        public TaskCompletionSource<ModelRequest> FirstRequest => _firstRequest;
        public void ReleaseFirst() => _releaseFirst.TrySetResult();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                _firstRequest.TrySetResult(request);
                await _releaseFirst.Task.WaitAsync(cancellationToken);
            }

            var prompt = ((TextBlock)request.Messages[0].Content[0]).Text;
            var expected = _expectedByPrompt[prompt];
            yield return new ResponseCompleted(new ModelResponse([new TextBlock(expected)],
                StopReason.EndTurn, new TokenUsage(10, 5, 0, 0, 0), null,
                new ProviderMetadata("snapshot-fixture", "snapshot-model", null)));
        }
    }

    [Fact]
    public async Task Full_suite_list_mutation_during_provider_await_keeps_original_qualified_snapshot()
    {
        var root = TempDirectory();
        try
        {
            var canonical = QuickProbeSuite.Probes().ToArray();
            var mutableProbes = canonical.ToList();
            var provider = new BlockingPassingProvider();
            var model = new ModelDefinition("snapshot-model", "local", 8192, 8192, 2048);
            var key = ModelQualificationHost.QualificationKeyFor(model, null);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using (var host = ModelQualificationHost.Create(root,
                [new ModelRegistryModelDescriptor("snapshot-model", "local", 8192, 8192, 2048)]))
            {
                var running = host.QualifyAsync("snapshot-model", new QualificationOptions
                {
                    ConsentGiven = true,
                    Provider = provider,
                    Probes = mutableProbes,
                }, timeout.Token);

                var dispatched = await provider.FirstRequest.Task.WaitAsync(timeout.Token);
                Assert.Equal(canonical[0].Prompt, ((TextBlock)dispatched.Messages[0].Content[0]).Text);
                mutableProbes[0] = new Probe(canonical[0].Id, ProbeKind.StructuredOutput,
                    "mutated prompt after validation", "{\"mutated\":true}", 0m);
                provider.ReleaseFirst();

                var result = await running.WaitAsync(timeout.Token);
                Assert.True(result.SuiteComplete);
                Assert.Equal("Qualified", result.NewState);
                Assert.Equal(1L, result.ProfileRevision);
                Assert.Equal(canonical.Length, result.Probes.Count);
                Assert.All(result.Probes, probe => Assert.Equal("Passed", probe.Status));
                Assert.Equal(canonical.Select(probe => (probe.Id.ToString(), probe.Kind.ToString())),
                    result.Probes.Select(probe => (probe.Id, probe.Kind)));

                using var json = ReadEvidence(root, result.EvidenceHash!);
                var evidence = json.RootElement;
                Assert.True(evidence.GetProperty("suiteComplete").GetBoolean());
                Assert.Equal(ProbeScorer.TaskSetHash(canonical),
                    evidence.GetProperty("benchmarkIdentity").GetProperty("taskSetHash").GetString());
                var evidenceProbes = evidence.GetProperty("probes").EnumerateArray().ToArray();
                Assert.Equal(canonical.Length, evidenceProbes.Length);
                for (var i = 0; i < canonical.Length; i++)
                {
                    Assert.Equal(canonical[i].Id.ToString(), evidenceProbes[i].GetProperty("id").GetString());
                    Assert.Equal(canonical[i].Kind.ToString(), evidenceProbes[i].GetProperty("kind").GetString());
                    Assert.Equal(canonical[i].Prompt, evidenceProbes[i].GetProperty("prompt").GetString());
                    Assert.Equal(canonical[i].Expected, evidenceProbes[i].GetProperty("expected").GetString());
                    Assert.Equal("Passed", evidenceProbes[i].GetProperty("status").GetString());
                    Assert.Equal(1d, evidenceProbes[i].GetProperty("score").GetDouble());
                    Assert.Equal(canonical[i].Expected, evidenceProbes[i].GetProperty("output").GetString());
                }
            }

            using var store = new SqliteModelQualificationStore(Path.Combine(root, "user.db"));
            var profile = store.Get(key, CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(ModelQualificationState.Qualified, profile!.State);
            Assert.Equal(1L, profile.ProfileRevision);
        }
        finally
        {
            ClearAndDelete(root);
        }
    }

    [Fact]
    public async Task Partial_suite_list_mutation_to_full_during_provider_await_stays_partial()
    {
        var root = TempDirectory();
        try
        {
            var canonical = QuickProbeSuite.Probes().ToArray();
            var initialSubset = canonical.Take(2).ToArray();
            var mutableProbes = initialSubset.ToList();
            var provider = new BlockingPassingProvider();
            var model = new ModelDefinition("snapshot-model", "local", 8192, 8192, 2048);
            var key = ModelQualificationHost.QualificationKeyFor(model, null);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using (var host = ModelQualificationHost.Create(root,
                [new ModelRegistryModelDescriptor("snapshot-model", "local", 8192, 8192, 2048)]))
            {
                var running = host.QualifyAsync("snapshot-model", new QualificationOptions
                {
                    ConsentGiven = true,
                    Provider = provider,
                    Probes = mutableProbes,
                }, timeout.Token);

                var dispatched = await provider.FirstRequest.Task.WaitAsync(timeout.Token);
                Assert.Equal(initialSubset[0].Prompt, ((TextBlock)dispatched.Messages[0].Content[0]).Text);
                mutableProbes.Clear();
                mutableProbes.AddRange(canonical);
                provider.ReleaseFirst();

                var result = await running.WaitAsync(timeout.Token);
                Assert.False(result.SuiteComplete);
                Assert.Equal("ProvisionallyClassified", result.NewState);
                Assert.Equal(1L, result.ProfileRevision);
                Assert.Equal(initialSubset.Length, result.Probes.Count);
                Assert.All(result.Probes, probe => Assert.Equal("Passed", probe.Status));

                using var json = ReadEvidence(root, result.EvidenceHash!);
                var evidence = json.RootElement;
                Assert.False(evidence.GetProperty("suiteComplete").GetBoolean());
                Assert.True(evidence.GetProperty("probeSetOverride").GetBoolean());
                Assert.Equal(ProbeScorer.TaskSetHash(initialSubset),
                    evidence.GetProperty("benchmarkIdentity").GetProperty("taskSetHash").GetString());
                var evidenceProbes = evidence.GetProperty("probes").EnumerateArray().ToArray();
                Assert.Equal(initialSubset.Length, evidenceProbes.Length);
                for (var i = 0; i < initialSubset.Length; i++)
                {
                    Assert.Equal(initialSubset[i].Id.ToString(), evidenceProbes[i].GetProperty("id").GetString());
                    Assert.Equal(initialSubset[i].Prompt, evidenceProbes[i].GetProperty("prompt").GetString());
                    Assert.Equal(initialSubset[i].Expected, evidenceProbes[i].GetProperty("expected").GetString());
                    Assert.Equal("Passed", evidenceProbes[i].GetProperty("status").GetString());
                    Assert.Equal(initialSubset[i].Expected, evidenceProbes[i].GetProperty("output").GetString());
                }
            }

            using var store = new SqliteModelQualificationStore(Path.Combine(root, "user.db"));
            var profile = store.Get(key, CancellationToken.None);
            Assert.NotNull(profile);
            Assert.Equal(ModelQualificationState.ProvisionallyClassified, profile!.State);
            Assert.Equal(1L, profile.ProfileRevision);
        }
        finally
        {
            ClearAndDelete(root);
        }
    }

    private static JsonDocument ReadEvidence(string dataDirectory, string artifactHash)
    {
        Assert.StartsWith("sha256:", artifactHash);
        var hash = ContentHash.Sha256(artifactHash["sha256:".Length..]);
        var content = new FileArtifactStore(dataDirectory).GetText(hash);
        Assert.NotNull(content);
        return JsonDocument.Parse(content!);
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "omnicore-m5-probe-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void ClearAndDelete(string root)
    {
        var databasePath = Path.Combine(root, "user.db");
        if (File.Exists(databasePath))
        {
            using var connection = new SqliteConnection("DataSource=" + databasePath);
            SqliteConnection.ClearPool(connection);
        }

        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
