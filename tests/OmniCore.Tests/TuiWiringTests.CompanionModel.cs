using OmniCore.Protocol;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Fact]
    public void Companion_model_settings_offer_paid_model_optional_local_only_and_own_counter() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1); var host = new CompanionHubTurn(fx);
        var id = Guid.NewGuid().ToString(); host.CompanionHub.BeginChatUsage(id, "cloud-chat", "paid");
        host.CompanionHub.FinishChatUsage(new(id, "cloud-chat", "paid", Input: 120, Output: 40, EstimatedUsd: .02m));
        fx.StartTui(turnHost: host, initialColumns: 120, initialRows: 40);
        var session = fx.Server.LastSessionId();
        Type(fx, "/pet model"); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay?.Id == "companion-model-settings" && HubView(fx, "companion-model-save").Enabled, "configuración independiente preparada");
        Assert.Contains("120 entrada / 40 salida", fx.Read(() => HubView(fx, "companion-model-usage").Text));
        HubClick(fx, "companion-model-save");
        fx.Wait(() => host.CompanionHub.ChatSettings().ModelId == "cloud-chat", "modelo por consumo guardado sin autorización del principal");
        fx.Wait(() => HubView(fx, "companion-model-local").Enabled, "guardado finalizado");
        HubClick(fx, "companion-model-local");
        fx.Wait(() => ((ListView)HubView(fx, "companion-model-list")).Source!.ToList().Count == 1, "solo modelos locales");
        HubClick(fx, "companion-model-save");
        fx.Wait(() => host.CompanionHub.ChatSettings() == new CompanionChatSettings("local-chat", true), "opción local persistente");
        fx.Wait(() => HubView(fx, "companion-model-status").Text.Contains("local-chat"), "confirmación visible");
        Assert.Equal(session, fx.Server.LastSessionId()); Assert.Equal(0, host.Chats); Assert.Null(host.Input);
        ExportPreferenceFrame(fx, 120, 40, "companion-model-config", "Sin cuotas");
    }, noColor: false);
}
