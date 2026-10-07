// Offline integration regression: real SQLite/CAS, scripted provider, no credentials or network.
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class ExplorerFinalResponsePublicationLeaseTests
{
    [Fact]
    public void Final_CAS_refs_and_terminal_event_batch_share_lease_reopen_and_count_usage_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-final-publication-" + Guid.NewGuid().ToString("N"));
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
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var route = ModelRoute.DefaultForModel("scripted", "fixture-provider", "http://127.0.0.1:9901",
                ProviderFamily.OpenAiChatCompatible);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct,
                null, route.Id, route);
            var eventObserver = new LeaseObservingEvents(store, root);
            var providerCalls = 0;
            var turn = new ExplorerTurn((_, _) =>
            {
                providerCalls++;
                Assert.True(artifacts.TryOpenLeaseProbe()); // no GC lease held during provider work
                return new ModelResponse(new ContentBlock[] { new TextBlock("final answer") }, StopReason.EndTurn,
                    new TokenUsage(7, 5, 0, 0, 0), null,
                    new ProviderMetadata("fixture-provider", "scripted", null));
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture-build"), selection,
                eventObserver, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                maximumGenerationRequestAttempts: 1);

            var result = turn.Ask("ask", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal("final answer", result.FinalText);
            Assert.Equal(1, providerCalls);
            Assert.True(artifacts.TryOpenLeaseProbe());
            Assert.True(eventObserver.FinalBatchLeaseHeldBeforeCommit);
            Assert.True(eventObserver.FinalBatchLeaseHeldAfterCommit);
            Assert.Equal(new[] { "model.completed", "assistant_message.recorded" }, eventObserver.FinalBatchTypes);

            var step = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelStepCompleted>());
            var completed = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelCompleted>());
            var assistant = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<AssistantMessageRecorded>());
            Assert.Equal(7, step.Usage.Input);
            Assert.Equal(5, step.Usage.Output);
            Assert.NotNull(step.ResponseArtifact);
            Assert.NotNull(completed.ResponseArtifact);
            Assert.NotNull(assistant.ContentRef);

            var stepReference = step.ResponseArtifact!;
            var usageReference = completed.ResponseArtifact!;
            var textReference = assistant.ContentRef!;
            Assert.Equal("final answer", innerArtifacts.GetText(textReference.Hash));
            Assert.Contains("\"input\":7", innerArtifacts.GetText(usageReference.Hash));
            Assert.Contains("\"output\":5", innerArtifacts.GetText(usageReference.Hash));
            Assert.True(innerArtifacts.Verify(stepReference.Hash, stepReference.Size));
            Assert.True(innerArtifacts.Verify(usageReference.Hash, usageReference.Size));
            Assert.True(innerArtifacts.Verify(textReference.Hash, textReference.Size));

            var finalPublications = artifacts.Observed.Where(item =>
                item.Reference.Hash == usageReference.Hash || item.Reference.Hash == textReference.Hash).ToArray();
            Assert.Collection(finalPublications,
                item => Assert.True(item.LeaseHeldAfterPut),
                item => Assert.True(item.LeaseHeldAfterPut));

            var usageBeforeReopen = SessionUsageReporter.ReadConversation(store, codecs, innerArtifacts, session);
            Assert.Equal(1, usageBeforeReopen.ModelInvocations);
            Assert.Equal(0, usageBeforeReopen.IncompleteInvocations);
            Assert.Equal(new TokenTotals(7, 5, 0, 0), usageBeforeReopen.Tokens.Value);

            store.Close();
            store = new SqliteEventStore(journal);
            var usageAfterReopen = SessionUsageReporter.ReadConversation(store, codecs, innerArtifacts, session);
            var usageRepeated = SessionUsageReporter.ReadConversation(store, codecs, innerArtifacts, session);
            Assert.Equal(1, usageAfterReopen.ModelInvocations);
            Assert.Equal(0, usageAfterReopen.IncompleteInvocations);
            Assert.Equal(new TokenTotals(7, 5, 0, 0), usageAfterReopen.Tokens.Value);
            Assert.Equal(usageAfterReopen, usageRepeated);

            var sweep = new ArtifactGc(root).Sweep(journal, TimeSpan.Zero, false,
                DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);
            Assert.True(sweep.LiveReferenced >= 3);
            Assert.True(innerArtifacts.Verify(usageReference.Hash, usageReference.Size));
            Assert.True(innerArtifacts.Verify(textReference.Hash, textReference.Size));
            Assert.Equal("final answer", innerArtifacts.GetText(textReference.Hash));
            Assert.Contains("\"input\":7", innerArtifacts.GetText(usageReference.Hash));
            Assert.Equal(usageAfterReopen,
                SessionUsageReporter.ReadConversation(store, codecs, innerArtifacts, session));
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
    public void Failed_final_batch_keeps_step_usage_but_never_publishes_terminal_refs_and_gc_collects_only_them()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-final-publication-fail-" + Guid.NewGuid().ToString("N"));
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
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var route = ModelRoute.DefaultForModel("scripted", "fixture-provider", "http://127.0.0.1:9901",
                ProviderFamily.OpenAiChatCompatible);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct,
                null, route.Id, route);
            var failingEvents = new LeaseObservingEvents(store, root, failFinalBatch: true);
            var providerCalls = 0;
            var turn = new ExplorerTurn((_, _) =>
            {
                providerCalls++;
                Assert.True(artifacts.TryOpenLeaseProbe());
                return new ModelResponse(new ContentBlock[] { new TextBlock("final answer") }, StopReason.EndTurn,
                    new TokenUsage(7, 5, 0, 0, 0), null,
                    new ProviderMetadata("fixture-provider", "scripted", null));
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture-build"), selection,
                failingEvents, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                maximumGenerationRequestAttempts: 1);

            var result = turn.Ask("ask", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.Error, result.StopReason);
            Assert.Equal(1, providerCalls);
            Assert.True(failingEvents.FinalBatchLeaseHeldBeforeCommit);
            Assert.False(failingEvents.FinalBatchDelegated);
            Assert.True(artifacts.TryOpenLeaseProbe());
            var events = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            var step = Assert.Single(events.OfType<ModelStepCompleted>());
            Assert.Equal(7, step.Usage.Input);
            Assert.Equal(5, step.Usage.Output);
            Assert.NotNull(step.ResponseArtifact);
            Assert.Empty(events.OfType<ModelCompleted>());
            Assert.Empty(events.OfType<AssistantMessageRecorded>());
            Assert.Single(events.OfType<TurnAbandoned>());

            var finalUsage = Assert.Single(artifacts.Observed, item =>
                item.Reference.Kind == ArtifactKind.ModelResponse && item.Reference.MediaType == "application/vnd.omnicore.model-usage+json"
                && item.Reference.Hash != step.ResponseArtifact!.Hash);
            var finalText = Assert.Single(artifacts.Observed, item =>
                item.Reference.Kind == ArtifactKind.ModelResponse && item.Reference.MediaType == "text/markdown");
            Assert.True(finalUsage.LeaseHeldAfterPut);
            Assert.True(finalText.LeaseHeldAfterPut);
            Assert.True(innerArtifacts.Verify(step.ResponseArtifact!.Hash, step.ResponseArtifact.Size));

            var sweep = new ArtifactGc(root).Sweep(journal, TimeSpan.Zero, false,
                DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);
            Assert.False(innerArtifacts.Verify(finalUsage.Reference.Hash, finalUsage.Reference.Size));
            Assert.False(innerArtifacts.Verify(finalText.Reference.Hash, finalText.Reference.Size));
            Assert.True(innerArtifacts.Verify(step.ResponseArtifact.Hash, step.ResponseArtifact.Size));
            Assert.Contains("\"input\":7", innerArtifacts.GetText(step.ResponseArtifact.Hash));
            Assert.Equal(1, SessionUsageReporter.ReadConversation(store, codecs, innerArtifacts, session).ModelInvocations);
            Assert.Equal(0, SessionUsageReporter.ReadConversation(store, codecs, innerArtifacts, session).IncompleteInvocations);
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
    public void Invalid_usage_budget_guard_records_diagnostic_final_text_as_assistant_message_not_model_completed()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-final-publication-budget-" + Guid.NewGuid().ToString("N"));
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
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var route = ModelRoute.DefaultForModel("scripted", "fixture-provider", "http://127.0.0.1:9901",
                ProviderFamily.OpenAiChatCompatible);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct,
                null, route.Id, route);
            var eventObserver = new LeaseObservingEvents(store, root);
            var providerCalls = 0;
            var turn = new ExplorerTurn((_, _) =>
            {
                providerCalls++;
                Assert.True(artifacts.TryOpenLeaseProbe());
                return new ModelResponse(new ContentBlock[] { new TextBlock("answer") }, StopReason.EndTurn,
                    new TokenUsage(7, -1, 0, 0, 0), null,
                    new ProviderMetadata("fixture-provider", "scripted", null));
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture-build"), selection,
                eventObserver, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: new ModelPricing(1m, 1m), enforceDefaultSpendCaps: true,
                maximumGenerationRequestAttempts: 1);

            var result = turn.Ask("ask", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.Cancelled, result.StopReason);
            Assert.Equal(1, providerCalls);
            Assert.Null(result.ResponseArtifactId);
            Assert.StartsWith("Presupuesto agotado:", result.FinalText);
            Assert.Equal(1, providerCalls);
            Assert.True(artifacts.TryOpenLeaseProbe());
            Assert.True(eventObserver.FinalBatchLeaseHeldBeforeCommit);
            Assert.True(eventObserver.FinalBatchLeaseHeldAfterCommit);
            Assert.Equal(new[] { "assistant_message.recorded" }, eventObserver.FinalBatchTypes);

            var events = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            var step = Assert.Single(events.OfType<ModelStepCompleted>());
            Assert.Equal(7, step.Usage.Input);
            Assert.Equal(-1, step.Usage.Output);
            Assert.NotNull(step.ResponseArtifact);
            Assert.Empty(events.OfType<ModelCompleted>());
            var assistant = Assert.Single(events.OfType<AssistantMessageRecorded>());
            Assert.NotNull(assistant.ContentRef);

            var stepReference = step.ResponseArtifact!;
            var textReference = assistant.ContentRef!;
            Assert.Equal(result.FinalText, innerArtifacts.GetText(textReference.Hash));
            Assert.True(innerArtifacts.Verify(stepReference.Hash, stepReference.Size));
            Assert.True(innerArtifacts.Verify(textReference.Hash, textReference.Size));

            var finalPublications = artifacts.Observed.Where(item =>
                item.Reference.Hash == textReference.Hash).ToArray();
            Assert.Collection(finalPublications,
                item => Assert.True(item.LeaseHeldAfterPut));

            store.Close();
            store = new SqliteEventStore(journal);
            var reopened = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            var reopenedAssistant = Assert.Single(reopened.OfType<AssistantMessageRecorded>());
            Assert.Equal(result.FinalText, innerArtifacts.GetText(reopenedAssistant.ContentRef!.Hash));
            Assert.Empty(reopened.OfType<ModelCompleted>());
            var sequenceBeforeRead = store.CurrentSequence(session);
            var usageBeforeGc = SessionUsageReporter.ReadConversation(store, codecs, innerArtifacts, session);
            new ArtifactGc(root).Sweep(journal, TimeSpan.Zero, false,
                DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);
            Assert.True(innerArtifacts.Verify(textReference.Hash, textReference.Size));
            Assert.True(innerArtifacts.Verify(stepReference.Hash, stepReference.Size));
            Assert.Equal(result.FinalText, innerArtifacts.GetText(textReference.Hash));
            Assert.Contains("\"output\":-1", innerArtifacts.GetText(stepReference.Hash));
            Assert.Equal(usageBeforeGc, SessionUsageReporter.ReadConversation(store, codecs, innerArtifacts, session));
            Assert.Equal(sequenceBeforeRead, store.CurrentSequence(session));
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
            if (kind == ArtifactKind.ModelResponse)
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

    private sealed class LeaseObservingEvents(IEventStore inner, string root, bool failFinalBatch = false)
        : IEventStore, IWorkspaceJournalReader
    {
        public bool FinalBatchLeaseHeldBeforeCommit { get; private set; }
        public bool FinalBatchLeaseHeldAfterCommit { get; private set; }
        public bool FinalBatchDelegated { get; private set; }
        public IReadOnlyList<string> FinalBatchTypes { get; private set; } = Array.Empty<string>();

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken) => inner.Append(sessionId, evt, durability, cancellationToken);

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken cancellationToken)
        {
            var finalTypes = events.Select(evt => evt.Type.ToString())
                .Where(type => type is "model.completed" or "assistant_message.recorded").ToArray();
            if (finalTypes.Length == 0)
            {
                inner.AppendBatch(sessionId, events, durability, cancellationToken);
                return;
            }

            FinalBatchTypes = finalTypes;
            FinalBatchLeaseHeldBeforeCommit = LeaseHeld(root);
            if (failFinalBatch)
                throw new InvalidOperationException("fixture final response append failure");

            inner.AppendBatch(sessionId, events, durability, cancellationToken);
            FinalBatchDelegated = true;
            FinalBatchLeaseHeldAfterCommit = LeaseHeld(root);
        }

        public long CurrentSequence(SessionId sessionId) => inner.CurrentSequence(sessionId);
        public IReadOnlyList<DomainEvent> ReadEvents(EventType type) =>
            ((IWorkspaceJournalReader)inner).ReadEvents(type);
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
