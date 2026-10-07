using Microsoft.Data.Sqlite;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>Real SQLite rollback and owned temporary state-file faults; no provider invocation.</summary>
public sealed class RunCreationSqliteBoundaryTests
{
    [Fact]
    public void Sqlite_abort_at_RunCreated_rolls_back_all_initial_rows_and_retry_creates_one_complete_run()
    {
        var root = NewRoot();
        var store = new SqliteEventStore(Path.Combine(root, "journal.db"));
        try
        {
            using var command = store.Connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER fixture_reject_run BEFORE INSERT ON events
                WHEN NEW.event_type = 'run.created'
                BEGIN SELECT RAISE(ABORT, 'controlled sqlite initialization failure'); END;
                """;
            command.ExecuteNonQuery();
            var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink());
            var failed = SendAct(server, root);
            Assert.Equal("error", failed.Status);
            Assert.Equal(RuntimeCommandOutcomeKind.Rejected, failed.Outcome?.Kind);
            Assert.Null(failed.FirstSeq);
            Assert.Null(failed.LastSeq);
            Assert.Null(server.LastSessionId());
            Assert.Null(server.LastRunId());
            command.CommandText = "SELECT COUNT(*) FROM events";
            Assert.Equal(0L, command.ExecuteScalar());
            command.CommandText = "PRAGMA synchronous";
            Assert.Equal(1L, command.ExecuteScalar());

            command.CommandText = "DROP TRIGGER fixture_reject_run";
            command.ExecuteNonQuery();
            var successful = SendAct(server, root);
            Assert.Equal("ok", successful.Status);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, successful.Outcome?.Kind);
            var session = Assert.IsType<SessionId>(server.LastSessionId());
            var events = store.ReadFrom(session, 1);
            Assert.Equal(12, events.Count);
            Assert.Single(events, evt => EventCodecs.Create().Decode(evt) is RunModeAuthoritySelected);
            Assert.Equal(Enumerable.Range(1, 12).Select(i => (long)i), events.Select(e => e.Sequence));
            Assert.Equal(1L, successful.FirstSeq);
            Assert.Equal(12L, successful.LastSeq);
            Assert.Equal(2L, store.LastCommitSynchronousLevel);
            Assert.All(events, e =>
            {
                Assert.Equal(TimeSpan.Zero, e.Timestamp.Offset);
                Assert.Equal("OmniCore.Engine.EventStream", e.Source);
                Assert.Equal(new CommandCausation(new CommandId(Guid.Parse(successful.CommandId))), e.Causation);
            });
            CanonicalStateTracker.Replay(EventCodecs.Create(), events);
        }
        finally
        {
            store.Close();
            SqliteConnection.ClearPool((SqliteConnection)store.Connection);
            store.Connection.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void State_file_failure_after_commit_keeps_the_new_run_accepted_not_an_old_session(bool seedPrevious)
    {
        var root = NewRoot();
        try
        {
            var statePath = Path.Combine(root, "session.state");
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), statePath);
            SessionId? previous = null;
            if (seedPrevious)
            {
                var sim = server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"sim\"}"), CancellationToken.None);
                Assert.Equal("ok", sim.Status);
                previous = server.LastSessionId();
                File.Delete(statePath); // owned temporary fixture file, never a user state path
            }
            Directory.CreateDirectory(statePath); // makes the subsequent SaveLastSession fail
            var ack = SendAct(server, root);
            Assert.Equal("error", ack.Status);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, ack.Outcome?.Kind);
            var session = Assert.IsType<SessionId>(server.LastSessionId());
            var run = Assert.IsType<RunId>(server.LastRunId());
            Assert.NotEqual(previous, session);
            var events = store.ReadFrom(session, 1);
            Assert.Equal(12, events.Count);
            Assert.Single(events, evt => codecs.Decode(evt) is RunModeAuthoritySelected);
            Assert.Equal(1L, ack.FirstSeq);
            Assert.Equal(12L, ack.LastSeq);
            Assert.All(events, e => Assert.Equal(
                new CommandCausation(new CommandId(Guid.Parse(ack.CommandId))), e.Causation));
            var created = Assert.Single(events, e => codecs.Decode(e) is RunCreated);
            Assert.Equal(run, created.RunId);
            Assert.Equal(RunState.Running, RunProjection.Replay(session, run, codecs, events).State);
            CanonicalStateTracker.Replay(codecs, events);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static CommandAck SendAct(OmniServer server, string workspace) => server.Send(
        WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"act\",\"objective\":\"inspect without invoking a model\",\"workspace\":"
            + System.Text.Json.JsonSerializer.Serialize(workspace) + "}"), CancellationToken.None);

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-run-atomic-sqlite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
