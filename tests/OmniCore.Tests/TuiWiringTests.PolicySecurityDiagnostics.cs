namespace OmniCore.Tests;

using OmniCore.Client;
using OmniCore.Host;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData("es", "Modelos y ejecución")]
    [InlineData("en", "Models and execution")]
    [InlineData("fr", "Modèles et exécution")]
    [InlineData("it", "Modelli ed esecuzione")]
    public void Policy_preferences_render_the_selected_locale_and_export_a_real_frame(string locale, string title) => RunTuiTest(fx =>
    {
        var host = PolicyPreferencesHost.Create(fx.Workspace,
            Path.Combine(fx.Root, "policy-locale-data"), Path.Combine(fx.Root, "policy-locale-config"));
        fx.StartTui(initialColumns: 80, initialRows: 25,
            localeSelection: new LocaleSelection(locale, LocaleSelectionSource.CommandLine), policyPreferences: host);
        fx.Invoke(() => fx.App.ShowPolicyPreferences("models_execution"));
        fx.Wait(() => fx.App.Overlay?.Id == "policy-preferences", "policy locale page appears");
        Assert.Contains(title, OverlayText(fx.App.Overlay!));
        ExportPreferenceFrame(fx, 80, 25, "policy-" + locale, title);
    });

    [Theory]
    [InlineData(80, 25, "policy-narrow")]
    [InlineData(120, 35, "policy-wide")]
    public void Policy_form_keeps_all_fields_in_scrollable_content_and_draft_survives_independent_saves(
        int columns, int rows, string scene) => RunTuiTest(fx =>
    {
        var host = PolicyPreferencesHost.Create(fx.Workspace,
            Path.Combine(fx.Root, "policy-test-data"), Path.Combine(fx.Root, "policy-test-config"));
        fx.StartTui(initialColumns: columns, initialRows: rows, policyPreferences: host);
        fx.Invoke(() => fx.App.ShowPolicyPreferences("models_execution"));
        fx.Wait(() => fx.App.Overlay?.Id == "policy-preferences", "formulario de modelos visible");
        var frame = fx.App.Overlay!;
        var panel = frame.SubViews.Single(view => view.Id == "policy-preferences-form");
        var fields = Descendants(panel).OfType<TextField>().ToDictionary(field => field.Id!.ToString(), StringComparer.Ordinal);
        Assert.Contains("policy-default-model", fields.Keys);
        Assert.Contains("policy-escalation-chain", fields.Keys);
        Assert.True(panel.GetContentSize().Height >= panel.SubViews.Max(view => view.Frame.Bottom),
            "el formulario declara el contenido completo, no sólo el área visible");
        if (columns == 80) Assert.True(panel.GetContentSize().Height > panel.Viewport.Height);
        var footer = frame.SubViews.Where(view => view.Id is "policy-preferences-save" or "policy-preferences-save-routes" or "policy-preferences-back");
        Assert.All(footer, view => Assert.True(view.Frame.Bottom <= frame.Viewport.Height, $"footer fuera de pantalla: {view.Id} {view.Frame}"));
        ExportPreferenceFrame(fx, columns, rows, scene, "Modelo efectivo");
        fx.Invoke(() => fields["policy-escalation-chain"].SetFocus());
        fx.Wait(() => panel.Viewport.Bottom >= fields["policy-escalation-chain"].Frame.Bottom,
            "el foco desplaza el formulario hasta la última ruta");
        Assert.True(fields["policy-escalation-chain"].HasFocus);

        fx.Invoke(() =>
        {
            fields["policy-session-cap"].Text = "5";
            fields["policy-route-prefer-local"].Text = "true";
        });
        ClickId(fx, "policy-preferences-save");
        Assert.Contains("session: 5", File.ReadAllText(Path.Combine(fx.Root, "policy-test-config", "settings.yaml")), StringComparison.Ordinal);
        Assert.Equal("true", fields["policy-route-prefer-local"].Text.ToString());
        ClickId(fx, "policy-preferences-save-routes");
        Assert.True(host.Read().Routing.PreferLocal);
        fx.Invoke(() => fields["policy-route-meta"].Text = "unsaved-draft" );
        ClickId(fx, "policy-preferences-back");
        Assert.Empty(host.Read().Routing.Meta);
        fx.Wait(() => fx.App.Overlay?.SubViews.Any(view => view.Id == "preferences-panel") == true,
            "regreso a categoría de preferencias");
    });

    [Theory]
    [InlineData(80, 25, "security-narrow")]
    [InlineData(120, 35, "security-wide")]
    public void Security_actions_require_confirmation_and_bind_to_the_inspected_workspace_state(
        int columns, int rows, string scene) => RunTuiTest(fx =>
    {
        var host = SecurityPreferencesHost.Create(fx.Workspace,
            Path.Combine(fx.Root, "security-test-data"), Path.Combine(fx.Root, "security-test-config"));
        fx.StartTui(initialColumns: columns, initialRows: rows, securityPreferences: host);
        fx.Invoke(() => fx.App.ShowSecurityPreferences("tools_security"));
        fx.Wait(() => fx.App.Overlay?.Id == "security-preferences", "seguridad visible");
        var form = fx.App.Overlay!.SubViews.Single(view => view.Id == "security-preferences-form");
        Assert.True(form.VerticalScrollBar.ScrollableContentSize > 0, "controles completos deben quedar en viewport desplazable");
        ExportPreferenceFrame(fx, columns, rows, scene, "Nivel de autonomía en ACT");

        var beforeDraft = host.Read();
        var profile = Descendants(form).OfType<TextField>().Single(field => field.Id!.ToString() == "security-profile");
        fx.Invoke(() => profile.Text = beforeDraft.Profile == "Balanced" ? "Conservative" : "Balanced");
        ClickId(fx, "security-save");
        fx.Wait(() => fx.App.Overlay?.Id == "security-preferences-confirm", "confirmación de borrador visible");
        Click(fx, "Cancelar");
        var afterCancel = host.Read();
        Assert.Equal(beforeDraft.Profile, afterCancel.Profile);
        Assert.Equal(beforeDraft.Revision, afterCancel.Revision);
        Assert.Same(form, fx.App.Overlay!.SubViews.Single(view => view.Id == "security-preferences-form"));
        Assert.Equal(beforeDraft.Profile == "Balanced" ? "Conservative" : "Balanced", profile.Text.ToString());

        ClickId(fx, "security-trust");
        fx.Wait(() => fx.App.Overlay?.Id == "security-preferences-confirm", "confirmación de confianza visible");
        Click(fx, "Cancelar");
        Assert.False(host.Read().WorkspaceTrusted);
        ClickId(fx, "security-trust");
        Click(fx, "Confirmar");
        Assert.True(host.Read().WorkspaceTrusted);
        ClickId(fx, "security-back");
    });

    [Theory]
    [InlineData(80, 25, "diagnostics-narrow")]
    [InlineData(120, 35, "diagnostics-wide")]
    public void Diagnostics_gc_is_previewed_and_cancelled_without_creating_workspace_data(
        int columns, int rows, string scene) => RunTuiTest(fx =>
    {
        var host = DiagnosticsPreferencesHost.Create(fx.Workspace,
            Path.Combine(fx.Root, "diagnostic-test-data"), Path.Combine(fx.Root, "diagnostic-test-config"));
        var workspaceId = OmniCore.Domain.WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(fx.Workspace)).ToString();
        var workspaceData = Path.Combine(fx.Root, "diagnostic-test-data", "workspaces", workspaceId);
        fx.StartTui(initialColumns: columns, initialRows: rows, diagnosticsPreferences: host);
        fx.Invoke(() => fx.App.ShowDiagnosticsPreferences("diagnostics_data"));
        fx.Wait(() => fx.App.Overlay?.Id == "diagnostics-preferences", "diagnóstico visible");
        ExportPreferenceFrame(fx, columns, rows, scene, "origen: predeterminado");
        ClickId(fx, "diagnostics-gc-preview");
        fx.Wait(() => fx.App.Overlay?.Id == "diagnostics-confirm", "confirmación de mantenimiento visible");
        Assert.Contains("sólo datos de esta área de trabajo", OverlayText(fx.App.Overlay!), StringComparison.Ordinal);
        Click(fx, "Cancelar");
        Assert.False(Directory.Exists(workspaceData));
        ClickId(fx, "diagnostics-gc-preview");
        Click(fx, "Eliminar blobs identificados");
        fx.Wait(() => fx.App.Overlay is { Title: not null }, "resultado confirmado de mantenimiento visible");
        Assert.False(Directory.Exists(workspaceData));
        Click(fx, "Aceptar");
    });

    [Fact]
    public void Security_level_buttons_select_the_permission_profile_without_typing() => RunTuiTest(fx =>
    {
        var host = SecurityPreferencesHost.Create(fx.Workspace,
            Path.Combine(fx.Root, "security-level-data"), Path.Combine(fx.Root, "security-level-config"));
        fx.StartTui(initialColumns: 120, initialRows: 35, securityPreferences: host);
        fx.Invoke(() => fx.App.ShowSecurityPreferences("tools_security"));
        fx.Wait(() => fx.App.Overlay?.Id == "security-preferences", "seguridad visible");
        var form = fx.App.Overlay!.SubViews.Single(view => view.Id == "security-preferences-form");
        var profile = Descendants(form).OfType<TextField>().Single(field => field.Id == "security-profile");
        var restrict = Descendants(form).OfType<Button>().Single(button => button.Id == "security-level-conservative");
        ClickId(fx, "security-level-conservative");
        Assert.Equal("Conservative", PreferenceInput.CanonicalValue("security.profile", profile.Text.ToString(), "es",
            "Conservative", "Balanced", "Autonomous"));
        Assert.StartsWith("\u25CF", restrict.Text.ToString());
        Assert.Contains(Descendants(form).OfType<Label>(), label => label.Id == "omni-detail" && label.Text.ToString().Length > 20);
        ClickId(fx, "security-save");
        fx.Wait(() => fx.App.Overlay?.Id == "security-preferences-confirm", "confirmación visible");
        Click(fx, "Confirmar");
        Assert.Equal("Conservative", host.Read().Profile);
    });
}
