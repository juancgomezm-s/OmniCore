namespace OmniCore.Infrastructure;

using System.Globalization;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Event Store durable en SQLite (ADR-0002 §1). Esquema: id, session_id, seq, type,
/// schema_version, payload (TEXT JSON), con índice único (session_id, seq). Un solo escritor
/// por sesión asigna secuencias contiguas. La clase de durabilidad Barrier se distingue en el
/// escritor alternando el pragma synchronous alrededor de ese commit (ADR-0002 §2): la conexión
/// usa WAL + synchronous=NORMAL por defecto y un commit Barrier la conmuta a synchronous=FULL
/// mientras se comete la transacción, restaurando NORMAL en finally. Un error nunca deja la
/// conexión en FULL. Standard conserva NORMAL.
/// </summary>
public sealed class SqliteEventStore : IEventStore, IWorkspaceJournalReader
{
    private readonly System.Data.Common.DbConnection _conn;

    public SqliteEventStore(string filePath)
    {
        var connString = "DataSource=" + filePath;
        _conn = Microsoft.Data.Sqlite.SqliteFactory.Instance!.CreateDataSource(connString)!.OpenConnection()!;
        _InitializeSchema();
    }

    /// <summary>
    /// Conexión del escritor, expuesta para diagnóstico y tests del driver (p. ej. leer el
    /// PRAGMA synchronous de la conexión). No es parte del contrato <see cref="IEventStore"/>.
    /// </summary>
    internal System.Data.Common.DbConnection Connection => _conn;

    private void _InitializeSchema()
    {
        // WAL + synchronous=NORMAL por defecto (ADR-0002 §2). journal_mode se aplica a la base,
        // synchronous es por conexión y se alterna alrededor de un commit Barrier.
        var wal = _conn.CreateCommand()!;
        wal.CommandText = "PRAGMA journal_mode=WAL";
        wal.ExecuteNonQuery();

        var syncDefault = _conn.CreateCommand()!;
        syncDefault.CommandText = "PRAGMA synchronous=NORMAL";
        syncDefault.ExecuteNonQuery();

        // Otro proceso puede tener el journal abierto (CLI + servidor): se espera al lock en vez de
        // fallar al instante con SQLITE_BUSY.
        var busy = _conn.CreateCommand()!;
        busy.CommandText = "PRAGMA busy_timeout=5000";
        busy.ExecuteNonQuery();

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
        CancellationToken cancellationToken) =>
        InsertRows(sessionId, new[] { evt }, durability, cancellationToken);

    public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events, DurabilityClass durability,
        CancellationToken cancellationToken) =>
        InsertRows(sessionId, events, durability, cancellationToken);

    /// <summary>
    /// Nivel de <c>PRAGMA synchronous</c> con el que se confirmó el último commit (0 OFF, 1 NORMAL,
    /// 2 FULL, 3 EXTRA). Permite a los tests comprobar que un Barrier se confirma de verdad en FULL.
    /// </summary>
    internal long LastCommitSynchronousLevel { get; private set; } = -1;

    /// <summary>
    /// Persiste un lote en una sola transacción atómica. Para un commit Barrier conmuta la
    /// conexión a synchronous=FULL antes de la transacción y la restaura a NORMAL en finally,
    /// asegurando que el WAL quede sincronizado a disco antes de continuar (ADR-0002 §2). Un fallo
    /// en cualquier fila revierte el lote entero y restaura synchronous=NORMAL: la conexión nunca
    /// queda en FULL.
    /// </summary>
    private void InsertRows(SessionId sessionId, IReadOnlyList<DomainEvent> events,
        DurabilityClass durability, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        cancellationToken.ThrowIfCancellationRequested();
        if (events.Count == 0)
        {
            return;
        }

        var barrier = durability == DurabilityClass.Barrier;
        try
        {
            if (barrier)
            {
                SetSynchronous("FULL");
            }

            // BEGIN IMMEDIATE: el lock de escritura se toma ANTES de leer la última secuencia, así
            // dos escritores (dos procesos sobre el mismo journal) nunca calculan el mismo número.
            using var tx = _conn.BeginTransaction();
            var seq = CurrentSequence(sessionId, tx);
            foreach (var evt in events)
            {
                seq += 1;
                InsertRow(sessionId, evt, seq, tx);
            }

            LastCommitSynchronousLevel = ReadSynchronousLevel(tx);
            tx.Commit();
        }
        finally
        {
            if (barrier)
            {
                SetSynchronous("NORMAL");
            }
        }
    }

    private void SetSynchronous(string mode)
    {
        var cmd = _conn.CreateCommand()!;
        cmd.CommandText = "PRAGMA synchronous=" + mode;
        cmd.ExecuteNonQuery();
    }

    public long CurrentSequence(SessionId sessionId) => CurrentSequence(sessionId, null);

    private long ReadSynchronousLevel(System.Data.Common.DbTransaction tx)
    {
        var cmd = _conn.CreateCommand()!;
        cmd.Transaction = tx;
        cmd.CommandText = "PRAGMA synchronous";
        return _AsLong(cmd.ExecuteScalar()!);
    }

    private long CurrentSequence(SessionId sessionId, System.Data.Common.DbTransaction? tx)
    {
        var cmd = _conn.CreateCommand()!;
        cmd.Transaction = tx;
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
        using var reader = cmd.ExecuteReader()!;
        foreach (System.Data.Common.DbDataRecord row in reader)
        {
            result.Add(ReadRow(sessionId, row));
        }

        return result.ToArray();
    }

    /// <summary>Reads one event type across all sessions in this workspace journal.</summary>
    public IReadOnlyList<DomainEvent> ReadEvents(EventType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var cmd = _conn.CreateCommand()!;
        cmd.CommandText = "SELECT session_id, event_id, seq, event_type, schema_version, timestamp, " +
            "causation, correlation, run_id, task_id, lane_id, turn_id, plan_item_id, toolcall_id, " +
            "payload, artifacts FROM events WHERE event_type = :type ORDER BY id";
        cmd.Parameters.Add(S(cmd, "type", type.ToString()));

        var result = new List<DomainEvent>();
        using var reader = cmd.ExecuteReader()!;
        foreach (System.Data.Common.DbDataRecord row in reader)
        {
            result.Add(ReadRow(SessionId.Parse(_AsString(row.GetValue(0))), row, 1));
        }

        return result.ToArray();
    }

    /// <summary>Cantidad de eventos persistidos de una sesión (auditoría previa a la purga).</summary>
    public long CountEvents(SessionId sessionId)
    {
        var cmd = _conn.CreateCommand()!;
        cmd.CommandText = "SELECT COUNT(*) FROM events WHERE session_id = :sid";
        cmd.Parameters.Add(S(cmd, "sid", sessionId.ToString()));
        return _AsLong(cmd.ExecuteScalar()!);
    }

    /// <summary>Elimina atómicamente los eventos de una sesión después de auditar la purga.</summary>
    public long PurgeSession(SessionId sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var tx = _conn.BeginTransaction();
        var cmd = _conn.CreateCommand()!;
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM events WHERE session_id = :sid";
        cmd.Parameters.Add(S(cmd, "sid", sessionId.ToString()));
        var deleted = (long)cmd.ExecuteNonQuery();
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

    private void InsertRow(SessionId sessionId, DomainEvent evt, long sequence, System.Data.Common.DbTransaction tx)
    {
        var cmd = _conn.CreateCommand()!;
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO events (session_id, seq, event_id, event_type, schema_version, " +
            "timestamp, causation, correlation, run_id, task_id, lane_id, turn_id, plan_item_id, " +
            "toolcall_id, payload, artifacts) " +
            "VALUES (:sid, :seq, :eid, :etype, :sver, :ts, :caus, :corr, :rid, :tid, :lid, :turn, :piid, :tcid, :payload, :art)";
        cmd.Parameters.Add(S(cmd, "sid", sessionId.ToString()));
        cmd.Parameters.Add(S(cmd, "seq", sequence));
        cmd.Parameters.Add(S(cmd, "eid", evt.EventId.ToString()));
        cmd.Parameters.Add(S(cmd, "etype", evt.Type.ToString()));
        cmd.Parameters.Add(S(cmd, "sver", (long) evt.SchemaVersion));
        // ISO 8601 invariante: el texto no depende de la cultura del sistema y reproduce el instante exacto.
        cmd.Parameters.Add(S(cmd, "ts", evt.Timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
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
        p.Value = value ?? DBNull.Value;
        return p;
    }

    private DomainEvent ReadRow(SessionId sessionId, System.Data.Common.DbDataRecord row, int offset = 0)
    {
        return DomainEvent.Stored(
            EventId.Parse(_AsString(row.GetValue(offset))),
            sessionId,
            _AsLong(row.GetValue(offset + 1)),
            EventType.Of(_AsString(row.GetValue(offset + 2))),
            (int) _AsLong(row.GetValue(offset + 3)),
            ParseTimestamp(_AsString(row.GetValue(offset + 4))),
            Parts.ParseCausation(_AsStringOrNull(row.GetValue(offset + 5))),
            Parts.ParseRunId(_AsStringOrNull(row.GetValue(offset + 6))),
            Parts.ParseRunId(_AsStringOrNull(row.GetValue(offset + 7))),
            Parts.ParseTaskId(_AsStringOrNull(row.GetValue(offset + 8))),
            Parts.ParseLaneId(_AsStringOrNull(row.GetValue(offset + 9))),
            Parts.ParseTurnId(_AsStringOrNull(row.GetValue(offset + 10))),
            Parts.ParsePlanItemId(_AsStringOrNull(row.GetValue(offset + 11))),
            Parts.ParseToolCallId(_AsStringOrNull(row.GetValue(offset + 12))),
            Parts.ParseArtifacts(_AsStringOrNull(row.GetValue(offset + 14))),
            _AsString(row.GetValue(offset + 13)));
    }

    /// <summary>
    /// Marca de tiempo persistida: ISO 8601 invariante; los journals anteriores la guardaban con el
    /// formato de la cultura del sistema, que se acepta como respaldo.
    /// </summary>
    private static DateTimeOffset ParseTimestamp(string text) =>
        DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var value)
            ? value
            : DateTimeOffset.Parse(text, System.Globalization.CultureInfo.CurrentCulture);

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

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("|", "\\|").Replace(";", "\\;");

    private static string Unescape(string value) => value.Replace("\\;", ";").Replace("\\|", "|").Replace("\\\\", "\\");

    public static string Artifacts(IReadOnlyList<ArtifactRef> refs)
    {
        var parts = new string[refs.Count];
        var i = 0;
        foreach (var r in refs)
        {
            parts[i++] = r.Id.ToString() + "|" + r.Hash.Algorithm + "|" + r.Hash.Value + "|" +
                r.Size + "|" + Escape(r.MediaType) + "|" + r.Kind + "|" + r.Sensitivity + "|" + (r.Redacted ? "1" : "0");
        }

        return string.Join(";", parts);
    }

    public static IReadOnlyList<ArtifactRef> ParseArtifacts(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<ArtifactRef>();
        }

        var refStrings = SplitUnescaped(text, ';');
        var result = new ArtifactRef[refStrings.Count];
        for (var i = 0; i < refStrings.Count; i++)
        {
            var fields = SplitUnescaped(refStrings[i], '|');
            if (fields.Count == 3)
            {
                // Persisted format before full-ref round-tripping: Id|Algorithm|Value.
                // Missing metadata is not derivable from the journal, so use neutral defaults.
                result[i] = new ArtifactRef(
                    ArtifactId.Parse(fields[0]),
                    new ContentHash(fields[1], fields[2]),
                    0,
                    "",
                    ArtifactKind.Other,
                    Sensitivity.Normal,
                    false);
                continue;
            }

            if (fields.Count != 8)
            {
                throw new InvalidOperationException("Formato de artifact ref inválido: " + refStrings[i]);
            }

            // M55: el escritor emite exactamente "1"/"0" para el flag redacted (ver Artifacts). Un
            // token distinto indica una fila corrupta; aceptarlo en silencio como false ocultaría
            // la corrupción, así que se rechaza con el mismo error de formato inválido.
            if (fields[7] != "0" && fields[7] != "1")
            {
                throw new InvalidOperationException("Formato de artifact ref inválido: " + refStrings[i]);
            }

            // Full refs must use the writer's canonical metadata representation.
            if (!long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                || size < 0
                || !Enum.TryParse<ArtifactKind>(fields[5], out var kind)
                || !Enum.IsDefined(kind) || kind.ToString() != fields[5]
                || !Enum.TryParse<Sensitivity>(fields[6], out var sensitivity)
                || !Enum.IsDefined(sensitivity) || sensitivity.ToString() != fields[6])
            {
                throw new InvalidOperationException("Formato de artifact ref inválido: " + refStrings[i]);
            }

            result[i] = new ArtifactRef(
                ArtifactId.Parse(fields[0]),
                new ContentHash(fields[1], fields[2]),
                size,
                Unescape(fields[4]),
                kind,
                sensitivity,
                fields[7] == "1");
        }

        return result;
    }

    private static List<string> SplitUnescaped(string text, char delimiter)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                // Preserve the escape for the next parsing layer. ArtifactRefs are
                // split first by ';' and then by '|'; unescaping here would expose
                // an escaped '|' (or '\\') to the second split.
                current.Append(c);
                current.Append(text[++i]);
            }
            else if (c == delimiter)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result;
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
