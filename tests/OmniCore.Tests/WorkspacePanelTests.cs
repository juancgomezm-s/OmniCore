using System.Diagnostics;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class WorkspacePanelTests
{
    private sealed class Paths(string root) : IPlatformPaths
    {
        public string DataDirectory => Path.Combine(root, "data");
        public string ConfigDirectory => Path.Combine(root, "config");
        public string UserDatabasePath => Path.Combine(DataDirectory, "user.db");
        public string WorkspaceDirectory(string id)
        {
            var directory = Path.Combine(DataDirectory, "workspaces", id);
            Directory.CreateDirectory(directory); // same eager behavior as DefaultPlatformPaths.
            return directory;
        }
    }
    private sealed class Repo : IDisposable
    {
        public string Temp { get; } = Path.Combine(Path.GetTempPath(), "omni-panel-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Temp, "repo");
        public Repo()
        {
            Directory.CreateDirectory(Root); Git("init"); Git("config", "user.email", "test@localhost"); Git("config", "user.name", "Test");
            File.WriteAllText(Path.Combine(Root, "a.txt"), "base\n"); File.WriteAllText(Path.Combine(Root, ".gitignore"), "ignored/\n");
            Git("add", "."); Git("commit", "-m", "base");
        }
        public string Git(params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10000), "git timeout"); Assert.True(process.ExitCode == 0, error); return output;
        }
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in Directory.EnumerateFiles(Temp, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Temp, true);
        }
    }
    private static WorkspacePanelResult Query(OmniServer server, WorkspacePanelRequest request) =>
        JsonSerializer.Deserialize(server.Query("workspace.panel:" + Uri.EscapeDataString(JsonSerializer.Serialize(request,
            WorkspacePanelJson.Default.WorkspacePanelRequest)), CancellationToken.None)!.Json, WorkspacePanelJson.Default.WorkspacePanelResult)!;

    [Fact]
    public async Task Lazy_files_search_status_diff_and_external_changes_use_real_git()
    {
        using var repo = new Repo(); var git = new GitWorktreeStore(new SystemProcessRuntime());
        Directory.CreateDirectory(Path.Combine(repo.Root, "folder")); File.WriteAllText(Path.Combine(repo.Root, "folder", "español.txt"), "hola\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, "ignored")); File.WriteAllText(Path.Combine(repo.Root, "ignored", "secret.txt"), "ignored");
        File.WriteAllText(Path.Combine(repo.Root, "a.txt"), "changed\nsecond\n");
        var files = await git.ReadDirectoryAsync(repo.Root, "", CancellationToken.None);
        Assert.Contains(files, file => file.Path == "folder" && file.Directory); Assert.DoesNotContain(files, file => file.Path is ".git" or "ignored");
        Assert.Equal(["folder/español.txt"], await git.SearchWorkspaceAsync(repo.Root, "español", CancellationToken.None));
        var status = await git.ReadWorkspaceAsync(repo.Root, CancellationToken.None);
        var changed = Assert.Single(status.Files, file => file.Path == "a.txt"); Assert.Equal(2, changed.Added); Assert.Equal(1, changed.Deleted);
        var diff = await git.ReadFileAsync(repo.Root, "a.txt", true, CancellationToken.None); Assert.Contains("+changed", diff.Text);
        File.WriteAllText(Path.Combine(repo.Root, "external.txt"), "external");
        Assert.Contains((await git.ReadDirectoryAsync(repo.Root, "", CancellationToken.None)), file => file.Path == "external.txt");
        Assert.Contains((await git.ReadWorkspaceAsync(repo.Root, CancellationToken.None)).Files, file => file.Path == "external.txt");
        await Assert.ThrowsAsync<IOException>(() => git.ReadFileAsync(repo.Root, "../outside", false, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => git.ReadFileAsync(repo.Root, "ignored/secret.txt", false, CancellationToken.None));
    }

    [Fact]
    public async Task Checkpoint_and_worktree_preserve_original_index_branch_stash_and_dirty_files()
    {
        using var repo = new Repo(); var git = new GitWorktreeStore(new SystemProcessRuntime());
        File.WriteAllText(Path.Combine(repo.Root, "a.txt"), "staged\n"); repo.Git("add", "a.txt");
        File.WriteAllText(Path.Combine(repo.Root, "a.txt"), "captured\n"); File.WriteAllText(Path.Combine(repo.Root, "new.txt"), "new\n");
        var index = repo.Git("ls-files", "--stage"); var head = repo.Git("rev-parse", "HEAD"); var branch = repo.Git("branch", "--show-current"); var stash = repo.Git("stash", "list");
        var snapshot = await git.CaptureWorkspaceAsync(repo.Root, Path.Combine(repo.Temp, "snapshots"), CancellationToken.None);
        File.WriteAllText(Path.Combine(repo.Root, "a.txt"), "original after checkpoint\n");
        var destination = Path.Combine(repo.Temp, "alternative"); var newBranch = "omnicore/alternative-" + Guid.NewGuid().ToString("N");
        await git.CreateCheckpointWorktreeAsync(repo.Root, destination, newBranch, snapshot, CancellationToken.None);
        Assert.Equal("captured\n", File.ReadAllText(Path.Combine(destination, "a.txt")));
        Assert.Equal("new\n", File.ReadAllText(Path.Combine(destination, "new.txt")));
        Assert.Equal("original after checkpoint\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
        Assert.Equal(index, repo.Git("ls-files", "--stage")); Assert.Equal(head, repo.Git("rev-parse", "HEAD"));
        Assert.Equal(branch, repo.Git("branch", "--show-current")); Assert.Equal(stash, repo.Git("stash", "list"));
        await git.RemoveCheckpointWorktreeAsync(repo.Root, destination, newBranch);
        Assert.DoesNotContain(destination, repo.Git("worktree", "list", "--porcelain"));
    }

    [Fact]
    public void Alternative_cuts_history_restores_files_and_failure_leaves_no_registration()
    {
        using var repo = new Repo(); var paths = new Paths(repo.Temp); Directory.CreateDirectory(paths.DataDirectory);
        var data = OmniHost.WorkspaceDataDirectory(paths, repo.Root); Directory.CreateDirectory(data);
        using var store = new SqliteEventStore(Path.Combine(data, "journal.db")); var codecs = EventCodecs.Create();
        var server = new OmniServer(store, codecs, new InMemoryAuditSink(), Path.Combine(data, "lastsession.txt"), new FileArtifactStore(data), platformPathsFactory: () => paths);
        server.ConfigureWorkspaceRoot(repo.Root);
        Assert.Equal("ok", server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.new\"}"), CancellationToken.None).Status);
        var session = server.LastSessionId()!;
        var opened = TestRun.Open(store, session);
        using (ExecutionScope.Begin(new(opened.RunId, opened.RootTask, opened.RootLane)))
        {
            var stream = new EventStream(store, codecs, session);
            stream.Append(new UserInputReceived(opened.RunId, "\"keep this request\"", null));
            var call = ToolCallId.New(); stream.Append(new ToolCallRequested(call, "provider-call", "filesystem.read", "{\"path\":\"a.txt\"}"));
            stream.Append(new ToolCallPrepared(call, "{}")); stream.Append(new ToolCallAuthorized(call)); stream.Append(new ToolCallStarted(call, EffectClass.None, null));
            stream.Append(new ToolCallSucceeded(call, "{\"text\":\"base\"}"));
            File.WriteAllText(Path.Combine(repo.Root, "a.txt"), "checkpoint content\n");
            server.RecordWorkspaceCheckpoint(session, store.CurrentSequence(session), "after-tool");
            var point = Assert.Single(Query(server, new("timeline")).Timeline!, row => row.Kind == "tool");
            Assert.Equal("read", point.Activity); Assert.Equal(["a.txt"], point.Files); Assert.NotNull(point.Checkpoint!.Snapshot);
            stream.Append(new UserInputReceived(opened.RunId, "\"must not leak after cut\"", null));
            var orphan = ToolCallId.New(); stream.Append(new ToolCallRequested(orphan, "orphan", "filesystem.read", "{\"path\":\"later.txt\"}"));
            stream.Append(new ToolCallPrepared(orphan, "{}")); stream.Append(new ToolCallAuthorized(orphan)); stream.Append(new ToolCallStarted(orphan, EffectClass.None, null));
            stream.Append(new ToolCallSucceeded(orphan, "later")); // run is quiescent for fork admission.
            File.WriteAllText(Path.Combine(repo.Root, "a.txt"), "original stays dirty\n");
            var index = repo.Git("ls-files", "--stage"); var beforeTrees = repo.Git("worktree", "list", "--porcelain");
            server.BeforeAlternativeSessionCommit = () => throw new IOException("injected after worktree");
            var failedId = Ids.NewV7(); var payload = "{\"cmd\":\"conversation.alternative\",\"checkpointId\":\"" + point.Checkpoint.Id + "\",\"instruction\":\"try another approach\"}";
            Assert.Equal("error", server.SendUserAction(WireEnvelope.Command(failedId, payload), CancellationToken.None).Status);
            Assert.Equal(beforeTrees, repo.Git("worktree", "list", "--porcelain"));
            Assert.Null(Query(server, new("alternative", CommandId: failedId)).Alternative);
            server.BeforeAlternativeSessionCommit = null;
            var workspaceDataDirectories = Directory.GetDirectories(Path.Combine(paths.DataDirectory, "workspaces")).Length;
            server.BeforeAlternativeManifestPublication = () => throw new IOException("injected after durable Session");
            var failedAfterSession = Ids.NewV7();
            Assert.Equal("error", server.SendUserAction(WireEnvelope.Command(failedAfterSession, payload), CancellationToken.None).Status);
            Assert.Equal(beforeTrees, repo.Git("worktree", "list", "--porcelain"));
            Assert.Null(Query(server, new("alternative", CommandId: failedAfterSession)).Alternative);
            Assert.Equal(workspaceDataDirectories, Directory.GetDirectories(Path.Combine(paths.DataDirectory, "workspaces")).Length);
            server.BeforeAlternativeManifestPublication = null;
            var id = Ids.NewV7(); Assert.Equal("ok", server.SendUserAction(WireEnvelope.Command(id, payload), CancellationToken.None).Status);
            var alternative = Query(server, new("alternative", CommandId: id)).Alternative!; Assert.NotNull(alternative);
            Assert.Equal("checkpoint content\n", File.ReadAllText(Path.Combine(alternative.Workspace, "a.txt")));
            var targetData = OmniHost.WorkspaceDataDirectory(paths, alternative.Workspace);
            using var targetStore = new SqliteEventStore(Path.Combine(targetData, "journal.db"));
            var imported = Assert.Single(targetStore.ReadFrom(SessionId.Parse(alternative.SessionId), 1).Select(codecs.Decode).OfType<ConversationHistoryImported>());
            Assert.Contains(imported.Messages, message => message.Role == "user" && message.Text == "keep this request");
            Assert.DoesNotContain(imported.Messages, message => message.Text.Contains("must not leak", StringComparison.Ordinal));
            Assert.Single(imported.Messages, message => message.Role == "tool");
            Assert.Equal("original stays dirty\n", File.ReadAllText(Path.Combine(repo.Root, "a.txt")));
            Assert.Equal(index, repo.Git("ls-files", "--stage")); Assert.Equal(session, server.LastSessionId());
            var historical = Query(server, new("checkpoint-diff", Path: "a.txt", CheckpointId: point.Checkpoint.Id)).File!;
            Assert.Contains("+checkpoint content", historical.Text); Assert.DoesNotContain("original stays dirty", historical.Text);
            var manifests = Path.Combine(data, "workspace-panel", "alternatives");
            Assert.Empty(Directory.EnumerateFiles(manifests, "*.pending.json"));
            // A crash after publication but before deleting the intent must retain the published Session/worktree.
            var staleIntent = Path.Combine(manifests, id + ".pending.json");
            File.WriteAllText(staleIntent, JsonSerializer.Serialize(alternative with { State = "preparing" }, WorkspacePanelJson.Default.WorkspaceAlternative));
            server.RecoverAlternativePreparations(); Assert.False(File.Exists(staleIntent)); Assert.True(Directory.Exists(alternative.Workspace));
        }
    }

    [Fact]
    public void Conversation_cut_drops_incomplete_calls_and_future_results()
    {
        var store = new InMemoryEventStore(); var codecs = EventCodecs.Create(); var session = SessionId.New();
        var opened = TestRun.Open(store, session);
        var server = new OmniServer(store, codecs, new InMemoryAuditSink());
        using var scope = ExecutionScope.Begin(new(opened.RunId, opened.RootTask, opened.RootLane));
        var stream = new EventStream(store, codecs, session);
        stream.Append(new UserInputReceived(opened.RunId, "\"previous instruction\"", null));
        void Start(ToolCallId id)
        {
            stream.Append(new ToolCallRequested(id, id.ToString(), "filesystem.read", "{\"path\":\"a.txt\"}"));
            stream.Append(new ToolCallPrepared(id, "{}")); stream.Append(new ToolCallAuthorized(id)); stream.Append(new ToolCallStarted(id, EffectClass.None, null));
        }
        var completed = ToolCallId.New(); Start(completed); stream.Append(new ToolCallSucceeded(completed, "kept result"));
        var pending = ToolCallId.New(); Start(pending); var cut = store.CurrentSequence(session);
        stream.Append(new ToolCallSucceeded(pending, "future result"));
        var exported = server.ExportCheckpointConversation(store.ReadFrom(session, 1), cut);
        Assert.Equal(2, exported.Count); Assert.Single(exported, message => message.Role == "tool");
        Assert.Contains(exported, message => message.CallId == completed.ToString() && message.Text == "kept result");
        Assert.DoesNotContain(exported, message => message.CallId == pending.ToString() || message.Text.Contains("future", StringComparison.Ordinal));
    }

    [Fact]
    public void Imported_private_history_is_lossless_and_rejects_malformed_tool_envelopes()
    {
        _ = OmniCore.Security.SecretRedactor.Shared;
        var privateText = "synthetic-panel-private-" + Guid.NewGuid().ToString("N");
        SecretRedactorRegistry.Register(privateText);
        Assert.DoesNotContain(privateText, OmniCore.Security.SecretRedactor.Shared.Redact(privateText));
        var session = SessionId.New(); var source = SessionId.New(); var codecs = EventCodecs.Create(); var store = new InMemoryEventStore();
        var imported = new ConversationHistoryImported("workspace", source.ToString(), 5, source + "-5-after-turn", [new("user", privateText + " · español ñ á é í ó ú")], null);
        new EventStream(store, codecs, session).Append(imported);
        Assert.Equal(imported.Messages[0].Text, Assert.IsType<ConversationHistoryImported>(codecs.Decode(Assert.Single(store.ReadFrom(session, 1)))).Messages[0].Text);
        Assert.Throws<InvalidDataException>(() => (imported with { Messages = [new("system", "not allowed")] }).Validate());
        Assert.Throws<InvalidDataException>(() => (imported with { Messages = [new("tool", "orphan result")] }).Validate());
    }
}
