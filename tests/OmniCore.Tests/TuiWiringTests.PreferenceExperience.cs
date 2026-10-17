using OmniCore.Client;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Wide_preferences_show_fields_immediately_and_keep_category_navigation(string locale) => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: 120, initialRows: 35,
            localeSelection: new LocaleSelection(locale, LocaleSelectionSource.CommandLine));
        fx.Invoke(() => fx.App.Composer!.Text = "keep draft");
        var journalCount = fx.Server.SubscribeSince(0).Count;
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences open");
        var frame = fx.App.Overlay!;
        Assert.Contains(Descendants(frame), view => view.Id == "preferences-locale");
        Assert.DoesNotContain(Descendants(frame), view => view.Id == "preferences-open-category");
        AssertPreferencesGeneralLayoutFit(fx);
        var navigation = Descendants(frame).OfType<ListView>().Single(view => view.Id == "preferences-categories");
        Assert.Equal(PreferenceCategoryCatalog.All.Count, navigation.Source!.Count);
        ExportPreferenceFrame(fx, 120, 35, "integrated-general-" + locale,
            new Localization(locale).Resolve("preferences.ui.animations_open"));

        ChoosePreferenceCategory(fx, "context_sessions", locale);
        Assert.Same(frame, fx.App.Overlay);
        Assert.Same(navigation, Descendants(frame).Single(view => view.Id == "preferences-categories"));
        Assert.Contains(Descendants(frame), view => view.Id == "preferences-context-open");
        Assert.Contains(Descendants(frame), view => view.Id == "preferences-memory-open");
        Assert.DoesNotContain(Descendants(frame), view => view.Id == "preferences-diagnostics-open");
        ChoosePreferenceCategory(fx, "diagnostics_data", locale);
        Assert.Same(frame, fx.App.Overlay);
        Assert.Contains(Descendants(frame), view => view.Id == "preferences-diagnostics-open");
        Assert.DoesNotContain(Descendants(frame), view => view.Id == "preferences-memory-open");
        Assert.Equal(journalCount, fx.Server.SubscribeSince(0).Count);
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "Escape closes integrated preferences");
        Assert.Equal("keep draft", fx.App.Composer!.Text.ToString());
    }, noColor: false);

    [Fact]
    public void Wide_preferences_preserve_locale_draft_while_filtering_and_switching_categories() => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: 120, initialRows: 35);
        var saved = UiLocalePreferencesProtocol.Decode(fx.Server.Query(UiLocalePreferencesProtocol.QueryName, default)!.Json)!;
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences open");
        var locales = Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "preferences-locale");
        ChooseLocaleDraft(fx, 2);
        Assert.Equal("es", fx.App.PreferencesLocale);
        var frame = fx.App.Overlay!;
        var search = Descendants(frame).OfType<TextField>().Single(view => view.Id == "preferences-search");
        var categoryNavigation = Descendants(frame).OfType<ListView>().Single(view => view.Id == "preferences-categories");
        fx.Invoke(() => search.Text = "no-such-setting");
        Assert.Same(frame, fx.App.Overlay);
        Assert.True(Descendants(frame).Single(view => view.Id == "preferences-empty-search").Visible);
        Assert.False(categoryNavigation.Visible);
        Assert.Equal("fr", fx.App.PreferencesDraftLocale);
        fx.Invoke(() => search.Text = "");
        ChoosePreferenceCategory(fx, "context_sessions");
        ChoosePreferenceCategory(fx, "general");
        Assert.Equal("es", fx.App.PreferencesLocale);
        Assert.Equal(2, Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "preferences-locale").SelectedItem);
        var afterNavigation = UiLocalePreferencesProtocol.Decode(fx.Server.Query(UiLocalePreferencesProtocol.QueryName, default)!.Json)!;
        Assert.Equal(saved.Revision, afterNavigation.Revision);
        Assert.Equal(saved.Locale, afterNavigation.Locale);
        Assert.Equal(saved.IsConfigured, afterNavigation.IsConfigured);
        Assert.Equal(saved.Diagnostics, afterNavigation.Diagnostics);
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is null, "Escape cancels draft and closes");
        Assert.Equal("es", fx.App.PreferencesLocale);
        var afterCancel = UiLocalePreferencesProtocol.Decode(fx.Server.Query(UiLocalePreferencesProtocol.QueryName, default)!.Json)!;
        Assert.Equal(saved.Revision, afterCancel.Revision);
        Assert.Equal(saved.Locale, afterCancel.Locale);
        Assert.Equal(saved.IsConfigured, afterCancel.IsConfigured);
        Assert.Equal(saved.Diagnostics, afterCancel.Diagnostics);
    });

    [Fact]
    public void Integrated_preferences_scroll_fields_in_a_short_terminal_and_search_by_keyboard() => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: 100, initialRows: 25);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "integrated preferences open");
        var panel = Descendants(fx.App.Overlay!).Single(view => view.Id == "preferences-panel");
        var lastField = Descendants(panel).OfType<Button>().Single(view => view.Id == "preferences-animations-open");
        fx.Wait(() => panel.Viewport.Height > 0 && lastField.Frame.Height > 0, "fields laid out");
        fx.Invoke(() => lastField.SetFocus());
        fx.Wait(() => lastField.HasFocus && panel.Viewport.Bottom >= lastField.Frame.Bottom,
            "keyboard focus scrolls the last field into view");
        KeyWithEffect(fx, KeyCode.F | KeyCode.CtrlMask,
            () => Descendants(fx.App.Overlay!).Single(view => view.Id == "preferences-search").HasFocus,
            "Ctrl+F returns to search");
        fx.Invoke(() => ((TextField)Descendants(fx.App.Overlay!).Single(view => view.Id == "preferences-search")).Text = "ejecutable");
        KeyWithEffect(fx, KeyCode.CursorDown,
            () => Descendants(fx.App.Overlay!).Any(view => view.Id == "local-engine-list"),
            "search opens the category containing the existing field");
        Assert.Contains(Descendants(fx.App.Overlay!), view => view.Id == "preferences-categories");
    });
}
