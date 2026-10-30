namespace OmniCore.Memory;

using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// SQLite-backed Project, Workspace, and User memory. This store only persists records;
/// authorization, trust checks, promotion policy, and context selection remain Host duties.
/// Each mutation uses a SQLite immediate transaction so independent processes serialize writes.
/// </summary>
public sealed partial class ScopedMemoryStore : IMemoryStore
{
    private const int MaximumRecordsPerPartition = 500;
    private const long MaximumPayloadBytesPerPartition = 4 * 1024 * 1024;
    private const string Table = "scoped_memory_records";
    private const string OperationsTable = "scoped_memory_operations";
    private const int MaximumOperationsPerPartition = 10_000;
    private readonly string _dataDirectory;

    public ScopedMemoryStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
    }

    public IReadOnlyList<MemoryRecord> Read(MemoryScope scope, CancellationToken cancellationToken)
    {
        var path = PathFor(scope);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return [];
        RejectReparsePointAncestors(path);
        using var connection = Open(path, create: false);
        if (!HasTable(connection)) return [];
        return ReadPartition(connection, null, scope, cancellationToken)
            .OrderByDescending(item => item.Importance * item.Confidence)
            .ThenByDescending(item => item.CreatedAt).ToArray();
    }

    public MemoryRecord Add(MemoryRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.Validate();
        var path = PathFor(record.Scope);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open(path, create: true);
        EnsureTable(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        var records = ReadPartition(connection, transaction, record.Scope, cancellationToken);
        var added = AddWithinTransaction(connection, transaction, record.Scope, record, records, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return added;
    }

    /// <summary>Returns a stable hash of the partition, excluding LastUsedAt so a context read in
    /// another turn does not invalidate an outstanding human promotion prompt.</summary>
    public string Revision(MemoryScope scope, CancellationToken cancellationToken)
        => Snapshot(scope, cancellationToken).Revision;

    /// <summary>Reads a scope and computes its CAS revision from the same SQLite snapshot.</summary>
    public (IReadOnlyList<MemoryRecord> Records, string Revision) Snapshot(MemoryScope scope,
        CancellationToken cancellationToken)
    {
        var path = PathFor(scope);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return ([], RevisionOf([]));
        using var connection = Open(path, create: false);
        if (!HasTable(connection)) return ([], RevisionOf([]));
        using var transaction = connection.BeginTransaction(deferred: true);
        var records = ReadPartition(connection, transaction, scope, cancellationToken);
        var revision = RevisionOf(records);
        transaction.Commit();
        return (records, revision);
    }

    /// <summary>Applies a journal-prepared scoped mutation exactly once. The operation ledger and
    /// record mutation share one SQLite transaction; a retry with the same operation id cannot
    /// resurrect a later-forgotten record.</summary>
    public ScopedMemoryMutationResult ApplyPrepared(ScopedMemoryMutationPrepared prepared,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ValidatePrepared(prepared);
        var path = PathFor(prepared.Scope);
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open(path, create: true);
        EnsureTable(connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        var requestHash = RequestHash(prepared);
        var prior = ReadOperation(connection, transaction, prepared.Scope, prepared.OperationId);
        if (prior is not null)
        {
            if (!string.Equals(prior.Value.RequestHash, requestHash, StringComparison.Ordinal))
                throw new InvalidDataException("A scoped memory operation id was reused with different content.");
            transaction.Commit();
            return new ScopedMemoryMutationResult(prior.Value.MemoryId, prior.Value.Removed);
        }

        var operationsCount = CountOperations(connection, transaction, prepared.Scope);
        // Reserve room to forget all live records. Legacy full ledgers get at most 500
        // additional forget receipts; they never prevent a user from erasing live memory.
        var records = ReadPartition(connection, transaction, prepared.Scope, cancellationToken);
        if (prepared.Operation == "promote"
            ? operationsCount + records.Count + 2 > MaximumOperationsPerPartition
            : operationsCount >= MaximumOperationsPerPartition + MaximumRecordsPerPartition)
            throw new InvalidOperationException("client.memory.capacity_reached");
        if (prepared.Operation == "promote" && prepared.Record!.Tags.Contains("cross-session-evidence")
            && !ReadEvidence(connection, transaction, prepared.Scope, prepared.Record.Kind, prepared.Record.Content)
                .Matches(prepared.Record.EvidenceRefs))
            throw new InvalidOperationException("sessionPreferences.error.memory_changed");
        if (!string.Equals(RevisionOf(records), prepared.ExpectedRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("sessionPreferences.error.memory_changed");

        ScopedMemoryMutationResult result;
        try
        {
            result = prepared.Operation switch
            {
                "promote" => new ScopedMemoryMutationResult(
                    AddWithinTransaction(connection, transaction, prepared.Scope, prepared.Record!, records,
                        cancellationToken).Id, false),
                "forget" => ApplyForget(connection, transaction, prepared.Scope, prepared.MemoryId, records),
                _ => throw new InvalidDataException("The scoped memory operation is unsupported."),
            };
        }
        catch (InvalidDataException exception) when (IsCapacityFailure(exception))
        {
            throw new InvalidOperationException("client.memory.capacity_reached", exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        WriteOperation(connection, transaction, prepared.Scope, prepared.OperationId,
            requestHash, result);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return result;
    }

    /// <summary>Read-only durable result lookup. Recovery can acknowledge an applied promotion
    /// even when its source evidence was subsequently forgotten, without applying it again.</summary>
    public ScopedMemoryMutationResult? ReadPreparedResult(ScopedMemoryMutationPrepared prepared, CancellationToken token)
    {
        ValidatePrepared(prepared); token.ThrowIfCancellationRequested(); var path = PathFor(prepared.Scope);
        if (!File.Exists(path)) return null;
        using var connection = Open(path, create: false);
        if (!HasTable(connection, OperationsTable)) return null;
        using var transaction = connection.BeginTransaction(deferred: true);
        var prior = ReadOperation(connection, transaction, prepared.Scope, prepared.OperationId);
        if (prior is null) { transaction.Commit(); return null; }
        if (prior.Value.RequestHash != RequestHash(prepared)) throw new InvalidDataException("Memory receipt payload mismatch.");
        transaction.Commit(); return new(prior.Value.MemoryId, prior.Value.Removed);
    }

    public bool Remove(MemoryScope scope, string memoryId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryId);
        var path = PathFor(scope);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return false;
        RejectReparsePointAncestors(path);
        using var connection = Open(path, create: false);
        if (!HasTable(connection)) return false;
        using var transaction = connection.BeginTransaction(deferred: false);
        var records = ReadPartition(connection, transaction, scope, cancellationToken);
        var target = records.FirstOrDefault(item => string.Equals(item.Id, memoryId, StringComparison.Ordinal));
        if (target is null)
        {
            transaction.Commit();
            return false;
        }
        RemoveWithinTransaction(connection, transaction, scope, target, records);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Removes within an immediate transaction and invokes the receipt publisher before commit.
    /// If publication throws after its receipt became durable, the transaction is committed and
    /// success is returned. The journal and this database cannot share a transaction; callers
    /// must reconcile durable receipts if the subsequent SQLite commit itself fails.
    /// </summary>
    public bool RemoveWithReceipt(MemoryScope scope, string memoryId, Action publishReceipt,
        Func<bool> receiptIsDurable, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryId);
        ArgumentNullException.ThrowIfNull(publishReceipt);
        ArgumentNullException.ThrowIfNull(receiptIsDurable);
        var path = PathFor(scope);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return false;
        RejectReparsePointAncestors(path);
        using var connection = Open(path, create: false);
        if (!HasTable(connection)) return false;
        using var transaction = connection.BeginTransaction(deferred: false);
        var records = ReadPartition(connection, transaction, scope, cancellationToken);
        var target = records.FirstOrDefault(item => string.Equals(item.Id, memoryId, StringComparison.Ordinal));
        if (target is null)
        {
            transaction.Commit();
            return false;
        }

        RemoveWithinTransaction(connection, transaction, scope, target, records);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            publishReceipt();
        }
        catch
        {
            if (!receiptIsDurable()) throw;
            transaction.Commit();
            return true;
        }
        transaction.Commit();
        return true;
    }

    public int MarkUsed(MemoryScope scope, IReadOnlyCollection<string> memoryIds,
        DateTimeOffset usedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memoryIds);
        var path = PathFor(scope);
        if (memoryIds.Count > MaximumRecordsPerPartition
            || memoryIds.Any(string.IsNullOrWhiteSpace)
            || memoryIds.Any(id => id.Length > 128))
            throw new ArgumentException("Scoped memory usage ids are outside their supported bounds.", nameof(memoryIds));
        var requested = memoryIds.ToHashSet(StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        if (requested.Count == 0) return 0;
        if (!File.Exists(path)) return 0;
        RejectReparsePointAncestors(path);
        using var connection = Open(path, create: false);
        if (!HasTable(connection)) return 0;
        using var transaction = connection.BeginTransaction(deferred: false);
        var records = ReadPartition(connection, transaction, scope, cancellationToken);
        var changed = 0;
        foreach (var record in records)
        {
            if (!requested.Contains(record.Id) || record.LastUsedAt >= usedAt) continue;
            var updated = record with { LastUsedAt = usedAt };
            var payload = Serialize(updated);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"UPDATE {Table} SET payload = $payload WHERE scope_level = $level AND scope_id = $scope AND record_id = $id";
            AddScopeParameters(command, scope);
            command.Parameters.Add("$payload", SqliteType.Blob).Value = payload;
            command.Parameters.AddWithValue("$id", record.Id);
            changed += command.ExecuteNonQuery();
        }
        if (records.Sum(record => (long)Serialize(requested.Contains(record.Id)
                && (record.LastUsedAt is null || record.LastUsedAt.Value < usedAt)
                ? record with { LastUsedAt = usedAt } : record).Length)
            > MaximumPayloadBytesPerPartition)
            throw new InvalidDataException("The scoped memory payload limit has been reached.");
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return changed;
    }

    private string PathFor(MemoryScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate();
        string path = scope.Level switch
        {
            ScopeLevel.Project => Path.Combine(_dataDirectory, "projects", scope.Id, "memory.db"),
            ScopeLevel.Workspace => Path.Combine(_dataDirectory, "workspaces", scope.Id, "memory.db"),
            ScopeLevel.User => Path.Combine(_dataDirectory, "user.db"),
            _ => throw new ArgumentException("Scoped memory supports only Project, Workspace, and User scopes.", nameof(scope)),
        };
        RejectStorageReparsePoints(path);
        return path;
    }

    private SqliteConnection Open(string path, bool create)
    {
        if (create)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        RejectStorageReparsePoints(path);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5,
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout = 5000";
            command.ExecuteNonQuery();
            RejectStorageReparsePoints(path);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static bool HasTable(SqliteConnection connection)
        => HasTable(connection, Table);

    private static bool HasTable(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type FROM sqlite_master WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        var type = command.ExecuteScalar() as string;
        if (type is null) return false;
        if (!string.Equals(type, "table", StringComparison.Ordinal))
            throw new InvalidDataException("The scoped memory database contains an incompatible object.");
        return true;
    }

    private static void EnsureTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE IF NOT EXISTS {Table} (scope_level TEXT NOT NULL, scope_id TEXT NOT NULL, record_id TEXT NOT NULL, normalized_content TEXT NOT NULL, supersedes_id TEXT NULL, payload BLOB NOT NULL, PRIMARY KEY (scope_level, scope_id, record_id), UNIQUE (scope_level, scope_id, normalized_content), UNIQUE (scope_level, scope_id, supersedes_id)); CREATE TABLE IF NOT EXISTS {OperationsTable} (scope_level TEXT NOT NULL, scope_id TEXT NOT NULL, operation_id TEXT NOT NULL, request_hash TEXT NOT NULL, memory_id TEXT NOT NULL, removed INTEGER NOT NULL, PRIMARY KEY (scope_level, scope_id, operation_id))";
        command.ExecuteNonQuery();
        if (!HasTable(connection, Table) || !HasTable(connection, OperationsTable))
            throw new InvalidDataException("Could not initialize the scoped memory tables.");
        EnsureEvidenceTables(connection);
    }

    private static MemoryRecord AddWithinTransaction(SqliteConnection connection,
        SqliteTransaction transaction, MemoryScope scope, MemoryRecord record,
        IReadOnlyList<MemoryRecord> records, CancellationToken cancellationToken)
    {
        var sameId = records.FirstOrDefault(item => string.Equals(item.Id, record.Id, StringComparison.Ordinal));
        if (sameId is not null)
        {
            if (Normalize(sameId.Content) == Normalize(record.Content)) return sameId;
            throw new InvalidDataException("A scoped memory record already uses that id.");
        }
        ValidateReplacement(records, record);
        var normalized = Normalize(record.Content);
        var duplicate = records.FirstOrDefault(item => Normalize(item.Content) == normalized);
        if (duplicate is not null) return duplicate;
        if (records.Count >= MaximumRecordsPerPartition)
            throw new InvalidDataException("The scoped memory record limit has been reached.");
        var payload = Serialize(record);
        if (records.Sum(item => (long)Serialize(item).Length) + payload.Length > MaximumPayloadBytesPerPartition)
            throw new InvalidDataException("The scoped memory payload limit has been reached.");
        cancellationToken.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {Table} (scope_level, scope_id, record_id, normalized_content, supersedes_id, payload) VALUES ($level, $scope, $id, $normalized, $supersedes, $payload)";
        AddScopeParameters(command, scope);
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$normalized", normalized);
        command.Parameters.AddWithValue("$supersedes", (object?)record.Supersedes ?? DBNull.Value);
        command.Parameters.Add("$payload", SqliteType.Blob).Value = payload;
        command.ExecuteNonQuery();
        return record;
    }

    private static ScopedMemoryMutationResult ApplyForget(SqliteConnection connection,
        SqliteTransaction transaction, MemoryScope scope, string memoryId,
        IReadOnlyList<MemoryRecord> records)
    {
        var target = records.FirstOrDefault(item => string.Equals(item.Id, memoryId, StringComparison.Ordinal));
        if (target is null) return new ScopedMemoryMutationResult(memoryId, false);
        TombstoneEvidence(connection, transaction, scope, target.Kind, target.Content);
        RemoveWithinTransaction(connection, transaction, scope, target, records);
        return new ScopedMemoryMutationResult(memoryId, true);
    }

    private static (string RequestHash, string MemoryId, bool Removed)? ReadOperation(
        SqliteConnection connection, SqliteTransaction transaction, MemoryScope scope, string operationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT request_hash, memory_id, removed FROM {OperationsTable} WHERE scope_level = $level AND scope_id = $scope AND operation_id = $id";
        AddScopeParameters(command, scope);
        command.Parameters.AddWithValue("$id", operationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0);
    }

    private static int CountOperations(SqliteConnection connection, SqliteTransaction transaction, MemoryScope scope)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {OperationsTable} WHERE scope_level = $level AND scope_id = $scope";
        AddScopeParameters(command, scope);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void WriteOperation(SqliteConnection connection, SqliteTransaction transaction,
        MemoryScope scope, string operationId, string requestHash, ScopedMemoryMutationResult result)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {OperationsTable} (scope_level, scope_id, operation_id, request_hash, memory_id, removed) VALUES ($level, $scope, $operation, $hash, $memory, $removed)";
        AddScopeParameters(command, scope);
        command.Parameters.AddWithValue("$operation", operationId);
        command.Parameters.AddWithValue("$hash", requestHash);
        command.Parameters.AddWithValue("$memory", result.MemoryId);
        command.Parameters.AddWithValue("$removed", result.Removed ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static void ValidatePrepared(ScopedMemoryMutationPrepared prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared.Scope);
        ArgumentNullException.ThrowIfNull(prepared.OperationId);
        ArgumentNullException.ThrowIfNull(prepared.Operation);
        ArgumentNullException.ThrowIfNull(prepared.CandidateId);
        ArgumentNullException.ThrowIfNull(prepared.MemoryId);
        ArgumentNullException.ThrowIfNull(prepared.ExpectedRevision);
        prepared.Scope.Validate();
        if (prepared.Scope.Level is not (ScopeLevel.Project or ScopeLevel.Workspace or ScopeLevel.User)
            || string.IsNullOrWhiteSpace(prepared.OperationId) || prepared.OperationId.Length > 128
            || prepared.OperationId.Any(char.IsControl)
            || prepared.ExpectedRevision.Length != 64
            || prepared.ExpectedRevision.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("The prepared scoped memory mutation is invalid.", nameof(prepared));
        if (prepared.Operation == "promote")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prepared.CandidateId);
            ArgumentException.ThrowIfNullOrWhiteSpace(prepared.MemoryId);
            ArgumentNullException.ThrowIfNull(prepared.Record);
            prepared.Record.Validate();
            if (prepared.CandidateId.Length > 128 || prepared.MemoryId.Length > 128
                || prepared.Record.Scope != prepared.Scope || prepared.Record.Id != prepared.MemoryId)
                throw new ArgumentException("The prepared promotion record does not match its scope or identity.", nameof(prepared));
        }
        else if (prepared.Operation == "forget")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prepared.MemoryId);
            if (prepared.CandidateId.Length > 128 || prepared.MemoryId.Length > 128 || prepared.Record is not null)
                throw new ArgumentException("The prepared forget mutation is invalid.", nameof(prepared));
        }
        else throw new ArgumentException("The scoped memory operation is unsupported.", nameof(prepared));
    }

    private static string RequestHash(ScopedMemoryMutationPrepared prepared)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(prepared,
            MemoryJsonContext.Default.ScopedMemoryMutationPrepared);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static string RevisionOf(IReadOnlyList<MemoryRecord> records)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var record in records.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var bytes = Serialize(record with { LastUsedAt = null });
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static List<MemoryRecord> ReadPartition(SqliteConnection connection,
        SqliteTransaction? transaction, MemoryScope scope, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT record_id, normalized_content, supersedes_id, length(payload), payload FROM {Table} WHERE scope_level = $level AND scope_id = $scope";
        AddScopeParameters(command, scope);
        using var reader = command.ExecuteReader();
        var records = new List<MemoryRecord>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long payloadBytes = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var storedId = reader.GetString(0);
            var storedNormalizedContent = reader.GetString(1);
            var storedSupersedes = reader.IsDBNull(2) ? null : reader.GetString(2);
            var storedPayloadBytes = reader.GetInt64(3);
            payloadBytes += storedPayloadBytes;
            if (records.Count >= MaximumRecordsPerPartition || payloadBytes > MaximumPayloadBytesPerPartition)
                throw new InvalidDataException("The scoped memory partition exceeds its storage bounds.");
            var payload = (byte[])reader[4];
            MemoryRecord record;
            try
            {
                record = JsonSerializer.Deserialize(payload, MemoryJsonContext.Default.MemoryRecord)
                    ?? throw new InvalidDataException("The scoped memory record is empty or malformed.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The scoped memory record is malformed.", exception);
            }
            record.Validate();
            if (record.Scope != scope || !ids.Add(record.Id)
                || !string.Equals(storedId, record.Id, StringComparison.Ordinal)
                || !string.Equals(storedNormalizedContent, Normalize(record.Content), StringComparison.Ordinal)
                || !string.Equals(storedSupersedes, record.Supersedes, StringComparison.Ordinal))
                throw new InvalidDataException("The scoped memory partition contains a foreign scope or duplicate id.");
            records.Add(record);
        }
        ValidateSupersedes(records);
        return records;
    }

    private static void AddScopeParameters(SqliteCommand command, MemoryScope scope)
    {
        command.Parameters.AddWithValue("$level", scope.Level.ToString());
        command.Parameters.AddWithValue("$scope", scope.Id);
    }

    private static void ValidateReplacement(IReadOnlyList<MemoryRecord> records, MemoryRecord record)
    {
        if (record.Supersedes is not { } supersededId) return;
        var superseded = records.FirstOrDefault(item => string.Equals(item.Id, supersededId, StringComparison.Ordinal));
        if (superseded is null)
            throw new InvalidDataException("A scoped memory replacement must reference an existing record in the same scope.");
        if (record.CreatedAt < superseded.CreatedAt)
            throw new InvalidDataException("A scoped memory replacement cannot predate the record it replaces.");
        if (records.Any(item => string.Equals(item.Supersedes, supersededId, StringComparison.Ordinal)))
            throw new InvalidDataException("A scoped memory record already has an explicit replacement.");
    }

    private static void ValidateSupersedes(IReadOnlyList<MemoryRecord> records)
    {
        var byId = records.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record.Supersedes is not { } priorId) continue;
            if (string.Equals(record.Id, priorId, StringComparison.Ordinal)
                || !byId.TryGetValue(priorId, out var prior)
                || record.CreatedAt < prior.CreatedAt
                || !replaced.Add(priorId))
                throw new InvalidDataException("The scoped memory partition contains an invalid replacement chain.");
        }
        foreach (var record in records)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = record;
            while (current.Supersedes is { } priorId)
            {
                if (!visited.Add(current.Id))
                    throw new InvalidDataException("The scoped memory partition contains a replacement cycle.");
                current = byId[priorId];
            }
        }
    }

    private static void RemoveWithinTransaction(SqliteConnection connection,
        SqliteTransaction transaction, MemoryScope scope, MemoryRecord target,
        IReadOnlyList<MemoryRecord> records)
    {
        var child = records.FirstOrDefault(item => string.Equals(item.Supersedes, target.Id, StringComparison.Ordinal));
        var updatedChild = child is null ? null : child with { Supersedes = target.Supersedes };
        var resultingBytes = records.Where(item => !ReferenceEquals(item, target))
            .Sum(item => (long)Serialize(ReferenceEquals(item, child) ? updatedChild! : item).Length);
        if (resultingBytes > MaximumPayloadBytesPerPartition)
            throw new InvalidDataException("The scoped memory payload limit has been reached.");

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {Table} WHERE scope_level = $level AND scope_id = $scope AND record_id = $id";
            AddScopeParameters(delete, scope);
            delete.Parameters.AddWithValue("$id", target.Id);
            delete.ExecuteNonQuery();
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = $"UPDATE {Table} SET supersedes_id = $prior, payload = $payload WHERE scope_level = $level AND scope_id = $scope AND supersedes_id = $id";
            AddScopeParameters(update, scope);
            update.Parameters.AddWithValue("$id", target.Id);
            update.Parameters.AddWithValue("$prior", (object?)target.Supersedes ?? DBNull.Value);
            update.Parameters.Add("$payload", SqliteType.Blob).Value = updatedChild is null
                ? Array.Empty<byte>() : Serialize(updatedChild);
            if (updatedChild is not null) update.ExecuteNonQuery();
        }
    }

    private void RejectStorageReparsePoints(string path)
    {
        RejectReparsePointAncestors(path);
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" }) RejectReparsePointAncestors(path + suffix);
    }

    private void RejectReparsePointAncestors(string targetPath)
    {
        var current = Path.GetFullPath(targetPath);
        var root = Path.GetPathRoot(_dataDirectory)!;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Scoped memory cannot use a reparse point in its storage path.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (string.Equals(current, root, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static byte[] Serialize(MemoryRecord record) =>
        JsonSerializer.SerializeToUtf8Bytes(record, MemoryJsonContext.Default.MemoryRecord);

    private static string Normalize(string content) => string.Join(' ', content.Normalize(NormalizationForm.FormKC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static bool IsCapacityFailure(InvalidDataException exception) => exception.Message is
        "The scoped memory record limit has been reached."
        or "The scoped memory payload limit has been reached.";
}
