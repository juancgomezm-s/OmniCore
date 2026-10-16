using Terminal.Gui.Drawing;

namespace OmniCore.Cli;

/// <summary>Color substitution only: layout, text, focus and commands are independent of the palette.</summary>
internal sealed record TuiTheme(string Id, string Name, string Background, string Panel, string Preferences,
    string Menu, string Input, string Selection, string Accent, string Foreground, string Muted)
{
    internal static IReadOnlyList<TuiTheme> All { get; } =
    [
        new("omnicore", "OmniCore", "#061822", "#202C3B", "#13222E", "#182A38", "#293E51", "#3D5068", "#67D4D0", "#F4F7FB", "#B9CBDF"),
        new("opencode", "OpenCode", "#0A0A0A", "#141414", "#111111", "#191919", "#242424", "#454545", "#FAB283", "#EEEEEE", "#B0B0B0"),
        new("claude", "Claude", "#1F1E1D", "#292724", "#24221F", "#2F2C28", "#38342E", "#554A40", "#D97757", "#F0EEE6", "#C2B8A9"),
        new("codex", "Codex", "#101112", "#191B1D", "#151719", "#202326", "#2B2F33", "#424A52", "#A3D8CB", "#F2F3F5", "#B3B9C1"),
    ];

    internal static TuiTheme Find(string? id) => All.FirstOrDefault(theme => theme.Id == id) ?? All[0];
    private Dictionary<Color, Color>? _colors;

    internal Color Map(Color color)
    {
        if (Id == "omnicore" || color == Color.None) return color;
        _colors ??= ColorPairs().ToDictionary(pair => new Color(pair.Source), pair => new Color(pair.Target));
        return _colors.GetValueOrDefault(color, color);
    }

    private (string Source, string Target)[] ColorPairs() =>
        new (string, string)[]
        {
            ("#061822", Background), ("#202C3B", Panel), ("#13222E", Preferences), ("#182A38", Menu),
            ("#293E51", Input), ("#293648", Input), ("#3D5068", Selection), ("#102331", Background),
            ("#142636", Menu), ("#142C3B", Menu), ("#1B2836", Menu), ("#0B2332", Menu),
            ("#67D4D0", Accent), ("#85E6DF", Accent), ("#85D6F5", Accent), ("#38BDF8", Accent),
            ("#B47CE7", Accent), ("#F4F7FB", Foreground), ("#D6E2EE", Foreground),
            ("#B9CBDF", Muted), ("#B1C2D1", Muted), ("#405666", Selection), ("#365364", Selection),
            ("#D8E5ED", Foreground), ("#8B9DAC", Muted), ("#91A8B8", Muted), ("#ADC4D4", Muted),
            ("#53B8F5", Accent), ("#4DBAC9", Accent), ("#526A7C", Muted), ("#B5DEF3", Foreground), ("#0A2330", Menu),
        };

    internal Terminal.Gui.Drawing.Attribute Map(Terminal.Gui.Drawing.Attribute attribute) =>
        new(Map(attribute.Foreground), Map(attribute.Background), attribute.Style);
}

internal sealed class TuiThemeStore(string path)
{
    internal TuiTheme Read()
    {
        try { return TuiTheme.Find(File.Exists(path) ? File.ReadAllText(path).Trim() : null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return TuiTheme.All[0]; }
    }

    internal void Save(TuiTheme theme)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, theme.Id); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
