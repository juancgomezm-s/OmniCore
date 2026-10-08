using System.Collections.ObjectModel;
using System.Globalization;
using OmniCore.Domain;
using OmniCore.Host;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

/// <summary>
/// Menú de mantenimiento de conexiones a providers, separado del selector de modelos de F3
/// (ADR-0011 §3, ADR-0030 §3, ADR-0031 §5). El renderer solo presenta: el estado y todas las
/// operaciones viven en el Host (<see cref="ITuiProviderConnectionHost"/>); la entrada de la
/// API key es enmascarada y transitoria, y ningún secreto se guarda en el estado del renderer
/// ni en logs o avisos (ADR-0018).
/// </summary>
public sealed partial class TuiApp
{
    private readonly ITuiProviderConnectionHost? _connectionsOverride;
    private ITuiProviderConnectionHost? _connections;
    private CancellationTokenSource? _providerCancellation;
    private bool _providerBusy;
    private string? _providerResultNotice;

    internal ITuiProviderConnectionHost? ProviderConnections => _connections;

    private ITuiProviderConnectionHost Connections => _connections ??= _connectionsOverride
        ?? new TuiProviderConnectionHost();

    private string Localize(string key) => _localization.Resolve(key);

    private string Resolve(LocalizedText text) => _localization.Resolve(text.Key, text.Args);

    private static string FormatStamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private string Stamp(string key, DateTimeOffset at) => _localization.Resolve(key,
        new Dictionary<string, string> { ["when"] = FormatStamp(at) });

    /// <summary>Glyph del estado medido: nunca el color como única señal (ADR-0031 §5).</summary>
    private static string StateGlyph(ProviderConnectionState state) => state switch
    {
        ProviderConnectionState.Connected => "●",
        ProviderConnectionState.NotConfigured => "○",
        ProviderConnectionState.Expired => "⌛",
        ProviderConnectionState.Invalid => "✗",
        ProviderConnectionState.Unreachable => "◌",
        _ => "◐",
    };

    private string StateLabel(ProviderConnectionState state) =>
        Localize("providers.menu.state." + state.ToString().ToLowerInvariant());

    private string MethodLabel(ProviderConnectionMethod method) =>
        Localize("providers.menu.method." + method.ToString().ToLowerInvariant());

    /// <summary>Abre el menú de conexiones. <paramref name="resultNotice"/> muestra el resultado
    /// de la acción que lo reabrió (estado medido + marca de tiempo), hasta la primera navegación.</summary>
    private void ShowProviderConnections(string? resultNotice = null)
    {
        if (_window is null || _overlay is not null) return;
        _providerResultNotice = resultNotice;
        var frame = OverlayFrame(Localize("providers.menu.title"), 16);
        frame.SubViews.Single(view => view.Id == "omni-menu-title").Id = "omni-provider-title";
        frame.Add(new Label { X = 2, Y = 1, Width = Dim.Fill(2), Text = Localize("providers.menu.subtitle") });
        var list = new ListView { Id = "omni-provider-list", X = 2, Y = 3, Width = Dim.Fill(4), Height = Dim.Fill(6),
            ShowMarks = false, CanFocus = false };
        var feedback = new Label { X = 2, Y = Pos.AnchorEnd(5), Width = Dim.Fill(2), Height = 2, Text = "" };
        var connect = new Button { Id = "provider-connect", X = 2, Y = Pos.AnchorEnd(2), Text = Localize("providers.menu.connect") };
        var test = new Button { Id = "provider-test", X = 18, Y = Pos.AnchorEnd(2), Text = Localize("providers.menu.test") };
        var discover = new Button { Id = "provider-discover", X = 33, Y = Pos.AnchorEnd(2), Text = Localize("providers.menu.discover") };
        var disconnect = new Button { Id = "provider-disconnect", X = 2, Y = Pos.AnchorEnd(3), Text = Localize("providers.menu.disconnect") };
        var back = new Button { Id = "provider-back", X = 23, Y = Pos.AnchorEnd(3), Text = Localize("providers.menu.back") };
        var help = new Label { Id = "omni-help", X = 2, Y = Pos.AnchorEnd(1), Width = Dim.Fill(2),
            Text = Localize("providers.menu.help") };
        var actions = new[] { connect, test, discover, disconnect };
        var rows = Array.Empty<ProviderConnectionMenuRow>();

        ProviderConnectionMenuRow? SelectedRow() =>
            list.SelectedItem is int index && index < rows.Length ? rows[index] : null;

        void Reload()
        {
            try { rows = Connections.List(CancellationToken.None).ToArray(); }
            catch (Exception)
            {
                rows = [];
                _providerResultNotice = Localize("providers.menu.listFailed");
            }
            list.SetSource(new ObservableCollection<string>(rows.Select(DescribeProviderRow)));
            list.CanFocus = rows.Length > 0;
            if (rows.Length > 0 && (list.SelectedItem is not int index || index >= rows.Length)) list.SelectedItem = 0;
            UpdateFooter();
            UpdateFeedback();
        }

        string DescribeProviderRow(ProviderConnectionMenuRow row) =>
            StateGlyph(row.State) + " " + row.ProviderId + " · " + MethodLabel(row.Method) + " · " + StateLabel(row.State)
            + (string.IsNullOrEmpty(row.Detail) ? "" : " · " + row.Detail);

        void UpdateFooter()
        {
            var selected = SelectedRow();
            connect.Enabled = !_providerBusy && selected?.CanConnect == true;
            test.Enabled = !_providerBusy && selected?.CanTest == true;
            discover.Enabled = !_providerBusy && selected?.CanDiscoverModels == true;
            disconnect.Enabled = !_providerBusy && selected?.CanDisconnect == true;
            back.Enabled = !_providerBusy;
        }

        void UpdateFeedback()
        {
            if (_providerBusy) return;
            if (_providerResultNotice is { } notice) { feedback.Text = notice; return; }
            var row = SelectedRow();
            if (row is null)
            {
                feedback.Text = rows.Length == 0 ? Localize("providers.menu.empty") : "";
                return;
            }
            var text = row.Notice is { } rowNotice ? Resolve(rowNotice) : StateLabel(row.State);
            var stamps = new List<string>();
            if (row.State == ProviderConnectionState.Connected && row.ValidatedAt is { } validated)
                stamps.Add(Stamp("providers.menu.verifiedAt", validated));
            if (row.DiscoveredAt is { } discovered) stamps.Add(Stamp("providers.menu.discoveredAt", discovered));
            feedback.Text = stamps.Count == 0 ? text : text + "\n" + string.Join(" · ", stamps);
        }

        void MoveList(int delta)
        {
            if (rows.Length == 0) return;
            _providerResultNotice = null;
            list.SelectedItem = ((list.SelectedItem ?? 0) + delta + rows.Length) % rows.Length;
            list.EnsureSelectedItemVisible();
            UpdateFooter();
            UpdateFeedback();
        }

        void MoveFocus(int delta)
        {
            var focusables = new List<View>();
            if (rows.Length > 0) focusables.Add(list);
            focusables.AddRange(actions);
            focusables.Add(back);
            var start = focusables.FindIndex(view => view.HasFocus);
            var index = start < 0 ? 0 : start;
            for (var step = 0; step < focusables.Count; step++)
            {
                index = (index + delta + focusables.Count) % focusables.Count;
                if (focusables[index] is not { } candidate
                    || (candidate is Button button && !button.Enabled)) continue;
                candidate.SetFocus();
                return;
            }
        }

        void RunTest() => RunProviderOperation(
            token => Connections.TestAsync(SelectedRow()!.ProviderId, token),
            Localize("providers.menu.busy.test"),
            result => { CloseOverlay(); ShowProviderConnections(FormatProviderResult(result)); },
            feedback, frame, [.. actions, back]);

        void RunDiscover() => RunProviderOperation(
            token => Connections.DiscoverAsync(SelectedRow()!.ProviderId, token),
            Localize("providers.menu.busy.discover"),
            result => { CloseOverlay(); ShowProviderConnections(FormatProviderResult(result)); },
            feedback, frame, [.. actions, back]);

        void HandleProviderShortcut(Key key)
        {
            if (key.KeyCode == (KeyCode.C | KeyCode.AltMask) && connect.Enabled)
            { ShowProviderConnectForm(SelectedRow()!.ProviderId); key.Handled = true; }
            else if (key.KeyCode == (KeyCode.T | KeyCode.AltMask) && test.Enabled)
            { RunTest(); key.Handled = true; }
            else if (key.KeyCode == (KeyCode.D | KeyCode.AltMask) && discover.Enabled)
            { RunDiscover(); key.Handled = true; }
            else if (key.KeyCode == (KeyCode.X | KeyCode.AltMask) && disconnect.Enabled)
            { ConfirmProviderDisconnect(SelectedRow()!); key.Handled = true; }
        }

        list.RowRender += (_, rowArgs) =>
        {
            if (rowArgs.Row == list.SelectedItem) rowArgs.RowAttribute = list.GetScheme().Focus;
        };
        list.KeyDown += (_, key) =>
        {
            switch (key.KeyCode)
            {
                case KeyCode.CursorDown: MoveList(1); key.Handled = true; break;
                case KeyCode.CursorUp: MoveList(-1); key.Handled = true; break;
                case KeyCode.CursorRight: MoveFocus(1); key.Handled = true; break;
                case KeyCode.CursorLeft: MoveFocus(-1); key.Handled = true; break;
                case KeyCode.Enter:
                    key.Handled = true;
                    var row = SelectedRow();
                    if (row?.CanConnect == true) ShowProviderConnectForm(row.ProviderId);
                    break;
                default: HandleProviderShortcut(key); break;
            }
        };
        list.MouseEvent += (_, mouse) =>
        {
            var delta = mouse.Flags.HasFlag(MouseFlags.WheeledDown) ? 1 : mouse.Flags.HasFlag(MouseFlags.WheeledUp) ? -1 : 0;
            if (delta == 0) return;
            MoveList(delta);
            mouse.Handled = true;
        };
        foreach (var button in actions.Append(back))
        {
            button.KeyDown += (_, key) =>
            {
                switch (key.KeyCode)
                {
                    case KeyCode.CursorDown: MoveFocus(1); key.Handled = true; break;
                    case KeyCode.CursorUp: MoveFocus(-1); key.Handled = true; break;
                    case KeyCode.CursorRight: MoveFocus(1); key.Handled = true; break;
                    case KeyCode.CursorLeft: MoveFocus(-1); key.Handled = true; break;
                    default: HandleProviderShortcut(key); break;
                }
            };
        }
        connect.Accepted += (_, _) => { if (SelectedRow() is { } row) ShowProviderConnectForm(row.ProviderId); };
        test.Accepted += (_, _) => { if (SelectedRow()?.CanTest == true) RunTest(); };
        discover.Accepted += (_, _) => { if (SelectedRow()?.CanDiscoverModels == true) RunDiscover(); };
        disconnect.Accepted += (_, _) => { if (SelectedRow()?.CanDisconnect == true) ConfirmProviderDisconnect(SelectedRow()!); };
        back.Accepted += (_, _) => { CloseOverlay(); ShowPreferences(); };
        frame.KeyDown += (_, key) => { if (!key.Handled) HandleProviderShortcut(key); };

        Reload();
        ApplyTheme(frame);
        _overlay = frame;
        _window.Add(frame);
        if (rows.Length > 0) list.SetFocus(); else back.SetFocus();
    }

    private string FormatProviderResult(ProviderConnectionMenuResult result)
    {
        var parts = new List<string> { StateLabel(result.State) };
        if (!string.IsNullOrEmpty(result.Detail) && result.RegisteredModels is null) parts.Add(result.Detail!);
        if (result.RegisteredModels is { } registered)
            parts.Add(registered.ToString(CultureInfo.InvariantCulture) + " " + Ui("modelos registrados", "models registered")
                + (result.Truncated ? Ui(" · lista truncada", " · truncated") : ""));
        if (result.QuotaWindows > 0)
            parts.Add(result.QuotaWindows.ToString(CultureInfo.InvariantCulture) + " " + Ui("ventanas de cuota", "rate-limit windows"));
        if (result.Notice is { } notice) parts.Add(Resolve(notice));
        var stamps = new List<string>();
        if (result.State == ProviderConnectionState.Connected && result.ValidatedAt is { } validated)
            stamps.Add(Stamp("providers.menu.verifiedAt", validated));
        if (result.DiscoveredAt is { } discovered) stamps.Add(Stamp("providers.menu.discoveredAt", discovered));
        var text = string.Join(" · ", parts);
        return stamps.Count == 0 ? text : text + "\n" + string.Join(" · ", stamps);
    }

    /// <summary>
    /// Formulario de conexión de la API key: entrada enmascarada (TextField.Secret) y
    /// transitoria; el borrador nunca pasa al composer ni a ningún estado persistente del
    /// renderer, y se destruye al cerrar el panel. El guardado (con o sin verificación) lo
    /// hace el backend cifrado del Host.
    /// </summary>
    private void ShowProviderConnectForm(string providerId)
    {
        if (_window is null) return;
        CloseOverlay();
        var frame = OverlayFrame(Localize("providers.menu.form.title"), 10);
        frame.SubViews.Single(view => view.Id == "omni-menu-title").Id = "omni-provider-title";
        frame.Add(new Label { X = 2, Y = 1, Width = Dim.Fill(2), Height = 2, Text = Localize("providers.menu.form.prompt") });
        frame.Add(new Label { X = 2, Y = 3, Width = Dim.Fill(2), Text = Localize("providers.menu.form.keyLabel") });
        var keyField = new TextField { Id = "provider-api-key", X = 2, Y = 4, Width = Dim.Fill(2), Secret = true, Text = "" };
        var verify = new CheckBox { X = 2, Y = 5, Text = Localize("providers.menu.form.validate"), Value = CheckState.Checked };
        var feedback = new Label { X = 2, Y = 6, Width = Dim.Fill(2), Height = 2, Text = "" };
        var save = new Button { Id = "provider-save", X = 2, Y = Pos.AnchorEnd(2), Text = Localize("providers.menu.form.save") };
        var cancel = new Button { Id = "provider-form-cancel", X = Pos.AnchorEnd(16), Y = Pos.AnchorEnd(2), Text = Localize("providers.menu.form.cancel") };
        var help = new Label { Id = "omni-help", X = 2, Y = Pos.AnchorEnd(1), Width = Dim.Fill(2), Text = Localize("providers.menu.form.help") };

        void LeaveForm(Key key)
        {
            key.Handled = true;
            var cancellation = _providerCancellation;
            if (cancellation is not null) { cancellation.Cancel(); return; }
            CloseOverlay();
            ShowProviderConnections();
        }
        keyField.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) LeaveForm(key); };
        verify.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc) LeaveForm(key); };
        frame.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc && !key.Handled) LeaveForm(key); };

        save.Accepted += (_, _) =>
        {
            var apiKey = keyField.Text?.ToString() ?? "";
            RunProviderOperation(token => Connections.ConnectAsync(apiKey, verify.Value == CheckState.Checked, token),
                Localize("providers.menu.busy.connect"),
                result => { CloseOverlay(); ShowProviderConnections(FormatProviderResult(result)); },
                feedback, frame, keyField, verify, save, cancel);
        };
        cancel.Accepted += (_, _) => LeaveForm(new Key(KeyCode.Esc));
        ApplyTheme(frame);
        _overlay = frame;
        _window.Add(frame);
        keyField.SetFocus();
    }

    private void ConfirmProviderDisconnect(ProviderConnectionMenuRow row)
    {
        CloseOverlay();
        var frame = OverlayFrame(Localize("providers.menu.disconnect.title",
            new Dictionary<string, string> { ["provider"] = row.ProviderId }), 9);
        frame.Add(new Label { X = 2, Y = 1, Width = Dim.Fill(2), Height = 2, Text = Localize("providers.menu.disconnect.confirm") });
        var cancel = new Button { Id = "provider-cancel-disconnect", X = 2, Y = Pos.AnchorEnd(2), Text = Ui("Cancelar", "Cancel") };
        var confirm = new Button { Id = "provider-confirm-disconnect", X = Pos.AnchorEnd(18), Y = Pos.AnchorEnd(2), Text = Localize("providers.menu.disconnect.ok") };
        confirm.Accepted += (_, _) => RunProviderOperation(
            token =>
            {
                Connections.Disconnect(row.ProviderId, token);
                return System.Threading.Tasks.Task.FromResult(new ProviderConnectionMenuResult(
                    ProviderConnectionState.NotConfigured, null, null));
            },
            Localize("providers.menu.busy.disconnect"),
            result => { CloseOverlay(); ShowProviderConnections(FormatProviderResult(result)); },
            new Label(), frame, confirm, cancel);
        cancel.Accepted += (_, _) => { CloseOverlay(); ShowProviderConnections(); };
        frame.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Esc && !key.Handled) { CloseOverlay(); ShowProviderConnections(); key.Handled = true; } };
        frame.Add(cancel, confirm);
        ApplyTheme(frame);
        _overlay = frame;
        _window!.Add(frame);
        cancel.SetFocus();
    }

    private string Localize(string key, IReadOnlyDictionary<string, string> args) =>
        _localization.Resolve(key, args);

    /// <summary>
    /// Ejecuta una acción del menú con estado de ocupación, cancelación y errores tipados:
    /// deshabilita los controles mientras corre, «Esc» cancela, y solo afirma el resultado que
    /// el backend midió (nunca éxito fabricado). El fallo inesperado se muestra como mensaje
    /// genérico localizado, jamás con el texto crudo de la excepción.
    /// </summary>
    private void RunProviderOperation(Func<CancellationToken, System.Threading.Tasks.Task<ProviderConnectionMenuResult>> operation,
        string busyText, Action<ProviderConnectionMenuResult> completed, Label feedback, FrameView frame,
        params View[] controls)
    {
        if (_providerCancellation is not null) return; // ya hay una operación activa; Esc cancela.
        _providerBusy = true;
        var cancellation = _providerCancellation = new CancellationTokenSource();
        foreach (var control in controls) control.Enabled = false;
        feedback.Text = busyText + " · " + Localize("providers.menu.cancelHint");
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            ProviderConnectionMenuResult? result = null;
            ProviderConnectionException? typed = null;
            bool cancelled;
            try { result = await operation(cancellation.Token).ConfigureAwait(false); }
            catch (ProviderConnectionException ex) { typed = ex; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception) { }
            finally { cancelled = cancellation.IsCancellationRequested; }
            _uiActions.Enqueue(() =>
            {
                _providerCancellation = null;
                _providerBusy = false;
                cancellation.Dispose();
                foreach (var control in controls) control.Enabled = true;
                if (!ReferenceEquals(_overlay, frame)) return; // panel ya reemplazado o cerrado
                if (cancelled) { feedback.Text = Localize("providers.menu.cancelled"); return; }
                if (result is not null) { completed(result); return; }
                feedback.Text = typed is not null ? Resolve(typed.UserMessage) : Localize("providers.menu.failed");
            });
        });
    }
}
