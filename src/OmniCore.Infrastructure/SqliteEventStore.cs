namespace OmniCore.Infrastructure;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Event Store durable en SQLite (ADR-0002 §1). Esquema: id, session_id, seq, type,
/// schema_version, payload (TEXT JSON), con índice único (session_id, seq). Un solo escritor
/// por sesión asigna secuencias contiguas. La clase de durabilidad Barrier se distingue en el
/// escritor cambiando el pragma synchronous (la conexión de escritura lo alterna alrededor del
/// commit; en M1 se registra como clase sin fsync explícito, ver ADR-0002 §2).
/// </summary>
public sealed class SqliteEventStore : IEventStore
{
    private readonly System.Data.Common.DbConnection _conn;

    public SqliteEventStore(string filePath)
    {
        var connString = "DataSource=" + filePath;
        _conn = Microsoft.Data.Sqlite.SqliteFactory.Instance!.CreateDataSource(connString)!.OpenConnection()!;
        _InitializeSchema();
    }

    private void _InitializeSchema()
    {
        var ddl = _conn.CreateCommand()!;
        ddl.CommandText = """
            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                seq INTEGER NOT NULL,
                event_id TEXT NOT NULL,
                event_type TEXT NOT NULL,
                schema_version INTEGER NOT NULL,
                timestamp TEXT NOT NULL,
                causation TEXT,
                correlation TEXT,
                run_id TEXT,
                task_id TEXT,
                lane_id TEXT,
                turn_id TEXT,
                plan_item_id TEXT,
                toolcall_id TEXT,
                payload TEXT NOT NULL,
                artifacts TEXT NOT NULL
            )
        """;
        ddl.ExecuteNonQuery();

        var idx = _conn.CreateCommand()!;
        idx.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_events_session_seq ON events (session_id, seq)
        """;
        idx.ExecuteNonQuery();
    }

    public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
        CancellationToken cancellationToken)
    {
        var next = CurrentSequence(sessionId) + 1;
        InsertRow(sessionId, evt, next);
    }

    public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events, DurabilityClass durability,
        CancellationToken cancellationToken)
    {
        var next = CurrentSequence(sessionId);
        foreach (var evt in events)
        {
            next += 1;
            InsertRow(sessionId, evt, next);
        }
    }

    public long CurrentSequence(SessionId sessionId)
    {
        var cmd = _conn.CreateCommand()!;
        cmd.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM events WHERE session_id = :sid";
        var p = cmd.CreateParameter()!;
        p.ParameterName = "sid";
        p.Value = sessionId.ToString();
        cmd.Parameters.Add(p);
        var scalar = cmd.ExecuteScalar()!;
        return _AsLong(scalar);
    }

    public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive)
    {
        var cmd = _conn.CreateCommand()!;
        cmd.CommandText = "SELECT event_id, seq, event_type, schema_version, timestamp, causation, correlation, " +
            "run_id, task_id, lane_id, turn_id, plan_item_id, toolcall_id, payload, artifacts " +
            "FROM events WHERE session_id = :sid AND seq >= :from ORDER BY seq";
        var ps = cmd.CreateParameter()!;
        ps.ParameterName = "sid";
        ps.Value = sessionId.ToString();
        cmd.Parameters.Add(ps);
        var pf = cmd.CreateParameter()!;
        pf.ParameterName = "from";
        pf.Value = fromSequenceInclusive;
        cmd.Parameters.Add(pf);

        var result = new List<DomainEvent>();
        var reader = cmd.ExecuteReader()!;
        foreach (System.Data.Common.DbDataRecord row in reader)
        {
            result.Add(ReadRow(sessionId, row));
        }

        return result.ToArray();
    }

    public void PutBlob(string contentHash, string content)
    {
        var cmd = _conn.CreateCommand()!;
        cmd.CommandText = "INSERT OR IGNORE INTO blobs (hash, content) VALUES (:h, :c)";
        var hp = cmd.CreateParameter()!;
        hp.ParameterName = "h";
        hp.Value = contentHash;
        cmd.Parameters.Add(hp);
        var cp = cmd.CreateParameter()!;
        cp.ParameterName = "c";
        cp.Value = content;
        cmd.Parameters.Add(cp);
        cmd.ExecuteNonQuery();
    }

    public string? GetBlob(string contentHash)
    {
        var cmd = _conn.CreateCommand()!;
        cmd.CommandText = "SELECT content FROM blobs WHERE hash = :h";
        var p = cmd.CreateParameter()!;
        p.ParameterName = "h";
        p.Value = contentHash;
        cmd.Parameters.Add(p);
        var scalar = cmd.ExecuteScalar();
        return scalar is null ? null : _AsString(scalar);
    }

    /// <summary>Cantidad de eventos persistidos de una sesión (auditoría previa a la purga).</summary>
    public long CountEvents(SessionId sessionId)
    {
        var cmd = _conn.CreateCommand()!;
        cmd.CommandText = "SELECT COUNT(*) FROM events WHERE session_id = :sid";
        cmd.Parameters.Add(S(cmd, "sid", sessionId.ToString()));
        return _AsLong(cmd.ExecuteScalar()!);
    }

    /// <summary>
    /// Purga de sesión (ADR-0001 §8, <c>omni session purge</c>): borra todos los eventos de la
    /// sesión en una transacción atómica y devuelve la cantidad de filas eliminadas. Los
    /// registros de auditoría sobreviven (ADR-0043) porque viven en otro archivo; quien orquesta
    /// la purga debe escribir la auditoría ANTES de llamar aquí (ver SessionPurger).
    /// </summary>
    public long PurgeSession(SessionId sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var tx = _conn.BeginTransaction();
        var cmd = _conn.CreateCommand()!;
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM events WHERE session_id = :sid";
        cmd.Parameters.Add(S(cmd, "sid", sessionId.ToString()));
        var deleted = (long) cmd.ExecuteNonQuery();
        tx.Commit();
        return deleted;
    }

    public void Close()
    {
        if (_conn is not null)
        {
            _conn.Close();
        }
    }

    private void InsertRow(SessionId sessionId, DomainEvent evt, long sequence)
    {
        var cmd = _conn.CreateCommand()!;
        cmd.CommandText = "INSERT INTO events (session_id, seq, event_id, event_type, schema_version, " +
            "timestamp, causation, correlation, run_id, task_id, lane_id, turn_id, plan_item_id, " +
            "toolcall_id, payload, artifacts) " +
            "VALUES (:sid, :seq, :eid, :etype, :sver, :ts, :caus, :corr, :rid, :tid, :lid, :turn, :piid, :tcid, :payload, :art)";
        cmd.Parameters.Add(S(cmd, "sid", sessionId.ToString()));
        cmd.Parameters.Add(S(cmd, "seq", sequence));
        cmd.Parameters.Add(S(cmd, "eid", evt.EventId.ToString()));
        cmd.Parameters.Add(S(cmd, "etype", evt.Type.ToString()));
        cmd.Parameters.Add(S(cmd, "sver", (long) evt.SchemaVersion));
        cmd.Parameters.Add(S(cmd, "ts", evt.Timestamp.ToString()));
        cmd.Parameters.Add(S(cmd, "caus", evt.Causation is null ? null : Parts.Causation(evt.Causation)));
        cmd.Parameters.Add(S(cmd, "corr", evt.CorrelationId is null ? null : evt.CorrelationId.ToString()));
        cmd.Parameters.Add(S(cmd, "rid", evt.RunId is null ? null : evt.RunId.ToString()));
        cmd.Parameters.Add(S(cmd, "tid", evt.TaskId is null ? null : evt.TaskId.ToString()));
        cmd.Parameters.Add(S(cmd, "lid", evt.LaneId is null ? null : evt.LaneId.ToString()));
        cmd.Parameters.Add(S(cmd, "turn", evt.TurnId is null ? null : evt.TurnId.ToString()));
        cmd.Parameters.Add(S(cmd, "piid", evt.PlanItemId is null ? null : evt.PlanItemId.ToString()));
        cmd.Parameters.Add(S(cmd, "tcid", evt.ToolCallId is null ? null : evt.ToolCallId.ToString()));
        cmd.Parameters.Add(S(cmd, "payload", evt.PayloadJson));
        cmd.Parameters.Add(S(cmd, "art", Parts.Artifacts(evt.ArtifactRefs)));
        cmd.ExecuteNonQuery();
    }

    private static System.Data.Common.DbParameter S(System.Data.Common.DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter()!;
        p.ParameterName = name;
        // El driver SQLite de esta plataforma no bindea Value == null ("Value must be set").
        p.Value = value is null ? "" : value;
        return p;
    }

    private DomainEvent ReadRow(SessionId sessionId, System.Data.Common.DbDataRecord row)
    {
        return DomainEvent.Stored(
            EventId.Parse(_AsString(row.GetValue(0))),
            sessionId,
            _AsLong(row.GetValue(1)),
            EventType.Of(_AsString(row.GetValue(2))),
            (int) _AsLong(row.GetValue(3)),
            DateTimeOffset.Parse(_AsString(row.GetValue(4))),
            Parts.ParseCausation(_AsStringOrNull(row.GetValue(5))),
            Parts.ParseRunId(_AsStringOrNull(row.GetValue(6))),
            Parts.ParseRunId(_AsStringOrNull(row.GetValue(7))),
            Parts.ParseTaskId(_AsStringOrNull(row.GetValue(8))),
            Parts.ParseLaneId(_AsStringOrNull(row.GetValue(9))),
            Parts.ParseTurnId(_AsStringOrNull(row.GetValue(10))),
            Parts.ParsePlanItemId(_AsStringOrNull(row.GetValue(11))),
            Parts.ParseToolCallId(_AsStringOrNull(row.GetValue(12))),
            new ArtifactRef[0],
            _AsString(row.GetValue(13)));
    }

    private static long _AsLong(object value)
    {
        if (value is long l)
        {
            return l;
        }

        if (value is int i)
        {
            return (long) i;
        }

        return long.Parse(_AsString(value));
    }

    private static string _AsString(object value)
    {
        var text = value is null ? null : value.ToString();
        if (text is null)
        {
            throw new InvalidOperationException("Valor NULL en columna inesperada del Event Store");
        }

        return text;
    }

    private static string? _AsStringOrNull(object? value) => value is null ? null : value.ToString();
}

/// <summary>Helpers de codificación de partes del envelope.</summary>
public sealed class Parts
{
    public static string Causation(CausationId causation)
    {
        if (causation is EventCausation e)
        {
            return "event:" + e.EventId.ToString();
        }

        if (causation is CommandCausation c)
        {
            return "command:" + c.CommandId.ToString();
        }

        return "unknown";
    }

    public static CausationId? ParseCausation(string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (text.StartsWith("event:"))
        {
            return new EventCausation(EventId.Parse(text.Substring(6)));
        }

        if (text.StartsWith("command:"))
        {
            return new CommandCausation(CommandId.Parse(text.Substring(8)));
        }

        return null;
    }

    public static string Artifacts(IReadOnlyList<ArtifactRef> refs)
    {
        var parts = new string[refs.Count];
        var i = 0;
        foreach (var r in refs)
        {
            parts[i++] = r.Id.ToString() + "|" + r.Hash.Algorithm + "|" + r.Hash.Value;
        }

        return string.Join(";", parts);
    }

    public static SessionId? ParseSessionId(string? text) => Empty(text) ? null : SessionId.Parse(text!);

    public static RunId? ParseRunId(string? text) => Empty(text) ? null : RunId.Parse(text!);

    public static TaskId? ParseTaskId(string? text) => Empty(text) ? null : TaskId.Parse(text!);

    public static LaneId? ParseLaneId(string? text) => Empty(text) ? null : LaneId.Parse(text!);

    public static TurnId? ParseTurnId(string? text) => Empty(text) ? null : TurnId.Parse(text!);

    public static PlanItemId? ParsePlanItemId(string? text) => Empty(text) ? null : PlanItemId.Parse(text!);

    private static bool Empty(string? text) => text is null || text!.Length == 0;

    public static ToolCallId? ParseToolCallId(string? text) => Empty(text) ? null : ToolCallId.Parse(text!);
}