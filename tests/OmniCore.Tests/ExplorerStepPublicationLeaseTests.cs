// Proposed regression only; intentionally outside the repository while the parent full run
// holds a source/build freeze. No provider credentials or network calls are used.
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

public sealed class ExplorerStepPublicationLeaseTests
{
    private static readonly QuestionnaireSchema Schema = new("Choose", null,
        new QuestionField[] { new("approach", "Which?", null, QuestionKind.SingleChoice,
            new[] { new QuestionOption("safe", "Safe", null) }, null, true, null, null, null) });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Provider_state_and_step_completion_share_one_publication_lease_through_gc_and_reopen(
        bool cancelAfterResponse)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-step-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        using var cancellation = new CancellationTokenSource();
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var innerArtifacts = new FileArtifactStore(root);
            var artifacts = new LeaseObservingArtifacts(innerArtifacts, root);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var questionnaires = new QuestionnaireInteractionService(store, codecs, artifacts);
            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var route = ModelRoute.DefaultForModel("scripted", "fixture-provider", "http://127.0.0.1:9901",
                ProviderFamily.OpenAiChatCompatible);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct,
                null, route.Id, route);
            var state = new ProviderState("fixture.kind", "{\"opaque\":\"fixture-state-exact\"}");

            ExplorerTurn MakeTurn(IEventStore eventStore,
                Func<ModelRequest, CancellationToken, ModelResponse> complete) => new(
                complete, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture-build"), selection,
                eventStore, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                questionnaires: questionnaires);

            var firstEvents = new LeaseObservingEvents(store, root);
            var firstCalls = 0;
            var suspended = MakeTurn(firstEvents, (_, _) =>
            {
                firstCalls++;
                Assert.True(artifacts.TryOpenLeaseProbe()); // never hold GC exclusion across provider work
                if (cancelAfterResponse) cancellation.Cancel();
                return new ModelResponse(new ContentBlock[]
                {
                    new ToolCallBlock(ToolCallId.New(), "fixture-question", "user.ask",
                        QuestionnaireCodec.EncodeSchema(Schema))
                }, StopReason.ToolUse, new TokenUsage(10, 2, 0, 0, 0), state,
                    new ProviderMetadata("fixture-provider", "scripted", null));
            }).Ask("ask", "system", session, run.RunId, run.RootLane, "", cancellation.Token);

            Assert.Equal(cancelAfterResponse ? StopReason.Cancelled : StopReason.InputRequired, suspended.StopReason);
            if (cancelAfterResponse) Assert.Null(suspended.PendingInteractionId);
            else Assert.NotNull(suspended.PendingInteractionId);
            Assert.Equal(1, firstCalls);
            var firstCompletion = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<ModelStepCompleted>());
            Assert.NotNull(firstCompletion.ResponseArtifact);

            var statePublication = Assert.Single(artifacts.Observed,
                item => item.Reference.Kind == ArtifactKind.ProviderOpaqueState);
            var responsePublication = Assert.Single(artifacts.Observed,
                item => item.Reference.Kind == ArtifactKind.ModelResponse);
            // These probes open the real .artifact-gc.lease with FileShare.None immediately
            // after each PutText returns. true means a publisher still excludes GC.
            Assert.True(statePublication.LeaseHeldAfterPut);
            Assert.True(responsePublication.LeaseHeldAfterPut);
            Assert.True(firstEvents.CompletionLeaseHeldBeforeCommit);
            Assert.True(firstEvents.CompletionLeaseHeldAfterCommit);

            var sweep = new ArtifactGc(root).Sweep(journal, TimeSpan.Zero, false,
                DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);
            Assert.True(sweep.LiveReferenced > 0);
            Assert.True(innerArtifacts.Verify(statePublication.Reference.Hash, statePublication.Reference.Size));
            Assert.Equal(state, System.Text.Json.JsonSerializer.Deserialize<ProviderState>(
                innerArtifacts.GetText(statePublication.Reference.Hash)!));
            Assert.Equal(10, firstCompletion.Usage.Input);
            Assert.Equal(2, firstCompletion.Usage.Output);
            Assert.True(artifacts.TryOpenLeaseProbe());
            if (cancelAfterResponse)
            {
                Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnInterrupted>());
                Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ToolCallRequested>());
                Assert.Empty(questionnaires.Pending(session));
                return;
            }

            store.Close();
            store = new SqliteEventStore(journal);
            questionnaires = new QuestionnaireInteractionService(store, codecs, artifacts);
            var stateFile = Path.Combine(root, "last-session.txt");
            File.WriteAllText(stateFile, session + "\n" + run.RunId);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, innerArtifacts);
            Assert.Equal("ok", server.RespondToQuestionnaire(suspended.PendingInteractionId!,
                new[] { new QuestionAnswer("approach", new[] { "safe" }, null, null) }, false).Status);

            var resumedEvents = new LeaseObservingEvents(store, root);
            ModelRequest? resumedRequest = null;
            var resumed = MakeTurn(resumedEvents, (request, _) =>
            {
                resumedRequest = request;
                return new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                    new TokenUsage(4, 1, 0, 0, 0), null,
                    new ProviderMetadata("fixture-provider", "scripted", null));
            }).Ask("continue", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, resumed.StopReason);
            Assert.NotNull(resumedRequest);
            Assert.Equal(state, resumedRequest.Continuation);
            Assert.True(resumedEvents.CompletionLeaseHeldBeforeCommit);
            Assert.True(resumedEvents.CompletionLeaseHeldAfterCommit);
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
    public void Failed_step_completion_append_releases_lease_and_leaves_only_collectable_artifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-step-publication-fail-" + Guid.NewGuid().ToString("N"));
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
            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var route = ModelRoute.DefaultForModel("scripted", "fixture-provider", "http://127.0.0.1:9901",
                ProviderFamily.OpenAiChatCompatible);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct,
                null, route.Id, route);
            var service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var failingEvents = new LeaseObservingEvents(store, root, failStepCompletion: true);
            var calls = 0;
            var turn = new ExplorerTurn((_, _) =>
            {
                calls++;
                Assert.True(artifacts.TryOpenLeaseProbe());
                return new ModelResponse(new ContentBlock[]
                {
                    new ToolCallBlock(ToolCallId.New(), "fixture-question", "user.ask",
                        QuestionnaireCodec.EncodeSchema(Schema))
                }, StopReason.ToolUse, new TokenUsage(10, 2, 0, 0, 0),
                    new ProviderState("fixture.kind", "{\"opaque\":\"orphan-state\"}"),
                    new ProviderMetadata("fixture-provider", "scripted", null));
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture-build"), selection,
                failingEvents, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                questionnaires: service);

            var result = turn.Ask("ask", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.Error, result.StopReason);
            Assert.Equal(1, calls);
            Assert.True(failingEvents.CompletionLeaseHeldBeforeCommit);
            Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelStepCompleted>());
            Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ToolCallRequested>());
            Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnAbandoned>());
            Assert.True(artifacts.TryOpenLeaseProbe()); // no leaked publication lease after the failed append

            var statePublication = Assert.Single(artifacts.Observed,
                item => item.Reference.Kind == ArtifactKind.ProviderOpaqueState);
            var responsePublication = Assert.Single(artifacts.Observed,
                item => item.Reference.Kind == ArtifactKind.ModelResponse);
            var sweep = new ArtifactGc(root).Sweep(journal, TimeSpan.Zero, false,
                DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);
            Assert.True(sweep.Deleted >= 2);
            Assert.False(innerArtifacts.Verify(statePublication.Reference.Hash, statePublication.Reference.Size));
            Assert.False(innerArtifacts.Verify(responsePublication.Reference.Hash, responsePublication.Reference.Size));
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class LeaseObservingArtifacts(IArtifactStore inner, string root)
        : IArtifactStore, IArtifactPublicationLease
    {
        public List<PublicationObservation> Observed { get; } = new();

        public IDisposable AcquirePublicationLease(CancellationToken cancellationToken) =>
            ((IArtifactPublicationLease)inner).AcquirePublicationLease(cancellationToken);

        public ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity)
        {
            var reference = inner.PutText(content, mediaType, kind, sensitivity);
            if (kind is ArtifactKind.ProviderOpaqueState or ArtifactKind.ModelResponse)
                Observed.Add(new PublicationObservation(reference, IsLeaseHeld(root)));
            return reference;
        }

        public string? GetText(ContentHash hash) => inner.GetText(hash);
        public bool Verify(ContentHash hash, long expectedSize) => inner.Verify(hash, expectedSize);
        public bool TryOpenLeaseProbe() => !IsLeaseHeld(root);

        private static bool IsLeaseHeld(string root)
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

    private sealed class LeaseObservingEvents(IEventStore inner, string root, bool failStepCompletion = false)
        : IEventStore
    {
        public bool CompletionLeaseHeldBeforeCommit { get; private set; }
        public bool CompletionLeaseHeldAfterCommit { get; private set; }

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
            if (!IsStepCompletion(evt))
            {
                inner.Append(sessionId, evt, durability, cancellationToken);
                return;
            }

            CompletionLeaseHeldBeforeCommit = IsLeaseHeld(root);
            if (failStepCompletion)
            {
                // Deliberately fail before the durable append to exercise release/cleanup.
                throw new InvalidOperationException("fixture step completion append failure");
            }

            inner.Append(sessionId, evt, durability, cancellationToken);
            CompletionLeaseHeldAfterCommit = IsLeaseHeld(root);
        }

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken cancellationToken)
        {
            if (events.Any(IsStepCompletion))
            {
                CompletionLeaseHeldBeforeCommit = IsLeaseHeld(root);
                if (failStepCompletion)
                    throw new InvalidOperationException("fixture step completion append failure");
                inner.AppendBatch(sessionId, events, durability, cancellationToken);
                CompletionLeaseHeldAfterCommit = IsLeaseHeld(root);
                return;
            }
            inner.AppendBatch(sessionId, events, durability, cancellationToken);
        }

        public long CurrentSequence(SessionId sessionId) => inner.CurrentSequence(sessionId);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) =>
            inner.ReadFrom(sessionId, fromSequenceInclusive);

        private static bool IsStepCompletion(DomainEvent evt) =>
            string.Equals(evt.Type.ToString(), "model_step.completed", StringComparison.Ordinal);

        private static bool IsLeaseHeld(string root)
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
