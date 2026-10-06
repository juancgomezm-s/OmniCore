using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Xunit;

namespace OmniCore.Tests;

/// <summary>Contract tests for the additive, writer-attributed Source envelope field.</summary>
public sealed class EventSourceEnvelopeTests
{
    private const string EngineWriter = "OmniCore.Engine.EventStream";

    private static string TempJournal() => Path.Combine(
        Path.GetTempPath(), "omnicore-event-source", Guid.NewGuid().ToString("N") + ".db");

    private static ArtifactRef MakeArtifact() => new(
        ArtifactId.New(), ContentHash.Sha256(new string('a', 64)), 23, "application/json",
        ArtifactKind.ModelResponse, Sensitivity.Sensitive, Redacted: true);

    private static DomainEvent NewEvent(SessionId session, string? source, ArtifactRef artifact)
    {
        var run = RunId.New();
        var task = TaskId.New();
        var lane = LaneId.New();
        var turn = TurnId.New();
        var planItem = PlanItemId.New();
        var toolCall = ToolCallId.New();
        var execution = ExecutionId.New();
        return DomainEvent.Create(session, EventType.Of("test.source-envelope"), 7,
            new EventCausation(EventId.New()), run, run, task, lane, turn, planItem, toolCall,
            new[] { artifact }, "{\"marker\":\"payload stays separate\"}", execution, source: source);
    }

    [Fact]
    public void Create_and_Stored_preserve_source_exactly_and_allow_legacy_null()
    {
        var session = SessionId.New();
        var artifact = MakeArtifact();
        const string source = "OmniCore.Engine.EventStream";
        var created = NewEvent(session, source, artifact);

        Assert.Equal(source, created.Source);
        var stored = DomainEvent.Stored(created.EventId, created.SessionId, 9, created.Type,
            created.SchemaVersion, created.Timestamp, created.Causation, created.CorrelationId,
            created.RunId, created.TaskId, created.LaneId, created.TurnId, created.PlanItemId,
            created.ToolCallId, created.ArtifactRefs, created.PayloadJson, created.ExecutionId,
            source: created.Source);
        Assert.Equal(source, stored.Source);
        Assert.Equal(created.EventId, stored.EventId);
        Assert.Equal(created.ExecutionId, stored.ExecutionId);

        var legacy = DomainEvent.Create(session, EventType.Of("test.legacy"), 1,
            null, null, null, null, null, null, null, null, Array.Empty<ArtifactRef>(), "{}");
        Assert.Null(legacy.Source);
        var storedLegacy = DomainEvent.Stored(EventId.New(), session, 1, legacy.Type, 1,
            legacy.Timestamp, null, null, null, null, null, null, null, null,
            Array.Empty<ArtifactRef>(), "{}");
        Assert.Null(storedLegacy.Source);
    }

    [Fact]
    public void Event_stream_uses_only_its_fixed_writer_identity()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session, "source marker must not become provenance");
        var events = store.ReadFrom(session, 1);

        Assert.NotEmpty(events);
        Assert.All(events, evt => Assert.Equal(EngineWriter, evt.Source));
        Assert.DoesNotContain("source marker", events[0].Source!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(run.RunId, events[0].RunId);
    }

    [Fact]
    public void In_memory_store_copies_source_and_the_rest_of_the_envelope()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var artifact = MakeArtifact();
        var created = NewEvent(session, "writer:fixed-identity", artifact);
        store.Append(session, created, DurabilityClass.Standard, CancellationToken.None);

        var persisted = Assert.Single(store.ReadFrom(session, 1));
        Assert.Equal("writer:fixed-identity", persisted.Source);
        Assert.Equal(created.RunId, persisted.RunId);
        Assert.Equal(created.TaskId, persisted.TaskId);
        Assert.Equal(created.LaneId, persisted.LaneId);
        Assert.Equal(created.TurnId, persisted.TurnId);
        Assert.Equal(created.ExecutionId, persisted.ExecutionId);
        Assert.Equal(created.Timestamp, persisted.Timestamp);
        Assert.Equal(TimeSpan.Zero, persisted.Timestamp.Offset);
        Assert.Equal(new[] { artifact }, persisted.ArtifactRefs);
    }

    [Fact]
    public void Sqlite_round_trip_preserves_source_scope_timestamp_and_artifact_refs()
    {
        var path = TempJournal();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var session = SessionId.New();
        var artifact = MakeArtifact();
        var created = NewEvent(session, "writer:sqlite-round-trip", artifact);
        var store = new SqliteEventStore(path);
        try
        {
            store.Append(session, created, DurabilityClass.Standard, CancellationToken.None);
        }
        finally
        {
            Close(store);
        }

        var reopened = new SqliteEventStore(path);
        try
        {
            var persisted = Assert.Single(reopened.ReadFrom(session, 1));
            Assert.Equal("writer:sqlite-round-trip", persisted.Source);
            Assert.Equal(created.RunId, persisted.RunId);
            Assert.Equal(created.TaskId, persisted.TaskId);
            Assert.Equal(created.LaneId, persisted.LaneId);
            Assert.Equal(created.TurnId, persisted.TurnId);
            Assert.Equal(created.ExecutionId, persisted.ExecutionId);
            Assert.Equal(created.Timestamp, persisted.Timestamp);
            Assert.Equal(TimeSpan.Zero, persisted.Timestamp.Offset);
            Assert.Equal(new[] { artifact }, persisted.ArtifactRefs);
        }
        finally
        {
            Close(reopened);
            TryDelete(path);
        }
    }

    [Fact]
    public void Workspace_read_and_batch_preserve_distinct_sources_without_cross_session_attribution()
    {
        var path = TempJournal();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sqlite = new SqliteEventStore(path);
        try
        {
            foreach (var store in new IEventStore[] { new InMemoryEventStore(), sqlite })
            {
                var first = SessionId.New();
                var second = SessionId.New();
                var one = NewEvent(first, "writer:first", MakeArtifact());
                var legacy = NewEvent(first, null, MakeArtifact());
                var two = NewEvent(second, "writer:second", MakeArtifact());
                store.AppendBatch(first, new[] { one, legacy }, DurabilityClass.Barrier, CancellationToken.None);
                store.Append(second, two, DurabilityClass.Standard, CancellationToken.None);
                var all = ((IWorkspaceJournalReader)store).ReadEvents(one.Type);
                Assert.Equal(3, all.Count);
                var indexed = all.ToDictionary(e => e.EventId);
                foreach (var expected in new[] { one, legacy, two })
                {
                    var actual = indexed[expected.EventId];
                    Assert.Equal(expected.SessionId, actual.SessionId);
                    Assert.Equal(expected.Source, actual.Source);
                    Assert.Equal(expected.ExecutionId, actual.ExecutionId);
                    Assert.Equal(expected.PayloadJson, actual.PayloadJson);
                    Assert.Equal(expected.ArtifactRefs, actual.ArtifactRefs);
                }
                Assert.Equal(new long[] { 1, 2 }, store.ReadFrom(first, 1).Select(e => e.Sequence));
                Assert.Equal(1, Assert.Single(store.ReadFrom(second, 1)).Sequence);
            }
        }
        finally
        {
            Close(sqlite);
            TryDelete(path);
        }
    }

    [Fact]
    public void Legacy_sqlite_schema_migrates_source_as_null_idempotently()
    {
        var path = TempJournal();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var session = SessionId.New();
        CreateLegacyJournal(path, session);

        for (var open = 0; open < 2; open++)
        {
            var store = new SqliteEventStore(path);
            try
            {
                var legacy = Assert.Single(store.ReadFrom(session, 1));
                Assert.Equal(1, legacy.Sequence);
                Assert.Null(legacy.Source);
            }
            finally
            {
                Close(store);
            }
        }

        var verify = new SqliteConnection("Data Source=" + path);
        verify.Open();
        using (var command = verify.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(events)";
            using var reader = command.ExecuteReader();
            var sourceColumns = 0;
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "source", StringComparison.OrdinalIgnoreCase))
                {
                    sourceColumns++;
                    Assert.Equal(0L, reader.GetInt64(3)); // nullable
                }
            }

            Assert.Equal(1, sourceColumns);
        }

        verify.Dispose();
        SqliteConnection.ClearPool(verify);
        TryDelete(path);
    }

    [Fact]
    public void Protocol_source_is_additive_omitted_when_null_and_never_inferred_from_payload()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        TestRun.Open(store, session, "private marker must not be used as Source");
        var events = store.ReadFrom(session, 1);
        var mapper = new ProtocolMapper(EventCodecs.Create());
        var mappedCreated = JsonObj.Parse(Assert.Single(mapper.Map(new[] { events[0] })).PayloadJson);

        Assert.Equal(EngineWriter, mappedCreated["source"]);
        Assert.Equal("1", mappedCreated["seq"]);
        Assert.Equal(session.ToString(), mappedCreated["sessionId"]);
        Assert.Equal("private marker must not be used as Source", mappedCreated["objective"]);
        Assert.DoesNotContain("private marker", mappedCreated["source"], StringComparison.OrdinalIgnoreCase);

        var sourceNull = DomainEvent.Stored(events[0].EventId, events[0].SessionId, events[0].Sequence,
            events[0].Type, events[0].SchemaVersion, events[0].Timestamp, events[0].Causation,
            events[0].CorrelationId, events[0].RunId, events[0].TaskId, events[0].LaneId,
            events[0].TurnId, events[0].PlanItemId, events[0].ToolCallId, events[0].ArtifactRefs,
            events[0].PayloadJson, events[0].ExecutionId, source: null);
        var mappedLegacy = JsonObj.Parse(Assert.Single(mapper.Map(new[] { sourceNull })).PayloadJson);

        Assert.False(mappedLegacy.ContainsKey("source"));
        Assert.Equal("1", mappedLegacy["seq"]);
        Assert.Equal(session.ToString(), mappedLegacy["sessionId"]);
    }

    private static void CreateLegacyJournal(string path, SessionId session)
    {
        using var connection = new SqliteConnection("Data Source=" + path);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                seq INTEGER NOT NULL,
                event_id TEXT NOT NULL,
                event_type TEXT NOT NULL,
                schema_version INTEGER NOT NULL,
                timestamp TEXT NOT NULL,
                causation TEXT, correlation TEXT, run_id TEXT, task_id TEXT, lane_id TEXT,
                turn_id TEXT, plan_item_id TEXT, toolcall_id TEXT, execution_id TEXT,
                payload TEXT NOT NULL, artifacts TEXT NOT NULL
            );
            CREATE UNIQUE INDEX ux_events_session_seq ON events(session_id, seq);
            INSERT INTO events(session_id,seq,event_id,event_type,schema_version,timestamp,payload,artifacts)
            VALUES ($session,1,$eventId,'legacy.source.test',1,'2026-01-02T03:04:05.0000000+00:00','{}','');
            """;
        command.Parameters.AddWithValue("$session", session.ToString());
        command.Parameters.AddWithValue("$eventId", EventId.New().ToString());
        command.ExecuteNonQuery();
        SqliteConnection.ClearPool(connection);
    }

    private static void Close(SqliteEventStore store)
    {
        store.Close();
        SqliteConnection.ClearPool((SqliteConnection)store.Connection);
        store.Connection.Dispose();
    }

    private static void TryDelete(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            if (File.Exists(candidate)) File.Delete(candidate);
        }
    }
}
