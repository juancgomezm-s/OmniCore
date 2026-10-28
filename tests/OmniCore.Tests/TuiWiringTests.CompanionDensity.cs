using Terminal.Gui.Input;
using Terminal.Gui.Drivers;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(120, 40)]
    [InlineData(80, 25)]
    public void Companion_dense_panel_keeps_controls_visible_and_preserves_message_line_breaks(int columns, int rows) => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1); var host = new CompanionHubTurn(fx);
        fx.StartTui(turnHost: host, initialColumns: columns, initialRows: rows);
        var main = fx.Read(() => fx.App.Composer!.Frame);
        Type(fx, "/pet chat"); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => HubView(fx, "companion-hub-action").Enabled, "mini chat preparado");
        fx.Invoke(() => { fx.App.Composer!.Text = "borrador conservado"; HubView(fx, "companion-chat-input").Text = "primera\n\nsegunda"; });
        HubClick(fx, "companion-hub-action");
        fx.Wait(() => HubView(fx, "companion-chat-history").Text.Contains("Hola, Juan"), "respuesta recibida");
        var text = fx.Read(() => HubView(fx, "companion-chat-history").Text.ToString()).Replace("\r\n", "\n");
        Assert.Contains("primera\n\nsegunda\nOmni:", text);
        Assert.Equal(main, fx.Read(() => fx.App.Composer!.Frame));
        Assert.Equal("borrador conservado", fx.Read(() => fx.App.Composer!.Text));
        ExportPreferenceFrame(fx, columns, rows, "companion-dense-controls", "Enviar");
        HubClick(fx, "companion-hub-close");
        fx.Wait(() => fx.App.Composer!.HasFocus, "foco al chat principal");
    }, noColor: false);
}
