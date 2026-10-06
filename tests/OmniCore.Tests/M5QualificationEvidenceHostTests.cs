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

/// <summary>Real Host/store/CAS integration with a scripted provider, not authenticated usage.</summary>
public sealed class M5QualificationEvidenceHostTests
{
    private sealed class Provider : IModelProvider
    {
        public ProviderCapabilities Capabilities => new(true, false, false);
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            var prompt = ((TextBlock)request.Messages[0].Content[0]).Text;
            var expected = QuickProbeSuite.Probes().Single(p => p.Prompt == prompt).Expected;
            yield return new ResponseCompleted(new ModelResponse([new TextBlock(expected)],
                StopReason.EndTurn, new TokenUsage(10, 5, 0, 0, 0), null,
                new ProviderMetadata("fixture-request", "fixture-model", null)));
        }
    }

    [Fact]
    public async Task Injected_probe_subset_is_explicit_and_its_actual_hash_is_not_the_default_suite_hash()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m5-evidence-host", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var subset = QuickProbeSuite.Probes().Take(2).ToArray();
            using var host = ModelQualificationHost.Create(dir,
                [new ModelRegistryModelDescriptor("fixture-model", "local", 8192, 8192, 2048)]);
            var result = await host.QualifyAsync("fixture-model", new QualificationOptions
            { ConsentGiven = true, Provider = new Provider(), Probes = subset }, CancellationToken.None);
            Assert.Equal(ModelQualificationState.ProvisionallyClassified.ToString(), result.NewState);
            Assert.False(result.SuiteComplete);
            var hash = ContentHash.Sha256(result.EvidenceHash!["sha256:".Length..]);
            using var json = JsonDocument.Parse(new FileArtifactStore(dir).GetText(hash)!);
            Assert.True(json.RootElement.GetProperty("probeSetOverride").GetBoolean());
            Assert.False(json.RootElement.GetProperty("suiteComplete").GetBoolean());
            Assert.Equal(2, json.RootElement.GetProperty("probes").GetArrayLength());
            var taskHash = json.RootElement.GetProperty("benchmarkIdentity").GetProperty("taskSetHash").GetString();
            Assert.Equal(ProbeScorer.TaskSetHash(subset), taskHash);
            Assert.NotEqual(ProbeScorer.TaskSetHash(QuickProbeSuite.Probes()), taskHash);
        }
        finally
        {
            using var connection = new SqliteConnection("DataSource=" + Path.Combine(dir, "user.db"));
            SqliteConnection.ClearPool(connection);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Host_commits_complete_evidence_reopens_history_and_gc_preserves_each_run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m5-evidence-host", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var model = new ModelDefinition("fixture-model", "local", 8192, 8192, 2048);
            var key = ModelQualificationHost.QualificationKeyFor(model, null);
            using (var host = ModelQualificationHost.Create(dir,
                [new ModelRegistryModelDescriptor("fixture-model", "local", 8192, 8192, 2048)]))
            {
                for (var revision = 1; revision <= 2; revision++)
                {
                    var result = await host.QualifyAsync("fixture-model", new QualificationOptions
                    { ConsentGiven = true, Provider = new Provider(), MaxTotalCostUsd = 1 }, CancellationToken.None);
                    Assert.Equal(revision, result.ProfileRevision);
                    Assert.True(result.SuiteComplete);
                    Assert.StartsWith("sha256:", result.EvidenceHash!);
                    using var store = new SqliteModelQualificationStore(Path.Combine(dir, "user.db"));
                    var evidence = Assert.IsType<ModelQualificationEvidence>(store.Evidence(key, revision, CancellationToken.None));
                    Assert.Equal(result.EvidenceHash, evidence.Artifact.Hash.ToString());
                    Assert.Equal(revision, evidence.SourceRunRevision);
                    var text = new FileArtifactStore(dir).GetText(evidence.Artifact.Hash);
                    using var json = JsonDocument.Parse(text!);
                    var root = json.RootElement;
                    Assert.Equal("omnicore.model-qualification.v1", root.GetProperty("schema").GetString());
                    Assert.Equal("injected-provider", root.GetProperty("source").GetString());
                    Assert.False(root.GetProperty("probeSetOverride").GetBoolean());
                    Assert.True(root.GetProperty("suiteComplete").GetBoolean());
                    var identity = root.GetProperty("benchmarkIdentity");
                    Assert.Equal(ProbeScorer.TaskSetHash(QuickProbeSuite.Probes()), identity.GetProperty("taskSetHash").GetString());
                    Assert.False(identity.GetProperty("samplingParametersSent").GetBoolean());
                    Assert.Equal(JsonValueKind.Null, identity.GetProperty("seed").ValueKind);
                    Assert.Equal(JsonValueKind.Null, identity.GetProperty("temperature").ValueKind);
                    Assert.Contains(typeof(ModelQualificationHost).Assembly.ManifestModule.ModuleVersionId.ToString("D"),
                        identity.GetProperty("omniCoreVersion").GetString()!);
                    var probes = root.GetProperty("probes").EnumerateArray().ToArray();
                    Assert.Equal(QuickProbeSuite.Probes().Count, probes.Length);
                    for (var i = 0; i < probes.Length; i++)
                    {
                        Assert.Equal(QuickProbeSuite.Probes()[i].Prompt, probes[i].GetProperty("prompt").GetString());
                        Assert.Equal(QuickProbeSuite.Probes()[i].Expected, probes[i].GetProperty("expected").GetString());
                        Assert.Equal(QuickProbeSuite.Probes()[i].Expected, probes[i].GetProperty("output").GetString());
                        Assert.Equal("Passed", probes[i].GetProperty("status").GetString());
                        Assert.Equal(1, probes[i].GetProperty("score").GetDouble());
                        Assert.Equal(JsonValueKind.Null, probes[i].GetProperty("costUsd").ValueKind);
                        Assert.True(probes[i].TryGetProperty("reportedUsageFields", out _));
                    }
                    Assert.Equal(result.Traits.Count, root.GetProperty("traits").GetArrayLength());
                }
            }
            using (var reopened = new SqliteModelQualificationStore(Path.Combine(dir, "user.db")))
            {
                Assert.NotNull(reopened.Evidence(key, 1, CancellationToken.None));
                Assert.NotNull(reopened.Evidence(key, 2, CancellationToken.None));
                reopened.MarkStale(key, 2, "2.0.0", CancellationToken.None);
                var alias = reopened.Evidence(key, 3, CancellationToken.None)!;
                Assert.Equal(2, alias.SourceRunRevision);
                Assert.Equal(reopened.Evidence(key, 2, CancellationToken.None)!.Artifact, alias.Artifact);
            }
            var swept = new ArtifactGc(dir).Sweep(null, TimeSpan.Zero, false,
                DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
            Assert.Equal(0, swept.Deleted);
            Assert.Equal(2, swept.LiveReferenced);
        }
        finally
        {
            using var connection = new SqliteConnection("DataSource=" + Path.Combine(dir, "user.db"));
            SqliteConnection.ClearPool(connection);
            Directory.Delete(dir, true);
        }
    }
}
