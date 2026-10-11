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
    private readonly string _root;

    public SessionMemoryStore(string workspaceDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDataDirectory);
        _root = Path.Combine(Path.GetFullPath(workspaceDataDirectory), "session-memory");
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
            var removed = records.RemoveAll(record => string.Equals(record.Id, memoryId, StringComparison.Ordinal)) > 0;
            if (removed) WriteFile(path, records, cancellationToken);
            return removed;
        }
    }

    private string PathFor(MemoryScope scope)
    {
        if (!Guid.TryParse(scope.Id, out var sessionId))
            throw new ArgumentException("Session memory requires a canonical SessionId.", nameof(scope));
        return Path.Combine(_root, sessionId.ToString("N") + ".json");
    }

    private static IReadOnlyList<MemoryRecord> ReadFile(string path, MemoryScope scope,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return [];
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
            return records.OrderByDescending(item => item.Importance * item.Confidence)
                .ThenByDescending(item => item.CreatedAt).ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Session memory file is malformed.", exception);
        }
    }

    private static void WriteFile(string path, List<MemoryRecord> records, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, records.ToArray(), MemoryJsonContext.Default.MemoryRecordArray);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static object Gate(string path) => Gates.GetOrAdd(path, static _ => new object());
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
