using OmniCore.Protocol;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(120, 40)]
    [InlineData(80, 25)]
    public void Companion_bubble_presents_notices_in_the_chat_without_tabs_provider_calls_or_main_focus_changes(int columns, int rows) => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1); var host = new CompanionHubTurn(fx);
        fx.StartTui(turnHost: host, initialColumns: columns, initialRows: rows);
        if (!fx.Read(() => fx.App.SidebarOpen)) fx.InjectKey(new Key(KeyCode.F2));
        Type(fx, "/pet home"); fx.InjectKey(new Key(KeyCode.Enter));
        if (rows >= 36) EmulatePetSixelDevice(fx);
        fx.Invoke(() => { fx.App.Composer!.Text = "borrador principal"; fx.App.Composer.SetFocus(); });
        var peer = new CompanionInstance("another", Path.Combine(fx.Root, "Alpha"), "another-session", null, "Disponible", DateTimeOffset.UtcNow, []);
        host.CompanionHub.Heartbeat(peer); host.CompanionHub.Notify(new("turn/another", peer.Id, peer.Session, "Terminó sus cambios", DateTimeOffset.UtcNow));
        fx.Wait(() => Descendants(fx.App.MainWindow!).Any(v => v.Id == "companion-notice-bubble" && v.Visible && v.Text == "(1)"), "burbuja de aviso");
        Assert.True(fx.Read(() => fx.App.Composer!.HasFocus)); Assert.Equal("borrador principal", fx.Read(() => fx.App.Composer!.Text));
        ExportPreferenceFrame(fx, columns, rows, "companion-bubble", "(1)");
        if (rows >= 36) AddDensityRasterToCapture(fx, columns, rows, "companion-bubble");
        HubClick(fx, "companion-notice-bubble");
        fx.Wait(() => HubView(fx, "companion-chat-history").Text.Contains("Terminó sus cambios"), "aviso conversacional");
        fx.Wait(() => host.CompanionHub.PendingNotices().Length == 0, "aviso mostrado y reconocido");
        Assert.DoesNotContain(fx.Read(() => Descendants(fx.App.MainWindow!).Select(v => v.Id).ToArray()), id => id is "companion-sessions-tab" or "companion-notices-tab" or "companion-memory-tab");
        Assert.Equal(0, host.Chats); Assert.Null(host.Input);
        ExportPreferenceFrame(fx, columns, rows, "companion-conversational-notice", "Terminó");
        fx.Invoke(() => HubView(fx, "companion-chat-input").Text = "borrador de mascota");
        HubClick(fx, "companion-model-open"); fx.Wait(() => fx.App.Overlay?.Id == "companion-settings", "preferencias únicas");
        HubClick(fx, "companion-settings-close");
        HubClick(fx, "companion-hub-open");
        fx.Wait(() => HubView(fx, "companion-chat-input").Text == "borrador de mascota", "borrador conservado al abrir preferencias");
        HubClick(fx, "companion-hub-close"); fx.Wait(() => fx.App.Composer!.HasFocus, "foco restaurado");
        Assert.Equal("borrador principal", fx.Read(() => fx.App.Composer!.Text));
    }, noColor: false);

    [Theory]
    [InlineData(120, 40)]
    [InlineData(80, 25)]
    public void Companion_maintenance_stays_in_one_window_for_models_notifications_categories_and_followups(int columns, int rows) => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1); var host = new CompanionHubTurn(fx);
        host.CompanionHub.RememberCategorized("Mi nombre es Juan", "personal");
        var followup = host.CompanionHub.RememberCategorized("Esperar validación", "followup", "Proyecto 106");
        fx.StartTui(turnHost: host, initialColumns: columns, initialRows: rows);
        Type(fx, "/pet model"); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay?.Id == "companion-settings" && ((ListView)HubView(fx, "companion-settings-model")).Source?.ToList().Count == 2, "mantenimiento preparado");
        var original = fx.Read(() => fx.App.Overlay);
        HubClick(fx, "companion-settings-local");
        fx.Wait(() => HubView(fx, "companion-settings-context").Text.Contains("32"), "contexto local visible");
        HubClick(fx, "companion-settings-notices"); fx.Wait(() => host.CompanionHub.ReadQuiet(), "avisos configurados");
        HubClick(fx, "companion-settings-attention"); fx.Wait(() => !host.CompanionHub.AttentionEnabled(), "atención configurada");
        HubClick(fx, "companion-settings-category"); HubClick(fx, "companion-settings-category"); HubClick(fx, "companion-settings-category");
        fx.Wait(() => HubView(fx, "companion-settings-memory-text").Text.Contains("Esperar validación"), "seguimiento visible");
        HubClick(fx, "companion-settings-resolve");
        fx.Wait(() => host.CompanionHub.MaintainMemory("followup").Single(f => f.Id == followup.Id).Status == "resolved", "seguimiento resuelto");
        Assert.Same(original, fx.Read(() => fx.App.Overlay)); Assert.Equal(0, host.Chats); Assert.Null(host.Input);
        ExportPreferenceFrame(fx, columns, rows, "companion-one-preferences", "Memoria: Seguimientos");
        HubClick(fx, "companion-settings-close"); fx.Wait(() => fx.App.Composer!.HasFocus, "cerrado sin ventanas intermedias");
    }, noColor: false);
}
