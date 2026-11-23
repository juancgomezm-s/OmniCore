using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Client;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    private sealed class ConfigurableClaudeAccountFixture(TuiClaudeAccountHost configuration) : ITuiClaudeAccountHost, ITuiClaudeAccountConfigurationHost
    {
        public int LoginCalls;
        public string? LastProvider;
        public TaskCompletionSource<TuiClaudeLoginOutcome> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<TuiClaudeAccountRow> List(CancellationToken token) => configuration.List(token)
            .Select(row => row with { Configured = true, UnavailableReasonKey = null }).ToArray();
        public async Task<TuiClaudeLoginOutcome> LoginAsync(string providerId, Action<TuiClaudeLoginProgress> progress, CancellationToken token)
        {
            LastProvider = providerId; Interlocked.Increment(ref LoginCalls); progress(new("AwaitingBrowser"));
            return await Completion.Task.WaitAsync(token);
        }
        public void Logout(string providerId, CancellationToken token) { }
        public ClaudeAccountConfigurationSnapshot ReadConfiguration(string? providerId, CancellationToken token) => configuration.ReadConfiguration(providerId, token);
        public void SaveConfiguration(string revision, string? providerId, ClaudeAccountConfigurationDraft draft, CancellationToken token) =>
            configuration.SaveConfiguration(revision, providerId, draft, token);
    }

    [Theory]
    [InlineData(80, 25, "es")]
    [InlineData(110, 35, "en")]
    [InlineData(80, 25, "fr")]
    [InlineData(110, 35, "it")]
    public void Claude_empty_account_can_configure_and_start_login_without_manual_YAML(int columns, int rows, string locale) => RunTuiTest(fx =>
    {
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "claude-setup"));
        var host = new ConfigurableClaudeAccountFixture(new TuiClaudeAccountHost(paths, _ => null));
        fx.StartTui(claudeAccounts: host, initialColumns: columns, initialRows: rows,
            localeSelection: new LocaleSelection(locale, LocaleSelectionSource.CommandLine));
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "abre preferencias");
        ChoosePreferenceCategory(fx, "accounts_connections"); ClickId(fx, "preferences-claude-open");
        AssertClaudePageFits(fx.App.Overlay!);
        Assert.True(EnterControl(fx, "claude-account-login").HasFocus);
        Assert.True(EnterControl(fx, "claude-account-login").Enabled);
        var actions = new[] { "claude-account-configure", "claude-account-login", "claude-account-logout", "claude-account-back" };
        Assert.Single(actions.Select(id => fx.Read(() => EnterControl(fx, id).Frame.X)).Distinct());
        ExportPreferenceFrame(fx, columns, rows, "claude-empty-" + locale, ClaudeAccountSetupResources.Get(locale, "configure"));
        EnterOnce(fx, () => fx.App.Overlay?.Id == "claude-setup-preferences", "Enter abre alta desde instalación vacía");
        Assert.Equal(0, host.LoginCalls);
        Assert.False(File.Exists(Path.Combine(paths.ConfigDirectory, "providers.yaml")));
        Assert.True(EnterControl(fx, "claude-setup-provider").HasFocus);
        EnterOnce(fx, () => EnterControl(fx, "claude-setup-advanced").HasFocus, "Enter avanza a opciones");
        EnterOnce(fx, () => EnterControl(fx, "claude-setup-save").HasFocus, "Enter enfoca guardar sin abrir sesión ni escribir");
        Assert.Equal(0, host.LoginCalls);
        EnterOnce(fx, () => host.LoginCalls == 1, "Guardar conecta configuración y login exactamente una vez");
        Assert.Equal("anthropic", host.LastProvider);
        Assert.NotNull(OmniHost.CreateClaudeOAuthSession(paths, "anthropic"));
        host.Completion.SetResult(new(true));
        fx.Wait(() => fx.App.Overlay?.Id == "claude-account-preferences" && OverlayText(fx.App.Overlay).Contains("anthropic", StringComparison.Ordinal), "resultado vuelve a cuenta");
        Assert.Contains("anthropic", OverlayText(fx.App.Overlay!));
    });

    [Fact]
    public void Claude_setup_advanced_fields_navigate_with_Enter_and_Esc_discards_without_authentication() => RunTuiTest(fx =>
    {
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "claude-setup-cancel"));
        var host = new ConfigurableClaudeAccountFixture(new TuiClaudeAccountHost(paths, _ => null));
        fx.StartTui(claudeAccounts: host, initialColumns: 80, initialRows: 25);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferencias");
        ChoosePreferenceCategory(fx, "accounts_connections"); ClickId(fx, "preferences-claude-open");
        ClickId(fx, "claude-account-configure");
        fx.Invoke(() => EnterControl(fx, "claude-setup-advanced").SetFocus());
        fx.InjectKey(new Key(KeyCode.Space));
        fx.Wait(() => EnterControl(fx, "claude-setup-client").Visible, "Espacio despliega campos de conexión");
        EnterOnce(fx, () => EnterControl(fx, "claude-setup-base").HasFocus, "Enter lleva al primer campo avanzado");
        foreach (var id in new[] { "client", "agent", "scopes", "authorize", "token", "profile", "roles" })
            EnterOnce(fx, () => EnterControl(fx, "claude-setup-" + id).HasFocus, "Enter avanza hasta " + id);
        EnterOnce(fx, () => EnterControl(fx, "claude-setup-save").HasFocus, "Enter al final enfoca Guardar");
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay?.Id == "claude-account-preferences", "Esc regresa a cuenta");
        Assert.Equal(0, host.LoginCalls);
        Assert.False(File.Exists(Path.Combine(paths.ConfigDirectory, "providers.yaml")));
    });
}
