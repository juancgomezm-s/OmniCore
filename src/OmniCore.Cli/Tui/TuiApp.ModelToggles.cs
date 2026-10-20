using OmniCore.Host;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    // Both screens use the same identities, including models not yet registered from OpenRouter.
    // Visibility is a user preference, independent of policies and the active conversation.
    private static ModelRegistryModelDescriptor[] AllPickerModels(
        IEnumerable<ModelRegistryModelDescriptor> configured, IEnumerable<OpenRouterPickerModel> cached) =>
        configured.Concat(cached.Select(model => new ModelRegistryModelDescriptor(model.Id, model.ProviderId, 0, 0, 0)))
            .DistinctBy(model => (model.ProviderId, model.Id)).ToArray();

    private void ShowModelToggles(string query = "", ModelPolicyKeyDto? highlighted = null)
    {
        if (_window is null || _overlay is not null) return;
        _policies.ReloadModels();
        _preferencesCategory = "models_execution";
        IReadOnlyList<OpenRouterPickerModel> cached = [];
        ProviderConnectionMenuRow[] providers = [];
        string? error = null;
        try
        {
            if (_providerConnectionHost is ITuiOpenRouterCatalogHost catalog)
            {
                cached = catalog.CachedOpenRouterPickerModels(CancellationToken.None);
                providers = _providerConnectionHost.List(CancellationToken.None)
                    .Where(row => row.Backend == "openrouter" && row.CanDiscoverModels).ToArray();
            }
        }
        catch (Exception) { error = _localization.Resolve("openrouter.catalog.failed"); }
        var all = AllPickerModels(_policies.Models, cached);
        var models = all;
        var frame = OverlayFrame(_localization.Resolve("shell.models.enable"), 20);
        frame.Id = "model-toggles";
        var feedback = new Label { Id = "omni-palette-feedback", X = 2, Y = 1, Width = Dim.Fill(4) };
        var search = new TextField { Id = "model-toggle-search", X = 10, Y = 2, Width = Dim.Fill(4), Text = query };
        var rows = new FrameView { Id = "model-toggle-rows", X = 1, Y = 4, Width = Dim.Fill(2),
            Height = Dim.Fill(4), BorderStyle = Terminal.Gui.Drawing.LineStyle.None };
        var pageSize = Math.Max(1, Math.Min(20, _window.Frame.Height - 2) - 8);
        var position = 0;
        var offset = 0;
        var buttons = new Dictionary<int, Button>();
        string Name(ModelRegistryModelDescriptor model) => CatalogDisplay(_policies.DisplayName(model.Id)
            ?? cached.FirstOrDefault(row => row.ProviderId == model.ProviderId && row.Id == model.Id)?.Name ?? model.Id, 160);
        string ToggleText(ModelRegistryModelDescriptor model) =>
            (_policies.IsVisibleInPicker(model.ProviderId, model.Id) ? "[   █]" : "[█   ]")
            + "  " + CatalogDisplay(model.ProviderId, 80) + " · " + CatalogDisplay(model.Id, 160);
        void UpdateFeedback() => feedback.Text = error ?? _localization.Resolve("shell.models.count",
            new Dictionary<string, string> { ["enabled"] = all.Count(model => _policies.IsVisibleInPicker(model.ProviderId, model.Id)).ToString(),
                ["total"] = all.Length.ToString() });
        void Toggle(int index)
        {
            var model = models[index];
            try
            {
                var visible = _policies.IsVisibleInPicker(model.ProviderId, model.Id);
                _policies.SetVisibleInPicker(model.ProviderId, model.Id, !visible, CancellationToken.None);
                buttons[index].Text = ToggleText(model);
                error = null;
            }
            catch (Exception) { error = _localization.Resolve("shell.models.saveFailed"); }
            UpdateFeedback();
        }
        void FocusRow(int index)
        {
            if (models.Length == 0) return;
            position = (index + models.Length) % models.Length;
            if (!buttons.ContainsKey(position))
            {
                offset = position < offset ? position : position - pageSize + 1;
                RenderRows();
            }
            buttons[position].SetFocus();
        }
        void RenderRows()
        {
            var previousRows = rows.SubViews.ToArray();
            rows.RemoveAll();
            _retiredOverlays.AddRange(previousRows);
            buttons.Clear();
            for (var index = offset; index < Math.Min(models.Length, offset + pageSize); index++)
            {
                var at = index;
                var model = models[at];
                var button = new Button { Id = "model-toggle-" + model.ProviderId + "/" + model.Id,
                    X = 0, Y = at - offset, Width = Dim.Fill(), TextAlignment = Alignment.Start,
                    Text = ToggleText(model) };
                buttons[at] = button;
                button.Accepted += (_, args) => { args.Handled = true; position = at; Toggle(at); };
                button.KeyDown += (_, key) =>
                {
                    if (key.KeyCode is KeyCode.CursorDown or KeyCode.CursorUp)
                    { FocusRow(at + (key.KeyCode == KeyCode.CursorDown ? 1 : -1)); key.Handled = true; }
                    else if (key.KeyCode is KeyCode.Enter or KeyCode.Space)
                    { Toggle(at); key.Handled = true; }
                    else if (key.KeyCode == (KeyCode.F | KeyCode.CtrlMask))
                    { search.SetFocus(); key.Handled = true; }
                };
                button.MouseEvent += (_, mouse) =>
                {
                    var delta = mouse.Flags.HasFlag(MouseFlags.WheeledDown) ? 1 : mouse.Flags.HasFlag(MouseFlags.WheeledUp) ? -1 : 0;
                    if (delta == 0) return;
                    FocusRow(at + delta); mouse.Handled = true;
                };
                rows.Add(button);
            }
            ApplyTheme(rows, preferences: true);
        }
        void Filter()
        {
            var text = search.Text.ToString().Trim();
            models = all.Where(model => (model.ProviderId + " " + model.Id + " " + Name(model))
                .Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
            offset = position = 0;
            RenderRows();
            UpdateFeedback();
            if (models.Length == 0 && error is null)
                feedback.Text = _localization.Resolve(all.Length > 0 ? "shell.no_matches_change_the_search"
                    : providers.Length > 0 ? "openrouter.picker.notLoaded" : "shell.no_visible_models_f4_to_configure");
        }
        search.TextChanged += (_, _) => Filter();
        search.KeyDown += (_, key) =>
        {
            if (key.KeyCode is KeyCode.Enter or KeyCode.CursorDown or KeyCode.CursorUp)
            { FocusRow(key.KeyCode == KeyCode.CursorUp ? models.Length - 1 : position); key.Handled = true; }
        };
        var help = new Label { Id = "omni-help", X = 2, Y = Pos.AnchorEnd(3), Width = Dim.Fill(4),
            Text = _localization.Resolve("shell.models.toggleHelp", "command", "/" + HumanCommandAliases.DisplayName("model", _locale)) };
        var back = new Button { Id = "model-toggles-back", X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(1),
            Text = _localization.Resolve("shell.close") };
        back.Accepted += (_, args) => { args.Handled = true; ReturnFromPreferencesSubpage(); };
        frame.Add(feedback, new Label { Id = "omni-heading", X = 2, Y = 2, Width = 8,
            Text = _localization.Resolve("shell.search") }, search, rows, help, back);
        if (providers.Length > 0)
        {
            var refresh = new Button { Id = "omni-palette-openrouter-refresh", X = 2, Y = Pos.AnchorEnd(1),
                Text = _localization.Resolve("openrouter.picker.refresh") };
            refresh.Accepted += (_, args) =>
            {
                args.Handled = true;
                RefreshOpenRouterPicker(frame, feedback, providers, search,
                    () => models.Length == 0 ? null : new(models[position].ProviderId, models[position].Id),
                    (text, selected) => { ShowModelToggles(text, selected); _preferencesReturnToCategory = true; });
            };
            frame.Add(refresh);
        }
        Filter();
        ApplyTheme(frame); _overlay = frame; _window.Add(frame);
        _preferencesReturnToCategory = true;
        var initial = highlighted is null ? 0 : Array.FindIndex(models,
            model => model.Id == highlighted.ModelId && model.ProviderId == highlighted.ProviderId);
        if (models.Length > 0) FocusRow(Math.Max(0, initial)); else search.SetFocus();
    }
}
