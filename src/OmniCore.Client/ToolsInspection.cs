using System.Text.Json;

namespace OmniCore.Client;

/// <summary>Read-only diagnostic metadata reported by the Host, not execution authority.</summary>
public sealed record ToolInspectionRow(string VisibleName, string ToolId, string Source,
    string EffectClass, string Decision, bool Visible)
{
    /// <summary>Original catalog text, not a prompt or execution permission. Older hosts may omit it.</summary>
    public string? Description { get; init; }
    public IReadOnlyDictionary<string, string>? UiDescriptions { get; init; }
}

/// <summary>The complete tool catalog, independent of any terminal or desktop UI.</summary>
public sealed record ToolsInspection(string? Mode, IReadOnlyList<ToolInspectionRow> Tools);

/// <summary>Decodes the existing tools query without truncating or silently dropping rows.</summary>
public static class ToolsInspectionJson
{
    public static ToolsInspection? Decode(string? json)
    {
        if (json is null) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tools", out var tools)
                || tools.ValueKind != JsonValueKind.Array) return null;
            string? mode = null;
            if (root.TryGetProperty("mode", out var modeValue))
            {
                if (modeValue.ValueKind != JsonValueKind.String) return null;
                mode = modeValue.GetString();
            }
            var rows = new List<ToolInspectionRow>();
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.Object
                    || Text(tool, "visibleName") is not { } name || Text(tool, "toolId") is not { } id
                    || Text(tool, "source") is not { } source || Text(tool, "effectClass") is not { } effect
                    || Text(tool, "decision") is not { } decision
                    || !tool.TryGetProperty("visible", out var visible)
                    || visible.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
                rows.Add(new(name, id, source, effect, decision, visible.GetBoolean())
                    { Description = Text(tool, "description"), UiDescriptions = ExternalUiResources.ReadDescriptions(tool, "uiDescriptions") });
            }
            return new(mode, rows.AsReadOnly());
        }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}

/// <summary>Human explanations only; never changes model definitions or permission decisions.</summary>
public static class ToolInspectionPresentation
{
    public static string Description(ToolInspectionRow row, Localization text)
    {
        var key = "client.tool." + row.ToolId + ".description";
        // Do not attribute a built-in explanation to a plugin with a similar name.
        if (row.Source == "BuiltIn:BuiltIn:Core:core@1" && Localization.HasInBoth(key))
            return text.Resolve(key);
        if (row.UiDescriptions?.GetValueOrDefault(text.Locale) is { Length: > 0 } translation) return translation;
        return string.IsNullOrWhiteSpace(row.Description) ? text.Resolve("client.tools.description.missing")
            : text.Resolve("client.tools.description.original") + row.Description;
    }

    public static string Decision(ToolInspectionRow row, Localization text) => text.Resolve(row.Decision switch
    {
        "reject:PLAN mode" => "client.tools.policy.plan",
        "reject:ModelCapabilityBoundary" => "client.tools.policy.boundary",
        "ask:ModelCapabilityBoundary" => "client.tools.policy.boundary_ask",
        "ask:permission policy" => "client.tools.policy.ask",
        "permission policy" => "client.tools.policy.evaluate",
        _ => "client.tools.policy.unknown",
    });
}
