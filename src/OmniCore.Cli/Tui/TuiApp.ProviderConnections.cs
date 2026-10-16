using System.Globalization;
using OmniCore.Host;
using OmniCore.Client;
using OmniCore.Protocol;
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

    private void ShowProviderConnections(string? activity = null, string? selectedProviderId = null, bool preferOpenRouter = false)
    {
        if (_window is null || _overlay is not null) return;
        if (_preferencesProvider is { } chosen && chosen.ProviderId == selectedProviderId)
        {
            try
            {
                _preferencesProvider = _providerConnectionHost.List(CancellationToken.None)
                    .FirstOrDefault(row => row.ProviderId == chosen.ProviderId && row.Backend == chosen.Backend) ?? chosen;
            }
            catch (Exception) { activity = AccountUi("error.readApi"); }
            ShowPreferences("accounts_connections", categoryPage: true);
            if (activity is not null && FindPreferenceView(_overlay, "provider-key-status") is Label inlineStatus)
                inlineStatus.Text = activity;
            return;
        }

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

        if (preferOpenRouter && !rows.Any(row => row.Backend == "openrouter"))
        { ShowMessage(_localization.Resolve("openrouter.status.empty")); return; }

        var frameHeight = Math.Clamp(_window.Frame.Height - 4, 18, 30);
        var frame = OverlayFrame(AccountUi("title.api"), frameHeight);
        frame.Id = "provider-connections";
        frame.Add(new Label { X = 2, Y = 2, Width = Dim.Fill(2), Height = 2,
            Text = AccountUi("help.providerState") });
        var actionY = frameHeight - 6;
        var statusY = actionY - 2;
        var list = new ListView { Id = "provider-connection-list", X = 2, Y = 4, Width = Dim.Fill(4), Height = frameHeight - 13,
            CanFocus = rows.Length > 0, ShowMarks = false };
        list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(rows.Select(ProviderConnectionRowText)));
        if (rows.Length > 0)
        {
            var selectedIndex = selectedProviderId is null ? preferOpenRouter ? Array.FindIndex(rows, row => row.Backend == "openrouter") : -1
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
        var credits = new Button { Id = "provider-credits", X = 2, Y = actionY + 2, Width = Dim.Percent(46),
            Text = _localization.Resolve("openrouter.credits.action") };
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
            credits.Enabled = selected?.Backend == "openrouter" && selected.CanTest
                && _providerConnectionHost is ITuiOpenRouterCatalogHost;
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
                ShowProviderApiKey(selected);
            }
        };
        test.Accepted += (_, _) =>
        {
            if (SelectedRow() is { CanTest: true } selected)
                StartProviderConnectionOperation(frame, status, selected.ProviderId,
                    async token => await _providerConnectionHost.TestAsync(selected, token)
                        .ConfigureAwait(false));
        };
        discover.Accepted += (_, _) =>
        {
            if (SelectedRow() is { CanDiscoverModels: true } selected)
            {
                if (selected.Backend == "openrouter"
                    && _providerConnectionHost is ITuiOpenRouterCatalogHost catalog
                    && catalog.SupportsOpenRouter(selected.ProviderId, CancellationToken.None))
                { CloseOverlay(); ShowOpenRouterCatalog(selected.ProviderId); }
                else StartProviderConnectionOperation(frame, status, selected.ProviderId,
                    async token => await _providerConnectionHost.DiscoverAsync(selected, token)
                        .ConfigureAwait(false));
            }
        };
        disconnect.Accepted += (_, _) =>
        {
            if (SelectedRow() is { CanDisconnect: true } selected)
                ShowProviderDisconnectConfirmation(selected);
        };

        credits.Accepted += (_, _) =>
        {
            if (SelectedRow() is { Backend: "openrouter", CanTest: true } selected
                && _providerConnectionHost is ITuiOpenRouterCatalogHost catalog)
            { CloseOverlay(); ShowOpenRouterCredits(selected.ProviderId, catalog); }
        };

        frame.Add(list, status, connect, test, discover, disconnect, credits, back);
        ApplyTheme(frame);
        _overlay = frame;
        _window.Add(frame);
        _preferencesReturnToCategory = true;
        RefreshActions();
        if (rows.Length > 0) list.SetFocus(); else back.SetFocus();
    }

    private void ShowOpenRouterCredits(string providerId, ITuiOpenRouterCatalogHost host)
    {
        if (_window is null || _overlay is not null) return;
        var frame = OverlayFrame(_localization.Resolve("openrouter.credits.title"), 17);
        frame.Id = "openrouter-credits";
        var detail = new Label { Id = "openrouter-credits-detail", X = 2, Y = 2,
            Width = Dim.Fill(4), Height = 9, Text = "" };
        var status = new Label { Id = "openrouter-credits-status", X = 2, Y = 11,
            Width = Dim.Fill(4), Height = 1, Text = "" };
        var refresh = new Button { Id = "openrouter-credits-refresh", X = 2, Y = Pos.AnchorEnd(2),
            Text = _localization.Resolve("openrouter.catalog.refresh") };
        var back = new Button { Id = "openrouter-credits-back", X = Pos.AnchorEnd(12), Y = Pos.AnchorEnd(2),
            Text = AccountUi("action.back") };
        back.Accepted += (_, _) => { CloseOverlay(); ShowProviderConnections(selectedProviderId: providerId); };
        OpenRouterCreditSnapshot? displayedSnapshot = null;
        void Refresh()
        {
            if (_providerConnectionCancellation is not null) return;
            var cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            _providerConnectionCancellation = cancellation;
            refresh.Enabled = false;
            status.Text = _localization.Resolve("openrouter.credits.loading");
            _providerConnectionTask = Task.Run(async () =>
            {
                try
                {
                    var snapshot = await host.ReadOpenRouterCreditsAsync(providerId, token).ConfigureAwait(false);
                    _uiActions.Enqueue(() =>
                    {
                        if (token.IsCancellationRequested || !ReferenceEquals(_overlay, frame)
                            || !ReferenceEquals(_providerConnectionCancellation, cancellation)) return;
                        _providerConnectionCancellation = null;
                        displayedSnapshot = snapshot;
                        detail.Text = OpenRouterCreditsText(providerId, snapshot);
                        status.Text = snapshot.AccountBalanceAvailability == MetricAvailability.NotSupported
                            ? _localization.Resolve("openrouter.credits.balanceUnavailable") : "";
                        refresh.Enabled = true;
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
                        if (displayedSnapshot is { } previous)
                            detail.Text = OpenRouterCreditsText(providerId, previous with
                            {
                                KeyAvailability = previous.KeyAvailability == MetricAvailability.Reported
                                    ? MetricAvailability.Stale : previous.KeyAvailability,
                                AccountBalanceAvailability = previous.AccountBalanceAvailability == MetricAvailability.Reported
                                    ? MetricAvailability.Stale : previous.AccountBalanceAvailability,
                            });
                        status.Text = _localization.Resolve("openrouter.credits.failed");
                        refresh.Enabled = true;
                    });
                }
                finally { cancellation.Dispose(); }
            });
        }
        refresh.Accepted += (_, _) => Refresh();
        frame.Add(detail, status, refresh, back);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame);
        _preferencesReturnToCategory = true;
        back.SetFocus();
        // Entering this page is the user's explicit credit query; listing connections never queries.
        Refresh();
    }

    private string OpenRouterCreditsText(string providerId, OpenRouterCreditSnapshot snapshot)
    {
        string Amount(decimal? value) => value?.ToString("0.############################",
            PreferenceInput.Culture(_locale)) ?? "—";
        var keyKnown = snapshot.KeyAvailability is MetricAvailability.Reported or MetricAvailability.Stale;
        var balanceKnown = snapshot.AccountBalanceAvailability is MetricAvailability.Reported or MetricAvailability.Stale;
        var balance = balanceKnown ? snapshot.Measurements.FirstOrDefault(value => value.Scope == CreditScope.AccountBalance)
            ?.Amount.Value : null;
        var staleKey = snapshot.KeyAvailability == MetricAvailability.Stale
            ? _localization.Resolve("openrouter.credits.stale") : "";
        var staleBalance = snapshot.AccountBalanceAvailability == MetricAvailability.Stale
            ? _localization.Resolve("openrouter.credits.stale") : "";
        return string.Join("\n", new[]
        {
            _localization.Resolve("openrouter.credits.provider") + ": " + providerId,
            _localization.Resolve("openrouter.credits.keyLimit") + staleKey + ": " + Amount(keyKnown ? snapshot.Key.Limit : null),
            _localization.Resolve("openrouter.credits.remaining") + staleKey + ": " + Amount(keyKnown ? snapshot.Key.LimitRemaining : null),
            _localization.Resolve("openrouter.credits.usage") + staleKey + ": " + Amount(keyKnown ? snapshot.Key.Usage : null),
            _localization.Resolve("openrouter.credits.accountBalance") + staleBalance + ": " + Amount(balance),
            _localization.Resolve("openrouter.credits.asOf") + ": " + (snapshot.AsOf == DateTimeOffset.MinValue ? "—"
                : FormatProviderTimestamp(snapshot.AsOf)),
        });
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
                await Task.Run(() => _providerConnectionHost.Disconnect(row, token), token)
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

    private void ShowProviderApiKey(ProviderConnectionMenuRow target)
    {
        var providerId = target.ProviderId;
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
                async token => await _providerConnectionHost.ConnectAsync(target, apiKey, shouldValidate, token)
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

    private void ShowOpenRouterCatalog(string providerId, string? search = null, int offset = 0,
        OpenRouterCatalogPage? loadedPage = null, string? activity = null)
    {
        if (_window is null || _overlay is not null
            || _providerConnectionHost is not ITuiOpenRouterCatalogHost catalogHost) return;
        OpenRouterCatalogPage page;
        try { page = loadedPage ?? catalogHost.BrowseOpenRouterCatalog(providerId, search, offset, 30, CancellationToken.None); }
        catch (Exception) { ShowMessage(_localization.Resolve("openrouter.catalog.failed")); return; }
        var height = Math.Clamp(_window.Frame.Height - 4, 17, 30);
        var frame = OverlayFrame(_localization.Resolve("openrouter.catalog.title"), height);
        frame.Add(new Label { X = 2, Y = 1, Width = Dim.Fill(4), Height = 2,
            Text = _localization.Resolve("openrouter.catalog.help") });
        var filter = new TextField { Id = "openrouter-catalog-search", X = 2, Y = 3,
            Width = Dim.Fill(4), Text = search ?? string.Empty };
        var rows = page.Models.Select(model => CatalogDisplay(model.Id, 160) + " · " + CatalogDisplay(model.Name, 80)
            + (model.ContextLength is { } context ? " · " + context.ToString(CultureInfo.InvariantCulture) : "")
            + " · USD " + CatalogDisplay(model.PromptPricePerToken, 32) + " "
            + _localization.Resolve("openrouter.catalog.perToken") + " "
            + _localization.Resolve("openrouter.catalog.input")).ToArray();
        var list = new ListView { Id = "openrouter-catalog-models", X = 2, Y = 5,
            Width = Dim.Fill(4), Height = height - 12, CanFocus = rows.Length > 0, ShowMarks = false };
        list.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(rows));
        if (rows.Length > 0) list.SelectedItem = 0;
        var details = new Label { Id = "openrouter-catalog-details", X = 2, Y = height - 7,
            Width = Dim.Fill(4), Height = 1, Text = "" };
        var statusText = activity ?? (page.Stale ? _localization.Resolve("openrouter.catalog.stale")
            : page.Error is not null ? _localization.Resolve("openrouter.catalog.failed")
            : page.Models.Count == 0 ? _localization.Resolve("openrouter.catalog.notLoaded")
            : page.Truncated ? _localization.Resolve("openrouter.catalog.truncated") : "");
        var status = new Label { Id = "openrouter-catalog-status", X = 2, Y = height - 5,
            Width = Dim.Fill(4), Height = 1, Text = statusText };
        void ShowDetails()
        {
            var selected = list.SelectedItem;
            if (selected is not { } index || index < 0 || index >= page.Models.Count) { details.Text = ""; return; }
            var model = page.Models[index];
            var parts = new List<string> { CatalogDisplay(model.Id, 120) };
            if (model.MaxCompletionTokens is { } output) parts.Add(_localization.Resolve("openrouter.catalog.max") + " " + output.ToString(CultureInfo.InvariantCulture));
            if (model.CompletionPricePerToken is { } completion) parts.Add("USD " + CatalogDisplay(completion, 32) + " "
                + _localization.Resolve("openrouter.catalog.perToken") + " " + _localization.Resolve("openrouter.catalog.output"));
            if (model.InputModalities.Count > 0) parts.Add(_localization.Resolve("openrouter.catalog.input") + " " + CatalogDisplay(string.Join(",", model.InputModalities), 80));
            if (model.OutputModalities.Count > 0) parts.Add(_localization.Resolve("openrouter.catalog.output") + " " + CatalogDisplay(string.Join(",", model.OutputModalities), 80));
            details.Text = string.Join(" · ", parts);
        }
        list.ValueChanged += (_, _) => ShowDetails();
        var refresh = new Button { Id = "openrouter-catalog-refresh", X = 2, Y = height - 3,
            Width = Dim.Percent(30), Text = _localization.Resolve("openrouter.catalog.refresh") };
        var register = new Button { Id = "openrouter-catalog-register", X = Pos.Right(refresh) + 1,
            Y = height - 3, Width = Dim.Percent(34), Text = _localization.Resolve("openrouter.catalog.register") };
        var next = new Button { Id = "openrouter-catalog-next", X = Pos.Right(register) + 1,
            Y = height - 3, Width = Dim.Percent(15), Text = _localization.Resolve("openrouter.catalog.next") };
        var back = new Button { Id = "openrouter-catalog-back", X = Pos.AnchorEnd(12), Y = Pos.AnchorEnd(1),
            Text = AccountUi("action.back") };
        void Reload(string? term, int start, OpenRouterCatalogPage? newPage = null, string? message = null)
        {
            CloseOverlay(); ShowOpenRouterCatalog(providerId, term, start, newPage, message);
        }
        filter.ValueChanged += (_, _) =>
        {
            var term = filter.Text?.ToString() ?? string.Empty;
            if (term.Length <= 100)
            {
                try { Reload(term, 0, catalogHost.BrowseOpenRouterCatalog(providerId, term, 0, 30, CancellationToken.None)); }
                catch (Exception) { status.Text = _localization.Resolve("openrouter.catalog.failed"); }
            }
        };
        refresh.Accepted += (_, _) => RunOpenRouterCatalogOperation(frame, status, providerId,
            AccountUi("result.connecting"), async token => await catalogHost.RefreshOpenRouterCatalogAsync(providerId,
                filter.Text?.ToString(), 0, 30, token).ConfigureAwait(false),
            refreshed => Reload(filter.Text?.ToString(), 0, refreshed, refreshed.Stale
                ? _localization.Resolve("openrouter.catalog.stale") : refreshed.Truncated ? _localization.Resolve("openrouter.catalog.truncated") : ""));
        register.Enabled = page.Models.Count > 0;
        register.Accepted += (_, _) =>
        {
            if (list.SelectedItem is not { } selected || selected < 0 || selected >= page.Models.Count)
            { status.Text = _localization.Resolve("openrouter.catalog.select"); return; }
            var model = page.Models[selected];
            RunOpenRouterCatalogOperation(frame, status, providerId, AccountUi("result.connecting"),
                token => Task.FromResult(catalogHost.RegisterOpenRouterModels(providerId, [model.Id], token)),
                added =>
                {
                    CloseOverlay();
                    ShowProviderConnections(_localization.Resolve("openrouter.catalog.registered",
                        new Dictionary<string, string> { ["0"] = model.Id }) + (added == 0 ? "" : " · +" + added), providerId);
                });
        };
        next.Enabled = page.NextOffset is not null;
        next.Accepted += (_, _) => Reload(filter.Text?.ToString(), page.NextOffset ?? offset);
        var previous = new Button { Id = "openrouter-catalog-previous", X = Pos.Right(next) + 1,
            Y = height - 3, Width = Dim.Percent(15), Text = _localization.Resolve("openrouter.catalog.previous"), Enabled = offset > 0 };
        previous.Accepted += (_, _) => Reload(filter.Text?.ToString(), Math.Max(0, offset - 30));
        back.Accepted += (_, _) => { CloseOverlay(); ShowProviderConnections(selectedProviderId: providerId); };
        frame.Add(filter, list, details, status, refresh, register, previous, next, back);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); _preferencesReturnToCategory = true;
        ShowDetails();
        filter.SetFocus();
    }

    private static string CatalogDisplay(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value)) return "?";
        var safe = new string(value.Where(character => !char.IsControl(character) && character != '\u001b').ToArray());
        return safe.Length <= maximumLength ? safe : safe[..maximumLength] + "…";
    }

    private void RunOpenRouterCatalogOperation<T>(FrameView frame, Label status, string providerId,
        string workingText, Func<CancellationToken, Task<T>> operation, Action<T> completed)
    {
        if (_providerConnectionCancellation is not null) return;
        var cancellation = new CancellationTokenSource(); var token = cancellation.Token;
        _providerConnectionCancellation = cancellation; status.Text = workingText;
        foreach (var button in frame.SubViews.OfType<Button>()) button.Enabled = false;
        _providerConnectionTask = Task.Run(async () =>
        {
            try
            {
                var value = await operation(token).ConfigureAwait(false);
                _uiActions.Enqueue(() =>
                {
                    if (token.IsCancellationRequested || !ReferenceEquals(_overlay, frame)
                        || !ReferenceEquals(_providerConnectionCancellation, cancellation)) return;
                    _providerConnectionCancellation = null; completed(value);
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception)
            {
                _uiActions.Enqueue(() =>
                {
                    if (!ReferenceEquals(_overlay, frame) || !ReferenceEquals(_providerConnectionCancellation, cancellation)) return;
                    _providerConnectionCancellation = null; status.Text = _localization.Resolve("openrouter.catalog.failed");
                    foreach (var button in frame.SubViews.OfType<Button>()) button.Enabled = true;
                });
            }
            finally { cancellation.Dispose(); }
        });
    }

    private void ClearProviderApiKey()
    {
        if (_providerApiKeyField is not null) _providerApiKeyField.Text = "";
        _providerApiKeyField = null;
    }
}
