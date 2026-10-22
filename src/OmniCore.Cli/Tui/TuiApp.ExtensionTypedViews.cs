using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using OmniCore.Client;
using OmniCore.Protocol;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private void AttachExtensionViewRefresh(FrameView frame, ExtensionViewRequest request, ExtensionView initial,
        Action<ExtensionView> apply, Action<string> notice, Func<bool>? editing = null)
    {
        var projection = new ExtensionViewProjection(); projection.Apply(initial);
        var current = initial;
        var cancellation = _extensionViewCancellation = new CancellationTokenSource();
        var token = cancellation.Token; var session = _cursorSession;
        var nextPoll = DateTimeOffset.UtcNow.AddSeconds(2); var pending = false; var needsSnapshot = false;
        _refreshExtensionView = force =>
        {
            if (pending || token.IsCancellationRequested || _overlay != frame || !force && DateTimeOffset.UtcNow < nextPoll) return;
            if (editing?.Invoke() == true) { if (force) notice(ExtensionText("form.draft")); return; }
            if (session != _cursorSession) { ShowExtensionViews(); ShowExtensionNotice(ExtensionText("session.changed")); return; }
            pending = true;
            var query = request with { AfterRevision = needsSnapshot ? null : current.Revision };
            _ = Task.Run(() =>
            {
                ExtensionView? update = null; string? failure = null;
                try { update = ExtensionProtocol.Decode(_client.Query("extensions.view:" + Uri.EscapeDataString(ExtensionProtocol.EncodeRequest(query)), token)?.Json ?? "{}"); }
                catch (OperationCanceledException) { }
                catch (Exception error) when (error is IOException or ArgumentException or JsonException or InvalidOperationException or UnauthorizedAccessException)
                { failure = error.GetType().Name; }
                _uiActions.Enqueue(() =>
                {
                    pending = false; nextPoll = DateTimeOffset.UtcNow.AddSeconds(2);
                    if (token.IsCancellationRequested || _overlay != frame || editing?.Invoke() == true) return;
                    if (session != _cursorSession) { ShowExtensionViews(); ShowExtensionNotice(ExtensionText("session.changed")); return; }
                    if (failure is not null) { notice(ExtensionText("platform.refresh_failed") + " · " + failure); return; }
                    if (update is null) return;
                    if (!projection.Apply(update)) { needsSnapshot = true; nextPoll = DateTimeOffset.MinValue; return; }
                    needsSnapshot = false;
                    var next = projection.Get(update.Id)!;
                    if (next.Revision == current.Revision && next.Scope == current.Scope) return;
                    current = next; apply(next);
                });
            }, CancellationToken.None);
        };
    }

    private void ShowExtensionTypedView(ExtensionViewRequest request, ExtensionView initial)
    {
        if (_window is null) return;
        CloseOverlay();
        var frame = OverlayFrame(request.ExtensionId + " / " + request.ViewId, _window.Frame.Height - 3);
        frame.Id = "extension-typed-preferences"; frame.Width = Dim.Percent(94); frame.Height = Dim.Fill(2);
        var panel = new View { Id = "extension-fields", X = 2, Y = 2, Width = Dim.Fill(4), Height = Dim.Fill(8), CanFocus = true,
            ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar };
        var status = new Label { Id = "extension-form-status", X = 2, Y = Pos.AnchorEnd(5), Width = Dim.Fill(4), Height = 2 };
        var hint = new Label { Id = "extension-form-hint", X = 2, Y = Pos.AnchorEnd(7), Width = Dim.Fill(4), Height = 2 };
        var back = new Button { Id = "extension-typed-back", X = 2, Y = Pos.AnchorEnd(2), Text = ExtensionText("platform.back") };
        back.Accepted += (_, _) => ShowExtensionViews();
        var refresh = new Button { Id = "extension-view-refresh", X = Pos.Right(back) + 2, Y = Pos.AnchorEnd(2), Text = ExtensionText("platform.refresh") };
        refresh.Accepted += (_, _) => _refreshExtensionView?.Invoke(true);
        frame.Add(panel, status, hint, back, refresh);
        var current = initial; var dirty = false;
        Button? discard = null;
        void MarkDirty()
        {
            dirty = true; status.Text = ExtensionText("form.draft");
            if (discard is not null) discard.Visible = true;
        }
        var values = new Dictionary<string, Func<JsonElement>>(StringComparer.Ordinal);
        var actionButtons = new List<Button>();
        string Literal(string value) => SessionPreferencesPresentation.SafeText(value);
        string Label(string value) => value.StartsWith("extension.", StringComparison.Ordinal) ? _localization.Resolve(value) : Literal(value);
        void Render(ExtensionView view)
        {
            // Rebuild only an untouched form. Automatic refresh never replaces an edited field.
            var focus = panel.SubViews.SelectMany(child => child.SubViews).FirstOrDefault(child => child.HasFocus)?.Id;
            foreach (var child in panel.SubViews.ToArray()) { panel.Remove(child); _retiredOverlays.Add(child); }
            foreach (var button in actionButtons) { frame.Remove(button); _retiredOverlays.Add(button); }
            actionButtons.Clear(); values.Clear(); current = view; var y = 0;
            var body = new View { Id = "extension-fields-body", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
                ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar };
            panel.Add(body);
            status.Text = view.Diagnostic is null ? ExtensionState(view.State) : _localization.Resolve(view.Diagnostic);
            if (view.Kind == "indicator")
            {
                var indicators = new SidebarView { Id = "extension-indicators", X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), Theme = _theme };
                var widget = ExtensionViewProjection.IndicatorWidget(view, _localization);
                indicators.Render([(widget, widget.Build(_state, WidgetSize.Expanded))]); body.Add(indicators);
                hint.Text = ExtensionText("indicator.legend");
            }
            else
            {
                foreach (var field in view.Fields ?? [])
                {
                    var original = field.Value;
                    body.Add(new Label { X = 0, Y = y++, Width = Dim.Fill(2), Text = Label(field.Label)
                        + (field.Required ? " *" : "") + (field.ReadOnly ? " · " + ExtensionText("form.read_only") : "") });
                    if (field.ReadOnly)
                    {
                        body.Add(new Label { Id = "extension-field-" + field.Id, X = 0, Y = y++, Width = Dim.Fill(2), Text = Literal(original?.ToString() ?? "—") });
                        values[field.Id] = () => original ?? JsonSerializer.SerializeToElement((string?)null, ExtensionFormUiJson.Default.String);
                    }
                    else if (field.Kind == "boolean")
                    {
                        var check = new CheckBox { Id = "extension-field-" + field.Id, X = 0, Y = y++, Width = Dim.Fill(2), Text = ExtensionText("form.enabled"),
                            Value = original?.ValueKind == JsonValueKind.True ? CheckState.Checked : CheckState.UnChecked };
                        check.ValueChanged += (_, _) => MarkDirty(); body.Add(check);
                        values[field.Id] = () => JsonSerializer.SerializeToElement(check.Value == CheckState.Checked, ExtensionFormUiJson.Default.Boolean);
                    }
                    else if (field.Kind == "choice")
                    {
                        var options = field.Options!;
                        var choices = new ListView { Id = "extension-field-" + field.Id, X = 0, Y = y, Width = Dim.Fill(2), Height = Math.Min(4, options.Count), CanFocus = true };
                        choices.SetSource(new ObservableCollection<string>(options.Select(Literal)));
                        choices.SelectedItem = Math.Max(0, options.ToList().FindIndex(option => original?.GetString() == option));
                        choices.ValueChanged += (_, _) => MarkDirty();
                        choices.Accepted += (_, args) => args.Handled = true;
                        body.Add(choices); y += Math.Min(4, options.Count);
                        values[field.Id] = () => JsonSerializer.SerializeToElement(choices.SelectedItem is int index && index >= 0 && index < options.Count ? options[index] : null, ExtensionFormUiJson.Default.String);
                    }
                    else
                    {
                        var input = new TextField { Id = "extension-field-" + field.Id, X = 0, Y = y++, Width = Dim.Fill(2), Text = field.Kind == "number"
                            && original is { ValueKind: JsonValueKind.Number } numeric && numeric.TryGetDecimal(out var initialNumber)
                                ? initialNumber.ToString(PreferenceInput.Culture(_locale)) : original?.ToString() ?? "" };
                        input.TextChanged += (_, _) => MarkDirty(); body.Add(input);
                        values[field.Id] = () =>
                        {
                            var text = input.Text?.ToString() ?? "";
                            if (field.Kind == "number")
                            {
                                if (string.IsNullOrWhiteSpace(text) && !field.Required) return JsonSerializer.SerializeToElement((string?)null, ExtensionFormUiJson.Default.String);
                                if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, PreferenceInput.Culture(_locale), out var number))
                                    throw new ArgumentException("extension.form.invalid");
                                return JsonSerializer.SerializeToElement(number, ExtensionFormUiJson.Default.Decimal);
                            }
                            return JsonSerializer.SerializeToElement(text, ExtensionFormUiJson.Default.String);
                        };
                    }
                    y++;
                }
                hint.Text = ExtensionText("form.help");
                var actions = view.Actions.Where(action => action.ItemId is null && action.ConfigurationKey is not null).ToArray();
                // Keep actions reachable even in a narrow terminal; arbitrary callbacks are not executable here.
                foreach (var action in actions.Length == 1 ? actions : [])
                {
                    hint.Text = ExtensionText("form.help") + " · " + Literal(action.ConfigurationKey!) + " / " + ExtensionText("scope." + action.ConfigurationScope);
                    var save = new Button { Id = "extension-form-save", X = Pos.Right(refresh) + 2, Y = Pos.AnchorEnd(2), Text = Label(action.Label),
                        Enabled = action.Enabled && view.State == "ready" && view.Scope is not null && _client is ITrustedUserActionClient };
                    save.Accepted += (_, _) =>
                    {
                        try
                        {
                            var payload = JsonSerializer.SerializeToElement(values.ToDictionary(pair => pair.Key, pair => pair.Value(), StringComparer.Ordinal), ExtensionFormUiJson.Default.DictionaryStringJsonElement);
                            ExtensionProtocol.ValidateFormValues(current, payload);
                            var submission = new ExtensionFormSubmission(request.ExtensionId, request.ViewId, action.Id, current.Revision, current.Scope!, payload, request.Path, request.Search);
                            var json = JsonSerializer.Serialize(submission, ExtensionJson.Default.ExtensionFormSubmission);
                            var ack = ((ITrustedUserActionClient)_client).SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"extensions.package\",\"action\":\"submit-view\","
                                + JsonObj.Field("extensionId", request.ExtensionId) + "," + JsonObj.Field("submission", json) + "}"), CancellationToken.None);
                            if (ack.Status != "ok") { status.Text = ExtensionText("form.rejected"); return; }
                            dirty = false; ShowExtensionView(request); ShowExtensionNotice(ExtensionText("form.saved"));
                        }
                        catch (Exception error) when (error is JsonException or ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException)
                        { status.Text = ExtensionText("form.invalid"); }
                    };
                    actionButtons.Add(save); frame.Add(save);
                    discard = new Button { Id = "extension-form-discard", X = Pos.Right(save) + 2, Y = Pos.AnchorEnd(2),
                        Text = ExtensionText("form.discard"), Visible = dirty };
                    discard.Accepted += (_, _) => ShowExtensionView(request);
                    actionButtons.Add(discard); frame.Add(discard);
                    if (!action.Enabled) status.Text = _localization.Resolve(action.DisabledReason ?? "extension.form.permission");
                }
                if (actions.Length != 1) status.Text = ExtensionText("form.action_unavailable");
                ConfigurePreferenceScroll(body, y + 1);
            }
            ApplyTheme(frame);
            if (focus is not null) body.SubViews.FirstOrDefault(child => child.Id == focus)?.SetFocus();
        }
        _overlay = frame; _window.Add(frame); Render(initial);
        AttachExtensionViewRefresh(frame, request, initial, Render, text => status.Text = text, () => dirty);
        panel.SubViews.FirstOrDefault()?.SubViews.FirstOrDefault(child => child.CanFocus)?.SetFocus();
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
[System.Text.Json.Serialization.JsonSerializable(typeof(bool))]
[System.Text.Json.Serialization.JsonSerializable(typeof(decimal))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, JsonElement>))]
internal partial class ExtensionFormUiJson : System.Text.Json.Serialization.JsonSerializerContext;
