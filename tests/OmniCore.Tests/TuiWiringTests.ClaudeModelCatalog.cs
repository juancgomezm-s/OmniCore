using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Client;
using System.Net;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    private sealed class ClaudeCatalogFixture(TuiClaudeAccountHost catalog) : ITuiClaudeAccountHost, ITuiClaudeModelCatalogHost
    {
        public int LoginCalls;
        public int DiscoveryCalls;
        public TaskCompletionSource<TuiClaudeModelCatalogOutcome>? Gate;
        public bool RejectCatalog;
        public IReadOnlyList<TuiClaudeAccountRow> List(CancellationToken token) => catalog.List(token);
        public Task<TuiClaudeLoginOutcome> LoginAsync(string providerId, Action<TuiClaudeLoginProgress> progress, CancellationToken token)
        { Interlocked.Increment(ref LoginCalls); return Task.FromResult(new TuiClaudeLoginOutcome(true)); }
        public void Logout(string providerId, CancellationToken token) => catalog.Logout(providerId, token);
        public Task<TuiClaudeModelCatalogOutcome> DiscoverModelsAsync(string providerId, CancellationToken token)
        {
            Interlocked.Increment(ref DiscoveryCalls);
            return Gate?.Task ?? (RejectCatalog ? Task.FromResult(new TuiClaudeModelCatalogOutcome(false, ErrorKey: "invalid"))
                : catalog.DiscoverModelsAsync(providerId, token));
        }
    }

    [Theory]
    [InlineData(false, 80, 25, "es")]
    [InlineData(true, 120, 40, "en")]
    [InlineData(false, 80, 25, "fr")]
    [InlineData(false, 120, 40, "it")]
    public void Claude_catalog_reaches_F3_and_visibility_in_the_same_TUI(bool signIn, int width, int height, string locale) => RunTuiTest(fx =>
    {
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "claude-models"));
        var host = CreateClaudeCatalogFixture(paths);
        using var policies = ModelPolicyHost.Create(paths.DataDirectory);
        policies.SetVisibleInPicker([policies.Models.Single(model => model.Id == "local-keep")], false, default);
        fx.StartTui(claudeAccounts: host, policies: policies, initialColumns: width, initialRows: height,
            localeSelection: new LocaleSelection(locale, LocaleSelectionSource.CommandLine));
        fx.Invoke(() => fx.App.Composer!.Text = "conservar borrador");
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferencias");
        ChoosePreferenceCategory(fx, "accounts_connections"); ClickId(fx, "preferences-claude-open");
        Assert.Equal(0, host.DiscoveryCalls);
        AssertClaudePageFits(fx.App.Overlay!);
        if (signIn) ClickId(fx, "claude-account-login");
        else
        {
            EnterOnce(fx, () => EnterControl(fx, "claude-account-discover").HasFocus, "Enter lleva a Actualizar modelos");
            EnterOnce(fx, () => host.DiscoveryCalls == 1, "Enter descubre para cuenta ya conectada");
        }
        var notice = string.Format(ClaudeModelCatalogResources.Get(locale, "ready"), 1);
        fx.Wait(() => fx.App.Overlay?.Id == "claude-account-preferences"
            && OverlayText(fx.App.Overlay).Contains(notice), "catálogo registrado y recargado");
        Assert.Equal(signIn ? 1 : 0, host.LoginCalls);
        Assert.Equal(1, host.DiscoveryCalls);
        Assert.Contains(policies.Models, model => model.Id == "claude-fixture-visible" && model.ProviderId == "anthropic");
        Assert.Contains(policies.Models, model => model.Id == "chatgpt-keep" && model.ProviderId == "chatgpt");
        Assert.False(policies.IsVisibleInPicker("local", "local-keep"));
        ExportPreferenceFrame(fx, width, height, "claude-catalog-ready-" + locale, ClaudeModelCatalogResources.Get(locale, "refresh"));
        for (var i = 0; i < 4 && fx.App.Overlay is not null; i++) fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => fx.App.Overlay is null, "cerrar preferencias");
        KeyWithEffect(fx, KeyCode.F3, () => fx.App.Overlay?.Id == "omni-model-palette", "F3");
        Assert.Contains(PickerItems(fx), row => row.Contains("claude-fixture-visible"));
        Assert.DoesNotContain(PickerItems(fx), row => row.Contains("local-keep"));
        KeyWithEffect(fx, KeyCode.M | KeyCode.CtrlMask, () => fx.App.Overlay?.Id == "model-toggles", "mantenimiento de visibilidad");
        Assert.Contains("claude-fixture-visible", OverlayText(fx.App.Overlay!));
        Assert.Equal("conservar borrador", fx.App.Composer!.Text.ToString());
    });

    [Fact]
    public void Claude_catalog_cancel_ignores_late_results_and_a_failed_catalog_does_not_report_failed_login() => RunTuiTest(fx =>
    {
        var host = CreateClaudeCatalogFixture(new DefaultPlatformPaths(Path.Combine(fx.Root, "claude-cancel")));
        host.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.StartTui(claudeAccounts: host);
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferencias");
        ChoosePreferenceCategory(fx, "accounts_connections"); ClickId(fx, "preferences-claude-open");
        ClickId(fx, "claude-account-discover");
        fx.Wait(() => host.DiscoveryCalls == 1, "descubrimiento activo");
        KeyWithEffect(fx, KeyCode.Esc, () => fx.App.Overlay?.Id == "claude-account-preferences", "cancelar");
        host.Gate.SetResult(new(true, 999));
        fx.App.ClaudeLoginTask!.GetAwaiter().GetResult();
        Assert.DoesNotContain("999", OverlayText(fx.App.Overlay!));
        host.Gate = null; host.RejectCatalog = true;
        ClickId(fx, "claude-account-login");
        fx.Wait(() => fx.App.Overlay?.Id == "claude-account-preferences"
            && OverlayText(fx.App.Overlay).Contains("401/403"), "fallo de catálogo explícito");
        Assert.Contains("Sesión Claude conectada.", OverlayText(fx.App.Overlay!));
        Assert.True(EnterControl(fx, "claude-account-discover").Enabled);
    });

    private static ClaudeCatalogFixture CreateClaudeCatalogFixture(DefaultPlatformPaths paths)
    {
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), """
            providers:
              local: { family: OpenAiChatCompatible, baseUrl: 'http://127.0.0.1:8080', auth: none }
              chatgpt: { family: OpenAIResponses, baseUrl: 'https://chatgpt.com/backend-api', profile: codex, auth: none }
              anthropic:
                family: AnthropicMessages
                baseUrl: https://api.anthropic.com
                oauth:
                  clientId: fixture-client
                  userAgent: 'fixture/{version}'
                  scopes: [user:profile, user:inference]
                  authorizeUrl: https://claude.com/cai/oauth/authorize
                  tokenUrl: https://platform.claude.com/v1/oauth/token
            """);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "models.yaml"), "models:\n  local-keep: { provider: local, context: 8192 }\n  chatgpt-keep: { provider: chatgpt, context: 16384 }\n");
        var session = OmniHost.CreateClaudeOAuthSession(paths, "anthropic", httpFactory: () => new HttpClient(new ClaudeCatalogHandler()))!;
        session.Credentials.Save(session.SecretRef, new ClaudeOAuthCredential("fixture-access", "fixture-refresh",
            DateTimeOffset.UtcNow.AddHours(1), ["user:profile", "user:inference"], session.Identity.ClientId), default);
        return new(new TuiClaudeAccountHost(paths, _ => session));
    }

    private sealed class ClaudeCatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"data":[{"id":"claude-fixture-visible","display_name":"Fixture Claude"}]}""") });
    }
}
