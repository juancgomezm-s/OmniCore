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
        new("claude", "Claude Light", "#FAF9F5", "#F0EEE6", "#F5F3ED", "#EAE7DF", "#FFFFFF", "#E4D5C5", "#A84325", "#262624", "#625E55"),
        new("codex", "Codex", "#101112", "#191B1D", "#151719", "#202326", "#2B2F33", "#424A52", "#A3D8CB", "#F2F3F5", "#B3B9C1"),
        new("claude-dark", "Claude Dark", "#262624", "#30302E", "#2B2B29", "#363633", "#3E3E38", "#565047", "#D97757", "#FAF9F5", "#C2B8A9"),
        new("omni-llama", "OmniLlama", "#031826", "#021B2A", "#062133", "#001522", "#062133", "#315E73", "#23C9EE", "#D6E4FF", "#A7BFD8"),
        new("omni-llama-light", "OmniLlama Light", "#F7F8FB", "#FFFFFF", "#F2F3F7", "#ECEEF4", "#FFFFFF", "#D5DBE5", "#087F9E", "#1A1A22", "#3C3C48"),
        new("nord", "Nord", "#2E3440", "#3B4252", "#343B49", "#3B4252", "#434C5E", "#4C566A", "#88C0D0", "#ECEFF4", "#D8DEE9"),
        new("dracula", "Dracula", "#282A36", "#21222C", "#282A36", "#343746", "#343746", "#44475A", "#BD93F9", "#F8F8F2", "#BCC2DD"),
        new("catppuccin-mocha", "Catppuccin Mocha", "#1E1E2E", "#181825", "#232334", "#313244", "#313244", "#45475A", "#CBA6F7", "#CDD6F4", "#BAC2DE"),
        new("catppuccin-latte", "Catppuccin Latte", "#EFF1F5", "#E6E9EF", "#EAECEF", "#DCE0E8", "#FFFFFF", "#CCD0DA", "#8839EF", "#4C4F69", "#5C5F77"),
        new("tokyo-night", "Tokyo Night", "#1A1B26", "#16161E", "#1F2335", "#24283B", "#292E42", "#414868", "#7AA2F7", "#C0CAF5", "#A9B1D6"),
        new("gruvbox", "Gruvbox", "#282828", "#1D2021", "#32302F", "#3C3836", "#504945", "#665C54", "#FABD2F", "#FBF1C7", "#D5C4A1"),
        new("solarized-dark", "Solarized Dark", "#002B36", "#073642", "#073642", "#0A3D49", "#134854", "#245764", "#2AA198", "#EEE8D5", "#A8B9B8"),
        new("solarized-light", "Solarized Light", "#FDF6E3", "#EEE8D5", "#F5EEDB", "#E8DFC8", "#FFFBF0", "#D8D0B9", "#076C93", "#394D54", "#4B6269"),
        new("rose-pine", "Rosé Pine", "#191724", "#1F1D2E", "#232031", "#26233A", "#312D46", "#403D52", "#EBBCBA", "#E0DEF4", "#C0BAD3"),
    ];

    internal static TuiTheme Find(string? id) => All.FirstOrDefault(theme => theme.Id == id) ?? All[0];
    private Dictionary<Color, Color>? _colors;
    internal bool IsLight => Luminance(Background) > .5;

    // Focus text must contrast with its actual fill, including light palettes.
    internal static Color On(string background) => new(Luminance(background) > .179 ? "#111111" : "#FFFFFF");
    private static double Luminance(string hex)
    {
        var color = new Color(hex);
        static double Linear(byte value) { var c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

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
            ("#FFFFFF", Foreground), ("#29404E", Muted),
            ("#EF9A70", IsLight ? "#9B3E0B" : "#EF9A70"),
            ("#C8A0F5", IsLight ? "#6F34A3" : "#C8A0F5"),
            ("#F1D58A", IsLight ? "#795400" : "#F1D58A"),
            ("#A6D99B", IsLight ? "#256531" : "#A6D99B"),
            ("#FF9D9D", IsLight ? "#AC2430" : "#FF9D9D"),
            ("#D6B5FA", IsLight ? "#7430A1" : "#D6B5FA"),
            ("#D99955", IsLight ? "#86520F" : "#D99955"),
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
