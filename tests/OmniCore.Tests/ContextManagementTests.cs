using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class ContextManagementTests
{
    private static readonly ExecutionFingerprint Fingerprint = new("model", "harness", "tools", "policy",
        "overrides", "test");

    private static ContextProvenance Provenance(ContributionCategory category = ContributionCategory.Conversation,
        IReadOnlyList<string>? refs = null, bool sensitive = false) =>
        new("test", category, "test", ScopeLevel.Run, sensitive, refs);

    [Fact]
    public void Two_hundred_turn_context_stays_bounded_and_keeps_working_state()
    {
        var entries = new List<ConversationContextEntry>();
        for (var i = 0; i < 200; i++)
            entries.Add(new ConversationContextEntry("turn-" + i, i % 2 == 0 ? ContextItemKind.UserMessage : ContextItemKind.AssistantMessage,
                "turn " + i + " " + new string('x', 240), i == 0));
        var policy = new ContextManagementPolicy(100, 80, 8, 16, 1000);
        var materializer = new ContextMaterializer(new FakeTokenCounter(), new IContextContributor[]
        {
            new SessionConversationContributor(entries),
            new WorkingStateContributor("Plan rev.7; P3 in progress; pending tests"),
        }, policy: policy);
        var request = new MaterializeRequest(SessionId.New(), RunId.New(), null, null, null, 200,
            Fingerprint);

        var snapshot = materializer.MaterializeWithinBudget(request, TestContext.Current.CancellationToken, 400);

        Assert.Contains(snapshot.Items, item => item.Kind == ContextItemKind.WorkingState
            && item.Content.Contains("P3 in progress", StringComparison.Ordinal));
        Assert.True(snapshot.TokenCount <= 400, $"snapshot tokens {snapshot.TokenCount}");
        Assert.True(snapshot.Items.Count < 200, $"items {snapshot.Items.Count}");
        Assert.Contains(snapshot.Diagnostics, d => d.Decision is ContextDecision.Compressed
            or ContextDecision.OmittedByBudget);
    }

    [Fact]
    public void Large_tool_output_is_externalized_and_re_readable_by_cas_reference()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-context-" + Guid.NewGuid().ToString("N"));
        var artifacts = new FileArtifactStore(root);
        try
        {
            var output = new string('a', 500);
            var item = new ContextItem("tool-1", ContextItemKind.ToolResult, output, 0,
                ContextPriority.Normal, RetentionPolicy.ConversationWindow,
                Provenance(ContributionCategory.ToolObservations));
            var materializer = new ContextMaterializer(new FakeTokenCounter(),
                new IContextContributor[] { new OneItemContributor(item) }, artifacts,
                new ContextManagementPolicy(40, 20, 4, 10, 300));
            var snapshot = materializer.Materialize(new MaterializeRequest(SessionId.New(), RunId.New(), null,
                null, null, 0, Fingerprint), TestContext.Current.CancellationToken);
            var externalized = Assert.Single(snapshot.Items);
            Assert.Contains("re-read", externalized.Content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("artifact.read", externalized.Content, StringComparison.Ordinal);
            var artifactRef = Assert.Single(externalized.Provenance.Refs!, r => r.StartsWith("artifact=", StringComparison.Ordinal));
            Assert.Equal(output, new ContextArtifactReferenceResolver(artifacts).Read(artifactRef));
            Assert.Contains(snapshot.Diagnostics, d => d.Decision == ContextDecision.Externalized
                && d.Provenance.Refs!.Contains(artifactRef));
            var belowThreshold = new ContextMaterializer(new FakeTokenCounter(),
                new IContextContributor[] { new OneItemContributor(item) }, artifacts,
                new ContextManagementPolicy(600, 20, 4, 10, 300)).Materialize(
                    new MaterializeRequest(SessionId.New(), RunId.New(), null, null, null, 0, Fingerprint),
                    TestContext.Current.CancellationToken);
            Assert.DoesNotContain(belowThreshold.Diagnostics, d => d.Decision == ContextDecision.Externalized);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Prune_and_compress_are_deterministic_and_retain_provenance()
    {
        var first = new ContextItem("old", ContextItemKind.File, "old read", 4, ContextPriority.Normal,
            RetentionPolicy.ConversationWindow, Provenance(ContributionCategory.File,
                new[] { "path=src/a.cs", "version=sha256:abc" }));
        var second = new ContextItem("new", ContextItemKind.File, "new read", 4, ContextPriority.Normal,
            RetentionPolicy.ConversationWindow, Provenance(ContributionCategory.File,
                new[] { "path=src/a.cs", "version=sha256:abc" }));
        var kept = ContextCompaction.PruneSupersededFileReads(new[] { first, second }, out var diagnostics);
        Assert.Equal(new[] { "new" }, kept.Select(item => item.Id));
        Assert.Equal(ContextDecision.Pruned, Assert.Single(diagnostics, d => d.ItemId == "old").Decision);
        Assert.Equal(first.Provenance, Assert.Single(diagnostics, d => d.ItemId == "old").Provenance);

        var oldConversation = new ContextItem("history", ContextItemKind.AssistantMessage,
            "assistant tool call: " + new string('x', 300), 0, ContextPriority.Normal,
            RetentionPolicy.ConversationWindow, Provenance());
        var compressed = ContextCompaction.Compress(oldConversation, 72);
        Assert.StartsWith("assistant tool call:", compressed.Content);
        Assert.Contains("older content compressed", compressed.Content);
        Assert.Equal(oldConversation.Provenance, compressed.Provenance);
        var toolCall = new ContextItem("call", ContextItemKind.AssistantMessage,
            "Assistant: tool call filesystem.read {\"path\":\"" + new string('x', 200) + "\"}",
            0, ContextPriority.Normal, RetentionPolicy.ConversationWindow, Provenance());
        var compressedCall = ContextCompaction.Compress(toolCall, 72);
        Assert.Contains("tool call filesystem.read", compressedCall.Content);
        Assert.Contains("older content compressed", compressedCall.Content);

        var threeEntries = new SessionConversationContributor(new[]
        {
            new ConversationContextEntry("c1", ContextItemKind.AssistantMessage, new string('a', 120)),
            new ConversationContextEntry("c2", ContextItemKind.AssistantMessage, new string('b', 120)),
            new ConversationContextEntry("c3", ContextItemKind.AssistantMessage, new string('c', 120)),
        });
        ContextSnapshot WithTail(int tail) => new ContextMaterializer(new FakeTokenCounter(),
            new IContextContributor[] { threeEntries }, policy: new ContextManagementPolicy(1000, 40, tail, 8, 500))
            .Materialize(new MaterializeRequest(SessionId.New(), RunId.New(), null, null, null, 0, Fingerprint),
                TestContext.Current.CancellationToken);
        var keepAll = WithTail(3);
        var compressOlder = WithTail(1);
        Assert.Equal(120, keepAll.Items[0].Content.Length);
        Assert.Contains(compressOlder.Diagnostics, d => d.Decision == ContextDecision.Compressed);
        Assert.True(compressOlder.Items[0].Content.Length < keepAll.Items[0].Content.Length);
        Assert.False(ContextCompaction.ShouldCompact(3, new ContextManagementPolicy(1, 1, 1, 4, 50)));
        Assert.True(ContextCompaction.ShouldCompact(4, new ContextManagementPolicy(1, 1, 1, 4, 50)));
    }

    [Fact]
    public async System.Threading.Tasks.Task Meta_model_uses_provider_records_typed_events_and_redacts_summary_artifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-meta-" + Guid.NewGuid().ToString("N"));
        var artifacts = new FileArtifactStore(root);
        var events = new RecordingContextEventSink();
        var provider = new ScriptedSummaryProvider("Decision: use safe API key [REDACTED]");
        var selection = new ModelSelection(new ModelIdValue("local-small"), 4096, ToolMode.Direct, null);
        try
        {
            var service = new MetaModelService(provider, artifacts, events, selection,
                text => text.Replace("SECRET-123", "[REDACTED]", StringComparison.Ordinal));
            var summary = await service.SummarizeAsync(RunId.New(), "CompressContext",
                "Decision: keep this. credential SECRET-123", 1000, TestContext.Current.CancellationToken);
            Assert.Contains("[REDACTED]", summary);
            Assert.DoesNotContain("SECRET-123", provider.LastRequest!.Messages[0].Content
                .OfType<TextBlock>().Single().Text);
            Assert.Equal(2, events.Payloads.Count);
            Assert.IsType<MetaModelInvocationStarted>(events.Payloads[0]);
            var completed = Assert.IsType<MetaModelInvocationCompleted>(events.Payloads[1]);
            Assert.Equal(service.Fingerprint("CompressContext"), completed.ModelFingerprint);
            Assert.DoesNotContain("SECRET-123", artifacts.GetText(completed.OutputArtifact.Hash));
            var bounded = await service.SummarizeAsync(RunId.New(), "CompressContext", "safe input", 10,
                TestContext.Current.CancellationToken);
            Assert.True(bounded.Length <= 11);
            Assert.Equal(4, events.Payloads.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SQLite_journal_reopened_by_a_second_instance_restores_checkpoints_and_materializes_identically()
    {
        // M4: cierre de sesión larga — la restauración debe funcionar reabriendo el journal de
        // SQLite desde otra instancia (mismo proceso y fichero), simulando lo que hace el
        // Host al arrancar de nuevo: replay del journal + artifact content-addressed, mismo
        // fingerprint de contexto, y el journal sigue siendo append-only.
        var root = Path.Combine(Path.GetTempPath(), "omnicore-sqlite-restore-" + Guid.NewGuid().ToString("N"));
        var journalPath = Path.Combine(root, "journal.db");
        Directory.CreateDirectory(root);
        try
        {
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var artifactBlobs = 0;

            // --- "Instancia A": la sesión original, que escribe y cierra limpio.
            ContextCheckpointRecorded checkpointA;
            long sequenceA;
            string firstFingerprint;
            string[] payloadsA;
            {
                var artifacts = new FileArtifactStore(root);
                var store = new SqliteEventStore(journalPath);
                var stream = new EventStream(store, codecs, session);
                stream.Append(new SessionCreated(session, WorkspaceId.Of("ws-restore").ToString(), "ws-restore",
                    ProfileId.New(), DateTimeOffset.UtcNow));
                var run = TestRun.Open(store, session).RunId;
                stream.Append(new UserInputReceived(run, "[\"cuenta algo\"]", null, "user"));

                var checkpointArtifact = artifacts.PutText("""{"summary":"checkpoint durable"}""",
                    "application/json", ArtifactKind.ContextSnapshot, Sensitivity.Sensitive);
                artifactBlobs = Directory.GetFiles(Path.Combine(root, "blobs"), "*", SearchOption.AllDirectories)
                    .Length;
                stream.Append(new ContextCheckpointRecorded("cp-sqlite-1", run, store.CurrentSequence(session),
                    checkpointArtifact, "meta-fingerprint"));

                payloadsA = store.ReadFrom(session, 1).Select(e => e.PayloadJson).ToArray();
                sequenceA = store.CurrentSequence(session);
                checkpointA = Assert.IsType<ContextCheckpointRecorded>(
                    codecs.Decode(store.ReadFrom(session, 1).Last()));
                var first = Materialize(store, artifacts, session, run, codecs, checkpointA);
                firstFingerprint = first.SnapshotFingerprint;
                Assert.Contains(first.Items, i => i.Kind == ContextItemKind.WorkingState);
                store.Close();
            }

            // --- "Instancia B": reabre el MISMO fichero con una instancia nueva y restaura.
            long sequenceB;
            string secondFingerprint;
            {
                var artifacts = new FileArtifactStore(root); // nueva instancia: artifacts por hash
                var store = new SqliteEventStore(journalPath);
                var payloadsB = store.ReadFrom(session, 1).Select(e => e.PayloadJson).ToArray();
                sequenceB = store.CurrentSequence(session);
                Assert.Equal(sequenceA, sequenceB);                      // nada se perdió al reabrir
                Assert.Equal(payloadsA, payloadsB);                       // y el orden es idéntico

                var checkpointB = Assert.IsType<ContextCheckpointRecorded>(
                    codecs.Decode(store.ReadFrom(session, 1).Last()));
                Assert.Equal(checkpointA.CheckpointId, checkpointB.CheckpointId);
                Assert.Equal(checkpointA.ThroughEventSequence, checkpointB.ThroughEventSequence);
                Assert.Equal(checkpointA.CheckpointArtifact.Hash, checkpointB.CheckpointArtifact.Hash);

                var second = Materialize(store, artifacts, session, checkpointB.RunId, codecs, checkpointB);
                secondFingerprint = second.SnapshotFingerprint;
                Assert.Contains(second.Items, i => i.Kind == ContextItemKind.Summary
                    && i.Content.Contains("checkpoint durable", StringComparison.Ordinal));
                Assert.Equal(artifactBlobs,
                    Directory.GetFiles(Path.Combine(root, "blobs"), "*", SearchOption.AllDirectories).Length);

                // El journal sigue siendo append-only en la instancia restaurada: puede seguir usándose.
                var stream = new EventStream(store, codecs, session);
                stream.Append(new UserInputReceived(checkpointB.RunId, "[\"continúa\"]", null, "user"));
                Assert.Equal(sequenceA + 1, store.CurrentSequence(session));
                store.Close();
            }

            // La restauración es determinista: mismo checkpoint ⇒ mismo fingerprint de contexto.
            Assert.Equal(firstFingerprint, secondFingerprint);
        }
        finally
        {
            // Close() devuelve la conexión al pool de Microsoft.Data.Sqlite: sin liberar el pool de
            // ESTE journal, Windows retiene el handle y no se puede borrar el directorio temporal.
            // ClearPool (no ClearAllPools): a la vez rompería conexiones en reposo de otros tests
            // en paralelo, igual que hace FileAuditSinkTests con su propio journal.
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(
                new Microsoft.Data.Sqlite.SqliteConnection("DataSource=" + journalPath));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Checkpoint_is_append_only_and_restart_materialization_has_same_fingerprint()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-checkpoint-" + Guid.NewGuid().ToString("N"));
        var artifacts = new FileArtifactStore(root);
        try
        {
            var originalArtifact = artifacts.PutText("canonical tool output", "text/plain", ArtifactKind.ToolOutput,
                Sensitivity.Normal);
            var originalFiles = Directory.GetFiles(Path.Combine(root, "blobs"), "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session).RunId;
            var originalEvents = store.ReadFrom(session, 1).ToArray();
            var checkpointArtifact = artifacts.PutText("""{"summary":"tested"}""", "application/json",
                ArtifactKind.ContextSnapshot, Sensitivity.Sensitive);
            var stream = new EventStream(store, codecs, session);
            stream.Append(new ContextCheckpointRecorded("cp-1", run, store.CurrentSequence(session),
                checkpointArtifact, "meta-fingerprint"));
            var checkpointEvent = Assert.IsType<ContextCheckpointRecorded>(
                codecs.Decode(store.ReadFrom(session, 1).Last()));
            var checkpointJson = System.Text.Json.JsonDocument.Parse(
                artifacts.GetText(checkpointEvent.CheckpointArtifact.Hash)!);
            var checkpointSummary = checkpointJson.RootElement.GetProperty("summary").GetString()!;
            checkpointJson.Dispose();
            var contributors = new IContextContributor[]
            {
                new WorkingStateContributor("Plan rev.3; P2 active"),
                new ContextCheckpointContributor(checkpointEvent.CheckpointId,
                    checkpointEvent.ThroughEventSequence, checkpointSummary, checkpointEvent.CheckpointArtifact),
            };
            var request = new MaterializeRequest(session, run, null, null, null,
                store.CurrentSequence(session), Fingerprint);
            var first = new ContextMaterializer(new FakeTokenCounter(), contributors)
                .Materialize(request, TestContext.Current.CancellationToken);
            // Recreate the materializer as a restarted Host would after replaying the durable checkpoint.
            var restarted = new ContextMaterializer(new FakeTokenCounter(), contributors)
                .Materialize(request, TestContext.Current.CancellationToken);
            var afterFiles = Directory.GetFiles(Path.Combine(root, "blobs"), "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);

            Assert.Equal(originalEvents.Length + 1, store.ReadFrom(session, 1).Count);
            Assert.Equal(originalEvents.Select(e => e.PayloadJson), store.ReadFrom(session, 1)
                .Take(originalEvents.Length).Select(e => e.PayloadJson));
            Assert.Equal(first.SnapshotFingerprint, restarted.SnapshotFingerprint);
            Assert.Contains(restarted.Items, i => i.Kind == ContextItemKind.WorkingState
                && i.Content == "Plan rev.3; P2 active");
            Assert.Contains(restarted.Items, i => i.Kind == ContextItemKind.Summary
                && i.Content.Contains("tested", StringComparison.Ordinal));
            Assert.Equal(originalFiles.Count + 1, afterFiles.Count);
            foreach (var (path, bytes) in originalFiles) Assert.Equal(bytes, afterFiles[path]);
            Assert.Equal("canonical tool output", artifacts.GetText(originalArtifact.Hash));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Two_hundred_turn_scripted_run_compacts_history_and_keeps_working_state_live()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-context-run-" + Guid.NewGuid().ToString("N"));
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var opened = TestRun.Open(store, session);
        var originalEvents = store.ReadFrom(session, 1).ToArray();
        var artifacts = new FileArtifactStore(root);
        var tools = OmniHost.CreateExplorerTools();
        var executor = OmniHost.CreateExplorerExecutor(tools.Catalog(), Path.GetTempPath());
        var policy = new ContextManagementPolicy(1000, 180, 8, 24, 1200);
        var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 4, GuidanceLevel.Off,
            1, PlanControl.RuntimeDriven, 4, policy);
        var provider = new ScriptedSummaryProvider("Facts: durable decision from old history.");
        ModelRequest? lastRequest = null;
        var turn = new OmniCore.Host.ExplorerTurn((request, cancellationToken) =>
        {
            lastRequest = request;
            var answer = string.Join(' ', Enumerable.Repeat("result", 60));
            return new ModelResponse(new ContentBlock[] { new TextBlock(answer) }, StopReason.EndTurn,
                new TokenUsage(10, 60, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null));
        }, executor, tools.Catalog(), new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            Fingerprint, new ModelSelection(new ModelIdValue("scripted"), 3000, ToolMode.Direct, null), store,
            codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(), harness,
            metaModelProvider: provider);
        try
        {
            for (var i = 0; i < 200; i++)
            {
                var question = "turn-" + i + " " + string.Join(' ', Enumerable.Repeat("question", 40));
                var result = turn.Ask(question, "Keep this run focused.", session, opened.RunId, opened.RootLane,
                    "Plan rev.7; P3 in progress; pending validation", CancellationToken.None);
                Assert.Equal(StopReason.EndTurn, result.StopReason);
            }

            var persisted = store.ReadFrom(session, 1);
            Assert.Equal(originalEvents.Select(e => e.PayloadJson), persisted.Take(originalEvents.Length)
                .Select(e => e.PayloadJson));
            Assert.True(persisted.Count(e => e.Type.Equals(EventType.Of("context.checkpoint_recorded"))) > 0);
            Assert.True(persisted.Count(e => e.Type.Equals(EventType.Of("meta_model.invocation_completed"))) > 0);
            var latestCheckpoint = persisted.Reverse().Select(evt => codecs.Decode(evt))
                .OfType<ContextCheckpointRecorded>().First();
            using var checkpointJson = System.Text.Json.JsonDocument.Parse(
                artifacts.GetText(latestCheckpoint.CheckpointArtifact.Hash)!);
            Assert.True(latestCheckpoint.ThroughEventSequence > 0);
            Assert.True(checkpointJson.RootElement.GetProperty("compactedThroughItemIndex").GetInt32() > 0);
            Assert.Contains(checkpointJson.RootElement.GetProperty("facts").EnumerateArray(),
                fact => fact.GetString()!.Contains("durable decision", StringComparison.Ordinal));
            Assert.Contains("P3 in progress", lastRequest!.Instructions);
            Assert.Contains("durable decision from old history", lastRequest.Instructions);
            Assert.True(lastRequest.Instructions!.Length < 6000, $"context chars {lastRequest.Instructions.Length}");
            Assert.True(lastRequest.Messages.Count <= 20, $"messages {lastRequest.Messages.Count}");
            var latestSnapshot = persisted.Reverse().Select(evt => codecs.Decode(evt))
                .OfType<TurnStarted>().First().ContextSnapshotRef!;
            using var snapshotJson = System.Text.Json.JsonDocument.Parse(artifacts.GetText(latestSnapshot.Hash)!);
            Assert.True(snapshotJson.RootElement.GetProperty("tokenCount").GetInt32() <= 3000);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Context_thresholds_are_derived_from_profile_and_config_change_behavior()
    {
        var profile = new EffectiveModelProfile("any-model", 8192, 8192, 1024, Array.Empty<string>(),
            new[] { ToolCallFormat.Native }, false, new Dictionary<string, double>());
        var policy = new HarnessPolicyResolver().Resolve(profile).ContextManagement;
        Assert.Equal(2048, policy.ExternalizeAboveCharacters);
        Assert.True(policy.RecentTailItems < ContextManagementPolicy.Default.RecentTailItems);
        var overridden = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 1, GuidanceLevel.Off,
            0, PlanControl.RuntimeDriven, 4, new ContextManagementPolicy(8, 8, 2, 4, 50));
        Assert.Equal(8, overridden.ContextManagement.ExternalizeAboveCharacters);
        Assert.Equal(2, overridden.ContextManagement.RecentTailItems);
    }

    private sealed class OneItemContributor(ContextItem item) : IContextContributor
    {
        public System.Threading.Tasks.Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request,
            CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>(
            new[] { item });
    }

    private sealed class RecordingContextEventSink : IContextEventSink
    {
        public List<DomainEventPayload> Payloads { get; } = new();
        public ValueTask AppendAsync(DomainEventPayload payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Payloads.Add(payload);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedSummaryProvider(string summary) : IModelProvider
    {
        public ProviderCapabilities Capabilities { get; } = ProviderCapabilities.Local();
        public ModelRequest? LastRequest { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastRequest = request;
            await System.Threading.Tasks.Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ResponseCompleted(new ModelResponse(new ContentBlock[] { new TextBlock(summary) },
                StopReason.EndTurn, new TokenUsage(0, 0, 0, 0, 0), null,
                new ProviderMetadata("scripted", "test-meta", null)));
        }
    }
    private static ContextSnapshot Materialize(IEventStore store, FileArtifactStore artifacts,
        SessionId session, RunId run, EventCodecs codecs, ContextCheckpointRecorded checkpoint)
    {
        using var checkpointJson = System.Text.Json.JsonDocument.Parse(
            artifacts.GetText(checkpoint.CheckpointArtifact.Hash)!);
        var summary = checkpointJson.RootElement.GetProperty("summary").GetString()!;
        var contributors = new IContextContributor[]
        {
            new WorkingStateContributor("Plan rev.3; P2 active"),
            new ContextCheckpointContributor(checkpoint.CheckpointId, checkpoint.ThroughEventSequence,
                summary, checkpoint.CheckpointArtifact),
        };
        var request = new MaterializeRequest(session, run, null, null, null,
            store.CurrentSequence(session), Fingerprint);
        return new ContextMaterializer(new FakeTokenCounter(), contributors)
            .Materialize(request, TestContext.Current.CancellationToken);
    }

}
