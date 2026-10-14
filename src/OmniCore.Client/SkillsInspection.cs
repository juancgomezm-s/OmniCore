using System.Text.Json;

namespace OmniCore.Client;

public sealed record SkillInspectionRow(string Id, string Scope, string Trust, string State,
    int BudgetTokens, bool ModelInvocable, bool HasDiagnostic, string ActivationOrigin = "None");

public static class SkillsInspectionJson
{
    public static IReadOnlyList<SkillInspectionRow>? Decode(string? json)
    {
        if (json is null) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (!document.RootElement.TryGetProperty("skills", out var skills)
                || skills.ValueKind != JsonValueKind.Array) return null;
            var rows = new List<SkillInspectionRow>();
            foreach (var skill in skills.EnumerateArray())
            {
                if (skill.ValueKind != JsonValueKind.Object
                    || Text(skill, "id") is not { Length: > 0 } id
                    || Text(skill, "scope") is not { Length: > 0 } scope
                    || Text(skill, "trust") is not { Length: > 0 } trust
                    || Text(skill, "state") is not { Length: > 0 } state
                    || !skill.TryGetProperty("budgetTokens", out var budget) || !budget.TryGetInt32(out var budgetTokens)
                    || !skill.TryGetProperty("modelInvocable", out var modelInvocable)
                    || modelInvocable.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
                rows.Add(new(id, scope, trust, state, budgetTokens, modelInvocable.GetBoolean(),
                    skill.TryGetProperty("diagnostic", out var diagnostic) && diagnostic.ValueKind == JsonValueKind.String
                        && diagnostic.GetString() is { Length: > 0 },
                    Text(skill, "activationOrigin") ?? "None"));
            }
            return rows.AsReadOnly();
        }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}

public static class SkillsInspectionPresentation
{
    public static string Format(string? json, Localization text)
    {
        var rows = SkillsInspectionJson.Decode(json);
        if (rows is null) return text.Resolve("client.skills.data_invalid");
        if (rows.Count == 0) return text.Resolve("client.skills.empty");
        var output = new List<string> { text.Resolve("client.skills.title") };
        foreach (var row in rows)
        {
            var detail = text.Resolve("client.skills.scope." + row.Scope) + " · "
                + text.Resolve("client.skills.trust." + row.Trust) + " · "
                + text.Resolve("client.skills.state." + row.State) + " · "
                + (row.ActivationOrigin == "Automatic" ? text.Resolve("client.skills.activation.automatic") + " · " : "")
                + text.Resolve("client.skills.budget", "tokens", row.BudgetTokens.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)) + " · "
                + text.Resolve(row.ModelInvocable ? "client.skills.model.yes" : "client.skills.model.no");
            if (row.HasDiagnostic) detail += " · " + text.Resolve("client.skills.diagnostic");
            output.Add(row.Id + " — " + detail);
        }
        return string.Join("\n", output);
    }
}
