using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

/// <summary>
/// Shared indentation for every menu and preferences page: each hierarchy level
/// moves two columns right, so sections and their controls never share a column.
/// </summary>
internal static class MenuIndent
{
    public const int Level0 = 2;
    public const int Level1 = 4;
    public const int Level2 = 6;

    public static int Of(int level) => Level0 + Math.Clamp(level, 0, 4) * 2;

    /// <summary>Section heading drawn in the accent color, one level above its content.</summary>
    public static Label Heading(string text, int y, int level = 0) => new()
    {
        Id = "omni-section", X = Of(level), Y = y, Width = Dim.Fill(2), Height = 1, Text = "│ " + text,
    };
}
