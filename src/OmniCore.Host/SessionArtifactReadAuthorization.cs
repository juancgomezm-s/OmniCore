namespace OmniCore.Host;

using System.Text.Json;
using System.Text.RegularExpressions;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

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
        if (ExecutionScope.Current is { } execution)
            return IsReferencedByLane(session, execution, requestedHash);

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

    private bool IsReferencedByLane(SessionId session, ExecutionScopeState execution, string requestedHash)
    {
        if (execution.RunId is not { } run || execution.LaneId is not { } lane) return false;
        try
        {
            var journal = _store.ReadFrom(session, 1);
            var codecs = EventCodecs.Create();
            var scope = new LaneConversationScope(journal, codecs, run, lane);
            if (scope.TargetTask is null || execution.TaskId is { } task && task != scope.TargetTask) return false;
            var pending = new Queue<ArtifactRef>();
            foreach (var evt in journal.Where(scope.CanRead))
            {
                foreach (var reference in evt.ArtifactRefs)
                    pending.Enqueue(reference);
                // Typed legacy roots for envelopes predating persisted ArtifactRefs.
                var legacy = codecs.Decode(evt) switch {
                    TurnStarted e => e.ContextSnapshotRef, ModelStepStarted e => e.ContextSnapshotRef,
                    ModelStepCompleted e => e.ResponseArtifact, ModelCompleted e => e.ResponseArtifact,
                    AssistantMessageRecorded e => e.ContentRef, UserInputReceived e => e.ContentRef,
                    ContextCheckpointRecorded e => e.CheckpointArtifact, _ => null,
                };
                if (legacy is not null) pending.Enqueue(legacy);
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var traversed = 0;
            while (pending.Count > 0 && traversed++ < 10000)
            {
                var reference = pending.Dequeue();
                if (reference.Hash.Algorithm != "sha256" || !seen.Add(reference.Hash.Value)
                    || !_artifacts.Verify(reference.Hash, reference.Size)) continue;
                if (reference.Kind != ArtifactKind.ContextSnapshot)
                {
                    if (reference.Hash.Value == requestedHash) return true;
                    continue; // User/model/tool text cannot manufacture further read grants.
                }
                var text = _artifacts.GetText(reference.Hash);
                if (text is null) continue;
                using var json = JsonDocument.Parse(text);
                var root = json.RootElement;
                if (root.TryGetProperty("laneId", out var bodyLane))
                {
                    if (bodyLane.GetString() != lane.ToString()) continue;
                    if (root.TryGetProperty("snapshotId", out _)
                        && (!root.TryGetProperty("contextScopeVersion", out var version) || version.GetInt32() != 1
                            || root.GetProperty("sessionId").GetString() != session.ToString()
                            || root.GetProperty("runId").GetString() != run.ToString()
                            || root.GetProperty("taskId").GetString() != scope.TargetTask.ToString())) continue;
                }
                else if (scope.HasMultipleLanes) continue;
                if (reference.Hash.Value == requestedHash) return true;
                if (!root.TryGetProperty("items", out var items)) continue;
                foreach (var item in items.EnumerateArray())
                {
                    var kind = item.GetProperty("kind").GetString();
                    var contributor = item.GetProperty("contributor").GetString();
                    // Only core-authored projection edges, never a hash/stub mentioned in text.
                    var externalized = kind == "ToolResult" && contributor == "session-conversation";
                    var summary = kind == "Summary" && contributor is "core.context-checkpoint" or "core.run-summary";
                    if ((!externalized && !summary) || !item.TryGetProperty("refs", out var refs)) continue;
                    foreach (var edge in refs.EnumerateArray())
                    {
                        var value = edge.GetString();
                        const string prefix = "artifact=sha256:";
                        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal)) continue;
                        var hash = value[prefix.Length..];
                        if (hash.Length != 64 || !hash.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f')) continue;
                        // These receipts describe an externalized output or a bounded summary.
                        // Do not enqueue its body as another graph of authority.
                        if (hash == requestedHash && _artifacts.GetText(ContentHash.Sha256(hash)) is not null) return true;
                    }
                }
            }
        }
        catch (Exception failure) when (failure is InvalidDataException or JsonException or KeyNotFoundException
            or InvalidOperationException or ArgumentException or FormatException)
        { return false; }
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
