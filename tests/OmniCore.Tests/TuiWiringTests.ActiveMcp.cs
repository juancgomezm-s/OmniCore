using OmniCore.Domain;
using OmniCore.Engine;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(80, 25)]
    [InlineData(130, 38)]
    public void Session_shows_current_Mcp_connectors_below_mode_and_clears_finished_calls(int columns, int rows) => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: columns, initialRows: rows);
        if (!fx.App.SidebarOpen) KeyWithEffect(fx, KeyCode.F2, () => fx.App.SidebarOpen, "sidebar visible");
        var run = fx.Server.LastRunId()!;
        var stream = new EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!);
        ToolCallId Start(string server)
        {
            var id = ToolCallId.New();
            using var scope = ExecutionScope.Begin(new(run));
            stream.Append(new ToolCallRequested(id, "fixture", "mcp." + server + ".lookup", "{}"));
            stream.Append(new ToolCallPrepared(id, "{}"));
            stream.Append(new ToolCallAuthorized(id));
            stream.Append(new ToolCallStarted(id, EffectClass.None, null)
            { Attribution = new("mcp." + server + ".lookup", "Mcp", null, "1", null, server) });
            return id;
        }
        var github = Start("github"); var search = Start("search");
        fx.Invoke(() => fx.App.PollOnce());
        fx.Wait(() => fx.App.SidebarSession!.Text.ToString()!.Contains("MCP: github", StringComparison.Ordinal)
            && fx.App.SidebarSession.Text.ToString()!.Contains("search", StringComparison.Ordinal), "two active MCP connectors");
        var session = fx.App.SidebarSession!;
        Assert.True(session.Frame.Bottom <= fx.App.SidebarContent!.Frame.Y);
        ExportPreferenceFrame(fx, columns, rows, "active-mcp", "MCP: github");
        using (ExecutionScope.Begin(new(run))) stream.Append(new ToolCallSucceeded(github, "{}"));
        fx.Invoke(() => fx.App.PollOnce());
        fx.Wait(() => !session.Text.ToString()!.Contains("github", StringComparison.Ordinal)
            && session.Text.ToString()!.Contains("MCP: search", StringComparison.Ordinal), "finished connector disappears");
        using (ExecutionScope.Begin(new(run))) stream.Append(new ToolCallFailed(search, "fixture", EffectOutcome.None));
        fx.Invoke(() => fx.App.PollOnce());
        fx.Wait(() => session.Text.ToString()!.Contains("MCP: —", StringComparison.Ordinal), "idle MCP state");
    }, noColor: false);
}
