using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Microsoft.Data.Sqlite;

namespace OmniCore.Tests;

public sealed class AgentLaneTranscriptTests
{
    [Fact]
    public void Reopened_host_reports_unknown_liveness_for_durable_running_lane()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var stream = new EventStream(store, codecs, session);
        var run = TestRun.Open(stream, session, "reopen heartbeat");
        var turn = TurnId.New();
        stream.AppendBatch(new DomainEventPayload[] {
            new TurnStarted(turn, run.RootLane),
            new ModelStepStarted(turn, 0, "fixture", 4096, "Direct", null, null, null),
        }, DurabilityClass.Barrier, new ExecutionScopeState?[] {
            new(run.RunId, run.RootTask, run.RootLane, TurnId: turn),
            new(run.RunId, run.RootTask, run.RootLane, TurnId: turn),
        });

        // A new Host instance replays the same Run, but replay is not an in-process heartbeat.
        var reopenedObservation = new SessionObservationHub(store, codecs, null);
        var snapshot = AgentLaneReader.Read(store, codecs, session, run.RunId,
            activities: reopenedObservation.Activities(session));

        Assert.Equal("Unknown", snapshot.Heartbeat?.State);
        Assert.Equal(0, snapshot.Heartbeat?.ObservedActiveLanes);
        var root = Assert.Single(snapshot.Lanes);
        Assert.Equal("Running", root.LaneState);
        Assert.Equal("Unknown", root.Heartbeat?.State);
        Assert.Contains("not liveness", root.Heartbeat!.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspector_transcripts_keep_interleaved_lanes_and_tool_receipts_attributed()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var artifactRoot = Path.Combine(Path.GetTempPath(), "omni-agent-transcript-" + Guid.NewGuid().ToString("N"));
        var artifacts = OmniHost.CreateArtifactStore(artifactRoot);
        try
        {
            var stream = new EventStream(store, codecs, session);
            var rootProfile = ProfileId.New();
            var run = TestRun.Open(stream, session, "parallel lane transcript", RunMode.Orchestrate,
                agentProfile: rootProfile);
            var parentExecution = ExecutionId.New();
            stream.Append(new AgentExecutionStarted(parentExecution, run.RootLane, rootProfile, null,
                ExecutionRelation.Awaited, ExecutionSupervision.Unmanaged));

            var childTask = TaskId.New();
            var childLane = LaneId.New();
            var childProfile = ProfileId.New();
            var childExecution = ExecutionId.New();
            stream.AppendBatch(new DomainEventPayload[] {
                new TaskCreated(childTask, run.RunId, "child reader", [], new TaskBudget(null, null, null, null), run.RootTask),
                new TaskReady(childTask),
                new LaneCreated(childLane, childTask, childProfile),
                new LaneStarted(childLane),
                new TaskStarted(childTask, childLane),
                new AgentExecutionStarted(childExecution, childLane, childProfile, parentExecution,
                    ExecutionRelation.Detached, ExecutionSupervision.Managed),
            }, DurabilityClass.Barrier, new ExecutionScopeState?[] {
                new(run.RunId), new(run.RunId), new(run.RunId, childTask, childLane),
                new(run.RunId, childTask, childLane), new(run.RunId, childTask, childLane),
                new(run.RunId, childTask, childLane, ExecutionId: childExecution),
            });

            var rootTurn = TurnId.New();
            var childTurn = TurnId.New();
            var rootTool = ToolCallId.New();
            var childTool = ToolCallId.New();
            var rootText = artifacts.PutText("root transcript only", "text/markdown", ArtifactKind.ModelResponse, Sensitivity.Normal);
            var childText = artifacts.PutText("child transcript only", "text/markdown", ArtifactKind.ModelResponse, Sensitivity.Normal);
            var corruptText = artifacts.PutText("corrupt transcript must stay hidden", "text/markdown",
                ArtifactKind.ModelResponse, Sensitivity.Normal);
            stream.AppendBatch(new DomainEventPayload[] {
                new TurnStarted(rootTurn, run.RootLane),
                new AssistantMessageRecorded(run.RunId, run.RootLane, rootTurn, rootText),
                new AssistantMessageRecorded(run.RunId, run.RootLane, rootTurn, corruptText),
                new ToolCallRequested(rootTool, "root-call", "filesystem.read", "{}"),
                new ToolCallPrepared(rootTool, "{}"),
                new ToolCallAuthorized(rootTool),
                new ToolCallStarted(rootTool, EffectClass.None, null),
                new ToolCallSucceeded(rootTool, "{}"),
                new TurnStarted(childTurn, childLane),
                new AssistantMessageRecorded(run.RunId, childLane, childTurn, childText),
                new ToolCallRequested(childTool, "child-call", "filesystem.read", "{}"),
                new ToolCallPrepared(childTool, "{}"),
                new ToolCallAuthorized(childTool),
                new ToolCallStarted(childTool, EffectClass.None, null),
                new ToolCallSucceeded(childTool, "{}"),
            }, DurabilityClass.Barrier, new ExecutionScopeState?[] {
                new(run.RunId, run.RootTask, run.RootLane, TurnId: rootTurn, ExecutionId: parentExecution),
                new(run.RunId, run.RootTask, run.RootLane, TurnId: rootTurn, ExecutionId: parentExecution),
                new(run.RunId, run.RootTask, run.RootLane, TurnId: rootTurn, ExecutionId: parentExecution),
                new(run.RunId, run.RootTask, run.RootLane, TurnId: rootTurn, ToolCallId: rootTool, ExecutionId: parentExecution),
                new(run.RunId, run.RootTask, run.RootLane, TurnId: rootTurn, ToolCallId: rootTool, ExecutionId: parentExecution),
                new(run.RunId, run.RootTask, run.RootLane, TurnId: rootTurn, ToolCallId: rootTool, ExecutionId: parentExecution),
                new(run.RunId, run.RootTask, run.RootLane, TurnId: rootTurn, ToolCallId: rootTool, ExecutionId: parentExecution),
                new(run.RunId, run.RootTask, run.RootLane, TurnId: rootTurn, ToolCallId: rootTool, ExecutionId: parentExecution),
                new(run.RunId, childTask, childLane, TurnId: childTurn, ExecutionId: childExecution),
                new(run.RunId, childTask, childLane, TurnId: childTurn, ExecutionId: childExecution),
                new(run.RunId, childTask, childLane, TurnId: childTurn, ToolCallId: childTool, ExecutionId: childExecution),
                new(run.RunId, childTask, childLane, TurnId: childTurn, ToolCallId: childTool, ExecutionId: childExecution),
                new(run.RunId, childTask, childLane, TurnId: childTurn, ToolCallId: childTool, ExecutionId: childExecution),
                new(run.RunId, childTask, childLane, TurnId: childTurn, ToolCallId: childTool, ExecutionId: childExecution),
                new(run.RunId, childTask, childLane, TurnId: childTurn, ToolCallId: childTool, ExecutionId: childExecution),
            });

            var corruptBlob = Path.Combine(artifactRoot, "blobs", "sha256", corruptText.Hash.Value[..2],
                corruptText.Hash.Value[2..4], corruptText.Hash.Value);
            File.WriteAllText(corruptBlob, new string('x', checked((int)corruptText.Size)));

            var snapshot = AgentLaneReader.Read(store, codecs, session, run.RunId, artifacts);
            var root = Assert.Single(snapshot.Lanes, item => item.LaneId == run.RootLane.ToString());
            var child = Assert.Single(snapshot.Lanes, item => item.LaneId == childLane.ToString());
            Assert.Contains(root.Transcript!, item => item.Text == "root transcript only");
            Assert.DoesNotContain(root.Transcript!, item => item.Text == "corrupt transcript must stay hidden");
            Assert.DoesNotContain(root.Transcript!, item => item.Text == "child transcript only");
            Assert.Contains(child.Transcript!, item => item.Text == "child transcript only");
            Assert.DoesNotContain(child.Transcript!, item => item.Text == "root transcript only");
            var childRequest = Assert.Single(child.Transcript!, item => item.Kind == "tool");
            Assert.Equal(run.RunId.ToString(), childRequest.RunId);
            Assert.Equal(childTask.ToString(), childRequest.TaskId);
            Assert.Equal(childLane.ToString(), childRequest.LaneId);
            Assert.Equal(childTurn.ToString(), childRequest.TurnId);
            Assert.Equal(childExecution.ToString(), childRequest.ExecutionId);
            Assert.Equal(childTool.ToString(), childRequest.ToolCallId);

            var laterExecution = ExecutionId.New();
            stream.AppendBatch(new DomainEventPayload[] {
                new AgentExecutionCompleted(childExecution, childLane, childProfile, parentExecution,
                    ExecutionRelation.Detached, ExecutionSupervision.Managed),
                new AgentExecutionStarted(laterExecution, childLane, childProfile, parentExecution,
                    ExecutionRelation.Detached, ExecutionSupervision.Managed),
            }, DurabilityClass.Barrier, new ExecutionScopeState?[] {
                new(run.RunId, childTask, childLane, ExecutionId: childExecution),
                new(run.RunId, childTask, childLane, ExecutionId: laterExecution),
            });
            var ambiguous = Assert.Single(AgentLaneReader.Read(store, codecs, session, run.RunId, artifacts).Lanes,
                item => item.LaneId == childLane.ToString());
            Assert.True(ambiguous.ExecutionAmbiguous);
            Assert.Empty(ambiguous.Transcript!);
        }
        finally
        {
            if (artifacts is IDisposable disposable) disposable.Dispose();
            if (Directory.Exists(artifactRoot)) Directory.Delete(artifactRoot, recursive: true);
        }
    }

    [Fact]
    public void Reopened_host_query_does_not_treat_a_durable_model_step_as_live_heartbeat()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-host-heartbeat-reopen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journalPath = Path.Combine(root, "journal.sqlite");
        var statePath = Path.Combine(root, "last-run.txt");
        var store = new SqliteEventStore(journalPath);
        var codecs = EventCodecs.Create();
        var artifacts = new FileArtifactStore(Path.Combine(root, "cas"));
        try
        {
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), statePath, artifacts);
            server.ConfigureWorkspaceRoot(root);
            var ack = server.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"act\",\"objective\":\"heartbeat reopen fixture\",\"workspace\":"
                + System.Text.Json.JsonSerializer.Serialize(root) + "}"), TestContext.Current.CancellationToken);
            Assert.Equal("ok", ack.Status);
            var session = server.LastSessionId() ?? throw new InvalidDataException("Fixture did not create a Session.");
            var run = server.LastRunId() ?? throw new InvalidDataException("Fixture did not create a Run.");
            var lane = server.LastLaneId() ?? throw new InvalidDataException("Fixture did not create a Lane.");
            var rootTask = RunProjection.Replay(session, run, codecs,
                store.ReadFrom(session, 1).Where(item => item.RunId == run).ToArray()).RootTask
                ?? throw new InvalidDataException("Fixture did not create a root Task.");
            var turn = TurnId.New();
            new EventStream(store, codecs, session).AppendBatch(new DomainEventPayload[] {
                new TurnStarted(turn, lane),
                new ModelStepStarted(turn, 0, "offline-fixture", 4096, "Direct", null, null, null),
            }, DurabilityClass.Barrier, new ExecutionScopeState?[] {
                new(run, rootTask, lane, TurnId: turn),
                new(run, rootTask, lane, TurnId: turn),
            });

            // Reopen through OmniServer and query its normal /agents projection. The durable
            // open provider step is visible, but the new process has no live execution lease.
            var reopened = new OmniServer(store, codecs, new InMemoryAuditSink(), statePath, artifacts);
            var snapshot = AgentsJson.Decode(reopened.Query("agents", TestContext.Current.CancellationToken)!.Json)!;
            Assert.Equal("Unknown", snapshot.Heartbeat!.State);
            Assert.Equal(0, snapshot.Heartbeat.ObservedActiveLanes);
            var rootLane = Assert.Single(snapshot.Lanes);
            Assert.Equal("Unknown", rootLane.Heartbeat!.State);
            Assert.Equal("Running", rootLane.LaneState);
        }
        finally
        {
            store.Close();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
