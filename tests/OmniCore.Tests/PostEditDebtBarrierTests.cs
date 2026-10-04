using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class PostEditDebtBarrierTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Injected_failure_around_atomic_debt_and_started_commit_never_leaves_orphan_debt(bool afterCommit)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-debt-barrier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        const string original = "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\n";
        var path = Path.Combine(root, "file.cs");
        File.WriteAllText(path, original);
        var store = new SqliteEventStore(journal);
        try
        {
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var key = ModelPolicyKey.For("scripted", "coder");
            var policy = EffectiveModelPolicy.Resolve(key,
                new StoredModelPolicy(key, 1, ModelPolicyPresets.For(ModelPolicyCategory.FullAgent),
                    DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
                new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 8, GuidanceLevel.Full, 3,
                    PlanControl.ModelDriven, 8));
            var boundary = OmniCliRuntime.CreateBoundary(policy, root);
            var executor = ScriptedToolExecutor.WithWorkspace(OmniHost.CreateActTools().Catalog(),
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision> {
                    ["filesystem.read"] = PermissionDecision.Allow, ["filesystem.patch"] = PermissionDecision.Allow
                }), root, boundary);
            var faultStore = new FaultStore(store, path, original, afterCommit);
            var stream = new EventStream(faultStore, codecs, session);
            var read = executor.ExecuteTool(new ValidatedToolCall(ToolCallId.New(), new ToolId("filesystem.read"),
                "read", "{\"path\":\"file.cs\"}"), false, CancellationToken.None, stream);
            Assert.True(read.Succeeded);
            stream.AppendBatch(read.Events, DurabilityClass.Standard);
            var edit = ToolCallId.New();
            var patch = new ValidatedToolCall(edit, new ToolId("filesystem.patch"), "patch",
                "{\"path\":\"file.cs\",\"expectedVersion\":\""
                + FilesystemPatchTool.VersionToken(System.Text.Encoding.UTF8.GetBytes(original))
                + "\",\"oldText\":\"two\",\"newText\":\"fixed\"}");
            Assert.Throws<IOException>(() => executor.ExecuteTool(patch, false, CancellationToken.None, stream));
            Assert.True(faultStore.Injected);
            Assert.Equal(original, File.ReadAllText(path));
            store.Close();
            store = new SqliteEventStore(journal);
            var events = store.ReadFrom(session, 1);
            var pending = PostEditValidationProjection.Pending(run.RunId, codecs, events);
            if (afterCommit)
            {
                Assert.Equal(edit, Assert.Single(pending));
                Assert.Contains(events, evt => codecs.Decode(evt) is ToolCallStarted started && started.ToolCallId == edit);
            }
            else
            {
                Assert.Empty(pending);
                Assert.DoesNotContain(events, evt => codecs.Decode(evt) is ToolCallStarted started && started.ToolCallId == edit);
            }
        }
        finally
        {
            store.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FaultStore(IEventStore inner, string path, string original, bool afterCommit) : IEventStore
    {
        public bool Injected { get; private set; }
        public void Append(SessionId session, DomainEvent evt, DurabilityClass durability, CancellationToken token)
        {
            Assert.NotEqual("post_edit_validation.pending", evt.Type.ToString());
            inner.Append(session, evt, durability, token);
        }
        public void AppendBatch(SessionId session, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken token)
        {
            if (events.Any(evt => evt.Type.ToString() == "post_edit_validation.pending"))
            {
                Assert.Equal(new[] { "post_edit_validation.pending", "toolcall.started" },
                    events.Select(evt => evt.Type.ToString()));
                Assert.Equal(DurabilityClass.Barrier, durability);
                Assert.Equal(original, File.ReadAllText(path));
                Injected = true;
                if (afterCommit) inner.AppendBatch(session, events, durability, token);
                throw new IOException("injected boundary failure");
            }
            inner.AppendBatch(session, events, durability, token);
        }
        public long CurrentSequence(SessionId session) => inner.CurrentSequence(session);
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId session, long seq) => inner.ReadFrom(session, seq);
    }
}
