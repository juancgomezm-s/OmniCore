using System.Globalization;
using System.Text;
using OmniCore.Protocol;

namespace OmniCore.Client;

public sealed record CommandPaletteEntry(string Id, string Title, string Description, string Source,
    KeyBindingTarget Target, bool Enabled, string? DisabledReason = null,
    IReadOnlyList<string>? ArgumentHints = null, string? SourceFingerprint = null, bool ExternalDescription = false);

/// <summary>Portable, side-effect-free palette projection. Runtime authority stays in the Host.</summary>
public static class CommandPalette
{
    public static IReadOnlyList<CommandPaletteEntry> Build(string? catalogJson, Localization text,
        KeyBindingContext context, bool modelWorkActive)
    {
        var entries = new List<CommandPaletteEntry>();
        foreach (var command in ClientCommandRegistry.Default.Commands)
        {
            var enabled = command.ActionId is not (ClientActionIds.RunCancel or ClientActionIds.RunInterrupt) || context["runActive"];
            if (command.ActionId == ClientActionIds.WorkspaceUndo && modelWorkActive) enabled = false;
            entries.Add(new("action:" + command.ActionId, "/" + HumanCommandAliases.DisplayName(command.Name, text.Locale),
                text.Resolve(command.DescriptionKey), "Core", new(KeyBindingTargetKind.Action, command.ActionId), enabled,
                enabled ? null : text.Resolve(command.ActionId == ClientActionIds.WorkspaceUndo ? "palette.busy" : "palette.no_run")));
        }
        foreach (var id in new[] { "sidebar.toggle", "model.select", "preferences.open", "keybindings.open", "updates.open", "session.search", "agent.inspect", "mode.open", "permissions.open", "reasoning.open", "think.open" })
            entries.Add(new("action:" + id, text.Resolve("palette.action." + id), "", "Core", new(KeyBindingTargetKind.Action, id), true));
        foreach (var row in CommandCatalogPresentation.Read(catalogJson, text).Where(row => row.Id.Contains(':')).Take(512))
        {
            var enabled = (row.CanonicalEnabled ?? row.Enabled) && !modelWorkActive;
            entries.Add(new("command:" + row.Id, "/" + row.DisplayName + " · " + row.Id, row.Description, row.Source,
                new(KeyBindingTargetKind.Command, row.Id), enabled,
                enabled ? null : modelWorkActive ? text.Resolve("palette.busy") : row.DisabledReason,
                row.Arguments, row.SourceFingerprint, row.ExternalDescription));
        }
        // A malformed duplicate qualified identity is never silently selected.
        return entries.GroupBy(entry => entry.Id, StringComparer.Ordinal).Where(group => group.Count() == 1)
            .Select(group => group.Single()).ToArray();
    }

    public static IReadOnlyList<CommandPaletteEntry> Search(IEnumerable<CommandPaletteEntry> entries, string query)
    {
        query = Normalize(query.Length > 128 ? query[..128] : query);
        return entries.Select(entry => (Entry: entry, Score: Score(entry, query))).Where(item => item.Score >= 0)
            .OrderByDescending(item => item.Score).ThenBy(item => item.Entry.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Entry.Id, StringComparer.Ordinal).Select(item => item.Entry).ToArray();
    }

    public static IReadOnlyList<KeyBinding> Bindings(CommandPaletteEntry entry, KeyBindingService service, KeyBindingContext context) =>
        service.Bindings.Where(binding => service.Resolve(binding.Sequence, context) is { Status: KeyBindingResolutionStatus.Matched, Binding: { } winner }
            && ReferenceEquals(binding, winner) && winner.Definition.Target.SameAs(entry.Target))
            .OrderByDescending(binding => binding.Definition.Source).ThenBy(binding => binding.Definition.Keys, StringComparer.Ordinal).ToArray();

    private static int Score(CommandPaletteEntry entry, string query)
    {
        if (query.Length == 0) return 0;
        var title = Normalize(entry.Title + " " + entry.Target.Id);
        var haystack = title + " " + Normalize(entry.Description + " " + entry.Source + " " + entry.Id);
        var score = 0;
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (title.Contains(word, StringComparison.Ordinal)) { score += 10000 - title.IndexOf(word, StringComparison.Ordinal); continue; }
            if (haystack.Contains(word, StringComparison.Ordinal)) { score += 100; continue; }
            var at = 0; var previous = -2;
            foreach (var letter in word)
            {
                var next = haystack.IndexOf(letter, at);
                if (next < 0) return -1;
                score += next == previous + 1 ? 4 : 1; previous = next; at = next + 1;
            }
        }
        return score;
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToUpperInvariant(character));
        return builder.ToString();
    }
}
