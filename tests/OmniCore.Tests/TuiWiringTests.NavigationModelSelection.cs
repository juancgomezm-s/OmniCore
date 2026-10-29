using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Fact]
    public void Explicit_model_choice_survives_clearing_and_old_journal_replay_until_a_new_invocation_starts() => RunTuiTest(fx =>
    {
        var stream = new OmniCore.Engine.EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!);
        stream.Append(new OmniCore.Domain.ModelStepStarted(OmniCore.Domain.TurnId.New(), 0, "local/old-model", 8192, "Direct", null, null, null));
        var policies = ModelPolicyHost.Create(Path.Combine(fx.Root, "switch-model-policy"));
        policies.RegisterChatGptModel("chosen-subscription", 8192, 2048);
        var host = new RouteResumeTurnHost();
        fx.StartTui(policies, new TestAccount { Current = new(true, "***1234", null, false), FailCatalog = true }, turnHost: host);
        fx.Wait(() => fx.App.Header!.Text.Contains("local/old-model"), "historical model visible before choosing");
        KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay is not null, "model picker");
        var list = fx.App.Overlay!.SubViews.OfType<ListView>().Single();
        fx.Invoke(() =>
        {
            list.SelectedItem = list.Source!.ToList().Cast<object>().Select(item => item.ToString()!).ToList()
                .FindIndex(item => item.Contains("chosen-subscription", StringComparison.Ordinal));
            list.SetFocus();
        });
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "reasoning-preferences", "choose subscription and offer reasoning preferences");
        ConfirmSelectedModelPreferences(fx, "chosen-subscription");
        fx.Wait(() => fx.App.Header!.Text.Contains("chosen-subscription"), "explicit choice in header");
        var session = fx.Server.LastSessionId()!;
        var sequence = fx.Server.AcquireStore().CurrentSequence(session);
        Type(fx, "/limpiar");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.ProjectionState.Conversation.Blocks.Count == 0, "clear view only");
        Assert.Equal(session, fx.Server.LastSessionId());
        Assert.Equal(sequence, fx.Server.AcquireStore().CurrentSequence(session));
        Type(fx, "/retomar " + session);
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.ProjectionState.Conversation.Blocks.Count > 0, "replay the same conversation");
        Assert.Contains("chosen-subscription", fx.App.Header!.Text);
        Type(fx, "muestra ejemplos de markdown");
        KeyWithEffect(fx, KeyCode.Enter, () => host.ExecuteCount == 1, "ordinary question");
        fx.Wait(() => fx.App.Status!.Text.Contains("Listo"), "turn refresh replays journal");
        Assert.Contains("chosen-subscription", fx.App.Header!.Text);
        Assert.DoesNotContain("local/old-model", fx.App.Header.Text);
        stream.Append(new OmniCore.Domain.ModelStepStarted(OmniCore.Domain.TurnId.New(), 1, "chatgpt/new-effective-model", 8192, "Direct", null, null, null));
        fx.Wait(() => fx.App.Header.Text.Contains("chatgpt/new-effective-model"), "fresh runtime observation replaces pending selection");
    });

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
        ExportPreferenceFrame(fx, width, height, "category-cycle-editing-actions", "Guardar");
        fx.Invoke(() => Descendants(panel).Single(view => view.Id == "preferences-locale").SetFocus());
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
        ChooseLocaleDraft(fx, 1);
        Assert.Equal("es", fx.App.PreferencesLocale);
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
        void AssertLanguageSelectionFrame(bool focused)
        {
            // Category navigation rebuilds the language view, and focusing it can scroll
            // its parent. Sample the current selected row after that layout has drawn.
            var selectedCell = System.Drawing.Point.Empty;
            var frame = CaptureConversationFrame(fx, 120, 40, "Español", cells =>
            {
                var language = Language();
                selectedCell = language.ViewportToScreen(new System.Drawing.Point(1,
                    (language.SelectedItem ?? 0) - language.Viewport.Y));
                if (selectedCell.X < 0 || selectedCell.X >= cells.GetLength(1)
                    || selectedCell.Y < 0 || selectedCell.Y >= cells.GetLength(0)) return false;
                var expected = focused ? language.GetScheme().Focus : language.GetScheme().Normal;
                return language.HasFocus == focused && cells[selectedCell.Y, selectedCell.X].Attribute?.Background == expected.Background;
            });
            var scheme = fx.Read(() => Language().GetScheme());
            Assert.NotEqual(scheme.Normal.Background, scheme.Focus.Background);
            Assert.Equal((focused ? scheme.Focus : scheme.Normal).Background,
                frame[selectedCell.Y, selectedCell.X].Attribute?.Background);
        }
        AssertLanguageSelectionFrame(focused: false);
        KeyWithEffect(fx, KeyCode.CursorDown, () => Navigation().SelectedItem == 1, "browse accounts without entering");
        Assert.True(Navigation().HasFocus); Assert.False(SearchFocused());
        KeyWithEffect(fx, KeyCode.CursorUp, () => Navigation().SelectedItem == 0, "browse General without entering");
        Assert.True(Navigation().HasFocus); Assert.False(Language().HasFocus); Assert.False(SearchFocused());
        Assert.Equal("es", fx.App.PreferencesDraftLocale);
        KeyWithEffect(fx, enter, () => Language().HasFocus, "explicit key enters language controls");
        Assert.False(SearchFocused());
        AssertLanguageSelectionFrame(focused: true);
        KeyWithEffect(fx, KeyCode.CursorDown, () => Language().SelectedItem == 1, "browse English after entering");
        Assert.Equal("es", fx.App.PreferencesDraftLocale); Assert.Equal("es", fx.App.PreferencesLocale);
        Assert.True(Language().HasFocus); Assert.False(SearchFocused());
        KeyWithEffect(fx, KeyCode.CursorDown, () => Language().SelectedItem == 2, "browse French without applying it");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.PreferencesDraftLocale == "fr", "Enter chooses the draft explicitly");
        Assert.Equal("es", fx.App.PreferencesLocale);
        Assert.True(Language().HasFocus); Assert.False(SearchFocused());
        KeyWithEffect(fx, KeyCode.CursorLeft, () => Navigation().HasFocus, "left returns to the category menu");
        Assert.False(SearchFocused());
        KeyWithEffect(fx, KeyCode.CursorDown, () => Navigation().SelectedItem == 1, "category changes keep menu focus");
        Assert.True(Navigation().HasFocus); Assert.False(SearchFocused());
        KeyWithEffect(fx, KeyCode.F | KeyCode.CtrlMask, SearchFocused, "search is entered explicitly");
        ExportPreferenceFrame(fx, 120, 40, "navigation-blue", "Preferencias");
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
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "reasoning-preferences", "Enter selects the registered model despite lookup failure");
        ConfirmSelectedModelPreferences(fx, "cached-next");
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
    public void Model_command_selects_registered_subscription_without_requiring_discovery() => RunTuiTest(fx =>
    {
        var policies = ModelPolicyHost.Create(Path.Combine(fx.Root, "offline-picker"));
        policies.RegisterChatGptModel("registered-only", 8192, 2048);
        var account = new TestAccount { Current = new(true, "***1234", null, false), FailCatalog = true };
        fx.StartTui(policies, account, initialColumns: 120, initialRows: 40);
        Type(fx, "/modelo");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "omni-model-palette", "model command opens picker");
        Assert.Equal(0, account.CatalogCalls);
        var list = fx.App.Overlay!.SubViews.OfType<ListView>().Single();
        fx.Invoke(() =>
        {
            list.SelectedItem = list.Source!.ToList().Cast<object>().Select(row => row.ToString()!).ToList().FindIndex(row => row.Contains("registered-only", StringComparison.Ordinal));
            Assert.True(list.SelectedItem >= 0); list.SetFocus();
        });
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "reasoning-preferences", "registered model can be selected directly");
        ConfirmSelectedModelPreferences(fx, "registered-only");
        Assert.Equal("registered-only", policies.CurrentSelection(ModelPolicyHost.WorkspaceSelectionId(Environment.CurrentDirectory), default)!.ModelId);
        Assert.Equal(0, account.CatalogCalls);
    });
}
