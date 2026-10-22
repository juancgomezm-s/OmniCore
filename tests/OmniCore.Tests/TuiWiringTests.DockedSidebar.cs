using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(64, 30, false)]
    [InlineData(80, 25, false)]
    [InlineData(80, 40, false)]
    [InlineData(89, 35, false)]
    [InlineData(120, 40, false)]
    [InlineData(240, 40, false)]
    [InlineData(120, 40, true)]
    public void Persisted_visible_sidebar_is_docked_from_first_frame_and_never_covers_chat_or_composer(
        int width, int height, bool explicitOverlay) => RunTuiTest(fx =>
    {
        void Configure(string key, string value)
        {
            var settings = SidebarPreferencesJson.Decode(fx.Server.Query("sidebarSettings", TestContext.Current.CancellationToken)!.Json)!;
            var ack = ((ITrustedUserActionClient)fx.Server).SendUserAction(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"sidebar.configure\",\"scope\":\"Workspace\"," + JsonObj.Field("key", key) + ","
                + JsonObj.Field("value", value) + "," + JsonObj.Field("revision", settings.Revision) + "}"),
                TestContext.Current.CancellationToken);
            Assert.Equal("ok", ack.Status);
        }
        Configure("sidebar.visible", "true");
        if (explicitOverlay) Configure("sidebar.mode", "overlay");
        SeedRecoveryHistory(fx);
        fx.StartTui(initialColumns: width, initialRows: height);
        fx.Wait(() => fx.App.Sidebar!.Visible && fx.App.Sidebar.Frame.Right == width, "panel guardado en el extremo derecho");
        void CheckBounds()
        {
            var sidebar = fx.App.Sidebar!.Frame;
            Assert.True(sidebar.Width <= Math.Min(width / 3, 40));
            Assert.Equal(width, sidebar.Right);
            Assert.True(fx.App.Conversation!.SuperView!.Frame.Right <= sidebar.X);
            Assert.True(fx.App.Composer!.SuperView!.Frame.Right <= sidebar.X);
            Assert.True(fx.App.Header!.Frame.Right <= sidebar.X);
            Assert.True(fx.App.Status!.Frame.Right <= sidebar.X);
            Assert.True(fx.App.Composer.HasFocus);
        }
        fx.Read(CheckBounds);
        Type(fx, "Este borrador largo debe ajustarse únicamente a la columna del mensaje, sin quedar debajo del panel.");
        fx.Read(CheckBounds);
        fx.Wait(() => fx.Read(() => fx.App.Conversation!.Viewport.Bottom >= fx.App.Conversation.Lines),
            "el último mensaje sigue visible al crecer el composer");
        ExportPreferenceFrame(fx, width, height, explicitOverlay ? "docked-explicit-overlay" : "docked-sidebar", "ULTIMO_MENSAJE");
        fx.InjectKey(new Key(KeyCode.F2));
        fx.Wait(() => !fx.App.Sidebar!.Visible && fx.App.Conversation!.SuperView!.Frame.Width == width, "cerrar recupera el ancho");
        fx.InjectKey(new Key(KeyCode.F2));
        fx.Wait(() => fx.App.Sidebar!.Visible && fx.App.Sidebar.Frame.Right == width, "reabrir sigue a la derecha");
        fx.Read(CheckBounds);
        Assert.True(fx.Loop.IsAlive);
        Assert.Null(fx.LoopError);
    }, noColor: false);
}
