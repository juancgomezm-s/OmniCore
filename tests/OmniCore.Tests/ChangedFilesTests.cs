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
        Assert.Contains("Line endings only", Assert.Single(FileDiffPresentation.Build(diff with { Before = "a\r\n", After = "a\n" }).Rows).Text);
        var large = diff with { Before = string.Join('\n', Enumerable.Range(0, 20)), After = "new" };
        var limited = FileDiffPresentation.Build(large, 8); Assert.Equal(8, limited.Rows.Count);
        Assert.Contains("Partial preview", limited.Rows[^1].Text);
        Assert.Contains(FileDiffPresentation.Build(diff with { After = "b" }).Rows, r => r.Text.Contains("No newline at end of after"));
    }
}
