namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;
using OmniCore.Client;
using OmniCore.Protocol;

public sealed partial class WorkspaceUndoTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public async System.Threading.Tasks.Task Human_undo_binds_preview_session_and_effect_without_overwriting_foreign_edits(string locale)
    {
        using var fixture = new Fixture();
        var before = System.Text.Encoding.UTF8.GetBytes("before\n");
        var after = System.Text.Encoding.UTF8.GetBytes("after\n");
        var path = Path.Combine(fixture.Root, "target.txt");
        File.WriteAllBytes(path, after);
        var effect = fixture.AddSingleEffect("target.txt", before, after);
        var pointer = Path.Combine(fixture.Root, "lastsession.txt");
        File.WriteAllText(pointer, fixture.Session + "\n" + fixture.Run.RunId);
        using var history = new SqliteWorkspaceMutationHistoryStore(Path.Combine(fixture.Root, "cas"));
        var server = new OmniServer(fixture.Store, fixture.Codecs, new InMemoryAuditSink(), pointer, fixture.Artifacts,
            platformPathsFactory: () => new UndoTestPaths(fixture.Root), mutationHistory: history);
        server.ConfigureWorkspaceRoot(fixture.Root);
        var review = UndoProtocol.Decode(server.Query(UndoProtocol.QueryFor(effect.ToString()), TestContext.Current.CancellationToken)!.Json)!;
        Assert.Null(review.Error);
        Assert.Equal("Ready", Assert.Single(review.Files).State);
        Assert.Equal("after\n", review.Files[0].Diff?.Before);
        Assert.Equal("before\n", review.Files[0].Diff?.After);
        var payload = "{" + JsonObj.Field("cmd", UndoProtocol.Apply) + "," + JsonObj.Field("sessionId", review.SessionId!)
            + "," + JsonObj.Field("effectId", effect.ToString()) + "," + JsonObj.Field("revision", review.Revision)
            + "," + JsonObj.Field("confirmed", "true") + "}";
        Assert.Equal("error", server.Send(WireEnvelope.Command(Ids.NewV7(), payload), TestContext.Current.CancellationToken).Status);
        Assert.Equal(after, File.ReadAllBytes(path));
        File.WriteAllText(path, "foreign edit\n");
        var stale = server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), payload), TestContext.Current.CancellationToken);
        Assert.Equal("undo.stale", stale.Error);
        Assert.Equal("foreign edit\n", File.ReadAllText(path));
        File.WriteAllBytes(path, after);
        var text = new Localization(locale);
        var handler = new ClientActionHandler();
        var refreshed = UndoProtocol.Decode(server.Query(UndoProtocol.QueryFor(effect.ToString()), TestContext.Current.CancellationToken)!.Json)!;
        var result = await handler.ExecuteAsync(new(ClientActionIds.WorkspaceUndo,
                ["apply", effect.ToString(), refreshed.Revision, "--confirm"], "Typed"), new(server, text), TestContext.Current.CancellationToken);
        Assert.Equal(text.Resolve("undo.done"), result.Text);
        Assert.Equal(before, File.ReadAllBytes(path));
        foreach (var key in UndoResources.Keys) Assert.False(text.ResolveDetailed(key).UsedFallback);
    }

    private sealed class UndoTestPaths(string root) : IPlatformPaths
    {
        public string DataDirectory => Path.Combine(root, "data");
        public string UserDatabasePath => Path.Combine(DataDirectory, "user.db");
        public string ConfigDirectory => Path.Combine(root, "config");
        public string WorkspaceDirectory(string workspaceId) => Path.Combine(DataDirectory, "workspaces", workspaceId);
    }

    [Fact]
    public async System.Threading.Tasks.Task Human_recovery_closes_pending_undo_as_Unknown_without_overwriting_conflicts()
    {
        using var fixture = new Fixture();
        var before = System.Text.Encoding.UTF8.GetBytes("before\n");
        var after = System.Text.Encoding.UTF8.GetBytes("after\n");
        var path = Path.Combine(fixture.Root, "target.txt");
        File.WriteAllBytes(path, after);
        var effect = fixture.AddSingleEffect("target.txt", before, after);
        fixture.AppendPendingUndo(effect, before, after);
        File.WriteAllText(path, "foreign edit\n");
        var pointer = Path.Combine(fixture.Root, "lastsession.txt");
        File.WriteAllText(pointer, fixture.Session + "\n" + fixture.Run.RunId);
        using var history = new SqliteWorkspaceMutationHistoryStore(Path.Combine(fixture.Root, "cas"));
        var server = new OmniServer(fixture.Store, fixture.Codecs, new InMemoryAuditSink(), pointer, fixture.Artifacts,
            platformPathsFactory: () => new UndoTestPaths(fixture.Root), mutationHistory: history);
        server.ConfigureWorkspaceRoot(fixture.Root);
        var review = UndoProtocol.Decode(server.Query(UndoProtocol.QueryFor(effect.ToString()), TestContext.Current.CancellationToken)!.Json)!;
        Assert.True(review.RecoveryRequired);
        var result = await new ClientActionHandler().ExecuteAsync(new(ClientActionIds.WorkspaceUndo,
            ["apply", effect.ToString(), review.Revision, "--confirm"], "User"), new(server, Localization.Spanish()), TestContext.Current.CancellationToken);
        Assert.Contains("incierta", result.Text);
        Assert.Equal("foreign edit\n", File.ReadAllText(path));
        var completion = Assert.Single(fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).OfType<UndoCompleted>());
        Assert.Equal(UndoOutcome.Unknown, completion.Outcome);
        var next = UndoProtocol.Decode(server.Query(UndoProtocol.QueryFor(effect.ToString()), TestContext.Current.CancellationToken)!.Json)!;
        Assert.False(next.RecoveryRequired);
    }

    [Fact]
    public void Preview_is_read_only_and_identifies_foreign_edits()
    {
        using var fixture = new Fixture();
        var before = System.Text.Encoding.UTF8.GetBytes("before\n");
        var after = System.Text.Encoding.UTF8.GetBytes("after\n");
        File.WriteAllBytes(Path.Combine(fixture.Root, "target.txt"), after);
        var effect = fixture.AddSingleEffect("target.txt", before, after);
        var service = fixture.Service(PermissionDecision.Allow);
        var count = fixture.Store.ReadFrom(fixture.Session, 1).Count;
        Assert.Equal("Ready", Assert.Single(service.Preview(fixture.Session, effect).Entries).State);
        Assert.Equal(count, fixture.Store.ReadFrom(fixture.Session, 1).Count);
        Assert.Equal(after, File.ReadAllBytes(Path.Combine(fixture.Root, "target.txt")));
        File.WriteAllText(Path.Combine(fixture.Root, "target.txt"), "someone else's edit");
        Assert.Equal("Conflict", Assert.Single(service.Preview(fixture.Session, effect).Entries).State);
        Assert.Equal(count, fixture.Store.ReadFrom(fixture.Session, 1).Count);
    }

    [Theory]
    [InlineData(PermissionDecision.Ask, true)]
    [InlineData(PermissionDecision.Deny, false)]
    public void Explicit_human_confirmation_lifts_Ask_once_but_never_Deny(PermissionDecision decision, bool succeeds)
    {
        using var fixture = new Fixture();
        var before = System.Text.Encoding.UTF8.GetBytes("before\n");
        var after = System.Text.Encoding.UTF8.GetBytes("after\n");
        File.WriteAllBytes(Path.Combine(fixture.Root, "target.txt"), after);
        var effect = fixture.AddSingleEffect("target.txt", before, after);
        var command = CommandId.New();
        var result = fixture.Service(decision).Undo(fixture.Session, effect, TestContext.Current.CancellationToken,
            confirmedByUser: true, commandIdOverride: command);
        Assert.Equal(succeeds ? UndoOutcome.Applied : UndoOutcome.Unsupported, result.Outcome);
        Assert.Equal(succeeds ? before : after, File.ReadAllBytes(Path.Combine(fixture.Root, "target.txt")));
        if (succeeds)
            Assert.Equal(command, Assert.Single(fixture.Store.ReadFrom(fixture.Session, 1).Select(fixture.Codecs.Decode).OfType<UndoStarted>()).CommandId);
    }

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

    [Fact]
    public void Pending_undo_for_another_effect_is_left_unclassified_until_selected()
    {
        using var fixture = new Fixture();
        var before = System.Text.Encoding.UTF8.GetBytes("before\n");
        var after = System.Text.Encoding.UTF8.GetBytes("after\n");
        File.WriteAllBytes(Path.Combine(fixture.Root, "a.txt"), after);
        File.WriteAllBytes(Path.Combine(fixture.Root, "b.txt"), after);
        var requested = fixture.AddSingleEffect("a.txt", before, after);
        var pendingEffect = fixture.AddSingleEffect("b.txt", before, after);
        fixture.AppendPendingUndo(pendingEffect, before, after);
        var service = fixture.Service(PermissionDecision.Allow);

        var mismatched = service.Undo(fixture.Session, requested, CancellationToken.None);

        Assert.Equal(UndoOutcome.Unsupported, mismatched.Outcome);
        Assert.Equal(after, File.ReadAllBytes(Path.Combine(fixture.Root, "a.txt")));
        Assert.Equal(after, File.ReadAllBytes(Path.Combine(fixture.Root, "b.txt")));
        Assert.DoesNotContain(fixture.Store.ReadFrom(fixture.Session, 1), item =>
            fixture.Codecs.Decode(item) is UndoCompleted);

        var selected = service.Undo(fixture.Session, pendingEffect, CancellationToken.None);
        Assert.Equal(UndoOutcome.Unknown, selected.Outcome);
        Assert.Equal(after, File.ReadAllBytes(Path.Combine(fixture.Root, "b.txt")));
        Assert.IsType<UndoCompleted>(fixture.Codecs.Decode(fixture.Store.ReadFrom(fixture.Session, 1).Last()));
    }

    [Fact]
    public void Pending_delete_recovery_preserves_quarantined_bytes_and_only_classifies()
    {
        using var fixture = new Fixture();
        var after = System.Text.Encoding.UTF8.GetBytes("created by OmniCore\n");
        File.WriteAllBytes(Path.Combine(fixture.Root, "created.txt"), after);
        var effect = fixture.AddSingleEffect("created.txt", null, after);
        const string quarantine = ".omni-undo-fixture-0";
        fixture.AppendPendingUndo(effect, null, after, quarantine);
        File.Move(Path.Combine(fixture.Root, "created.txt"), Path.Combine(fixture.Root, quarantine));

        var service = fixture.Service(PermissionDecision.Allow);
        var result = service.Undo(fixture.Session, effect, CancellationToken.None);

        Assert.Equal(UndoOutcome.Applied, result.Outcome);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "created.txt")));
        Assert.Equal(after, File.ReadAllBytes(Path.Combine(fixture.Root, quarantine)));
        Assert.Contains(result.Files, file => file.RelativePath == "created.txt" && file.Outcome == ReconciliationOutcome.Applied);
        Assert.Empty(service.Plan(fixture.Session).Entries);
    }

    [Fact]
    public void Explicit_effect_id_selects_a_reversible_effect_from_an_older_workspace_session()
    {
        var parent = Path.Combine(Path.GetTempPath(), "omnicore-undo-old-session-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "workspace");
        var data = Path.Combine(parent, "data");
        var config = Path.Combine(parent, "config");
        var previousData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var previousConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
        Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
        try
        {
            var paths = OmniHost.CreatePlatformPaths();
            var workspaceData = OmniHost.WorkspaceDataDirectory(paths, root);
            var codecs = EventCodecs.Create();
            var store = new SqliteEventStore(Path.Combine(workspaceData, "journal.db"));
            var artifacts = OmniHost.CreateArtifactStore(workspaceData);
            var canonical = ProjectIdentity.CanonicalWorkspacePath(root);
            var identity = WorkspaceRootIdentity.Establish(root);
            var oldSession = SessionId.New();
            var currentSession = SessionId.New();
            var before = System.Text.Encoding.UTF8.GetBytes("before\n");
            var after = System.Text.Encoding.UTF8.GetBytes("after\n");
            File.WriteAllBytes(Path.Combine(root, "target.txt"), after);
            var effect = ToolCallId.New();

            void AppendSession(SessionId session, bool withEffect)
            {
                var stream = new EventStream(store, codecs, session);
                stream.Append(new SessionCreated(session, WorkspaceId.Of(canonical).ToString(), root,
                    ProfileId.New(), DateTimeOffset.UtcNow));
                stream.Append(new WorkspaceRootEstablished(session, canonical, DateTimeOffset.UtcNow, identity));
                var run = TestRun.Open(stream, session);
                if (!withEffect) return;
                var beforeRef = FilesystemPreimage.Capture(new ReconciliationSpec(FileVersion.VersionToken(before),
                    FileVersion.VersionToken(after), null), artifacts, before).BeforeStateRef;
                var afterRef = FilesystemPreimage.CaptureAfter(artifacts, after);
                var execution = ExecutionId.New();
                using var scope = ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask,
                    run.RootLane, TurnId.New(), effect, execution));
                stream.Append(new ToolCallRequested(effect, "fixture", "filesystem.write", "{}"));
                stream.Append(new ToolCallPrepared(effect, "{}"));
                stream.Append(new PermissionEvaluated(effect, PermissionDecision.Allow, "{}", null));
                stream.Append(new ToolCallAuthorized(effect));
                stream.Append(new ToolCallStarted(effect, EffectClass.NonIdempotent,
                    FilesystemReconciliationMetadata.Encode("target.txt", FileVersion.VersionToken(before),
                        FileVersion.VersionToken(after)))
                { TargetRef = "target.txt", BeforeStateRef = beforeRef, Reversibility = Reversibility.Reversible },
                    DurabilityClass.Barrier);
                stream.Append(new ToolCallSucceeded(effect, "{}") { AfterStateRef = afterRef });
            }

            AppendSession(oldSession, withEffect: true);
            AppendSession(currentSession, withEffect: false);
            store.Close();

            var selected = WorktreeUndoHost.PlanForCli(root, effect.ToString());

            Assert.NotNull(selected);
            Assert.Equal(oldSession.ToString(), selected.SessionId);
            Assert.Contains(selected.Entries, item => item.OriginalEffect == effect.ToString() && item.State == "Ready");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", previousData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", previousConfig);
            try { if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true); } catch { }
        }
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

        public ToolCallId AddSingleEffect(string path, byte[]? before, byte[] after)
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

        public void AppendPendingUndo(ToolCallId effect, byte[]? before, byte[] after, string? quarantinePath = null)
        {
            var source = Store.ReadFrom(Session, 1).Single(item => Codecs.Decode(item) is ToolCallStarted start
                && start.ToolCallId == effect);
            var payload = Assert.IsType<ToolCallStarted>(Codecs.Decode(source));
            var attribution = new UndoAttribution(source.EventId, source.RunId, source.TaskId, source.LaneId,
                source.TurnId, source.ExecutionId, ExecutionId.New());
            var command = CommandId.New();
            using var execution = ExecutionScope.Begin(new ExecutionScopeState(source.RunId, source.TaskId,
                source.LaneId, source.TurnId, effect, attribution.UndoExecutionId));
            using var cause = CausationScope.Begin(new CommandCausation(command));
            _stream.Append(new UndoStarted(command, Guid.NewGuid().ToString("N"), effect, attribution,
                [new UndoFileIntent(payload.TargetRef!, FileVersion.VersionToken(after), before is null
                    ? FilesystemReconciliationMetadata.AbsentPreHash : FileVersion.VersionToken(before), quarantinePath)],
                Identity), DurabilityClass.Barrier);
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
