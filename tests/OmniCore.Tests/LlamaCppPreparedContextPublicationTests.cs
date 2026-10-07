// Offline preparation/tokenizer regression: fake HTTP, real SQLite/CAS, no credentials.
using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Models;

namespace OmniCore.Tests;

public sealed class LlamaCppPreparedContextPublicationTests
{
    private static readonly ExecutionFingerprint Fingerprint = new("model", "harness", "tools", "policy",
        "overrides", "test");

    [Fact]
    public void Tokenizer_runs_without_lease_or_CAS_and_diagnostic_only_ref_is_published_under_root_lease()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-prepared-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            var innerArtifacts = new FileArtifactStore(root);
            var artifacts = new RecordingPreparationStore(innerArtifacts, root);
            var output = "prepared-output-" + Guid.NewGuid().ToString("N") + new string('x', 700);
            var contributor = new LeaseCheckingContributor(artifacts, output);
            var counterLockFree = false;
            var counterSawNoCas = false;
            ContentHash? hashDuringTokenize = null;
            using var handler = new ScriptedTokenizeHandler(() =>
            {
                counterLockFree = artifacts.TryOpenLeaseProbe();
                var preparedDuringCount = artifacts.Prepared.SingleOrDefault();
                if (preparedDuringCount is not null)
                {
                    hashDuringTokenize = preparedDuringCount.Reference.Hash;
                    counterSawNoCas = !innerArtifacts.Verify(preparedDuringCount.Reference.Hash,
                        preparedDuringCount.Reference.Size);
                }
            }, tokenCount: 10);
            var fallback = new HeuristicTokenCounter();
            var tokenizer = new LlamaCppTokenCounter("http://127.0.0.1:1",
                TokenizerId.Parse("llama.cpp:prepared-context-" + Guid.NewGuid().ToString("N")), fallback,
                httpFactory: () => new HttpClient(handler, disposeHandler: false), timeout: TimeSpan.FromSeconds(2));
            var policy = new ContextManagementPolicy(40, 20, 4, 10, 300);
            var materializer = new ContextMaterializer(tokenizer, new IContextContributor[] { contributor },
                artifacts, policy);
            var request = new MaterializeRequest(SessionId.New(), RunId.New(), null, null, null, 0, Fingerprint);

            var prepared = materializer.PrepareWithinBudget(request, CancellationToken.None, maxTokens: 1);

            Assert.True(contributor.LeaseWasAvailable);
            Assert.True(counterLockFree);
            Assert.True(counterSawNoCas);
            Assert.True(handler.WasTokenizeRequest);
            Assert.NotNull(hashDuringTokenize);
            var artifact = Assert.Single(artifacts.Prepared);
            Assert.Equal(hashDuringTokenize, artifact.Reference.Hash);
            Assert.Equal(ArtifactKind.ToolOutput, artifact.Reference.Kind);
            Assert.Equal("text/plain", artifact.Reference.MediaType);
            Assert.False(innerArtifacts.Verify(artifact.Reference.Hash, artifact.Reference.Size));
            Assert.Equal(0, BlobCount(root));

            // The budget intentionally omits the externalized conversation item. Its artifact
            // hash is still in the persisted diagnostics' provenance and must be published too.
            Assert.DoesNotContain(prepared.Snapshot.Items, item => item.Id == "large-tool-output");
            var diagnostic = Assert.Single(prepared.Snapshot.Diagnostics,
                item => item.ItemId == "large-tool-output");
            Assert.Equal(ContextDecision.OmittedByBudget, diagnostic.Decision);
            var diagnosticReference = Assert.Single(diagnostic.Provenance.Refs!,
                value => value.StartsWith("artifact=", StringComparison.Ordinal));
            Assert.Equal("artifact=" + artifact.Reference.Hash, diagnosticReference);

            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var turnId = TurnId.New();
            var snapshotText = SerializePersistedSnapshot(prepared.Snapshot);
            ArtifactRef snapshotReference;
            using (artifacts.AcquirePublicationLease(CancellationToken.None))
            {
                prepared.PublishArtifacts();
                Assert.True(innerArtifacts.Verify(artifact.Reference.Hash, artifact.Reference.Size));
                Assert.False(artifacts.TryOpenLeaseProbe());
                snapshotReference = artifacts.PutText(snapshotText, "application/json",
                    ArtifactKind.ContextSnapshot, Sensitivity.Sensitive);
                var stream = new EventStream(store, codecs, session);
                stream.Append(new TurnStarted(turnId, run.RootLane, Fingerprint, snapshotReference),
                    DurabilityClass.Barrier);
            }
            Assert.True(artifacts.TryOpenLeaseProbe());
            Assert.True(innerArtifacts.Verify(snapshotReference.Hash, snapshotReference.Size));
            Assert.Equal(output, innerArtifacts.GetText(artifact.Reference.Hash));

            store.Close();
            store = new SqliteEventStore(journal);
            var reopenedStart = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>());
            Assert.Equal(snapshotReference, reopenedStart.ContextSnapshotRef);
            var sweep = new ArtifactGc(root).Sweep(journal, TimeSpan.Zero, false,
                DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);
            Assert.True(innerArtifacts.Verify(snapshotReference.Hash, snapshotReference.Size));
            Assert.True(innerArtifacts.Verify(artifact.Reference.Hash, artifact.Reference.Size));
            Assert.Equal(output, innerArtifacts.GetText(artifact.Reference.Hash));
            Assert.Contains(diagnosticReference, innerArtifacts.GetText(snapshotReference.Hash));
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
    public void Cancellation_during_tokenizer_leaves_prepared_tool_blob_unpublished()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-prepared-context-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var innerArtifacts = new FileArtifactStore(root);
            var artifacts = new RecordingPreparationStore(innerArtifacts, root);
            var contributor = new LeaseCheckingContributor(artifacts,
                "cancel-output-" + Guid.NewGuid().ToString("N") + new string('y', 700));
            using var cancellation = new CancellationTokenSource();
            var counterLockFree = false;
            using var handler = new ScriptedTokenizeHandler(() =>
            {
                counterLockFree = artifacts.TryOpenLeaseProbe();
                cancellation.Cancel();
            }, tokenCount: 10, cancel: true);
            var fallback = new HeuristicTokenCounter();
            var tokenizer = new LlamaCppTokenCounter("http://127.0.0.1:1",
                TokenizerId.Parse("llama.cpp:prepared-context-cancel-" + Guid.NewGuid().ToString("N")), fallback,
                httpFactory: () => new HttpClient(handler, disposeHandler: false), timeout: TimeSpan.FromSeconds(2));
            var materializer = new ContextMaterializer(tokenizer, new IContextContributor[] { contributor }, artifacts,
                new ContextManagementPolicy(40, 20, 4, 10, 300));
            var request = new MaterializeRequest(SessionId.New(), RunId.New(), null, null, null, 0, Fingerprint);

            Assert.ThrowsAny<OperationCanceledException>(() =>
                materializer.PrepareWithinBudget(request, cancellation.Token, maxTokens: 1));

            Assert.True(cancellation.IsCancellationRequested);
            Assert.True(contributor.LeaseWasAvailable);
            Assert.True(counterLockFree);
            Assert.True(handler.WasTokenizeRequest);
            var artifact = Assert.Single(artifacts.Prepared);
            Assert.False(innerArtifacts.Verify(artifact.Reference.Hash, artifact.Reference.Size));
            Assert.Equal(0, BlobCount(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string SerializePersistedSnapshot(ContextSnapshot snapshot) => JsonSerializer.Serialize(new
    {
        items = snapshot.Items.Select(item => new
        {
            id = item.Id,
            kind = item.Kind.ToString(),
            content = item.Content,
            provenance = new { refs = item.Provenance.Refs },
        }),
        diagnostics = snapshot.Diagnostics.Select(item => new
        {
            itemId = item.ItemId,
            decision = item.Decision.ToString(),
            provenance = new { refs = item.Provenance.Refs },
        }),
    });

    private static int BlobCount(string root)
    {
        var blobs = Path.Combine(root, "blobs", "sha256");
        return Directory.Exists(blobs)
            ? Directory.EnumerateFiles(blobs, "*", SearchOption.AllDirectories).Count()
            : 0;
    }

    private sealed class LeaseCheckingContributor(RecordingPreparationStore artifacts, string content)
        : IContextContributor
    {
        public bool LeaseWasAvailable { get; private set; }

        public System.Threading.Tasks.Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LeaseWasAvailable = artifacts.TryOpenLeaseProbe();
            var provenance = new ContextProvenance("scripted-tool", ContributionCategory.ToolObservations,
                "fixture", ScopeLevel.Run, false);
            IReadOnlyList<ContextItem> items = new[]
            {
                new ContextItem("large-tool-output", ContextItemKind.ToolResult, content, 0,
                    ContextPriority.Normal, RetentionPolicy.ConversationWindow, provenance),
            };
            return System.Threading.Tasks.Task.FromResult(items);
        }
    }

    private sealed class RecordingPreparationStore(IArtifactStore inner, string root)
        : IArtifactStore, IArtifactPublicationLease, IArtifactPreparationStore
    {
        public List<IPreparedArtifact> Prepared { get; } = new();

        public IDisposable AcquirePublicationLease(CancellationToken cancellationToken) =>
            ((IArtifactPublicationLease)inner).AcquirePublicationLease(cancellationToken);

        public IPreparedArtifact PrepareText(string content, string mediaType, ArtifactKind kind,
            Sensitivity sensitivity)
        {
            var prepared = ((IArtifactPreparationStore)inner).PrepareText(content, mediaType, kind, sensitivity);
            Prepared.Add(prepared);
            return prepared;
        }

        public ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity) =>
            inner.PutText(content, mediaType, kind, sensitivity);

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

    private sealed class ScriptedTokenizeHandler(Action onSend, int tokenCount, bool cancel = false)
        : HttpMessageHandler
    {
        public bool WasTokenizeRequest { get; private set; }

        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            WasTokenizeRequest = request.RequestUri is not null
                && string.Equals("tokenize", request.RequestUri.AbsolutePath.Trim('/'), StringComparison.Ordinal);
            onSend();
            if (cancel)
                return System.Threading.Tasks.Task.FromCanceled<HttpResponseMessage>(new CancellationToken(canceled: true));

            var body = JsonSerializer.Serialize(new { tokens = Enumerable.Range(0, tokenCount).Select(_ => new { id = 1 }) });
            return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }
    }
}
