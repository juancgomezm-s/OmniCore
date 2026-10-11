namespace OmniCore.Client;

using System.Globalization;
using System.Text.Json;

/// <summary>Typed, allow-listed Session preference data. Unknown fields are intentionally discarded.</summary>
public sealed record SessionPreferencesData(string QueryName, string? SessionId, bool? WorkspaceTrusted,
    ContextSnapshotPresentation? Context, IReadOnlyList<ExtensionPresentation> Extensions,
    IReadOnlyList<SkillPresentation> Skills, IReadOnlyList<MemoryPresentation> Memory,
    bool Available = true)
{
    public static SessionPreferencesData Unavailable(string queryName) =>
        new(queryName, null, null, null, [], [], [], Available: false);
}

public sealed record ContextSnapshotPresentation(string SnapshotId, string Fingerprint, int? TokenCount,
    string? TokenAccuracy, string? TokenBudget, bool? Overflowed,
    IReadOnlyList<ContextItemPresentation> Items, IReadOnlyList<ContextDiagnosticPresentation> Diagnostics);

public sealed record ContextItemPresentation(string Id, string Kind, int? Tokens, string? TokenAccuracy,
    string? Priority, string? Contributor, string? Category, string? Source, string? Scope, bool? Sensitive,
    IReadOnlyList<string> References);

public sealed record ContextDiagnosticPresentation(string ItemId, string Decision, int? Tokens,
    string? Reason, string? Contributor, string? Category, string? Source, string? Scope, bool? Sensitive);

public sealed record ExtensionPresentation(string Id, string DisplayName, string? Version, string Trust,
    string Status, string ManifestSha256, string PackageSha256, string ConsentHash,
    IReadOnlyList<string> RequestedCapabilities, IReadOnlyList<string> ConsentedCapabilities, bool AmbiguousId = false);

public sealed record SkillPresentation(string Id, string DisplayName, string? Version, string Scope,
    string Trust, string State, string ManifestSha256, bool Sealed, IReadOnlyList<string> Tools, bool ModelInvocable);

public sealed record MemoryPresentation(string Id, string Kind, string Content, string Source,
    double? Importance, double? Confidence, string? CreatedAt, IReadOnlyList<string> Tags);

public static class SessionPreferencesPresentation
{
    public static SessionPreferencesData Decode(string queryName, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return SessionPreferencesData.Unavailable(queryName);
            var session = String(root, "session");
            var trusted = Boolean(root, "workspaceTrusted");
            return queryName switch
            {
                "context" when ValidContext(root) => new(queryName, session, trusted, DecodeContext(root), [], [], []),
                "extensions" when ValidNullableString(root, "session") && IsBoolean(root, "workspaceTrusted")
                    && ValidObjectArray(root, "extensions") => new(queryName, session, trusted, null,
                    Array(root, "extensions").Select(DecodeExtension).ToArray(), [], []),
                "skills" when ValidNullableString(root, "session") && IsBoolean(root, "workspaceTrusted")
                    && ValidObjectArray(root, "skills") => new(queryName, session, trusted, null, [],
                    Array(root, "skills").Select(DecodeSkill).Where(item => item is not null).Cast<SkillPresentation>().ToArray(), []),
                "memory.session" when ValidNullableString(root, "session") && ValidObjectArray(root, "records") => new(queryName, session, trusted, null, [], [],
                    Array(root, "records").Select(DecodeMemory).Where(item => item is not null).Cast<MemoryPresentation>().ToArray()),
                _ => SessionPreferencesData.Unavailable(queryName),
            };
        }
        catch (JsonException) { return SessionPreferencesData.Unavailable(queryName); }
        catch (InvalidOperationException) { return SessionPreferencesData.Unavailable(queryName); }
        catch (FormatException) { return SessionPreferencesData.Unavailable(queryName); }
    }

    public static string SafeText(string? value, int maximumLength = 240)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var safe = new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray())
            .Replace('\r', ' ').Replace('\n', ' ').Trim();
        return safe.Length <= maximumLength ? safe : safe[..maximumLength] + "…";
    }

    private static bool ValidContext(JsonElement root) => root.TryGetProperty("snapshot", out var snapshot)
        && (snapshot.ValueKind == JsonValueKind.Null || (snapshot.ValueKind == JsonValueKind.Object
            && ValidObjectArray(snapshot, "items") && ValidObjectArray(snapshot, "diagnostics")));

    private static bool ValidObjectArray(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
        && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object);

    private static bool ValidNullableString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.Null or JsonValueKind.String;

    private static bool IsBoolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static ContextSnapshotPresentation? DecodeContext(JsonElement root)
    {
        if (!root.TryGetProperty("snapshot", out var snapshot) || snapshot.ValueKind != JsonValueKind.Object) return null;
        return new ContextSnapshotPresentation(String(snapshot, "snapshotId") ?? "", String(snapshot, "fingerprint") ?? "",
            Integer(snapshot, "tokenCount"), Scalar(snapshot, "tokenAccuracy"), Scalar(snapshot, "tokenBudget"),
            Boolean(snapshot, "overflowed"), Array(snapshot, "items").Select(DecodeContextItem).ToArray(),
            Array(snapshot, "diagnostics").Select(DecodeContextDiagnostic).ToArray());
    }

    private static ContextItemPresentation DecodeContextItem(JsonElement item) => new(
        String(item, "id") ?? "", String(item, "kind") ?? "unknown", Integer(item, "tokens"),
        Scalar(item, "tokenAccuracy"), Scalar(item, "priority"), String(item, "contributor"),
        String(item, "category"), String(item, "source"), String(item, "scope"), Boolean(item, "sensitive"),
        Strings(item, "refs"));

    private static ContextDiagnosticPresentation DecodeContextDiagnostic(JsonElement item) => new(
        String(item, "itemId") ?? "", String(item, "decision") ?? "unknown", Integer(item, "tokens"),
        String(item, "reason"), String(item, "contributor"), String(item, "category"), String(item, "source"),
        String(item, "scope"), Boolean(item, "sensitive"));

    private static ExtensionPresentation DecodeExtension(JsonElement item)
    {
        var id = String(item, "id") ?? "";
        return new(id, String(item, "displayName") ?? id, String(item, "version"),
            String(item, "trust") ?? "Unknown", String(item, "status") ?? "Unknown",
            String(item, "manifestSha256") ?? "", String(item, "packageSha256") ?? "", String(item, "consentHash") ?? "",
            Strings(item, "requestedCapabilities"), Strings(item, "consentedCapabilities"), Boolean(item, "ambiguousId") ?? false);
    }

    private static SkillPresentation? DecodeSkill(JsonElement item)
    {
        var id = String(item, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        return new(id, String(item, "displayName") ?? id, String(item, "version"),
            String(item, "scope") ?? "Unknown", String(item, "trust") ?? "Unknown",
            String(item, "state") ?? "Unknown", String(item, "manifestSha256") ?? "", Boolean(item, "sealed") ?? false,
            Strings(item, "tools"), Boolean(item, "modelInvocable") ?? false);
    }

    private static MemoryPresentation? DecodeMemory(JsonElement item)
    {
        var id = String(item, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        return new(id, String(item, "kind") ?? "unknown", String(item, "content") ?? "",
            String(item, "source") ?? "unknown", Number(item, "importance"), Number(item, "confidence"),
            String(item, "createdAt"), Strings(item, "tags"));
    }

    private static JsonElement[] Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToArray() : [];

    private static string[] Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "").ToArray() : [];

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Scalar(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText() : null;

    private static bool? Boolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;

    private static int? Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number : null;
}
