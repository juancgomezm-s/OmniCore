namespace OmniCore.Tests;

using OmniCore.Client;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

public sealed partial class TuiWiringTests
{
    [Fact]
    public void Preferences_keep_keyboard_focus_visible_without_color() => RunTuiTest(fx =>
    {
        var previous = Environment.GetEnvironmentVariable("NO_COLOR");
        try
        {
            Environment.SetEnvironmentVariable("NO_COLOR", "1");
            fx.StartTui(initialColumns: 80, initialRows: 25);
            KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences visible");
            foreach (var view in Descendants(fx.App.Overlay!).Where(view => view is Button or ListView or TextField))
            {
                Assert.True(view.GetScheme().Focus.Style.HasFlag(Terminal.Gui.Drawing.TextStyle.Reverse), view.Id);
                Assert.NotEqual(view.GetScheme().Normal, view.GetScheme().Focus);
            }
        }
        finally { Environment.SetEnvironmentVariable("NO_COLOR", previous); }
    });

    [Theory]
    [InlineData("es", "/preferencias")]
    [InlineData("en", "/preferences")]
    [InlineData("fr", "/préférences")]
    [InlineData("it", "/preferenze")]
    public void Localized_preferences_command_opens_the_same_center_and_catalog_is_read_only(string locale, string command)
        => RunTuiTest(fx =>
    {
        fx.StartTui(localeSelection: new LocaleSelection(locale, LocaleSelectionSource.CommandLine));
        Type(fx, command);
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is not null, "preferences visible");
        ChoosePreferenceCategory(fx, "extensions_skills", locale);
        ClickId(fx, "preferences-commands-open");
        fx.Wait(() => fx.App.Overlay?.Id == "command-preferences", "command catalog visible");
        var filter = fx.App.Overlay!.SubViews.OfType<TextField>().Single();
#pragma warning disable CS0618 // Matches the pinned detail viewer under test.
        var detail = fx.App.Overlay.SubViews.OfType<TextView>().Single();
#pragma warning restore CS0618
        fx.Invoke(() => filter.Text = "core:explain");
        fx.Wait(() => detail.Text.ToString().Contains("core:explain", StringComparison.Ordinal), "real Host catalog entry visible");
        Assert.Contains(new Localization(locale).Resolve("catalog.explain"), detail.Text.ToString());
        Assert.Equal("", fx.App.Composer!.Text.ToString());
        fx.Invoke(() => fx.App.Composer.Text = "unsent user draft");
        ClickId(fx, "commands-prepare");
        Assert.Equal("unsent user draft", fx.App.Composer.Text.ToString());
        Assert.Equal("command-preferences", fx.App.Overlay?.Id);
        Assert.Contains(new Localization(locale).Resolve("catalog.draft_exists"), detail.Text.ToString());
        fx.Invoke(() => fx.App.Composer.Text = "");
        ClickId(fx, "commands-prepare");
        Assert.Null(fx.App.Overlay);
        Assert.Equal("/" + OmniCore.Protocol.HumanCommandAliases.DisplayName("explain", locale) + " ", fx.App.Composer.Text.ToString());
    });
}
