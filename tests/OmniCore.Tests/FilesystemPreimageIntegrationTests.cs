namespace OmniCore.Tests;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Tools;
using OmniCore.Security;
using OmniCore.Execution;

// The host resolves its artifact directory from the process-wide data directory.
// Keep environment changes and cleanup isolated from other host/GC fixtures.
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class FilesystemPreimageIntegrationTests
{
    [Theory]
    [InlineData("filesystem.write", FileVersion.FileEncoding.Utf8NoBom)]
    [InlineData("filesystem.write", FileVersion.FileEncoding.Utf8Bom)]
    [InlineData("filesystem.write", FileVersion.FileEncoding.Utf16LeBom)]
    [InlineData("filesystem.write", FileVersion.FileEncoding.Utf16BeBom)]
    [InlineData("filesystem.patch", FileVersion.FileEncoding.Utf8NoBom)]
    [InlineData("filesystem.patch", FileVersion.FileEncoding.Utf8Bom)]
    [InlineData("filesystem.patch", FileVersion.FileEncoding.Utf16LeBom)]
    [InlineData("filesystem.patch", FileVersion.FileEncoding.Utf16BeBom)]
    public void Normal_host_captures_preimage_before_mutation(string toolName, FileVersion.FileEncoding encoding)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-preimage-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        var priorData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        SqliteEventStore? store = null;
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", Path.Combine(root, "user"));
            var original = FileVersion.Encode("original α\r\nsecond\n", encoding);
            var file = Path.Combine(workspace, "file.txt");
            File.WriteAllBytes(file, original);
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var database = Path.Combine(root, "journal.db");
            store = new SqliteEventStore(database);
            var artifactDirectory = OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace);
            var artifacts = new FileArtifactStore(artifactDirectory);
            var barrierStore = new PreimageBarrierStore(store, file, original, artifacts, artifactDirectory, database);
            var stream = new EventStream(barrierStore, codecs, session);
            var run = TestRun.Open(stream, session);
            var turn = TurnId.New();
            stream.Append(new TurnStarted(turn, run.RootLane));
            var key = ModelPolicyKey.For("fixture", "fixture");
            var policy = EffectiveModelPolicy.Resolve(key, new StoredModelPolicy(key, 1,
                    ModelPolicyPresets.For(ModelPolicyCategory.FullAgent), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
                new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 8, GuidanceLevel.Full, 3,
                    PlanControl.ModelDriven, 8));
            var boundary = new ModelCapabilityBoundary(policy);
            var tools = OmniHost.CreateActTools().Catalog();
            var executor = OmniHost.CreateActExecutor(tools, workspace, boundary, runId: run.RunId);
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane, turn));
            var read = executor.ExecuteTool(new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.read"),
                "read", "{\"path\":\"file.txt\"}"), false, TestContext.Current.CancellationToken, stream);
            Assert.True(read.Succeeded);
            stream.AppendBatch(read.Events, DurabilityClass.Standard);
            var call = ToolCallId.New();
            var arguments = toolName == "filesystem.write"
                ? JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = "file.txt",
                    ["content"] = "updated α\r\nsecond\n", ["expectedVersion"] = FileVersion.VersionToken(original) })
                : JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = "file.txt",
                    ["oldText"] = "original", ["newText"] = "updated", ["expectedVersion"] = FileVersion.VersionToken(original) });
            var result = executor.ExecuteTool(new ValidatedToolCall(call, new ToolId(toolName), "write", arguments),
                false, TestContext.Current.CancellationToken, stream);
            Assert.True(result.Succeeded, result.Summary);
            stream.AppendBatch(result.Events, DurabilityClass.Standard);
            var envelope = Assert.Single(store.ReadFrom(session, 1), evt => codecs.Decode(evt) is ToolCallStarted s && s.ToolCallId == call);
            var started = Assert.IsType<ToolCallStarted>(codecs.Decode(envelope));
            Assert.Equal(Reversibility.Reversible, started.Reversibility);
            Assert.NotNull(started.BeforeStateRef);
            Assert.Equal("file.txt", started.TargetRef);
            Assert.Equal(started.BeforeStateRef, Assert.Single(envelope.ArtifactRefs));
            Assert.Equal(1, barrierStore.CapturedBarriers);
            Assert.Equal(1, barrierStore.BlockedSweeps);
            Assert.Equal(original, FilesystemPreimage.Read(artifacts, started.BeforeStateRef));
            Assert.Equal(FileVersion.Encode("updated α\r\nsecond\n", encoding), File.ReadAllBytes(file));
            var terminal = Assert.Single(store.ReadFrom(session, 1), evt => codecs.Decode(evt) is ToolCallSucceeded s && s.ToolCallId == call);
            var succeeded = Assert.IsType<ToolCallSucceeded>(codecs.Decode(terminal));
            Assert.NotNull(succeeded.AfterStateRef);
            Assert.Equal(succeeded.AfterStateRef, Assert.Single(terminal.ArtifactRefs));
            var changed = ChangedFilesReader.Read(store, codecs, session, artifacts);
            var changedRow = Assert.Single(changed.Files);
            Assert.Equal("M", changedRow.Status); Assert.Equal("file.txt", changedRow.Path);
            Assert.Equal(1, changedRow.Added); Assert.Equal(1, changedRow.Deleted);
            var diff = ChangedFilesReader.ReadDiff(store, codecs, session, call.ToString(), artifacts);
            Assert.True(diff.Available); Assert.Equal("original α\r\nsecond\n", diff.Before);
            Assert.Equal("updated α\r\nsecond\n", diff.After);
            // A later user edit and unrelated file do not change this immutable attributed effect.
            File.WriteAllText(file, "user edit after OmniCore");
            File.WriteAllText(Path.Combine(workspace, "unrelated.txt"), "not OmniCore");
            Assert.Equal(diff, ChangedFilesReader.ReadDiff(store, codecs, session, call.ToString(), artifacts));
            Assert.Single(ChangedFilesReader.Read(store, codecs, session, artifacts).Files);
            store.Close();
            store = new SqliteEventStore(database);
            var reopened = Assert.Single(store.ReadFrom(session, 1), evt => codecs.Decode(evt) is ToolCallStarted s && s.ToolCallId == call);
            Assert.Equal(envelope.PayloadJson, reopened.PayloadJson);
            Assert.Equal(envelope.ArtifactRefs, reopened.ArtifactRefs);
            Assert.Equal(run.RootTask, reopened.TaskId);
            Assert.Equal(run.RootLane, reopened.LaneId);
            Assert.Equal(turn, reopened.TurnId);
            var reopenedArtifacts = new FileArtifactStore(artifactDirectory);
            Assert.Equal(original, FilesystemPreimage.Read(reopenedArtifacts, started.BeforeStateRef));
            Assert.Equal(diff, ChangedFilesReader.ReadDiff(store, codecs, session, call.ToString(), reopenedArtifacts));
            Assert.False(ChangedFilesReader.ReadDiff(store, codecs, SessionId.New(), call.ToString(), reopenedArtifacts).Available);
            var orphan = reopenedArtifacts.PutText("unreferenced capture fixture", "text/plain", ArtifactKind.Other, Sensitivity.Normal);
            var gc = new ArtifactGc(artifactDirectory).Sweep(database, TimeSpan.Zero, false,
                DateTimeOffset.UtcNow.AddDays(2), TestContext.Current.CancellationToken);
            Assert.Equal(1, gc.Deleted);
            Assert.False(reopenedArtifacts.Verify(orphan.Hash, orphan.Size));
            Assert.Equal(original, FilesystemPreimage.Read(reopenedArtifacts, started.BeforeStateRef));
            Assert.True(reopenedArtifacts.Verify(succeeded.AfterStateRef.Hash, succeeded.AfterStateRef.Size));
        }
        finally
        {
            store?.Close();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=" + Path.Combine(root, "journal.db"));
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", priorData);
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("verify")]
    [InlineData("redacted")]
    [InlineData("hash")]
    public void Capture_failure_never_emits_started_or_executes_write(string failure)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-preimage-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var original = System.Text.Encoding.UTF8.GetBytes("original\n");
            var file = Path.Combine(root, "file.txt");
            File.WriteAllBytes(file, original);
            var artifacts = new FaultyArtifacts(new FileArtifactStore(Path.Combine(root, "cas")), failure);
            var events = new List<DomainEventPayload>();
            var runtime = ToolRuntime.For(new FakeCatalog().Add(new FilesystemWriteTool(new PathBoundaryValidator())),
                OmniCore.Security.ScriptedPermissionPolicy.WithTool("filesystem.write", PermissionDecision.Allow),
                evt => { events.Add(evt); return VoidBox.Instance; });
            var arguments = JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = "file.txt",
                ["content"] = "updated\n", ["expectedVersion"] = FileVersion.VersionToken(original) });
            var result = runtime.Run(new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.write"), "write", arguments),
                new ToolPreparationContext(root, DateTimeOffset.UtcNow),
                new ToolExecutionContext(root, null, artifacts: artifacts), false, TestContext.Current.CancellationToken);
            Assert.False(result.Succeeded);
            Assert.Equal(ToolCallState.Failed, result.FinalState);
            Assert.Equal(original, File.ReadAllBytes(file));
            Assert.DoesNotContain(events, evt => evt is ToolCallStarted or ToolCallSucceeded or ToolCallEffectUnknown);
            Assert.Single(events.OfType<ToolCallAuthorized>());
            var failed = Assert.Single(events.OfType<ToolCallFailed>());
            Assert.Equal(EffectOutcome.None, failed.EffectOutcome);
            Assert.DoesNotContain("unsafe-detail", failed.Cause);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Absence_and_empty_file_are_distinct_faithful_preimages()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-preimage-absence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var artifacts = new FileArtifactStore(root);
            var absent = FilesystemPreimage.Capture(new ReconciliationSpec("absent", "post", null), artifacts, null);
            var empty = FilesystemPreimage.Capture(new ReconciliationSpec(FileVersion.VersionToken([]), "post", null), artifacts, []);
            Assert.Equal(Reversibility.Reversible, absent.Reversibility);
            Assert.Equal(Reversibility.Reversible, empty.Reversibility);
            Assert.NotNull(absent.BeforeStateRef);
            Assert.NotNull(empty.BeforeStateRef);
            Assert.NotEqual(absent.BeforeStateRef.Hash, empty.BeforeStateRef.Hash);
            Assert.Null(FilesystemPreimage.Read(artifacts, absent.BeforeStateRef));
            Assert.Empty(FilesystemPreimage.Read(artifacts, empty.BeforeStateRef)!);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(FileVersion.FileEncoding.Utf8NoBom)]
    [InlineData(FileVersion.FileEncoding.Utf16LeBom)]
    public void Registered_secret_is_rejected_before_CAS_publication(FileVersion.FileEncoding encoding)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-preimage-secret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var secret = "preimage-fixture-" + Guid.NewGuid().ToString("N") + "\"\\\nα";
            Secret.Of(secret);
            var artifacts = new FileArtifactStore(root);
            var bytes = FileVersion.Encode("prefix " + secret + " suffix", encoding);
            var error = Assert.Throws<FilesystemPreimageException>(() => FilesystemPreimage.Capture(
                new ReconciliationSpec(FileVersion.VersionToken(bytes), "post", null), artifacts, bytes));
            Assert.DoesNotContain(secret, error.Message);
            Assert.False(Directory.Exists(Path.Combine(root, "blobs"))); // No encoded secret bypass.
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Creation_records_absence_only_when_no_new_parent_topology_is_needed(bool nested)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-preimage-create-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var artifacts = new FileArtifactStore(Path.Combine(root, "cas"));
            var events = new List<DomainEventPayload>();
            var runtime = ToolRuntime.For(new FakeCatalog().Add(new FilesystemWriteTool(new PathBoundaryValidator())),
                ScriptedPermissionPolicy.WithTool("filesystem.write", PermissionDecision.Allow),
                evt => { events.Add(evt); return VoidBox.Instance; });
            var path = nested ? "new-parent/file.txt" : "file.txt";
            var arguments = JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = path, ["content"] = "created\n" });
            var result = runtime.Run(new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.write"), "create", arguments),
                new ToolPreparationContext(root, DateTimeOffset.UtcNow), new ToolExecutionContext(root, null, artifacts: artifacts),
                false, TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded, result.Summary);
            var started = Assert.Single(events.OfType<ToolCallStarted>());
            Assert.Equal(path, started.TargetRef);
            Assert.Equal(nested ? Reversibility.Unknown : Reversibility.Reversible, started.Reversibility);
            if (nested) Assert.Null(started.BeforeStateRef);
            else
            {
                Assert.NotNull(started.BeforeStateRef);
                Assert.Null(FilesystemPreimage.Read(artifacts, started.BeforeStateRef));
            }
            Assert.Equal("created\n", File.ReadAllText(Path.Combine(root, path)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Concurrent_file_change_after_snapshot_is_not_overwritten()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-preimage-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "file.txt");
            var original = System.Text.Encoding.UTF8.GetBytes("original\n");
            var concurrent = System.Text.Encoding.UTF8.GetBytes("concurrent\n");
            File.WriteAllBytes(file, original);
            var inner = new FileArtifactStore(Path.Combine(root, "cas"));
            var artifacts = new FaultyArtifacts(inner, "none", () => File.WriteAllBytes(file, concurrent));
            var events = new List<DomainEventPayload>();
            var runtime = ToolRuntime.For(new FakeCatalog().Add(new FilesystemWriteTool(new PathBoundaryValidator())),
                ScriptedPermissionPolicy.WithTool("filesystem.write", PermissionDecision.Allow),
                evt => { events.Add(evt); return VoidBox.Instance; });
            var arguments = JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = "file.txt",
                ["content"] = "updated\n", ["expectedVersion"] = FileVersion.VersionToken(original) });
            var result = runtime.Run(new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.write"), "write", arguments),
                new ToolPreparationContext(root, DateTimeOffset.UtcNow), new ToolExecutionContext(root, null, artifacts: artifacts),
                false, TestContext.Current.CancellationToken);
            Assert.False(result.Succeeded);
            Assert.Equal(concurrent, File.ReadAllBytes(file));
            var started = Assert.Single(events.OfType<ToolCallStarted>());
            Assert.NotNull(started.BeforeStateRef);
            Assert.Equal(original, FilesystemPreimage.Read(inner, started.BeforeStateRef));
            var failed = Assert.Single(events.OfType<ToolCallFailed>());
            Assert.Equal(ToolErrorCode.StaleWrite, failed.ErrorCode);
            Assert.Equal(EffectOutcome.None, failed.EffectOutcome);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FaultyArtifacts(IArtifactStore inner, string failure, Action? afterPut = null) : IArtifactStore, IArtifactPublicationLease
    {
        public IDisposable AcquirePublicationLease(CancellationToken token) => ((IArtifactPublicationLease)inner).AcquirePublicationLease(token);
        public ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity)
        {
            if (failure == "throw") throw new IOException("unsafe-detail");
            var reference = inner.PutText(content, mediaType, kind, sensitivity);
            afterPut?.Invoke();
            return failure switch
            {
                "redacted" => reference with { Redacted = true },
                "hash" => reference with { Hash = ContentHash.Sha256(new string('a', 64)) },
                _ => reference,
            };
        }
        public string? GetText(ContentHash hash) => inner.GetText(hash);
        public bool Verify(ContentHash hash, long expectedSize) => failure != "verify" && inner.Verify(hash, expectedSize);
    }

    private sealed class PreimageBarrierStore(IEventStore inner, string file, byte[] original, IArtifactStore artifacts,
        string artifactDirectory, string journal) : IEventStore
    {
        public int CapturedBarriers { get; private set; }
        public int BlockedSweeps { get; private set; }
        private void Check(DomainEvent evt, DurabilityClass durability)
        {
            if (EventCodecs.Create().Decode(evt) is not ToolCallStarted { EffectClass: not EffectClass.None } started) return;
            Assert.Equal(DurabilityClass.Barrier, durability);
            Assert.Equal(original, File.ReadAllBytes(file));
            Assert.NotNull(started.BeforeStateRef);
            Assert.Equal(original, FilesystemPreimage.Read(artifacts, started.BeforeStateRef));
            // Attempt a real zero-grace sweep on another thread BEFORE the referencing append.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            using var entered = new ManualResetEventSlim();
            var sweep = System.Threading.Tasks.Task.Run(() =>
            {
                entered.Set();
                return new ArtifactGc(artifactDirectory).Sweep(journal, TimeSpan.Zero, false,
                    DateTimeOffset.UtcNow.AddDays(2), timeout.Token);
            }, timeout.Token);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            timeout.CancelAfter(TimeSpan.FromMilliseconds(100));
            Assert.ThrowsAny<OperationCanceledException>(() => sweep.GetAwaiter().GetResult());
            BlockedSweeps++;
            CapturedBarriers++;
        }
        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability, CancellationToken cancellationToken)
        { Check(evt, durability); inner.Append(sessionId, evt, durability, cancellationToken); }
        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> evts, DurabilityClass durability, CancellationToken cancellationToken)
        { foreach (var evt in evts) Check(evt, durability); inner.AppendBatch(sessionId, evts, durability, cancellationToken); }
        public long CurrentSequence(SessionId sessionId) => inner.CurrentSequence(sessionId);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) => inner.ReadFrom(sessionId, fromSequenceInclusive);
    }
}
