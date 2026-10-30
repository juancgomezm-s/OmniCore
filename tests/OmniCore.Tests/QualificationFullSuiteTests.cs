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

/// <summary>Real Git worktrees, real patch tool and SDK compiler; scripted model output, no provider charge.</summary>
public sealed class QualificationFullSuiteTests
{
    private static ModelSelection Selection() => new(new ModelIdValue("fixture-model"), 8192, ToolMode.Direct, null);

    [Fact]
    public void Full_task_set_is_versioned_unique_and_distinct_from_quick()
    {
        var full = FullProbeSuite.Probes();
        Assert.Equal(20, full.Count);
        Assert.Equal(20, full.Select(p => p.Id).Distinct().Count());
        Assert.Equal(10, full.Count(p => p.Kind == ProbeKind.CodeFixture));
        Assert.Equal(ProbeScorer.TaskSetHash(full), ProbeScorer.TaskSetHash(FullProbeSuite.Probes()));
        Assert.NotEqual(ProbeScorer.TaskSetHash(full), ProbeScorer.TaskSetHash(QuickProbeSuite.Probes()));
    }

    [Fact]
    public async Task Code_fixture_cannot_claim_pass_without_executing_a_validator()
    {
        var probe = FullProbeSuite.Probes().First(p => p.Kind == ProbeKind.CodeFixture);
        var provider = new Provider([probe]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProbeRunner(provider)
            .RunProbeAsync(new ProbeRequest(probe, Selection()), TestContext.Current.CancellationToken));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Arbitrary_code_does_not_reach_the_compiler_or_validator()
    {
        var probe = FullProbeSuite.Probes().First(p => p.Kind == ProbeKind.CodeFixture);
        var provider = new Provider([probe], "{\"path\":\"../outside.cs\",\"newText\":\"System.IO.File.Delete(unsafe)\"}");
        var validator = new RejectingValidator();
        var result = await new ProbeRunner(provider).RunProbeAsync(
            new ProbeRequest(probe, Selection()) { FixtureValidator = validator }, TestContext.Current.CancellationToken);
        Assert.Equal(ProbeStatus.Failed, result.Status);
        Assert.Equal(0, validator.Calls);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Exact_json_is_not_sufficient_when_real_code_validation_fails()
    {
        var probe = FullProbeSuite.Probes().First(p => p.Kind == ProbeKind.CodeFixture);
        var validator = new RejectingValidator();
        var result = await new ProbeRunner(new Provider([probe])).RunProbeAsync(
            new ProbeRequest(probe, Selection()) { FixtureValidator = validator }, TestContext.Current.CancellationToken);
        Assert.Equal(ProbeStatus.Failed, result.Status);
        Assert.Equal(1, validator.Calls);
        Assert.NotNull(result.Usage);
    }

    [Fact]
    public async Task Real_fixture_compiles_and_runs_the_pinned_postimage()
    {
        var probe = FullProbeSuite.Probes().First(p => p.Kind == ProbeKind.CodeFixture);
        using var fixtures = new FullQualificationFixtures();
        var result = await fixtures.ValidateAsync(probe, probe.Expected, TestContext.Current.CancellationToken);
        Assert.True(result.Passed, result.Error);
    }

    [Fact]
    public async Task Full_host_runs_twenty_probes_ten_real_worktrees_and_persists_exact_suite_identity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omni-full-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var host = ModelQualificationHost.Create(directory,
                [new ModelRegistryModelDescriptor("fixture-model", "local", 8192, 8192, 2048)]);
            var probes = FullProbeSuite.Probes();
            var provider = new Provider(probes);
            var result = await host.QualifyAsync("fixture-model", new QualificationOptions
            { Suite = "full", ConsentGiven = true, Provider = provider }, TestContext.Current.CancellationToken);
            Assert.True(result.SuiteComplete);
            Assert.Equal("Qualified", result.NewState);
            Assert.Equal(20, provider.Calls);
            Assert.All(result.Probes, p => Assert.Equal("Passed", p.Status));
            var trait = Assert.Single(result.Traits, t => t.Trait == "CodeFixtureEditReliability");
            Assert.Equal(10, trait.Samples);
            // Fixture edits never establish operational mutation or native tool-call safety.
            Assert.DoesNotContain(result.Traits, t => t.Trait == "FileMutationReliability");
            using var store = new SqliteModelQualificationStore(Path.Combine(directory, "user.db"));
            var model = new ModelDefinition("fixture-model", "local", 8192, 8192, 2048);
            var key = ModelQualificationHost.QualificationKeyFor(model, null);
            var profile = store.Get(key, CancellationToken.None)!;
            Assert.Equal(FullProbeSuite.SuiteId, profile.SuiteId);
            Assert.Equal(FullProbeSuite.SuiteVersion, profile.SuiteVersion);
            var evidence = store.Evidence(key, profile.ProfileRevision, CancellationToken.None)!;
            using var json = System.Text.Json.JsonDocument.Parse(new FileArtifactStore(directory).GetText(evidence.Artifact.Hash)!);
            Assert.Equal(FullProbeSuite.SuiteId, json.RootElement.GetProperty("benchmarkIdentity").GetProperty("suiteId").GetString());
            Assert.Equal(ProbeScorer.TaskSetHash(probes), json.RootElement.GetProperty("benchmarkIdentity").GetProperty("taskSetHash").GetString());
        }
        finally
        {
            using var connection = new SqliteConnection("DataSource=" + Path.Combine(directory, "user.db"));
            SqliteConnection.ClearPool(connection);
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RejectingValidator : IProbeFixtureValidator
    {
        public int Calls { get; private set; }
        public ValueTask<ProbeFixtureValidation> ValidateAsync(Probe probe, string output, CancellationToken token)
        { Calls++; return ValueTask.FromResult(new ProbeFixtureValidation(false, "pinned test failed")); }
    }

    private sealed class Provider(IReadOnlyList<Probe> probes, string? wrong = null) : IModelProvider, IModelRequestAttemptBound
    {
        public int Calls { get; private set; }
        public long? MaximumGenerationRequestAttempts => 1;
        public ProviderCapabilities Capabilities => new(true, false, false);
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++; await Task.Yield();
            var prompt = ((TextBlock)request.Messages[0].Content[0]).Text;
            yield return new ResponseCompleted(new ModelResponse([new TextBlock(wrong ?? probes.Single(p => p.Prompt == prompt).Expected)],
                StopReason.EndTurn, new TokenUsage(10, 5, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture-model", null)));
        }
    }
}