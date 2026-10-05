using Microsoft.Data.Sqlite;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class ExecutionScopeTests
{
    [Fact]
    public void Event_stream_uses_scope_only_for_missing_ids_and_keeps_payload_ids_and_no_scope_behavior()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-exec-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(path);
            var session = SessionId.New();
            var stream = new EventStream(store, EventCodecs.Create(), session);
            var run = TestRun.Open(stream, session);
            var turnId = TurnId.New();
            var payloadRun = run.RunId;
            var payloadTask = TaskId.New();
            var scopedRun = RunId.New();
            var scopedTask = TaskId.New();
            var scopedLane = LaneId.New();
            var scopedTurn = TurnId.New();
            var scopedCall = ToolCallId.New();
            var payloadCall = ToolCallId.New();
            using (ExecutionScope.Begin(new ExecutionScopeState(scopedRun, scopedTask, scopedLane,
                scopedTurn, scopedCall)))
            {
                stream.Append(new TaskCreated(payloadTask, payloadRun, "payload priority",
                    Array.Empty<TaskDependency>(), new TaskBudget(null, null, null, null)));
                stream.Append(new TurnStarted(turnId, run.RootLane,
                    new ExecutionFingerprint("m", "h", "t", "c", "o", "v"), null));
                stream.Append(new ToolCallRequested(payloadCall, "provider", "test.read", "{}"));
            }

            var events = store.ReadFrom(session, 1);
            var taskCreated = events.Single(evt => evt.Type.ToString() == "task.created"
                && evt.TaskId == payloadTask);
            Assert.Equal(payloadRun, taskCreated.RunId);
            Assert.Equal(payloadTask, taskCreated.TaskId);
            var started = events.Single(evt => evt.Type.ToString() == "turn.started");
            Assert.Equal(scopedRun, started.RunId); // absent from payload: ambient attribution applies
            Assert.Equal(scopedTask, started.TaskId); // absent from payload
            Assert.Equal(run.RootLane, started.LaneId); // payload is authoritative
            Assert.Equal(turnId, started.TurnId); // payload is authoritative
            var requested = events.Single(evt => evt.Type.ToString() == "toolcall.requested");
            Assert.Equal(scopedRun, requested.RunId);
            Assert.Equal(scopedTask, requested.TaskId);
            Assert.Equal(scopedLane, requested.LaneId);
            Assert.Equal(scopedTurn, requested.TurnId);
            Assert.Equal(payloadCall, requested.ToolCallId); // payload call id wins

            var noScopeSession = SessionId.New();
            var noScope = new EventStream(store, EventCodecs.Create(), noScopeSession);
            var noScopeRun = TestRun.Open(noScope, noScopeSession);
            var rootCreated = store.ReadFrom(noScopeSession, 1).Single(evt => evt.Type.ToString() == "run.created");
            Assert.Equal(noScopeRun.RunId, rootCreated.RunId);
            Assert.Null(rootCreated.TaskId);
            Assert.Null(rootCreated.LaneId);
            Assert.Null(rootCreated.TurnId);
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + path);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Nested_scope_restores_parent_idempotently_and_flows_across_await_without_leaking_to_parent()
    {
        var outer = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New(), TurnId.New());
        using (ExecutionScope.Begin(outer))
        {
            Assert.Equal(outer, ExecutionScope.Current);
            var inner = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New(), TurnId.New());
            var scope = ExecutionScope.Begin(inner);
            Assert.Equal(inner, ExecutionScope.Current);
            await Task.Yield();
            Assert.Equal(inner, ExecutionScope.Current);
            scope.Dispose();
            scope.Dispose();
            Assert.Equal(outer, ExecutionScope.Current);

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var childA = Task.Run(async () =>
            {
                using var childScope = ExecutionScope.Begin(inner);
                entered.SetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal(inner, ExecutionScope.Current);
            }, TestContext.Current.CancellationToken);
            var childB = Task.Run(async () =>
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal(outer, ExecutionScope.Current);
            }, TestContext.Current.CancellationToken);
            await childB;
            release.SetResult();
            await childA;
            Assert.Equal(outer, ExecutionScope.Current);
        }

        Assert.Null(ExecutionScope.Current);
    }
}
