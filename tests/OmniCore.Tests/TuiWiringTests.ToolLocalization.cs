using OmniCore.Cli;
using OmniCore.Client;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Fact]
    public void Tools_opened_from_preferences_return_to_the_security_category() => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: 80, initialRows: 25);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences opened");
        ChoosePreferenceCategory(fx, "tools_security");
        ClickId(fx, "preferences-tools-open");
        fx.Wait(() => fx.App.Overlay?.SubViews.Any(view => view.Id == "tools-list") == true, "tools opened");
        ClickId(fx, "tools-close");
        fx.Wait(() => fx.App.Overlay is not null && Descendants(fx.App.Overlay)
            .Any(view => view.Id == "preferences-tools-open"), "security category restored");
    });

    [Theory]
    [InlineData("es", 80, 25)]
    [InlineData("en", 120, 35)]
    [InlineData("fr", 80, 25)]
    [InlineData("it", 80, 25)]
    public void Tool_preferences_locale_is_applied_to_the_complete_read_only_page(string locale, int width, int height) => RunTuiTest(fx =>
    {
        var text = new Localization(locale);
        fx.StartTui(initialColumns: width, initialRows: height,
            localeSelection: new(locale, LocaleSelectionSource.CommandLine));
        Type(fx, "/tools");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is not null, "tools opened");
        var overlay = fx.App.Overlay!;
        Assert.Equal(text.Resolve("tools.ui.title"), ((FrameView)overlay).Title?.Trim());
        Assert.Contains(text.Resolve("tools.ui.purpose"), OverlayText(overlay));
        Assert.Contains(text.Resolve("tools.ui.help"), OverlayText(overlay));
        Assert.Contains(text.Resolve("tools.ui.close"), OverlayText(overlay));
        var search = overlay.SubViews.OfType<TextField>().Single(view => view.Id == "tools-search");
        fx.Invoke(() => search.Text = "filesystem.read");
        var detail = overlay.SubViews.OfType<StyledTextPresenter>().Single(view => view.Id == "tools-detail");
        fx.Wait(() => detail.Text.Contains("filesystem.read", StringComparison.Ordinal), "filtered detail");
        Assert.Contains(text.Resolve("client.tool.filesystem.read.description"), detail.Text);
        Assert.Contains(text.Resolve("tools.ui.not_permission"), detail.Text);
        Assert.Contains(text.Resolve("tools.ui.control"), detail.Text);
        Assert.DoesNotContain("tools.ui.", OverlayText(overlay));
        Assert.All(overlay.SubViews, view => Assert.True(view.Frame.Bottom <= overlay.Viewport.Height));
        ExportPreferenceFrame(fx, width, height, "tools-" + locale, "filesystem.read");
        fx.Invoke(() => search.Text = "no such tool 314159");
        Assert.Equal(text.Resolve("tools.ui.no_matches"), detail.Text);
    });
}
