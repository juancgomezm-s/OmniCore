// Offline checkpoint publication regression: real SQLite/CAS; scripted providers.
// Providers below are scripted and never access credentials or the network.
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

namespace OmniCore.Tests;

public sealed class ExplorerContextCheckpointPublicationLeaseTests
{
    private static readonly ExecutionFingerprint Fingerprint = new("model", "harness", "tools", "policy",
        "overrides", "test");

    [Fact]
    public void Checkpoint_artifact_and_event_share_short_lease_and_survive_reopen_and_zero_grace_gc()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-context-checkpoint-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var innerArtifacts = new FileArtifactStore(root);
            var artifacts = new LeaseObservingArtifacts(innerArtifacts, root);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var metaProvider = new LeaseCheckingSummaryProvider(artifacts);
            var primaryCalls = 0;
            var events = new LeaseObservingEvents(store, codecs, root);
            var turn = CreateTurn(events, codecs, artifacts, root, metaProvider, () =>
            {
                primaryCalls++;
                Assert.True(artifacts.TryOpenLeaseProbe()); // no publication lock across provider work
                return new ModelResponse(new ContentBlock[] { new TextBlock("turn answer") }, StopReason.EndTurn,
                    new TokenUsage(8, 3, 0, 0, 0), null,
                    new ProviderMetadata("scripted", "primary", null));
            });

            var first = turn.Ask("first historical question", "Keep the run focused.", session, run.RunId,
                run.RootLane, "working state", CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, first.StopReason);
            Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ContextCheckpointRecorded>());
            var priorCompletion = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelCompleted>());
            var priorAssistant = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<AssistantMessageRecorded>());
            Assert.NotNull(priorCompletion.ResponseArtifact);
            Assert.NotNull(priorAssistant.ContentRef);

            var second = turn.Ask("second question forces old-history compaction", "Keep the run focused.",
                session, run.RunId, run.RootLane, "working state", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, second.StopReason);
            Assert.Equal(2, primaryCalls);
            Assert.Equal(1, metaProvider.Calls);
            Assert.True(metaProvider.LeaseWasAvailableDuringStream);
            Assert.Equal(2, events.MetaPublicationLeases.Count);
            Assert.All(events.MetaPublicationLeases, held => Assert.True(held));
            Assert.True(artifacts.TryOpenLeaseProbe());
            Assert.True(events.CheckpointLeaseHeldBeforeAppend);
            Assert.True(events.CheckpointLeaseHeldAfterAppend);
            Assert.True(events.CheckpointAppendDelegated);
            var checkpoint = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<ContextCheckpointRecorded>());
            Assert.NotNull(checkpoint.CheckpointArtifact);
            Assert.Equal(events.ObservedCheckpoint!.CheckpointArtifact, checkpoint.CheckpointArtifact);

            var checkpointPublication = Assert.Single(artifacts.Observed,
                item => item.Reference.Hash == checkpoint.CheckpointArtifact.Hash);
            Assert.Equal("application/vnd.omnicore.context-checkpoint+json", checkpointPublication.Reference.MediaType);
            Assert.Equal(ArtifactKind.ContextSnapshot, checkpointPublication.Reference.Kind);
            Assert.True(checkpointPublication.LeaseHeldAfterPut);
            var checkpointJson = innerArtifacts.GetText(checkpoint.CheckpointArtifact.Hash);
            Assert.Contains("durable scripted summary", checkpointJson);
            Assert.True(innerArtifacts.Verify(checkpoint.CheckpointArtifact.Hash, checkpoint.CheckpointArtifact.Size));

            store.Close();
            store = new SqliteEventStore(journal);
            var reopenedCheckpoint = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<ContextCheckpointRecorded>());
            Assert.Equal(checkpoint.CheckpointId, reopenedCheckpoint.CheckpointId);
            Assert.Equal(checkpoint.CheckpointArtifact, reopenedCheckpoint.CheckpointArtifact);
            Assert.Contains("durable scripted summary", innerArtifacts.GetText(reopenedCheckpoint.CheckpointArtifact.Hash));

            var sweep = new ArtifactGc(root).Sweep(journal, TimeSpan.Zero, false,
                DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);
            Assert.True(sweep.LiveReferenced > 0);
            Assert.True(innerArtifacts.Verify(reopenedCheckpoint.CheckpointArtifact.Hash,
                reopenedCheckpoint.CheckpointArtifact.Size));
            Assert.True(innerArtifacts.Verify(priorCompletion.ResponseArtifact!.Hash,
                priorCompletion.ResponseArtifact.Size));
            Assert.True(innerArtifacts.Verify(priorAssistant.ContentRef!.Hash, priorAssistant.ContentRef.Size));
            Assert.Contains("durable scripted summary",
                innerArtifacts.GetText(reopenedCheckpoint.CheckpointArtifact.Hash));
            var metaStart = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<MetaModelInvocationStarted>());
            var metaCompleted = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<MetaModelInvocationCompleted>());
            Assert.True(innerArtifacts.Verify(metaStart.InputArtifact.Hash, metaStart.InputArtifact.Size));
            Assert.True(innerArtifacts.Verify(metaCompleted.OutputArtifact.Hash, metaCompleted.OutputArtifact.Size));
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Failed_checkpoint_append_leaves_no_checkpoint_event_and_gc_collects_only_orphan_checkpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-context-checkpoint-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var innerArtifacts = new FileArtifactStore(root);
            var artifacts = new LeaseObservingArtifacts(innerArtifacts, root);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var metaProvider = new LeaseCheckingSummaryProvider(artifacts);
            var primaryCalls = 0;
            var events = new LeaseObservingEvents(store, codecs, root, failCheckpointAppend: true);
            var turn = CreateTurn(events, codecs, artifacts, root, metaProvider, () =>
            {
                primaryCalls++;
                Assert.True(artifacts.TryOpenLeaseProbe());
                return new ModelResponse(new ContentBlock[] { new TextBlock("turn answer") }, StopReason.EndTurn,
                    new TokenUsage(8, 3, 0, 0, 0), null,
                    new ProviderMetadata("scripted", "primary", null));
            });

            var first = turn.Ask("first historical question", "Keep the run focused.", session, run.RunId,
                run.RootLane, "working state", CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, first.StopReason);
            var priorCompletion = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelCompleted>());
            var priorAssistant = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<AssistantMessageRecorded>());
            Assert.NotNull(priorCompletion.ResponseArtifact);
            Assert.NotNull(priorAssistant.ContentRef);
            Assert.Equal(1, primaryCalls);

            var failed = turn.Ask("second question forces old-history compaction", "Keep the run focused.",
                session, run.RunId, run.RootLane, "working state", CancellationToken.None);

            Assert.Equal(StopReason.Error, failed.StopReason);
            Assert.Equal(1, primaryCalls); // failed checkpoint publication must stop before dispatch
            Assert.Equal(1, metaProvider.Calls);
            Assert.True(metaProvider.LeaseWasAvailableDuringStream);
            Assert.True(events.CheckpointLeaseHeldBeforeAppend);
            Assert.False(events.CheckpointAppendDelegated);
            Assert.True(artifacts.TryOpenLeaseProbe());
            Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ContextCheckpointRecorded>());

            var orphan = Assert.Single(artifacts.Observed,
                item => item.Reference.MediaType == "application/vnd.omnicore.context-checkpoint+json");
            Assert.True(orphan.LeaseHeldAfterPut);
            Assert.True(innerArtifacts.Verify(orphan.Reference.Hash, orphan.Reference.Size));
            Assert.True(innerArtifacts.Verify(priorCompletion.ResponseArtifact!.Hash,
                priorCompletion.ResponseArtifact.Size));
            Assert.True(innerArtifacts.Verify(priorAssistant.ContentRef!.Hash, priorAssistant.ContentRef.Size));

            store.Close();
            var sweep = new ArtifactGc(root).Sweep(journal, TimeSpan.Zero, false,
                DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);
            Assert.False(innerArtifacts.Verify(orphan.Reference.Hash, orphan.Reference.Size));
            Assert.True(innerArtifacts.Verify(priorCompletion.ResponseArtifact!.Hash,
                priorCompletion.ResponseArtifact.Size));
            Assert.True(innerArtifacts.Verify(priorAssistant.ContentRef!.Hash, priorAssistant.ContentRef.Size));

            store = new SqliteEventStore(journal);
            Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ContextCheckpointRecorded>());
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            Directory.Delete(root, recursive: true);
        }
    }

    private static ExplorerTurn CreateTurn(IEventStore events, EventCodecs codecs, IArtifactStore artifacts,
        string root, IModelProvider metaProvider, Func<ModelResponse> primary)
    {
        var catalog = new FakeCatalog();
        var executor = ScriptedToolExecutor.WithWorkspace(catalog,
            new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
        var policy = new ContextManagementPolicy(1000, 180, 0, 2, 1200);
        var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 4, GuidanceLevel.Off,
            1, PlanControl.RuntimeDriven, 4, policy);
        return new ExplorerTurn((_, _) => primary(), executor, catalog,
            new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()), Fingerprint,
            new ModelSelection(new ModelIdValue("scripted"), 3000, ToolMode.Direct, null), events,
            codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(), harness,
            metaModelProvider: metaProvider);
    }

    private sealed class LeaseCheckingSummaryProvider(LeaseObservingArtifacts artifacts, string summary = "Facts: durable scripted summary")
        : IModelProvider
    {
        public ProviderCapabilities Capabilities { get; } = ProviderCapabilities.Local();
        public int Calls { get; private set; }
        public bool LeaseWasAvailableDuringStream { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            LeaseWasAvailableDuringStream = artifacts.TryOpenLeaseProbe();
            Assert.True(LeaseWasAvailableDuringStream);
            await System.Threading.Tasks.Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ResponseCompleted(new ModelResponse(new ContentBlock[] { new TextBlock(summary) },
                StopReason.EndTurn, new TokenUsage(0, 0, 0, 0, 0), null,
                new ProviderMetadata("scripted", "meta", null)));
        }
    }

    private sealed class LeaseObservingArtifacts(IArtifactStore inner, string root)
        : IArtifactStore, IArtifactPublicationLease, IArtifactPreparationStore
    {
        public List<PublicationObservation> Observed { get; } = new();

        public IDisposable AcquirePublicationLease(CancellationToken cancellationToken) =>
            ((IArtifactPublicationLease)inner).AcquirePublicationLease(cancellationToken);

        public IPreparedArtifact PrepareText(string content, string mediaType, ArtifactKind kind,
            Sensitivity sensitivity) => ((IArtifactPreparationStore)inner).PrepareText(content, mediaType, kind, sensitivity);

        public ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity)
        {
            var reference = inner.PutText(content, mediaType, kind, sensitivity);
            if (kind == ArtifactKind.ContextSnapshot)
                Observed.Add(new PublicationObservation(reference, LeaseHeld(root)));
            return reference;
        }

        public string? GetText(ContentHash hash) => inner.GetText(hash);
        public bool Verify(ContentHash hash, long expectedSize) => inner.Verify(hash, expectedSize);
        public bool TryOpenLeaseProbe() => !LeaseHeld(root);

        private static bool LeaseHeld(string root)
        {
            try
            {
                using var probe = new FileStream(Path.Combine(root, ".artifact-gc.lease"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }
    }

    private sealed record PublicationObservation(ArtifactRef Reference, bool LeaseHeldAfterPut);

    private sealed class LeaseObservingEvents(IEventStore inner, EventCodecs codecs, string root,
        bool failCheckpointAppend = false) : IEventStore
    {
        public bool CheckpointLeaseHeldBeforeAppend { get; private set; }
        public bool CheckpointLeaseHeldAfterAppend { get; private set; }
        public bool CheckpointAppendDelegated { get; private set; }
        public ContextCheckpointRecorded? ObservedCheckpoint { get; private set; }
        public List<bool> MetaPublicationLeases { get; } = new();

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
            if (codecs.Decode(evt) is MetaModelInvocationStarted or MetaModelInvocationCompleted)
            {
                var before = LeaseHeld(root);
                inner.Append(sessionId, evt, durability, cancellationToken);
                MetaPublicationLeases.Add(before && LeaseHeld(root));
                return;
            }
            if (codecs.Decode(evt) is not ContextCheckpointRecorded checkpoint)
            {
                inner.Append(sessionId, evt, durability, cancellationToken);
                return;
            }

            ObservedCheckpoint = checkpoint;
            CheckpointLeaseHeldBeforeAppend = LeaseHeld(root);
            if (failCheckpointAppend)
                throw new InvalidOperationException("fixture checkpoint append failure");

            inner.Append(sessionId, evt, durability, cancellationToken);
            CheckpointAppendDelegated = true;
            CheckpointLeaseHeldAfterAppend = LeaseHeld(root);
        }

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> evts,
            DurabilityClass durability, CancellationToken cancellationToken)
        {
            var checkpoints = evts.Select(evt => codecs.Decode(evt)).OfType<ContextCheckpointRecorded>().ToArray();
            if (checkpoints.Length > 0)
            {
                ObservedCheckpoint = Assert.Single(checkpoints);
                CheckpointLeaseHeldBeforeAppend = LeaseHeld(root);
                if (failCheckpointAppend)
                    throw new InvalidOperationException("fixture checkpoint append failure");

                inner.AppendBatch(sessionId, evts, durability, cancellationToken);
                CheckpointAppendDelegated = true;
                CheckpointLeaseHeldAfterAppend = LeaseHeld(root);
                return;
            }
            inner.AppendBatch(sessionId, evts, durability, cancellationToken);
        }

        public long CurrentSequence(SessionId sessionId) => inner.CurrentSequence(sessionId);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) =>
            inner.ReadFrom(sessionId, fromSequenceInclusive);

        private static bool LeaseHeld(string root)
        {
            try
            {
                using var probe = new FileStream(Path.Combine(root, ".artifact-gc.lease"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }
    }
}
