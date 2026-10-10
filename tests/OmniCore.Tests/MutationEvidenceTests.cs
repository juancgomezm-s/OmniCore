using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// ADR-0044 §4 / ADR-0007 §2: la FileMutationReliability se mide en uso real. El ledger observa las
/// mutaciones, el agregador las suma al perfil de cualificación y el onboarding recomienda con ellas.
/// </summary>
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class MutationEvidenceTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-mutation-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static FileMutationPolicy Policy(FileMutationMode mode, int maxFiles = 5, int maxLines = 600,
        double ratio = 0.5) =>
        new(mode, DestructiveActionPolicy.Deny, DestructiveActionPolicy.Deny, maxFiles, maxLines, ratio,
            requirePriorRead: true, requireExpectedVersionToken: true, requirePostEditValidation: true,
            allowParallelMutations: false);

    private static MutationLedger Ledger(FileMutationPolicy policy)
    {
        var registry = new FileReadRegistry();
        registry.Ledger.Bind(policy);
        return registry.Ledger;
    }

    [Fact]
    public void Successful_mutations_are_observed_as_patch_or_replace_with_their_diff_size()
    {
        var ledger = Ledger(Policy(FileMutationMode.Full));
        ledger.RecordMutation("a.cs", 2, 3, ToolCallId.New(), ModelToolCapability.PatchExisting, 20);
        ledger.RecordMutation("b.cs", 4, 4, ToolCallId.New(), ModelToolCapability.ReplaceFile, 10);
        ledger.RecordMutation("c.cs", 0, 7, ToolCallId.New(), ModelToolCapability.CreateFile);

        var samples = ledger.ObservedSamples();

        Assert.Equal(new FileMutationSample("a.cs", 20, 5, true, true, true, true, 0, false), samples[0]);
        Assert.False(samples[1].PatchPreferred);
        Assert.Equal((10, 8), (samples[1].OriginalLines, samples[1].ChangedLines));
        Assert.True(samples[2].PatchPreferred);
        Assert.True(FileMutationEvaluator.SampleValue(samples[1]) < FileMutationEvaluator.SampleValue(samples[0]));
    }

    [Fact]
    public void Refusals_are_observed_by_what_the_model_tried_and_allowed_calls_leave_no_sample()
    {
        var ledger = Ledger(Policy(FileMutationMode.PatchExisting, maxFiles: 1, maxLines: 10, ratio: 0.25));

        Assert.NotNull(ledger.RefuseMutation(ModelToolCapability.ReplaceFile, "a.cs", true, 1, 1, 8)); // modo
        Assert.NotNull(ledger.RefuseMutation(ModelToolCapability.PatchExisting, "b.cs", true, 1, 11, 100)); // líneas
        Assert.NotNull(ledger.RefuseMutation(ModelToolCapability.PatchExisting, "c.cs", true, 6, 0, 8)); // ratio
        Assert.Null(ledger.RefuseMutation(ModelToolCapability.PatchExisting, "d.cs", true, 1, 1, 100));

        var samples = ledger.ObservedSamples();
        Assert.Equal(3, samples.Count);
        Assert.False(samples[0].PatchPreferred);
        Assert.False(samples[0].WithinScope);
        Assert.False(samples[1].WithinScope);
        Assert.True(samples[1].UnrelatedContentPreserved);
        Assert.False(samples[2].UnrelatedContentPreserved);
        Assert.True(samples[2].WithinScope);
    }

    [Fact]
    public void Token_violations_and_validation_breaks_are_attributed_to_the_edit_that_caused_them()
    {
        var ledger = Ledger(Policy(FileMutationMode.PatchAndCreate));
        var first = ToolCallId.New();
        var second = ToolCallId.New();
        ledger.RecordTokenViolation("old.cs", ModelToolCapability.PatchExisting);
        ledger.RecordMutation("a.cs", 1, 1, first, ModelToolCapability.PatchExisting, 10);
        ledger.RecordMutation("b.cs", 1, 1, second, ModelToolCapability.PatchExisting, 10);

        var pending = ledger.PendingValidations();
        ledger.RecordValidationBreak(new[] { pending[0] });
        ledger.RecordValidationBreak(new[] { pending[0] });

        var samples = ledger.ObservedSamples();
        Assert.False(samples[0].VersionTokenRespected);
        Assert.Equal(2, samples[1].BreakCount);
        Assert.Equal(0, samples[2].BreakCount);
        // La rotura de otra edición no altera una muestra de rechazo (no publicada).
        Assert.Equal(0, samples[0].BreakCount);
    }

    [Fact]
    public void Taking_the_samples_drains_them_so_each_one_is_aggregated_once()
    {
        var ledger = Ledger(Policy(FileMutationMode.Full));
        ledger.RecordMutation("a.cs", 1, 1, ToolCallId.New(), ModelToolCapability.PatchExisting, 10);

        Assert.Single(ledger.TakeObservedSamples());
        Assert.Empty(ledger.TakeObservedSamples());
        Assert.Empty(ledger.ObservedSamples());
    }

    // ---------------------------------------------------------------- pipeline real de tools

    private static ScriptedToolExecutor Pipeline(string workspace, ModelCapabilityBoundary boundary)
    {
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(), includeMutationTools: true);
        var policy = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
        {
            ["filesystem.read"] = PermissionDecision.Allow,
            ["filesystem.write"] = PermissionDecision.Allow,
            ["filesystem.patch"] = PermissionDecision.Allow,
        });
        return ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(), policy, workspace, boundary);
    }

    private static ModelCapabilityBoundary FullAgentBoundary()
    {
        var key = ModelPolicyKey.For("p", "m");
        return new ModelCapabilityBoundary(EffectiveModelPolicy.Resolve(key,
            new StoredModelPolicy(key, 1, ModelPolicyPresets.For(ModelPolicyCategory.FullAgent),
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 8, GuidanceLevel.Full, 3,
                PlanControl.ModelDriven, 8)));
    }

    private static ValidatedToolCall Call(string tool, Dictionary<string, string?> arguments) =>
        new(ToolCallId.New(), new ToolId(tool), "pc-" + tool, JsonSerializer.Serialize(arguments));

    private static string Version(string content) => FilesystemPatchTool.VersionToken(Encoding.UTF8.GetBytes(content));

    [Fact]
    public void Real_tools_report_patch_replace_and_stale_token_to_the_ledger()
    {
        var workspace = TempDir();
        try
        {
            var original = string.Join("\n", Enumerable.Range(1, 8).Select(i => "linea-" + i)) + "\n";
            File.WriteAllText(Path.Combine(workspace, "doc.txt"), original);
            var boundary = FullAgentBoundary();
            var executor = Pipeline(workspace, boundary);
            var ct = TestContext.Current.CancellationToken;

            var read = executor.ExecuteToolWithoutJournal(
                Call("filesystem.read", new() { ["path"] = "doc.txt" }), true, ct);
            Assert.True(read.Succeeded, read.Summary);

            // Un parche localizado con el token real.
            var patched = executor.ExecuteToolWithoutJournal(Call("filesystem.patch", new()
            {
                ["path"] = "doc.txt", ["expectedVersion"] = Version(original), ["oldText"] = "linea-2", ["newText"] = "dos",
            }), true, ct);
            Assert.True(patched.Succeeded, patched.Summary);

            // Un parche con el token anterior, ya obsoleto.
            var stale = executor.ExecuteToolWithoutJournal(Call("filesystem.patch", new()
            {
                ["path"] = "doc.txt", ["expectedVersion"] = Version(original), ["oldText"] = "linea-3", ["newText"] = "tres",
            }), true, ct);
            Assert.False(stale.Succeeded);
            Assert.StartsWith("STALE_WRITE", stale.Summary);

            // Un reemplazo completo con el token correcto, tras leer otra vez.
            var current = File.ReadAllText(Path.Combine(workspace, "doc.txt"));
            executor.ExecuteToolWithoutJournal(Call("filesystem.read", new() { ["path"] = "doc.txt" }), true, ct);
            var replaced = executor.ExecuteToolWithoutJournal(Call("filesystem.write", new()
            {
                ["path"] = "doc.txt", ["content"] = current + "nueva\n", ["expectedVersion"] = Version(current),
            }), true, ct);
            Assert.True(replaced.Succeeded, replaced.Summary);

            var samples = boundary.ReadRegistry().Ledger.ObservedSamples();
            Assert.Equal(3, samples.Count);
            Assert.True(samples[0].PatchPreferred);
            Assert.Equal((8, 2), (samples[0].OriginalLines, samples[0].ChangedLines));
            Assert.False(samples[1].VersionTokenRespected);
            Assert.False(samples[2].PatchPreferred);
        }
        finally
        {
            try { Directory.Delete(workspace, true); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- agregación en el perfil

    private static (SqliteModelQualificationStore Store, ModelQualificationKey Key, string Directory) Profile(
        ModelQualificationState state, params ModelTraitRecord[] extra)
    {
        var directory = TempDir();
        var key = new ModelQualificationKey("p", "m", null, null, Array.Empty<string>(), null, null, null,
            "default", ToolCallFormat.Native, ToolMode.Direct, "v1");
        var store = OmniHost.CreateModelQualificationStore(directory);
        var profile = store.Upsert(key, 0, state, "fixture-quick", "1.0.0", TestContext.Current.CancellationToken);
        var traits = new List<ModelTraitRecord>
        {
            new(key.QualificationKeyHash(), profile.ProfileRevision, "InstructionFollowing", 1.0, 1.0, 10, "fixture"),
            new(key.QualificationKeyHash(), profile.ProfileRevision, "StructuredOutputReliability", 1.0, 1.0, 10, "fixture"),
        };
        traits.AddRange(extra);
        store.SaveTraits(key, profile.ProfileRevision, traits, TestContext.Current.CancellationToken);
        return (store, key, directory);
    }

    private static FileMutationSample Good() => new("a.cs", 100, 4, true, true, true, true, 0, false);
    private static FileMutationSample Bad() => new("b.cs", 100, 90, false, false, false, false, 2, true);

    [Fact]
    public void Recorder_aggregates_samples_into_the_profile_trait_across_runs_and_keeps_the_other_traits()
    {
        var (store, key, directory) = Profile(ModelQualificationState.Qualified);
        using (store)
        {
            var ct = TestContext.Current.CancellationToken;
            var first = MutationEvidenceRecorder.Record(store, key, [Good(), Good()], DateTimeOffset.UnixEpoch, ct)!;
            Assert.Equal(2, first.SampleSize);
            var second = MutationEvidenceRecorder.Record(store, key, [Bad()], DateTimeOffset.UnixEpoch, ct)!;
            Assert.Equal(3, second.SampleSize);

            var traits = store.Traits(key, store.Get(key, ct)!.ProfileRevision, ct);
            var stored = Assert.Single(traits, trait => trait.Trait == MutationEvidenceRecorder.TraitName);
            Assert.Equal(3, stored.Samples);
            Assert.Equal(MutationEvidenceRecorder.Source, stored.Source);
            var expected = (2 * FileMutationEvaluator.SampleValue(Good()) + FileMutationEvaluator.SampleValue(Bad())) / 3;
            Assert.Equal(expected, stored.Value, 6);
            Assert.Equal(0.3, stored.Confidence, 6); // 3 muestras de 10 para confianza total
            Assert.Contains(traits, trait => trait.Trait == "InstructionFollowing" && trait.Samples == 10);
        }
        Directory.Delete(directory, true);
    }

    [Theory]
    [InlineData(ModelQualificationState.Unknown)]
    [InlineData(ModelQualificationState.Declared)]
    [InlineData(ModelQualificationState.ProvisionallyClassified)]
    public void Recorder_needs_a_usable_qualification_profile_to_anchor_the_evidence(ModelQualificationState state)
    {
        var (store, key, directory) = Profile(state);
        using (store)
        {
            Assert.Null(MutationEvidenceRecorder.Record(store, key, [Good()], DateTimeOffset.UnixEpoch,
                TestContext.Current.CancellationToken));
            var revision = store.Get(key, TestContext.Current.CancellationToken)!.ProfileRevision;
            Assert.DoesNotContain(store.Traits(key, revision, TestContext.Current.CancellationToken),
                trait => trait.Trait == MutationEvidenceRecorder.TraitName);

            var unknown = new ModelQualificationKey("p", "never-qualified", null, null, Array.Empty<string>(), null,
                null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1");
            Assert.Null(MutationEvidenceRecorder.Record(store, unknown, [Good()], DateTimeOffset.UnixEpoch,
                TestContext.Current.CancellationToken));
        }
        Directory.Delete(directory, true);
    }

    // ---------------------------------------------------------------- recomendación y onboarding

    private static IReadOnlyDictionary<string, double> Quick(double? mutation = null)
    {
        var map = new Dictionary<string, double>
        {
            ["InstructionFollowing"] = 1.0,
            ["StructuredOutputReliability"] = 1.0,
        };
        if (mutation is { } value) map["FileMutationReliability"] = value;
        return map;
    }

    [Fact]
    public void The_recommendation_ignores_file_mutation_evidence_below_the_minimum_sample_count()
    {
        var thin = QualificationRecommender.Recommend(Quick(0.1),
            QualificationRecommender.MinimumFileMutationSamples - 1);
        Assert.Equal(ModelPolicyCategory.PatchOnly, thin.Category);
        Assert.Contains(thin.Notes, note => note.Contains("evidencia insuficiente", StringComparison.Ordinal));

        var enough = QualificationRecommender.Recommend(Quick(0.1), QualificationRecommender.MinimumFileMutationSamples);
        Assert.Equal(ModelPolicyCategory.ObserveOnly, enough.Category);

        var strong = QualificationRecommender.Recommend(Quick(0.95), QualificationRecommender.MinimumFileMutationSamples);
        Assert.Equal(ModelPolicyCategory.ScopedCoder, strong.Category);
    }

    [Fact]
    public void Onboarding_recommends_from_the_stored_qualification_and_degrades_without_it()
    {
        var directory = TempDir();
        var priorConfig = Environment.GetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable);
        try
        {
            var config = Path.Combine(directory, "config");
            Directory.CreateDirectory(config);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, config);
            File.WriteAllText(Path.Combine(config, "providers.yaml"),
                "providers:\n  scripted: { family: OpenAiChatCompatible, baseUrl: http://127.0.0.1:1/v1, auth: none, billingMode: Local }\n");
            File.WriteAllText(Path.Combine(config, "models.yaml"),
                "models:\n  scripted-model: { provider: scripted, context: 8192, maxOutput: 2048 }\n");
            var registry = OmniHost.LoadUserConfiguration(config).Registry;
            var model = registry.Model("scripted-model")!;
            var key = ModelQualificationHost.QualificationKeyFor(model, registry.Provider("scripted"));
            var policyKey = ModelPolicyKey.For("scripted", "scripted-model");
            var ct = TestContext.Current.CancellationToken;

            using (var service = OmniHost.CreateModelPolicyService(directory))
            {
                var without = service.Draft(policyKey, ct);
                Assert.False(without.HasQualificationEvidence);
                Assert.Equal(ModelPolicyCategory.ObserveOnly, without.RecommendedCategory);
            }

            using (var store = OmniHost.CreateModelQualificationStore(directory))
            {
                var profile = store.Upsert(key, 0, ModelQualificationState.Qualified, "quick", "1.0.0", ct);
                var hash = key.QualificationKeyHash();
                store.SaveTraits(key, profile.ProfileRevision, new[]
                {
                    new ModelTraitRecord(hash, profile.ProfileRevision, "InstructionFollowing", 1.0, 1.0, 10, "empirical"),
                    new ModelTraitRecord(hash, profile.ProfileRevision, "StructuredOutputReliability", 1.0, 1.0, 10, "empirical"),
                }, ct);
            }

            using (var service = OmniHost.CreateModelPolicyService(directory))
            {
                var qualified = service.Draft(policyKey, ct);
                Assert.True(qualified.HasQualificationEvidence);
                Assert.Equal(ModelPolicyCategory.PatchOnly, qualified.RecommendedCategory);
                Assert.Contains(qualified.Warnings, warning => warning.Contains("FileMutationReliability", StringComparison.Ordinal));
            }

            using (var store = OmniHost.CreateModelQualificationStore(directory))
                MutationEvidenceRecorder.Record(store, key,
                    Enumerable.Repeat(Good(), QualificationRecommender.MinimumFileMutationSamples).ToArray(),
                    DateTimeOffset.UnixEpoch, ct);
            using (var service = OmniHost.CreateModelPolicyService(directory))
                Assert.Equal(ModelPolicyCategory.ScopedCoder, service.Draft(policyKey, ct).RecommendedCategory);

            using (var store = OmniHost.CreateModelQualificationStore(directory))
                MutationEvidenceRecorder.Record(store, key,
                    Enumerable.Repeat(Bad(), 30).ToArray(), DateTimeOffset.UnixEpoch, ct);
            using (var service = OmniHost.CreateModelPolicyService(directory))
                Assert.Equal(ModelPolicyCategory.ObserveOnly, service.Draft(policyKey, ct).RecommendedCategory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, priorConfig);
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
