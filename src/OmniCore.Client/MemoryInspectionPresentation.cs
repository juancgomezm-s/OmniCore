namespace OmniCore.Client;

/// <summary>Locale-aware command view of Session memory metadata. Memory contents remain verbatim data.</summary>
public static class MemoryInspectionPresentation
{
    public static string Format(string json, Localization text)
    {
        string? scope = null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return text.Resolve("client.memory.empty");
            if (root.TryGetProperty("available", out var available) && available.ValueKind == System.Text.Json.JsonValueKind.False)
                return root.TryGetProperty("error", out var error) ? text.ResolveWire(error.GetString() ?? "client.no_data") : text.Resolve("client.no_data");
            if (root.TryGetProperty("scope", out var level) && level.ValueKind == System.Text.Json.JsonValueKind.String)
                scope = level.GetString();
        }
        catch (System.Text.Json.JsonException) { return text.Resolve("client.no_data"); }
        var data = SessionPreferencesPresentation.Decode("memory.session", json);
        if (!data.Available) return text.Resolve("client.memory.empty");
        var output = new List<string> { scope is "project" or "workspace" or "global"
            ? text.Resolve("client.memory.scoped_heading") + " · " + text.Resolve("client.memory.scope." + scope)
            : text.Resolve("client.memory.heading") };
        foreach (var memory in data.Memory.Take(50))
        {
            output.Add(text.Resolve("client.memory.record") + " " + SessionPreferencesPresentation.SafeText(memory.Id, 96)
                + " · " + text.Resolve("client.memory.state.promoted") + " · " + EnumLabel(text, "memory.kind", memory.Kind));
            output.Add(text.Resolve("client.memory.content") + ": "
                + SessionPreferencesPresentation.SafeText(memory.Content, 512));
        }
        foreach (var candidate in data.MemoryCandidates ?? [])
        {
            output.Add(text.Resolve("client.memory.candidate") + " "
                + SessionPreferencesPresentation.SafeText(candidate.Id, 96) + " · "
                + text.Resolve("client.memory.state." + candidate.Decision) + " · "
                + EnumLabel(text, "memory.kind", candidate.Kind) + " · "
                + text.Resolve("client.memory.source") + ": " + EnumLabel(text, "memory.origin", candidate.Origin));
            output.Add(text.Resolve("client.memory.content") + ": "
                + SessionPreferencesPresentation.SafeText(candidate.Content, 512));
        }
        if (output.Count == 1) output.Add(text.Resolve("client.memory.empty"));
        return string.Join("\n", output);
    }

    private static string EnumLabel(Localization text, string family, string value)
    {
        var key = "sessionPreferences.enum." + family + "." + value.ToLowerInvariant();
        var localized = text.ResolveDetailed(key);
        return localized.UsedFallback || localized.Text == key ? value : localized.Text;
    }
}
