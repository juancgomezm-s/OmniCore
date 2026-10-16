using OmniCore.Host;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private ProviderConnectionMenuRow? _preferencesProvider;

    private static string ProviderDisplayName(ProviderConnectionMenuRow row) => row.Backend == "openrouter"
        ? row.ProviderId == "openrouter" ? "OpenRouter" : "OpenRouter · " + row.ProviderId
        : row.ProviderId == "anthropic" ? "Anthropic" : row.ProviderId;

    private void ShowProviderPreferences(ProviderConnectionMenuRow target)
    {
        _preferencesProvider = target;
        CloseOverlay();
        ShowPreferences("accounts_connections", categoryPage: true);
    }

    private int RenderProviderPreferences(View panel, List<View> controls, ProviderConnectionMenuRow target)
    {
        var heading = panel.SubViews.OfType<Label>().Single(view => view.Id == "preferences-description");
        heading.Text = ProviderDisplayName(target) + " · " + ProviderConnectionStateText(target.State);
        void Add(View view) { panel.Add(view); controls.Add(view); }
        Add(new Label { X = 3, Y = 2, Text = AccountUi("method.apiKey") });
        var key = _providerApiKeyField = new TextField { Id = "provider-api-key", X = 3, Y = 3,
            Width = Dim.Fill(3), Secret = true, CanFocus = target.CanConnect, Enabled = target.CanConnect };
        var validate = new CheckBox { Id = "provider-validate-key", X = 3, Y = 4,
            Width = Dim.Fill(3), Text = AccountUi("action.validate"), Enabled = target.CanConnect };
        var status = new Label { Id = "provider-key-status", X = 3, Y = 5, Width = Dim.Fill(3), Height = 2,
            Text = AccountUi("help.apiKey") };
        var save = new Button { Id = "provider-key-save", X = 3, Y = 7, Width = Dim.Percent(43), Text = AccountUi("action.save") };
        var test = new Button { Id = "provider-test", X = Pos.Right(save) + 1, Y = 7, Width = Dim.Fill(3), Text = AccountUi("action.test") };
        var catalog = new Button { Id = "provider-discover", X = 3, Y = 9, Width = Dim.Percent(43), Text = AccountUi("action.discover") };
        var credits = new Button { Id = "provider-credits", X = Pos.Right(catalog) + 1, Y = 9, Width = Dim.Fill(3),
            Text = _localization.Resolve("openrouter.credits.action"), Visible = target.Backend == "openrouter" };
        var disconnect = new Button { Id = "provider-disconnect", X = 3, Y = 11, Width = Dim.Percent(43), Text = AccountUi("action.disconnect") };
        var back = new Button { Id = "provider-key-cancel", X = Pos.Right(disconnect) + 1, Y = 11, Width = Dim.Fill(3), Text = AccountUi("action.back") };

        void RefreshActions()
        {
            var busy = _providerConnectionCancellation is not null;
            var dirty = !string.IsNullOrEmpty(key.Text?.ToString());
            key.Enabled = !busy && target.CanConnect;
            validate.Enabled = !busy && target.CanConnect;
            save.Enabled = !busy && target.CanConnect;
            test.Enabled = !busy && !dirty && target.CanTest;
            catalog.Enabled = !busy && !dirty && target.CanDiscoverModels;
            credits.Enabled = !busy && !dirty && target.CanTest && _providerConnectionHost is ITuiOpenRouterCatalogHost;
            disconnect.Enabled = !busy && !dirty && target.CanDisconnect;
        }

        void Start(Func<CancellationToken, Task<ProviderConnectionMenuResult?>> operation)
        {
            if (_providerConnectionCancellation is not null || _overlay is null) return;
            var owner = _overlay;
            var cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            _providerConnectionCancellation = cancellation;
            status.Text = AccountUi("result.connecting");
            RefreshActions();
            _providerConnectionTask = Task.Run(async () =>
            {
                try
                {
                    var result = await operation(token).ConfigureAwait(false);
                    _uiActions.Enqueue(() =>
                    {
                        if (token.IsCancellationRequested || !ReferenceEquals(_overlay, owner)
                            || !ReferenceEquals(_providerConnectionCancellation, cancellation)) return;
                        _providerConnectionCancellation = null;
                        try
                        {
                            target = _providerConnectionHost.List(CancellationToken.None)
                                .FirstOrDefault(row => row.ProviderId == target.ProviderId && row.Backend == target.Backend) ?? target;
                        }
                        catch (Exception)
                        { status.Text = AccountUi("error.readApi"); RefreshActions(); return; }
                        _preferencesProvider = target;
                        heading.Text = ProviderDisplayName(target) + " · " + ProviderConnectionStateText(target.State);
                        status.Text = result is null ? AccountUi("result.removed") : ProviderOperationSummary(result);
                        RefreshActions();
                    });
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception)
                {
                    _uiActions.Enqueue(() =>
                    {
                        if (token.IsCancellationRequested || !ReferenceEquals(_overlay, owner)
                            || !ReferenceEquals(_providerConnectionCancellation, cancellation)) return;
                        _providerConnectionCancellation = null;
                        status.Text = AccountUi("result.failed");
                        RefreshActions();
                    });
                }
                finally { cancellation.Dispose(); }
            });
        }

        void Save()
        {
            if (!save.Enabled) return;
            var secret = key.Text?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(secret))
            { status.Text = AccountUi("result.enterKey"); key.SetFocus(); return; }
            var shouldValidate = validate.Value == CheckState.Checked;
            key.Text = "";
            Start(async token => await _providerConnectionHost.ConnectAsync(target, secret, shouldValidate, token).ConfigureAwait(false));
        }
        save.Accepted += (_, _) => Save();
        key.KeyDown += (_, input) =>
        {
            if (input.KeyCode != KeyCode.Enter) return;
            Save(); input.Handled = true;
        };
        key.TextChanged += (_, _) => RefreshActions();
        test.Accepted += (_, _) => Start(async token => await _providerConnectionHost.TestAsync(target, token).ConfigureAwait(false));
        catalog.Accepted += (_, _) =>
        {
            if (target.Backend == "openrouter" && _providerConnectionHost is ITuiOpenRouterCatalogHost browser
                && browser.SupportsOpenRouter(target.ProviderId, CancellationToken.None))
            { CloseOverlay(); ShowOpenRouterCatalog(target.ProviderId); }
            else Start(async token => await _providerConnectionHost.DiscoverAsync(target, token).ConfigureAwait(false));
        };
        credits.Accepted += (_, _) =>
        {
            if (_providerConnectionHost is ITuiOpenRouterCatalogHost host)
            { CloseOverlay(); ShowOpenRouterCredits(target.ProviderId, host); }
        };
        disconnect.Accepted += (_, _) => ShowProviderDisconnectConfirmation(target);
        back.Accepted += (_, _) =>
        { _preferencesProvider = null; CloseOverlay(); ShowPreferences("accounts_connections", categoryPage: true); };
        foreach (var view in new View[] { key, validate, status, save, test, catalog, credits, disconnect, back }) Add(view);
        RefreshActions();
        return 13;
    }
}
