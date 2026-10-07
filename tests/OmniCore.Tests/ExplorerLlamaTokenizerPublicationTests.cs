using System.Net;
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

public sealed class ExplorerLlamaTokenizerPublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tokenizer_runs_unlocked_before_CAS_publication_and_snapshot_roots_commit_under_lease(bool failStart)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-tokenizer-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var inner = new FileArtifactStore(root);
            var artifacts = new ObservedArtifacts(inner, root);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var observedEvents = new ObservedEvents(store, root, failStart);
            var contributor = new ToolOutputContributor(root);
            var handler = new TokenizerHandler(artifacts, root);
            var counter = new LlamaCppTokenCounter("http://fixture.invalid", new TokenizerId(Guid.NewGuid().ToString()),
                new FakeTokenCounter(), httpFactory: () => new HttpClient(handler, disposeHandler: false));
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var policy = new ContextManagementPolicy(20, 80, 4, 100, 500);
            var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 4, GuidanceLevel.Off,
                1, PlanControl.RuntimeDriven, 4, policy);
            var providerCalls = 0;
            var providerUnlocked = true;
            var turn = new ExplorerTurn((_, _) =>
            {
                providerCalls++;
                providerUnlocked &= !LeaseHeld(root);
                return new ModelResponse(new ContentBlock[] { new TextBlock("answer") }, StopReason.EndTurn,
                    new TokenUsage(4, 2, 0, 0, 0), null, new ProviderMetadata("fixture", "scripted", null));
            }, executor, catalog, new ContextMaterializer(counter, new IContextContributor[] { contributor }),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "test"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
                observedEvents, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(), harness);

            var result = turn.Ask("question", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.True(handler.Calls > 0);
            Assert.True(handler.AllCallsUnlocked);
            Assert.True(handler.NoToolBlobDuringCounts);
            Assert.True(contributor.Unlocked);
            Assert.True(providerUnlocked);
            Assert.True(artifacts.AllPublicationsHeld);
            Assert.True(observedEvents.AllStartAppendsHeld);
            Assert.False(LeaseHeld(root));
            var tool = Assert.Single(artifacts.ToolReferences);
            var events = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            if (failStart)
            {
                Assert.Equal(StopReason.Error, result.StopReason);
                Assert.Equal(0, providerCalls);
                Assert.Empty(events.OfType<TurnStarted>());
                Assert.Empty(events.OfType<ModelStepStarted>());
                new ArtifactGc(root).Sweep(journal, TimeSpan.Zero, false,
                    DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);
                Assert.False(inner.Verify(tool.Hash, tool.Size));
            }
            else
            {
                Assert.Equal(StopReason.EndTurn, result.StopReason);
                Assert.Equal(1, providerCalls);
                var start = Assert.Single(events.OfType<TurnStarted>());
                var step = Assert.Single(events.OfType<ModelStepStarted>());
                Assert.NotNull(start.ContextSnapshotRef);
                Assert.NotNull(step.ContextSnapshotRef);
                Assert.Contains("artifact=" + tool.Hash, inner.GetText(step.ContextSnapshotRef.Hash));
                store.Close();
                store = new SqliteEventStore(journal);
                new ArtifactGc(root).Sweep(journal, TimeSpan.Zero, false,
                    DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);
                Assert.True(inner.Verify(tool.Hash, tool.Size));
                Assert.True(inner.Verify(start.ContextSnapshotRef.Hash, start.ContextSnapshotRef.Size));
                Assert.True(inner.Verify(step.ContextSnapshotRef.Hash, step.ContextSnapshotRef.Size));
                Assert.Equal(contributor.Output, inner.GetText(tool.Hash));
            }
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool LeaseHeld(string root)
    {
        try
        {
            using var probe = new FileStream(Path.Combine(root, ".artifact-gc.lease"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
    }

    private sealed class ToolOutputContributor(string root) : IContextContributor
    {
        public string Output { get; } = new('x', 700);
        public bool Unlocked { get; private set; }
        public System.Threading.Tasks.Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
            CancellationToken cancellationToken)
        {
            Unlocked = !LeaseHeld(root);
            return System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>(new[] {
                new ContextItem("large-tool", ContextItemKind.ToolResult, Output, 0, ContextPriority.Normal,
                    RetentionPolicy.ConversationWindow, new ContextProvenance("fixture", ContributionCategory.ToolObservations,
                        "fixture", ScopeLevel.Run, false)) });
        }
    }

    private sealed class TokenizerHandler(ObservedArtifacts artifacts, string root) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool AllCallsUnlocked { get; private set; } = true;
        public bool NoToolBlobDuringCounts { get; private set; } = true;
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            AllCallsUnlocked &= !LeaseHeld(root);
            NoToolBlobDuringCounts &= artifacts.ToolReferences.All(reference => !artifacts.Verify(reference.Hash, reference.Size));
            return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"tokens\":[1,2]}") });
        }
    }

    private sealed class ObservedArtifacts(FileArtifactStore inner, string root)
        : IArtifactStore, IArtifactPreparationStore, IArtifactPublicationLease
    {
        public List<ArtifactRef> ToolReferences { get; } = new();
        public bool AllPublicationsHeld { get; private set; } = true;
        public ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity)
        {
            var reference = inner.PutText(content, mediaType, kind, sensitivity);
            Observe(reference);
            if (kind == ArtifactKind.ToolOutput) ToolReferences.Add(reference);
            return reference;
        }
        public IPreparedArtifact PrepareText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity)
        {
            var prepared = inner.PrepareText(content, mediaType, kind, sensitivity);
            if (kind == ArtifactKind.ToolOutput) ToolReferences.Add(prepared.Reference);
            return new ObservedPrepared(prepared, this);
        }
        private void Observe(ArtifactRef reference)
        {
            if (reference.Kind is ArtifactKind.ToolOutput or ArtifactKind.ContextSnapshot)
                AllPublicationsHeld &= LeaseHeld(root);
        }
        private sealed class ObservedPrepared(IPreparedArtifact inner, ObservedArtifacts owner) : IPreparedArtifact
        {
            public ArtifactRef Reference => inner.Reference;
            public ArtifactRef Publish() { var reference = inner.Publish(); owner.Observe(reference); return reference; }
        }
        public string? GetText(ContentHash hash) => inner.GetText(hash);
        public bool Verify(ContentHash hash, long size) => inner.Verify(hash, size);
        public IDisposable AcquirePublicationLease(CancellationToken token) => inner.AcquirePublicationLease(token);
    }

    private sealed class ObservedEvents(IEventStore inner, string root, bool failStart) : IEventStore
    {
        public bool AllStartAppendsHeld { get; private set; } = true;
        public void AppendBatch(SessionId session, IReadOnlyList<DomainEvent> events, DurabilityClass durability,
            CancellationToken token)
        {
            var starts = events.Any(evt => evt.Type.ToString() is "turn.started" or "model_step.started");
            if (starts) { AllStartAppendsHeld &= LeaseHeld(root); if (failStart) throw new IOException("fixture start failure"); }
            inner.AppendBatch(session, events, durability, token);
            if (starts) AllStartAppendsHeld &= LeaseHeld(root);
        }
        public void Append(SessionId session, DomainEvent evt, DurabilityClass durability, CancellationToken token) =>
            inner.Append(session, evt, durability, token);
        public long CurrentSequence(SessionId session) => inner.CurrentSequence(session);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId session, long sequence) => inner.ReadFrom(session, sequence);
    }
}
