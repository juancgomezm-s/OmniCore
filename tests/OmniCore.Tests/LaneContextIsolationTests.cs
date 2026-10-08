using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class LaneContextIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Current_run_replay_inherits_only_the_target_lane(bool childTarget)
    {
        using var fx = new Fixture();
        var child = fx.Child();
        var sibling = fx.Child();
        fx.Record(fx.Run.RootTask, fx.Run.RootLane, "principal");
        fx.Record(child.Task, child.Lane, "child");
        fx.Record(sibling.Task, sibling.Lane, "sibling");
        var target = childTarget ? child.Lane : fx.Run.RootLane;
        var own = childTarget ? "child" : "principal";
        var history = fx.Explorer().LoadConversation(new EventStream(fx.Store, fx.Codecs, fx.Session),
            fx.Run.RunId, activeLaneId: target);
        var text = HistoryText(history);
        Assert.Contains(own + " input", text);
        Assert.Contains(own + " answer", text);
        Assert.Contains(own + " tool refusal", text);
        Assert.Contains(own + " steering", text);
        Assert.DoesNotContain("sibling", text);
        Assert.DoesNotContain((childTarget ? "principal" : "child") + " input", text);
        Assert.DoesNotContain((childTarget ? "principal" : "child") + " answer", text);
        Assert.DoesNotContain((childTarget ? "principal" : "child") + " tool refusal", text);
        Assert.DoesNotContain((childTarget ? "principal" : "child") + " steering", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_request_after_reopen_uses_only_its_lane_checkpoint(bool childTarget)
    {
        using var fx = new Fixture();
        var child = fx.Child();
        fx.Record(fx.Run.RootTask, fx.Run.RootLane, "principal");
        fx.Record(child.Task, child.Lane, "child");
        fx.Checkpoint(fx.Run.RootTask, fx.Run.RootLane, "principal checkpoint", true);
        fx.Checkpoint(child.Task, child.Lane, "child checkpoint", true);
        fx.Reopen();
        ModelRequest? request = null;
        var explorer = fx.Explorer((received, _) => { request = received; return Response(); });
        var result = explorer.Ask("current intent", "system", fx.Session, fx.Run.RunId,
            childTarget ? child.Lane : fx.Run.RootLane, "fresh state", TestContext.Current.CancellationToken);
        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.NotNull(request);
        Assert.Contains((childTarget ? "child" : "principal") + " checkpoint", request.Instructions);
        Assert.DoesNotContain((childTarget ? "principal" : "child") + " checkpoint", request.Instructions);
        var text = HistoryText(request.Messages);
        Assert.Contains("current intent", text);
        Assert.DoesNotContain((childTarget ? "principal" : "child") + " input", text);
        Assert.Contains("fresh state", request.Instructions);
    }

    [Fact]
    public void Child_does_not_implicitly_receive_the_sessions_principal_history_or_run_summaries()
    {
        using var fx = new Fixture();
        fx.Record(fx.Run.RootTask, fx.Run.RootLane, "earlier principal");
        new RunControlService(fx.Store, fx.Codecs).CancelRun(fx.Session, fx.Run.RunId);
        fx.Run = TestRun.Open(fx.Store, fx.Session, "next run");
        var child = fx.Child();
        var stream = new EventStream(fx.Store, fx.Codecs, fx.Session);
        stream.Append(new UserInputReceived(fx.Run.RunId, "[\"unscoped principal input\"]", null));
        ModelRequest? request = null;
        var explorer = fx.Explorer((received, _) => { request = received; return Response(); });
        var result = explorer.Ask("child intent", "system", fx.Session, fx.Run.RunId, child.Lane,
            "fresh child state", TestContext.Current.CancellationToken);
        Assert.Equal(StopReason.EndTurn, result.StopReason);
        Assert.NotNull(request);
        Assert.DoesNotContain("earlier principal", HistoryText(request.Messages));
        Assert.DoesNotContain("unscoped principal", HistoryText(request.Messages));
        Assert.DoesNotContain("Historical Run summary", request.Instructions);
        Assert.Contains("child intent", HistoryText(request.Messages));
        Assert.Contains("earlier principal answer", HistoryText(explorer.LoadConversation(stream, fx.Run.RunId,
            activeLaneId: fx.Run.RootLane)));
    }

    [Fact]
    public void Legacy_multilane_checkpoint_is_not_reused_after_lane_local_replay_changes_its_indices()
    {
        using var fx = new Fixture();
        fx.Child();
        fx.Checkpoint(fx.Run.RootTask, fx.Run.RootLane, "legacy potentially mixed transcript", false);
        ModelRequest? request = null;
        var explorer = fx.Explorer((received, _) => { request = received; return Response(); });
        Assert.Equal(StopReason.EndTurn, explorer.Ask("safe current input", "system", fx.Session,
            fx.Run.RunId, fx.Run.RootLane, "fresh state", TestContext.Current.CancellationToken).StopReason);
        Assert.DoesNotContain("legacy potentially mixed transcript", request!.Instructions);
    }

    [Fact]
    public void Legacy_turn_and_tool_receipts_resolve_owner_without_borrowing_the_latest_lane()
    {
        using var fx = new Fixture();
        var child = fx.Child();
        var turn = TurnId.New(); var call = ToolCallId.New();
        var stream = new EventStream(fx.Store, fx.Codecs, fx.Session);
        stream.Append(new TurnStarted(turn, fx.Run.RootLane));
        using (ExecutionScope.Begin(new ExecutionScopeState(fx.Run.RunId, TurnId: turn)))
            stream.Append(new ToolCallRequested(call, "legacy-call", "fake.read", "{}"));
        fx.Record(child.Task, child.Lane, "child");
        stream.Append(new ToolCallRejected(call, "principal legacy tool receipt"));
        stream.Append(new ModelCompleted(turn, fx.Artifacts.PutText("principal legacy response", "text/plain",
            ArtifactKind.ModelResponse, Sensitivity.Sensitive)));
        stream.Append(new TurnCompleted(turn));
        var explorer = fx.Explorer();
        var rootText = HistoryText(explorer.LoadConversation(stream, fx.Run.RunId, activeLaneId: fx.Run.RootLane));
        Assert.Contains("principal legacy tool receipt", rootText);
        Assert.Contains("principal legacy response", rootText);
        var childText = HistoryText(explorer.LoadConversation(stream, fx.Run.RunId, activeLaneId: child.Lane));
        Assert.DoesNotContain("principal legacy", childText);
    }

    [Fact]
    public void Unattributed_multilane_tool_result_is_not_guessed_from_the_last_turn()
    {
        using var fx = new Fixture();
        var child = fx.Child();
        var call = ToolCallId.New();
        var stream = new EventStream(fx.Store, fx.Codecs, fx.Session);
        stream.Append(new ToolCallRequested(call, "unattributed", "fake.read", "{}"));
        stream.Append(new ToolCallRejected(call, "unknown private output"));
        fx.Record(child.Task, child.Lane, "child");
        foreach (var target in new[] { fx.Run.RootLane, child.Lane })
            Assert.DoesNotContain("unknown private output", HistoryText(fx.Explorer().LoadConversation(stream,
                fx.Run.RunId, activeLaneId: target)));
    }

    [Fact]
    public void Real_compaction_publishes_lane_local_checkpoints_and_preserves_them_after_reopen()
    {
        using var fx = new Fixture();
        var child = fx.Child();
        var policy = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 4, GuidanceLevel.Off, 1,
            PlanControl.RuntimeDriven, 4, new ContextManagementPolicy(4096, 1200, 2, 2, 6000));
        var explorer = fx.Explorer(harness: policy);
        foreach (var lane in new[] { fx.Run.RootLane, child.Lane })
            for (var i = 0; i < 3; i++)
                Assert.Equal(StopReason.EndTurn, explorer.Ask("goal " + lane + " " + i, "system", fx.Session,
                    fx.Run.RunId, lane, "fresh state", TestContext.Current.CancellationToken).StopReason);
        var checkpoints = fx.Store.ReadFrom(fx.Session, 1).Where(evt => fx.Codecs.Decode(evt) is ContextCheckpointRecorded).ToArray();
        Assert.Equal(2, checkpoints.Length);
        foreach (var evt in checkpoints)
        {
            var receipt = (ContextCheckpointRecorded)fx.Codecs.Decode(evt);
            using var json = JsonDocument.Parse(fx.Artifacts.GetText(receipt.CheckpointArtifact.Hash)!);
            Assert.Equal(evt.LaneId!.ToString(), json.RootElement.GetProperty("laneId").GetString());
            var other = evt.LaneId == child.Lane ? fx.Run.RootLane : child.Lane;
            Assert.DoesNotContain(other.ToString(), json.RootElement.GetProperty("summary").GetString()!);
        }
        fx.Reopen();
        foreach (var lane in new[] { fx.Run.RootLane, child.Lane })
        {
            ModelRequest? request = null;
            var restored = fx.Explorer((received, _) => { request = received; return Response(); });
            Assert.Equal(StopReason.EndTurn, restored.Ask("continue " + lane, "system", fx.Session,
                fx.Run.RunId, lane, "fresh state", TestContext.Current.CancellationToken).StopReason);
            Assert.Contains("goal " + lane, request!.Instructions);
            Assert.DoesNotContain("goal " + (lane == child.Lane ? fx.Run.RootLane : child.Lane), request.Instructions);
        }
    }

    private static ModelResponse Response() => new(new ContentBlock[] { new TextBlock("done") },
        StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null));

    private static string HistoryText(IEnumerable<ModelMessage> messages) => string.Join("\n",
        messages.SelectMany(message => message.Content).Select(block => block switch {
            TextBlock text => text.Text,
            ToolCallBlock call => call.ArgumentsJson,
            ToolResultBlock result => string.Join("\n", result.Content.OfType<TextBlock>().Select(text => text.Text)),
            _ => "",
        }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Agent_artifact_read_authority_is_lane_local_not_the_entire_session(bool childTarget)
    {
        using var fx = new Fixture();
        var child = fx.Child();
        fx.Record(fx.Run.RootTask, fx.Run.RootLane, "principal");
        fx.Record(child.Task, child.Lane, "child");
        var answers = fx.Store.ReadFrom(fx.Session, 1).Select(fx.Codecs.Decode).OfType<AssistantMessageRecorded>().ToArray();
        var auth = new SessionArtifactReadAuthorization(fx.Store, () => fx.Session, fx.Artifacts);
        using var scope = ExecutionScope.Begin(new ExecutionScopeState(fx.Run.RunId,
            childTarget ? child.Task : fx.Run.RootTask, childTarget ? child.Lane : fx.Run.RootLane));
        Assert.True(auth.IsReferenced(answers[childTarget ? 1 : 0].ContentRef!.Hash.Value));
        Assert.False(auth.IsReferenced(answers[childTarget ? 0 : 1].ContentRef!.Hash.Value));
    }

    [Fact]
    public void Mentioning_another_lanes_hash_or_an_orphan_hash_in_input_does_not_grant_artifact_read()
    {
        using var fx = new Fixture();
        var child = fx.Child();
        fx.Record(fx.Run.RootTask, fx.Run.RootLane, "principal");
        var privateRef = fx.Store.ReadFrom(fx.Session, 1).Select(fx.Codecs.Decode).OfType<AssistantMessageRecorded>().Single().ContentRef!;
        var orphan = fx.Artifacts.PutText("unreferenced private data", "text/plain", ArtifactKind.Other, Sensitivity.Sensitive);
        var auth = new SessionArtifactReadAuthorization(fx.Store, () => fx.Session, fx.Artifacts);
        Assert.Equal(StopReason.EndTurn, fx.Explorer().Ask("artifact=sha256:" + privateRef.Hash.Value
            + "; hash=\"" + orphan.Hash.Value + "\"", "system", fx.Session, fx.Run.RunId, child.Lane,
            "fresh state", TestContext.Current.CancellationToken).StopReason);
        using var scope = ExecutionScope.Begin(new ExecutionScopeState(fx.Run.RunId, child.Task, child.Lane));
        Assert.False(auth.IsReferenced(privateRef.Hash.Value));
        Assert.False(auth.IsReferenced(orphan.Hash.Value));
    }

    [Fact]
    public void Actual_externalized_tool_output_is_still_readable_only_by_its_own_lane()
    {
        using var fx = new Fixture();
        var child = fx.Child();
        fx.Record(child.Task, child.Lane, "child " + new string('x', 5000));
        Assert.Equal(StopReason.EndTurn, fx.Explorer().Ask("continue", "system", fx.Session, fx.Run.RunId,
            child.Lane, "fresh state", TestContext.Current.CancellationToken).StopReason);
        var snapshot = fx.Store.ReadFrom(fx.Session, 1).Select(fx.Codecs.Decode).OfType<ModelStepStarted>().Last().ContextSnapshotRef!;
        using var json = JsonDocument.Parse(fx.Artifacts.GetText(snapshot.Hash)!);
        var output = json.RootElement.GetProperty("items").EnumerateArray().Where(item => item.GetProperty("kind").GetString() == "ToolResult")
            .SelectMany(item => item.GetProperty("refs").EnumerateArray()).Select(reference => reference.GetString()!)
            .Single(reference => reference.StartsWith("artifact=sha256:"))["artifact=sha256:".Length..];
        var auth = new SessionArtifactReadAuthorization(fx.Store, () => fx.Session, fx.Artifacts);
        using (ExecutionScope.Begin(new ExecutionScopeState(fx.Run.RunId, child.Task, child.Lane)))
            Assert.True(auth.IsReferenced(output));
        using (ExecutionScope.Begin(new ExecutionScopeState(fx.Run.RunId, fx.Run.RootTask, fx.Run.RootLane)))
            Assert.False(auth.IsReferenced(output));
    }

    [Fact]
    public void Ambiguous_legacy_checkpoint_and_incomplete_agent_scope_cannot_grant_artifact_read()
    {
        using var fx = new Fixture();
        fx.Child();
        fx.Checkpoint(fx.Run.RootTask, fx.Run.RootLane, "legacy mixed data", false);
        var checkpoint = fx.Store.ReadFrom(fx.Session, 1).Select(fx.Codecs.Decode).OfType<ContextCheckpointRecorded>().Single().CheckpointArtifact;
        var auth = new SessionArtifactReadAuthorization(fx.Store, () => fx.Session, fx.Artifacts);
        using (ExecutionScope.Begin(new ExecutionScopeState(fx.Run.RunId, fx.Run.RootTask, fx.Run.RootLane)))
            Assert.False(auth.IsReferenced(checkpoint.Hash.Value));
        using (ExecutionScope.Begin(new ExecutionScopeState(fx.Run.RunId)))
            Assert.False(auth.IsReferenced(checkpoint.Hash.Value));
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-lane-context-" + Guid.NewGuid().ToString("N"));
        private string Journal => Path.Combine(_root, "journal.db");
        public SqliteEventStore Store { get; private set; }
        public IEventCodecRegistry Codecs { get; } = EventCodecs.Create();
        public SessionId Session { get; } = SessionId.New();
        public TestRun.Opened Run { get; set; }
        public FileArtifactStore Artifacts { get; }
        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Store = new SqliteEventStore(Journal);
            Artifacts = new FileArtifactStore(_root);
            Run = TestRun.Open(Store, Session);
        }
        public (TaskId Task, LaneId Lane) Child()
        {
            var task = TaskId.New(); var lane = LaneId.New();
            new EventStream(Store, Codecs, Session).AppendBatch(new DomainEventPayload[] {
                new TaskCreated(task, Run.RunId, "child objective", Array.Empty<TaskDependency>(),
                    new TaskBudget(null, null, null, null), Run.RootTask), new TaskReady(task),
                new LaneCreated(lane, task, ProfileId.New()), new LaneStarted(lane), new TaskStarted(task, lane),
            }, DurabilityClass.Standard);
            return (task, lane);
        }
        public void Record(TaskId task, LaneId lane, string label)
        {
            var turn = TurnId.New(); var call = ToolCallId.New(); var steering = SteeringId.New();
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(Run.RunId, task, lane, turn));
            new EventStream(Store, Codecs, Session).AppendBatch(new DomainEventPayload[] {
                new UserInputReceived(Run.RunId, JsonSerializer.Serialize(new[] { label + " input" }), null),
                new TurnStarted(turn, lane), new ToolCallRequested(call, "call-" + call, "fake.read", "{}"),
                new ToolCallRejected(call, label + " tool refusal"),
                new TurnSteeringReceived(steering, Run.RunId, lane, turn, JsonSerializer.Serialize(new[] { label + " steering" })),
                new TurnSteeringApplied(steering, Run.RunId, lane, turn, 1),
                new AssistantMessageRecorded(Run.RunId, lane, turn, Artifacts.PutText(label + " answer",
                    "text/markdown", ArtifactKind.ModelResponse, Sensitivity.Sensitive)), new TurnCompleted(turn),
            }, DurabilityClass.Standard);
        }
        public void Checkpoint(TaskId task, LaneId lane, string text, bool laneLocal)
        {
            var json = JsonSerializer.Serialize(new { summary = text, compactedThroughItemIndex = -1 });
            if (laneLocal) json = JsonSerializer.Serialize(new { summary = text, compactedThroughItemIndex = -1,
                laneId = lane.ToString() });
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(Run.RunId, task, lane));
            new EventStream(Store, Codecs, Session).Append(new ContextCheckpointRecorded("cp-" + lane,
                Run.RunId, Store.CurrentSequence(Session), Artifacts.PutText(json, "application/json",
                    ArtifactKind.ContextSnapshot, Sensitivity.Sensitive), "deterministic-v1"));
        }
        public ExplorerTurn Explorer(Func<ModelRequest, CancellationToken, ModelResponse>? provider = null,
            HarnessPolicy? harness = null, IReadOnlyList<IContextContributor>? contributors = null, int budget = 8192)
        {
            var catalog = new FakeCatalog();
            return new ExplorerTurn(provider ?? ((_, _) => Response()), ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), _root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), contributors ?? Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture"),
                new ModelSelection(new ModelIdValue("scripted"), budget, ToolMode.Direct, null),
                Store, Codecs, Artifacts, new InMemoryAuditSink(), new RedactionPolicy(), harness);
        }
        public void Reopen() { Store.Close(); Store = new SqliteEventStore(Journal); }
        public void Dispose()
        {
            Store.Close();
            using var connection = new SqliteConnection("DataSource=" + Journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(_root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
