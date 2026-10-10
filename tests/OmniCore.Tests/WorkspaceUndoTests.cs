namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

public sealed class WorkspaceUndoTests
{
    [Fact]
    public void Undo_restores_exact_attributed_preimage_and_is_idempotent()
    {
        using var fixture = new Fixture();
        var before = FileVersion.Encode("before α\r\n", FileVersion.FileEncoding.Utf16BeBom);
        var after = FileVersion.Encode("after β\n", FileVersion.FileEncoding.Utf16BeBom);
        File.WriteAllBytes(Path.Combine(fixture.Root, "target.txt"), after);
        var call = fixture.AddSingleEffect("target.txt", before, after);
        var service = fixture.Service(PermissionDecision.Allow);

        var result = service.Undo(fixture.Session, call, CancellationToken.None);

        Assert.Equal(UndoOutcome.Applied, result.Outcome);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(fixture.Root, "target.txt")));
        Assert.Single(result.Files);
        var replay = fixture.Codecs.Decode(fixture.Store.ReadFrom(fixture.Session, 1).Last());
        Assert.IsType<UndoCompleted>(replay);
        Assert.Equal(UndoOutcome.Unsupported, service.Undo(fixture.Session, call, CancellationToken.None).Outcome);
    }

    [Fact]
    public void Undo_rejects_foreign_edit_and_other_session_even_when_hashes_match()
    {
        using var fixture = new Fixture();
        var before = System.Text.Encoding.UTF8.GetBytes("before\n");
        var after = System.Text.Encoding.UTF8.GetBytes("after\n");
        File.WriteAllBytes(Path.Combine(fixture.Root, "target.txt"), after);
        var call = fixture.AddSingleEffect("target.txt", before, after);
        var otherSession = fixture.AddForeignSessionWithSameEffect(call, before, after);
        var service = fixture.Service(PermissionDecision.Allow);

        Assert.Equal(UndoOutcome.Unsupported, service.Undo(otherSession, call, CancellationToken.None).Outcome);
        Assert.Equal(after, File.ReadAllBytes(Path.Combine(fixture.Root, "target.txt")));
        File.WriteAllText(Path.Combine(fixture.Root, "target.txt"), "foreign edit\n");
        Assert.Equal(UndoOutcome.Conflict, service.Undo(fixture.Session, call, CancellationToken.None).Outcome);
        Assert.Equal("foreign edit\n", File.ReadAllText(Path.Combine(fixture.Root, "target.txt")));
    }

    [Fact]
    public void Denied_workspace_write_does_not_change_target()
    {
        using var fixture = new Fixture();
        var before = System.Text.Encoding.UTF8.GetBytes("before\n");
        var after = System.Text.Encoding.UTF8.GetBytes("after\n");
        File.WriteAllBytes(Path.Combine(fixture.Root, "target.txt"), after);
        var call = fixture.AddSingleEffect("target.txt", before, after);

        var result = fixture.Service(PermissionDecision.Deny).Undo(fixture.Session, call, CancellationToken.None);

        Assert.Equal(UndoOutcome.Unsupported, result.Outcome);
        Assert.Equal(after, File.ReadAllBytes(Path.Combine(fixture.Root, "target.txt")));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "omnicore-undo-test-" + Guid.NewGuid().ToString("N"));
        public SessionId Session { get; } = SessionId.New();
        public InMemoryEventStore Store { get; } = new();
        public IEventCodecRegistry Codecs { get; } = EventCodecs.Create();
        public string Identity { get; }
        public FileArtifactStore Artifacts { get; }
        public TestRun.Opened Run { get; }
        private readonly EventStream _stream;

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Identity = WorkspaceRootIdentity.Establish(Root);
            Artifacts = new FileArtifactStore(Path.Combine(Root, "cas"));
            _stream = new EventStream(Store, Codecs, Session);
            var canonical = ProjectIdentity.CanonicalWorkspacePath(Root);
            _stream.Append(new SessionCreated(Session, WorkspaceId.Of(canonical).ToString(), Root,
                ProfileId.New(), DateTimeOffset.UtcNow));
            _stream.Append(new WorkspaceRootEstablished(Session, canonical, DateTimeOffset.UtcNow, Identity));
            Run = TestRun.Open(_stream, Session);
        }

        public ToolCallId AddSingleEffect(string path, byte[] before, byte[] after)
        {
            var call = ToolCallId.New();
            var beforeRef = FilesystemPreimage.Capture(new ReconciliationSpec(
                before is null ? FilesystemReconciliationMetadata.AbsentPreHash : FileVersion.VersionToken(before),
                FileVersion.VersionToken(after), null), Artifacts, before).BeforeStateRef;
            var afterRef = FilesystemPreimage.CaptureAfter(Artifacts, after);
            var stream = _stream;
            var execution = ExecutionId.New();
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(Run.RunId, Run.RootTask,
                Run.RootLane, TurnId.New(), call, execution));
            stream.Append(new ToolCallRequested(call, "fixture", "filesystem.write", "{}"));
            stream.Append(new ToolCallPrepared(call, "{}"));
            stream.Append(new PermissionEvaluated(call, PermissionDecision.Allow, "{}", null));
            stream.Append(new ToolCallAuthorized(call));
            var beforeHash = before is null ? FilesystemReconciliationMetadata.AbsentPreHash : FileVersion.VersionToken(before);
            stream.Append(new ToolCallStarted(call, EffectClass.NonIdempotent,
                FilesystemReconciliationMetadata.Encode(path, beforeHash, FileVersion.VersionToken(after)))
            { TargetRef = path, BeforeStateRef = beforeRef, Reversibility = Reversibility.Reversible }, DurabilityClass.Barrier);
            stream.Append(new ToolCallSucceeded(call, "{}") { AfterStateRef = afterRef });
            return call;
        }

        public SessionId AddForeignSessionWithSameEffect(ToolCallId call, byte[] before, byte[] after)
        {
            // This session has the same path and byte hashes but a different verified workspace.
            var otherRoot = Path.Combine(Path.GetTempPath(), "omnicore-undo-foreign-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(otherRoot);
            try
            {
                var otherSession = SessionId.New(); var identity = WorkspaceRootIdentity.Establish(otherRoot);
                var stream = new EventStream(Store, Codecs, otherSession);
                var canonical = ProjectIdentity.CanonicalWorkspacePath(otherRoot);
                stream.Append(new SessionCreated(otherSession, WorkspaceId.Of(canonical).ToString(), otherRoot,
                    ProfileId.New(), DateTimeOffset.UtcNow));
                stream.Append(new WorkspaceRootEstablished(otherSession, canonical, DateTimeOffset.UtcNow, identity));
                var run = TestRun.Open(stream, otherSession);
                var execution = ExecutionId.New();
                using var scope = ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask,
                    run.RootLane, TurnId.New(), call, execution));
                stream.Append(new ToolCallRequested(call, "fixture", "filesystem.write", "{}"));
                stream.Append(new ToolCallPrepared(call, "{}")); stream.Append(new PermissionEvaluated(call, PermissionDecision.Allow, "{}", null));
                stream.Append(new ToolCallAuthorized(call));
                var beforeRef = FilesystemPreimage.Capture(new ReconciliationSpec(FileVersion.VersionToken(before),
                    FileVersion.VersionToken(after), null), Artifacts, before).BeforeStateRef;
                var afterRef = FilesystemPreimage.CaptureAfter(Artifacts, after);
                stream.Append(new ToolCallStarted(call, EffectClass.NonIdempotent,
                    FilesystemReconciliationMetadata.Encode("target.txt", FileVersion.VersionToken(before), FileVersion.VersionToken(after)))
                { TargetRef = "target.txt", BeforeStateRef = beforeRef, Reversibility = Reversibility.Reversible }, DurabilityClass.Barrier);
                stream.Append(new ToolCallSucceeded(call, "{}") { AfterStateRef = afterRef });
                return otherSession;
            }
            finally { Directory.Delete(otherRoot, true); }
        }

        public UndoService Service(PermissionDecision decision) => new(Root, Identity, Store, Codecs, Artifacts,
            ScriptedPermissionPolicy.WithTool("workspace.undo", decision));
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
