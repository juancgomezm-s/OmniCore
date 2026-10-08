using OmniCore.Protocol;

namespace OmniCore.Client;

public sealed class ChangedFilesProjection
{
    public string? SessionId { get; private set; }
    public ChangedFilesSnapshot? Snapshot { get; private set; }
    public void Activate(string? session)
    { if (session == SessionId) return; SessionId = session; Snapshot = null; }
    public bool Apply(ChangedFilesSnapshot snapshot)
    {
        if (snapshot.SessionId != SessionId || snapshot.BasedOnJournalSequence < (Snapshot?.BasedOnJournalSequence ?? 0)) return false;
        Snapshot = snapshot; return true;
    }
    public bool Poll(IOmniClient client, CancellationToken token = default)
    { var query = client.Query("changedFiles", token); return query is not null && FilesJson.Decode(query.Json) is { } snapshot && Apply(snapshot); }
    public ISidebarWidget Widget(string locale) => new FilesWidget(Snapshot, locale);

    private sealed class FilesWidget(ChangedFilesSnapshot? snapshot, string locale) : ISidebarWidget
    {
        public string Id => "core.files";
        public string Title => locale == "en" ? "FILES · effects" : "ARCHIVOS · efectos";
        public ThemeRole Accent => ThemeRole.Info;
        public int DefaultPriority => 50;
        public WidgetRelevance Evaluate(ClientState state) => snapshot?.Unavailable == true || snapshot?.Files.Any(f => f.Status == "?") == true
            ? WidgetRelevance.Attention : snapshot?.Files.Count > 0 ? WidgetRelevance.Normal : WidgetRelevance.None;
        public WidgetModel Build(ClientState state, WidgetSize size)
        {
            if (snapshot?.Unavailable == true) return new ListWidgetModel(Title, [new(locale == "en" ? "Unavailable" : "No disponible", ThemeRole.Error)]);
            var groups = (snapshot?.Files ?? []).GroupBy(f => (f.Path, f.LaneId)).ToArray();
            var rows = new List<WidgetRowModel>();
            static WidgetRowModel Row(ChangedFile f, string suffix = "") => new(f.Status + " " + f.Path
                + (f.Added is { } a && f.Deleted is { } d ? $" +{a} -{d}" : " · —")
                + (!f.DiffAvailable ? " · " + f.UnavailableReason : "") + suffix, f.Status == "?" ? ThemeRole.Attention : ThemeRole.Info,
                "diff.open", f.EffectId);
            foreach (var group in groups)
            {
                var effects = group.ToArray();
                rows.Add(Row(effects[^1], effects.Length > 1 ? $" · {effects.Length}" + (locale == "en" ? " effects · latest" : " efectos · último") : ""));
                if (size == WidgetSize.Expanded) foreach (var effect in effects[..^1]) rows.Add(Row(effect, locale == "en" ? " · earlier" : " · anterior"));
            }
            return new ListWidgetModel(Title + " " + groups.Length, rows);
        }
    }
}

/// <summary>Bounded, ordered line diff. Replacements preserve both sides, including newline diagnostics.</summary>
public static class FileDiffPresentation
{
    public static ListWidgetModel Build(FileDiffSnapshot diff, int maximumRows = 1200)
    {
        if (!diff.Available) return new(diff.Path, [new(diff.Reason ?? "Diff unavailable", ThemeRole.Attention)]);
        var before = Lines(diff.Before!); var after = Lines(diff.After!);
        var rows = new List<WidgetRowModel>();
        var prefix = 0;
        while (prefix < Math.Min(before.Length, after.Length) && before[prefix] == after[prefix]) prefix++;
        var suffix = 0;
        while (suffix < Math.Min(before.Length, after.Length) - prefix
            && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
        if (prefix == before.Length && prefix == after.Length)
            return new(diff.Path, [new(diff.Before == diff.After ? "Sin cambios de texto · No text changes"
                : "Sólo finales de línea · Line endings only", ThemeRole.Muted)]);
        var start = Math.Max(0, prefix - 3);
        var trailing = Math.Min(3, suffix);
        rows.Add(new($"@@ -{(before.Length == 0 ? 0 : start + 1)},{before.Length - suffix - start + trailing} +{(after.Length == 0 ? 0 : start + 1)},{after.Length - suffix - start + trailing} @@", ThemeRole.Info));
        for (var i = start; i < prefix; i++) rows.Add(new("  " + before[i], ThemeRole.Primary));
        for (var i = prefix; i < before.Length - suffix; i++) rows.Add(new("- " + before[i], ThemeRole.Error));
        for (var i = prefix; i < after.Length - suffix; i++) rows.Add(new("+ " + after[i], ThemeRole.Success));
        for (var i = 0; i < Math.Min(3, suffix); i++) rows.Add(new("  " + after[after.Length - suffix + i], ThemeRole.Primary));
        if (diff.Before!.Length > 0 && !diff.Before.EndsWith('\n')) rows.Add(new("\\ No newline at end of before", ThemeRole.Muted));
        if (diff.After!.Length > 0 && !diff.After.EndsWith('\n')) rows.Add(new("\\ No newline at end of after", ThemeRole.Muted));
        if (rows.Count > maximumRows) rows = rows.Take(Math.Max(1, maximumRows - 1)).Append(new("… Vista parcial · Partial preview", ThemeRole.Attention)).ToList();
        return new(diff.Path, rows);
    }
    private static string[] Lines(string text) => text.Length == 0 ? [] : (text.EndsWith('\n') ? text[..^1] : text).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => line.EndsWith('\r') ? line[..^1] : line).ToArray();
}
