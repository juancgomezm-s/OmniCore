using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;

namespace OmniCore.Tests;

public sealed class ConversationLifecycleTests
{
    private sealed class Paths(string root) : IPlatformPaths
    {
        public string DataDirectory => Path.Combine(root, "data");
        public string UserDatabasePath => Path.Combine(DataDirectory, "user.db");
        public string ConfigDirectory => Path.Combine(root, "config");
        public string WorkspaceDirectory(string workspaceId) => Path.Combine(DataDirectory, "workspaces", workspaceId);
    }
    private static CommandAck Command(OmniServer server, string command, string? session = null) => server.SendUserAction(
        WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", command)
            + (session is null ? "" : "," + JsonObj.Field("sessionId", session)) + "}"), CancellationToken.None);

    [Fact]
    public void New_and_resume_keep_distinct_durable_sessions_without_inventing_user_input()
    {
        var store = new InMemoryEventStore(); var codecs = EventCodecs.Create();
        var server = new OmniServer(store, codecs, new InMemoryAuditSink(), platformPathsFactory:
            () => new Paths(Path.Combine(Path.GetTempPath(), "omni-session-config-" + Guid.NewGuid().ToString("N"))));
        Assert.Equal("error", server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.new\"}"), CancellationToken.None).Status);
        Assert.Null(server.LastSessionId());
        Assert.Equal("ok", Command(server, "conversation.new").Status);
        var first = server.LastSessionId()!;
        Assert.Null(server.LastRunId());
        Assert.DoesNotContain(store.ReadFrom(first, 1), evt => codecs.Decode(evt) is UserInputReceived);
        Assert.Equal("ok", Command(server, "conversation.new").Status);
        var second = server.LastSessionId()!; Assert.NotEqual(first, second);
        Assert.Equal(2, ConversationJson.Decode(server.Query("conversations", CancellationToken.None)!.Json)!.Conversations.Count);
        Assert.Equal("ok", Command(server, "conversation.resume", first.ToString()).Status);
        Assert.Equal(first, server.LastSessionId());
        Assert.Equal("error", Command(server, "conversation.resume", SessionId.New().ToString()).Status);
        Assert.Equal(first, server.LastSessionId());
    }

    [Fact]
    public void Empty_session_and_selection_survive_SQLite_close_and_reopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-session-sqlite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var journal = Path.Combine(root, "journal.db"); var pointer = Path.Combine(root, "last-run");
        var codecs = EventCodecs.Create(); SessionId first; SessionId second;
        OmniServer Open(SqliteEventStore store) => new(store, codecs, new InMemoryAuditSink(), pointer,
            artifacts: new FileArtifactStore(root), platformPathsFactory: () => new Paths(root));
        try
        {
            using (var store = new SqliteEventStore(journal))
            {
                var server = Open(store);
                Assert.Equal("ok", Command(server, "conversation.new").Status); first = server.LastSessionId()!;
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool((Microsoft.Data.Sqlite.SqliteConnection)store.Connection);
            }
            using (var store = new SqliteEventStore(journal))
            {
                var server = Open(store); Assert.Equal(first, server.LastSessionId()); Assert.Null(server.LastRunId());
                Assert.Equal("ok", Command(server, "conversation.new").Status); second = server.LastSessionId()!;
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool((Microsoft.Data.Sqlite.SqliteConnection)store.Connection);
            }
            using (var store = new SqliteEventStore(journal))
            {
                var server = Open(store); Assert.Equal(second, server.LastSessionId());
                Assert.Equal(2, ConversationJson.Decode(server.Query("conversations", CancellationToken.None)!.Json)!.Conversations.Count);
                Assert.Equal("ok", Command(server, "conversation.resume", first.ToString()).Status);
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool((Microsoft.Data.Sqlite.SqliteConnection)store.Connection);
            }
            using (var store = new SqliteEventStore(journal))
            {
                Assert.Equal(first, Open(store).LastSessionId());
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool((Microsoft.Data.Sqlite.SqliteConnection)store.Connection);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Switching_or_compacting_cannot_interrupt_an_open_model_turn()
    {
        WithHistory((server, store, codecs, artifacts, opened, turn, requests) =>
        {
            Assert.Equal("ok", Command(server, "conversation.resume", opened.SessionId.ToString()).Status);
            using var execution = ExecutionScope.Begin(new(opened.RunId, opened.RootTask, opened.RootLane));
            new EventStream(store, codecs, opened.SessionId).Append(new TurnStarted(TurnId.New(), opened.RootLane));
            var sequence = store.CurrentSequence(opened.SessionId);
            Assert.Equal("busy", Command(server, "conversation.new").Status);
            Assert.Equal("busy", Command(server, "conversation.compact").Status);
            Assert.Equal(opened.SessionId, server.LastSessionId()); Assert.Equal(sequence, store.CurrentSequence(opened.SessionId));
        });
    }

    [Theory]
    [InlineData(949, 1000, false)]
    [InlineData(950, 1000, true)]
    [InlineData(951, 1000, true)]
    [InlineData(95, 101, false)]
    [InlineData(96, 101, true)]
    [InlineData(1, 0, false)]
    [InlineData(long.MaxValue, long.MaxValue, true)]
    public void Auto_compaction_threshold_is_95_percent_with_safe_integer_rounding(long tokens, long budget, bool expected)
        => Assert.Equal(expected, ContextCompaction.ShouldCompactAtCapacity(tokens, budget));

    [Theory]
    [InlineData(8192, 8192, 2048, 6144)]
    [InlineData(4096, 8192, 2048, 4096)]
    [InlineData(8192, 8192, 8192, 0)]
    public void Input_budget_reserves_the_declared_output_window(long configured, long capacity, long output, long expected)
        => Assert.Equal(expected, ContextCompaction.EffectiveInputBudget(configured, capacity, output));

    [Fact]
    public void Token_pressure_compacts_before_dispatch_even_below_the_message_count_cadence()
    {
        WithHistory((server, store, codecs, artifacts, opened, turn, requests) =>
        {
            for (var index = 0; index < 10; index++)
                Assert.Equal(StopReason.EndTurn, turn.Ask("question-" + index + " " + string.Join(' ', Enumerable.Repeat("word", 65)),
                    "Follow the task.", opened.SessionId, opened.RunId, opened.RootLane, "Current working state", CancellationToken.None).StopReason);
            Assert.Contains(store.ReadFrom(opened.SessionId, 1), evt => codecs.Decode(evt) is ContextCheckpointRecorded);
            Assert.Equal(10, requests.Count);
            Assert.Contains("Current working state", requests[^1].Instructions);
        });
    }

    [Fact]
    public void Manual_compaction_preserves_journal_and_is_consumed_on_next_turn_and_next_run()
    {
        WithHistory((server, store, codecs, artifacts, opened, turn, requests) =>
        {
            for (var index = 0; index < 8; index++)
                turn.Ask("question-" + index, "Follow the task.", opened.SessionId, opened.RunId, opened.RootLane, "state", CancellationToken.None);
            Assert.Equal("ok", Command(server, "conversation.resume", opened.SessionId.ToString()).Status);
            var before = store.ReadFrom(opened.SessionId, 1).ToArray();
            var count = requests.Count;
            var ack = Command(server, "conversation.compact");
            Assert.Equal("ok", ack.Status); Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
            Assert.Equal(count, requests.Count); // manual extraction never dispatches an unrequested model call
            var after = store.ReadFrom(opened.SessionId, 1);
            Assert.Equal(before.Select(evt => evt.PayloadJson), after.Take(before.Length).Select(evt => evt.PayloadJson));
            var checkpoint = after.Select(codecs.Decode).OfType<ContextCheckpointRecorded>().Last();
            Assert.Equal("manual-session-v1", checkpoint.MetaModelFingerprint);
            Assert.Contains("question-1", artifacts.GetText(checkpoint.CheckpointArtifact.Hash));
            Assert.Equal(StopReason.EndTurn, turn.Ask("after-compaction", "Follow the task.", opened.SessionId, opened.RunId, opened.RootLane, "state", CancellationToken.None).StopReason);
            Assert.Contains("question-1", requests[^1].Instructions);
            Assert.DoesNotContain(requests[^1].Messages, message => message.Content.OfType<TextBlock>().Any(text => text.Text == "question-1"));
            var next = TestRun.Open(store, opened.SessionId);
            Assert.Equal(StopReason.EndTurn, turn.Ask("another-run", "Follow the task.", opened.SessionId, next.RunId, next.RootLane, "new state", CancellationToken.None).StopReason);
            Assert.Contains("question-1", requests[^1].Instructions);
            Assert.Contains(requests[^1].Messages, message => message.Content.OfType<TextBlock>().Any(text => text.Text == "question-0"));
        });
    }

    [Fact]
    public void Manual_checkpoint_keeps_old_tool_failures_and_applied_user_corrections()
    {
        WithHistory((server, store, codecs, artifacts, opened, turn, requests) =>
        {
            for (var index = 0; index < 8; index++)
            {
                turn.Ask("question-" + index, "Follow the task.", opened.SessionId, opened.RunId, opened.RootLane, "state", CancellationToken.None);
                if (index != 1) continue;
                using var execution = ExecutionScope.Begin(new(opened.RunId, opened.RootTask, opened.RootLane));
                var stream = new EventStream(store, codecs, opened.SessionId);
                var call = ToolCallId.New();
                stream.Append(new ToolCallRequested(call, "fixture-call", "filesystem.read", "{\"path\":\"src/Worker.cs\"}"));
                stream.Append(new ToolCallPrepared(call, "{}"));
                stream.Append(new ToolCallAuthorized(call));
                stream.Append(new ToolCallStarted(call, EffectClass.None, null));
                stream.Append(new ToolCallFailed(call, "E17: missing file", EffectOutcome.None));
                var steering = SteeringId.New(); var turnId = TurnId.New();
                stream.Append(new TurnStarted(turnId, opened.RootLane));
                stream.Append(new TurnSteeringReceived(steering, opened.RunId, opened.RootLane, turnId, "[\"keep Spanish\"]"));
                stream.Append(new TurnSteeringApplied(steering, opened.RunId, opened.RootLane, turnId, 0));
                stream.Append(new TurnCompleted(turnId));
            }
            Assert.Equal("ok", Command(server, "conversation.resume", opened.SessionId.ToString()).Status);
            Assert.Equal("ok", Command(server, "conversation.compact").Status);
            var checkpoint = store.ReadFrom(opened.SessionId, 1).Select(codecs.Decode).OfType<ContextCheckpointRecorded>().Last();
            var text = artifacts.GetText(checkpoint.CheckpointArtifact.Hash)!;
            Assert.Contains("E17: missing file", text); Assert.Contains("keep Spanish", text);
            turn.Ask("continue", "Follow the task.", opened.SessionId, opened.RunId, opened.RootLane, "state", CancellationToken.None);
            Assert.Contains("E17: missing file", requests[^1].Instructions);
            Assert.Contains("keep Spanish", requests[^1].Instructions);
        });
    }

    [Fact]
    public void Visible_reasoning_is_not_rendered_into_context_or_compaction_prose()
    {
        var message = new ModelMessage(MessageRole.Assistant,
            [new ReasoningBlock("private-display-trace", ReasoningVisibility.Full, null), new TextBlock("visible-answer")]);
        var text = ExplorerTurn.RenderMessage(message);
        Assert.Contains("visible-answer", text); Assert.DoesNotContain("private-display-trace", text);
        var failure = new ModelMessage(MessageRole.Tool,
            [new ToolResultBlock(ToolCallId.New(), [new TextBlock("missing file")], true)]);
        Assert.Contains("[ERROR] missing file", ExplorerTurn.RenderMessage(failure));
    }

    private static void WithHistory(Action<OmniServer, InMemoryEventStore, EventCodecs, FileArtifactStore,
        TestRun.Opened, ExplorerTurn, List<ModelRequest>> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-conversations-" + Guid.NewGuid().ToString("N"));
        var store = new InMemoryEventStore(); var codecs = EventCodecs.Create(); var artifacts = new FileArtifactStore(root);
        var server = new OmniServer(store, codecs, new InMemoryAuditSink(), artifacts: artifacts, platformPathsFactory: () => new Paths(root));
        Command(server, "conversation.new"); var opened = TestRun.Open(store, server.LastSessionId()!);
        var tools = OmniHost.CreateExplorerTools(); var executor = OmniHost.CreateExplorerExecutor(tools.Catalog(), Path.GetTempPath());
        var requests = new List<ModelRequest>();
        var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 4, GuidanceLevel.Off, 1,
            PlanControl.RuntimeDriven, 4, new ContextManagementPolicy(100_000, 10_000, 12, 10_000, 160));
        var turn = new ExplorerTurn((request, _) =>
        {
            requests.Add(request);
            return new ModelResponse([new TextBlock(string.Join(' ', Enumerable.Repeat("answer", 60)))], StopReason.EndTurn,
                new TokenUsage(10, 60, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null));
        }, executor, tools.Catalog(), new ContextMaterializer(new FakeTokenCounter(), []),
            new ExecutionFingerprint("rules", "tools", "model", "context", "none", "M3"),
            new ModelSelection(new ModelIdValue("scripted"), 600, ToolMode.Direct, null), store, codecs, artifacts,
            new InMemoryAuditSink(), new RedactionPolicy(), harness);
        try { body(server, store, codecs, artifacts, opened, turn, requests); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
