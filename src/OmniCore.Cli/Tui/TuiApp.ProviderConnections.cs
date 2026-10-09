using System.Globalization;
using OmniCore.Host;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private readonly ITuiProviderConnectionHost _providerConnectionHost;
    private CancellationTokenSource? _providerConnectionCancellation;
    private Task? _providerConnectionTask;
    private TextField? _providerApiKeyField;

    internal Task? ProviderConnectionTask => _providerConnectionTask;

    private void ShowProviderConnections(string? activity = null, string? selectedProviderId = null)
    {
        if (_window is null || _overlay is not null) return;

        ProviderConnectionMenuRow[] rows;
        try
        {
            rows = _providerConnectionHost.List(CancellationToken.None).ToArray();
        }
        catch (Exception)
        {
            ShowMessage(Ui("No se pudo leer el estado de las conexiones API.",
                "Could not read provider connection status."));
            return;
        }

        var frame = OverlayFrame(Ui("Conexiones API de providers", "Provider API connections"), 18);
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(2),
            Text = Ui("El estado y las fechas vienen del Host.", "Status and timestamps come from the Host.") });
        var list = new ListView { Id = "provider-connection-list", X = 2, Y = 4, Width = Dim.Fill(4), Height = 7,
            CanFocus = rows.Length > 0, ShowMarks = false };
        list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(rows.Select(ProviderConnectionRowText)));
        if (rows.Length > 0)
        {
            var selectedIndex = selectedProviderId is null ? -1
                : Array.FindIndex(rows, row => row.ProviderId == selectedProviderId);
            list.SelectedItem = selectedIndex >= 0 ? selectedIndex : 0;
        }
        var status = new Label { Id = "provider-connection-status", X = 2, Y = 12,
            Width = Dim.Fill(4), Height = 1, Text = activity ?? (rows.Length == 0
                ? Ui("No hay providers configurados.", "No providers are configured.") : "") };

        var connect = new Button { Id = "provider-connect", X = 2, Y = 14, Width = Dim.Percent(46),
            Text = Ui("Conectar…", "Connect…") };
        var test = new Button { Id = "provider-test", X = Pos.Right(connect) + 1, Y = 14,
            Width = Dim.Percent(46), Text = Ui("Probar", "Test") };
        var discover = new Button { Id = "provider-discover", X = 2, Y = 15, Width = Dim.Percent(46),
            Text = Ui("Descubrir modelos", "Discover models") };
        var disconnect = new Button { Id = "provider-disconnect", X = Pos.Right(discover) + 1, Y = 15,
            Width = Dim.Percent(46), Text = Ui("Desconectar", "Disconnect") };
        var back = new Button { Id = "provider-back", X = Pos.AnchorEnd(12), Y = Pos.AnchorEnd(2),
            Text = Ui("Volver", "Back") };
        back.Accepted += (_, _) => { CloseOverlay(); ShowPreferences(); };

        ProviderConnectionMenuRow? SelectedRow() => list.SelectedItem is { } index
            && index >= 0 && index < rows.Length ? rows[index] : null;

        void RefreshActions()
        {
            var selected = SelectedRow();
            connect.Enabled = selected?.CanConnect == true;
            test.Enabled = selected?.CanTest == true;
            discover.Enabled = selected?.CanDiscoverModels == true;
            disconnect.Enabled = selected?.CanDisconnect == true;
            if (selected is not null && activity is null)
                status.Text = ProviderConnectionDates(selected);
        }

        list.ValueChanged += (_, _) => RefreshActions();
        connect.Accepted += (_, _) =>
        {
            if (SelectedRow() is { CanConnect: true } selected)
            {
                CloseOverlay();
                ShowProviderApiKey(selected.ProviderId);
            }
        };
        test.Accepted += (_, _) =>
        {
            if (SelectedRow() is { CanTest: true } selected)
                StartProviderConnectionOperation(frame, status, selected.ProviderId,
                    async token => await _providerConnectionHost.TestAsync(selected.ProviderId, token)
                        .ConfigureAwait(false));
        };
        discover.Accepted += (_, _) =>
        {
            if (SelectedRow() is { CanDiscoverModels: true } selected)
                StartProviderConnectionOperation(frame, status, selected.ProviderId,
                    async token => await _providerConnectionHost.DiscoverAsync(selected.ProviderId, token)
                        .ConfigureAwait(false));
        };
        disconnect.Accepted += (_, _) =>
        {
            if (SelectedRow() is { CanDisconnect: true } selected)
                StartProviderConnectionOperation(frame, status, selected.ProviderId, async token =>
                {
                    await Task.Run(() => _providerConnectionHost.Disconnect(selected.ProviderId, token), token)
                        .ConfigureAwait(false);
                    return null;
                });
        };

        frame.Add(list, status, connect, test, discover, disconnect, back,
            new Label { Id = "omni-help", X = 2, Y = Pos.AnchorEnd(3),
                Text = Ui("↑↓ elegir · Esc cerrar", "↑↓ select · Esc close") });
        ApplyTheme(frame);
        _overlay = frame;
        _window.Add(frame);
        RefreshActions();
        if (rows.Length > 0) list.SetFocus(); else back.SetFocus();
    }

    private string ProviderConnectionRowText(ProviderConnectionMenuRow row)
    {
        var method = row.Method switch
        {
            ProviderConnectionMethod.ApiKey => Ui("API key", "API key"),
            ProviderConnectionMethod.Subscription => Ui("suscripción", "subscription"),
            _ => Ui("sin método", "no method"),
        };
        return row.ProviderId + " · " + method + " · " + ProviderConnectionStateText(row.State)
            + ProviderConnectionDates(row, includeLead: true);
    }

    private string ProviderConnectionDates(ProviderConnectionMenuRow row, bool includeLead = false)
    {
        var values = new List<string>();
        if (row.ValidatedAt is { } validated)
            values.Add((row.State == ProviderConnectionState.Connected
                ? Ui("verificada ", "validated ") : Ui("último OK ", "last successful check "))
                + FormatProviderTimestamp(validated));
        if (row.DiscoveredAt is { } discovered)
            values.Add(Ui("modelos ", "models ") + FormatProviderTimestamp(discovered));
        return values.Count == 0
            ? includeLead ? "" : Ui("Sin mediciones registradas.", "No measurements recorded.")
            : (includeLead ? " · " : "") + string.Join(" · ", values);
    }

    private static string FormatProviderTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private string ProviderConnectionStateText(ProviderConnectionState state) => state switch
    {
        ProviderConnectionState.Connected => Ui("Conectado", "Connected"),
        ProviderConnectionState.NotConfigured => Ui("Sin configurar", "Not configured"),
        ProviderConnectionState.Expired => Ui("Expirado", "Expired"),
        ProviderConnectionState.Invalid => Ui("Inválido", "Invalid"),
        ProviderConnectionState.Unreachable => Ui("Sin conexión", "Unreachable"),
        _ => Ui("Desconocido", "Unknown"),
    };

    private void ShowProviderApiKey(string providerId)
    {
        if (_window is null || _overlay is not null) return;
        var frame = OverlayFrame(Ui("Conectar provider", "Connect provider"), 12);
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(2),
            Text = Ui("La API key se entrega directamente al Host.", "The API key is sent directly to the Host.") });
        var key = _providerApiKeyField = new TextField { Id = "provider-api-key", X = 2, Y = 4,
            Width = Dim.Fill(4), Secret = true, Text = "" };
        var validate = new CheckBox { Id = "provider-validate-key", X = 2, Y = 6,
            Text = Ui("Verificar ahora con la API del provider", "Validate now with the provider API"),
            Value = CheckState.UnChecked };
        var status = new Label { Id = "provider-key-status", X = 2, Y = 8,
            Width = Dim.Fill(4), Height = 1, Text = "" };
        var save = new Button { Id = "provider-key-save", X = 2, Y = Pos.AnchorEnd(2),
            Text = Ui("Guardar", "Save") };
        save.Accepted += (_, _) =>
        {
            var apiKey = key.Text?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                status.Text = Ui("Escribe una API key.", "Enter an API key.");
                key.SetFocus();
                return;
            }
            var shouldValidate = validate.Value == CheckState.Checked;
            key.Text = "";
            key.Enabled = false;
            validate.Enabled = false;
            save.Enabled = false;
            StartProviderConnectionOperation(frame, status, providerId,
                async token => await _providerConnectionHost.ConnectAsync(providerId, apiKey, shouldValidate, token)
                    .ConfigureAwait(false));
        };
        var cancel = new Button { Id = "provider-key-cancel", X = Pos.AnchorEnd(12), Y = Pos.AnchorEnd(2),
            Text = Ui("Cancelar", "Cancel") };
        cancel.Accepted += (_, _) => { CloseOverlay(); ShowProviderConnections(selectedProviderId: providerId); };
        frame.Add(key, validate, status, save, cancel);
        ApplyTheme(frame);
        _overlay = frame;
        _window.Add(frame);
        key.SetFocus();
    }

    private void StartProviderConnectionOperation(FrameView frame, Label status, string providerId,
        Func<CancellationToken, Task<ProviderConnectionMenuResult?>> operation)
    {
        if (_providerConnectionCancellation is not null) return;
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _providerConnectionCancellation = cancellation;
        status.Text = Ui("Procesando conexión…", "Processing connection…");
        foreach (var button in frame.SubViews.OfType<Button>()) button.Enabled = false;

        _providerConnectionTask = Task.Run(async () =>
        {
            try
            {
                var result = await operation(token).ConfigureAwait(false);
                _uiActions.Enqueue(() =>
                {
                    if (token.IsCancellationRequested || !ReferenceEquals(_overlay, frame)
                        || !ReferenceEquals(_providerConnectionCancellation, cancellation)) return;
                    _providerConnectionCancellation = null;
                    var summary = result is null
                        ? Ui("Conexión eliminada.", "Connection removed.")
                        : ProviderOperationSummary(result);
                    CloseOverlay();
                    ShowProviderConnections(summary, providerId);
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception)
            {
                _uiActions.Enqueue(() =>
                {
                    if (token.IsCancellationRequested || !ReferenceEquals(_overlay, frame)
                        || !ReferenceEquals(_providerConnectionCancellation, cancellation)) return;
                    _providerConnectionCancellation = null;
                    CloseOverlay();
                    ShowProviderConnections(Ui("La operación falló. Revisa la configuración del provider.",
                        "The operation failed. Check the provider configuration."), providerId);
                });
            }
            finally { cancellation.Dispose(); }
        });
    }

    private string ProviderOperationSummary(ProviderConnectionMenuResult result)
    {
        var values = new List<string> { Ui("Estado: ", "Status: ") + ProviderConnectionStateText(result.State) };
        if (result.ValidatedAt is { } validated)
            values.Add(Ui("validada ", "validated ") + FormatProviderTimestamp(validated));
        if (result.DiscoveredAt is { } discovered)
            values.Add(Ui("modelos ", "models ") + FormatProviderTimestamp(discovered));
        if (result.RegisteredModels is { } models)
        {
            values.Add(Ui($"Modelos registrados: {models}", $"Models registered: {models}"));
            values.Add(Ui($"Ventanas de cuota reportadas: {result.QuotaWindows}",
                $"Quota windows reported: {result.QuotaWindows}"));
        }
        if (result.Truncated) values.Add(Ui("El catálogo reportado fue acotado.", "The reported catalog was capped."));
        return string.Join(" · ", values);
    }

    private void CancelProviderConnectionOperation()
    {
        var cancellation = _providerConnectionCancellation;
        _providerConnectionCancellation = null;
        if (cancellation is null) return;
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void ClearProviderApiKey()
    {
        if (_providerApiKeyField is not null) _providerApiKeyField.Text = "";
        _providerApiKeyField = null;
    }
}
