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
            Assert.Null(Assert.Single(first.Components, c => c.Name == "provider.adapter").Content);
            foreach (var component in first.Components.Where(c => c.Name != "provider.adapter"))
            {
                Assert.NotNull(component.Content);
                Assert.False(component.Content.Redacted);
                Assert.Equal(component.Hash, component.Content.Hash);
                Assert.True(store.Verify(component.Hash, component.Content.Size));
                Assert.Equal(component.Content.Hash, Assert.Single(second.Components, c => c.Name == component.Name).Content!.Hash);
                Assert.DoesNotContain("private-fixture.invalid", store.GetText(component.Hash)!);
            }
            Assert.Equal(5, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Count(path =>
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
