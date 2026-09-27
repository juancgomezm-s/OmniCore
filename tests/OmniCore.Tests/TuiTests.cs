namespace OmniCore.Cli;

public sealed class TuiTests
{
    [Fact]
    public async Task TuiApp_renders_header_and_status_line()
    {
        var state = OmniCore.Client.ClientState.Empty();
        var frame = TuiApp.Render(state, "es");

        Assert.Contains("act", frame); // status line con modo
        Assert.Contains("—", frame); // cuota desconocida sin inventar datos (ADR-0031)
    }

    [Fact]
    public async Task TuiApp_renders_conversation_blocks_in_order()
    {
        var projection = new OmniCore.Client.ClientProjection();
        var state = OmniCore.Client.ClientState.Empty();
        state = projection.Apply(state, OmniCore.Protocol.WireEnvelope.Event(
            OmniCore.Protocol.Ids.NewV7(),
            "{" + OmniCore.Protocol.JsonObj.Field("type", "sim.events")
            + "," + OmniCore.Protocol.JsonObj.Field("run", "Completed")
            + "," + OmniCore.Protocol.JsonObj.Field("exitCode", "0") + "}"));

        var frame = TuiApp.Render(state, "es");

        Assert.Contains("Completed", frame);
    }

    [Fact]
    public async Task TuiApp_renders_sidebar_when_widgets_present()
    {
        var sidebar = new OmniCore.Client.SidebarModel(new string[] { "session", "plan" });
        var state = new OmniCore.Client.ClientState(
            OmniCore.Client.HeaderModel.Empty(), OmniCore.Client.ConversationModel.Empty(), sidebar,
            OmniCore.Client.ComposerModel.Empty(), OmniCore.Client.StatusLineModel.Empty(),
            new OmniCore.Client.InteractionOverlayModel[0],
            new OmniCore.Client.ConnectionState(OmniCore.Client.ClientStatus.Connected));

        var frame = TuiApp.Render(state, "es");

        Assert.Contains("session", frame);
        Assert.Contains("plan", frame);
    }
}