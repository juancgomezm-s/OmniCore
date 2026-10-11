using System.Text.Json;

namespace OmniCore.Client;

/// <summary>Read-only diagnostic metadata reported by the Host, not execution authority.</summary>
public sealed record ToolInspectionRow(string VisibleName, string ToolId, string Source,
    string EffectClass, string Decision, bool Visible);

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
                rows.Add(new(name, id, source, effect, decision, visible.GetBoolean()));
            }
            return new(mode, rows.AsReadOnly());
        }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
