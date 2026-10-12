using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class ChangedFilesTests
{
    [Fact]
    public void Postimage_capture_is_optional_private_and_exact()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omni-postimage-" + Guid.NewGuid().ToString("N"));
        var artifacts = new FileArtifactStore(directory);
        try
        {
        var bytes = FileVersion.Encode("áéíóú ñ ¿¡\r\n", FileVersion.FileEncoding.Utf16BeBom);
        var reference = FilesystemPreimage.CaptureAfter(artifacts, bytes);
        Assert.NotNull(reference); Assert.Equal(bytes, FilesystemPreimage.Read(artifacts, reference));
        Assert.Throws<FilesystemPreimageException>(() => FilesystemPreimage.Read(artifacts, reference with { Size = reference.Size + 1 }));
        Assert.Null(FilesystemPreimage.CaptureAfter(null, bytes));
        Assert.Null(FilesystemPreimage.CaptureAfter(artifacts, new byte[2 * 1024 * 1024 + 1]));
        Assert.Null(FilesystemPreimage.CaptureAfter(artifacts, System.Text.Encoding.UTF8.GetBytes("api_key=sk-1234567890abcdefghijklmnopqrstuvwxyz")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Files_projection_rejects_stale_ownership_and_clears_on_switch()
    {
        var projection = new ChangedFilesProjection(); projection.Activate("one");
        var snapshot = new ChangedFilesSnapshot("one", 5, [new("id", "á.txt", "run", "lane", "M", 2, 1, true, null)]);
        Assert.True(projection.Apply(FilesJson.Decode(FilesJson.Encode(snapshot))!));
        Assert.False(projection.Apply(snapshot with { SessionId = "two" }));
        Assert.False(projection.Apply(snapshot with { BasedOnJournalSequence = 4 }));
        var model = Assert.IsType<ListWidgetModel>(projection.Widget("es").Build(ClientState.Empty(), WidgetSize.Normal));
        Assert.Equal("diff.open", Assert.Single(model.Rows).Action); Assert.Equal("id", model.Rows[0].Target);
        projection.Activate("two"); Assert.Null(projection.Snapshot);
        Assert.Equal(WidgetRelevance.None, projection.Widget("es").Evaluate(ClientState.Empty()));
    }

    [Fact]
    public void Worktree_undo_cli_messages_are_available_in_both_locales()
    {
        var spanish = Localization.Spanish();
        var english = Localization.English();
        foreach (var key in new[] { "worktree.undo.noSession", "worktree.undo.select", "worktree.undo.cancelled",
                     "worktree.undo.outcome.Applied", "worktree.undo.outcome.NoOp", "worktree.undo.outcome.Conflict",
                     "worktree.undo.outcome.Unknown", "worktree.undo.outcome.Unsupported",
                     "worktree.undo.state.Ready", "worktree.undo.state.Unsupported",
                     "worktree.undo.fileOutcome.Applied", "worktree.undo.fileOutcome.NotApplied",
                     "worktree.undo.fileOutcome.Conflict", "worktree.undo.fileOutcome.Unresolvable" })
        {
            Assert.False(string.IsNullOrWhiteSpace(spanish.Resolve(key)));
            Assert.False(string.IsNullOrWhiteSpace(english.Resolve(key)));
            Assert.NotEqual(spanish.Resolve(key), english.Resolve(key));
        }
    }

    [Fact]
    public void Unknown_and_missing_evidence_are_not_empty_preimages_or_clean_files()
    {
        var store = new InMemoryEventStore(); var codecs = EventCodecs.Create(); var session = SessionId.New();
        var stream = new EventStream(store, codecs, session); var run = TestRun.Open(stream, session);
        using var scope = ExecutionScope.Begin(new(run.RunId, run.RootTask, run.RootLane));
        var id = ToolCallId.New();
        void Prepare(ToolCallId call)
        {
            stream.Append(new ToolCallRequested(call, "fixture", "filesystem.write", "{}"));
            stream.Append(new ToolCallPrepared(call, "{}"));
            stream.Append(new ToolCallAuthorized(call));
        }
        Prepare(id);
        stream.Append(new ToolCallStarted(id, EffectClass.NonIdempotent, null) { TargetRef = "file.txt" });
        var row = Assert.Single(ChangedFilesReader.Read(store, codecs, session, null).Files);
        Assert.Equal("?", row.Status); Assert.Null(row.Added); Assert.False(row.DiffAvailable);
        stream.Append(new ToolCallSucceeded(id, "{}"));
        var terminal = store.ReadFrom(session, 1).Last();
        var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(terminal.PayloadJson)!.AsObject();
        legacyJson.Remove("afterStateRef"); legacyJson.Remove("AfterStateRef");
        var legacy = DomainEvent.Create(session, terminal.Type, 1, terminal.Causation, terminal.CorrelationId,
            terminal.RunId, terminal.TaskId, terminal.LaneId, terminal.TurnId, terminal.PlanItemId,
            id, terminal.ArtifactRefs, legacyJson.ToJsonString(), terminal.ExecutionId, terminal.Source);
        var reopenedLegacy = Assert.IsType<ToolCallSucceeded>(codecs.Decode(legacy));
        Assert.Equal(id, reopenedLegacy.ToolCallId); Assert.Null(reopenedLegacy.AfterStateRef);
        Assert.Equal("MetadataUnavailable", ChangedFilesReader.ReadDiff(store, codecs, session, id.ToString(), null).Reason);
        var unreadable = ChangedFilesReader.Read(store, new UnreadableCodecs(codecs), session, null);
        Assert.True(unreadable.Unavailable); Assert.Empty(unreadable.Files);
        Assert.True(unreadable.BasedOnJournalSequence > 0);
        Assert.Equal("JournalUnavailable", ChangedFilesReader.ReadDiff(store, new UnreadableCodecs(codecs), session, id.ToString(), null).Reason);
        Assert.False(ChangedFilesReader.ReadDiff(store, codecs, session, "other-id", null).Available);
        var other = ToolCallId.New();
        Prepare(other);
        stream.Append(new ToolCallStarted(other, EffectClass.NonIdempotent, null) { TargetRef = "../escape" });
        Assert.Single(ChangedFilesReader.Read(store, codecs, session, null).Files);
    }

    private sealed class UnreadableCodecs(OmniCore.Abstractions.IEventCodecRegistry inner) : OmniCore.Abstractions.IEventCodecRegistry
    {
        public OmniCore.Abstractions.IDomainEventCodec CodecFor(EventType type) => inner.CodecFor(type);
        public int CurrentVersion(EventType type) => inner.CurrentVersion(type);
        public DomainEventPayload Decode(DomainEvent evt) => throw new System.Text.Json.JsonException("private invalid payload");
    }

    [Fact]
    public void Diff_preserves_blank_lines_and_reports_a_bounded_preview()
    {
        var diff = new FileDiffSnapshot("s", "id", "file.txt", true, null, "a\n\n", "b\n\n");
        var rows = FileDiffPresentation.Build(FilesJson.DecodeDiff(FilesJson.Encode(diff))!).Rows;
        Assert.Contains(rows, r => r.Text == "- a"); Assert.Contains(rows, r => r.Text == "+ b");
        Assert.Contains(rows, r => r.Text == "  ");
        Assert.Equal("@@ -1,2 +1,2 @@", rows[0].Text);
        Assert.Equal("@@ -0,0 +1,1 @@", FileDiffPresentation.Build(diff with { Before = "", After = "new\n" }).Rows[0].Text);
        Assert.Contains("Line endings only", Assert.Single(FileDiffPresentation.Build(diff with { Before = "a\r\n", After = "a\n" }, locale: "en").Rows).Text);
        var large = diff with { Before = string.Join('\n', Enumerable.Range(0, 20)), After = "new" };
        var limited = FileDiffPresentation.Build(large, 8, locale: "en"); Assert.Equal(8, limited.Rows.Count);
        Assert.Contains("Partial preview", limited.Rows[^1].Text);
        Assert.Contains(FileDiffPresentation.Build(diff with { After = "b" }, locale: "en").Rows, r => r.Text.Contains("No newline at end of after"));
    }

    [Fact]
    public void Batch_history_reads_verified_CAS_after_workspace_removal_and_keeps_deletions()
    {
        var parent = Path.Combine(Path.GetTempPath(), "omni-batch-history-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "workspace");
        var artifacts = new FileArtifactStore(Path.Combine(parent, "cas"));
        Directory.CreateDirectory(root);
        try
        {
            var canonical = ProjectIdentity.CanonicalWorkspacePath(root);
            var session = SessionId.New();
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var stream = new EventStream(store, codecs, session);
            stream.Append(new SessionCreated(session, WorkspaceId.Of(canonical).ToString(), root,
                ProfileId.New(), DateTimeOffset.UtcNow));
            stream.Append(new WorkspaceRootEstablished(session, canonical, DateTimeOffset.UtcNow, "fixture-root-id"));
            var run = TestRun.Open(stream, session);
            var callId = ToolCallId.New();
            var owner = Guid.NewGuid().ToString("N");
            var proposal = new string('a', 64);
            var preimage = FileVersion.Encode("removed λ\n", FileVersion.FileEncoding.Utf8NoBom);
            var preHash = FileVersion.VersionToken(preimage);
            var common = Path.Combine(parent, "repo.git");
            var gitHead = new string('b', 40);
            var file = new { relativePath = "removed.txt", expectedPreSha256 = preHash,
                expectedPostSha256 = (string?)null, expectedPreMode = "100644", expectedPostMode = (string?)null,
                preimage = new { text = "removed λ\n", encoding = "Utf8NoBom" }, postimage = (object?)null };
            var payload = System.Text.Json.JsonSerializer.Serialize(new { version = 1, ownershipId = owner,
                proposalId = proposal, workspaceRoot = canonical, gitCommonDirectory = common,
                workspaceHead = gitHead, workspaceBranch = "main", laneHead = new string('c', 40),
                laneBranch = "lane", files = new[] { file } });
            var purpose = "m7-worktree-integration:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(canonical + "\n" + owner + "\n" + proposal))).ToLowerInvariant();
            var reference = artifacts.PutProtectedText(payload, purpose, "application/json", ArtifactKind.Patch);
            var metadata = System.Text.Json.JsonSerializer.Serialize(new { kind = "worktree.integration.batch", version = 1,
                toolCallId = callId.ToString(), ownershipId = owner, proposalId = proposal, workspaceRoot = canonical,
                gitCommonDirectory = common, workspaceHead = gitHead, workspaceBranch = "main",
                laneHead = new string('c', 40), laneBranch = "lane",
                files = new[] { new { relativePath = "removed.txt", expectedPreSha256 = preHash,
                    expectedPostSha256 = (string?)null, expectedPreMode = "100644", expectedPostMode = (string?)null } } });

            using (ExecutionScope.Begin(new(run.RunId, run.RootTask, run.RootLane)))
            {
                stream.Append(new ToolCallRequested(callId, "fixture", "worktree.integrate", "{}"));
                stream.Append(new ToolCallPrepared(callId, "{}"));
                stream.Append(new ToolCallAuthorized(callId));
                stream.Append(new WorktreeIntegrationStateRecorded(callId, owner, proposal,
                    WorktreeIntegrationState.Started,
                    [new WorktreeIntegrationFileStatus("removed.txt", ReconciliationOutcome.NotApplied)]));
                stream.Append(new ToolCallStarted(callId, EffectClass.Reconcilable, metadata) { BeforeStateRef = reference });
                stream.Append(new WorktreeIntegrationStateRecorded(callId, owner, proposal,
                    WorktreeIntegrationState.Completed,
                    [new WorktreeIntegrationFileStatus("removed.txt", ReconciliationOutcome.Applied)]));
                stream.Append(new ToolCallSucceeded(callId, "{}"));
            }

            var typedStart = Assert.IsType<ToolCallStarted>(store.ReadFrom(session, 1)
                .Select(codecs.Decode).Single(payload => payload is ToolCallStarted));
            var completed = store.ReadFrom(session, 1).Select(codecs.Decode).OfType<WorktreeIntegrationStateRecorded>()
                .Single(item => item.State == WorktreeIntegrationState.Completed);
            var snapshots = new WorkspaceSnapshotStore(canonical, store, codecs, artifacts, "fixture-root-id",
                verifyPhysicalRoot: false);
            var mismatched = typedStart with { ReconciliationJson = metadata.Replace(
                "\"expectedPreMode\":\"100644\"", "\"expectedPreMode\":\"100755\"", StringComparison.Ordinal) };
            Assert.Null(snapshots.ReadBatchEvidence(callId, mismatched, completed));
            var mismatchedIdentity = typedStart with { ReconciliationJson = metadata.Replace(
                "\"workspaceHead\":\"" + gitHead + "\"", "\"workspaceHead\":\"" + new string('d', 40) + "\"",
                StringComparison.Ordinal) };
            Assert.Null(snapshots.ReadBatchEvidence(callId, mismatchedIdentity, completed));
            var alteredPayload = payload.Replace("\"workspaceBranch\":\"main\"", "\"workspaceBranch\":\"other\"",
                StringComparison.Ordinal);
            var alteredPayloadRef = artifacts.PutProtectedText(alteredPayload, purpose, "application/json", ArtifactKind.Patch);
            Assert.Null(snapshots.ReadBatchEvidence(callId, typedStart with { BeforeStateRef = alteredPayloadRef }, completed));

            // A compensation is a new durable fact. It must not erase the historical integration
            // diff, even though UndoPlan would no longer offer that effect for another restore.
            var sourceEnvelope = store.ReadFrom(session, 1).Single(item => codecs.Decode(item) is ToolCallStarted);
            var undoId = Guid.NewGuid().ToString("N");
            var commandId = CommandId.New();
            var undoExecution = ExecutionId.New();
            var attribution = new UndoAttribution(sourceEnvelope.EventId, sourceEnvelope.RunId, sourceEnvelope.TaskId,
                sourceEnvelope.LaneId, sourceEnvelope.TurnId, sourceEnvelope.ExecutionId, undoExecution);
            using (ExecutionScope.Begin(new ExecutionScopeState(sourceEnvelope.RunId, sourceEnvelope.TaskId,
                       sourceEnvelope.LaneId, sourceEnvelope.TurnId, callId, undoExecution)))
            using (CausationScope.Begin(new CommandCausation(commandId)))
            {
                stream.Append(new UndoStarted(commandId, undoId, callId, attribution,
                    [new UndoFileIntent("removed.txt", FilesystemReconciliationMetadata.AbsentPreHash, preHash)],
                    "fixture-root-id"), DurabilityClass.Barrier);
                stream.Append(new UndoCompleted(commandId, undoId, callId, attribution, UndoOutcome.Applied,
                    [new UndoFileState("removed.txt", ReconciliationOutcome.Applied)], "fixture-root-id"), DurabilityClass.Barrier);
            }

            Directory.Delete(root, recursive: true);
            var row = Assert.Single(ChangedFilesReader.Read(store, codecs, session, artifacts).Files);
            Assert.Equal("D", row.Status);
            Assert.True(row.DiffAvailable);
            var diff = ChangedFilesReader.ReadDiff(store, codecs, session, callId + ":removed.txt", artifacts);
            Assert.True(diff.Available);
            Assert.Equal("removed λ\n", diff.Before);
            Assert.Equal("", diff.After);
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }
}
