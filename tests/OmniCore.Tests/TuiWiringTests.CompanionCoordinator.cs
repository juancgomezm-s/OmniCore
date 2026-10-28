using OmniCore.Host;
using OmniCore.Client;
using OmniCore.Protocol;
using OmniCore.Domain;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    private sealed class CompanionHubTurn(TuiFixture fixture) : ITuiTurnHost
    {
        public CompanionCoordinator CompanionHub { get; } = new(Path.Combine(fixture.Root, "hub"));
        public string CompanionWorkspace => fixture.Workspace;
        public CompanionModelOption[] CompanionModels() => [new("cloud-chat", "paid", false), new("local-chat", "local", true)];
        public int Chats; public string? Input;
        public Func<IReadOnlyList<CompanionChatMessage>, CancellationToken, Task<string>>? ChatHandler;
        public Task<string> CompanionChatAsync(IReadOnlyList<CompanionChatMessage> history, CancellationToken token)
        { Chats++; return ChatHandler?.Invoke(history, token) ?? System.Threading.Tasks.Task.FromResult("Hola, Juan. ¿Cómo va tu proyecto?"); }
        public Task<int> ExecuteAsync(string input, Action<string> diagnostics, CancellationToken token)
        { Input = input; return System.Threading.Tasks.Task.FromResult(0); }
    }
    private static View HubView(TuiFixture fx, string id) => Descendants(fx.App.MainWindow!).Single(v => v.Id == id);
    private static void HubClick(TuiFixture fx, string id) => fx.Invoke(() => ((Button)HubView(fx, id)).InvokeCommand(Command.Accept));

    [Theory]
    [InlineData(120, 40)]
    [InlineData(80, 25)]
    public void Companion_mini_chat_preserves_main_draft_uses_model_only_on_send_and_closes_cleanly(int width, int height) => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1); var host = new CompanionHubTurn(fx);
        fx.StartTui(turnHost: host, initialColumns: width, initialRows: height);
        Type(fx, "/pet chat"); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => Descendants(fx.App.MainWindow!).Any(v => v.Id == "companion-chat-input"), "mini chat nativo");
        fx.Wait(() => host.CompanionHub.Read().Instances.Length > 0, "instancia registrada");
        fx.Wait(() => HubView(fx, "companion-hub-action").Enabled, "historial preparado para enviar");
        fx.Invoke(() => fx.App.Composer!.Text = "borrador principal sin enviar");
        Assert.Equal(0, host.Chats);
        fx.Invoke(() => HubView(fx, "companion-chat-input").Text = "Hola mascota");
        HubClick(fx, "companion-hub-action");
        fx.Wait(() => HubView(fx, "companion-chat-history").Text.Contains("Hola, Juan"), "respuesta independiente");
        Assert.Equal(1, host.Chats); Assert.Null(host.Input);
        Assert.Equal("borrador principal sin enviar", fx.Read(() => fx.App.Composer!.Text));
        ExportPreferenceFrame(fx, width, height, "companion-mini-chat", "Hola, Juan");
        HubClick(fx, "companion-hub-close");
        fx.Wait(() => !Descendants(fx.App.MainWindow!).Any(v => v.Id == "companion-hub"), "panel cerrado");
        Assert.True(fx.Read(() => fx.App.Composer!.HasFocus));
        Assert.Equal("borrador principal sin enviar", fx.Read(() => fx.App.Composer!.Text));
    }, noColor: false);

    [Fact]
    public void Companion_remote_instruction_reaches_principal_without_changing_editor_or_focus() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1); var host = new CompanionHubTurn(fx);
        fx.StartTui(turnHost: host, initialColumns: 120, initialRows: 40);
        fx.Wait(() => host.CompanionHub.Read().Instances.Length == 1, "heartbeat");
        fx.Invoke(() => { fx.App.Composer!.Text = "mi borrador"; fx.App.Composer.SetFocus(); });
        var target = host.CompanionHub.Read().Instances[0];
        host.CompanionHub.Send(new(Guid.NewGuid().ToString(), target.Id, target.Session, target.Run, "instruction", "Revisa las pruebas"));
        fx.Wait(() => host.Input == "Revisa las pruebas", "entrega al principal");
        fx.Wait(() => host.CompanionHub.Read().Deliveries[0].Status == "accepted", "acuse real");
        Assert.Equal("mi borrador", fx.Read(() => fx.App.Composer!.Text));
        Assert.True(fx.Read(() => fx.App.Composer!.HasFocus));
    }, noColor: false);

    [Fact]
    public void Companion_restrict_stops_roaming_and_keeps_chat_available() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1); var host = new CompanionHubTurn(fx);
        fx.StartTui(turnHost: host, initialColumns: 120, initialRows: 40);
        Type(fx, "/pet play"); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.CompanionRunner?.Visible == true, "paseo activo");
        HubClick(fx, "companion-restrict");
        fx.Wait(() => fx.App.CompanionPet!.Preferences.Restricted && fx.App.CompanionRunner?.Visible != true, "restricción aplicada");
        Assert.Equal("sad", fx.Read(() => fx.App.CompanionPet!.Reaction!.AnimationId));
        HubClick(fx, "companion-hub-open");
        fx.Wait(() => Descendants(fx.App.MainWindow!).Any(v => v.Id == "companion-chat-input"), "chat disponible estando restringida");
        ExportPreferenceFrame(fx, 120, 40, "companion-restricted-chat", "Chat");
    }, noColor: false);

    [Fact]
    public void Companion_memory_editor_saves_selected_facts_without_touching_main_chat() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "ox", 0); var host = new CompanionHubTurn(fx);
        fx.StartTui(turnHost: host, initialColumns: 120, initialRows: 40);
        Type(fx, "/pet memory"); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => HubView(fx, "companion-chat-input").Text.Contains("Nombre preferido"), "memoria cargada");
        fx.Invoke(() => { fx.App.Composer!.Text = "borrador privado"; HubView(fx, "companion-chat-input").Text = "Nombre preferido: Juan\nPreguntar por el proyecto"; });
        HubClick(fx, "companion-hub-action");
        fx.Wait(() => host.CompanionHub.ReadMemory().Text.Contains("Juan"), "guardado local");
        Assert.DoesNotContain("borrador privado", host.CompanionHub.ReadMemory().Text);
        Assert.Equal(0, host.Chats); ExportPreferenceFrame(fx, 120, 40, "companion-memory", "Juan");
    }, noColor: false);

    [Fact]
    public void Companion_remote_decision_uses_real_core_validation_and_resolves_notice() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1); var host = new CompanionHubTurn(fx); var interaction = InteractionId.New();
        fx.AppendInteraction(interaction, "Permission", "{\"operation\":\"tool.authorize\",\"toolOrExecutable\":\"filesystem.read\",\"target\":\"src/Alpha.cs\"}",
            "[{\"id\":\"allow_once\"},{\"id\":\"deny\"}]", "deny");
        fx.StartTui(turnHost: host, initialColumns: 120, initialRows: 40);
        fx.Wait(() => host.CompanionHub.Read().Instances.Any(i => i.Decisions.Length == 1), "decisión publicada");
        var instance = host.CompanionHub.Read().Instances[0];
        host.CompanionHub.Send(new(Guid.NewGuid().ToString(), instance.Id, instance.Session, instance.Run, "answer", "", interaction.ToString(), "deny"));
        fx.Wait(() => fx.Decoded<InteractionResolved>().Any(i => i.InteractionId == interaction), "respuesta admitida por Core");
        fx.Wait(() => host.CompanionHub.Read().Notices.Any(n => n.Resolved), "aviso resuelto");
        Assert.Null(host.Input);
    }, noColor: false);

    [Fact]
    public void Companion_sadness_animates_native_graphics_inside_the_chat_panel() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1); var host = new CompanionHubTurn(fx);
        var client = new CompanionExpressionClient(fx.Server) { Expression = "sad" };
        fx.StartTui(turnHost: host, client: client, initialColumns: 120, initialRows: 40);
        if (!fx.Read(() => fx.App.SidebarOpen)) fx.InjectKey(new Key(KeyCode.F2));
        fx.Wait(() => fx.App.Sidebar!.Visible, "lateral visible para animación gráfica");
        Type(fx, "/pet home"); fx.InjectKey(new Key(KeyCode.Enter)); EmulatePetSixelDevice(fx);
        Type(fx, "/pet chat"); fx.InjectKey(new Key(KeyCode.Enter));
        var first = WaitForPetExpressionFrame(fx, "sad", null); ExportPetRasterFrame(fx, "sad-first");
        var second = WaitForPetExpressionFrame(fx, "sad", first); Assert.NotEqual(first, second); ExportPetRasterFrame(fx, "sad-second");
        ExportPreferenceFrame(fx, 120, 40, "companion-graphic-chat", "Mensaje");
        var directory = Environment.GetEnvironmentVariable("OMNICORE_PET_GRAPHICS_CAPTURE_DIR");
        if (directory is not null) fx.Read(() =>
        {
            var raster = fx.Application.Driver!.GetOutputBuffer().GetRasterImages().Single(); var pixels = raster.Pixels!;
            var bytes = new byte[pixels.Length * 3]; var offset = 0;
            for (var y = 0; y < pixels.GetLength(1); y++) for (var x = 0; x < pixels.GetLength(0); x++)
            { var color = pixels[x, y]; bytes[offset++] = color.R; bytes[offset++] = color.G; bytes[offset++] = color.B; }
            File.WriteAllBytes(Path.Combine(directory, "companion-sad-second.rgb"), bytes);
            return true;
        });
    }, noColor: false);

    [Fact]
    public void Companion_panel_resize_preserves_both_drafts_and_returns_to_sidebar() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1); var host = new CompanionHubTurn(fx);
        fx.StartTui(turnHost: host, initialColumns: 120, initialRows: 40);
        if (!fx.Read(() => fx.App.SidebarOpen)) fx.InjectKey(new Key(KeyCode.F2));
        fx.Wait(() => fx.App.Sidebar!.Visible, "lateral visible antes de abrir mascota");
        Type(fx, "/pet chat"); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => HubView(fx, "companion-hub-action").Enabled, "mini chat preparado");
        fx.Invoke(() => { HubView(fx, "companion-chat-input").Text = "borrador de mascota"; fx.App.Composer!.Text = "borrador de trabajo"; fx.App.Composer.SetFocus(); });
        fx.Invoke(() => fx.Application.Driver!.SetScreenSize(80, 25));
        fx.Wait(() => fx.App.Overlay?.Id == "companion-hub", "tarjeta estrecha");
        Assert.Equal("borrador de mascota", fx.Read(() => HubView(fx, "companion-chat-input").Text));
        Assert.Equal("borrador de trabajo", fx.Read(() => fx.App.Composer!.Text));
        fx.Invoke(() => fx.Application.Driver!.SetScreenSize(120, 40));
        fx.Wait(() => fx.App.Overlay is null && Descendants(fx.App.Sidebar!).Any(v => v.Id == "companion-hub"), "panel lateral restaurado");
        Assert.Equal("borrador de mascota", fx.Read(() => HubView(fx, "companion-chat-input").Text));
        Assert.True(fx.Read(() => fx.App.Composer!.HasFocus));
    }, noColor: false);
}
