using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(80, 25)]
    [InlineData(120, 35)]
    public void Preferences_category_arrows_wrap_at_both_ends_without_entering_content_or_footer(int width, int height) => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: width, initialRows: height);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences open");
        var navigation = Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "preferences-categories");
        var count = navigation.Source!.Count;
        Assert.True(navigation.HasFocus);
        // Visit every edge twice, including Up from first and Down from last.
        foreach (var delta in new[] { -1, 1 })
        for (var step = 0; step < count * 2; step++)
        {
            var expected = (navigation.SelectedItem!.Value + delta + count) % count;
            KeyWithEffect(fx, delta < 0 ? KeyCode.CursorUp : KeyCode.CursorDown,
                () => navigation.SelectedItem == expected && navigation.HasFocus, "category arrows wrap and retain menu focus");
            Assert.DoesNotContain(Descendants(fx.App.Overlay!), view => view.Id != "preferences-categories"
                && view.CanFocus && !view.SubViews.Any() && view.HasFocus);
            Assert.DoesNotContain(fx.App.Overlay!.SubViews,
                view => view.Id is "preferences-save" or "preferences-reload" or "preferences-cancel");
        }
        Assert.Equal(0, navigation.SelectedItem);
        if (width >= 100)
        {
            var actions = Descendants(fx.App.Overlay!).Single(view => view.Id == "preferences-category-actions");
            Assert.False(actions.Visible);
            ExportPreferenceFrame(fx, width, height, "category-cycle-preview", "General y apariencia");
        }
        KeyWithEffect(fx, KeyCode.CursorRight,
            () => Descendants(fx.App.Overlay!).Any(view => view.Id == "preferences-locale" && view.HasFocus), "explicit entry opens General controls");
        var panel = Descendants(fx.App.Overlay!).Single(view => view.Id == "preferences-panel");
        var categoryActions = Descendants(panel).Single(view => view.Id == "preferences-category-actions");
        Assert.True(categoryActions.Visible);
        Assert.Equal(3, Descendants(categoryActions).OfType<Button>().Count());
        var save = Descendants(categoryActions).OfType<Button>().Single(view => view.Id == "preferences-save");
        fx.Invoke(() => save.SetFocus());
        fx.Wait(() => save.HasFocus && panel.Viewport.Top <= categoryActions.Frame.Top
            && panel.Viewport.Bottom >= categoryActions.Frame.Bottom, "category actions scroll into view when focused");
        ClickThroughApplication(fx, "preferences-save");
        Assert.Equal("es", fx.App.PreferencesLocaleSnapshot!.Locale);
        Assert.True(fx.App.PreferencesLocaleSnapshot.IsConfigured);
        ExportPreferenceFrame(fx, width, height, "category-cycle-editing", "Español");
        if (width >= 100)
        {
            KeyWithEffect(fx, KeyCode.CursorLeft, () => navigation.HasFocus, "left exits category editing");
            Assert.False(categoryActions.Visible);
            KeyWithEffect(fx, KeyCode.CursorUp, () => navigation.SelectedItem == count - 1 && navigation.HasFocus, "Up still wraps after leaving editing");
        }
    }, noColor: false);

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public void Preferences_category_cancel_discards_locale_draft_and_keeps_preferences_open(int width) => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: width, initialRows: 35);
        var before = UiLocalePreferencesProtocol.Decode(fx.Server.Query(UiLocalePreferencesProtocol.QueryName, default)!.Json)!;
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences open");
        ChoosePreferenceCategory(fx, "general");
        KeyWithEffect(fx, KeyCode.CursorDown, () => fx.App.PreferencesLocale == "en", "preview another language");
        fx.Invoke(() => Descendants(fx.App.Overlay!).Single(view => view.Id == "preferences-cancel").SetFocus());
        ClickThroughApplication(fx, "preferences-cancel");
        fx.Wait(() => fx.App.PreferencesLocale == "es" && fx.App.Overlay is not null
            && Descendants(fx.App.Overlay).Any(view => view.Id == "preferences-categories" && view.HasFocus), "Cancel returns to the menu without closing preferences");
        Assert.Equal("es", fx.App.PreferencesDraftLocale);
        var after = UiLocalePreferencesProtocol.Decode(fx.Server.Query(UiLocalePreferencesProtocol.QueryName, default)!.Json)!;
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Locale, after.Locale);
        Assert.Equal(before.IsConfigured, after.IsConfigured);
    });

    [Theory]
    [InlineData(KeyCode.CursorRight)]
    [InlineData(KeyCode.Enter)]
    [InlineData(KeyCode.Tab)]
    public void Preferences_preview_keeps_menu_focus_until_explicit_entry_and_never_sends_arrows_to_search(KeyCode enter) => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: 120, initialRows: 40);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences open");
        ListView Navigation() => Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "preferences-categories");
        ListView Language() => Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "preferences-locale");
        bool SearchFocused() => Descendants(fx.App.Overlay!).Single(view => view.Id == "preferences-search").HasFocus;
        fx.Wait(() => Navigation().HasFocus, "opening focuses the category menu");
        Assert.False(Language().HasFocus); Assert.False(SearchFocused());
        var origin = System.Drawing.Point.Empty;
        fx.Invoke(() => origin = Language().ViewportToScreen(System.Drawing.Point.Empty));
        var previewCells = CaptureConversationFrame(fx, 120, 40, "Español");
        Assert.Equal(Language().GetScheme().Normal.Background, previewCells[origin.Y, origin.X + 1].Attribute?.Background);
        KeyWithEffect(fx, KeyCode.CursorDown, () => Navigation().SelectedItem == 1, "browse accounts without entering");
        Assert.True(Navigation().HasFocus); Assert.False(SearchFocused());
        KeyWithEffect(fx, KeyCode.CursorUp, () => Navigation().SelectedItem == 0, "browse General without entering");
        Assert.True(Navigation().HasFocus); Assert.False(Language().HasFocus); Assert.False(SearchFocused());
        Assert.Equal("es", fx.App.PreferencesDraftLocale);
        KeyWithEffect(fx, enter, () => Language().HasFocus, "explicit key enters language controls");
        Assert.False(SearchFocused());
        var activeCells = CaptureConversationFrame(fx, 120, 40, "Español");
        Assert.NotEqual(Language().GetScheme().Normal.Background, activeCells[origin.Y, origin.X + 1].Attribute?.Background);
        KeyWithEffect(fx, KeyCode.CursorDown, () => fx.App.PreferencesLocale == "en", "choose English after entering");
        Assert.True(Language().HasFocus); Assert.False(SearchFocused());
        KeyWithEffect(fx, KeyCode.CursorDown, () => fx.App.PreferencesLocale == "fr", "locale rebuild preserves input focus");
        Assert.True(Language().HasFocus); Assert.False(SearchFocused());
        KeyWithEffect(fx, KeyCode.CursorLeft, () => Navigation().HasFocus, "left returns to the category menu");
        Assert.False(SearchFocused());
        KeyWithEffect(fx, KeyCode.CursorDown, () => Navigation().SelectedItem == 1, "category changes keep menu focus");
        Assert.True(Navigation().HasFocus); Assert.False(SearchFocused());
        KeyWithEffect(fx, KeyCode.F | KeyCode.CtrlMask, SearchFocused, "search is entered explicitly");
        ExportPreferenceFrame(fx, 120, 40, "navigation-blue", "Préférences");
    }, noColor: false);

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public void Cached_subscription_model_can_be_selected_with_Enter_after_catalog_failure(int width) => RunTuiTest(fx =>
    {
        var policies = ModelPolicyHost.Create(Path.Combine(fx.Root, "cached-failed"));
        policies.RegisterChatGptModel("cached-old", 8192, 2048);
        policies.RegisterChatGptModel("cached-next", 8192, 2048);
        var workspace = ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory);
        policies.Select(workspace, new("chatgpt", "cached-old"), "cached-old", true, default);
        var account = new TestAccount { Current = new(true, "***1234", null, false), FailCatalog = true };
        fx.StartTui(policies, account, initialColumns: width, initialRows: 30);
        KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay is not null, "picker open");
        fx.Wait(() => OverlayText(fx.App.Overlay!).Contains("Falló la consulta", StringComparison.Ordinal)
            && Descendants(fx.App.Overlay!).OfType<Button>().Single(view => view.Id == "omni-palette-refresh").Enabled, "failed lookup offers retry");
        Assert.Equal("cached-old", policies.CurrentSelection(workspace, default)!.ModelId);
        Assert.DoesNotContain("SECRET", OverlayText(fx.App.Overlay!));
        var list = Descendants(fx.App.Overlay!).OfType<ListView>().Single();
        fx.Invoke(() =>
        {
            list.SelectedItem = list.Source!.ToList().Cast<object>().Select(row => row.ToString()!).ToList().FindIndex(row => row.Contains("cached-next", StringComparison.Ordinal));
            list.SetFocus();
        });
        ExportPreferenceFrame(fx, width, 30, "cached-model-after-failure", "cached-next");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is null, "Enter selects the registered model despite lookup failure");
        Assert.Equal("cached-next", policies.CurrentSelection(workspace, default)!.ModelId);
        Assert.Equal("chatgpt", policies.CurrentSelection(workspace, default)!.Key.ProviderId);
        Assert.True(policies.CurrentSelection(workspace, default)!.ObserveOnly);
        Assert.Contains("cached-next", fx.App.Header!.Text.ToString());
        Assert.Equal(0, account.LoginCalls);
    });

    [Fact]
    public void Picker_retries_discovery_without_reopening_and_keeps_search_and_registered_selection() => RunTuiTest(fx =>
    {
        var policies = ModelPolicyHost.Create(Path.Combine(fx.Root, "retry-catalog"));
        policies.RegisterChatGptModel("model-a", 8192, 2048);
        policies.RegisterChatGptModel("model-b", 8192, 2048);
        var workspace = ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory);
        policies.Select(workspace, new("chatgpt", "model-a"), "model-a", true, default);
        var account = new TestAccount { Current = new(true, "***1234", null, false), FailCatalog = true };
        fx.StartTui(policies, account);
        KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay is not null, "picker open");
        fx.Wait(() => OverlayText(fx.App.Overlay!).Contains("Falló la consulta", StringComparison.Ordinal), "lookup failure shown");
        var original = fx.App.Overlay;
        fx.Invoke(() => fx.App.Overlay!.SubViews.OfType<TextField>().Single().Text = "model");
        fx.Invoke(() =>
        {
            var list = fx.App.Overlay!.SubViews.OfType<ListView>().Single();
            list.SelectedItem = list.Source!.ToList().Cast<object>().Select(row => row.ToString()!).ToList().FindIndex(row => row.Contains("model-b", StringComparison.Ordinal));
        });
        account.Catalog = [new("model-a", "Model A"), new("model-b", "Model B"), new("new-model", "New model")];
        account.FailCatalog = false;
        ClickId(fx, "omni-palette-refresh");
        fx.Wait(() => !ReferenceEquals(original, fx.App.Overlay) && PickerItems(fx).Any(row => row.Contains("New model", StringComparison.Ordinal)), "retry refreshes the same picker");
        Assert.Equal("model", fx.App.Overlay!.SubViews.OfType<TextField>().Single().Text.ToString());
        var refreshed = fx.App.Overlay.SubViews.OfType<ListView>().Single();
        Assert.Contains("Model B", refreshed.Source!.ToList()[refreshed.SelectedItem!.Value]!.ToString());
        Assert.Equal(2, account.CatalogCalls); Assert.Equal(0, account.LoginCalls);
        Assert.Equal("model-a", policies.CurrentSelection(workspace, default)!.ModelId);
    });

    [Fact]
    public void Successful_picker_refresh_still_retires_unavailable_models_and_focuses_the_safe_replacement() => RunTuiTest(fx =>
    {
        var policies = ModelPolicyHost.Create(Path.Combine(fx.Root, "retired-picker"));
        policies.RegisterChatGptModel("retired-model", 8192, 2048);
        var workspace = ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory);
        policies.Select(workspace, new("chatgpt", "retired-model"), "retired-model", true, default);
        var account = new TestAccount { Current = new(true, "***1234", null, false),
            Catalog = [new("replacement-model", "Replacement model")] };
        fx.StartTui(policies, account);
        KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay is not null, "picker open");
        fx.Wait(() => PickerItems(fx).Any(row => row.Contains("Replacement model", StringComparison.Ordinal)), "authoritative catalog applied");
        Assert.DoesNotContain(PickerItems(fx), row => row.Contains("retired-model", StringComparison.Ordinal));
        Assert.Equal("replacement-model", policies.CurrentSelection(workspace, default)!.ModelId);
        Assert.True(policies.CurrentSelection(workspace, default)!.ObserveOnly);
        var list = fx.App.Overlay!.SubViews.OfType<ListView>().Single();
        Assert.Contains("Replacement model", list.Source!.ToList()[list.SelectedItem!.Value]!.ToString());
    });

    [Fact]
    public void Preferences_model_picker_selects_registered_subscription_without_requiring_discovery() => RunTuiTest(fx =>
    {
        var policies = ModelPolicyHost.Create(Path.Combine(fx.Root, "offline-picker"));
        policies.RegisterChatGptModel("registered-only", 8192, 2048);
        var account = new TestAccount { Current = new(true, "***1234", null, false), FailCatalog = true };
        fx.StartTui(policies, account, initialColumns: 120, initialRows: 40);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences open");
        ChoosePreferenceCategory(fx, "models_execution"); ClickId(fx, "preferences-model-select");
        Assert.Equal(0, account.CatalogCalls);
        var list = fx.App.Overlay!.SubViews.OfType<ListView>().Single();
        fx.Invoke(() =>
        {
            list.SelectedItem = list.Source!.ToList().Cast<object>().Select(row => row.ToString()!).ToList().FindIndex(row => row.Contains("registered-only", StringComparison.Ordinal));
            Assert.True(list.SelectedItem >= 0); list.SetFocus();
        });
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is null, "registered model can be selected directly");
        Assert.Equal("registered-only", policies.CurrentSelection(ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory), default)!.ModelId);
        Assert.Equal(0, account.CatalogCalls);
    });
}
