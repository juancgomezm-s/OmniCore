namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>One explicit local Ask decision for external memory, with exact project bindings.</summary>
public sealed class ExternalAgentMemoryConsentStore(string userDataDirectory)
{
    private readonly string _path = Path.Combine(Path.GetFullPath(userDataDirectory), "external-agent-memory-consent.json");
    private readonly object _gate = new();

    public bool IsAllowed() => Read().Allowed;

    public bool IsProjectAllowed(string? projectIdentity) => projectIdentity is not null
        && Read().ProjectHashes.Contains(ProjectHash(projectIdentity), StringComparer.Ordinal);

    public void SetGlobal(bool allowed)
    {
        lock (_gate)
        {
            using var lease = AcquireLeaseWithRetry();
            var previous = ReadUnsafe();
            WriteUnsafe(allowed, previous.ProjectHashes);
        }
    }

    public void SetProject(bool allowed, string projectIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectIdentity);
        lock (_gate)
        {
            using var lease = AcquireLeaseWithRetry();
            var previous = ReadUnsafe();
            var hashes = previous.ProjectHashes.ToHashSet(StringComparer.Ordinal);
            var hash = ProjectHash(projectIdentity);
            if (allowed) hashes.Add(hash);
            else hashes.Remove(hash);
            WriteUnsafe(previous.Allowed, hashes.Order(StringComparer.Ordinal).ToArray());
        }
    }

    public void Set(bool allowed, string? projectIdentity = null)
    {
        lock (_gate)
        {
            using var lease = AcquireLeaseWithRetry();
            var previous = ReadUnsafe();
            var hashes = allowed ? previous.ProjectHashes.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
            if (allowed && projectIdentity is not null) hashes.Add(ProjectHash(projectIdentity));
            WriteUnsafe(allowed, hashes.Order(StringComparer.Ordinal).ToArray());
        }
    }

    private (bool Allowed, string[] ProjectHashes) Read()
    {
        lock (_gate)
        {
            using var lease = AcquireLeaseWithRetry();
            return ReadUnsafe();
        }
    }

    private FileStream AcquireLeaseWithRetry()
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return ConfigurationWriteLease.Acquire(_path); }
            catch (IOException) when (attempt < 49) { Thread.Sleep(10); }
        }
    }

    private (bool Allowed, string[] ProjectHashes) ReadUnsafe()
    {
        try
        {
            ConfigurationWriteLease.EnsureNoLinks(_path);
            if (!File.Exists(_path) || (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0) return (false, []);
            var bytes = ReadBounded(_path, 16_384);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
                || !version.TryGetInt32(out var revision) || revision is not (1 or 2)
                || !root.TryGetProperty("readExternalMemory", out var allowed)
                || allowed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return (false, []);
            var hashes = new List<string>();
            if (revision == 2 && root.TryGetProperty("projectIdentityHashes", out var entries)
                && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in entries.EnumerateArray().Take(64))
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: 64 } hash
                        && hash.All(Uri.IsHexDigit)) hashes.Add(hash.ToLowerInvariant());
            }
            return (allowed.GetBoolean(), hashes.Distinct(StringComparer.Ordinal).ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or ArgumentException)
        { return (false, []); }
    }

    private void WriteUnsafe(bool allowed, string[] hashes)
    {
        ConfigurationWriteLease.EnsureNoLinks(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var content = "{\"version\":2,\"readExternalMemory\":" + (allowed ? "true" : "false")
                + ",\"projectIdentityHashes\":[" + string.Join(",", hashes.Select(hash => "\"" + hash + "\"")) + "]}";
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            if (File.Exists(_path)) File.Move(temporary, _path, overwrite: true);
            else File.Move(temporary, _path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4_096, FileOptions.SequentialScan);
        if (stream.Length is < 2 || stream.Length > maximumBytes) throw new InvalidDataException("Memory consent state exceeds its limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("Memory consent state changed during read.");
        return bytes;
    }

    private static string ProjectHash(string identity) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.GetFullPath(identity).Replace(Path.DirectorySeparatorChar, '/'))));
}
