// Synthetic provider fixtures with real private SQLite/CAS; no authenticated provider queries.
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class ProviderAdapterContentTests
{
    private const string Prompt =
        "Contexto del workspace (fuentes del run):\nfixture instruction\nFingerprint: fixture-model · context";
    private const string Endpoint = "https://private-fixture.invalid/v1";

    [Fact]
    public void Provider_adapter_v2_content_is_exact_prepared_and_published_without_hash_drift()
    {
        var root = NewDirectory("omni-provider-adapter-content-");
        var eagerRoot = NewDirectory("omni-provider-adapter-eager-");
        try
        {
            var provider = new FixtureAdapter();
            var model = Model();
            var route = Route(Endpoint);
            var hashOnly = CreateFingerprint(model, route, provider, artifacts: null);

            var store = new FileArtifactStore(root);
            var prepared = Prepare(model, route, provider, store);
            var adapter = Assert.Single(prepared.Fingerprint.Components,
                component => component.Name == "provider.adapter");
            var expectedJson = AdapterJson(route, provider);
            var adapterRef = Assert.IsType<ArtifactRef>(adapter.Content);
            var handle = Assert.Single(prepared.Artifacts, item => item.Reference == adapterRef);

            Assert.Equal("2", adapter.Version);
            Assert.Equal(hashOnly.Hash(), prepared.Fingerprint.Hash());
            Assert.Equal(ContentHash.Sha256(Digest(expectedJson)), adapter.Hash);
            Assert.Equal(adapter.Hash, adapterRef.Hash);
            Assert.Equal("application/vnd.omnicore.fingerprint-component+json", handle.Reference.MediaType);
            Assert.Equal(ArtifactKind.Other, handle.Reference.Kind);
            Assert.Equal(Sensitivity.Sensitive, handle.Reference.Sensitivity);
            Assert.False(handle.Reference.Redacted);
            Assert.False(store.Verify(handle.Reference.Hash, handle.Reference.Size));
            Assert.Null(store.GetText(handle.Reference.Hash));
            Assert.Empty(Blobs(root)); // Prepare must not publish before the caller's short lease

            using (store.AcquirePublicationLease(CancellationToken.None))
                Assert.Equal(handle.Reference, handle.Publish());

            Assert.True(store.Verify(adapterRef.Hash, adapterRef.Size));
            Assert.Equal(expectedJson, store.GetText(adapterRef.Hash));
            var repeated = Prepare(model, route, provider, store);
            var repeatedAdapter = Part(repeated.Fingerprint, "provider.adapter");
            Assert.Equal(adapter.Hash, repeatedAdapter.Hash);
            using (store.AcquirePublicationLease(CancellationToken.None))
                Assert.Single(repeated.Artifacts, item => item.Reference == repeatedAdapter.Content).Publish();
            Assert.Single(Blobs(root)); // Re-preparing the same exact component deduplicates bytes.
            var reopenedStore = new FileArtifactStore(root);
            Assert.True(reopenedStore.Verify(adapterRef.Hash, adapterRef.Size));
            Assert.Equal(expectedJson, reopenedStore.GetText(adapterRef.Hash));

            // The eager compatibility API gets the same canonical component and fingerprint,
            // while retaining its existing eager-write behavior.
            var eagerStore = new FileArtifactStore(eagerRoot);
            var eager = CreateFingerprint(model, route, provider, eagerStore);
            var eagerAdapter = Assert.Single(eager.Components, component => component.Name == "provider.adapter");
            var eagerRef = Assert.IsType<ArtifactRef>(eagerAdapter.Content);
            Assert.Equal(hashOnly.Hash(), eager.Hash());
            Assert.Equal(adapter.Hash, eagerAdapter.Hash);
            Assert.Equal(adapterRef.Hash, eagerRef.Hash);
            Assert.Equal(adapterRef.Size, eagerRef.Size);
            Assert.True(eagerStore.Verify(eagerRef.Hash, eagerRef.Size));
            Assert.Equal(expectedJson, eagerStore.GetText(eagerRef.Hash));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(eagerRoot, recursive: true);
        }
    }

    [Fact]
    public void Redacted_synthetic_endpoint_keeps_original_digest_but_has_no_exact_content_ref_or_handle()
    {
        var root = NewDirectory("omni-provider-adapter-redacted-");
        try
        {
            const string sentinel = "synthetic-endpoint-secret-9d6a";
            var endpoint = "https://fixture.invalid/" + sentinel + "/v1";
            var provider = new FixtureAdapter();
            var model = Model();
            var route = Route(endpoint);
            var hashOnly = CreateFingerprint(model, route, provider, artifacts: null);
            var redactor = new SyntheticRedactor(sentinel);
            var store = new FileArtifactStore(root, redactor);
            var prepared = Prepare(model, route, provider, store);
            var component = Assert.Single(prepared.Fingerprint.Components,
                candidate => candidate.Name == "provider.adapter");

            Assert.Equal(hashOnly.Hash(), prepared.Fingerprint.Hash());
            Assert.Equal(Part(hashOnly, "provider.adapter").Hash, component.Hash);
            Assert.Null(component.Content);
            Assert.True(redactor.SawSentinel);
            Assert.DoesNotContain(prepared.Artifacts, item => item.Reference.Hash == component.Hash);
            Assert.Empty(Blobs(root));

            // Publish only the other exact components, as ExplorerTurn does under its lease.
            using (store.AcquirePublicationLease(CancellationToken.None))
                foreach (var artifact in prepared.Artifacts)
                    Assert.Equal(artifact.Reference, artifact.Publish());
            Assert.All(Blobs(root), path =>
                Assert.DoesNotContain(sentinel, File.ReadAllText(path), StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Legacy_resume_without_adapter_content_preserves_original_fingerprint_and_does_not_publish_new_ref()
    {
        using var fixture = new TurnFixture();
        var opened = fixture.OpenRun();
        var baseline = fixture.PrepareBaseline(Endpoint);
        var turnConfiguration = fixture.PrepareTurnConfiguration(baseline, fixture.ProfileFor(opened));
        var current = turnConfiguration.Fingerprint;
        var currentAdapter = Part(current, "provider.adapter");
        var currentAdapterRef = Assert.IsType<ArtifactRef>(currentAdapter.Content);

        var legacyComponents = current.Components.Select(component => component.Name == "provider.adapter"
            ? component with { Content = null }
            : component).ToArray();
        var legacy = new ExecutionFingerprint(current.ModelKey, current.HarnessPolicyHash, current.ToolkitHash,
            current.ContextPolicyHash, current.OverridesHash, current.Build, current.ModelPolicyHash,
            current.TokenizerHash, legacyComponents);
        Assert.Equal(current.Hash(), legacy.Hash()); // Content is explanatory, not aggregate identity

        var referenced = legacy.Components.Where(component => component.Content is not null)
            .Select(component => component.Content!).ToHashSet();
        using (fixture.Artifacts.AcquirePublicationLease(CancellationToken.None))
        {
            foreach (var handle in baseline.Artifacts.Concat(turnConfiguration.Artifacts)
                .Where(handle => referenced.Contains(handle.Reference))
                .DistinctBy(handle => handle.Reference.Hash))
                Assert.Equal(handle.Reference, handle.Publish());
            fixture.Stream.AppendBatch(new DomainEventPayload[]
            {
                new UserInputReceived(opened.RunId, "\"initial question\"", null, "fixture"),
                new TurnStarted(TurnId.New(), opened.RootLane, legacy),
            }, DurabilityClass.Barrier);
        }

        var startBefore = Assert.Single(fixture.Store.ReadFrom(fixture.Session, 1)
            .Select(fixture.Codecs.Decode).OfType<TurnStarted>());
        var originalComponents = startBefore.Fingerprint!.Components.ToArray();
        Assert.Null(Part(startBefore.Fingerprint, "provider.adapter").Content);
        fixture.ReopenJournal();

        var changed = fixture.PrepareBaseline("https://changed-fixture.invalid/v1");
        var mismatch = fixture.CreateTurn(changed, "https://changed-fixture.invalid/v1").Ask("continue", "fixture instruction", fixture.Session,
            opened.RunId, opened.RootLane, "", CancellationToken.None);
        Assert.Equal(StopReason.Error, mismatch.StopReason);
        Assert.Equal(0, fixture.ProviderCalls);
        Assert.Contains("effective fingerprint differs", mismatch.FinalText, StringComparison.Ordinal);
        Assert.False(fixture.Artifacts.Verify(currentAdapterRef.Hash, currentAdapterRef.Size));

        // The unchanged historical fingerprint resumes normally and retains Content=null;
        // current preparation may not retrofit/publish the new adapter content reference.
        var resumed = fixture.CreateTurn(baseline).Ask("continue", "fixture instruction", fixture.Session,
            opened.RunId, opened.RootLane, "", CancellationToken.None);
        Assert.Equal(StopReason.EndTurn, resumed.StopReason);
        Assert.Equal(1, fixture.ProviderCalls);
        var after = Assert.Single(fixture.Store.ReadFrom(fixture.Session, 1)
            .Select(fixture.Codecs.Decode).OfType<TurnStarted>());
        Assert.Equal(originalComponents, after.Fingerprint!.Components.ToArray());
        Assert.Null(Part(after.Fingerprint, "provider.adapter").Content);
        Assert.False(fixture.Artifacts.Verify(currentAdapterRef.Hash, currentAdapterRef.Size));
        foreach (var component in originalComponents.Where(component => component.Content is not null))
            Assert.True(fixture.Artifacts.Verify(component.Content!.Hash, component.Content.Size));

        var sweep = fixture.SweepZeroGrace();
        Assert.True(sweep.LiveReferenced > 0);
        Assert.False(fixture.Artifacts.Verify(currentAdapterRef.Hash, currentAdapterRef.Size));
        foreach (var component in originalComponents.Where(component => component.Content is not null))
            Assert.True(fixture.Artifacts.Verify(component.Content!.Hash, component.Content.Size));
    }

    private static FingerprintComponent Part(ExecutionFingerprint fingerprint, string name) =>
        Assert.Single(fingerprint.Components, component => component.Name == name);

    private static ModelDefinition Model() => new("fixture-model", "fixture-provider", 8192, 7000, 1024);

    private static ModelRoute Route(string endpoint) => new("fixture-provider", endpoint,
        ProviderFamily.OpenAiChatCompatible, null, "fixture-model", new RouteId("fixture-route"));

    private static ExecutionFingerprint CreateFingerprint(ModelDefinition model, ModelRoute route,
        IModelProvider provider, IArtifactStore? artifacts)
    {
        var profile = new ModelProfileResolver().Resolve(model, null, route: route);
        var harness = new HarnessPolicyResolver().Resolve(profile);
        var selection = new ModelSelection(new ModelIdValue(model.Id), 7000, ToolMode.Direct, null, route.Id, route);
        return RuntimeFingerprintFactory.Create(model, profile, harness, selection,
            "harness", "context", "policy", "fixture-tokenizer", provider, artifacts: artifacts);
    }

    private static RuntimeFingerprintFactory.PreparedRuntimeFingerprint Prepare(ModelDefinition model,
        ModelRoute route, IModelProvider provider, IArtifactStore artifacts)
    {
        var profile = new ModelProfileResolver().Resolve(model, null, route: route);
        var harness = new HarnessPolicyResolver().Resolve(profile);
        var selection = new ModelSelection(new ModelIdValue(model.Id), 7000, ToolMode.Direct, null, route.Id, route);
        return RuntimeFingerprintFactory.Prepare(model, profile, harness, selection,
            "harness", "context", "policy", "fixture-tokenizer", provider, artifacts: artifacts);
    }

    private static string AdapterJson(ModelRoute route, IModelProvider provider)
    {
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("route");
            using (var routeJson = System.Text.Json.JsonDocument.Parse(route.CanonicalJson()))
                routeJson.RootElement.WriteTo(writer);
            writer.WriteString("providerType", provider.GetType().FullName);
            writer.WriteString("providerBuild", RuntimeBuildIdentity.ForAssembly(provider.GetType().Assembly));
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Digest(string value) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private static string NewDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string[] Blobs(string root) => Directory.Exists(Path.Combine(root, "blobs"))
        ? Directory.GetFiles(Path.Combine(root, "blobs"), "*", SearchOption.AllDirectories)
        : Array.Empty<string>();

    private sealed class SyntheticRedactor(string sentinel) : ISecretRedactor
    {
        public bool SawSentinel { get; private set; }
        public void RegisterSecret(string value) => throw new NotSupportedException("Private fixture is fixed.");
        public string Redact(string input)
        {
            SawSentinel |= input.Contains(sentinel, StringComparison.Ordinal);
            return input.Replace(sentinel, "[REDACTED]", StringComparison.Ordinal);
        }
    }

    private sealed class FixtureAdapter : IModelProvider
    {
        public ProviderCapabilities Capabilities => new(false, false, false);
        public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Fingerprint fixture never calls an adapter.");
    }

    private sealed class TurnFixture : IDisposable
    {
        private readonly string _journalPath;
        private SqliteEventStore _inner;
        public string Root { get; }
        public SessionId Session { get; } = SessionId.New();
        public EventCodecs Codecs { get; } = EventCodecs.Create();
        public IEventStore Store => _inner;
        public EventStream Stream => new(Store, Codecs, Session);
        public FileArtifactStore Artifacts { get; }
        public FakeCatalog Catalog { get; } = new();
        public int ProviderCalls { get; private set; }

        public TurnFixture()
        {
            Root = NewDirectory("omni-provider-adapter-resume-");
            _journalPath = Path.Combine(Root, "journal.db");
            _inner = new SqliteEventStore(_journalPath);
            Artifacts = new FileArtifactStore(Root);
        }

        public TestRun.Opened OpenRun() => TestRun.Open(Stream, Session,
            agentProfile: ProfileId.New(), mode: RunMode.Plan);

        public ProfileId ProfileFor(TestRun.Opened opened) => Store.ReadFrom(Session, 1)
            .Select(Codecs.Decode).OfType<LaneCreated>().Single(lane => lane.LaneId == opened.RootLane).AgentProfile;

        public RuntimeFingerprintFactory.PreparedRuntimeFingerprint PrepareBaseline(string endpoint)
        {
            var model = Model();
            var route = Route(endpoint);
            var profile = new ModelProfileResolver().Resolve(model, null, route: route);
            var harness = new HarnessPolicyResolver().Resolve(profile);
            var selection = new ModelSelection(new ModelIdValue(model.Id), 7000, ToolMode.Direct, null, route.Id, route);
            return RuntimeFingerprintFactory.Prepare(model, profile, harness, selection,
                "harness", "context", "policy", "fixture-tokenizer", Provider, artifacts: Artifacts);
        }

        public RuntimeFingerprintFactory.PreparedRuntimeFingerprint PrepareTurnConfiguration(
            RuntimeFingerprintFactory.PreparedRuntimeFingerprint baseline, ProfileId profile)
        {
            var plan = PlanProjection.Replay(Codecs, Store.ReadFrom(Session, 1)).Latest();
            return RuntimeFingerprintFactory.PrepareTurnConfiguration(baseline.Fingerprint, Catalog,
                Catalog.Definitions(), Prompt, plan, Artifacts, agentProfile: profile,
                activeSkills: Array.Empty<ActiveSkillFingerprint>());
        }

        public ExplorerTurn CreateTurn(RuntimeFingerprintFactory.PreparedRuntimeFingerprint baseline,
            string endpoint = Endpoint)
        {
            var model = Model();
            var route = Route(endpoint);
            return new ExplorerTurn((request, token) =>
            {
                ProviderCalls++;
                Assert.True(LeaseProbe.CanAcquire(Root));
                return new ModelResponse(new ContentBlock[] { new TextBlock("done") },
                    StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
                    new ProviderMetadata("scripted", "fixture", null));
            }, new NoTools(), Catalog, new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                baseline.Fingerprint, new ModelSelection(new ModelIdValue(model.Id), 7000, ToolMode.Direct,
                    null, route.Id, route), Store, Codecs, Artifacts, new InMemoryAuditSink(),
                new RedactionPolicy(), recordEffectiveFingerprint: true,
                activeSkills: Array.Empty<ActiveSkillFingerprint>(), fingerprintArtifacts: baseline.Artifacts);
        }

        private FixtureAdapter Provider { get; } = new();

        public void ReopenJournal()
        {
            _inner.Close();
            _inner = new SqliteEventStore(_journalPath);
        }

        public ArtifactGc.SweepResult SweepZeroGrace() => new ArtifactGc(Root).Sweep(_journalPath,
            TimeSpan.Zero, dryRun: false, DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);

        public void Dispose()
        {
            _inner.Close();
            using var connection = new SqliteConnection("DataSource=" + _journalPath);
            SqliteConnection.ClearPool(connection);
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class NoTools : IToolExecutor
    {
        public ToolOutcome ExecuteTool(ValidatedToolCall call, bool approve, CancellationToken token,
            EventStream stream) => throw new InvalidOperationException("No tool invocation is expected.");
    }

    private static class LeaseProbe
    {
        public static bool CanAcquire(string root)
        {
            try
            {
                using var lease = new FileStream(Path.Combine(root, ".artifact-gc.lease"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException) { return false; }
        }
    }
}


