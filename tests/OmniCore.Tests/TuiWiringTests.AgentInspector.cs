using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    private static void OpenAgentInspector(TuiFixture fx)
    {
        OpenGlobalPalette(fx); PaletteSearch(fx, "agent.inspect");
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay?.Id == "agent-inspector", "paleta abre diagnóstico");
    }
    private static void InspectorSearch(TuiFixture fx, string text) => fx.Invoke(() =>
        Descendants(fx.App.Overlay!).OfType<TextField>().Single(view => view.Id == "agent-inspector-search").Text = text);
    private static string InspectorDetails(TuiFixture fx) => fx.Read(() =>
        Descendants(fx.App.Overlay!).Single(view => view.Id == "agent-inspector-details").Text);

    [Theory]
    [InlineData("es", 80, 25, "Diagnóstico de agentes")]
    [InlineData("en", 130, 38, "Agent diagnostics")]
    [InlineData("fr", 80, 25, "Diagnostic des agents")]
    [InlineData("it", 130, 38, "Diagnostica degli agenti")]
    public void Agent_inspector_uses_the_real_Host_query_in_four_locales_without_commands_or_draft_changes(string locale, int width, int height, string title) => RunTuiTest(fx =>
    {
        var recorded = new InspectorClient(fx.Server);
        fx.StartTui(client: recorded, initialColumns: width, initialRows: height, localeSelection: new(locale, LocaleSelectionSource.CommandLine));
        Type(fx, "borrador sin enviar");
        var session = fx.Server.LastSessionId()!; var sequence = fx.Server.AcquireStore().CurrentSequence(session);
        OpenAgentInspector(fx);
        fx.Read(() =>
        {
            Assert.Contains(title, fx.App.Overlay!.Title);
            var list = Descendants(fx.App.Overlay).OfType<ListView>().Single(); Assert.True(list.Source!.Count > 0);
            Assert.Contains(session.ToString(), InspectorDetails(fx));
            Assert.Contains(fx.Server.LastRunId()!.ToString(), InspectorDetails(fx));
            Assert.True(Descendants(fx.App.Overlay).Single(view => view.Id == "agent-inspector-search").HasFocus);
            var details = Descendants(fx.App.Overlay).Single(view => view.Id == "agent-inspector-details");
            Assert.True(details.Frame.Height >= 3);
        });
        ExportPreferenceFrame(fx, width, height, "agent-inspector-" + locale, title);
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => Descendants(fx.App.Overlay!).Single(view => view.Id == "agent-inspector-details").HasFocus, "Enter enfoca detalle desplazable");
        ClickId(fx, "agent-inspector-refresh");
        Assert.Contains(session.ToString(), InspectorDetails(fx));
        fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => fx.App.Overlay is null && fx.App.Composer!.HasFocus, "Esc vuelve al borrador");
        Assert.Equal("borrador sin enviar", fx.Read(() => fx.App.Composer!.Text));
        Assert.Equal(sequence, fx.Server.AcquireStore().CurrentSequence(session)); Assert.Empty(recorded.Commands);
    }, noColor: false);

    [Fact]
    public void Agent_inspector_distinguishes_claim_debt_and_evidence_and_refresh_keeps_the_selected_Lane() => RunTuiTest(fx =>
    {
        var recorded = new InspectorClient(fx.Server); fx.StartTui(client: recorded, initialColumns: 130, initialRows: 38);
        var original = AgentsJson.Decode(fx.Server.Query("agents", CancellationToken.None)!.Json)!;
        var lane = AgentInspectorTests.Lane() with { LaneId = "inspect-lane", TaskId = "inspect-task", ExecutionId = "inspect-execution",
            SupervisionState = "Failed · Wait (subject blocked; joins unresolved)",
            ValidationDebts = [new("inspect-debt", "Integration", ["run the integration suite"])],
            IntegrationVerified = true, IntegrationValidationPassed = true, ToolBackedEvidenceCount = 1 };
        recorded.Agents = AgentsJson.Encode(original with { Lanes = original.Lanes.Append(lane).ToArray() });
        OpenAgentInspector(fx); InspectorSearch(fx, "inspect-execution");
        var text = InspectorDetails(fx);
        Assert.Contains("inspect-debt", text); Assert.Contains("run the integration suite", text);
        Assert.Contains("La declaración del agente no acredita", text); Assert.Contains("Deuda de validación pendiente", text);
        Assert.DoesNotContain("Integración verificada y validada", text);
        Assert.Contains("Supervisión fallida", text);
        InspectorSearch(fx, ""); fx.InjectKey(new Key(KeyCode.CursorDown));
        Assert.Contains("inspect-execution", InspectorDetails(fx));
        recorded.Agents = AgentsJson.Encode(original with { BasedOnJournalSequence = original.BasedOnJournalSequence + 1,
            Lanes = new[] { lane with { ValidationDebts = [] } }.Concat(original.Lanes).ToArray() });
        ClickId(fx, "agent-inspector-refresh");
        Assert.Contains("inspect-execution", InspectorDetails(fx));
        Assert.Contains("Integración verificada y validada", InspectorDetails(fx));
        InspectorSearch(fx, "no-match-123456");
        fx.Read(() => Assert.Contains("Sin agentes que coincidan", OverlayText(fx.App.Overlay!)));
        Assert.Empty(InspectorDetails(fx));
        InspectorSearch(fx, new string('x', 257));
        fx.Read(() => Assert.Contains("256", OverlayText(fx.App.Overlay!)));
        Assert.Empty(recorded.Commands);
    });

    [Theory]
    [InlineData("stale", "Captura antigua")]
    [InlineData("scope", "Cambió la conversación")]
    [InlineData("invalid", "Captura inconsistente")]
    [InlineData("failure", "Diagnóstico no disponible")]
    public void Agent_inspector_refresh_rejects_wrong_or_unavailable_captures_and_can_recover(string kind, string message) => RunTuiTest(fx =>
    {
        var recorded = new InspectorClient(fx.Server); fx.StartTui(client: recorded); OpenAgentInspector(fx);
        var original = AgentsJson.Decode(fx.Server.Query("agents", CancellationToken.None)!.Json)!;
        recorded.Agents = AgentsJson.Encode(kind switch {
            "stale" => original with { BasedOnJournalSequence = original.BasedOnJournalSequence - 1 },
            "scope" => original with { RunId = "other-run", Lanes = [AgentInspectorTests.Lane() with { Objective = "FOREIGN SECRET" }] },
            "invalid" => original with { Lanes = original.Lanes.Concat(original.Lanes).ToArray() }, _ => original });
        recorded.FailAgents = kind == "failure";
        ClickId(fx, "agent-inspector-refresh");
        fx.Read(() => { Assert.Contains(message, OverlayText(fx.App.Overlay!)); Assert.DoesNotContain("FOREIGN SECRET", OverlayText(fx.App.Overlay!));
            Assert.DoesNotContain("internal error", OverlayText(fx.App.Overlay!)); });
        Assert.Empty(InspectorDetails(fx));
        recorded.Agents = null; recorded.FailAgents = false; ClickId(fx, "agent-inspector-refresh");
        Assert.Contains(original.SessionId, InspectorDetails(fx)); Assert.Empty(recorded.Commands);
    });

    [Fact]
    public void Agent_inspector_User_binding_preserves_attachments_and_pending_permissions() => RunTuiTest(fx =>
    {
        KeyboardFile(fx, "version: 1\nbindings:\n  - keys: F9\n    action: agent.inspect\n");
        var path = Path.Combine(fx.Root, "inspector-note.txt"); File.WriteAllText(path, "fixture attachment");
        var recorded = new InspectorClient(fx.Server); fx.StartTui(client: recorded, turnHost: new AttachmentLayoutTurn(fx));
        Type(fx, "/attach \"" + path + "\""); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay is not null, "adjunto preparado"); fx.InjectKey(new Key(KeyCode.Esc));
        Type(fx, "borrador con adjunto");
        var chip = fx.Read(() => Descendants(fx.App.MainWindow!).Single(view => view.Id == "omni-attachments").Text);
        fx.InjectKey(new Key(KeyCode.F9)); fx.Wait(() => fx.App.Overlay?.Id == "agent-inspector", "atajo User abre diagnóstico");
        fx.InjectKey(new Key(KeyCode.Esc)); fx.Wait(() => fx.App.Overlay is null, "cierra diagnóstico");
        Assert.Equal("borrador con adjunto", fx.Read(() => fx.App.Composer!.Text));
        fx.Read(() => Assert.Equal(chip, Descendants(fx.App.MainWindow!).Single(view => view.Id == "omni-attachments").Text));
        var permission = InteractionId.New();
        fx.Invoke(() => { fx.AppendInteraction(permission, "Permission", "{\"operation\":\"test\"}", "[{\"id\":\"deny\"}]", "deny"); fx.App.PollOnce(); });
        var approval = fx.Read(() => fx.App.Overlay); Assert.NotNull(approval);
        fx.InjectKey(new Key(KeyCode.F9)); Assert.Same(approval, fx.Read(() => fx.App.Overlay)); Assert.Empty(recorded.Commands);
    });

    [Fact]
    public void Agent_inspector_stops_refreshing_after_the_active_conversation_changes() => RunTuiTest(fx =>
    {
        var recorded = new InspectorClient(fx.Server); fx.StartTui(client: recorded); OpenAgentInspector(fx);
        fx.Invoke(() => Assert.Equal("ok", fx.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.new\"}"), CancellationToken.None).Status));
        ClickId(fx, "agent-inspector-refresh");
        fx.Read(() =>
        {
            Assert.Contains("Cambió la conversación", OverlayText(fx.App.Overlay!));
            Assert.False(Descendants(fx.App.Overlay!).Single(view => view.Id == "agent-inspector-refresh").Enabled);
        });
        Assert.Empty(InspectorDetails(fx)); Assert.Empty(recorded.Commands);
    });

    private sealed class InspectorClient(IOmniClient inner) : IOmniClient, ITrustedUserActionClient
    {
        internal string? Agents;
        internal volatile bool FailAgents;
        internal readonly System.Collections.Concurrent.ConcurrentQueue<WireEnvelope> Commands = new();
        public CommandAck Send(WireEnvelope command, CancellationToken token) { Commands.Enqueue(command); return inner.Send(command, token); }
        public CommandAck SendUserAction(WireEnvelope command, CancellationToken token)
        { Commands.Enqueue(command); return ((ITrustedUserActionClient)inner).SendUserAction(command, token); }
        public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence) => inner.SubscribeSince(fromSequence);
        public SessionQueryResult? Query(string name, CancellationToken token) => name == "agents"
            ? FailAgents ? throw new IOException("internal error fixture") : Agents is { } payload ? new(name, payload) : inner.Query(name, token)
            : inner.Query(name, token);
    }
}
