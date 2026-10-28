using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Host;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Fact]
    public void Companion_tool_message_reaches_principal_and_memory_and_reaction_refresh_without_touching_draft() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1); var host = new CompanionHubTurn(fx);
        fx.StartTui(turnHost: host, initialColumns: 120, initialRows: 40);
        fx.Wait(() => host.CompanionHub.Read().Instances.Length == 1, "principal anunciado");
        var target = host.CompanionHub.Read().Instances[0];
        var provider = new CompanionToolsTests.ScriptedProvider([
            CompanionToolsTests.Response([
                CompanionToolsTests.Call("memory.remember", "{\"text\":\"Mi nombre es Juan\"}"),
                CompanionToolsTests.Call("agent.message", JsonSerializer.Serialize(new { target = target.Session, text = "revisa las pruebas" })),
                CompanionToolsTests.Call("pet.react", "{\"animation\":\"success\"}")]),
            CompanionToolsTests.Response([new TextBlock("Guardé el recuerdo y dejé tu instrucción en cola.")])]);
        host.ChatHandler = (history, token) => CompanionConversation.Reply(provider, new(new("test"), 8192, ToolMode.Direct, null), "Omni", "", history, token,
            tools: new(host.CompanionHub, history[^1].Text, target.Session));
        Type(fx, "/pet chat"); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => HubView(fx, "companion-hub-action").Enabled, "mini chat disponible");
        fx.Invoke(() =>
        {
            fx.App.Composer!.Text = "borrador del trabajo";
            HubView(fx, "companion-chat-input").Text = "Recuerda: Mi nombre es Juan. Dile a " + target.Session + ": revisa las pruebas";
            HubView(fx, "companion-chat-input").SetFocus();
        });
        HubClick(fx, "companion-hub-action");
        fx.Wait(() => HubView(fx, "companion-chat-history").Text.Contains("instrucción en cola"), "respuesta de herramientas");
        fx.Wait(() => host.Input == "revisa las pruebas", "instrucción admitida por principal");
        fx.Wait(() => host.CompanionHub.Read().Deliveries.Any(d => d.Status == "accepted"), "acuse de la sesión");
        Assert.Equal("borrador del trabajo", fx.Read(() => fx.App.Composer!.Text));
        Assert.True(fx.Read(() => HubView(fx, "companion-chat-input").HasFocus));
        Assert.Contains("Mi nombre es Juan", host.CompanionHub.ReadMemory().Text);
        Assert.Single(host.CompanionHub.Read().Deliveries);
        ExportPreferenceFrame(fx, 120, 40, "companion-tools-message", "instrucción en cola");
        HubClick(fx, "companion-memory-tab");
        fx.Wait(() => HubView(fx, "companion-chat-input").Text.Contains("Mi nombre es Juan"), "memoria actualizada tras tool");
    }, noColor: false);
}
