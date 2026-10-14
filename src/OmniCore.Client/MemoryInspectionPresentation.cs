namespace OmniCore.Client;

/// <summary>Locale-aware command view of Session memory metadata. Memory contents remain verbatim data.</summary>
public static class MemoryInspectionPresentation
{
    public static string Format(string json, Localization text)
    {
        var data = SessionPreferencesPresentation.Decode("memory.session", json);
        if (!data.Available) return text.Resolve("client.memory.empty");
        var output = new List<string> { text.Resolve("client.memory.heading") };
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
