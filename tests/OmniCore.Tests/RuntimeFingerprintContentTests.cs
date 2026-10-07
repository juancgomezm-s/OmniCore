namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Tools;

public sealed class RuntimeFingerprintContentTests
{
    [Fact]
    public void Turn_instruction_intent_is_exact_fingerprinted_and_not_inherited_by_legacy_turns()
    {
        WithDirectory(root =>
        {
            var artifacts = new FileArtifactStore(root);
            var catalog = new FakeCatalog();
            var baseline = new ExecutionFingerprint("model", "h", "t", "c", "o", "build");
            ExecutionFingerprint Apply(ExecutionFingerprint fingerprint, TurnInstructionSnapshot? snapshot) =>
                RuntimeFingerprintFactory.WithTurnConfiguration(fingerprint, catalog, [], "system", null,
                    artifacts, instructionSnapshot: snapshot);
            var original = Apply(baseline, new TurnInstructionSnapshot(true, "system"));
            var component = Assert.Single(original.Components, part => part.Name == "turn.instruction");
            Assert.NotNull(component.Content);
            Assert.True(artifacts.Verify(component.Hash, component.Content.Size));
            using var json = System.Text.Json.JsonDocument.Parse(artifacts.GetText(component.Hash)!);
            Assert.True(json.RootElement.GetProperty("conversationOnly").GetBoolean());
            Assert.Equal("system", json.RootElement.GetProperty("resolvedInstruction").GetString());
            Assert.Equal(original.Hash(), Apply(original, new TurnInstructionSnapshot(true, "system")).Hash());
            Assert.NotEqual(original.Hash(), Apply(baseline, new TurnInstructionSnapshot(false, "system")).Hash());
            Assert.NotEqual(original.Hash(), Apply(baseline, new TurnInstructionSnapshot(true, "changed")).Hash());
            Assert.Equal(Apply(baseline, null).Hash(), Apply(original, null).Hash());
            Assert.DoesNotContain(Apply(original, null).Components, part => part.Name == "turn.instruction");
        });
    }

    [Fact]
    public void Reasoning_resolution_fingerprints_requested_applied_source_revisions_reserve_and_reductions()
    {
        var requested = new ReasoningRequest("fixture-effort", null);
        var applied = new ReasoningRequest("budget", 1024);
        ReasoningResolution Resolution(long revision = 1, int reserve = 512,
            ReasoningReduction reduction = ReasoningReduction.Capability) => new(requested, applied,
                ReasoningSelectionSource.UltraCode, modeAuthorityRevision: revision,
                outputReserveTokens: reserve, reductions: [reduction]);
        ExecutionFingerprint Create(ReasoningResolution? resolution)
        {
            var model = new ModelDefinition("fixture-model", "fixture-provider", 8192, 7000, 4096);
            var profile = new ModelProfileResolver().Resolve(model, null);
            return RuntimeFingerprintFactory.Create(model, profile, new HarnessPolicyResolver().Resolve(profile),
                new ModelSelection(new ModelIdValue(model.Id), 7000, ToolMode.Direct,
                    resolution?.AppliedRequest, reasoningResolution: resolution), "h", "c", "p", "t");
        }
        Assert.DoesNotContain(Create(null).Components, component => component.Name == "model.reasoning.resolved");
        var original = Create(Resolution());
        var component = Assert.Single(original.Components, part => part.Name == "model.reasoning.resolved");
        Assert.Equal(original.Hash(), Create(Resolution()).Hash());
        foreach (var changed in new[]
        {
            Resolution(revision: 2), Resolution(reserve: 1024), Resolution(reduction: ReasoningReduction.ContextCapacity),
            new ReasoningResolution(requested, applied, ReasoningSelectionSource.RunOverride,
                runPreferenceRevision: 1, reductions: [ReasoningReduction.Capability]),
            new ReasoningResolution(new ReasoningRequest("fixture-other", null), applied,
                ReasoningSelectionSource.UltraCode, modeAuthorityRevision: 1, outputReserveTokens: 512,
                reductions: [ReasoningReduction.Capability]),
        })
        {
            var fingerprint = Create(changed);
            Assert.NotEqual(component.Hash, Assert.Single(fingerprint.Components,
                part => part.Name == component.Name).Hash);
            Assert.All(original.Components.Where(part => part.Name != component.Name),
                part => Assert.Equal(part.Hash, Assert.Single(fingerprint.Components, candidate => candidate.Name == part.Name).Hash));
        }
    }

    [Fact]
    public void Explicit_UltraCode_budget_and_output_reserve_are_fingerprinted_without_upgrading_legacy_capabilities()
    {
        static ExecutionFingerprint Create(int? budget, int? reserve)
        {
            var model = new ModelDefinition("fixture-model", "fixture-provider", 8192, 7000, 4096,
                reasoningCapability: new ReasoningCapability(true, ["budget"],
                    ReasoningReplayPolicy.PreserveAcrossSteps, budget, reserve));
            var profile = new ModelProfileResolver().Resolve(model, null);
            return RuntimeFingerprintFactory.Create(model, profile, new HarnessPolicyResolver().Resolve(profile),
                new ModelSelection(new ModelIdValue(model.Id), 7000, ToolMode.Direct, null), "h", "c", "p", "t");
        }
        var legacy = Assert.Single(Create(null, null).Components, component => component.Name == "model.reasoning.declared");
        Assert.Equal("1", legacy.Version);
        var first = Create(1024, 512);
        var configured = Assert.Single(first.Components, component => component.Name == "model.reasoning.declared");
        Assert.Equal("2", configured.Version);
        foreach (var changed in new[] { Create(2048, 512), Create(1024, 1024) })
        {
            Assert.NotEqual(configured.Hash, Assert.Single(changed.Components,
                component => component.Name == "model.reasoning.declared").Hash);
            foreach (var other in first.Components.Where(component => component.Name != "model.reasoning.declared"))
                Assert.Equal(other.Hash, Assert.Single(changed.Components, component => component.Name == other.Name).Hash);
        }
    }

    [Fact]
    public void Prepared_components_have_exact_future_refs_without_publishing_or_changing_configuration_hash()
    {
        WithDirectory(root =>
        {
            var store = new FileArtifactStore(root);
            var model = new ModelDefinition("fixture-model", "fixture-provider", 8192, 7000, 1024);
            var profile = new ModelProfileResolver().Resolve(model, null);
            var harness = new HarnessPolicyResolver().Resolve(profile);
            var selection = new ModelSelection(new ModelIdValue(model.Id), 7000, ToolMode.Direct, null);
            var hashOnly = RuntimeFingerprintFactory.Create(model, profile, harness, selection,
                "harness", "context", "policy", "counter");
            var prepared = RuntimeFingerprintFactory.Prepare(model, profile, harness, selection,
                "harness", "context", "policy", "counter", artifacts: store);
            Assert.Equal(hashOnly.Hash(), prepared.Fingerprint.Hash());
            Assert.NotEmpty(prepared.Artifacts);
            Assert.False(Directory.Exists(Path.Combine(root, "blobs")));
            foreach (var component in prepared.Fingerprint.Components)
            {
                Assert.NotNull(component.Content);
                var handle = Assert.Single(prepared.Artifacts, item => item.Reference == component.Content);
                Assert.Equal(component.Hash, handle.Reference.Hash);
                Assert.False(store.Verify(handle.Reference.Hash, handle.Reference.Size));
                using (store.AcquirePublicationLease(CancellationToken.None))
                    Assert.Equal(component.Content, handle.Publish());
                Assert.True(store.Verify(handle.Reference.Hash, handle.Reference.Size));
            }
        });
    }

    [Fact]
    public void Prepared_redacted_component_has_no_exact_ref_or_pending_publication()
    {
        WithDirectory(root =>
        {
            const string secret = "private-fixture-value";
            var store = new FileArtifactStore(root, new FixtureRedactor(secret));
            var catalog = new FakeCatalog();
            var baseline = new ExecutionFingerprint("model", "harness", "tools", "context", "none", "build");
            var prepared = RuntimeFingerprintFactory.PrepareTurnConfiguration(baseline, catalog,
                catalog.Definitions(), secret, null, store);
            var prompt = Assert.Single(prepared.Fingerprint.Components, item => item.Name == "prompt.template");
            Assert.Null(prompt.Content);
            Assert.DoesNotContain(prepared.Artifacts, item => item.Reference.Hash == prompt.Hash);
            Assert.All(prepared.Artifacts, item => Assert.False(item.Reference.Redacted));
            Assert.False(Directory.Exists(Path.Combine(root, "blobs")));
        });
    }

    [Fact]
    public void Resolved_components_are_exact_deduplicated_and_do_not_change_configuration_hash()
    {
        WithDirectory(root =>
        {
            var store = new FileArtifactStore(root);
            var model = new ModelDefinition("fixture-model", "fixture-provider", 8192, 7000, 1024);
            var route = new ModelRoute(model.ProviderId, "http://private-fixture.invalid/v1",
                ProviderFamily.OpenAiChatCompatible, null, model.Id);
            var profile = new ModelProfileResolver().Resolve(model, null, route: route);
            var harness = new HarnessPolicyResolver().Resolve(profile);
            var selection = new ModelSelection(new ModelIdValue(model.Id), 7000, ToolMode.Direct, null, route.Id, route);
            ExecutionFingerprint Create(IArtifactStore? artifacts) => RuntimeFingerprintFactory.Create(model, profile,
                harness, selection, "harness", "context", "policy", "counter", artifacts: artifacts);
            var hashOnly = Create(null);
            var first = Create(store);
            var second = Create(store);
            Assert.Equal(hashOnly.Hash(), first.Hash());
            Assert.Equal(first.Hash(), second.Hash());
            Assert.NotNull(Assert.Single(first.Components, c => c.Name == "provider.adapter").Content);
            foreach (var component in first.Components)
            {
                Assert.NotNull(component.Content);
                Assert.False(component.Content.Redacted);
                Assert.Equal(component.Hash, component.Content.Hash);
                Assert.True(store.Verify(component.Hash, component.Content.Size));
                Assert.Equal(component.Content.Hash, Assert.Single(second.Components, c => c.Name == component.Name).Content!.Hash);
                if (component.Name == "provider.adapter")
                    Assert.Contains("private-fixture.invalid", store.GetText(component.Hash)!);
                else Assert.DoesNotContain("private-fixture.invalid", store.GetText(component.Hash)!);
            }
            Assert.Equal(6, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Count(path =>
                Path.GetFileName(path).Length == 64));
        });
    }

    [Fact]
    public void Redacted_component_is_not_claimed_as_exact_configuration_and_secret_is_not_persisted()
    {
        WithDirectory(root =>
        {
            const string secret = "private-fixture-value";
            var store = new FileArtifactStore(root, new FixtureRedactor(secret));
            var catalog = new FakeCatalog().Add(FakeTool.Read("fixture.inspect"));
            var baseline = new ExecutionFingerprint("model", "harness", "tools", "context", "none", "build");
            var hashOnly = RuntimeFingerprintFactory.WithTurnConfiguration(baseline, catalog, catalog.Definitions(), secret, null);
            var recorded = RuntimeFingerprintFactory.WithTurnConfiguration(baseline, catalog, catalog.Definitions(), secret, null, store);
            Assert.Equal(hashOnly.Hash(), recorded.Hash());
            Assert.Null(Assert.Single(recorded.Components, c => c.Name == "prompt.template").Content);
            Assert.NotNull(Assert.Single(recorded.Components, c => c.Name == "tools.plan").Content);
            var blobs = Directory.GetFiles(Path.Combine(root, "blobs"), "*", SearchOption.AllDirectories);
            Assert.NotEmpty(blobs);
            Assert.All(blobs, path => Assert.DoesNotContain(secret, File.ReadAllText(path)));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_exact_artifact_fails_closed(bool wrongHash)
    {
        var catalog = new FakeCatalog().Add(FakeTool.Read("fixture.inspect"));
        var baseline = new ExecutionFingerprint("model", "harness", "tools", "context", "none", "build");
        Assert.Throws<InvalidDataException>(() => RuntimeFingerprintFactory.WithTurnConfiguration(baseline, catalog,
            catalog.Definitions(), "system", null, new InvalidArtifactStore(wrongHash)));
    }

    private static void WithDirectory(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-fingerprint-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { test(root); }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FixtureRedactor(string secret) : ISecretRedactor
    {
        public void RegisterSecret(string value) => throw new NotSupportedException("Fixed fixture only.");
        public string Redact(string input) => input.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
    }

    private sealed class InvalidArtifactStore(bool wrongHash) : IArtifactStore
    {
        public ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity)
        {
            var hash = wrongHash ? new string('a', 64) : Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));
            return new ArtifactRef(ArtifactId.New(), ContentHash.Sha256(hash),
                System.Text.Encoding.UTF8.GetByteCount(content), mediaType, kind, sensitivity);
        }
        public bool Verify(ContentHash hash, long size) => false;
        public string? GetText(ContentHash hash) => null;
    }
}
