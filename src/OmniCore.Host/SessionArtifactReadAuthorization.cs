namespace OmniCore.Host;

using System.Text.Json;
using System.Text.RegularExpressions;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Read authority for externalized CAS content. Starts only at references in the active
/// session's canonical journal and follows references embedded in those artifacts.
/// </summary>
internal sealed class SessionArtifactReadAuthorization
{
    private readonly IEventStore _store;
    private readonly Func<SessionId?> _currentSession;
    private readonly IArtifactStore _artifacts;

    public SessionArtifactReadAuthorization(IEventStore store, Func<SessionId?> currentSession,
        IArtifactStore artifacts)
    {
        _store = store;
        _currentSession = currentSession;
        _artifacts = artifacts;
    }

    public bool IsReferenced(string requestedHash)
    {
        var session = _currentSession();
        if (session is null) return false;

        // Context snapshots can contain externalized output hashes in their artifact.read
        // stub. Follow typed ContentHash objects and that exact stub syntax, never arbitrary
        // hashes found outside the current session's referenced artifact graph.
        var pending = new Queue<string>();
        var discovered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evt in _store.ReadFrom(session!, 1))
        {
            foreach (var reference in evt.ArtifactRefs)
                if (reference.Hash.Algorithm == "sha256") pending.Enqueue(reference.Hash.Value);
            try
            {
                using var payload = JsonDocument.Parse(evt.PayloadJson);
                CollectContentHashes(payload.RootElement, pending);
            }
            catch (JsonException)
            {
                // Corrupt payloads contribute no authority to read arbitrary artifacts.
            }
        }

        var traversed = 0;
        while (pending.Count > 0 && traversed++ < 10000)
        {
            var candidate = pending.Dequeue();
            if (!discovered.Add(candidate)) continue;
            if (string.Equals(candidate, requestedHash, StringComparison.Ordinal)) return true;
            try
            {
                var content = _artifacts.GetText(ContentHash.Sha256(candidate));
                if (content is null) continue;
                foreach (Match match in Regex.Matches(content, @"artifact=sha256:([0-9a-f]{64})",
                    RegexOptions.CultureInvariant))
                    pending.Enqueue(match.Groups[1].Value);
                CollectStubHashes(content, pending);
                try
                {
                    using var json = JsonDocument.Parse(content);
                    CollectContentHashes(json.RootElement, pending);
                }
                catch (JsonException) { }
            }
            catch (InvalidDataException)
            {
                // A corrupted referenced blob cannot extend read authority.
            }
        }
        return false;
    }

    private static void CollectStubHashes(string content, Queue<string> hashes)
    {
        var index = 0;
        while ((index = content.IndexOf("hash=", index, StringComparison.Ordinal)) >= 0)
        {
            var start = index + 5;
            if (start < content.Length && content[start] == '\\') start++;
            if (start < content.Length && content[start] == '"')
            {
                start++;
                if (start + 64 <= content.Length)
                {
                    var hash = content.Substring(start, 64);
                    var valid = hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
                    var end = start + 64;
                    if (end < content.Length && content[end] == '\\') end++;
                    if (valid && end < content.Length && content[end] == '"') hashes.Enqueue(hash);
                }
            }
            index += 5;
        }
    }

    private static void CollectContentHashes(JsonElement element, Queue<string> hashes)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            string? algorithm = null;
            string? value = null;
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("algorithm", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                    algorithm = property.Value.GetString();
                else if (property.Name.Equals("value", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                    value = property.Value.GetString();
            }
            if (algorithm == "sha256" && value is not null
                && value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                hashes.Enqueue(value);
            foreach (var property in element.EnumerateObject()) CollectContentHashes(property.Value, hashes);
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CollectContentHashes(item, hashes);
    }
}
