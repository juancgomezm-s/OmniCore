using System.Collections.ObjectModel;
using System.Drawing;
using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

#pragma warning disable CS0618 // TextView is the same pinned Terminal.Gui component used by existing TUI presenters.
public sealed partial class TuiApp
{
    private string _preferencesCategory = "general";
    private string _preferencesQuery = "";
    private bool _preferencesCenterActive;
    private bool _preferencesCategoryPage;
    private bool _preferencesReturnToCategory;
    private string _preferencesDraftLocale = "es";
    private string _preferencesOriginalLocale = "";
    private int _preferencesLastLayoutWidth = -1;
    private int _preferencesLastLayoutHeight = -1;
    private UiLocalePreferencesSnapshot? _preferencesLocaleSnapshot;
    private LocalEnginePreferencesSnapshot? _localEngineSnapshot;
    private string? _localEngineNotice;
    private string? _localEngineSelectedProviderId;
    private Task<LocalEngineRuntimeActionResult>? _localEngineRuntimeTask;
    private Label? _preferencesNotice;
    private Button? _preferencesReload;

    internal string PreferencesLocale => _locale;
    internal string PreferencesDraftLocale => _preferencesDraftLocale;
    internal UiLocalePreferencesSnapshot? PreferencesLocaleSnapshot => _preferencesLocaleSnapshot;
    internal Task? LocalEngineRuntimeTask => _localEngineRuntimeTask;

    private string Pref(string key) => _localization.Resolve("preferences.ui." + key);

    private void ShowPreferences(string? categoryId = null, bool categoryPage = false)
    {
        if (_window is null || _overlay is not null) return;
        _preferencesLastLayoutWidth = _window.Frame.Width;
        _preferencesLastLayoutHeight = _window.Frame.Height;
        _preferencesCenterActive = true;
        _preferencesCategoryPage = categoryPage;
        if (categoryId is not null) _preferencesCategory = categoryId;
        if ((!categoryPage && categoryId is null) || _preferencesLocaleSnapshot is null)
        {
            var result = _client.Query(UiLocalePreferencesProtocol.QueryName, CancellationToken.None);
            _preferencesLocaleSnapshot = null;
            if (result is not null)
            {
                try { _preferencesLocaleSnapshot = UiLocalePreferencesProtocol.Decode(result.Json); }
                catch (System.Text.Json.JsonException) { }
            }
        }
        if (!categoryPage && categoryId is null)
        {
            if (_localeSelection?.HasProcessOverride != true)
            {
                _localeSelection = LocaleSelectionResolver.Resolve(null, null,
                    _preferencesLocaleSnapshot?.IsConfigured == true ? _preferencesLocaleSnapshot.Locale : null);
                if (_locale != _localeSelection.Locale)
                {
                    _locale = _localeSelection.Locale;
                    _localization = new Localization(_locale);
                    _projection = new ClientProjection(_localization);
                    _localeChanged?.Invoke(_locale);
                }
            }
            if (_preferencesOriginalLocale.Length == 0) _preferencesOriginalLocale = _locale;
            _preferencesDraftLocale = _preferencesLocaleSnapshot?.Locale ?? _locale;
        }
        var categories = PreferenceCategoryCatalog.Resolve(_localization);
        var matches = PreferencesPresentation.Search(categories, _preferencesQuery, _locale);
        var selected = categories.FirstOrDefault(item => item.Id == _preferencesCategory) ?? categories[0];
        var wide = PreferencesPresentation.IsWideLayout(_window.Frame.Width);
        var title = categoryPage ? Pref("title") + " › " + selected.Title : Pref("title");
        var frame = OverlayFrame(title, _window.Frame.Height - 3);
        frame.Width = wide ? Dim.Percent(82) : Dim.Percent(92);
        frame.Height = Dim.Fill(2);
        if (!categoryPage)
        {
            var searchLabel = new Label { X = 2, Y = 3, Width = 10, Height = 1, Text = Pref("search") };
            var search = new TextField { Id = "preferences-search", X = 12, Y = 3,
                Width = wide ? Dim.Percent(47) : Dim.Fill(4), Height = 1, Text = _preferencesQuery };
            var categoryList = new ListView { Id = "preferences-categories", X = 2, Y = 5,
                Width = wide ? 30 : Dim.Fill(4), Height = Dim.Fill(5), ShowMarks = false,
                CanFocus = true, Visible = matches.Count > 0 };
            var contentX = wide ? 35 : 2;
            var contentWidth = wide ? Dim.Fill(3) : Dim.Fill(4);
            var panel = new View { Id = "preferences-panel", X = contentX, Y = 2,
                Width = contentWidth, Height = Dim.Fill(5), CanFocus = true };
            var accent = new View { Id = "preferences-accent", X = 0, Y = 0, Width = 1,
                Height = Dim.Fill(), CanFocus = false };
            var heading = new Label { Id = "preferences-panel-title", X = 2, Y = 1,
                Width = Dim.Fill(3), Height = 1, Text = selected.Title };
            var description = new Label { Id = "preferences-panel-description", X = 2, Y = 3,
                Width = Dim.Fill(3), Height = wide ? 3 : 4, Text = selected.Description };
            var categoryHelp = new Label { Id = "preferences-panel-help", X = 2,
                Y = wide ? 7 : 8, Width = Dim.Fill(3), Height = 3, Text = selected.Help };
            var open = new Button { Id = "preferences-open-category", X = 2,
                Y = wide ? 11 : 13, Width = Dim.Fill(3), TextAlignment = Alignment.Start,
                Text = selected.Title + "  ›" };
            var emptySearch = new Label { Id = "preferences-empty-search", X = 2, Y = 5,
                Width = wide ? 30 : Dim.Fill(4), Height = 2, Text = Pref("no_matches"), Visible = matches.Count == 0 };
            var notice = new Label { Id = "preferences-notice", X = 2, Y = Pos.AnchorEnd(4),
                Width = Dim.Fill(4), Height = 3, Text = _localization.Resolve("shell.preferences_language_note") };
            var categoryBack = new Button { Id = "preferences-close", X = 2, Y = Pos.AnchorEnd(2), Text = Pref("close") };
            categoryBack.Accepted += (_, _) => ClosePreferenceCenter();
            categoryList.SetSource(new ObservableCollection<string>(matches.Select(item => item.Title)));
            var matchIndex = matches.ToList().FindIndex(item => item.Id == _preferencesCategory);
            if (matchIndex >= 0) categoryList.SelectedItem = matchIndex;
            void SyncPanel()
            {
                emptySearch.Visible = matches.Count == 0;
                categoryList.Visible = matches.Count > 0;
                open.Enabled = matches.Count > 0;
                if (matches.Count == 0)
                {
                    heading.Text = Pref("no_matches");
                    description.Text = "";
                    categoryHelp.Text = "";
                    open.Text = Pref("no_matches");
                    return;
                }
                if (categoryList.SelectedItem is not int index || index < 0 || index >= matches.Count) return;
                selected = matches[index];
                heading.Text = selected.Title;
                description.Text = selected.Description;
                categoryHelp.Text = selected.Help;
                open.Text = selected.Title + "  ›";
            }
            void OpenCategory()
            {
                if (categoryList.SelectedItem is int index && index >= 0 && index < matches.Count)
                {
                    _preferencesCategory = matches[index].Id;
                    CloseOverlay(); ShowPreferences(_preferencesCategory, categoryPage: true);
                }
            }
            categoryList.ValueChanged += (_, _) => SyncPanel();
            categoryList.Accepted += (_, args) => { args.Handled = true; OpenCategory(); };
            open.Accepted += (_, _) => OpenCategory();
            search.TextChanged += (_, _) =>
            {
                _preferencesQuery = search.Text.ToString();
                var filtered = PreferencesPresentation.Search(categories, _preferencesQuery, _locale);
                var keepId = selected.Id;
                matches = filtered;
                categoryList.SetSource(new ObservableCollection<string>(matches.Select(item => item.Title)));
                var next = matches.ToList().FindIndex(item => item.Id == keepId);
                if (next < 0) next = 0;
                if (matches.Count > 0) categoryList.SelectedItem = next;
                SyncPanel();
            };
            search.KeyDown += (_, key) =>
            {
                if (key.KeyCode == (KeyCode.F | KeyCode.CtrlMask))
                { search.SetFocus(); key.Handled = true; return; }
                if (key.KeyCode is KeyCode.CursorDown or KeyCode.Enter && matches.Count > 0)
                { categoryList.SetFocus(); key.Handled = true; }
            };
            frame.KeyDown += (_, key) =>
            {
                if (key.KeyCode == (KeyCode.F | KeyCode.CtrlMask))
                { search.SetFocus(); key.Handled = true; }
            };
            frame.Add(searchLabel, search, categoryList, categoryBack, emptySearch,
                new Label { Id = "preferences-help", X = 2, Y = Pos.AnchorEnd(3),
                    Width = Dim.Fill(3), Height = 1, Text = Pref(wide ? "help" : "help_narrow") });
            if (wide)
            {
                frame.Add(panel);
                panel.Add(accent, heading, description, categoryHelp, open, notice);
            }
            ApplyTheme(frame);
            _overlay = frame; _window.Add(frame);
            categoryList.SetFocus();
            return;
        }

        var contentPanel = new View { Id = "preferences-panel", X = 2, Y = 3,
            Width = Dim.Fill(4), Height = Dim.Fill(6), CanFocus = true };
        contentPanel.Add(new View { Id = "preferences-accent", X = 0, Y = 0, Width = 1,
            Height = Dim.Fill(), CanFocus = false });
        var descriptionLabel = new Label { Id = "preferences-description", X = 2, Y = 0,
            Width = Dim.Fill(3), Height = 2, Text = selected.Description };
        contentPanel.Add(descriptionLabel);
        _preferencesNotice = new Label { Id = "preferences-notice", X = 4, Y = Pos.AnchorEnd(5),
            Width = Dim.Fill(5), Height = 2 };

        var controls = new List<View>();
        void Add(View view) { controls.Add(view); contentPanel.Add(view); }
        void AddAction(string id, string key, int y, Action action)
        {
            var button = new Button { Id = id, X = 2, Y = y, Width = Dim.Fill(3),
                TextAlignment = Alignment.Start, Text = Pref(key) };
            button.Accepted += (_, _) => action(); Add(button);
        }
        var y = 3;
        switch (selected.Id)
        {
            case "general":
                Add(new Label { Id = "preferences-locale-label", X = 2, Y = y++, Width = Dim.Fill(4), Height = 1, Text = Pref("locale_label") });
                var language = new ListView { Id = "preferences-locale", X = 2, Y = y,
                    Width = Dim.Fill(4), Height = 4, ShowMarks = false };
                language.SetSource(new ObservableCollection<string>(new[]
                    { Pref("locale_es"), Pref("locale_en"), Pref("locale_fr"), Pref("locale_it") }));
                language.SelectedItem = Array.IndexOf(UiLocaleCodes.Supported.ToArray(), _preferencesDraftLocale);
                language.ValueChanged += (_, _) =>
                {
                    if (language.SelectedItem is not int index || index < 0 || index >= UiLocaleCodes.Supported.Count) return;
                    _preferencesDraftLocale = UiLocaleCodes.Supported[index];
                    if (_localeSelection?.HasProcessOverride != true)
                        ApplyPreferenceLocale(_preferencesDraftLocale, frame, categoryPage: true);
                };
                Add(language); y += 4;
                Add(new Label { X = 2, Y = y, Width = Dim.Fill(4), Height = 2,
                    Text = Pref(_localeSelection?.HasProcessOverride == true ? "locale_override" : "locale_note") }); y += 2;
                var sourceKey = _localeSelection?.HasProcessOverride == true ? _localeSelection.Source switch
                {
                    LocaleSelectionSource.CommandLine => "source_cli",
                    _ => "source_env",
                } : _preferencesDraftLocale != (_preferencesLocaleSnapshot?.Locale ?? _locale)
                    ? "source_preview" : _localeSelection?.Source == LocaleSelectionSource.UserSetting
                        || _preferencesLocaleSnapshot?.IsConfigured == true ? "source_user" : "source_default";
                Add(new Label { Id = "preferences-locale-source", X = 2, Y = y++, Width = Dim.Fill(4), Height = 1,
                    Text = Pref("source_label") + ": " + Pref(sourceKey) + " · " + Pref("locale_" + _locale) });
                Add(new Label { Id = "preferences-locale-saved", X = 2, Y = y++, Width = Dim.Fill(4), Height = 1,
                    Text = Pref("preference_label") + ": " + Pref(_preferencesLocaleSnapshot?.IsConfigured == true ? "preference_saved" : "preference_default")
                        + " · " + (_preferencesLocaleSnapshot?.Locale ?? _locale) });
                break;
            case "accounts_connections":
                Add(new Label { Id = "preferences-account-heading", X = 2, Y = y++, Width = Dim.Fill(4), Height = 1, Text = Pref("account_title") });
                try
                {
                    _account ??= _accountOverride ?? new TuiAccountHost();
                    var state = _account.Status();
                    var status = state.LoggedIn ? state.AccountIdMasked + " · " + AccountUi(state.Expired ? "status.expired" : "status.connected") : AccountUi("status.disconnected");
                    AddAction("preferences-account-open", "account_open", y++, () => { CloseOverlay(); ShowAccount(fromPreferences: true); });
                    Add(new Label { Id = "preferences-account-status", X = 2, Y = y++, Width = Dim.Fill(4), Height = 1, Text = status });
                }
                catch
                {
                    AddAction("preferences-account-open", "account_open", y++, () => { CloseOverlay(); ShowAccount(fromPreferences: true); });
                    Add(new Label { Id = "preferences-account-status", X = 2, Y = y++, Width = Dim.Fill(4), Height = 1, Text = AccountUi("status.unavailable") });
                }
                var claudeOpen = new Button { Id = "preferences-claude-open", X = 2, Y = y++,
                    Width = Dim.Fill(3), TextAlignment = Alignment.Start, Text = AccountUi("action.manageClaude") };
                claudeOpen.Accepted += (_, _) => { CloseOverlay(); ShowClaudeAccounts(); };
                Add(claudeOpen);
                string claudeSummary;
                try
                {
                    var rows = _claudeAccounts.List(CancellationToken.None);
                    claudeSummary = rows.Count == 0 ? AccountUi("status.noRows")
                        : string.Join(" · ", rows.Select(row => row.ProviderId + ": "
                            + AccountUi(row.UnavailableReasonKey is null ? row.Dead ? "status.dead" : row.Expired ? "status.expired"
                                : row.SignedIn ? "status.connected" : "status.notConfigured" : "status.unavailable")));
                }
                catch { claudeSummary = AccountUi("status.unavailable"); }
                Add(new Label { Id = "preferences-claude-status", X = 2, Y = y++, Width = Dim.Fill(4), Height = 1, Text = claudeSummary });
                AddAction("preferences-api-open", "api_open", y++, () => { CloseOverlay(); ShowProviderConnections(); });
                Add(new Label { Id = "preferences-api-status", X = 2, Y = y, Width = Dim.Fill(4), Height = 1,
                    Text = AccountUi("help.providerState") });
                break;
            case "local_engines":
                ShowLocalEngineList(contentPanel, y);
                break;
            case "models_execution":
                AddAction("preferences-models-open", "models_open", y, () => { CloseOverlay(); ShowPolicyPreferences("models_execution"); });
                var catalogButton = new Button { Id = "preferences-model-catalog-open", X = 2, Y = y + 2,
                    Width = Dim.Fill(3), TextAlignment = Alignment.Start, Text = _localization.Resolve("modelCatalogPreferences.open") };
                catalogButton.Accepted += (_, _) => { CloseOverlay(); ShowModelCatalogPreferences(); };
                Add(catalogButton);
                break;
            case "tools_security":
                AddAction("preferences-tools-open", "tools_open", y, () => RunPreferenceAction(ClientActionIds.ToolsInspect)); y += 2;
                AddAction("preferences-permissions-open", "permissions_open", y++, () => { CloseOverlay(); ShowSecurityPreferences("tools_security"); });
                Add(new Label { X = 2, Y = y, Width = Dim.Fill(4), Height = 2, Text = Pref("permissions_hint") });
                break;
            case "interface":
                AddAction("preferences-sidebar-open", "sidebar_open", y, () => { CloseOverlay(); ShowSidebarSettings(); }); y += 2;
                AddAction("preferences-animations-open", "animations_open", y, () => { CloseOverlay(); ShowAnimationSettings(); });
                break;
            case "context_memory":
                Add(new Label { X = 2, Y = y, Width = Dim.Fill(4), Height = 2, Text = Pref("context_hint") }); y += 2;
                AddAction("preferences-context-open", "context_open", y++, () => RunPreferenceAction(ClientActionIds.ContextInspect));
                Add(new Label { X = 2, Y = y++, Width = Dim.Fill(4), Height = 1, Text = Pref("usage_label") });
                if (_turnUsage is { } usage)
                {
                    var tokens = usage.SessionTokenMeasurement is { } measurement
                        ? UsagePresentation.Tokens(measurement, _locale) : Pref("usage_unknown");
                    var cost = UsagePresentation.Cost(usage.SessionCost);
                    Add(new Label { Id = "preferences-usage-value", X = 2, Y = y, Width = Dim.Fill(4), Height = 2,
                        Text = tokens + (cost is null ? "" : " · " + cost) + " · " + usage.AsOf.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture) });
                    y += 2;
                }
                else { Add(new Label { Id = "preferences-usage-value", X = 2, Y = y, Width = Dim.Fill(4), Height = 2, Text = Pref("usage_unknown") }); y += 2; }
                AddAction("preferences-memory-open", "memory_open", y, () => RunPreferenceAction(ClientActionIds.MemorySession));
                break;
            case "extensions_skills":
                Add(new Label { X = 2, Y = y, Width = Dim.Fill(4), Height = 2, Text = Pref("extensions_hint") }); y += 2;
                AddAction("preferences-extensions-open", "extensions_open", y++, () => RunPreferenceAction(ClientActionIds.ExtensionsInspect));
                AddAction("preferences-skills-open", "skills_open", y++, () => RunPreferenceAction(ClientActionIds.SkillsInspect));
                var commandsButton = new Button { Id = "preferences-commands-open", X = 2, Y = y,
                    Width = Dim.Fill(4), TextAlignment = Alignment.Start, Text = _localization.Resolve("catalog.title") };
                commandsButton.Accepted += (_, _) => ShowCommandPreferences();
                Add(commandsButton);
                break;
            case "diagnostics_data":
                AddAction("preferences-diagnostics-open", "diagnostics_open", y, () => { CloseOverlay(); ShowDiagnosticsPreferences("diagnostics_data"); });
                break;
        }
        frame.Add(_preferencesNotice);
        var back = new Button { Id = "preferences-back", X = 2, Y = Pos.AnchorEnd(2), Text = Pref("back") };
        back.Accepted += (_, _) =>
        {
            if (selected.Id == "general") CancelPreferenceLocaleDraft();
            CloseOverlay(); ShowPreferences(_preferencesCategory);
        };
        if (selected.Id == "general")
        {
            var cancel = new Button { Id = "preferences-cancel", X = Pos.AnchorEnd(37), Y = Pos.AnchorEnd(2),
                Width = 12, Text = Pref("cancel") };
            cancel.Accepted += (_, _) => ClosePreferenceCenter();
            var save = new Button { Id = "preferences-save", X = Pos.AnchorEnd(22), Y = Pos.AnchorEnd(2),
                Width = 12, Text = Pref("save") };
            save.Accepted += (_, _) => SavePreferenceLocale();
            _preferencesReload = new Button { Id = "preferences-reload", X = Pos.AnchorEnd(52), Y = Pos.AnchorEnd(2),
                Width = 12, Text = Pref("reload"), Enabled = false };
            _preferencesReload.Accepted += (_, _) => ReloadPreferenceLocale(frame);
            frame.Add(contentPanel, back, cancel, save, _preferencesReload, new Label { Id = "preferences-help", X = 2, Y = Pos.AnchorEnd(3),
                Width = Dim.Fill(3), Height = 1, Text = Pref("help_detail") });
        }
        else frame.Add(contentPanel, back, new Label { Id = "preferences-help", X = 2, Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(3), Height = 1, Text = Pref("help_detail") });
        ApplyTheme(frame); _overlay = frame; _window.Add(frame);
        View? focus = controls.OfType<View>().FirstOrDefault(view => view.CanFocus);
        if (selected.Id == "local_engines")
        {
            var engineList = FindPreferenceView(frame, "local-engine-list");
            if (engineList?.CanFocus == true) focus = engineList;
        }
        if (focus is not null) focus.SetFocus(); else back.SetFocus();
    }

    private void ReflowPreferencesIfNeeded()
    {
        if (_window is null || _overlay is null || !_preferencesCenterActive || _preferencesReturnToCategory)
            return;
        var width = _window.Frame.Width;
        var height = _window.Frame.Height;
        if (width == _preferencesLastLayoutWidth && height == _preferencesLastLayoutHeight) return;

        var focusId = FindFocusedPreferenceView(_overlay)?.Id;
        var listStates = DescendantViews(_overlay).OfType<ListView>()
            .Where(view => !string.IsNullOrEmpty(view.Id))
            .ToDictionary(view => view.Id!, view => (view.SelectedItem, view.Viewport.Y), StringComparer.Ordinal);
        var categoryPage = _preferencesCategoryPage;
        _preferencesLastLayoutWidth = width;
        _preferencesLastLayoutHeight = height;
        CloseOverlay();
        ShowPreferences(_preferencesCategory, categoryPage);

        if (_overlay is null) return;
        foreach (var state in listStates)
        {
            if (FindPreferenceView(_overlay, state.Key) is not ListView list) continue;
            if (state.Value.SelectedItem is int selected && list.Source is { } source && selected >= 0 && selected < source.Count)
                list.SelectedItem = selected;
            var viewport = list.Viewport;
            var maxTop = Math.Max(0, (list.Source?.Count ?? 0) - viewport.Height);
            list.Viewport = new Rectangle(viewport.X, Math.Clamp(state.Value.Y, 0, maxTop), viewport.Width, viewport.Height);
        }
        if (!string.IsNullOrEmpty(focusId)) FindPreferenceView(_overlay, focusId)?.SetFocus();
    }

    private static View? FindFocusedPreferenceView(View root)
    {
        foreach (var child in root.SubViews)
            if (FindFocusedPreferenceView(child) is { } focused) return focused;
        // HasFocus can be true on a container because focus is somewhere beneath it.
        // Only record a focus target that can actually receive focus, after checking children.
        return root.CanFocus && root.HasFocus ? root : null;
    }

    private static IEnumerable<View> DescendantViews(View root)
    {
        yield return root;
        foreach (var child in root.SubViews)
            foreach (var descendant in DescendantViews(child)) yield return descendant;
    }

    private void RunPreferenceAction(string actionId)
    {
        switch (actionId)
        {
            case ClientActionIds.ToolsInspect:
                var tools = _client.Query("tools", CancellationToken.None)?.Json;
                var inspection = tools is null ? null : ToolsInspectionJson.Decode(tools);
                if (inspection is null) ShowMessage(Pref("unavailable")); else ShowTools(inspection);
                return;
            case ClientActionIds.ContextInspect: CloseOverlay(); ShowSessionPreferences("context"); return;
            case ClientActionIds.ExtensionsInspect: CloseOverlay(); ShowSessionPreferences("extensions"); return;
            case ClientActionIds.SkillsInspect: CloseOverlay(); ShowSessionPreferences("skills"); return;
            case ClientActionIds.MemorySession: CloseOverlay(); ShowSessionPreferences("memory.session"); return;
            case ClientActionIds.PermissionsList:
                ShowPreferenceText(Pref("permissions_open"), CliApp.PermissionsListing()); return;
            default: ShowMessage(Pref("unavailable")); return;
        }
    }

    private void ShowPreferenceInspection(string queryName)
    {
        if (_window is null) return;
        var query = _client.Query(queryName, CancellationToken.None);
        var lines = query?.Json is { } json ? FormatPreferenceInspection(queryName, json) : [Pref("no_data")];
        CloseOverlay();
        var title = queryName switch
        {
            "context" => Pref("context_open"), "extensions" => Pref("extensions_open"),
            "skills" => Pref("skills_open"), _ => Pref("memory_open"),
        };
        var frame = OverlayFrame(title, _window.Frame.Height - 3);
        frame.Width = Dim.Percent(90); frame.Height = Dim.Fill(2);
        var content = new TextView { Id = "preferences-inspection", X = 2, Y = 2,
            Width = Dim.Fill(4), Height = Dim.Fill(5), ReadOnly = true, WordWrap = true,
            Text = string.Join(Environment.NewLine, lines) };
        var close = new Button { Id = "preferences-inspection-close", X = 2, Y = Pos.AnchorEnd(2), Text = Pref("back") };
        close.Accepted += (_, _) => { CloseOverlay(); ShowPreferences(_preferencesCategory, categoryPage: true); };
        frame.Add(content, close, new Label { Id = "preferences-help", X = 2, Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(4), Height = 1, Text = Pref("inspection_help") });
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); _preferencesReturnToCategory = true; content.SetFocus();
    }

    private void ShowPreferenceText(string title, string text)
    {
        if (_window is null) return;
        CloseOverlay();
        var frame = OverlayFrame(title, _window.Frame.Height - 3);
        frame.Width = Dim.Percent(90); frame.Height = Dim.Fill(2);
        var content = new TextView { Id = "preferences-inspection", X = 2, Y = 2,
            Width = Dim.Fill(4), Height = Dim.Fill(5), ReadOnly = true, WordWrap = true, Text = text };
        var close = new Button { Id = "preferences-inspection-close", X = 2, Y = Pos.AnchorEnd(2), Text = Pref("back") };
        close.Accepted += (_, _) => { CloseOverlay(); ShowPreferences(_preferencesCategory, categoryPage: true); };
        frame.Add(content, close, new Label { Id = "preferences-help", X = 2, Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(4), Height = 1, Text = Pref("inspection_help") });
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); _preferencesReturnToCategory = true; content.SetFocus();
    }

    private string[] FormatPreferenceInspection(string queryName, string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var lines = new List<string>();
            var root = document.RootElement;
            if (queryName == "context" && root.TryGetProperty("snapshot", out var snapshot)
                && snapshot.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined)
                return [Pref("context_empty")];
            if (queryName == "skills" && root.TryGetProperty("workspaceTrusted", out var trusted))
                lines.Add(Pref("workspace_trust") + ": " + (trusted.ValueKind == System.Text.Json.JsonValueKind.True ? Pref("trusted") : Pref("untrusted")));
            Append(root, "", 0);
            return lines.Count == 0 ? [Pref("no_data")] : lines.ToArray();

            void Append(System.Text.Json.JsonElement element, string label, int depth)
            {
                var indent = new string(' ', Math.Min(depth, 5) * 2);
                switch (element.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.Object:
                        foreach (var property in element.EnumerateObject())
                        {
                            if (property.Name is "manifestSha256" or "packageSha256" or "session") continue;
                            var propertyLabel = InspectionLabel(property.Name);
                            if (property.Value.ValueKind is System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Array)
                            {
                                if (property.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    var count = property.Value.GetArrayLength();
                                    lines.Add(indent + propertyLabel + " · " + count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                                    var index = 1;
                                    foreach (var item in property.Value.EnumerateArray())
                                    {
                                        if (item.ValueKind is not (System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Array))
                                        {
                                            lines.Add(indent + "  · " + InspectionScalar(property.Name, item));
                                            index++;
                                            continue;
                                        }
                                        var itemTitle = item.ValueKind == System.Text.Json.JsonValueKind.Object
                                            ? ReadText(item, "displayName") ?? ReadText(item, "id") ?? (index++).ToString(System.Globalization.CultureInfo.InvariantCulture)
                                            : (index++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                                        lines.Add(indent + "  · " + itemTitle);
                                        Append(item, "", depth + 2);
                                    }
                                }
                                else Append(property.Value, propertyLabel, depth + 1);
                            }
                            else lines.Add(indent + propertyLabel + ": " + InspectionScalar(property.Name, property.Value));
                        }
                        break;
                    case System.Text.Json.JsonValueKind.Array:
                        foreach (var item in element.EnumerateArray()) Append(item, label, depth + 1);
                        break;
                    case System.Text.Json.JsonValueKind.String:
                        if (label.Length > 0) lines.Add(indent + label + ": " + Scalar(element));
                        break;
                }
            }
        }
        catch (System.Text.Json.JsonException) { return [Pref("invalid_data")]; }
    }

    private string InspectionLabel(string key) => key switch
    {
        "workspaceTrusted" => Pref("workspace_trust"), "records" => Pref("records"), "extensions" => Pref("extensions"),
        "skills" => Pref("skills"), "snapshot" => Pref("context_snapshot"), "id" => Pref("id"),
        "displayName" => Pref("display_name"), "version" => Pref("version"), "scope" => Pref("scope_label"),
        "trust" => Pref("trust"), "status" or "state" => Pref("state"), "diagnostic" => Pref("diagnostic"),
        "kind" => Pref("kind"), "source" => Pref("source"), "importance" => Pref("importance"),
        "confidence" => Pref("confidence"), "createdAt" => Pref("created"), "tags" => Pref("tags"),
        _ => key,
    };

    private string InspectionScalar(string key, System.Text.Json.JsonElement value)
    {
        if (value.ValueKind != System.Text.Json.JsonValueKind.String) return Scalar(value);
        var text = value.GetString() ?? "";
        if (key == "scope") return Pref("scope_" + text.ToLowerInvariant()) is { } translated && translated != "preferences.ui.scope_" + text.ToLowerInvariant() ? translated : text;
        if (key is "trust" or "workspaceTrusted") return text.ToLowerInvariant() switch
        {
            "trusted" or "true" => Pref("trusted"), "untrusted" or "false" => Pref("untrusted"), _ => text,
        };
        return text;
    }

    private static string? ReadText(System.Text.Json.JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() : null;

    private static string Scalar(System.Text.Json.JsonElement value) => value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => value.GetString() ?? "",
        System.Text.Json.JsonValueKind.True => "true", System.Text.Json.JsonValueKind.False => "false",
        System.Text.Json.JsonValueKind.Null => "—", _ => value.ToString(),
    };

    private void ShowLocalEngineList(View contentPanel, int y)
    {
        // Re-read on each visit so external edits and revision conflicts have a recovery path.
        try { _localEngineSnapshot = _localEngines.Read(CancellationToken.None); }
        catch { _localEngineSnapshot = null; }
        var snapshot = _localEngineSnapshot;
        if (snapshot is null)
        {
            contentPanel.Add(new Label { Id = "local-engine-error", X = 2, Y = y, Width = Dim.Fill(4),
                Height = 2, Text = LocalEngineError("unavailable") });
            return;
        }
        var rows = snapshot.Engines.ToArray();
        var listHeight = Math.Max(2, Math.Min(6, contentPanel.Frame.Height - y - 10));
        var list = new ListView { Id = "local-engine-list", X = 2, Y = y,
            Width = Dim.Fill(4), Height = listHeight,
            CanFocus = rows.Length > 0, ShowMarks = false };
        list.SetSource(new ObservableCollection<string>(rows.Select(row => LocalEngineRowText(row))));
        var selectedIndex = _localEngineSelectedProviderId is null ? -1
            : Array.FindIndex(rows, row => row.ProviderId == _localEngineSelectedProviderId);
        if (rows.Length > 0) list.SelectedItem = selectedIndex >= 0 ? selectedIndex : 0;
        var listBottom = y + listHeight;
        var status = new Label { Id = "local-engine-runtime-status", X = 2, Y = listBottom + 1,
            Width = Dim.Fill(4), Height = 2, Text = LocalEngineStatusText(SelectedRow()) };
        var actionY = listBottom + 3;
        var add = new Button { Id = "local-engine-add", X = 2, Y = actionY,
            Width = Dim.Percent(47), Height = 2, TextAlignment = Alignment.Start, Text = Pref("local_add"),
            Enabled = snapshot.Diagnostics.Count == 0 };
        add.Accepted += (_, _) => ShowLocalEngineEditor(snapshot, null);
        var edit = new Button { Id = "local-engine-edit", X = Pos.Right(add) + 1, Y = actionY,
            Width = Dim.Percent(47), Height = 2, TextAlignment = Alignment.Start, Text = Pref("local_edit") };
        var runtime = new Button { Id = "local-engine-runtime", X = 2, Y = actionY + 2,
            Width = Dim.Percent(47), Height = 2, TextAlignment = Alignment.Start, Text = Pref("local_runtime_check") };
        var delete = new Button { Id = "local-engine-delete", X = Pos.Right(runtime) + 1, Y = actionY + 2,
            Width = Dim.Percent(47), Height = 2, TextAlignment = Alignment.Start, Text = Pref("local_delete") };
        var notice = new Label { Id = "local-engine-notice", X = 2, Y = actionY + 5,
            Width = Dim.Fill(4), Height = 3, Text = _localEngineNotice ?? LocalEngineDiagnostics(snapshot) };
        void SyncSelection()
        {
            var row = SelectedRow();
            var runtimeState = ReadLocalEngineRuntimeStatus(row);
            _localEngineSelectedProviderId = row?.ProviderId;
            edit.Enabled = row?.CanEdit == true;
            delete.Enabled = row?.CanDelete == true;
            runtime.Enabled = _localEngineSupervisor is not null && row is not null
                && runtimeState is not null && !runtimeState.Transitioning;
            runtime.Text = row?.Mode == "attach" ? Pref("local_runtime_check")
                : runtimeState is { ManagedOwned: true, Running: true } ? Pref("local_runtime_stop") : Pref("local_runtime_start");
            status.Text = LocalEngineStatusText(row, runtimeState);
        }
        LocalEngineConfigurationRow? SelectedRow() => list.SelectedItem is int index
            && index >= 0 && index < rows.Length ? rows[index] : null;
        void OpenEditor()
        {
            if (list.SelectedItem is int index && index >= 0 && index < rows.Length)
            {
                _localEngineSelectedProviderId = rows[index].ProviderId;
                ShowLocalEngineEditor(snapshot, rows[index]);
            }
        }
        edit.Accepted += (_, _) => OpenEditor();
        list.Accepted += (_, args) => { args.Handled = true; OpenEditor(); };
        list.ValueChanged += (_, _) => SyncSelection();
        runtime.Accepted += (_, _) =>
        {
            if (SelectedRow() is { } row && _localEngineSupervisor is not null)
            {
                var state = ReadLocalEngineRuntimeStatus(row);
                var stop = row.Mode == "managed" && state is { ManagedOwned: true, Running: true };
                ShowLocalEngineRuntimeConfirmation(row, stop);
            }
        };
        delete.Accepted += (_, _) =>
        {
            if (SelectedRow() is { CanDelete: true } row)
                ShowLocalEngineDeleteConfirmation(row, snapshot);
        };
        if (rows.Length == 0)
            contentPanel.Add(new Label { Id = "local-engine-empty", X = 2, Y = y - 1,
                Width = Dim.Fill(4), Height = 1, Text = Pref("local_empty") });
        contentPanel.Add(list, status, notice, add, edit, runtime, delete);
        ConfigurePreferenceScroll(contentPanel, actionY + 8);
        SyncSelection();
    }

    private string LocalEngineDiagnostics(LocalEnginePreferencesSnapshot snapshot)
    {
        if (snapshot.Diagnostics.Count > 0)
        {
            var key = snapshot.Diagnostics[0].StartsWith("localEngines.error.", StringComparison.Ordinal)
                ? snapshot.Diagnostics[0]["localEngines.error.".Length..] : "unavailable";
            return LocalEngineError(key);
        }
        return _localEngineSupervisor is null ? Pref("local_runtime_supervisorUnavailable") : Pref("local_reload");
    }

    private string LocalEngineStatusText(LocalEngineConfigurationRow? row)
    {
        if (row is null) return Pref("local_runtime_select");
        if (_localEngineSupervisor is null) return Pref("local_runtime_supervisorUnavailable");
        return LocalEngineStatusText(row, ReadLocalEngineRuntimeStatus(row));
    }

    private string LocalEngineStatusText(LocalEngineConfigurationRow? row, LocalEngineRuntimeStatus? status)
    {
        if (status is null) return LocalEngineRuntimeText("error_unavailable");
        return LocalEngineRuntimeTextFromWire(status.StateKey);
    }

    private LocalEngineRuntimeStatus? ReadLocalEngineRuntimeStatus(LocalEngineConfigurationRow? row)
    {
        if (row is null || _localEngineSupervisor is null) return null;
        try { return _localEngineSupervisor.Read(CancellationToken.None)
            .FirstOrDefault(item => item.ProviderId == row.ProviderId); }
        catch { return null; }
    }

    private string LocalEngineRuntimeText(string key) => _localization.Resolve("preferences.ui.local_runtime_" + key);

    private string LocalEngineRuntimeTextFromWire(string key)
    {
        const string prefix = "localEngines.runtime.";
        return key.StartsWith(prefix, StringComparison.Ordinal)
            ? LocalEngineRuntimeText(key[prefix.Length..].Replace('.', '_')) : LocalEngineRuntimeText("error_unavailable");
    }

    private void ShowLocalEngineDeleteConfirmation(LocalEngineConfigurationRow row,
        LocalEnginePreferencesSnapshot snapshot)
    {
        if (_window is null || _overlay is null) return;
        CloseOverlay();
        var frame = OverlayFrame(Pref("local_title") + " › " + row.ProviderId, 12);
        var message = new Label { Id = "local-engine-delete-question", X = 2, Y = 2,
            Width = Dim.Fill(4), Height = 3, Text = Pref("local_error_removeConfirm") };
        var status = new Label { Id = "local-engine-delete-status", X = 2, Y = 6,
            Width = Dim.Fill(4), Height = 2, Text = "" };
        var cancel = new Button { Id = "local-engine-delete-cancel", X = 2, Y = Pos.AnchorEnd(2), Text = Pref("local_cancel") };
        cancel.Accepted += (_, _) => { CloseOverlay(); ShowPreferences("local_engines", categoryPage: true); };
        var confirm = new Button { Id = "local-engine-delete-confirm", X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(2), Text = Pref("local_confirm") };
        confirm.Accepted += (_, _) =>
        {
            try
            {
                _localEngineSnapshot = _localEngines.Delete(row.ProviderId, snapshot.Revision, CancellationToken.None);
                _localEngineSelectedProviderId = null;
                _localEngineNotice = Pref("local_error_deleteSuccess");
                CloseOverlay(); ShowPreferences("local_engines", categoryPage: true);
            }
            catch (LocalEngineConfigurationException error)
            {
                var key = error.MessageKey.StartsWith("localEngines.error.", StringComparison.Ordinal)
                    ? error.MessageKey["localEngines.error.".Length..] : "unavailable";
                status.Text = LocalEngineError(key);
            }
            catch { status.Text = LocalEngineError("unavailable"); }
        };
        frame.Add(message, status, cancel, confirm);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); _preferencesReturnToCategory = true; confirm.SetFocus();
    }

    private void ShowLocalEngineRuntimeConfirmation(LocalEngineConfigurationRow row, bool stop)
    {
        if (_window is null || _overlay is null || _localEngineSupervisor is null) return;
        CloseOverlay();
        var frame = OverlayFrame(Pref("local_title") + " › " + row.ProviderId, 12);
        var messageKey = stop ? "local_runtime_stopConfirm" : row.Mode == "managed"
            ? "local_runtime_startConfirm" : "local_runtime_readyConfirm";
        var message = new Label { Id = "local-engine-runtime-question", X = 2, Y = 2,
            Width = Dim.Fill(4), Height = 3, Text = Pref(messageKey) };
        var status = new Label { Id = "local-engine-runtime-result", X = 2, Y = 6,
            Width = Dim.Fill(4), Height = 2, Text = "" };
        var cancel = new Button { Id = "local-engine-runtime-cancel", X = 2, Y = Pos.AnchorEnd(2), Text = Pref("local_cancel") };
        cancel.Accepted += (_, _) => { CloseOverlay(); ShowPreferences("local_engines", categoryPage: true); };
        var confirm = new Button { Id = "local-engine-runtime-confirm", X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(2), Text = Pref("local_confirm") };
        confirm.Accepted += (_, _) =>
        {
            if (_localEngineRuntimeTask is { IsCompleted: false }) return;
            confirm.Enabled = false;
            cancel.Text = Pref("local_runtime_close");
            message.Text = Pref("local_runtime_dismiss");
            status.Text = Pref("local_runtime_runningAction");
            var supervisor = _localEngineSupervisor;
            var operation = Task.Run(() => stop
                ? supervisor.Stop(row.ProviderId, CancellationToken.None)
                : supervisor.EnsureReady(row.ProviderId, CancellationToken.None));
            _localEngineRuntimeTask = operation;
            _ = operation.ContinueWith(completed => _uiActions.Enqueue(() =>
            {
                if (!ReferenceEquals(_overlay, frame)) return;
                confirm.Enabled = true;
                if (completed.IsCompletedSuccessfully)
                    status.Text = LocalEngineRuntimeTextFromWire(completed.Result.StateKey);
                else status.Text = LocalEngineRuntimeText("actionFailed");
                if (completed.IsCompletedSuccessfully && completed.Result.Success)
                    _localEngineNotice = LocalEngineRuntimeTextFromWire(completed.Result.StateKey);
            }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        };
        frame.Add(message, status, cancel, confirm);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); _preferencesReturnToCategory = true; confirm.SetFocus();
    }

    private string LocalEngineRowText(LocalEngineConfigurationRow row)
    {
        var mode = row.Mode == "managed" ? Pref("local_managed") : Pref("local_attach");
        var endpoint = row.BaseUrl;
        return row.ProviderId + " · " + mode + " · " + endpoint
            + (row.CanEdit ? "" : " · " + LocalEngineError("sensitive"));
    }

    private string LocalEngineError(string code) => _localization.Resolve("preferences.ui.local_error_" + code);

    private void ShowLocalEngineEditor(LocalEnginePreferencesSnapshot snapshot, LocalEngineConfigurationRow? existing)
    {
        if (_overlay is null || _window is null) return;
        CloseOverlay();
        var frame = OverlayFrame(Pref("local_title") + " › " + (existing?.ProviderId ?? Pref("local_add")), _window.Frame.Height - 3);
        frame.Width = Dim.Percent(82);
        frame.Height = Dim.Fill(2);
        var y = 2;
        void Field(string id, string label, string? value, out TextField field)
        {
            frame.Add(new Label { X = 2, Y = y, Width = 22, Height = 1, Text = label });
            field = new TextField { Id = id, X = 25, Y = y++, Width = Dim.Fill(4), Height = 1, Text = value ?? "" };
            frame.Add(field);
        }
        Field("local-engine-id", Pref("local_id"), existing?.ProviderId, out var providerId);
        providerId.ReadOnly = existing is not null;
        var mode = existing?.Mode ?? "attach";
        var attachDefaultEndpoint = existing is null ? "http://127.0.0.1:8080" : null;
        var modeButton = new Button { Id = "local-engine-mode", X = 2, Y = y++, Width = Dim.Fill(4),
            TextAlignment = Alignment.Start, Text = Pref("local_mode") + ": " + LocalEngineMode(mode) };
        frame.Add(modeButton);
        Field("local-engine-endpoint", Pref("local_endpoint"), existing?.BaseUrl
            ?? (mode == "managed" ? "auto" : "http://127.0.0.1:8080"), out var endpoint);
        modeButton.Accepted += (_, _) =>
        {
            mode = mode == "attach" ? "managed" : "attach";
            if (mode == "managed" && (endpoint.Text.ToString() == attachDefaultEndpoint || endpoint.Text.ToString().Length == 0))
                endpoint.Text = "auto";
            else if (mode == "attach" && endpoint.Text.ToString() == "auto")
                endpoint.Text = attachDefaultEndpoint ?? "http://127.0.0.1:8080";
            modeButton.Text = Pref("local_mode") + ": " + LocalEngineMode(mode);
        };
        Field("local-engine-executable", Pref("local_executable"), existing?.Executable, out var executable);
        var argsLabel = new Label { X = 2, Y = y, Width = 22, Height = 1, Text = Pref("local_arguments") };
        var args = new TextView { Id = "local-engine-arguments", X = 25, Y = y + 1, Width = Dim.Fill(4), Height = 2,
            WordWrap = false, Text = EncodeLocalArguments(existing?.Arguments ?? []) };
        frame.Add(argsLabel, args, new Label { Id = "local-engine-arguments-help", X = 2, Y = y + 3,
            Width = Dim.Fill(4), Height = 2, Text = Pref("local_arguments_help") });
        y += 5;
        Field("local-engine-directory", Pref("local_directory"), existing?.WorkingDirectory, out var directory);
        Field("local-engine-timeout", Pref("local_timeout"), (existing?.ReadinessTimeoutSeconds ?? 120).ToString(System.Globalization.CultureInfo.InvariantCulture), out var timeout);
        var notice = new Label { Id = "local-engine-notice", X = 2, Y = Pos.AnchorEnd(7), Width = Dim.Fill(4), Height = 3 };
        var cancel = new Button { Id = "local-engine-cancel", X = 2, Y = Pos.AnchorEnd(2), Text = Pref("cancel") };
        cancel.Accepted += (_, _) => { CloseOverlay(); ShowPreferences("local_engines", categoryPage: true); };
        var save = new Button { Id = "local-engine-save", X = Pos.AnchorEnd(13), Y = Pos.AnchorEnd(2), Text = Pref("save") };
        save.Accepted += (_, _) =>
        {
            if (!int.TryParse(timeout.Text.ToString(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var timeoutValue))
            { notice.Text = LocalEngineError("invalidTimeout"); timeout.SetFocus(); return; }
            var draft = new LocalEngineConfigurationDraft(providerId.Text.ToString(), mode,
                endpoint.Text.ToString(),
                mode == "managed" ? executable.Text.ToString() : null,
                DecodeLocalArguments(args.Text.ToString()),
                directory.Text.ToString(), timeoutValue);
            try
            {
                _localEngineSnapshot = _localEngines.Save(draft, snapshot.Revision, CancellationToken.None);
                _localEngineNotice = Pref("local_saved");
                CloseOverlay(); ShowPreferences("local_engines", categoryPage: true);
            }
            catch (LocalEngineConfigurationException error)
            {
                var key = error.MessageKey.StartsWith("localEngines.error.", StringComparison.Ordinal)
                    ? error.MessageKey["localEngines.error.".Length..] : "unavailable";
                notice.Text = LocalEngineError(key);
            }
            catch { notice.Text = LocalEngineError("unavailable"); }
        };
        frame.Add(notice, cancel, save,
            new Label { Id = "preferences-help", X = 2, Y = Pos.AnchorEnd(4), Width = Dim.Fill(4),
                Height = 2, Text = Pref("local_reload") });
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); _preferencesReturnToCategory = true; providerId.SetFocus();
    }

    private string LocalEngineMode(string mode) => mode == "managed" ? Pref("local_managed") : Pref("local_attach");

    internal static string EncodeLocalArguments(IReadOnlyList<string> arguments) => string.Join("\n",
        arguments.Select(argument => argument.Length == 0 ? "\"\""
            : argument == "\"\"" || argument.StartsWith('\\') ? "\\" + argument : argument));

    internal static IReadOnlyList<string> DecodeLocalArguments(string text)
    {
        if (text.Length == 0) return [];
        return text.Replace("\r", "", StringComparison.Ordinal).Split('\n')
            .Select(argument => argument == "\"\"" ? "" : argument.StartsWith('\\') ? argument[1..] : argument).ToArray();
    }

    private void ApplyPreferenceLocale(string locale, FrameView oldFrame, bool categoryPage, bool refreshIfUnchanged = false)
    {
        if (_localeSelection?.HasProcessOverride == true) return;
        locale = UiLocaleCodes.NormalizeOrDefault(locale);
        if (locale == _locale)
        {
            _preferencesDraftLocale = locale;
            if (refreshIfUnchanged && categoryPage)
            {
                CloseOverlay();
                ShowPreferences(_preferencesCategory, categoryPage: true);
            }
            return;
        }
        _locale = locale;
        _localization = new Localization(locale);
        _projection = new ClientProjection(_localization);
        _localeChanged?.Invoke(locale);
        _preferencesDraftLocale = locale;
        // Preserve the active ClientState and conversation buffer; only presentation resources change.
        if (categoryPage)
        {
            CloseOverlay();
            ShowPreferences(_preferencesCategory, categoryPage: true);
        }
    }

    private void ClosePreferenceCenter()
    {
        CancelPreferenceLocaleDraft();
        _preferencesOriginalLocale = "";
        CloseOverlay();
    }

    private void HandlePreferencesEscape()
    {
        if (_preferencesCategoryPage)
        {
            if (_preferencesCategory == "general") CancelPreferenceLocaleDraft();
            CloseOverlay();
            ShowPreferences(_preferencesCategory);
            return;
        }
        ClosePreferenceCenter();
    }

    private void ReturnFromPreferencesSubpage()
    {
        if (_returnToClaudeAccounts || _returnToChatGptAccount) { ReturnFromAccountSubpage(); return; }
        if (_returnToProviderConnections)
        {
            _returnToProviderConnections = false;
            var providerId = _returnProviderId;
            CloseOverlay(); ShowProviderConnections(selectedProviderId: providerId);
            return;
        }
        CloseOverlay();
        ShowPreferences(_preferencesCategory, categoryPage: true);
    }

    private void CancelPreferenceLocaleDraft()
    {
        if (_localeSelection?.HasProcessOverride != true && _preferencesOriginalLocale.Length > 0
            && _preferencesDraftLocale != (_preferencesLocaleSnapshot?.Locale ?? _preferencesOriginalLocale))
            RevertPreferenceLocale();
        _preferencesDraftLocale = _preferencesLocaleSnapshot?.Locale ?? _locale;
        _preferencesOriginalLocale = _locale;
    }

    private void RevertPreferenceLocale()
    {
        if (_localeSelection?.HasProcessOverride == true || _locale == _preferencesOriginalLocale) return;
        _locale = _preferencesOriginalLocale;
        _localization = new Localization(_locale);
        _projection = new ClientProjection(_localization);
        _localeChanged?.Invoke(_locale);
    }

    private void SavePreferenceLocale()
    {
        if (_client is not ITrustedUserActionClient trusted || _preferencesLocaleSnapshot is null)
        {
            if (_preferencesNotice is not null) _preferencesNotice.Text = Pref("save_error");
            return;
        }
        var ack = trusted.SendUserAction(UiLocalePreferencesProtocol.Configure("User", _preferencesDraftLocale,
            _preferencesLocaleSnapshot.Revision), CancellationToken.None);
        if (ack.Status != "ok")
        {
            if (_preferencesNotice is not null) _preferencesNotice.Text = ack.Error is { } error
                    && error.StartsWith("ui.locale.error.", StringComparison.Ordinal)
                ? _localization.ResolveWire(error) : Pref("save_error");
            if (_preferencesReload is not null && ack.Error is "ui.locale.error.stale" or "ui.locale.error.revision_required")
                _preferencesReload.Enabled = true;
            return;
        }
        if (_preferencesReload is not null) _preferencesReload.Enabled = false;
        _preferencesOriginalLocale = _locale;
        var result = _client.Query(UiLocalePreferencesProtocol.QueryName, CancellationToken.None);
        if (result is not null)
        {
            try { _preferencesLocaleSnapshot = UiLocalePreferencesProtocol.Decode(result.Json); }
            catch (System.Text.Json.JsonException) { }
        }
        if (_localeSelection?.HasProcessOverride != true && _preferencesLocaleSnapshot?.IsConfigured == true)
            _localeSelection = new LocaleSelection(_preferencesLocaleSnapshot.Locale, LocaleSelectionSource.UserSetting);
        var savedLabel = FindPreferenceView(_overlay, "preferences-locale-saved") as Label;
        if (savedLabel is not null)
            savedLabel.Text = Pref("preference_label") + ": " + Pref(_preferencesLocaleSnapshot?.IsConfigured == true
                ? "preference_saved" : "preference_default") + " · " + (_preferencesLocaleSnapshot?.Locale ?? _locale);
        var sourceLabel = FindPreferenceView(_overlay, "preferences-locale-source") as Label;
        if (sourceLabel is not null)
        {
            var sourceKey = _localeSelection?.HasProcessOverride == true
                ? _localeSelection.Source == LocaleSelectionSource.CommandLine ? "source_cli" : "source_env"
                : _preferencesLocaleSnapshot?.IsConfigured == true ? "source_user" : "source_default";
            sourceLabel.Text = Pref("source_label") + ": " + Pref(sourceKey) + " · " + Pref("locale_" + _locale);
        }
        if (_preferencesNotice is not null) _preferencesNotice.Text = Pref("saved");
    }

    private void ReloadPreferenceLocale(FrameView frame)
    {
        var result = _client.Query(UiLocalePreferencesProtocol.QueryName, CancellationToken.None);
        if (result is null)
        {
            if (_preferencesNotice is not null) _preferencesNotice.Text = Pref("save_error");
            return;
        }
        try { _preferencesLocaleSnapshot = UiLocalePreferencesProtocol.Decode(result.Json); }
        catch (System.Text.Json.JsonException)
        {
            if (_preferencesNotice is not null) _preferencesNotice.Text = Pref("save_error");
            return;
        }
        if (_preferencesLocaleSnapshot is null)
        {
            if (_preferencesNotice is not null) _preferencesNotice.Text = Pref("save_error");
            return;
        }
        if (_localeSelection?.HasProcessOverride != true)
            _localeSelection = _preferencesLocaleSnapshot.IsConfigured
                ? new LocaleSelection(_preferencesLocaleSnapshot.Locale, LocaleSelectionSource.UserSetting)
                : new LocaleSelection(_preferencesLocaleSnapshot.Locale, LocaleSelectionSource.Default);
        _preferencesDraftLocale = _preferencesLocaleSnapshot.Locale;
        if (_localeSelection?.HasProcessOverride != true)
        {
            _preferencesOriginalLocale = _preferencesLocaleSnapshot.Locale;
            ApplyPreferenceLocale(_preferencesDraftLocale, frame, categoryPage: true, refreshIfUnchanged: true);
        }
        else
        {
            if (_preferencesNotice is not null) _preferencesNotice.Text = Pref("saved");
            if (_preferencesReload is not null) _preferencesReload.Enabled = false;
            CloseOverlay(); ShowPreferences(_preferencesCategory, categoryPage: true);
        }
    }

    private static View? FindPreferenceView(View? root, string id)
    {
        if (root is null) return null;
        if (root.Id == id) return root;
        foreach (var child in root.SubViews)
            if (FindPreferenceView(child, id) is { } match) return match;
        return null;
    }
}
#pragma warning restore CS0618
