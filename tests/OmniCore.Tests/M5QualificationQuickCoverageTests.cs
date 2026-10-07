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

/// <summary>Coverage gates on the actual Quick task set, using a private scripted provider.</summary>
public sealed class M5QualificationQuickCoverageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Passing_subset_or_modified_set_is_provisional_not_qualified_quick(bool modified)
    {
        using var fixture = new Fixture();
        var probes = QuickProbeSuite.Probes().ToArray();
        if (modified)
            probes[0] = new Probe(probes[0].Id, probes[0].Kind, probes[0].Prompt,
                "different-expected-answer", probes[0].MaxCostUsd);
        else probes = probes.Take(2).ToArray();
        var result = await fixture.Host.QualifyAsync("fixture-model", fixture.Options(probes), CancellationToken.None);
        Assert.All(result.Probes, probe => Assert.Equal("Passed", probe.Status));
        Assert.Equal(ModelQualificationState.ProvisionallyClassified.ToString(), result.NewState);
        Assert.False(result.SuiteComplete);
        using var store = fixture.Store();
        Assert.Equal(ModelQualificationState.ProvisionallyClassified, store.Get(fixture.Key, CancellationToken.None)!.State);
        Assert.NotNull(store.Evidence(fixture.Key, 1, CancellationToken.None));
        Assert.Null(ModelQualificationHost.UsableTraits(store, fixture.Model, null, CancellationToken.None));
    }

    [Fact]
    public async Task Complete_reordered_quick_still_qualifies_and_has_all_probes()
    {
        using var fixture = new Fixture();
        var probes = QuickProbeSuite.Probes().Reverse().ToArray();
        var result = await fixture.Host.QualifyAsync("fixture-model", fixture.Options(probes), CancellationToken.None);
        Assert.Equal(QuickProbeSuite.Probes().Count, result.Probes.Count);
        Assert.All(result.Probes, probe => Assert.Equal("Passed", probe.Status));
        Assert.Equal(ModelQualificationState.Qualified.ToString(), result.NewState);
        Assert.True(result.SuiteComplete);
        using var store = fixture.Store();
        Assert.Equal(ModelQualificationState.Qualified, store.Get(fixture.Key, CancellationToken.None)!.State);
        Assert.NotNull(ModelQualificationHost.UsableTraits(store, fixture.Model, null, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full_quick_persists_complete_visible_answer_and_qualification_not_a_prefix(bool invalidSuffix)
    {
        using var fixture = new Fixture();
        var probes = QuickProbeSuite.Probes();
        var result = await fixture.Host.QualifyAsync("fixture-model", new QualificationOptions
        { ConsentGiven = true, Provider = new Provider(probes, invalidSuffix) },
            TestContext.Current.CancellationToken);
        var expectedState = invalidSuffix ? ModelQualificationState.ProvisionallyClassified
            : ModelQualificationState.Qualified;
        Assert.True(result.SuiteComplete);
        Assert.Equal(10, result.Probes.Count);
        Assert.Equal(expectedState.ToString(), result.NewState);
        Assert.All(result.Probes, probe => Assert.Equal(invalidSuffix ? "Failed" : "Passed", probe.Status));
        using var store = fixture.Store();
        var profile = Assert.IsType<ModelQualificationProfile>(store.Get(fixture.Key,
            TestContext.Current.CancellationToken));
        Assert.Equal(expectedState, profile.State);
        Assert.Equal(QuickProbeSuite.SuiteVersion, profile.SuiteVersion);
        var evidence = Assert.IsType<ModelQualificationEvidence>(store.Evidence(fixture.Key,
            profile.ProfileRevision, TestContext.Current.CancellationToken));
        using var json = JsonDocument.Parse(new FileArtifactStore(fixture.DirectoryPath)
            .GetText(evidence.Artifact.Hash)!);
        Assert.Equal(QuickProbeSuite.SuiteVersion, json.RootElement.GetProperty("benchmarkIdentity")
            .GetProperty("suiteVersion").GetString());
        var recorded = json.RootElement.GetProperty("probes").EnumerateArray().ToArray();
        Assert.Equal(10, recorded.Length);
        Assert.All(recorded, probe => Assert.Equal(probe.GetProperty("expected").GetString()
            + (invalidSuffix ? " trailing" : string.Empty), probe.GetProperty("output").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_or_duplicate_probe_ids_reject_before_calls_or_profile(bool duplicate)
    {
        using var fixture = new Fixture();
        var first = QuickProbeSuite.Probes()[0];
        var probes = duplicate ? new[] { first, first } : Array.Empty<Probe>();
        var provider = new Provider(probes);
        var options = new QualificationOptions { ConsentGiven = true, Provider = provider, Probes = probes };
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Host.QualifyAsync("fixture-model", options, CancellationToken.None));
        Assert.Equal(0, provider.Calls);
        using var store = fixture.Store();
        Assert.Empty(store.List(CancellationToken.None));
    }

    [Fact]
    public async Task Unsupported_suite_cannot_bypass_validation_using_a_probe_override()
    {
        using var fixture = new Fixture();
        var probes = QuickProbeSuite.Probes().Take(2).ToArray();
        var provider = new Provider(probes);
        var options = new QualificationOptions { Suite = "full", ConsentGiven = true, Provider = provider, Probes = probes };
        await Assert.ThrowsAsync<ModelQualificationUnsupportedSuiteException>(() => fixture.Host.QualifyAsync("fixture-model", options, CancellationToken.None));
        Assert.Equal(0, provider.Calls);
        using var store = fixture.Store();
        Assert.Empty(store.List(CancellationToken.None));
    }

    // Scripted fixture has one generation response per StreamAsync invocation; this is not a billing guarantee.
    private sealed class Provider(IReadOnlyList<Probe> probes, bool? invalidSuffix = null) : IModelProvider, IModelRequestAttemptBound
    {
        public int Calls { get; private set; }
        public long? MaximumGenerationRequestAttempts => 1;
        public ProviderCapabilities Capabilities => new(true, false, false);
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++; await Task.Yield();
            var prompt = ((TextBlock)request.Messages[0].Content[0]).Text;
            var expected = probes.First(p => p.Prompt == prompt).Expected;
            ContentBlock[] content = invalidSuffix switch
            {
                true => [new TextBlock(expected), new TextBlock(" trailing")],
                false => [new TextBlock(expected[..1]), new TextBlock(expected[1..])],
                null => [new TextBlock(expected)],
            };
            yield return new ResponseCompleted(new ModelResponse(content,
                StopReason.EndTurn, new TokenUsage(10, 5, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture-model", null)));
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "omnicore-quick-coverage-" + Guid.NewGuid().ToString("N"));
        public ModelDefinition Model { get; } = new("fixture-model", "local", 8192, 8192, 2048);
        public ModelQualificationKey Key => ModelQualificationHost.QualificationKeyFor(Model, null);
        public ModelQualificationHost Host { get; }
        public Fixture() => Host = ModelQualificationHost.Create(DirectoryPath,
            [new ModelRegistryModelDescriptor("fixture-model", "local", 8192, 8192, 2048)]);
        public SqliteModelQualificationStore Store() => new(Path.Combine(DirectoryPath, "user.db"));
        public QualificationOptions Options(IReadOnlyList<Probe> probes) => new()
        { ConsentGiven = true, Provider = new Provider(probes), Probes = probes };
        public void Dispose()
        {
            Host.Dispose();
            using var connection = new SqliteConnection("DataSource=" + Path.Combine(DirectoryPath, "user.db"));
            SqliteConnection.ClearPool(connection); Directory.Delete(DirectoryPath, true);
        }
    }
}
