namespace OmniCore.Memory;

using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// SQLite-backed Project, Workspace, and User memory. This store only persists records;
/// authorization, trust checks, promotion policy, and context selection remain Host duties.
/// Each mutation uses a SQLite immediate transaction so independent processes serialize writes.
/// </summary>
public sealed class ScopedMemoryStore : IMemoryStore
{
    private const int MaximumRecordsPerPartition = 500;
    private const long MaximumPayloadBytesPerPartition = 4 * 1024 * 1024;
    private const string Table = "scoped_memory_records";
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
        var sameId = records.FirstOrDefault(item => string.Equals(item.Id, record.Id, StringComparison.Ordinal));
        if (sameId is not null)
        {
            if (Normalize(sameId.Content) == Normalize(record.Content))
            {
                transaction.Commit();
                return sameId;
            }
            throw new InvalidDataException("A scoped memory record already uses that id.");
        }

        ValidateReplacement(records, record);
        var normalized = Normalize(record.Content);
        var duplicate = records.FirstOrDefault(item => Normalize(item.Content) == normalized);
        if (duplicate is not null)
        {
            transaction.Commit();
            return duplicate;
        }
        if (records.Count >= MaximumRecordsPerPartition)
            throw new InvalidDataException("The scoped memory record limit has been reached.");

        var payload = JsonSerializer.SerializeToUtf8Bytes(record, MemoryJsonContext.Default.MemoryRecord);
        if (records.Sum(item => (long)Serialize(item).Length) + payload.Length > MaximumPayloadBytesPerPartition)
            throw new InvalidDataException("The scoped memory payload limit has been reached.");
        cancellationToken.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {Table} (scope_level, scope_id, record_id, normalized_content, supersedes_id, payload) VALUES ($level, $scope, $id, $normalized, $supersedes, $payload)";
        AddScopeParameters(command, record.Scope);
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$normalized", normalized);
        command.Parameters.AddWithValue("$supersedes", (object?)record.Supersedes ?? DBNull.Value);
        command.Parameters.Add("$payload", SqliteType.Blob).Value = payload;
        command.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return record;
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
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type FROM sqlite_master WHERE name = $name";
        command.Parameters.AddWithValue("$name", Table);
        var type = command.ExecuteScalar() as string;
        if (type is null) return false;
        if (!string.Equals(type, "table", StringComparison.Ordinal))
            throw new InvalidDataException("The scoped memory database contains an incompatible object.");
        return true;
    }

    private static void EnsureTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE IF NOT EXISTS {Table} (scope_level TEXT NOT NULL, scope_id TEXT NOT NULL, record_id TEXT NOT NULL, normalized_content TEXT NOT NULL, supersedes_id TEXT NULL, payload BLOB NOT NULL, PRIMARY KEY (scope_level, scope_id, record_id), UNIQUE (scope_level, scope_id, normalized_content), UNIQUE (scope_level, scope_id, supersedes_id))";
        command.ExecuteNonQuery();
        if (!HasTable(connection)) throw new InvalidDataException("Could not initialize the scoped memory table.");
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
}
