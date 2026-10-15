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

public static class ExternalAgentSourcesPresentation
{
    public static string Format(string? json, Localization text)
    {
        if (json is null) return text.Resolve("client.agent_sources.invalid");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (!root.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
                return text.Resolve("client.agent_sources.invalid");
            var output = new List<string> { text.Resolve("client.agent_sources.title") };
            var allowed = root.TryGetProperty("externalMemoryAllowed", out var consent)
                && consent.ValueKind == JsonValueKind.True;
            var projectAllowed = root.TryGetProperty("externalProjectMemoryAllowed", out var projectConsent)
                && projectConsent.ValueKind == JsonValueKind.True;
            output.Add(text.Resolve(allowed ? "client.agent_sources.memory.global_allowed" : "client.agent_sources.memory.global_ask"));
            output.Add(text.Resolve(projectAllowed ? "client.agent_sources.memory.project_allowed" : "client.agent_sources.memory.project_ask"));
            foreach (var source in sources.EnumerateArray())
            {
                if (source.ValueKind != JsonValueKind.Object) return text.Resolve("client.agent_sources.invalid");
                var kind = Text(source, "kind");
                var adapter = Text(source, "adapter");
                var scope = Text(source, "scope");
                var path = Text(source, "path");
                var state = Text(source, "state");
                if (kind is null || adapter is null || scope is null || path is null || state is null)
                    return text.Resolve("client.agent_sources.invalid");
                var hash = Text(source, "sha256");
                var id = Text(source, "skillId");
                var diagnostic = Text(source, "diagnostic");
                output.Add(text.Resolve("client.agent_sources.row", new Dictionary<string, string>
                {
                    ["kind"] = text.Resolve("client.agent_sources.kind." + kind),
                    ["adapter"] = adapter,
                    ["scope"] = text.Resolve("client.agent_sources.scope." + scope),
                    ["path"] = path,
                    ["state"] = text.Resolve("client.agent_sources.state." + state),
                    ["identity"] = id is null ? (hash is { Length: >= 12 } ? hash[..12] : "") : id,
                }));
                if (diagnostic is { Length: > 0 }) output.Add("  " + text.Resolve("client.agent_sources.diagnostic") + ": " + diagnostic);
            }
            if (sources.GetArrayLength() == 0) output.Add(text.Resolve("client.agent_sources.empty"));
            output.Add(text.Resolve("client.agent_sources.memory.controls.scoped"));
            return string.Join("\n", output);
        }
        catch (JsonException) { return text.Resolve("client.agent_sources.invalid"); }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
