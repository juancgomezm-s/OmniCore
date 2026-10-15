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
public sealed class SqliteEventStore : IEventStore, IWorkspaceJournalReader, IDisposable
{
    private readonly System.Data.Common.DbConnection _conn;
    private readonly object _connectionSync = new();
    private readonly string _executionColumn = "execution_id";
    private readonly string _sourceColumn = "source";
    private readonly string _journalPath;
    private readonly Dictionary<SessionId, FileStream> _sessionWriterLeases = [];
    private bool _closed;

    /// <summary>Opaque receipt scope: receipts are isolated by this workspace journal database.</summary>
    public string CommandReceiptScope => "workspace-journal-v1";

    public SqliteEventStore(string filePath) : this(filePath, readOnly: false) { }

    internal SqliteEventStore(string filePath, bool readOnly)
    {
        _journalPath = Path.GetFullPath(filePath);
        var connString = readOnly ? new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = _journalPath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString() : "DataSource=" + filePath;
        _conn = Microsoft.Data.Sqlite.SqliteFactory.Instance!.CreateDataSource(connString)!.OpenConnection()!;
        try
        {
            if (!readOnly) _InitializeSchema();
            else
            {
                // Read historical envelopes without migrating another workspace's journal.
                using var columns = _conn.CreateCommand();
                columns.CommandText = "PRAGMA table_info(events)";
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var reader = columns.ExecuteReader();
                while (reader.Read()) names.Add(reader.GetString(1));
                if (!names.Contains("execution_id")) _executionColumn = "NULL";
                if (!names.Contains("source")) _sourceColumn = "NULL";
            }
        }
        catch { _conn.Dispose(); throw; }
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
        using var wal = _conn.CreateCommand()!;
        wal.CommandText = "PRAGMA journal_mode=WAL";
        wal.ExecuteNonQuery();

        using var syncDefault = _conn.CreateCommand()!;
        syncDefault.CommandText = "PRAGMA synchronous=NORMAL";
        syncDefault.ExecuteNonQuery();

        // Otro proceso puede tener el journal abierto (CLI + servidor): se espera al lock en vez de
        // fallar al instante con SQLITE_BUSY.
        using var busy = _conn.CreateCommand()!;
        busy.CommandText = "PRAGMA busy_timeout=5000";
        busy.ExecuteNonQuery();

        using var ddl = _conn.CreateCommand()!;
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
                execution_id TEXT,
                source TEXT,
                payload TEXT NOT NULL,
                artifacts TEXT NOT NULL
            )
        """;
        ddl.ExecuteNonQuery();

        // Additive, idempotent envelope migration. Existing payloads and sequence indexes stay
        // untouched; historical rows naturally read with NULL ExecutionId / Source.
        // BEGIN IMMEDIATE serializes the existence check with ALTER TABLE. A second host opening
        // this legacy journal waits, then observes the migrated schema instead of racing an ALTER.
        using var migration = ((Microsoft.Data.Sqlite.SqliteConnection)_conn).BeginTransaction(deferred: false);
        using var columns = _conn.CreateCommand()!;
        columns.Transaction = migration;
        columns.CommandText = "PRAGMA table_info(events)";
        var hasExecutionId = false;
        var hasSource = false;
        using (var reader = columns.ExecuteReader()!)
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "execution_id", StringComparison.OrdinalIgnoreCase))
                {
                    hasExecutionId = true;
                }
                if (string.Equals(reader.GetString(1), "source", StringComparison.OrdinalIgnoreCase))
                    hasSource = true;
            }
        }

        if (!hasExecutionId)
        {
            using var migrate = _conn.CreateCommand()!;
            migrate.Transaction = migration;
            migrate.CommandText = "ALTER TABLE events ADD COLUMN execution_id TEXT";
            migrate.ExecuteNonQuery();
        }

        if (!hasSource)
        {
            using var migrate = _conn.CreateCommand()!;
            migrate.Transaction = migration;
            migrate.CommandText = "ALTER TABLE events ADD COLUMN source TEXT";
            migrate.ExecuteNonQuery();
        }

        migration.Commit();

        using var receipts = _conn.CreateCommand()!;
        receipts.CommandText = """
            CREATE TABLE IF NOT EXISTS command_receipts (
                scope_id TEXT NOT NULL,
                command_id TEXT NOT NULL,
                request_hash TEXT NOT NULL,
                state INTEGER NOT NULL,
                session_id TEXT,
                ack_status TEXT,
                ack_outcome TEXT,
                ack_reason TEXT,
                first_seq INTEGER,
                last_seq INTEGER,
                PRIMARY KEY (scope_id, command_id),
                CHECK (state IN (0, 1))
            )
        """;
        receipts.ExecuteNonQuery();

        // Conversation execution is a second durable fact, separate from command admission.
        // A missing execution_state after process death is Unknown; it must never authorize resend.
        using (var receiptMigration = ((Microsoft.Data.Sqlite.SqliteConnection)_conn).BeginTransaction(deferred: false))
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var receiptColumns = _conn.CreateCommand()!)
            {
                receiptColumns.Transaction = receiptMigration;
                receiptColumns.CommandText = "PRAGMA table_info(command_receipts)";
                using var reader = receiptColumns.ExecuteReader()!;
                while (reader.Read()) existing.Add(reader.GetString(1));
            }
            foreach (var column in new[] { (Name: "execution_state", Type: "TEXT"),
                         (Name: "execution_code", Type: "TEXT"), (Name: "execution_run_id", Type: "TEXT") })
            {
                if (existing.Contains(column.Name)) continue;
                using var add = _conn.CreateCommand()!;
                add.Transaction = receiptMigration;
                add.CommandText = $"ALTER TABLE command_receipts ADD COLUMN {column.Name} {column.Type}";
                add.ExecuteNonQuery();
            }
            receiptMigration.Commit();
        }

        using var tombstones = _conn.CreateCommand()!;
        tombstones.CommandText = "CREATE TABLE IF NOT EXISTS purged_sessions (session_id TEXT PRIMARY KEY, high_water INTEGER NOT NULL)";
        tombstones.ExecuteNonQuery();

        using var receiptIndex = _conn.CreateCommand()!;
        receiptIndex.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ux_command_receipt_id ON command_receipts (command_id)";
        receiptIndex.ExecuteNonQuery();

        using var idx = _conn.CreateCommand()!;
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
        lock (_connectionSync)
        {
            ThrowIfClosed();
            AcquireSessionWriterLeaseCore(sessionId);
            InsertRowsCore(sessionId, events, durability, cancellationToken);
        }
    }

    private void InsertRowsCore(SessionId sessionId, IReadOnlyList<DomainEvent> events,
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
        using var cmd = _conn.CreateCommand()!;
        cmd.CommandText = "PRAGMA synchronous=" + mode;
        cmd.ExecuteNonQuery();
    }

    public long CurrentSequence(SessionId sessionId) => WithConnection(() => CurrentSequence(sessionId, null));

    private long ReadSynchronousLevel(System.Data.Common.DbTransaction tx)
    {
        using var cmd = _conn.CreateCommand()!;
        cmd.Transaction = tx;
        cmd.CommandText = "PRAGMA synchronous";
        return _AsLong(cmd.ExecuteScalar()!);
    }

    private long CurrentSequence(SessionId sessionId, System.Data.Common.DbTransaction? tx)
    {
        using var cmd = _conn.CreateCommand()!;
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
        => WithConnection(() => ReadFromCore(sessionId, fromSequenceInclusive));

    /// <summary>Reads a bounded replay page against a stable SQLite snapshot.</summary>
    public EventReplayPage ReadPage(SessionId sessionId, long fromSequenceInclusive, int pageSize = 128,
        long? highWaterSnapshot = null)
    {
        if (fromSequenceInclusive < 0) throw new ArgumentOutOfRangeException(nameof(fromSequenceInclusive));
        if (pageSize is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(pageSize));
        return WithConnection(() =>
        {
            using var tx = ((Microsoft.Data.Sqlite.SqliteConnection)_conn).BeginTransaction(deferred: true);
            using var high = _conn.CreateCommand()!;
            high.Transaction = tx;
            high.CommandText = "SELECT MAX(value) FROM (SELECT COALESCE(MAX(seq),0) AS value FROM events WHERE session_id=:sid "
                + "UNION ALL SELECT COALESCE(MAX(high_water),0) FROM purged_sessions WHERE session_id=:sid)";
            high.Parameters.Add(S(high, "sid", sessionId.ToString()));
            var currentHighWater = _AsLong(high.ExecuteScalar()!);
            var highWater = highWaterSnapshot ?? currentHighWater;
            if (highWater < 0 || highWater > currentHighWater)
                throw new ArgumentOutOfRangeException(nameof(highWaterSnapshot));

            var requested = Math.Max(1, fromSequenceInclusive);
            using var first = _conn.CreateCommand()!;
            first.Transaction = tx;
            first.CommandText = "SELECT MIN(seq) FROM events WHERE session_id=:sid AND seq>=:from AND seq<=:high";
            first.Parameters.Add(S(first, "sid", sessionId.ToString()));
            first.Parameters.Add(S(first, "from", requested));
            first.Parameters.Add(S(first, "high", highWater));
            var firstValue = first.ExecuteScalar();
            var firstAvailable = firstValue is null or DBNull ? (long?)null : _AsLong(firstValue);
            using var purged = _conn.CreateCommand()!;
            purged.Transaction = tx;
            purged.CommandText = "SELECT EXISTS(SELECT 1 FROM purged_sessions WHERE session_id=:sid)";
            purged.Parameters.Add(S(purged, "sid", sessionId.ToString()));
            var wasPurged = Convert.ToInt64(purged.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
            var gap = firstAvailable is { } available && requested < available
                || wasPurged && firstAvailable is null && requested <= highWater;

            using var query = _conn.CreateCommand()!;
            query.Transaction = tx;
            query.CommandText = "SELECT event_id, seq, event_type, schema_version, timestamp, causation, correlation, "
                + "run_id, task_id, lane_id, turn_id, plan_item_id, toolcall_id, execution_id, payload, artifacts, source "
                + "FROM events WHERE session_id=:sid AND seq>=:from AND seq<=:high ORDER BY seq LIMIT :limit";
            query.Parameters.Add(S(query, "sid", sessionId.ToString()));
            query.Parameters.Add(S(query, "from", requested));
            query.Parameters.Add(S(query, "high", highWater));
            query.Parameters.Add(S(query, "limit", pageSize));
            var events = new List<DomainEvent>(pageSize);
            using (var reader = query.ExecuteReader()!)
                while (reader.Read()) events.Add(ReadRow(sessionId, reader));
            tx.Commit();
            if (events.Count > 0 && events[0].Sequence != requested) gap = true;
            for (var i = 1; i < events.Count; i++)
                if (events[i].Sequence != events[i - 1].Sequence + 1) { gap = true; break; }
            if (events.Count == 0 && requested <= highWater) gap = true;
            var next = events.Count == 0 ? highWater + 1 : events[^1].Sequence + 1;
            return new EventReplayPage(fromSequenceInclusive, next, highWater, gap, next > highWater, events);
        });
    }

    private IReadOnlyList<DomainEvent> ReadFromCore(SessionId sessionId, long fromSequenceInclusive)
    {
        using var cmd = _conn.CreateCommand()!;
        cmd.CommandText = "SELECT event_id, seq, event_type, schema_version, timestamp, causation, correlation, " +
            "run_id, task_id, lane_id, turn_id, plan_item_id, toolcall_id, execution_id, payload, artifacts, source " +
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
        => WithConnection(() => ReadEventsCore(type));

    internal IReadOnlyList<DomainEvent> ReadEventsSnapshot(IEnumerable<EventType> types)
        => WithConnection(() =>
        {
            using var transaction = ((Microsoft.Data.Sqlite.SqliteConnection)_conn).BeginTransaction(deferred: true);
            var events = types.SelectMany(type => ReadEventsCore(type, transaction)).ToArray();
            transaction.Commit();
            return (IReadOnlyList<DomainEvent>)events;
        });

    private IReadOnlyList<DomainEvent> ReadEventsCore(EventType type,
        System.Data.Common.DbTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        using var cmd = _conn.CreateCommand()!;
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT session_id, event_id, seq, event_type, schema_version, timestamp, " +
            "causation, correlation, run_id, task_id, lane_id, turn_id, plan_item_id, toolcall_id, " + _executionColumn + ", " +
            "payload, artifacts, " + _sourceColumn + " FROM events WHERE event_type = :type ORDER BY id";
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
        => WithConnection(() => CountEventsCore(sessionId));

    public CommandReceiptReservation ReserveCommandReceipt(string commandId, string requestHash, string? scope = null,
        string? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        if (requestHash.Length != 64 || !requestHash.All(Uri.IsHexDigit))
            throw new ArgumentException("A SHA-256 request hash is required.", nameof(requestHash));
        scope ??= CommandReceiptScope;
        lock (_connectionSync)
        {
            ThrowIfClosed();
            SetSynchronous("FULL");
            try
            {
                using var tx = ((Microsoft.Data.Sqlite.SqliteConnection)_conn).BeginTransaction(deferred: false);
                var existing = ReadCommandReceipt(commandId, scope, tx);
                if (existing is not null)
                {
                    tx.Commit();
                    return string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
                        ? existing.State == CommandReceiptState.Completed
                            ? new(CommandReceiptState.Completed, existing)
                            : new(CommandReceiptState.Unknown, existing)
                        : new(CommandReceiptState.IdConflict, existing);
                }

                using var insert = _conn.CreateCommand()!;
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO command_receipts (scope_id, command_id, request_hash, state, session_id) "
                    + "VALUES (:scope, :command, :hash, 0, :sid)";
                insert.Parameters.Add(S(insert, "scope", scope));
                insert.Parameters.Add(S(insert, "command", commandId));
                insert.Parameters.Add(S(insert, "hash", requestHash));
                insert.Parameters.Add(S(insert, "sid", sessionId));
                insert.ExecuteNonQuery();
                tx.Commit();
                return new(CommandReceiptState.Reserved, new(commandId, requestHash,
                    CommandReceiptState.Unknown, null, null, null, null, null, null));
            }
            finally { SetSynchronous("NORMAL"); }
        }
    }

    public void CompleteCommandReceipt(string commandId, string requestHash, string status,
        string? outcomeKind, string? outcomeReason, long? firstSequence, long? lastSequence, string? scope = null,
        string? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        scope ??= CommandReceiptScope;
        lock (_connectionSync)
        {
            ThrowIfClosed();
            SetSynchronous("FULL");
            try
            {
                using var tx = ((Microsoft.Data.Sqlite.SqliteConnection)_conn).BeginTransaction(deferred: false);
                using var update = _conn.CreateCommand()!;
                update.Transaction = tx;
                update.CommandText = "UPDATE command_receipts SET state = 1, session_id = COALESCE(session_id, :sid), ack_status = :status, "
                    + "ack_outcome = :outcome, ack_reason = :reason, first_seq = :first, last_seq = :last "
                    + "WHERE scope_id = :scope AND command_id = :command AND request_hash = :hash AND state = 0";
                update.Parameters.Add(S(update, "status", status));
                update.Parameters.Add(S(update, "sid", sessionId));
                update.Parameters.Add(S(update, "outcome", outcomeKind));
                update.Parameters.Add(S(update, "reason", outcomeReason is { Length: > 0 and <= 96 }
                    && outcomeReason.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
                    ? outcomeReason : null));
                update.Parameters.Add(S(update, "first", firstSequence));
                update.Parameters.Add(S(update, "last", lastSequence));
                update.Parameters.Add(S(update, "scope", scope));
                update.Parameters.Add(S(update, "command", commandId));
                update.Parameters.Add(S(update, "hash", requestHash));
                var changed = update.ExecuteNonQuery();
                if (changed == 0 && ReadCommandReceipt(commandId, scope, tx) is not { State: CommandReceiptState.Completed })
                    throw new InvalidOperationException("Command receipt reservation was not found.");
                tx.Commit();
            }
            finally { SetSynchronous("NORMAL"); }
        }
    }

    public CommandReceipt? ReadCommandReceipt(string commandId, string? scope = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        scope ??= CommandReceiptScope;
        return WithConnection(() => ReadCommandReceipt(commandId, scope, null));
    }

    /// <summary>Persists a redacted terminal fact for an admitted conversation command.</summary>
    public void CompleteCommandExecution(string commandId, string requestHash, string state, string code,
        string sessionId, string runId, string? scope = null)
    {
        if (state is not ("completed" or "failed" or "cancelled" or "deferred")) throw new ArgumentOutOfRangeException(nameof(state));
        if (code.Length is 0 or > 96 || !code.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
            throw new ArgumentException("Execution code must be a short canonical code.", nameof(code));
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        scope ??= CommandReceiptScope;
        WithConnection(() =>
        {
            using var tx = ((Microsoft.Data.Sqlite.SqliteConnection)_conn).BeginTransaction(deferred: false);
            using var update = _conn.CreateCommand()!;
            update.Transaction = tx;
            update.CommandText = "UPDATE command_receipts SET execution_state=:state, execution_code=:code, execution_run_id=:run "
                + "WHERE scope_id=:scope AND command_id=:command AND request_hash=:hash AND state=1 "
                + "AND session_id=:session AND execution_state IS NULL";
            update.Parameters.Add(S(update, "state", state));
            update.Parameters.Add(S(update, "code", code));
            update.Parameters.Add(S(update, "run", runId));
            update.Parameters.Add(S(update, "session", sessionId));
            update.Parameters.Add(S(update, "scope", scope));
            update.Parameters.Add(S(update, "command", commandId));
            update.Parameters.Add(S(update, "hash", requestHash));
            var changed = update.ExecuteNonQuery();
            if (changed == 0)
            {
                var existing = ReadCommandReceipt(commandId, scope, tx);
                if (existing is not { State: CommandReceiptState.Completed }
                    || existing.RequestHash != requestHash || existing.SessionId != sessionId
                    || existing.ExecutionRunId != runId || existing.ExecutionState != state || existing.ExecutionCode != code)
                    throw new InvalidOperationException("Conversation execution receipt conflicts with its admission.");
            }
            tx.Commit();
            return true;
        });
    }

    private CommandReceipt? ReadCommandReceipt(string commandId, string scope, System.Data.Common.DbTransaction? tx)
    {
        using var query = _conn.CreateCommand()!;
        query.Transaction = tx;
        query.CommandText = "SELECT request_hash, state, session_id, ack_status, ack_outcome, ack_reason, first_seq, last_seq, "
            + "execution_state, execution_code, execution_run_id "
            + "FROM command_receipts WHERE scope_id = :scope AND command_id = :command";
        query.Parameters.Add(S(query, "scope", scope));
        query.Parameters.Add(S(query, "command", commandId));
        using var reader = query.ExecuteReader()!;
        if (!reader.Read()) return null;
        var completed = _AsLong(reader.GetValue(1)) == 1;
        return new CommandReceipt(commandId, reader.GetString(0), completed
            ? CommandReceiptState.Completed : CommandReceiptState.Unknown,
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : _AsLong(reader.GetValue(6)),
            reader.IsDBNull(7) ? null : _AsLong(reader.GetValue(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    private long CountEventsCore(SessionId sessionId)
    {
        using var cmd = _conn.CreateCommand()!;
        cmd.CommandText = "SELECT COUNT(*) FROM events WHERE session_id = :sid";
        cmd.Parameters.Add(S(cmd, "sid", sessionId.ToString()));
        return _AsLong(cmd.ExecuteScalar()!);
    }

    /// <summary>Elimina atómicamente los eventos de una sesión después de auditar la purga.</summary>
    public long PurgeSession(SessionId sessionId, CancellationToken cancellationToken)
        => WithConnection(() =>
        {
            AcquireSessionWriterLeaseCore(sessionId);
            return PurgeSessionCore(sessionId, cancellationToken);
        });

    private long PurgeSessionCore(SessionId sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var tx = _conn.BeginTransaction();
        using (var tombstone = _conn.CreateCommand()!)
        {
            tombstone.Transaction = tx;
            tombstone.CommandText = "INSERT INTO purged_sessions(session_id, high_water) "
                + "VALUES(:sid, COALESCE((SELECT MAX(seq) FROM events WHERE session_id=:sid),0)) "
                + "ON CONFLICT(session_id) DO UPDATE SET high_water=MAX(high_water, excluded.high_water)";
            tombstone.Parameters.Add(S(tombstone, "sid", sessionId.ToString()));
            tombstone.ExecuteNonQuery();
        }
        using var cmd = _conn.CreateCommand()!;
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM events WHERE session_id = :sid";
        cmd.Parameters.Add(S(cmd, "sid", sessionId.ToString()));
        var deleted = (long)cmd.ExecuteNonQuery();
        tx.Commit();
        return deleted;
    }

    public void Close()
    {
        lock (_connectionSync)
        {
            if (_closed) return;
            _closed = true;
            try { CloseCore(); }
            finally
            {
                foreach (var lease in _sessionWriterLeases.Values) lease.Dispose();
                _sessionWriterLeases.Clear();
            }
        }
    }

    public void Dispose() => Close();

    /// <summary>
    /// Acquires a non-expiring OS lock for one Session and retains its open handle until Close.
    /// The lock file is intentionally permanent: deleting it while another process has it open
    /// could create two independently locked inodes and split-brain ownership.
    /// </summary>
    /// <returns>True when this call acquired a new handle; false when this store already owned it.</returns>
    public bool AcquireSessionWriterLease(SessionId sessionId)
    {
        lock (_connectionSync)
        {
            ThrowIfClosed();
            return AcquireSessionWriterLeaseCore(sessionId);
        }
    }

    /// <summary>Releases only this store's lease; the permanent lock path is never removed.</summary>
    public void ReleaseSessionWriterLease(SessionId sessionId)
    {
        lock (_connectionSync)
        {
            if (_sessionWriterLeases.Remove(sessionId, out var lease)) lease.Dispose();
        }
    }

    private bool AcquireSessionWriterLeaseCore(SessionId sessionId)
    {
        if (_sessionWriterLeases.ContainsKey(sessionId)) return false;
        var directory = _journalPath + ".session-writers";
        Directory.CreateDirectory(directory);
        var leasePath = Path.Combine(directory, sessionId + ".lock");
        try
        {
            _sessionWriterLeases.Add(sessionId, new FileStream(leasePath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough));
            return true;
        }
        catch (IOException)
        {
            throw new SessionWriterLeaseUnavailableException(sessionId);
        }
    }

    private void ThrowIfClosed()
    {
        if (_closed) throw new ObjectDisposedException(nameof(SqliteEventStore));
    }

    private void CloseCore()
    {
        if (_conn is not null)
        {
            _conn.Close();
        }
    }

    // A background model turn and the TUI poll share this connection. Hold the lock
    // through reader materialization / the entire transaction, never over provider I/O.
    private T WithConnection<T>(Func<T> operation)
    {
        lock (_connectionSync) return operation();
    }

    private void InsertRow(SessionId sessionId, DomainEvent evt, long sequence, System.Data.Common.DbTransaction tx)
    {
        using var cmd = _conn.CreateCommand()!;
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO events (session_id, seq, event_id, event_type, schema_version, " +
            "timestamp, causation, correlation, run_id, task_id, lane_id, turn_id, plan_item_id, " +
            "toolcall_id, execution_id, payload, artifacts, source) " +
            "VALUES (:sid, :seq, :eid, :etype, :sver, :ts, :caus, :corr, :rid, :tid, :lid, :turn, :piid, :tcid, :xid, :payload, :art, :source)";
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
        cmd.Parameters.Add(S(cmd, "xid", evt.ExecutionId is null ? null : evt.ExecutionId.ToString()));
        cmd.Parameters.Add(S(cmd, "payload", evt.PayloadJson));
        cmd.Parameters.Add(S(cmd, "art", Parts.Artifacts(evt.ArtifactRefs)));
        cmd.Parameters.Add(S(cmd, "source", evt.Source));
        cmd.ExecuteNonQuery();
    }

    private static System.Data.Common.DbParameter S(System.Data.Common.DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter()!;
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        return p;
    }

    private DomainEvent ReadRow(SessionId sessionId, System.Data.IDataRecord row, int offset = 0)
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
            Parts.ParseArtifacts(_AsStringOrNull(row.GetValue(offset + 15))),
            _AsString(row.GetValue(offset + 14)),
            Parts.ParseExecutionId(_AsStringOrNull(row.GetValue(offset + 13))),
            row.IsDBNull(offset + 16) ? null : _AsString(row.GetValue(offset + 16)));
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

    public static ExecutionId? ParseExecutionId(string? text) => Empty(text) ? null : ExecutionId.Parse(text!);
}
