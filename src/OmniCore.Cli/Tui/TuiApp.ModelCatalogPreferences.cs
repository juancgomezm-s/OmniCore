#pragma warning disable CS0618 // TextView is the pinned Terminal.Gui multiline editor used by existing TUI forms.
namespace OmniCore.Cli;

using System.Collections.ObjectModel;
using System.Globalization;
using OmniCore.Client;
using OmniCore.Host;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

public sealed partial class TuiApp
{
    private IModelCatalogPreferencesHost? _modelCatalogPreferencesOverride;
    private ModelCatalogPreferencesSnapshot? _modelCatalogSnapshot;
    private string? _modelCatalogSelectedId;
    private string? _modelCatalogNotice;
    private string? _modelCatalogDeleteId;

    internal void SetModelCatalogPreferencesHost(IModelCatalogPreferencesHost host) =>
        _modelCatalogPreferencesOverride = host ?? throw new ArgumentNullException(nameof(host));

    internal void ShowModelCatalogPreferences()
    {
        if (_window is null || _overlay is not null) return;
        _modelCatalogSelectedId = null;
        _modelCatalogNotice = null;
        RefreshModelCatalog();
        RenderModelCatalog();
    }

    /// <summary>Called by the global Escape dispatcher before the preferences-center handler.</summary>
    internal bool HandleModelCatalogPreferencesEscape()
    {
        if (_overlay?.Id is not ("model-catalog-preferences" or "model-catalog-editor" or "model-catalog-confirm"))
            return false;
        var page = _overlay.Id;
        if (page == "model-catalog-confirm")
        {
            _modelCatalogDeleteId = null;
            CloseOverlay();
            RenderModelCatalog();
        }
        else if (page == "model-catalog-editor")
        {
            CloseOverlay();
            RenderModelCatalog();
        }
        else
        {
            ReturnToPreferenceCategory();
        }
        return true;
    }

    private IModelCatalogPreferencesHost ModelCatalogHost() => _modelCatalogPreferencesOverride
        ?? ModelCatalogPreferencesHost.Create(workspaceRoot: Environment.CurrentDirectory);

    private string ModelCatalogText(string key) => _localization.Resolve("modelCatalogPreferences." + key);

    private void RefreshModelCatalog()
    {
        try
        {
            _modelCatalogSnapshot = ModelCatalogHost().Read(CancellationToken.None);
            if (_modelCatalogSelectedId is not null
                && !_modelCatalogSnapshot.Models.Any(model => model.Id == _modelCatalogSelectedId))
                _modelCatalogSelectedId = null;
            if (_modelCatalogSnapshot.Diagnostics.Count > 0)
                _modelCatalogNotice = ModelCatalogError(_modelCatalogSnapshot.Diagnostics[0]);
        }
        catch (Exception)
        {
            _modelCatalogSnapshot = ModelCatalogPreferencesSnapshot.Unavailable(["modelCatalog.error.unavailable"]);
            _modelCatalogNotice = ModelCatalogText("unavailable");
        }
    }

    private void RenderModelCatalog()
    {
        if (_window is null || _overlay is not null) return;
        var snapshot = _modelCatalogSnapshot ?? ModelCatalogPreferencesSnapshot.Unavailable([]);
        var frame = OverlayFrame(ModelCatalogText("title"), _window.Frame.Height - 3);
        frame.Id = "model-catalog-preferences";
        frame.Width = Dim.Percent(92);
        frame.Height = Dim.Fill(2);
        frame.Add(new Label { Id = "model-catalog-intro", X = MenuIndent.Level0, Y = 1, Width = Dim.Fill(4), Height = 1,
            Text = ModelCatalogText("intro") });

        // The browser takes every free row; the text below it stays compact so large
        // catalogs (OpenRouter lists hundreds of models) remain navigable.
        var allModels = snapshot.Models;
        var models = allModels;
        var filterLabel = new Label { X = MenuIndent.Level0, Y = 2, Width = 9, Height = 1, Text = ModelCatalogText("filter") + ":" };
        var filter = new TextField { Id = "model-catalog-filter", X = MenuIndent.Level0 + 10, Y = 2, Width = Dim.Percent(40), Height = 1,
            CanFocus = snapshot.Available && allModels.Count > 0 };
        var position = new Label { Id = "model-catalog-position", X = Pos.AnchorEnd(32), Y = 2, Width = 28, Height = 1,
            TextAlignment = Alignment.End };
        var columns = new Label { Id = "model-catalog-columns", X = MenuIndent.Level1, Y = 3, Width = Dim.Fill(4), Height = 1,
            Text = ModelCatalogText("columns") };
        var list = new ListView { Id = "model-catalog-list", X = MenuIndent.Level1, Y = 4, Width = Dim.Fill(4), Height = Dim.Fill(7),
            CanFocus = snapshot.Available && allModels.Count > 0, ShowMarks = false,
            ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar };
        var idWidth = Math.Clamp(allModels.Count == 0 ? 10 : allModels.Max(model => model.Id.Length), 10, 48);
        list.SetSource(new ObservableCollection<string>(models.Select(model => ModelCatalogRowText(model, idWidth))));
        var selected = _modelCatalogSelectedId is null ? -1 : models.ToList()
            .FindIndex(model => string.Equals(model.Id, _modelCatalogSelectedId, StringComparison.Ordinal));
        if (models.Count > 0) list.SelectedItem = selected >= 0 ? selected : 0;
        frame.Add(filterLabel, filter, position, columns, list);

        var current = list.SelectedItem is int initialIndex && initialIndex >= 0 && initialIndex < models.Count ? models[initialIndex] : null;
        if (current is not null) _modelCatalogSelectedId = current.Id;
        var status = new Label { Id = "model-catalog-status", X = MenuIndent.Level0, Y = Pos.AnchorEnd(4), Width = Dim.Fill(4), Height = 1,
            Text = _modelCatalogNotice ?? (models.Count == 0 ? ModelCatalogText("empty") : ModelCatalogText("selectHint")) };
        var detailsText = current is null ? "" : string.Join("\n", new[]
        {
            ModelCatalogText("context") + ": " + current.Context.ToString(CultureInfo.InvariantCulture)
                + " · " + ModelCatalogText("maxOutput") + ": " + (current.MaxOutput?.ToString(CultureInfo.InvariantCulture) ?? _localization.Resolve("shell.state.off")),
            ModelCatalogText("aliases") + ": " + (current.Aliases.Count == 0 ? "—" : string.Join(", ", current.Aliases)),
            ModelCatalogText("inputModalities") + ": " + string.Join(", ", current.InputModalities ?? []),
            current.DeleteBlockers.Count == 0 ? "" : ModelCatalogText("deleteBlocked") + " "
                + string.Join("; ", current.DeleteBlockers.Select(ModelCatalogError))
        }.Where(line => line.Length > 0));
        var details = new Label { Id = "model-catalog-details", X = MenuIndent.Level1, Y = Pos.AnchorEnd(7), Width = Dim.Fill(4), Height = 2,
            Text = detailsText };
        var warning = new Label { Id = "model-catalog-warning", X = MenuIndent.Level0, Y = Pos.AnchorEnd(5), Width = Dim.Fill(4), Height = 1,
            Text = ModelCatalogText("otherFoldersWarning") };
        var add = new Button { Id = "model-catalog-add", X = MenuIndent.Level0, Y = Pos.AnchorEnd(2), Text = ModelCatalogText("add") };
        var edit = new Button { Id = "model-catalog-edit", X = Pos.Right(add) + 2, Y = Pos.AnchorEnd(2), Text = ModelCatalogText("edit") };
        var delete = new Button { Id = "model-catalog-delete", X = Pos.Right(edit) + 2, Y = Pos.AnchorEnd(2), Text = ModelCatalogText("delete") };
        var back = new Button { Id = "model-catalog-back", X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(2), Text = ModelCatalogText("back") };
        add.Enabled = snapshot.Available && snapshot.Providers.Count > 0;
        edit.Enabled = snapshot.Available && current is not null;
        delete.Enabled = snapshot.Available && current?.CanDelete == true;

        string Details(ModelCatalogModel? model) => model is null ? "" : string.Join("\n", new[]
        {
            ModelCatalogText("context") + ": " + model.Context.ToString("N0", CultureInfo.InvariantCulture)
                + " · " + ModelCatalogText("maxOutput") + ": " + (model.MaxOutput?.ToString("N0", CultureInfo.InvariantCulture) ?? _localization.Resolve("shell.state.off"))
                + " · " + ModelCatalogText("aliases") + ": " + (model.Aliases.Count == 0 ? "—" : string.Join(", ", model.Aliases)),
            ModelCatalogText("inputModalities") + ": " + string.Join(", ", model.InputModalities ?? []),
            model.DeleteBlockers.Count == 0 ? "" : ModelCatalogText("deleteBlocked") + " "
                + string.Join("; ", model.DeleteBlockers.Select(ModelCatalogError))
        }.Where(line => line.Length > 0));
        void UpdatePosition()
        {
            var index = list.SelectedItem is int at && at >= 0 && at < models.Count ? at + 1 : 0;
            var edge = models.Count == 0 ? "" : index <= 1 ? " · ▲ " + ModelCatalogText("atStart")
                : index >= models.Count ? " · ▼ " + ModelCatalogText("atEnd") : "";
            position.Text = (models.Count == 0 ? "0 / 0" : _localization.Resolve("modelCatalogPreferences.position", new Dictionary<string, string>
                { ["index"] = index.ToString(CultureInfo.InvariantCulture), ["total"] = models.Count.ToString(CultureInfo.InvariantCulture) }))
                + (models.Count != allModels.Count ? " (" + allModels.Count.ToString(CultureInfo.InvariantCulture) + ")" : "") + edge;
        }
        void ApplyFilter()
        {
            var keepId = _modelCatalogSelectedId;
            var term = filter.Text.ToString()?.Trim() ?? "";
            models = term.Length == 0 ? allModels : allModels.Where(model =>
                model.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
                || model.ProviderId.Contains(term, StringComparison.OrdinalIgnoreCase)
                || model.Aliases.Any(alias => alias.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
            list.SetSource(new ObservableCollection<string>(models.Select(model => ModelCatalogRowText(model, idWidth))));
            var next = models.ToList().FindIndex(model => string.Equals(model.Id, keepId, StringComparison.Ordinal));
            if (models.Count > 0) list.SelectedItem = next >= 0 ? next : 0;
            SelectCurrent();
            if (models.Count == 0 && allModels.Count > 0) status.Text = ModelCatalogText("noMatches");
        }
        filter.TextChanged += (_, _) => ApplyFilter();
        filter.KeyDown += (_, key) =>
        {
            if (key.KeyCode is KeyCode.CursorDown or KeyCode.Enter && models.Count > 0) { list.SetFocus(); key.Handled = true; }
        };
        list.KeyDown += (_, key) =>
        {
            // Continuous browsing: moving past either end wraps around instead of stopping.
            if (models.Count < 2) return;
            if (key.KeyCode == KeyCode.CursorDown && list.SelectedItem == models.Count - 1) { list.SelectedItem = 0; key.Handled = true; }
            else if (key.KeyCode == KeyCode.CursorUp && list.SelectedItem == 0) { list.SelectedItem = models.Count - 1; key.Handled = true; }
        };
        void SelectCurrent()
        {
            UpdatePosition();
            var selectedModel = list.SelectedItem is int index && index >= 0 && index < models.Count ? models[index] : null;
            _modelCatalogSelectedId = selectedModel?.Id;
            details.Text = Details(selectedModel);
            edit.Enabled = snapshot.Available && selectedModel is not null;
            delete.Enabled = snapshot.Available && selectedModel?.CanDelete == true;
            status.Text = selectedModel is null
                ? (models.Count == 0 ? ModelCatalogText("empty") : ModelCatalogText("selectHint"))
                : _modelCatalogNotice ?? "";
        }
        list.ValueChanged += (_, _) => SelectCurrent();
        list.Accepted += (_, e) =>
        {
            e.Handled = true;
            SelectCurrent();
            if (FindModel(_modelCatalogSelectedId) is { } model) ShowModelCatalogEditor(snapshot, model);
        };
        SelectCurrent();
        add.Accepted += (_, _) => ShowModelCatalogEditor(snapshot, null);
        edit.Accepted += (_, _) => { if (FindModel(_modelCatalogSelectedId) is { } model) ShowModelCatalogEditor(snapshot, model); };
        delete.Accepted += (_, _) => { if (FindModel(_modelCatalogSelectedId) is { CanDelete: true } model) ShowModelCatalogDeleteConfirm(model); };
        back.Accepted += (_, _) => ReturnToPreferenceCategory();
        frame.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; HandleModelCatalogPreferencesEscape(); } };
        frame.Add(details, status, warning, add, edit, delete, back);
        if (!snapshot.Available)
        {
            list.Enabled = add.Enabled = edit.Enabled = delete.Enabled = false;
            status.Text = _modelCatalogNotice ?? ModelCatalogText("catalogUnavailable");
        }
        ApplyTheme(frame);
        _overlay = frame;
        _window.Add(frame);
        if (list.CanFocus) list.SetFocus(); else back.SetFocus();
    }

    private string ModelCatalogRowText(ModelCatalogModel model, int idWidth)
    {
        var family = _modelCatalogSnapshot?.Providers.FirstOrDefault(provider => provider.Id == model.ProviderId)?.Family ?? "";
        var context = model.Context >= 1_000_000 ? (model.Context / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M"
            : model.Context >= 1_000 ? (model.Context / 1_000).ToString(CultureInfo.InvariantCulture) + "k"
            : model.Context.ToString(CultureInfo.InvariantCulture);
        var id = model.Id.Length > idWidth ? model.Id[..(idWidth - 1)] + "…" : model.Id.PadRight(idWidth);
        return id + "  " + (model.ProviderId + " (" + family + ")").PadRight(28) + "  " + context.PadLeft(6);
    }

    private ModelCatalogModel? FindModel(string? id) => id is null ? null
        : _modelCatalogSnapshot?.Models.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.Ordinal));

    private void ShowModelCatalogEditor(ModelCatalogPreferencesSnapshot snapshot, ModelCatalogModel? existing)
    {
        if (_window is null) return;
        CloseOverlay();
        var frame = OverlayFrame(ModelCatalogText(existing is null ? "newModel" : "edit")
            + (existing is null ? "" : " · " + existing.Id), _window.Frame.Height - 3);
        frame.Id = "model-catalog-editor";
        frame.Width = Dim.Percent(92);
        frame.Height = Dim.Fill(2);
        var providers = snapshot.Providers;
        var y = 2;
        TextField Field(string id, string labelKey, string value)
        {
            frame.Add(new Label { X = 2, Y = y, Width = 22, Height = 1, Text = ModelCatalogText(labelKey) });
            var field = new TextField { Id = id, X = 25, Y = y++, Width = Dim.Fill(4), Height = 1, Text = value };
            frame.Add(field);
            return field;
        }

        var modelId = Field("model-catalog-id", "modelId", existing?.Id ?? "");
        modelId.ReadOnly = existing is not null;
        var provider = Field("model-catalog-provider", "provider", existing?.ProviderId ?? providers.FirstOrDefault()?.Id ?? "");
        if (existing is not null)
        {
            var family = providers.FirstOrDefault(item => item.Id == existing.ProviderId)?.Family ?? "";
            frame.Add(new Label { Id = "model-catalog-family", X = 25, Y = y++, Width = Dim.Fill(4), Height = 1,
                Text = ModelCatalogText("family") + ": " + family });
        }
        var context = Field("model-catalog-context", "context", existing?.Context.ToString(CultureInfo.InvariantCulture) ?? "8192");
        var usable = Field("model-catalog-usable-context", "usableContext", existing?.RecommendedUsableContext.ToString(CultureInfo.InvariantCulture) ?? "8192");
        var output = Field("model-catalog-max-output", "maxOutput", existing?.MaxOutput?.ToString(CultureInfo.InvariantCulture) ?? "");
        var parameters = Field("model-catalog-parameters", "parameters", existing?.ParametersBillions?.ToString("R", PreferenceInput.Culture(_locale)) ?? "");
        var inputModalities = Field("model-catalog-input-modalities", "inputModalities",
            string.Join(",", existing?.InputModalities ?? ["text"]));
        frame.Add(new Label { X = 2, Y = y, Width = 22, Height = 1, Text = ModelCatalogText("aliases") });
        var aliases = new TextView { Id = "model-catalog-aliases", X = 25, Y = y, Width = Dim.Fill(4), Height = 2,
            Text = string.Join("\n", existing?.Aliases ?? []) };
        frame.Add(aliases);
        y += 2;
        frame.Add(new Label { Id = "model-catalog-literal-help", X = 2, Y = y, Width = Dim.Fill(4), Height = 2,
            Text = ModelCatalogText("literalHelp") });
        var status = new Label { Id = "model-catalog-editor-status", X = 2, Y = Pos.AnchorEnd(6), Width = Dim.Fill(4), Height = 1,
            Text = "" };
        var save = new Button { Id = "model-catalog-save", X = 2, Y = Pos.AnchorEnd(3), Text = ModelCatalogText("save") };
        var cancel = new Button { Id = "model-catalog-cancel", X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(3), Text = ModelCatalogText("cancel") };
        cancel.Accepted += (_, _) => { CloseOverlay(); RenderModelCatalog(); };
        save.Enabled = snapshot.Available;
        save.Accepted += (_, _) =>
        {
            if (!long.TryParse(context.Text.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var contextValue)
                || !long.TryParse(usable.Text.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var usableValue))
            { status.Text = ModelCatalogText("invalidLimits"); return; }
            long? outputValue = null;
            if (!string.IsNullOrWhiteSpace(output.Text.ToString()))
            {
                if (!long.TryParse(output.Text.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedOutput))
                { status.Text = ModelCatalogText("invalidLimits"); return; }
                outputValue = parsedOutput;
            }
            double? parametersValue = null;
            if (!string.IsNullOrWhiteSpace(parameters.Text.ToString()))
            {
                if (!double.TryParse(parameters.Text.ToString(), NumberStyles.Float, PreferenceInput.Culture(_locale), out var parsed))
                { status.Text = ModelCatalogText("invalidParameters"); return; }
                parametersValue = parsed;
            }
            var aliasesValue = aliases.Text.ToString().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var modalitiesValue = inputModalities.Text.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            try
            {
                var draft = new ModelCatalogDraft(modelId.Text.ToString().Trim(), provider.Text.ToString().Trim(), contextValue,
                    usableValue, outputValue, parametersValue, aliasesValue, existing?.Id, modalitiesValue);
                _modelCatalogSnapshot = ModelCatalogHost().Save(draft, snapshot.Revision, CancellationToken.None);
                _modelCatalogSelectedId = draft.Id;
                _modelCatalogNotice = ModelCatalogText("saved");
                CloseOverlay(); RenderModelCatalog();
            }
            catch (ModelCatalogConfigurationException exception) { status.Text = ModelCatalogError(exception.MessageKey); }
            catch (Exception) { status.Text = ModelCatalogText("saveFailed"); }
        };
        frame.Add(status, save, cancel);
        frame.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; HandleModelCatalogPreferencesEscape(); } };
        ApplyTheme(frame);
        _overlay = frame;
        _window.Add(frame);
        if (existing is null) modelId.SetFocus(); else provider.SetFocus();
    }

    private void ShowModelCatalogDeleteConfirm(ModelCatalogModel model)
    {
        if (_window is null) return;
        _modelCatalogDeleteId = model.Id;
        CloseOverlay();
        var frame = OverlayFrame(ModelCatalogText("confirmTitle"), 12);
        frame.Id = "model-catalog-confirm";
        frame.Add(new Label { Id = "model-catalog-delete-warning", X = 2, Y = 2, Width = Dim.Fill(4), Height = 4,
            Text = ModelCatalogText("deleteWarning") + "\n\n" + model.Id });
        var yes = new Button { Id = "model-catalog-confirm-delete", X = 2, Y = Pos.AnchorEnd(3), Text = ModelCatalogText("confirmDelete") };
        var no = new Button { Id = "model-catalog-cancel-delete", X = Pos.AnchorEnd(14), Y = Pos.AnchorEnd(3), Text = ModelCatalogText("cancel") };
        yes.Accepted += (_, _) =>
        {
            var snapshot = _modelCatalogSnapshot;
            if (snapshot is null || _modelCatalogDeleteId is null) return;
            try
            {
                _modelCatalogSnapshot = ModelCatalogHost().Delete(_modelCatalogDeleteId, snapshot.Revision, CancellationToken.None);
                _modelCatalogSelectedId = null;
                _modelCatalogNotice = ModelCatalogText("saved");
                _modelCatalogDeleteId = null;
                CloseOverlay(); RenderModelCatalog();
            }
            catch (ModelCatalogConfigurationException exception)
            {
                _modelCatalogNotice = ModelCatalogError(exception.MessageKey);
                _modelCatalogDeleteId = null;
                CloseOverlay(); RefreshModelCatalog(); RenderModelCatalog();
            }
            catch (Exception)
            {
                _modelCatalogNotice = ModelCatalogText("saveFailed");
                _modelCatalogDeleteId = null;
                CloseOverlay(); RenderModelCatalog();
            }
        };
        no.Accepted += (_, _) => { _modelCatalogDeleteId = null; CloseOverlay(); RenderModelCatalog(); };
        frame.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) { key.Handled = true; HandleModelCatalogPreferencesEscape(); } };
        frame.Add(yes, no);
        ApplyTheme(frame);
        _overlay = frame;
        _window.Add(frame);
        no.SetFocus();
    }

    private string ModelCatalogError(string key)
    {
        if (key.StartsWith("modelCatalog.reference.", StringComparison.Ordinal))
        {
            var reference = key["modelCatalog.reference.".Length..];
            return ModelCatalogText(reference switch
            {
                "userDefault" => "referenceUserDefault", "projectDefault" => "referenceProjectDefault",
                "workspaceDefault" => "referenceWorkspaceDefault", _ => "referenceRouting"
            });
        }
        var code = key.StartsWith("modelCatalog.error.", StringComparison.Ordinal)
            ? key["modelCatalog.error.".Length..] : key;
        var resource = code switch
        {
            "unavailable" => "unavailable", "configurationInvalid" => "catalogUnavailable",
            "workspaceScopeUnavailable" => "workspaceScopeUnavailable", "revisionConflict" => "stale",
            "unknownProvider" => "unknownProvider", "modelAlreadyExists" => "modelAlreadyExists",
            "modelNotFound" => "modelNotFound", "invalidId" => "invalidId", "invalidProvider" => "invalidProvider",
            "invalidLimits" => "invalidLimits", "invalidParameters" => "invalidParameters", "invalidAliases" => "invalidAliases",
            "ambiguousAlias" => "ambiguousAlias", "referenced" => "referenced", _ => "saveFailed"
        };
        return ModelCatalogText(resource);
    }
}
#pragma warning restore CS0618
