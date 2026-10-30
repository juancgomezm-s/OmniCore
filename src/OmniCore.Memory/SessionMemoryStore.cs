namespace OmniCore.Memory;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Atomic, Workspace-local file store partitioned strictly by SessionId (ADR-0039).</summary>
public sealed class SessionMemoryStore : IMemoryStore
{
    private const int MaximumRecordsPerSession = 500;
    private const long MaximumFileBytes = 4 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dataDirectory;
    private readonly string _root;

    public SessionMemoryStore(string workspaceDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDataDirectory);
        _dataDirectory = Path.GetFullPath(workspaceDataDirectory);
        _root = Path.Combine(_dataDirectory, "session-memory");
    }

    public IReadOnlyList<MemoryRecord> Read(MemoryScope scope, CancellationToken cancellationToken)
    {
        scope.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathFor(scope);
        lock (Gate(path)) return ReadFile(path, scope, cancellationToken);
    }

    public MemoryRecord Add(MemoryRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        record.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathFor(record.Scope);
        lock (Gate(path))
        {
            var records = ReadFile(path, record.Scope, cancellationToken).ToList();
            var sameId = records.FirstOrDefault(item => string.Equals(item.Id, record.Id, StringComparison.Ordinal));
            if (sameId is not null && Normalize(sameId.Content) == Normalize(record.Content)) return sameId;
            if (sameId is not null)
                throw new InvalidDataException("A Session memory record already uses that id.");
            if (record.Supersedes is { } supersededId)
            {
                var superseded = records.FirstOrDefault(item => string.Equals(item.Id, supersededId, StringComparison.Ordinal));
                if (superseded is null)
                    throw new InvalidDataException("A Session memory replacement must reference an existing record in the same Session.");
                if (record.CreatedAt < superseded.CreatedAt)
                    throw new InvalidDataException("A Session memory replacement cannot predate the record it replaces.");
                if (records.Any(item => string.Equals(item.Supersedes, supersededId, StringComparison.Ordinal)))
                    throw new InvalidDataException("A Session memory record already has an explicit replacement.");
            }
            var normalized = Normalize(record.Content);
            var duplicate = records.FirstOrDefault(item => Normalize(item.Content) == normalized);
            if (duplicate is not null) return duplicate;
            if (records.Count >= MaximumRecordsPerSession)
                throw new InvalidDataException("The Session memory record limit has been reached.");
            records.Add(record);
            WriteFile(path, records, cancellationToken);
            return record;
        }
    }

    public bool Remove(MemoryScope scope, string memoryId, CancellationToken cancellationToken)
    {
        scope.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryId);
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathFor(scope);
        lock (Gate(path))
        {
            var records = ReadFile(path, scope, cancellationToken).ToList();
            var target = records.FirstOrDefault(record => string.Equals(record.Id, memoryId, StringComparison.Ordinal));
            var removed = target is not null;
            if (target is not null)
            {
                // Collapse replacement chains across a forgotten node. Forgetting the newest
                // replacement releases its predecessor; forgetting an older node keeps the
                // newest descendant attached to the older node's predecessor.
                for (var index = 0; index < records.Count; index++)
                    if (string.Equals(records[index].Supersedes, target.Id, StringComparison.Ordinal))
                        records[index] = records[index] with { Supersedes = target.Supersedes };
                records.Remove(target);
            }
            if (removed) WriteFile(path, records, cancellationToken);
            return removed;
        }
    }

    /// <summary>Atomically edits the record set, publishes its durable journal receipt, and
    /// restores the exact prior set (including replacement links) if publication failed before
    /// the receipt became durable.</summary>
    public bool RemoveWithReceipt(MemoryScope scope, string memoryId, Action publishReceipt,
        Func<bool> receiptIsDurable, CancellationToken cancellationToken)
    {
        scope.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryId);
        ArgumentNullException.ThrowIfNull(publishReceipt);
        ArgumentNullException.ThrowIfNull(receiptIsDurable);
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathFor(scope);
        lock (Gate(path))
        {
            var prior = ReadFile(path, scope, cancellationToken).ToList();
            var target = prior.FirstOrDefault(record => string.Equals(record.Id, memoryId, StringComparison.Ordinal));
            if (target is null) return false;
            var replacement = prior.ToList();
            for (var index = 0; index < replacement.Count; index++)
                if (string.Equals(replacement[index].Supersedes, target.Id, StringComparison.Ordinal))
                    replacement[index] = replacement[index] with { Supersedes = target.Supersedes };
            replacement.Remove(target);
            WriteFile(path, replacement, cancellationToken);
            try
            {
                publishReceipt();
                return true;
            }
            catch (Exception publishFailure)
            {
                if (receiptIsDurable()) return true;
                try { WriteFile(path, prior, CancellationToken.None); }
                catch (Exception restoreFailure)
                {
                    throw new InvalidOperationException("sessionPreferences.error.memory_forget_rollback_failed",
                        new AggregateException(publishFailure, restoreFailure));
                }
                throw;
            }
        }
    }

    public int MarkUsed(MemoryScope scope, IReadOnlyCollection<string> memoryIds, DateTimeOffset usedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(memoryIds);
        scope.Validate();
        if (memoryIds.Count > MaximumRecordsPerSession
            || memoryIds.Any(string.IsNullOrWhiteSpace)
            || memoryIds.Any(id => id.Length > 128))
            throw new ArgumentException("Session memory usage ids are outside their supported bounds.", nameof(memoryIds));
        cancellationToken.ThrowIfCancellationRequested();
        var requested = memoryIds.ToHashSet(StringComparer.Ordinal);
        var path = PathFor(scope);
        if (requested.Count == 0) return 0;
        lock (Gate(path))
        {
            var records = ReadFile(path, scope, cancellationToken).ToList();
            var changed = 0;
            for (var index = 0; index < records.Count; index++)
            {
                var record = records[index];
                if (!requested.Contains(record.Id) || record.LastUsedAt >= usedAt) continue;
                records[index] = record with { LastUsedAt = usedAt };
                changed++;
            }
            if (changed > 0) WriteFile(path, records, cancellationToken);
            return changed;
        }
    }

    private string PathFor(MemoryScope scope)
    {
        if (scope.Level != ScopeLevel.Session || !Guid.TryParse(scope.Id, out var sessionId))
            throw new ArgumentException("Session memory requires a canonical SessionId.", nameof(scope));
        var path = Path.Combine(_root, sessionId.ToString("N") + ".json");
        RejectReparsePointAncestors(path);
        return path;
    }

    private IReadOnlyList<MemoryRecord> ReadFile(string path, MemoryScope scope,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return [];
        RejectReparsePointAncestors(path);
        if (new FileInfo(path).Length > MaximumFileBytes)
            throw new InvalidDataException("The Session memory file exceeds the size limit.");
        try
        {
            using var stream = File.OpenRead(path);
            var records = JsonSerializer.Deserialize(stream, MemoryJsonContext.Default.MemoryRecordArray)
                ?? throw new InvalidDataException("The Session memory file is empty or malformed.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (records.Length > MaximumRecordsPerSession)
                throw new InvalidDataException("The Session memory record limit has been exceeded.");
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                record.Validate();
                if (record.Scope != scope || !ids.Add(record.Id))
                    throw new InvalidDataException("The Session memory file contains a foreign scope or duplicate id.");
            }
            ValidateSupersedes(records);
            return records.OrderByDescending(item => item.Importance * item.Confidence)
                .ThenByDescending(item => item.CreatedAt).ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Session memory file is malformed.", exception);
        }
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
                throw new InvalidDataException("The Session memory file contains an invalid replacement chain.");
        }

        foreach (var record in records)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = record;
            while (current.Supersedes is { } priorId)
            {
                if (!visited.Add(current.Id))
                    throw new InvalidDataException("The Session memory file contains a replacement cycle.");
                current = byId[priorId];
            }
        }
    }

    private void WriteFile(string path, List<MemoryRecord> records, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RejectReparsePointAncestors(path);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, records.ToArray(), MemoryJsonContext.Default.MemoryRecordArray);
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePointAncestors(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static object Gate(string path) => Gates.GetOrAdd(path, static _ => new object());

    private void RejectReparsePointAncestors(string targetPath)
    {
        var current = Path.GetFullPath(targetPath);
        var root = Path.GetPathRoot(_dataDirectory)!;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Session memory cannot use a reparse point in its storage path.");
            }
            if (string.Equals(current, root, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static string Normalize(string content) => string.Join(' ', content.Normalize(NormalizationForm.FormKC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MemoryRecord[]))]
[JsonSerializable(typeof(MemoryRecord))]
[JsonSerializable(typeof(MemoryScope))]
internal sealed partial class MemoryJsonContext : JsonSerializerContext
{
}
