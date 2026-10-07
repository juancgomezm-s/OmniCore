using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class ExecutionIdEnvelopeTests
{
    [Fact]
    public async Task Execution_id_scope_nests_restores_and_isolates_async_children()
    {
        var outer = new ExecutionScopeState(ExecutionId: ExecutionId.New());
        var inner = new ExecutionScopeState(ExecutionId: ExecutionId.New());
        using (ExecutionScope.Begin(outer))
        {
            Assert.Equal(outer.ExecutionId, ExecutionScope.Current?.ExecutionId);
            var nested = ExecutionScope.Begin(inner);
            Assert.Equal(inner.ExecutionId, ExecutionScope.Current?.ExecutionId);
            await Task.Yield();
            Assert.Equal(inner.ExecutionId, ExecutionScope.Current?.ExecutionId);
            nested.Dispose();
            nested.Dispose();
            Assert.Equal(outer.ExecutionId, ExecutionScope.Current?.ExecutionId);

            var childEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var child = Task.Run(async () =>
            {
                using var childScope = ExecutionScope.Begin(inner);
                childEntered.SetResult();
                await releaseChild.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal(inner.ExecutionId, ExecutionScope.Current?.ExecutionId);
            }, TestContext.Current.CancellationToken);
            var sibling = Task.Run(async () =>
            {
                await childEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal(outer.ExecutionId, ExecutionScope.Current?.ExecutionId);
            }, TestContext.Current.CancellationToken);
            await sibling;
            releaseChild.SetResult();
            await child;
            Assert.Equal(outer.ExecutionId, ExecutionScope.Current?.ExecutionId);
        }

        Assert.Null(ExecutionScope.Current);
    }

    [Fact]
    public void In_memory_store_preserves_execution_id_payload_priority_scope_fallback_and_null_compatibility()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var stream = new EventStream(store, EventCodecs.Create(), session);
        var scopeExecution = ExecutionId.New();
        var profile = ProfileId.New();
        var run = TestRun.Open(stream, session, agentProfile: profile);
        Assert.All(store.ReadFrom(session, 1), evt => Assert.Null(evt.ExecutionId));

        var payloadExecution = ExecutionId.New();
        using (ExecutionScope.Begin(new ExecutionScopeState(ExecutionId: scopeExecution)))
        {
            stream.Append(new AgentExecutionStarted(payloadExecution, run.RootLane, profile, null,
                ExecutionRelation.Awaited, ExecutionSupervision.Managed));
        }

        var executionEvent = Assert.Single(store.ReadEvents(EventType.Of("agent_execution.started")));
        Assert.Equal(payloadExecution, executionEvent.ExecutionId);
        Assert.Contains(payloadExecution.ToString(), executionEvent.PayloadJson, StringComparison.Ordinal);
        Assert.NotEqual(scopeExecution, executionEvent.ExecutionId);

        var scopedStore = new InMemoryEventStore();
        var scopedSession = SessionId.New();
        RunId scopedRun;
        using (ExecutionScope.Begin(new ExecutionScopeState(ExecutionId: scopeExecution)))
        {
            scopedRun = TestRun.Open(scopedStore, scopedSession).RunId;
        }

        Assert.All(scopedStore.ReadFrom(scopedSession, 1), evt => Assert.Equal(scopeExecution, evt.ExecutionId));
        Assert.NotEqual(run.RunId, scopedRun);
    }

    [Fact]
    public void Sqlite_roundtrip_and_workspace_reader_preserve_execution_id_across_reopen()
    {
        WithJournal((journal, root) =>
        {
            var store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var stream = new EventStream(store, codecs, session);
            var profile = ProfileId.New();
            var run = TestRun.Open(stream, session, agentProfile: profile);
            var expected = ExecutionId.New();
            try
            {
                stream.Append(new AgentExecutionStarted(expected, run.RootLane, profile, null,
                    ExecutionRelation.Detached, ExecutionSupervision.Unmanaged));
            }
            finally
            {
                Release(store);
            }

            store = new SqliteEventStore(journal);
            try
            {
                var fromSession = store.ReadFrom(session, 1);
                Assert.Equal(expected, Assert.Single(fromSession, evt =>
                    evt.Type.ToString() == "agent_execution.started").ExecutionId);
                Assert.Equal(expected, Assert.Single(store.ReadEvents(EventType.Of("agent_execution.started")))
                    .ExecutionId);
                Assert.All(fromSession.Take(7), evt => Assert.Null(evt.ExecutionId));
            }
            finally
            {
                Release(store);
            }
        });
    }

    [Fact]
    public void Legacy_schema_migrates_idempotently_without_rewriting_existing_rows_or_indexes()
    {
        WithJournal((journal, root) =>
        {
            var session = SessionId.New();
            var original = DomainEvent.Create(session, EventType.Of("legacy.event"), 1, null, null, null,
                null, null, null, null, null, Array.Empty<ArtifactRef>(), "{\"legacy\":true}");
            var store = new SqliteEventStore(journal);
            store.Append(session, original, DurabilityClass.Standard, CancellationToken.None);
            Release(store);

            var legacy = new SqliteConnection("DataSource=" + journal);
            try
            {
                legacy.Open();
                Execute(legacy, "ALTER TABLE events DROP COLUMN execution_id");
            }
            finally
            {
                SqliteConnection.ClearPool(legacy);
                legacy.Dispose();
            }

            var preMigration = ReadLegacyFingerprint(journal);
            for (var reopen = 0; reopen < 2; reopen++)
            {
                store = new SqliteEventStore(journal);
                try
                {
                    var read = Assert.Single(store.ReadFrom(session, 1));
                    Assert.Null(read.ExecutionId);
                    Assert.Equal(original.PayloadJson, read.PayloadJson);
                    Assert.Equal(original.EventId, read.EventId);
                }
                finally
                {
                    Release(store);
                }

                Assert.Equal(preMigration.RowId, ReadLegacyFingerprint(journal).RowId);
                Assert.Equal(preMigration.Payload, ReadLegacyFingerprint(journal).Payload);
                Assert.Equal(preMigration.EventId, ReadLegacyFingerprint(journal).EventId);
                Assert.Equal(1, CountColumn(journal, "execution_id"));
                Assert.Equal(preMigration.IndexSql, ReadUniqueIndexSql(journal));
            }
        });
    }

    [Fact]
    public void Concurrent_open_of_legacy_journal_serializes_check_and_column_migration()
    {
        WithJournal((journal, root) =>
        {
            var session = SessionId.New();
            var original = DomainEvent.Create(session, EventType.Of("legacy.concurrent"), 1, null, null, null,
                null, null, null, null, null, Array.Empty<ArtifactRef>(), "{\"preserve\":true}");
            var store = new SqliteEventStore(journal);
            store.Append(session, original, DurabilityClass.Standard, CancellationToken.None);
            Release(store);

            var legacy = new SqliteConnection("DataSource=" + journal);
            try
            {
                legacy.Open();
                Execute(legacy, "ALTER TABLE events DROP COLUMN execution_id");
            }
            finally
            {
                SqliteConnection.ClearPool(legacy);
                legacy.Dispose();
            }

            using var startTogether = new Barrier(2);
            Task<DomainEvent> OpenAndRead() => Task.Run(() =>
            {
                Assert.True(startTogether.SignalAndWait(TimeSpan.FromSeconds(5)));
                var concurrentStore = new SqliteEventStore(journal);
                try
                {
                    return Assert.Single(concurrentStore.ReadFrom(session, 1));
                }
                finally
                {
                    Release(concurrentStore);
                }
            }, TestContext.Current.CancellationToken);

            var results = Task.WhenAll(OpenAndRead(), OpenAndRead()).GetAwaiter().GetResult();
            Assert.All(results, evt =>
            {
                Assert.Null(evt.ExecutionId);
                Assert.Equal(original.EventId, evt.EventId);
                Assert.Equal(original.PayloadJson, evt.PayloadJson);
            });
            Assert.Equal(1, CountColumn(journal, "execution_id"));
        });
    }

    private static void WithJournal(Action<string, string> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-execution-envelope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            body(Path.Combine(root, "journal.db"), root);
        }
        finally
        {
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

    private static int CountColumn(string journal, string name)
    {
        var connection = new SqliteConnection("DataSource=" + journal);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(events)";
            using var reader = command.ExecuteReader();
            var count = 0;
            while (reader.Read())
            {
                if (reader.GetString(1) == name) count++;
            }

            return count;
        }
        finally
        {
            SqliteConnection.ClearPool(connection);
            connection.Dispose();
        }
    }

    private static (long RowId, string Payload, string EventId, string IndexSql) ReadLegacyFingerprint(string journal)
    {
        var connection = new SqliteConnection("DataSource=" + journal);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, payload, event_id FROM events ORDER BY id LIMIT 1";
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            var rowId = reader.GetInt64(0);
            var payload = reader.GetString(1);
            var eventId = reader.GetString(2);
            reader.Close();
            return (rowId, payload, eventId, ReadUniqueIndexSql(connection));
        }
        finally
        {
            SqliteConnection.ClearPool(connection);
            connection.Dispose();
        }
    }

    private static string ReadUniqueIndexSql(string journal)
    {
        var connection = new SqliteConnection("DataSource=" + journal);
        try
        {
            connection.Open();
            return ReadUniqueIndexSql(connection);
        }
        finally
        {
            SqliteConnection.ClearPool(connection);
            connection.Dispose();
        }
    }

    private static string ReadUniqueIndexSql(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name='ux_events_session_seq'";
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
