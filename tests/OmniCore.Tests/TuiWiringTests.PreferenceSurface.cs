using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(80, 25, false)]
    [InlineData(120, 35, false)]
    [InlineData(80, 25, true)]
    [InlineData(120, 35, true)]
    public void Preferences_surface_is_distinct_from_the_open_sidebar_and_respects_no_color(
        int columns, int rows, bool noColor) => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: columns, initialRows: rows);
        if (!fx.App.Sidebar!.Visible)
            KeyWithEffect(fx, KeyCode.F2, () => fx.App.Sidebar.Visible, "sidebar visible");
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences visible beside sidebar");
        var preferences = fx.App.Overlay!;
        Assert.True(fx.App.Sidebar.Visible);
        Assert.Equal("preferences-center", preferences.Id);
        var expected = noColor ? Color.None : new Color("#13222E");
        Assert.Equal(expected, preferences.GetScheme().Normal.Background);
        Assert.Equal(noColor ? Color.None : new Color("#202C3B"), fx.App.Sidebar.GetScheme().Normal.Background);
        if (!noColor)
        {
            Assert.NotEqual(fx.App.Sidebar.GetScheme().Normal.Background, expected);
            var foreground = ThemeLuminance("#F4F7FB");
            var background = ThemeLuminance("#13222E");
            Assert.True((foreground + .05) / (background + .05) >= 4.5);
            ExportPreferenceFrame(fx, columns, rows, "surface-distinct", "Preferencias");
        }
        ChoosePreferenceCategory(fx, "general");
        Assert.Equal(expected, fx.App.Overlay!.GetScheme().Normal.Background);
        Assert.Equal(expected, Descendants(fx.App.Overlay!).Single(view => view.Id == "preferences-panel").GetScheme().Normal.Background);
        var language = Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "preferences-locale");
        Assert.NotEqual(language.GetScheme().Normal, language.GetScheme().Focus);
        if (noColor) Assert.True(language.GetScheme().Focus.Style.HasFlag(TextStyle.Reverse));
    }, noColor);
}
