using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniCore.Protocol;

/// <summary>Portable v1 presentation contract. Data is literal, never HTML or executable code.</summary>
public sealed record ExtensionView([property: JsonRequired] string Id, [property: JsonRequired] string ExtensionId,
    [property: JsonRequired] string Kind, [property: JsonRequired] long Revision,
    [property: JsonRequired] string State, [property: JsonRequired] IReadOnlyList<ExtensionViewItem> Items,
    [property: JsonRequired] IReadOnlyList<ExtensionViewAction> Actions,
    string? Diagnostic = null, string? Cursor = null, bool Reset = true, IReadOnlyList<string>? RemovedIds = null,
    JsonElement? Metadata = null, IReadOnlyList<ExtensionViewColumn>? Columns = null,
    IReadOnlyList<ExtensionViewField>? Fields = null, IReadOnlyList<ExtensionViewIndicator>? Indicators = null,
    IReadOnlyList<string>? ItemOrder = null, ExtensionViewScope? Scope = null);
public sealed record ExtensionViewItem(string Id, string Label, string? ParentId = null,
    bool HasChildren = false, string? Detail = null, JsonElement? Data = null,
    IReadOnlyDictionary<string, JsonElement>? Cells = null);
public sealed record ExtensionViewColumn(string Id, string Label, string ValueType = "text", string Alignment = "left");
public sealed record ExtensionViewField(string Id, string Label, string Kind, JsonElement? Value = null,
    bool Required = false, bool ReadOnly = false, IReadOnlyList<string>? Options = null);
public sealed record ExtensionViewIndicator(string Id, string Label, string State, decimal? Value = null, string? Unit = null);
public sealed record ExtensionViewAction(string Id, string Label, [property: JsonRequired] bool RequiresUserAuthority, string? ItemId = null,
    string? ConfigurationKey = null, string ConfigurationScope = "workspace", bool Enabled = true, string? DisabledReason = null);
public sealed record ExtensionViewScope([property: JsonRequired] string WorkspaceId, [property: JsonRequired] string? SessionId,
    [property: JsonRequired] string ManifestSha256, [property: JsonRequired] string PackageSha256,
    string? SnapshotId = null);
public sealed record ExtensionFormSubmission(string ExtensionId, string ViewId, string ActionId,
    long ExpectedRevision, ExtensionViewScope Scope, JsonElement Values, string Path = "", string Search = "");
public sealed record ExtensionViewRequest(string ExtensionId, string ViewId, string Path = "", string Search = "",
    long? AfterRevision = null, IReadOnlyList<string>? ClientKinds = null);
public sealed record ExtensionPackageState(string Id, string Version, string State, string ManifestSha256,
    string PackageSha256, string Provenance, bool Enabled, bool TrustedProcess,
    IReadOnlyList<string> GrantedCapabilities, string? Diagnostic = null, string? RuntimeKind = null,
    IReadOnlyList<string>? RequestedCapabilities = null, IReadOnlyList<string>? ConfigurationKeys = null,
    IReadOnlyList<string>? ContextContributors = null);
public sealed record ExtensionContextPreview(string Id, string ExtensionId, string Contributor, string Version,
    string WorkspaceId, string SessionId, string RunId, string ManifestSha256, string PackageSha256,
    string ContentSha256, IReadOnlyList<ExtensionContextText> Items, bool Approved, string? ConsentSha256 = null);
public sealed record ExtensionContextText(string Id, string Kind, string Content);
public sealed record ExtensionNotification(long Sequence, string Id, string ExtensionId, string? SessionId,
    string Level, string Text, string ManifestSha256, string PackageSha256);
public sealed record ExtensionNotificationPage(long NextSequence, bool Gap, IReadOnlyList<ExtensionNotification> Items);
public sealed record ExtensionInitialize(string ProtocolVersion, string ExtensionId,
    IReadOnlyList<string> Capabilities, string? SessionId);
public sealed record ExtensionActivityEvent(long Sequence, string Kind, string Text, string? RunId,
    string? ExecutionId, string? Agent, IReadOnlyList<string> Files, string Activity,
    string? CheckpointId, bool Recoverable, string? ExtensionId = null, string? ToolId = null,
    string? CorrelationId = null, string? CausationId = null);
/// <summary>A bounded read projection, not a second journal or a push subscription.</summary>
public sealed record ExtensionActivityPage(string? SessionId, long NextSequence, long HighWater,
    bool Gap, long? EarliestAvailableSequence, IReadOnlyList<ExtensionActivityEvent> Items);

public static class ExtensionProtocol
{
    public const string Version = "1.0";
    public const int MaximumFrameBytes = 262144;
    public const int MaximumItems = 256;
    public static string Encode(ExtensionView value) => JsonSerializer.Serialize(value, ExtensionJson.Default.ExtensionView);
    public static ExtensionView Decode(string json)
    {
        var value = JsonSerializer.Deserialize(json, ExtensionJson.Default.ExtensionView) ?? throw new JsonException("extension_view_invalid");
        if (string.IsNullOrWhiteSpace(value.Id) || value.Id.Length > 240 || string.IsNullOrWhiteSpace(value.ExtensionId)
            || value.Revision is < 0 or > 9007199254740991 || value.State is not ("ready" or "partial" or "unsupported" or "error")
            || value.Kind is not ("tree" or "list" or "table" or "form" or "indicator" or "details")
            || value.Items is null || value.Actions is null || value.Items.Count > MaximumItems || value.Actions.Count > MaximumItems
            || value.Items.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 1024
                || item.Label is null || item.Label.Length > 2048 || item.Cells?.Count > 32)
            || value.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != value.Items.Count
            || value.Actions.Any(action => action is null || string.IsNullOrWhiteSpace(action.Id) || action.Id.Length > 128
                || action.Label is null || action.Label.Length > 2048)
            || value.RemovedIds?.Count > MaximumItems || value.ItemOrder?.Count > MaximumItems
            || value.ItemOrder?.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 1024) == true
            || value.ItemOrder is { } order && order.Distinct(StringComparer.Ordinal).Count() != order.Count)
            throw new JsonException("extension_view_invalid");
        if (value.Columns?.Count > 32 || value.Fields?.Count > 64 || value.Indicators?.Count > 64
            || value.Columns?.Any(column => column is null || string.IsNullOrWhiteSpace(column.Id) || column.Id.Length > 80 || column.Label is null || column.Label.Length > 240
                || column.ValueType is not ("text" or "number" or "boolean") || column.Alignment is not ("left" or "right" or "center")) == true
            || value.Fields?.Any(field => field is null || string.IsNullOrWhiteSpace(field.Id) || field.Id.Length > 80 || field.Label is null || field.Label.Length > 240
                || field.Kind is not ("text" or "number" or "boolean" or "choice") || field.Options?.Count > 128) == true
            || value.Indicators?.Any(indicator => indicator is null || string.IsNullOrWhiteSpace(indicator.Id) || indicator.Id.Length > 80 || indicator.Label is null || indicator.Label.Length > 240
                || indicator.State is not ("unknown" or "ready" or "active" or "warning" or "error")) == true)
            throw new JsonException("extension_view_schema_invalid");
        if (value.Fields?.Select(field => field.Id).Distinct(StringComparer.Ordinal).Count() != value.Fields?.Count
            || value.Indicators?.Select(indicator => indicator.Id).Distinct(StringComparer.Ordinal).Count() != value.Indicators?.Count
            || value.Columns?.Select(column => column.Id).Distinct(StringComparer.Ordinal).Count() != value.Columns?.Count
            || value.Actions.Select(action => (action.Id, action.ItemId)).Distinct().Count() != value.Actions.Count
            || value.Fields?.Any(field => !ValidFieldValue(field, field.Value, requireValue: false)
                || field.Options?.Any(option => option is null || option.Length > 240 || option.Any(char.IsControl)) == true
                || field.Options?.Distinct(StringComparer.Ordinal).Count() != field.Options?.Count
                || field.Kind == "choice" && field.Options is not { Count: > 0 }) == true
            || value.Actions.Any(action => action.ConfigurationScope is not ("workspace" or "user" or "session")
                || action.ConfigurationKey is { } key && (key.Length is < 1 or > 80 || !action.RequiresUserAuthority
                    || value.Kind != "form" || action.ItemId is not null || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')))))
            throw new JsonException("extension_view_schema_invalid");
        return value;
    }
    public static void ValidateFormValues(ExtensionView view, JsonElement values)
    {
        if (view.Kind != "form" || view.Fields is null || values.ValueKind != JsonValueKind.Object
            || System.Text.Encoding.UTF8.GetByteCount(values.GetRawText()) > 8192) throw new ArgumentException("extension.form.invalid");
        var fields = view.Fields.ToDictionary(field => field.Id, StringComparer.Ordinal);
        var properties = values.EnumerateObject().ToArray();
        if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length
            || properties.Any(property => !fields.ContainsKey(property.Name))) throw new ArgumentException("extension.form.invalid");
        foreach (var field in view.Fields)
        {
            var present = values.TryGetProperty(field.Id, out var value);
            if (!ValidFieldValue(field, present ? value : null, requireValue: true)) throw new ArgumentException("extension.form.invalid");
            if (field.ReadOnly && (!present || !SameFieldValue(field.Value, value))) throw new ArgumentException("extension.form.read_only");
        }
    }
    private static bool SameFieldValue(JsonElement? previous, JsonElement value) => previous is null
        ? value.ValueKind == JsonValueKind.Null : previous.Value.ValueKind == value.ValueKind
            && (value.ValueKind == JsonValueKind.String ? previous.Value.GetString() == value.GetString()
                : value.ValueKind == JsonValueKind.Number ? previous.Value.TryGetDecimal(out var before) && value.TryGetDecimal(out var after) && before == after
                : previous.Value.GetRawText() == value.GetRawText());
    private static bool ValidFieldValue(ExtensionViewField field, JsonElement? value, bool requireValue)
    {
        if (value is null || value.Value.ValueKind == JsonValueKind.Null) return !requireValue || !field.Required;
        var item = value.Value;
        return field.Kind switch
        {
            "text" => item.ValueKind == JsonValueKind.String && item.GetString()!.Length <= 4096
                && (!requireValue || !field.Required || !string.IsNullOrWhiteSpace(item.GetString())) && !item.GetString()!.Any(char.IsControl),
            "number" => item.ValueKind == JsonValueKind.Number && item.TryGetDecimal(out _),
            "boolean" => item.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "choice" => item.ValueKind == JsonValueKind.String && field.Options?.Contains(item.GetString()!, StringComparer.Ordinal) == true,
            _ => false,
        };
    }
    public static string EncodeRequest(ExtensionViewRequest value) => JsonSerializer.Serialize(value, ExtensionJson.Default.ExtensionViewRequest);
    public static ExtensionViewRequest DecodeRequest(string json)
    {
        var value = JsonSerializer.Deserialize(json, ExtensionJson.Default.ExtensionViewRequest) ?? throw new JsonException("extension_view_request_invalid");
        if (string.IsNullOrWhiteSpace(value.ExtensionId) || value.ExtensionId.Length > 120 || string.IsNullOrWhiteSpace(value.ViewId)
            || value.ViewId.Length > 80 || value.Path is null || value.Path.Length > 1024 || value.Search is null || value.Search.Length > 256
            || value.ClientKinds?.Count > 6 || value.AfterRevision is < 0) throw new JsonException("extension_view_request_invalid");
        return value;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ExtensionView))]
[JsonSerializable(typeof(ExtensionViewRequest))]
[JsonSerializable(typeof(ExtensionPackageState))]
[JsonSerializable(typeof(ExtensionPackageState[]))]
[JsonSerializable(typeof(ExtensionInitialize))]
[JsonSerializable(typeof(ExtensionActivityPage))]
[JsonSerializable(typeof(ExtensionFormSubmission))]
[JsonSerializable(typeof(ExtensionContextPreview))]
[JsonSerializable(typeof(ExtensionContextPreview[]))]
[JsonSerializable(typeof(ExtensionNotificationPage))]
public partial class ExtensionJson : JsonSerializerContext;
