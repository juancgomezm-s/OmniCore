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
    private bool _returnToProviderConnections;
    private string? _returnProviderId;

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
            ShowMessage(AccountUi("error.readApi"));
            return;
        }

        var frameHeight = Math.Clamp(_window.Frame.Height - 4, 18, 30);
        var frame = OverlayFrame(AccountUi("title.api"), frameHeight);
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(2), Height = 2,
            Text = AccountUi("help.providerState") });
        var actionY = frameHeight - 6;
        var statusY = actionY - 2;
        var list = new ListView { Id = "provider-connection-list", X = 2, Y = 4, Width = Dim.Fill(4), Height = frameHeight - 13,
            CanFocus = rows.Length > 0, ShowMarks = false };
        list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(rows.Select(ProviderConnectionRowText)));
        if (rows.Length > 0)
        {
            var selectedIndex = selectedProviderId is null ? -1
                : Array.FindIndex(rows, row => row.ProviderId == selectedProviderId);
            list.SelectedItem = selectedIndex >= 0 ? selectedIndex : 0;
        }
        var status = new Label { Id = "provider-connection-status", X = 2, Y = statusY,
            Width = Dim.Fill(4), Height = 1, Text = activity ?? (rows.Length == 0
                ? AccountUi("status.noProviders") : "") };

        var connect = new Button { Id = "provider-connect", X = 2, Y = actionY, Width = Dim.Percent(46),
            Text = AccountUi("action.connect") };
        var test = new Button { Id = "provider-test", X = Pos.Right(connect) + 1, Y = actionY,
            Width = Dim.Percent(46), Text = AccountUi("action.test") };
        var discover = new Button { Id = "provider-discover", X = 2, Y = actionY + 1, Width = Dim.Percent(46),
            Text = AccountUi("action.discover") };
        var disconnect = new Button { Id = "provider-disconnect", X = Pos.Right(discover) + 1, Y = actionY + 1,
            Width = Dim.Percent(46), Text = AccountUi("action.disconnect") };
        var back = new Button { Id = "provider-back", X = Pos.AnchorEnd(12), Y = Pos.AnchorEnd(2),
            Text = AccountUi("action.back") };
        back.Accepted += (_, _) =>
        {
            _returnToProviderConnections = false; _returnProviderId = null;
            CloseOverlay(); ShowPreferences("accounts_connections", categoryPage: true);
        };

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
                status.Text = selected.NoticeWire is { } notice
                    ? _localization.ResolveWire(notice)
                    : selected.CanConnect || selected.CanTest || selected.CanDiscoverModels || selected.CanDisconnect
                        ? ProviderConnectionDates(selected) : AccountUi("help.noProviderAction");
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
                ShowProviderDisconnectConfirmation(selected);
        };

        frame.Add(list, status, connect, test, discover, disconnect, back);
        ApplyTheme(frame);
        _overlay = frame;
        _window.Add(frame);
        _preferencesReturnToCategory = true;
        RefreshActions();
        if (rows.Length > 0) list.SetFocus(); else back.SetFocus();
    }

    private void ShowProviderDisconnectConfirmation(ProviderConnectionMenuRow row)
    {
        if (_window is null || _overlay is null || !row.CanDisconnect) return;
        CloseOverlay();
        var frame = OverlayFrame(AccountUi("action.disconnect") + " · " + row.ProviderId, 12);
        var question = new Label { Id = "provider-disconnect-question", X = 2, Y = 2,
            Width = Dim.Fill(4), Height = 3, Text = AccountUi("api.disconnectConfirm") };
        var status = new Label { Id = "provider-disconnect-status", X = 2, Y = 6,
            Width = Dim.Fill(4), Height = 2, Text = "" };
        var cancel = new Button { Id = "provider-disconnect-cancel", X = 2, Y = Pos.AnchorEnd(2), Text = AccountUi("action.cancel") };
        cancel.Accepted += (_, _) =>
        {
            _returnToProviderConnections = false;
            CloseOverlay(); ShowProviderConnections(selectedProviderId: row.ProviderId);
        };
        var confirm = new Button { Id = "provider-disconnect-confirm", X = Pos.AnchorEnd(19),
            Y = Pos.AnchorEnd(2), Text = AccountUi("action.confirmDisconnect") };
        confirm.Accepted += (_, _) =>
        {
            _returnToProviderConnections = false;
            StartProviderConnectionOperation(frame, status, row.ProviderId, async token =>
            {
                await Task.Run(() => _providerConnectionHost.Disconnect(row.ProviderId, token), token)
                    .ConfigureAwait(false);
                return null;
            });
        };
        frame.Add(question, status, cancel, confirm);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame);
        _preferencesReturnToCategory = true;
        _returnToProviderConnections = true; _returnProviderId = row.ProviderId;
        confirm.SetFocus();
    }

    private string ProviderConnectionRowText(ProviderConnectionMenuRow row)
    {
        var method = row.Method switch
        {
            ProviderConnectionMethod.ApiKey => AccountUi("method.apiKey"),
            ProviderConnectionMethod.Subscription => AccountUi("method.subscription"),
            _ => AccountUi("method.none"),
        };
        return row.ProviderId + " · " + method + " · " + ProviderConnectionStateText(row.State)
            + ProviderConnectionDates(row, includeLead: true);
    }

    private string ProviderConnectionDates(ProviderConnectionMenuRow row, bool includeLead = false)
    {
        var values = new List<string>();
        if (row.ValidatedAt is { } validated)
            values.Add((row.State == ProviderConnectionState.Connected
                ? AccountUi("date.validated") : AccountUi("date.lastOk"))
                + FormatProviderTimestamp(validated));
        if (row.DiscoveredAt is { } discovered)
            values.Add(AccountUi("date.models") + FormatProviderTimestamp(discovered));
        return values.Count == 0
            ? includeLead ? "" : AccountUi("date.none")
            : (includeLead ? " · " : "") + string.Join(" · ", values);
    }

    private static string FormatProviderTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private string ProviderConnectionStateText(ProviderConnectionState state) => state switch
    {
        ProviderConnectionState.Connected => AccountUi("state.connected"),
        ProviderConnectionState.NotConfigured => AccountUi("state.notConfigured"),
        ProviderConnectionState.Expired => AccountUi("state.expired"),
        ProviderConnectionState.Invalid => AccountUi("state.invalid"),
        ProviderConnectionState.Unreachable => AccountUi("state.unreachable"),
        _ => AccountUi("state.unknown"),
    };

    private void ShowProviderApiKey(string providerId)
    {
        if (_window is null || _overlay is not null) return;
        var frame = OverlayFrame(AccountUi("title.connect"), 12);
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(2),
            Text = AccountUi("help.apiKey") });
        var key = _providerApiKeyField = new TextField { Id = "provider-api-key", X = 2, Y = 4,
            Width = Dim.Fill(4), Secret = true, Text = "" };
        var validate = new CheckBox { Id = "provider-validate-key", X = 2, Y = 6,
            Text = AccountUi("action.validate"),
            Value = CheckState.UnChecked };
        var status = new Label { Id = "provider-key-status", X = 2, Y = 8,
            Width = Dim.Fill(4), Height = 1, Text = "" };
        var save = new Button { Id = "provider-key-save", X = 2, Y = Pos.AnchorEnd(2),
            Text = AccountUi("action.save") };
        save.Accepted += (_, _) =>
        {
            var apiKey = key.Text?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                status.Text = AccountUi("result.enterKey");
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
            Text = AccountUi("action.cancel") };
        cancel.Accepted += (_, _) => { CloseOverlay(); ShowProviderConnections(selectedProviderId: providerId); };
        frame.Add(key, validate, status, save, cancel);
        ApplyTheme(frame);
        _overlay = frame;
        _window.Add(frame);
        _preferencesReturnToCategory = true;
        key.SetFocus();
    }

    private void StartProviderConnectionOperation(FrameView frame, Label status, string providerId,
        Func<CancellationToken, Task<ProviderConnectionMenuResult?>> operation)
    {
        if (_providerConnectionCancellation is not null) return;
        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _providerConnectionCancellation = cancellation;
        status.Text = AccountUi("result.connecting");
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
                        ? AccountUi("result.removed")
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
                    ShowProviderConnections(AccountUi("result.failed"), providerId);
                });
            }
            finally { cancellation.Dispose(); }
        });
    }

    private string ProviderOperationSummary(ProviderConnectionMenuResult result)
    {
        var values = new List<string> { AccountUi("result.state") + ProviderConnectionStateText(result.State) };
        if (result.ValidatedAt is { } validated)
            values.Add(AccountUi("date.validated") + FormatProviderTimestamp(validated));
        if (result.DiscoveredAt is { } discovered)
            values.Add(AccountUi("date.models") + FormatProviderTimestamp(discovered));
        if (result.RegisteredModels is { } models)
        {
            values.Add(_localization.Resolve("accounts.ui.result.registered", new Dictionary<string, string> { ["0"] = models.ToString(CultureInfo.InvariantCulture) }));
            values.Add(_localization.Resolve("accounts.ui.result.quota", new Dictionary<string, string> { ["0"] = result.QuotaWindows.ToString(CultureInfo.InvariantCulture) }));
        }
        if (result.Truncated) values.Add(AccountUi("result.truncated"));
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
