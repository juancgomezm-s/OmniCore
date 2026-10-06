using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>
/// Verifica que las escalaciones persistidas v1 se upcastean al leer, sin reescribir el journal,
/// y que añadir su atribución v2 no altera el estado de Run/Task/Lane.
/// </summary>
public sealed class EscalationV1ReopenRegressionTests
{
    [Fact]
    public void V1_escalations_reopen_and_upcast_without_mutating_envelopes_or_completing_run()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-escalation-v1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session, "legacy escalation replay");
            var stream = new EventStream(store, codecs, session);
            var beforeEscalations = CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1)).Snapshot();

            // Construct historical v1 payloads by removing only fields introduced in v2. The
            // journal receives version 1 directly; decoding below must go through EventCodecs.
            var legacyRequested = new ModelEscalationRequested(run.RunId, "model-small", "model-large",
                EscalationCause.ContextLimit);
            var legacyApproved = new ModelEscalationApproved(run.RunId, "model-large", "test-operator");
            var legacyCompleted = new ModelEscalationCompleted(run.RunId, "model-large");
            var oldEvents = new[]
            {
                CreateV1Event(codecs, session, run, legacyRequested),
                CreateV1Event(codecs, session, run, legacyApproved),
                CreateV1Event(codecs, session, run, legacyCompleted),
            };
            foreach (var oldEvent in oldEvents)
                store.Append(session, oldEvent, DurabilityClass.Standard, CancellationToken.None);

            // The v2 contrast carries explicit payload and envelope attribution. It remains an
            // audit record only: these event types do not implicitly finish the active Run.
            var turn = TurnId.New();
            var lane = run.RootLane;
            stream.Append(new ModelEscalationRequested(run.RunId, "model-small", "model-large",
                EscalationCause.RepeatedFailure, turn, lane));
            stream.Append(new ModelEscalationApproved(run.RunId, "model-large", "test-operator", turn, lane));
            stream.Append(new ModelEscalationCompleted(run.RunId, "model-large", turn, lane));

            var beforeClose = store.ReadFrom(session, 1)
                .Where(evt => evt.Type.ToString().StartsWith("model.escalation_", StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(6, beforeClose.Length);
            Assert.Equal(new[] { 1, 1, 1, 2, 2, 2 }, beforeClose.Select(evt => evt.SchemaVersion));
            var legacyBeforeClose = beforeClose.Take(3).ToArray();
            Assert.All(legacyBeforeClose, evt => Assert.DoesNotContain("TurnId", evt.PayloadJson, StringComparison.Ordinal));
            Assert.All(legacyBeforeClose, evt => Assert.DoesNotContain("LaneId", evt.PayloadJson, StringComparison.Ordinal));

            Release(store);
            store = new SqliteEventStore(journal);

            var reopened = store.ReadFrom(session, 1);
            var persisted = reopened
                .Where(evt => evt.Type.ToString().StartsWith("model.escalation_", StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(beforeClose.Length, persisted.Length);
            for (var i = 0; i < persisted.Length; i++)
            {
                Assert.Equal(beforeClose[i].EventId, persisted[i].EventId);
                Assert.Equal(beforeClose[i].Sequence, persisted[i].Sequence);
                Assert.Equal(beforeClose[i].Timestamp, persisted[i].Timestamp);
                Assert.Equal(beforeClose[i].SchemaVersion, persisted[i].SchemaVersion);
                Assert.Equal(beforeClose[i].PayloadJson, persisted[i].PayloadJson);
                Assert.Equal(beforeClose[i].Source, persisted[i].Source);
                Assert.Equal(beforeClose[i].Causation, persisted[i].Causation);
                Assert.Equal(run.RunId, persisted[i].RunId);
                Assert.Equal(run.RunId, persisted[i].CorrelationId);
                Assert.Equal(TimeSpan.Zero, persisted[i].Timestamp.Offset);
                if (i < 3)
                {
                    Assert.Null(persisted[i].TurnId);
                    Assert.Null(persisted[i].LaneId);
                }
            }
            Assert.Equal(Enumerable.Range((int)legacyBeforeClose[0].Sequence, 6).Select(value => (long)value),
                persisted.Select(evt => evt.Sequence));

            var decoded = persisted.Select(codecs.Decode).ToArray();
            var requestedV1 = Assert.IsType<ModelEscalationRequested>(decoded[0]);
            Assert.Equal(run.RunId, requestedV1.RunId);
            Assert.Equal("model-small", requestedV1.FromModel);
            Assert.Equal("model-large", requestedV1.ToModel);
            Assert.Equal(EscalationCause.ContextLimit, requestedV1.Cause);
            Assert.Null(requestedV1.TurnId);
            Assert.Null(requestedV1.LaneId);

            var approvedV1 = Assert.IsType<ModelEscalationApproved>(decoded[1]);
            Assert.Equal(run.RunId, approvedV1.RunId);
            Assert.Equal("model-large", approvedV1.ToModel);
            Assert.Equal("test-operator", approvedV1.ApprovedBy);
            Assert.Null(approvedV1.TurnId);
            Assert.Null(approvedV1.LaneId);

            var completedV1 = Assert.IsType<ModelEscalationCompleted>(decoded[2]);
            Assert.Equal(run.RunId, completedV1.RunId);
            Assert.Equal("model-large", completedV1.ToModel);
            Assert.Null(completedV1.TurnId);
            Assert.Null(completedV1.LaneId);

            Assert.Equal(turn, Assert.IsType<ModelEscalationRequested>(decoded[3]).TurnId);
            Assert.Equal(lane, Assert.IsType<ModelEscalationRequested>(decoded[3]).LaneId);
            Assert.Equal(turn, Assert.IsType<ModelEscalationApproved>(decoded[4]).TurnId);
            Assert.Equal(lane, Assert.IsType<ModelEscalationApproved>(decoded[4]).LaneId);
            Assert.Equal(turn, Assert.IsType<ModelEscalationCompleted>(decoded[5]).TurnId);
            Assert.Equal(lane, Assert.IsType<ModelEscalationCompleted>(decoded[5]).LaneId);
            Assert.Equal(lane, persisted[3].LaneId);
            Assert.Equal(turn, persisted[3].TurnId);

            // Decode is a view/upcast; persisted v1 schema and JSON remain untouched.
            Assert.Equal(1, persisted[0].SchemaVersion);
            using (var json = JsonDocument.Parse(persisted[0].PayloadJson))
            {
                Assert.False(json.RootElement.TryGetProperty("TurnId", out _));
                Assert.False(json.RootElement.TryGetProperty("LaneId", out _));
            }

            var replayed = CanonicalStateTracker.Replay(codecs, reopened);
            Assert.Equal(beforeEscalations, replayed.Snapshot());
            Assert.Equal(RunState.Running, replayed.Run(run.RunId));
            Assert.Equal(TaskState.Running, replayed.Task(run.RootTask));
            Assert.Equal(LaneState.Running, replayed.Lane(run.RootLane));
            Assert.DoesNotContain(decoded, payload => payload is RunCompleted or RunFailed or RunCancelled
                or TaskCompleted or TaskFailed or LaneCompleted or LaneFailed);
        }
        finally
        {
            if (store is not null)
                Release(store);
            Directory.Delete(root, recursive: true);
        }
    }

    private static DomainEvent CreateV1Event(EventCodecs codecs, SessionId session,
        TestRun.Opened run, DomainEventPayload currentPayload)
    {
        var type = currentPayload.Type();
        var currentJson = codecs.CodecFor(type).Encode(currentPayload);
        var oldJson = JsonNode.Parse(currentJson)!.AsObject();
        Assert.True(oldJson.Remove("TurnId"));
        Assert.True(oldJson.Remove("LaneId"));
        return DomainEvent.Create(session, type, 1, null, run.RunId, run.RunId, null, null, null,
            null, null, Array.Empty<ArtifactRef>(), oldJson.ToJsonString(), source: "legacy-v1-fixture");
    }

    private static void Release(SqliteEventStore store)
    {
        var connection = Assert.IsType<SqliteConnection>(store.Connection);
        store.Close();
        SqliteConnection.ClearPool(connection);
        connection.Dispose();
    }
}
