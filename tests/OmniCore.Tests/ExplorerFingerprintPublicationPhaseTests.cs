// Proposed once RuntimeFingerprintFactory exposes the planned prepared overloads:
//   Prepare(...same args as Create...) -> PreparedRuntimeFingerprint { Fingerprint, Artifacts }
//   PrepareTurnConfiguration(...same args as WithTurnConfiguration...) -> same carrier.
// No event/schema changes are expected. This file is intentionally outside the repository and
// has not been compiled against those not-yet-added APIs.
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;
using PreparedRuntimeFingerprint = OmniCore.Host.RuntimeFingerprintFactory.PreparedRuntimeFingerprint;

namespace OmniCore.Tests;

public sealed class ExplorerFingerprintPublicationPhaseTests
{
    private const string EffectivePrompt =
        "Contexto del workspace (fuentes del run):\nfixture instruction\nFingerprint: fixture-model · context";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prepared_components_survive_zero_grace_gc_during_materialization_and_reopen(bool overflow)
    {
        using var fixture = new Fixture();
        var run = fixture.OpenRun();
        var baseline = fixture.PrepareBaseline(overflow ? 1 : 7000);
        Assert.NotEmpty(baseline.Artifacts);
        Assert.All(baseline.Artifacts, prepared =>
            Assert.False(fixture.Artifacts.Verify(prepared.Reference.Hash, prepared.Reference.Size)));

        var gcDuringContext = new GcProbeContributor(fixture.DataDirectory, () =>
        {
            Assert.True(LeaseProbe.CanAcquire(fixture.DataDirectory));
            Assert.Empty(Fixture.Blobs(fixture.DataDirectory)); // preparation did not publish CAS bytes
            fixture.SweepZeroGrace();
            Assert.All(baseline.Artifacts, prepared =>
                Assert.False(fixture.Artifacts.Verify(prepared.Reference.Hash, prepared.Reference.Size)));
        });
        var turn = fixture.CreateTurn(baseline, gcDuringContext, overflow ? 1 : 7000);

        var result = turn.Ask("hello", "fixture instruction", fixture.Session, run.RunId, run.RootLane,
            "", CancellationToken.None);

        Assert.Equal(overflow ? StopReason.ContextOverflow : StopReason.EndTurn, result.StopReason);
        Assert.Equal(1, gcDuringContext.Calls);
        Assert.True(gcDuringContext.LeaseWasAvailable);
        var startedEvent = Assert.Single(fixture.Store.ReadFrom(fixture.Session, 1), evt =>
            fixture.Codecs.Decode(evt) is TurnStarted);
        var started = Assert.IsType<TurnStarted>(fixture.Codecs.Decode(startedEvent));
        Assert.NotNull(started.Fingerprint);
        Assert.True(fixture.StartStore!.TurnStartedLeaseHeldBeforeAppend);
        Assert.True(fixture.StartStore.TurnStartedLeaseHeldAfterAppend);
        Assert.True(fixture.StartStore.TurnStartedAppendDelegated);
        Assert.True(LeaseProbe.CanAcquire(fixture.DataDirectory)); // no lease crosses provider execution
        Assert.Equal(overflow ? 0 : 1, fixture.ProviderCalls);

        var expectedHashOnlyBaseline = fixture.CreateHashOnlyBaseline(overflow ? 1 : 7000);
        var expected = RuntimeFingerprintFactory.WithTurnConfiguration(expectedHashOnlyBaseline,
            fixture.Catalog, fixture.Catalog.Definitions(), EffectivePrompt, null,
            agentProfile: fixture.ProfileFor(run), activeSkills: Array.Empty<ActiveSkillFingerprint>());
        Assert.Equal(expected.Hash(), started.Fingerprint.Hash());
        foreach (var prepared in baseline.Artifacts)
        {
            var component = Assert.Single(started.Fingerprint.Components,
                candidate => candidate.Hash == prepared.Reference.Hash);
            Assert.Equal(prepared.Reference, component.Content);
            Assert.Contains(prepared.Reference, startedEvent.ArtifactRefs);
        }
        foreach (var component in started.Fingerprint.Components.Where(component => component.Content is not null))
        {
            Assert.Contains(component.Content!, startedEvent.ArtifactRefs);
            Assert.True(fixture.Artifacts.Verify(component.Content!.Hash, component.Content.Size));
            Assert.NotNull(fixture.Artifacts.GetText(component.Content.Hash));
        }
        Assert.All(baseline.Artifacts, prepared =>
            Assert.True(fixture.Artifacts.Verify(prepared.Reference.Hash, prepared.Reference.Size)));

        fixture.CloseJournal();
        fixture.ReopenJournal();
        var reopened = Assert.Single(fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode)
            .OfType<TurnStarted>());
        Assert.Equal(started.Fingerprint.Components.ToArray(), reopened.Fingerprint!.Components.ToArray());
        var gcAfterReopen = fixture.SweepZeroGrace();
        Assert.True(gcAfterReopen.LiveReferenced > 0);
        foreach (var component in reopened.Fingerprint.Components.Where(component => component.Content is not null))
            Assert.True(fixture.Artifacts.Verify(component.Content!.Hash, component.Content.Size));
    }

    [Fact]
    public void Failed_turn_started_append_collects_prepared_orphans_and_retry_republishes_exact_refs()
    {
        using var fixture = new Fixture(failFirstTurnStart: true);
        var run = fixture.OpenRun();
        var baseline = fixture.PrepareBaseline();
        var turnConfiguration = fixture.PrepareExpectedTurnConfiguration(baseline, fixture.ProfileFor(run));
        var allPrepared = baseline.Artifacts.Concat(turnConfiguration.Artifacts).ToArray();
        Assert.NotEmpty(allPrepared);
        Assert.All(allPrepared, prepared =>
            Assert.False(fixture.Artifacts.Verify(prepared.Reference.Hash, prepared.Reference.Size)));
        var turn = fixture.CreateTurn(baseline,
            new GcProbeContributor(fixture.DataDirectory, () => fixture.SweepZeroGrace()));

        var failed = turn.Ask("hello", "fixture instruction", fixture.Session, run.RunId, run.RootLane,
            "", CancellationToken.None);

        Assert.Equal(StopReason.Error, failed.StopReason);
        Assert.Equal(0, fixture.ProviderCalls);
        Assert.True(fixture.StartStore!.FailedTurnStartOnce);
        Assert.Empty(fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).OfType<TurnStarted>());
        Assert.All(allPrepared, prepared =>
            Assert.True(fixture.Artifacts.Verify(prepared.Reference.Hash, prepared.Reference.Size)));
        var removed = fixture.SweepZeroGrace();
        Assert.True(removed.Deleted >= allPrepared.Select(p => p.Reference.Hash).Distinct().Count());
        Assert.All(allPrepared, prepared =>
            Assert.False(fixture.Artifacts.Verify(prepared.Reference.Hash, prepared.Reference.Size)));
        var retried = turn.Ask("hello", "fixture instruction", fixture.Session, run.RunId, run.RootLane,
            "", CancellationToken.None);

        Assert.Equal(StopReason.EndTurn, retried.StopReason);
        Assert.Equal(1, fixture.ProviderCalls);
        var startedEvent = Assert.Single(fixture.Store.ReadFrom(fixture.Session, 1), evt =>
            fixture.Codecs.Decode(evt) is TurnStarted);
        var started = Assert.IsType<TurnStarted>(fixture.Codecs.Decode(startedEvent));
        var expectedHashOnlyBaseline = fixture.CreateHashOnlyBaseline();
        var expected = RuntimeFingerprintFactory.WithTurnConfiguration(expectedHashOnlyBaseline,
            fixture.Catalog, fixture.Catalog.Definitions(), EffectivePrompt, null,
            agentProfile: fixture.ProfileFor(run), activeSkills: Array.Empty<ActiveSkillFingerprint>());
        Assert.Equal(expected.Hash(), started.Fingerprint!.Hash());
        foreach (var component in started.Fingerprint.Components.Where(component => component.Content is not null))
        {
            Assert.Contains(component.Content!, startedEvent.ArtifactRefs);
            Assert.True(fixture.Artifacts.Verify(component.Content!.Hash, component.Content.Size));
        }
    }

    [Fact]
    public void Resume_keeps_original_component_refs_and_mismatch_rejects_before_provider()
    {
        using var fixture = new Fixture();
        var run = fixture.OpenRun();
        var baseline = fixture.PrepareBaseline();
        var perTurn = fixture.PrepareExpectedTurnConfiguration(baseline, fixture.ProfileFor(run));
        var original = perTurn.Fingerprint;
        using (fixture.Artifacts.AcquirePublicationLease(CancellationToken.None))
        {
            foreach (var prepared in baseline.Artifacts.Concat(perTurn.Artifacts))
                Assert.Equal(prepared.Reference, prepared.Publish());
            fixture.Stream.AppendBatch(new DomainEventPayload[]
            {
                new UserInputReceived(run.RunId, "\"initial question\"", null, "fixture"),
                new TurnStarted(TurnId.New(), run.RootLane, original),
            }, DurabilityClass.Barrier);
        }
        var originalStart = Assert.Single(fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode)
            .OfType<TurnStarted>());
        var originalComponents = originalStart.Fingerprint!.Components.ToArray();
        fixture.CloseJournal();
        fixture.ReopenJournal();
        fixture.Artifacts.ResetPublishCount();

        var turn = fixture.CreateTurn(baseline, new GcProbeContributor(fixture.DataDirectory, () => { }));
        var mismatch = turn.Ask("continue", "different effective instruction", fixture.Session, run.RunId,
            run.RootLane, "", CancellationToken.None);

        Assert.Equal(StopReason.Error, mismatch.StopReason);
        Assert.Equal(0, fixture.ProviderCalls);
        Assert.Contains("effective fingerprint differs", mismatch.FinalText, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Artifacts.EagerFingerprintWrites);
        Assert.Equal(0, fixture.Artifacts.PreparedPublishCount);
        Assert.Equal(originalComponents, Assert.Single(fixture.Store.ReadFrom(fixture.Session, 1)
            .Select(fixture.Codecs.Decode).OfType<TurnStarted>()).Fingerprint!.Components.ToArray());

        var resumed = turn.Ask("continue", "fixture instruction", fixture.Session, run.RunId,
            run.RootLane, "", CancellationToken.None);
        Assert.Equal(StopReason.EndTurn, resumed.StopReason);
        Assert.Equal(1, fixture.ProviderCalls);
        Assert.Equal(0, fixture.Artifacts.EagerFingerprintWrites);
        Assert.Equal(0, fixture.Artifacts.PreparedPublishCount); // no newly computed content is republished on resume
        var after = Assert.Single(fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode)
            .OfType<TurnStarted>());
        Assert.Equal(originalComponents, after.Fingerprint!.Components.ToArray());
        Assert.Single(fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode)
            .OfType<TurnStarted>());
        foreach (var component in originalComponents.Where(component => component.Content is not null))
            Assert.True(fixture.Artifacts.Verify(component.Content!.Hash, component.Content.Size));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _journalPath;
        private readonly bool _failFirstTurnStart;
        private SqliteEventStore? _innerStore;
        public string DataDirectory { get; }
        public TrackingArtifactStore Artifacts { get; }
        public EventCodecs Codecs { get; } = EventCodecs.Create();
        public SessionId Session { get; } = SessionId.New();
        public FakeCatalog Catalog { get; } = new();
        public IEventStore Store => (IEventStore?)StartStore ?? _innerStore!;
        public EventStream Stream => new(Store, Codecs, Session);
        public TurnStartObservingStore? StartStore { get; private set; }
        public int ProviderCalls { get; private set; }

        public Fixture(bool failFirstTurnStart = false)
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "omni-fingerprint-phase-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DataDirectory);
            _journalPath = Path.Combine(DataDirectory, "journal.db");
            _failFirstTurnStart = failFirstTurnStart;
            _innerStore = new SqliteEventStore(_journalPath);
            StartStore = new TurnStartObservingStore(_innerStore, Codecs, DataDirectory, failFirstTurnStart);
            Artifacts = new TrackingArtifactStore(new FileArtifactStore(DataDirectory));
        }

        public TestRun.Opened OpenRun() => TestRun.Open(Stream, Session, agentProfile: ProfileId.New(),
            mode: RunMode.Plan);

        public ProfileId ProfileFor(TestRun.Opened run) => Store.ReadFrom(Session, 1).Select(Codecs.Decode)
            .OfType<LaneCreated>().Single(lane => lane.LaneId == run.RootLane).AgentProfile;

        public PreparedRuntimeFingerprint PrepareBaseline(long contextBudget = 7000)
        {
            var model = new ModelDefinition("fixture-model", "fixture-provider", 8192, 7000, 1024);
            var route = new ModelRoute(model.ProviderId, "http://fixture.invalid/v1",
                ProviderFamily.OpenAiChatCompatible, null, model.Id);
            var profile = new ModelProfileResolver().Resolve(model, null, route: route);
            var harness = new HarnessPolicyResolver().Resolve(profile);
            var selection = new ModelSelection(new ModelIdValue(model.Id), contextBudget, ToolMode.Direct,
                null, route.Id, route);
            return RuntimeFingerprintFactory.Prepare(model, profile, harness, selection,
                "harness", "context", "policy", "fixture-tokenizer", provider: null,
                qualification: null, artifacts: Artifacts);
        }

        public ExecutionFingerprint CreateHashOnlyBaseline(long contextBudget = 7000)
        {
            var model = new ModelDefinition("fixture-model", "fixture-provider", 8192, 7000, 1024);
            var route = new ModelRoute(model.ProviderId, "http://fixture.invalid/v1",
                ProviderFamily.OpenAiChatCompatible, null, model.Id);
            var profile = new ModelProfileResolver().Resolve(model, null, route: route);
            var harness = new HarnessPolicyResolver().Resolve(profile);
            var selection = new ModelSelection(new ModelIdValue(model.Id), contextBudget, ToolMode.Direct,
                null, route.Id, route);
            return RuntimeFingerprintFactory.Create(model, profile, harness, selection,
                "harness", "context", "policy", "fixture-tokenizer", provider: null,
                qualification: null, artifacts: null);
        }

        public PreparedRuntimeFingerprint PrepareExpectedTurnConfiguration(PreparedRuntimeFingerprint baseline,
            ProfileId profileId) => RuntimeFingerprintFactory.PrepareTurnConfiguration(baseline.Fingerprint,
                Catalog, Catalog.Definitions(), EffectivePrompt, null, Artifacts,
                agentProfile: profileId, activeSkills: Array.Empty<ActiveSkillFingerprint>());

        public ExplorerTurn CreateTurn(PreparedRuntimeFingerprint baseline, IContextContributor contributor, long contextBudget = 7000)
        {
            var tools = Catalog;
            return new ExplorerTurn((request, token) =>
            {
                ProviderCalls++;
                Assert.True(LeaseProbe.CanAcquire(DataDirectory));
                return new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "fixture", null));
            }, new NoTools(), tools,
                new ContextMaterializer(new FakeTokenCounter(), new[] { contributor }),
                baseline.Fingerprint,
                new ModelSelection(new ModelIdValue("fixture-model"), contextBudget, ToolMode.Direct, null),
                Store, Codecs, Artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                recordEffectiveFingerprint: true, activeSkills: Array.Empty<ActiveSkillFingerprint>(),
                fingerprintArtifacts: baseline.Artifacts);
        }

        public ArtifactGc.SweepResult SweepZeroGrace() => new ArtifactGc(DataDirectory).Sweep(_journalPath,
            TimeSpan.Zero, dryRun: false, DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);

        public void CloseJournal()
        {
            _innerStore?.Close();
            _innerStore = null;
            StartStore = null;
        }

        public void ReopenJournal()
        {
            _innerStore = new SqliteEventStore(_journalPath);
            StartStore = new TurnStartObservingStore(_innerStore, Codecs, DataDirectory, failFirstTurnStart: false);
        }

        public void Dispose()
        {
            CloseJournal();
            using var connection = new SqliteConnection("DataSource=" + _journalPath);
            SqliteConnection.ClearPool(connection); // only this fixture's exact journal
            if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, recursive: true);
        }

        public static string[] Blobs(string root) => Directory.Exists(Path.Combine(root, "blobs"))
            ? Directory.GetFiles(Path.Combine(root, "blobs"), "*", SearchOption.AllDirectories)
            : Array.Empty<string>();
    }

    private sealed class TrackingArtifactStore(FileArtifactStore inner)
        : IArtifactStore, IArtifactPreparationStore, IArtifactPublicationLease
    {
        private int _preparedPublishCount;
        private int _eagerFingerprintWrites;
        public int EagerFingerprintWrites => Volatile.Read(ref _eagerFingerprintWrites);
        public int PreparedPublishCount => Volatile.Read(ref _preparedPublishCount);
        public IDisposable AcquirePublicationLease(CancellationToken cancellationToken) =>
            ((IArtifactPublicationLease)inner).AcquirePublicationLease(cancellationToken);
        public IPreparedArtifact PrepareText(string content, string mediaType, ArtifactKind kind,
            Sensitivity sensitivity) => new TrackingPreparedArtifact(
                ((IArtifactPreparationStore)inner).PrepareText(content, mediaType, kind, sensitivity),
                () => Interlocked.Increment(ref _preparedPublishCount));
        public ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity)
        {
            if (mediaType == "application/vnd.omnicore.fingerprint-component+json")
                Interlocked.Increment(ref _eagerFingerprintWrites);
            return inner.PutText(content, mediaType, kind, sensitivity);
        }
        public string? GetText(ContentHash hash) => inner.GetText(hash);
        public bool Verify(ContentHash hash, long size) => inner.Verify(hash, size);
        public void ResetPublishCount()
        {
            Interlocked.Exchange(ref _preparedPublishCount, 0);
            Interlocked.Exchange(ref _eagerFingerprintWrites, 0);
        }
        private sealed class TrackingPreparedArtifact(IPreparedArtifact inner, Action published) : IPreparedArtifact
        {
            public ArtifactRef Reference => inner.Reference;
            public ArtifactRef Publish()
            {
                var reference = inner.Publish();
                published();
                return reference;
            }
        }
    }

    private sealed class TurnStartObservingStore(IEventStore inner, IEventCodecRegistry codecs, string root,
        bool failFirstTurnStart) : IEventStore
    {
        public bool TurnStartedLeaseHeldBeforeAppend { get; private set; }
        public bool TurnStartedLeaseHeldAfterAppend { get; private set; }
        public bool TurnStartedAppendDelegated { get; private set; }
        public bool FailedTurnStartOnce { get; private set; }

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
            if (codecs.Decode(evt) is TurnStarted) ObserveAndAppend(sessionId, evt, durability, cancellationToken);
            else inner.Append(sessionId, evt, durability, cancellationToken);
        }

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> evts,
            DurabilityClass durability, CancellationToken cancellationToken)
        {
            if (evts.Any(evt => codecs.Decode(evt) is TurnStarted))
            {
                var started = Assert.Single(evts, evt => codecs.Decode(evt) is TurnStarted);
                ObserveAndAppend(sessionId, started, durability, cancellationToken, evts);
                return;
            }
            inner.AppendBatch(sessionId, evts, durability, cancellationToken);
        }

        public long CurrentSequence(SessionId sessionId) => inner.CurrentSequence(sessionId);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) =>
            inner.ReadFrom(sessionId, fromSequenceInclusive);

        private void ObserveAndAppend(SessionId sessionId, DomainEvent started, DurabilityClass durability,
            CancellationToken token, IReadOnlyList<DomainEvent>? batch = null)
        {
            TurnStartedLeaseHeldBeforeAppend = !LeaseProbe.CanAcquire(root);
            if (failFirstTurnStart && !FailedTurnStartOnce)
            {
                FailedTurnStartOnce = true;
                throw new IOException("Injected pre-delegation TurnStarted append failure.");
            }
            if (batch is null) inner.Append(sessionId, started, durability, token);
            else inner.AppendBatch(sessionId, batch, durability, token);
            TurnStartedAppendDelegated = true;
            TurnStartedLeaseHeldAfterAppend = !LeaseProbe.CanAcquire(root);
        }
    }

    private sealed class GcProbeContributor(string root, Action beforeReturn) : IContextContributor
    {
        public int Calls { get; private set; }
        public bool LeaseWasAvailable { get; private set; }
        public async System.Threading.Tasks.Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LeaseWasAvailable = LeaseProbe.CanAcquire(root);
            Assert.True(LeaseWasAvailable);
            await System.Threading.Tasks.Task.Yield();
            Assert.True(LeaseProbe.CanAcquire(root));
            beforeReturn();
            return new[]
            {
                new ContextItem("fixture-context", ContextItemKind.WorkingState, "prepared context", 1,
                    ContextPriority.Normal, RetentionPolicy.ConversationWindow,
                    new ContextProvenance("fixture", ContributionCategory.WorkingState, "fixture",
                        ScopeLevel.Run, false, null))
            };
        }
    }

    private sealed class NoTools : IToolExecutor
    {
        public ToolOutcome ExecuteTool(ValidatedToolCall call, bool approve, CancellationToken token,
            EventStream stream) => throw new InvalidOperationException("No tool calls expected in this fixture.");
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
