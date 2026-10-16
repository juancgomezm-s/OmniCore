using OmniCore.Client;
using BillingMode = OmniCore.Domain.BillingMode;
using OmniCore.Host;
using System.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    private sealed class ClaudeAccountsFixture : ITuiClaudeAccountHost
    {
        public IReadOnlyList<TuiClaudeAccountRow> Rows { get; set; } = [];
        public int ListCalls { get; private set; }
        public int LoginCalls { get; private set; }
        public int LogoutCalls { get; private set; }
        public string? LastProviderId { get; private set; }
        public TaskCompletionSource<TuiClaudeLoginOutcome>? LoginGate { get; set; }
        public TuiClaudeLoginOutcome Outcome { get; set; } = new(true);

        public IReadOnlyList<TuiClaudeAccountRow> List(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); ListCalls++; return Rows; }

        public Task<TuiClaudeLoginOutcome> LoginAsync(string providerId, Action<TuiClaudeLoginProgress> progress,
            CancellationToken cancellationToken)
        {
            LoginCalls++; LastProviderId = providerId;
            progress(new("PreparingChallenge"));
            return LoginGate?.Task ?? Task.FromResult(Outcome);
        }

        public void Logout(string providerId, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); LogoutCalls++; LastProviderId = providerId; }
    }

    private sealed class ProviderConnectionsFixture : ITuiProviderConnectionHost, ITuiOpenRouterCatalogHost
    {
        public IReadOnlyList<ProviderConnectionMenuRow> Rows { get; init; } = [];
        public int ListCalls { get; private set; }
        public int OperationCalls { get; private set; }
        public string? LastProviderId { get; private set; }
        public int CatalogBrowseCalls { get; private set; }
        public int CatalogRefreshCalls { get; private set; }
        public int CreditCalls { get; private set; }
        public bool FailCreditRead { get; set; }
        public bool FailConnect { get; set; }
        public IReadOnlyList<string> RegisteredModels { get; private set; } = [];
        public IReadOnlyList<ProviderConnectionMenuRow> List(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); ListCalls++; return Rows; }
        public Task<ProviderConnectionMenuResult> ConnectAsync(string apiKey, bool validate, CancellationToken cancellationToken)
        { OperationCalls++; return Task.FromResult(new ProviderConnectionMenuResult(ProviderConnectionState.Unknown, null, null)); }
        public Task<ProviderConnectionMenuResult> ConnectAsync(string providerId, string apiKey, bool validate, CancellationToken cancellationToken)
        {
            OperationCalls++; LastProviderId = providerId;
            if (FailConnect) throw new IOException("Invalid fixture connection");
            return Task.FromResult(new ProviderConnectionMenuResult(ProviderConnectionState.Unknown, null, null));
        }
        public Task<ProviderConnectionMenuResult> TestAsync(string providerId, CancellationToken cancellationToken)
        { OperationCalls++; return Task.FromResult(new ProviderConnectionMenuResult(ProviderConnectionState.Unknown, null, null)); }
        public Task<ProviderConnectionMenuResult> DiscoverAsync(string providerId, CancellationToken cancellationToken)
        { OperationCalls++; return Task.FromResult(new ProviderConnectionMenuResult(ProviderConnectionState.Unknown, null, null)); }
        public void Disconnect(string providerId, CancellationToken cancellationToken)
        { OperationCalls++; LastProviderId = providerId; }
        public bool SupportsOpenRouter(string providerId, CancellationToken cancellationToken) => providerId == "openrouter";
        public OpenRouterCatalogPage BrowseOpenRouterCatalog(string providerId, string? search, int offset, int limit,
            CancellationToken cancellationToken)
        {
            CatalogBrowseCalls++;
            IReadOnlyList<OpenRouterModel> models = [new("openai/fixture-model", "Fixture model", "", 8000, 2000,
                "0.0000001", "0.0000002", ["text"], ["text"], ["tools"], "{\"id\":\"openai/fixture-model\"}")];
            return search is not null && !"openai/fixture-model".Contains(search, StringComparison.OrdinalIgnoreCase)
                ? new([], 0, null, false, false, null)
                : new(models.Skip(offset).Take(limit).ToArray(), 1, null, false, false, null);
        }
        public Task<OpenRouterCatalogPage> RefreshOpenRouterCatalogAsync(string providerId, string? search,
            int offset, int limit, CancellationToken cancellationToken)
        {
            CatalogRefreshCalls++;
            return Task.FromResult(BrowseOpenRouterCatalog(providerId, search, offset, limit, cancellationToken));
        }
        public int RegisterOpenRouterModels(string providerId, IReadOnlyList<string> modelIds,
            CancellationToken cancellationToken)
        { RegisteredModels = modelIds; return modelIds.Count; }
        public Task<OpenRouterCreditSnapshot> ReadOpenRouterCreditsAsync(string providerId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreditCalls++; LastProviderId = providerId;
            if (FailCreditRead) throw new IOException("Credit fixture unavailable");
            var at = new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
            return Task.FromResult(new OpenRouterCreditSnapshot([], OmniCore.Protocol.MetricAvailability.NotSupported,
                null, at, at, new(10m, 7m, 3m, null, null, false, at)) { ProviderId = providerId });
        }
    }

    private sealed class LocalEngineSupervisorFixture : ITuiLocalEngineSupervisor
    {
        private int _reads;
        private int _ensureCalls;
        private int _stopCalls;
        public int EnsureCalls => Volatile.Read(ref _ensureCalls);
        public int StopCalls => Volatile.Read(ref _stopCalls);
        public string? LastProviderId { get; private set; }
        public LocalEngineRuntimeStatus Status { get; set; } = new("fixture-engine", "managed", false, false,
            "localEngines.runtime.managedUnowned");
        public ManualResetEventSlim? EnsureGate { get; set; }
        public int ReadCalls => Volatile.Read(ref _reads);

        public IReadOnlyList<LocalEngineRuntimeStatus> Read(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref _reads); return [Status]; }

        public LocalEngineRuntimeActionResult EnsureReady(string providerId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _ensureCalls); LastProviderId = providerId;
            EnsureGate?.Wait(cancellationToken);
            Status = new(providerId, "managed", true, true, "localEngines.runtime.managedRunning");
            return new(true, Status.StateKey);
        }

        public LocalEngineRuntimeActionResult Stop(string providerId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _stopCalls); LastProviderId = providerId;
            Status = new(providerId, "managed", true, false, "localEngines.runtime.managedStopped");
            return new(true, Status.StateKey);
        }
    }

    [Fact]
    public void Accounts_preferences_resources_are_complete_in_all_supported_locales() =>
        Assert.All(new[] { "es", "en", "fr", "it" }, locale =>
            Assert.All(AccountsPreferencesResources.Keys, key =>
                Assert.True(AccountsPreferencesResources.TryTranslate(locale, key, out var text)
                    && !string.IsNullOrWhiteSpace(text), locale + " missing " + key)));

    [Fact]
    public void Preferences_accounts_open_without_login_and_keep_claude_identity_exact() => RunTuiTest(fx =>
    {
        var accounts = new ClaudeAccountsFixture
        {
            Rows =
            [
                new("claude-work", true, true, false, false, "j***@***", "Pro", null, null, null),
                new("claude-alt", true, false, false, false, null, null, null, null, null),
            ],
        };
        fx.StartTui(claudeAccounts: accounts, initialColumns: 80, initialRows: 25);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "abre preferencias");
        ChoosePreferenceCategory(fx, "accounts_connections");
        Assert.Equal(0, accounts.LoginCalls);
        Assert.Equal(1, accounts.ListCalls);
        ClickId(fx, "preferences-claude-open");
        fx.Wait(() => fx.App.Overlay is not null
            && Descendants(fx.App.Overlay).Any(view => view.Id == "claude-account-list"), "se abre lista de cuentas Claude");
        AssertClaudePageFits(fx.App.Overlay!);
        ExportPreferenceFrame(fx, 80, 25, "accounts-claude", "claude-alt");
        Assert.Equal(0, accounts.LoginCalls);
        var list = Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "claude-account-list");
        fx.Invoke(() => list.SelectedItem = 1);
        ClickId(fx, "claude-account-login");
        fx.Wait(() => accounts.LoginCalls == 1, "login explícito invoca proveedor seleccionado");
        Assert.Equal("claude-alt", accounts.LastProviderId);
        fx.Wait(() => fx.App.Overlay is not null && OverlayText(fx.App.Overlay).Contains("Sesión Claude conectada."),
            "resultado de login localizado vuelve a cuentas");
        Assert.Equal(0, accounts.LogoutCalls);
    });

    [Theory]
    [InlineData(80, 25, "accounts-claude-narrow")]
    [InlineData(120, 40, "accounts-claude-wide")]
    public void Claude_accounts_detail_and_controls_fit_without_overlap(int columns, int rows, string scene) => RunTuiTest(fx =>
    {
        var accounts = new ClaudeAccountsFixture
        {
            Rows = [new("claude-pro", true, true, false, false, "m***@***", "Pro", "tier-1",
                new DateTimeOffset(2027, 1, 2, 3, 4, 0, TimeSpan.Zero), null)],
        };
        fx.StartTui(claudeAccounts: accounts, initialColumns: columns, initialRows: rows);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "centro visible");
        ChoosePreferenceCategory(fx, "accounts_connections");
        ClickId(fx, "preferences-claude-open");
        fx.Wait(() => fx.App.Overlay is not null
            && Descendants(fx.App.Overlay).Any(view => view.Id == "claude-account-detail"), "cuentas visibles");
        var overlay = fx.App.Overlay!;
        AssertClaudePageFits(overlay);
        var detail = Descendants(overlay).Single(view => view.Id == "claude-account-detail");
        var detailText = detail.Text?.ToString() ?? "";
        Assert.Contains("claude-pro", detailText);
        Assert.Contains("m***@***", detailText);
        Assert.Contains("Pro", detailText);
        Assert.Contains("2027-01-02", detailText);
        Assert.DoesNotContain("maria", detailText, StringComparison.OrdinalIgnoreCase);
        ExportPreferenceFrame(fx, columns, rows, scene, "claude-pro");
    });

    [Fact]
    public void Provider_connections_open_without_operations_and_keep_capabilities_exact() => RunTuiTest(fx =>
    {
        var providers = new ProviderConnectionsFixture
        {
            Rows =
            [
                new("openai-custom", ProviderConnectionMethod.ApiKey, ProviderConnectionState.Unknown,
                    BillingMode.Unknown, null, null, false, false, false, false, null, null),
                new("anthropic", ProviderConnectionMethod.ApiKey, ProviderConnectionState.NotConfigured,
                    BillingMode.MeteredCurrency, null, null, true, false, false, false, null, null),
            ],
        };
        fx.StartTui(providerConnections: providers, initialColumns: 80, initialRows: 25);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "centro visible");
        ChoosePreferenceCategory(fx, "accounts_connections");
        Assert.Equal(1, providers.ListCalls);
        Assert.Equal(0, providers.OperationCalls);
        Assert.DoesNotContain(Descendants(fx.App.Overlay!), view => view.Id == "preferences-api-open");
        ClickId(fx, "preferences-provider-open-openai-custom");
        fx.Wait(() => Descendants(fx.App.Overlay!).Any(view => view.Id == "provider-key-save"), "direct unsupported provider view");
        var overlay = fx.App.Overlay!;
        Assert.All(Descendants(overlay).OfType<Button>().Where(view => view.Id is "provider-key-save" or "provider-test" or "provider-discover" or "provider-disconnect"),
            view => Assert.False(view.Enabled));
        Assert.True(AccountsPreferencesResources.TryTranslate("es", "accounts.ui.help.noProviderAction", out var unavailableMessage));
        Assert.Contains(unavailableMessage, OverlayText(overlay));
        ClickId(fx, "provider-key-cancel");
        ClickId(fx, "preferences-provider-open-anthropic");
        fx.Wait(() => Descendants(fx.App.Overlay!).Any(view => view.Id == "provider-key-save"), "direct Anthropic form");
        Assert.True(Descendants(fx.App.Overlay!).OfType<Button>().Single(view => view.Id == "provider-key-save").Enabled);
        foreach (var id in new[] { "provider-test", "provider-discover", "provider-disconnect" })
            Assert.False(Descendants(fx.App.Overlay!).OfType<Button>().Single(view => view.Id == id).Enabled);
        Assert.Equal(0, providers.OperationCalls);
        ExportPreferenceFrame(fx, 80, 25, "accounts-api", "Anthropic");
    });

    [Fact]
    public void OpenRouter_catalog_view_browses_locally_refreshes_explicitly_and_registers_only_selection() => RunTuiTest(fx =>
    {
        var providers = new ProviderConnectionsFixture
        {
            Rows = [new("openrouter", ProviderConnectionMethod.ApiKey, ProviderConnectionState.Connected,
                BillingMode.MeteredCurrency, null, null, false, true, true, true, null, null) { Backend = "openrouter" }],
        };
        fx.StartTui(providerConnections: providers, initialColumns: 90, initialRows: 27);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences opened");
        ChoosePreferenceCategory(fx, "accounts_connections");
        ClickId(fx, "preferences-openrouter-open");
        fx.Wait(() => Descendants(fx.App.Overlay!).Any(view => view.Id == "provider-discover"), "direct provider form visible");
        ClickId(fx, "provider-discover");
        fx.Wait(() => Descendants(fx.App.Overlay!).Any(view => view.Id == "openrouter-catalog-models"), "catalog view visible");
        Assert.Equal(0, providers.CatalogRefreshCalls);
        Assert.Contains("openrouter.catalog.help", OpenRouterResources.Keys);
        Assert.All(new[] { "es", "en", "fr", "it" }, locale =>
            Assert.All(OpenRouterResources.Keys, key =>
                Assert.True(OpenRouterResources.TryTranslate(locale, key, out var value) && !string.IsNullOrWhiteSpace(value), locale + " missing " + key)));

        ClickId(fx, "openrouter-catalog-refresh");
        fx.Wait(() => providers.CatalogRefreshCalls == 1 && providers.CatalogBrowseCalls >= 2
            && Descendants(fx.App.Overlay!).OfType<Button>().Any(view => view.Id == "openrouter-catalog-refresh" && view.Enabled),
            "explicit catalog refresh completes");
        ClickId(fx, "openrouter-catalog-register");
        fx.Wait(() => providers.RegisteredModels.Count > 0, "selected catalog model reaches Host registration");
        Assert.Equal(["openai/fixture-model"], providers.RegisteredModels);
        fx.Wait(() => Descendants(fx.App.Overlay!).Any(view => view.Id == "provider-api-key"),
            "catalog registration returns to the selected provider form");
    });

    [Fact]
    public void Provider_disconnect_requires_explicit_confirmation_and_uses_the_selected_id() => RunTuiTest(fx =>
    {
        var providers = new ProviderConnectionsFixture
        {
            Rows = [new("anthropic-team", ProviderConnectionMethod.ApiKey, ProviderConnectionState.Connected,
                BillingMode.MeteredCurrency, null, null, false, false, true, false, null, null)],
        };
        fx.StartTui(providerConnections: providers, initialColumns: 80, initialRows: 25);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "centro visible");
        ChoosePreferenceCategory(fx, "accounts_connections");
        ClickId(fx, "preferences-provider-open-anthropic-team");
        fx.Wait(() => Descendants(fx.App.Overlay!).Any(view => view.Id == "provider-disconnect"), "direct provider form visible");
        ClickId(fx, "provider-disconnect");
        fx.Wait(() => Descendants(fx.App.Overlay!).Any(view => view.Id == "provider-disconnect-confirm"), "confirmación visible");
        Assert.Equal(0, providers.OperationCalls);
        ClickId(fx, "provider-disconnect-cancel");
        fx.Wait(() => Descendants(fx.App.Overlay!).Any(view => view.Id == "provider-api-key"), "cancelación devuelve a la conexión");
        Assert.Equal(0, providers.OperationCalls);
        ClickId(fx, "provider-disconnect");
        ClickId(fx, "provider-disconnect-confirm");
        fx.Wait(() => providers.OperationCalls == 1
            && Descendants(fx.App.Overlay!).Any(view => view.Id == "provider-api-key"), "desconexión explícita completa");
        Assert.Equal("anthropic-team", providers.LastProviderId);
    });

    [Fact]
    public void Local_engine_page_is_read_only_until_confirmed_and_targets_selected_provider_for_start_and_stop() => RunTuiTest(fx =>
    {
        var host = new TuiLocalEngineHost(Path.Combine(fx.Root, "local-runtime-fixture"));
        var snapshot = host.Read(CancellationToken.None);
        host.Save(new("fixture-engine", "managed", "auto", "C:/fixture/server.exe", [], null, 15),
            snapshot.Revision, CancellationToken.None);
        var supervisor = new LocalEngineSupervisorFixture();
        fx.StartTui(localEngines: host, localEngineSupervisor: supervisor, initialColumns: 80, initialRows: 25);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "centro visible");
        ChoosePreferenceCategory(fx, "local_engines");
        Assert.Equal(0, supervisor.EnsureCalls);
        Assert.Equal(0, supervisor.StopCalls);
        Assert.True(supervisor.ReadCalls > 0);
        ClickId(fx, "local-engine-runtime");
        ClickId(fx, "local-engine-runtime-confirm");
        fx.Wait(() => OverlayText(fx.App.Overlay!).Contains("Active and managed", StringComparison.Ordinal)
            || OverlayText(fx.App.Overlay!).Contains("Activo y administrado", StringComparison.Ordinal),
            "acción confirmada inicia el provider elegido");
        Assert.Equal(1, supervisor.EnsureCalls);
        Assert.Equal("fixture-engine", supervisor.LastProviderId);
        Assert.Equal(0, supervisor.StopCalls);
        ClickId(fx, "local-engine-runtime-cancel");
        fx.Wait(() => Descendants(fx.App.Overlay!).Any(view => view.Id == "local-engine-runtime"),
            "volver a motores locales");
        ClickId(fx, "local-engine-runtime");
        ClickId(fx, "local-engine-runtime-confirm");
        fx.Wait(() => OverlayText(fx.App.Overlay!).Contains("Managed by this instance", StringComparison.Ordinal)
            || OverlayText(fx.App.Overlay!).Contains("Administrado por esta instancia", StringComparison.Ordinal),
            "detención confirmada del proceso propio");
        Assert.Equal(1, supervisor.StopCalls);
        Assert.Equal("fixture-engine", supervisor.LastProviderId);
    });

    [Fact]
    public void Dismissing_local_engine_runtime_result_discards_late_completion_from_overlay() => RunTuiTest(fx =>
    {
        var host = new TuiLocalEngineHost(Path.Combine(fx.Root, "local-runtime-late-fixture"));
        var snapshot = host.Read(CancellationToken.None);
        host.Save(new("fixture-engine", "managed", "auto", "C:/fixture/server.exe", [], null, 15),
            snapshot.Revision, CancellationToken.None);
        using var gate = new ManualResetEventSlim();
        var supervisor = new LocalEngineSupervisorFixture { EnsureGate = gate };
        fx.StartTui(localEngines: host, localEngineSupervisor: supervisor, initialColumns: 80, initialRows: 25);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "centro visible");
        ChoosePreferenceCategory(fx, "local_engines");
        ClickId(fx, "local-engine-runtime");
        ClickId(fx, "local-engine-runtime-confirm");
        fx.Wait(() => supervisor.EnsureCalls == 1, "operación confirmada iniciada fuera del hilo TUI");
        ClickId(fx, "local-engine-runtime-cancel");
        fx.Wait(() => Descendants(fx.App.Overlay!).Any(view => view.Id == "local-engine-runtime"),
            "overlay devuelto a la lista antes del resultado");
        gate.Set();
        fx.Wait(() => supervisor.ReadCalls > 0 && fx.App.Overlay is not null,
            "TUI estable tras completar operación tardía");
        fx.Wait(() => fx.App.LocalEngineRuntimeTask?.IsCompleted == true, "resultado host ya completó");
        Thread.Sleep(600); // Allow the UI poll to process the queued late completion.
        Assert.DoesNotContain(Descendants(fx.App.Overlay!).OfType<Label>(), view =>
            view.Id == "local-engine-runtime-result");
    });

    [Fact]
    public void Claude_login_cancel_ignores_late_host_completion_and_returns_to_account_list() => RunTuiTest(fx =>
    {
        var gate = new TaskCompletionSource<TuiClaudeLoginOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accounts = new ClaudeAccountsFixture
        {
            Rows = [new("claude-primary", true, false, false, false, null, null, null, null, null)],
            LoginGate = gate,
        };
        fx.StartTui(claudeAccounts: accounts, initialColumns: 120, initialRows: 35);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "abre preferencias");
        ChoosePreferenceCategory(fx, "accounts_connections");
        ClickId(fx, "preferences-claude-open");
        ClickId(fx, "claude-account-login");
        fx.Wait(() => fx.App.Overlay is not null && OverlayText(fx.App.Overlay).Contains("La autorización está activa"),
            "se muestra operación cancelable");
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is not null
            && Descendants(fx.App.Overlay).Any(view => view.Id == "claude-account-list"),
            "Esc cancela y vuelve a la lista Claude");
        gate.SetResult(new(true));
        Thread.Sleep(100);
        Assert.True(AccountsPreferencesResources.TryTranslate("es", "accounts.ui.title.claude", out var claudeTitle));
        Assert.Equal(claudeTitle, fx.App.Overlay!.Title?.ToString().Trim());
        Assert.DoesNotContain("Claude session connected", OverlayText(fx.App.Overlay));
        Assert.Equal(1, accounts.LoginCalls);
    });

    [Fact]
    public void Claude_login_shows_typed_failure_in_the_selected_locale() => RunTuiTest(fx =>
    {
        var accounts = new ClaudeAccountsFixture
        {
            Rows = [new("claude-primary", true, false, false, false, null, null, null, null, null)],
            Outcome = new(false, "claude.oauth.failed.timeout"),
        };
        fx.StartTui(claudeAccounts: accounts, initialColumns: 80, initialRows: 25);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "centro abierto");
        ChoosePreferenceCategory(fx, "accounts_connections");
        ClickId(fx, "preferences-claude-open");
        ClickId(fx, "claude-account-login");
        // Allow the host result to return to the TUI event queue.
        fx.Wait(() => fx.App.Overlay is not null && OverlayText(fx.App.Overlay).Contains("El inicio de sesión expiró"),
            "el error OAuth tipado se traduce y no se oculta tras un genérico");
        Assert.Equal(1, accounts.LoginCalls);
    });

    [Fact]
    public void Preferences_center_reflows_live_without_losing_search_category_locale_or_focus() => RunTuiTest(fx =>
    {
        var changed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        fx.StartTui(initialColumns: 80, initialRows: 25, localeChanged: changed.Enqueue);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "centro visible en ancho estrecho");
        var initial = fx.App.Overlay!;
        fx.Invoke(() =>
        {
            var search = Descendants(initial).OfType<TextField>().Single(view => view.Id == "preferences-search");
            search.Text = "cuenta";
        });
        fx.Wait(() => Descendants(fx.App.Overlay!).OfType<ListView>()
            .Single(view => view.Id == "preferences-categories").Source!.Count > 0, "búsqueda activa");
        var narrowCategories = Descendants(initial).OfType<ListView>().Single(view => view.Id == "preferences-categories");
        fx.Invoke(() => narrowCategories.SetFocus());
        fx.Wait(() => narrowCategories.HasFocus, "la lista de categorías conserva el foco antes del reflow");
        fx.Invoke(() => fx.Application.Driver!.SetScreenSize(120, 35));
        fx.Wait(() => fx.App.Overlay is not null && !ReferenceEquals(initial, fx.App.Overlay)
            && Descendants(fx.App.Overlay).Any(view => view.Id == "preferences-categories" && view.HasFocus),
            "reflow al ancho amplio conserva el foco de categorías");
        var wideSearch = Descendants(fx.App.Overlay!).OfType<TextField>().Single(view => view.Id == "preferences-search");
        Assert.Equal("cuenta", wideSearch.Text.ToString());
        var wideCategories = Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "preferences-categories");
        Assert.True(wideCategories.HasFocus, "el reflow conserva el foco exacto de la lista de categorías");
        ExportPreferenceFrame(fx, 120, 35, "center-wide", "cuenta");

        fx.Invoke(() => wideSearch.Text = "");
        ChoosePreferenceCategory(fx, "general");
        var locales = Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "preferences-locale");
        fx.Invoke(() => locales.SelectedItem = 2);
        fx.Wait(() => fx.App.PreferencesLocale == "fr", "vista previa del idioma francés");
        var previewFrame = fx.App.Overlay!;
        fx.Invoke(() => fx.Application.Driver!.SetScreenSize(80, 25));
        fx.Wait(() => fx.App.Overlay is not null && !ReferenceEquals(previewFrame, fx.App.Overlay)
            && Descendants(fx.App.Overlay).Any(view => view.Id == "preferences-locale" && view.HasFocus),
            "reflow al ancho estrecho conserva el foco de idioma");
        Assert.Equal("fr", fx.App.PreferencesDraftLocale);
        Assert.Equal("fr", fx.App.PreferencesLocale);
        Assert.True(Descendants(fx.App.Overlay!).OfType<ListView>().Single(view => view.Id == "preferences-locale").HasFocus);
        ExportPreferenceFrame(fx, 80, 25, "general-fr", "Français");
        Assert.Contains("fr", changed);
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay is not null
            && fx.App.Overlay.Title?.Contains("Preferencias") == true, "Esc cancela la vista previa y vuelve a categorías");
        Assert.Equal("es", fx.App.PreferencesLocale);
        Assert.Contains("es", changed);
    });

    private static void AssertClaudePageFits(View overlay)
    {
        var frame = (FrameView)overlay;
        var controls = Descendants(overlay).Where(view => view.Id is "claude-account-list" or "claude-account-detail"
            or "claude-account-status" or "claude-account-login" or "claude-account-logout" or "claude-account-back").ToArray();
        foreach (var view in controls)
        {
            Assert.True(view.Frame.X >= 0 && view.Frame.Y >= 0 && view.Frame.Right <= frame.Viewport.Width
                && view.Frame.Bottom <= frame.Viewport.Height, view.Id + " fits in the Claude account page");
        }
        for (var left = 0; left < controls.Length; left++)
        for (var right = left + 1; right < controls.Length; right++)
            Assert.False(controls[left].Frame.IntersectsWith(controls[right].Frame),
                controls[left].Id + " overlaps " + controls[right].Id);
    }
}
