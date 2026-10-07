namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;

public sealed class FingerprintContentAppendFailureTests
{
    [Fact]
    public void Failed_turn_start_leaves_only_collectable_orphans_then_retry_persists_valid_references()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-fingerprint-append-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SqliteEventStore? store = null;
        try
        {
            var journalPath = Path.Combine(root, "journal.db");
            var artifactDirectory = Path.Combine(root, "artifacts");
            store = new SqliteEventStore(journalPath);
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var rawStream = new EventStream(store, codecs, session);
            var run = TestRun.Open(rawStream, session, "fingerprint append failure fixture");
            var artifacts = new FileArtifactStore(artifactDirectory);
            var prior = artifacts.PutText("prior journal content", "text/plain", ArtifactKind.Other,
                Sensitivity.Normal);
            rawStream.Append(new UserInputReceived(run.RunId, "[]", prior));

            var failingStore = new FailTurnStartedOnceStore(store, codecs);
            var catalog = new FakeCatalog().Add(FakeTool.Read("fixture.inspect"));
            var executor = new NoTools();
            var providerCalls = 0;
            var durableStarts = new List<TurnStarted>();
            void OnStarted(TurnStarted started)
            {
                Assert.Equal(0, providerCalls);
                Assert.Contains(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>(),
                    persisted => persisted.TurnId == started.TurnId);
                durableStarts.Add(started);
            }
            var selection = new ModelSelection(new ModelIdValue("fixture-model"), 8192, ToolMode.Direct, null);
            var model = new ModelDefinition("fixture-model", "fixture-provider", 8192, 7000, 1024);
            var profile = new ModelProfileResolver().Resolve(model, null);
            var harness = new HarnessPolicyResolver().Resolve(profile);
            var preparedBaseline = RuntimeFingerprintFactory.Prepare(model, profile, harness, selection,
                "harness", "context", "policy", "counter", artifacts: artifacts);
            var baseline = preparedBaseline.Fingerprint;
            Assert.NotEmpty(preparedBaseline.Artifacts);
            Assert.All(preparedBaseline.Artifacts, artifact =>
                Assert.False(artifacts.Verify(artifact.Reference.Hash, artifact.Reference.Size)));
            var turn = new ExplorerTurn((_, _) =>
                {
                    providerCalls++;
                    Assert.Single(durableStarts);
                    return new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                        new TokenUsage(1, 1, 0, 0, 0), null,
                        new ProviderMetadata("scripted-fixture", "fixture-model", null));
                }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), []), baseline, selection,
                failingStore, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                recordEffectiveFingerprint: true, fingerprintArtifacts: preparedBaseline.Artifacts);

            var beforeAttempt = store.CurrentSequence(session);
            var beforeBlobs = BlobPaths(artifactDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var failed = turn.Ask("hello", "fingerprint fixture instruction", session, run.RunId,
                run.RootLane, "", CancellationToken.None, turnStarted: OnStarted);

            Assert.Equal(StopReason.Error, failed.StopReason);
            Assert.Equal(0, providerCalls);
            Assert.Empty(durableStarts);
            Assert.True(failingStore.FailedOnce);
            Assert.Equal(beforeAttempt, store.CurrentSequence(session));
            var eventsAfterFailure = store.ReadFrom(session, 1);
            Assert.DoesNotContain(eventsAfterFailure, evt => codecs.Decode(evt) is TurnStarted);
            Assert.All(eventsAfterFailure, evt => Assert.DoesNotContain(evt.ArtifactRefs,
                reference => !beforeBlobs.Contains(BlobPath(artifactDirectory, reference.Hash.Value))));

            var afterFailedBlobs = BlobPaths(artifactDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
            afterFailedBlobs.ExceptWith(beforeBlobs);
            Assert.NotEmpty(afterFailedBlobs);
            Assert.All(afterFailedBlobs, path => Assert.True(File.Exists(path)));
            Assert.True(artifacts.Verify(prior.Hash, prior.Size));

            var swept = new ArtifactGc(artifactDirectory).Sweep(journalPath, TimeSpan.Zero, dryRun: false,
                DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
            Assert.Equal((long)afterFailedBlobs.Count, swept.Deleted);
            Assert.All(afterFailedBlobs, path => Assert.False(File.Exists(path)));
            Assert.True(File.Exists(BlobPath(artifactDirectory, prior.Hash.Value)));
            Assert.True(artifacts.Verify(prior.Hash, prior.Size));

            var sweptAgain = new ArtifactGc(artifactDirectory).Sweep(journalPath, TimeSpan.Zero, dryRun: false,
                DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
            Assert.Equal(0L, sweptAgain.Deleted);
            Assert.Equal(0L, sweptAgain.ReclaimedBytes);

            var retried = turn.Ask("hello", "fingerprint fixture instruction", session, run.RunId,
                run.RootLane, "", CancellationToken.None, turnStarted: OnStarted);
            Assert.Equal(StopReason.EndTurn, retried.StopReason);
            Assert.Equal(1, providerCalls);
            var started = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>());
            Assert.Equal(started.TurnId, Assert.Single(durableStarts).TurnId);
            Assert.NotNull(started.Fingerprint);
            Assert.NotEmpty(started.Fingerprint.Components);
            foreach (var component in started.Fingerprint.Components)
            {
                Assert.NotNull(component.Content);
                Assert.Equal(component.Hash, component.Content.Hash);
                Assert.True(artifacts.Verify(component.Hash, component.Content.Size));
                Assert.Contains(store.ReadFrom(session, 1)
                    .Single(evt => codecs.Decode(evt) is TurnStarted).ArtifactRefs,
                    reference => reference.Id == component.Content.Id);
            }
        }
        finally
        {
            store?.Close();
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=" + Path.Combine(root, "journal.db")))
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            // GC opens a distinct read-only pool; release only this fixture's pool too.
            using (var gcConnection = new Microsoft.Data.Sqlite.SqliteConnection(
                "DataSource=" + Path.Combine(root, "journal.db") + ";Mode=ReadOnly"))
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(gcConnection);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static string[] BlobPaths(string dataDirectory) => Directory.Exists(Path.Combine(dataDirectory, "blobs"))
        ? Directory.GetFiles(Path.Combine(dataDirectory, "blobs"), "*", SearchOption.AllDirectories)
        : Array.Empty<string>();

    private static string BlobPath(string dataDirectory, string hash) => Path.Combine(dataDirectory, "blobs", "sha256",
        hash[..2], hash.Substring(2, 2), hash);

    private sealed class FailTurnStartedOnceStore(IEventStore inner, IEventCodecRegistry codecs) : IEventStore
    {
        public bool FailedOnce { get; private set; }

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
            if (IsTurnStarted(evt)) Fail();
            inner.Append(sessionId, evt, durability, cancellationToken);
        }

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> evts,
            DurabilityClass durability, CancellationToken cancellationToken)
        {
            if (evts.Any(IsTurnStarted)) Fail();
            inner.AppendBatch(sessionId, evts, durability, cancellationToken);
        }

        public long CurrentSequence(SessionId sessionId) => inner.CurrentSequence(sessionId);

        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) =>
            inner.ReadFrom(sessionId, fromSequenceInclusive);

        private bool IsTurnStarted(DomainEvent evt) => codecs.Decode(evt) is TurnStarted;

        private void Fail()
        {
            if (FailedOnce) return;
            FailedOnce = true;
            throw new IOException("Injected pre-append TurnStarted failure.");
        }
    }

    private sealed class NoTools : IToolExecutor
    {
        public ToolOutcome ExecuteTool(ValidatedToolCall call, bool approve, CancellationToken token,
            EventStream stream) => throw new InvalidOperationException("EndTurn fixture must not execute tools.");
    }
}
