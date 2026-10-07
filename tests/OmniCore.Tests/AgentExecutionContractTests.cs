using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class AgentExecutionContractTests
{
    [Fact]
    public void Execution_id_is_UUIDv7()
    {
        var id = ExecutionId.New();
        Assert.Equal(7, id.Value.Version);
        Assert.Equal(id.Value, ExecutionId.Parse(id.ToString()).Value);
    }

    [Fact]
    public void Task_created_v2_roundtrips_parent_and_v1_payload_upcasts_missing_parent_as_null()
    {
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var run = RunId.New();
        var task = TaskId.New();
        var parent = TaskId.New();
        var taskPayload = new TaskCreated(task, run, "child", Array.Empty<TaskDependency>(),
            new TaskBudget(null, null, null, null), parent);
        Assert.Equal(2, taskPayload.SchemaVersion());

        var taskCodec = codecs.CodecFor(taskPayload.Type());
        Assert.Equal(2, codecs.CurrentVersion(taskPayload.Type()));
        var encodedV2 = taskCodec.Encode(taskPayload);
        Assert.Contains("ParentTaskId", encodedV2, StringComparison.Ordinal);
        var eventV2 = DomainEvent.Create(session, taskPayload.Type(), 2, null, run, run, task, null, null,
            null, null, Array.Empty<ArtifactRef>(), encodedV2);
        var decodedV2 = Assert.IsType<TaskCreated>(codecs.Decode(eventV2));
        Assert.Equal(task, decodedV2.TaskId);
        Assert.Equal(run, decodedV2.RunId);
        Assert.Equal(parent, decodedV2.ParentTaskId);

        var legacyPayload = new TaskCreated(task, run, "legacy", Array.Empty<TaskDependency>(),
            new TaskBudget(null, null, null, null));
        var legacyJson = JsonNode.Parse(taskCodec.Encode(legacyPayload))!.AsObject();
        Assert.True(legacyJson.Remove(nameof(TaskCreated.ParentTaskId)));
        var eventV1 = DomainEvent.Create(session, legacyPayload.Type(), 1, null, run, run, task, null, null,
            null, null, Array.Empty<ArtifactRef>(), legacyJson.ToJsonString());
        var decodedV1 = Assert.IsType<TaskCreated>(codecs.Decode(eventV1));
        Assert.Equal(task, decodedV1.TaskId);
        Assert.Equal(run, decodedV1.RunId);
        Assert.Null(decodedV1.ParentTaskId);
    }

    [Fact]
    public void Agent_execution_lifecycle_roundtrips_in_SQLite_without_completing_lane_task_or_run()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-agent-execution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var stream = new EventStream(store, codecs, session);
            var rootProfile = ProfileId.New();
            var run = TestRun.Open(stream, session, agentProfile: rootProfile);
            var childTask = TaskId.New();
            stream.Append(new TaskCreated(childTask, run.RunId, "nested work", Array.Empty<TaskDependency>(),
                new TaskBudget(null, null, null, null), run.RootTask));
            var execution = ExecutionId.New();
            var parent = ExecutionId.New();
            var profile = ProfileId.New();
            var childLane = LaneId.New();
            stream.Append(new LaneCreated(childLane, childTask, profile));
            var beforeLifecycle = CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1)).Snapshot();
            // A parent is a durable execution, not a random ID in the child's payload.
            var awaitedUnmanagedId = parent;
            var awaitedUnmanaged = new AgentExecutionStarted(awaitedUnmanagedId, run.RootLane,
                rootProfile, null, ExecutionRelation.Awaited, ExecutionSupervision.Unmanaged);
            stream.Append(awaitedUnmanaged);
            var detachedManaged = new AgentExecutionStarted(execution, childLane, profile, parent,
                ExecutionRelation.Detached, ExecutionSupervision.Managed);
            stream.Append(detachedManaged);
            stream.Append(new AgentExecutionCompleted(execution, childLane, profile, parent,
                ExecutionRelation.Detached, ExecutionSupervision.Managed));
            stream.Append(new AgentExecutionFailed(awaitedUnmanaged.ExecutionId, awaitedUnmanaged.LaneId,
                awaitedUnmanaged.ProfileId, awaitedUnmanaged.ParentExecutionId,
                awaitedUnmanaged.Relation, awaitedUnmanaged.Supervision));

            Release(store);
            store = new SqliteEventStore(journal);
            var persisted = store.ReadFrom(session, 1);
            var payloads = persisted.Select(codecs.Decode).ToArray();
            var started = payloads.OfType<AgentExecutionStarted>().ToArray();
            Assert.Equal(2, started.Length);
            Assert.Contains(started, evt => evt.ExecutionId == execution && evt.ParentExecutionId == parent
                && evt.LaneId == childLane && evt.ProfileId == profile
                && evt.Relation == ExecutionRelation.Detached && evt.Supervision == ExecutionSupervision.Managed);
            Assert.Contains(started, evt => evt.ExecutionId == awaitedUnmanagedId && evt.ParentExecutionId is null
                && evt.Relation == ExecutionRelation.Awaited && evt.Supervision == ExecutionSupervision.Unmanaged);
            var completed = Assert.Single(payloads.OfType<AgentExecutionCompleted>());
            Assert.Equal(new AgentExecutionCompleted(execution, childLane, profile, parent,
                ExecutionRelation.Detached, ExecutionSupervision.Managed), completed);
            var failed = Assert.Single(payloads.OfType<AgentExecutionFailed>());
            Assert.Equal(new AgentExecutionFailed(awaitedUnmanagedId, awaitedUnmanaged.LaneId,
                awaitedUnmanaged.ProfileId, null, ExecutionRelation.Awaited, ExecutionSupervision.Unmanaged), failed);
            Assert.Equal(new[] { "agent_execution.started", "agent_execution.started",
                    "agent_execution.completed", "agent_execution.failed" },
                persisted.Where(evt => evt.Type.ToString().StartsWith("agent_execution.", StringComparison.Ordinal))
                    .Select(evt => evt.Type.ToString()));

            var persistedChild = Assert.Single(payloads.OfType<TaskCreated>(), evt => evt.TaskId == childTask);
            Assert.Equal(run.RootTask, persistedChild.ParentTaskId);
            var replayed = CanonicalStateTracker.Replay(codecs, persisted);
            Assert.Equal(beforeLifecycle, replayed.Snapshot());
            _ = PreM6RecordProjection.Replay(session, codecs, persisted);
            Assert.Equal(RunState.Running, replayed.Run(run.RunId));
            Assert.Equal(TaskState.Running, replayed.Task(run.RootTask));
            Assert.Equal(TaskState.Pending, replayed.Task(childTask));
            Assert.Equal(LaneState.Running, replayed.Lane(run.RootLane));
            Assert.DoesNotContain(payloads, payload => payload is TaskCompleted or LaneCompleted or RunCompleted);
        }
        finally
        {
            if (store is not null)
            {
                Release(store);
            }

            Directory.Delete(root, true);
        }
    }

    private static void Release(SqliteEventStore store)
    {
        var connection = Assert.IsType<SqliteConnection>(store.Connection);
        store.Close();
        SqliteConnection.ClearPool(connection);
        connection.Dispose();
    }
}
