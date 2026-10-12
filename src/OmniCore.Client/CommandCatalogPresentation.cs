using System.Text.Json;
using OmniCore.Protocol;

namespace OmniCore.Client;

public sealed record CommandMenuRow(string Id, string Name, string DisplayName, string Description,
    string Source, bool Enabled, string? DisabledReason, IReadOnlyList<string> Arguments,
    bool ExternalDescription);

/// <summary>Human command catalog. Browsing and composing never invokes a command.</summary>
public static class CommandCatalogPresentation
{
    /// <summary>Localizes only the built-in source label; extension/provider source IDs stay exact.</summary>
    public static string DisplaySource(CommandMenuRow row, Localization text) => row.Source == "Core"
        ? text.Resolve("uiCompletion.command.coreSource") : row.Source;

    public static IReadOnlyList<CommandMenuRow> Read(string? json, Localization text)
    {
        var rows = ClientCommandRegistry.Default.Commands.Select(command => new CommandMenuRow(command.ActionId,
            command.Name, HumanCommandAliases.DisplayName(command.Name, text.Locale), text.Resolve(command.DescriptionKey),
            "Core", true, null, [], false)).ToList();
        foreach (var (name, key) in new[] { ("preferences", "shell.open_settings"), ("models", "shell.select_model"),
            ("animation", "shell.activity_and_preview"), ("sidebar", "shell.toggle_workspace_panel") })
            rows.Add(new(name, name, HumanCommandAliases.DisplayName(name, text.Locale), text.Resolve(key), "Core", true, null, [], false));
        try
        {
            if (json is null) return rows;
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return rows;
            if (root.TryGetProperty("catalog", out var catalog)) root = catalog;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("commands", out var commands) || commands.ValueKind != JsonValueKind.Array)
                return rows;
            foreach (var command in commands.EnumerateArray())
            {
                if (command.ValueKind != JsonValueKind.Object) continue;
                var id = String(command, "id"); var name = String(command, "name");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                var builtin = id is "core:explain" or "core:orq-auth";
                var descriptions = ExternalUiResources.ReadDescriptions(command, "uiDescriptions");
                var localized = descriptions.TryGetValue(text.Locale, out var translation) ? Clean(translation) : "";
                var description = builtin ? text.Resolve(id == "core:explain" ? "catalog.explain" : "catalog.workflow")
                    : localized.Length > 0 ? localized : String(command, "description");
                // On a short-name conflict keep the exact canonical id visible and available for explicit composition.
                var enabled = command.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
                var args = command.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => Clean(v.GetString() ?? "")).ToArray() : [];
                rows.Add(new(id, name, builtin ? HumanCommandAliases.DisplayName(name, text.Locale) : name, description, String(command, "source"), enabled,
                    DescribeReason(String(command, "disabledReason"), text), args, !builtin && localized.Length == 0));
            }
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return rows;
    }

    private static string String(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? Clean(value.GetString() ?? "") : "";
    private static string Clean(string value) => new(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    public static string DescribeReason(string reason, Localization text) => text.ResolveWire(reason switch
    {
        "A more specific command source wins this short name." => "catalog.reason.shadowed",
        "This short name is ambiguous across command sources; use its canonical id." => "catalog.reason.ambiguous",
        "An active Act Run is required to invoke an extension tool command." => "catalog.reason.act",
        "An active Orchestrate Run with current authorization is required." => "shell.requires_an_active_orchestrate_run_and_current_authorization",
        _ => reason,
    });
}
