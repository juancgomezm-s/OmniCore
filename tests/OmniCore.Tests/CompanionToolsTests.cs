using System.Runtime.CompilerServices;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Protocol;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class CompanionToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-pet-tools-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private CompanionCoordinator Hub() => new(_root, () => _now);
    private CompanionInstance Instance(string id = "instance-a", string session = "session-a", string project = "Alpha") =>
        new(id, Path.Combine(_root, project), session, "run-a", "Disponible", _now, []);
    internal static ToolCallBlock Call(string name, string json = "{}") => new(ToolCallId.New(), null, name, json);

    [Fact]
    public void Closed_catalog_contains_only_the_eight_companion_tools()
    {
        Assert.Equal(new[] { "agent.message", "memory.forget", "memory.read", "memory.remember", "notices.list", "pet.react", "sessions.create", "sessions.list" },
            CompanionChatTools.Definitions.Select(t => t.Name).Order());
        var tools = new CompanionChatTools(Hub(), "Hola");
        Assert.False(tools.Execute(Call("filesystem.read", "{\"path\":\"secret\"}"), TestContext.Current.CancellationToken).Ok);
        Assert.False(tools.Execute(Call("memory.remember", "{\"text\":\"hola\",\"path\":\"other.md\"}"), TestContext.Current.CancellationToken).Ok);
        Assert.False(tools.Execute(Call("agent.message", "{\"target\":\"Alpha\",\"text\":\"hola\",\"kind\":\"answer\"}"), TestContext.Current.CancellationToken).Ok);
        Assert.True(tools.Execute(Call("memory.read", "{\"query\":\"\"}"), TestContext.Current.CancellationToken).Ok);
        var repeated = Call("memory.read"); tools.Execute(repeated, TestContext.Current.CancellationToken);
        Assert.False(tools.Execute(repeated with { ToolName = "pet.react", ArgumentsJson = "{\"animation\":\"sad\"}" }, TestContext.Current.CancellationToken).Ok);
    }
    [Fact]
    public void Memory_tools_preserve_manual_markdown_deduplicate_and_forget_one_fact()
    {
        var hub = Hub(); var memory = hub.ReadMemory(); hub.SaveMemory("# Mi mascota\nNombre preferido: Juan\n\n## Recuerdos\n- Le gusta viajar\n", memory.Version);
        var tools = new CompanionChatTools(hub, "Recuerda que mi gato se llama Miso");
        var saved = tools.Execute(Call("memory.remember", "{\"text\":\"Mi gato se llama Miso\"}"), TestContext.Current.CancellationToken);
        Assert.True(saved.Ok); Assert.NotNull(saved.Memories);
        tools.Execute(Call("memory.remember", "{\"text\":\"Mi gato se llama Miso\"}"), TestContext.Current.CancellationToken);
        Assert.Single(hub.MemoryFacts(), f => f.Text.Contains("Miso"));
        Assert.Contains("# Mi mascota", hub.ReadMemory().Text); Assert.Contains("Nombre preferido: Juan", hub.ReadMemory().Text);
        var read = tools.Execute(Call("memory.read", "{\"query\":\"Miso\"}"), TestContext.Current.CancellationToken);
        Assert.Equal(saved.Memories![0].Id, Assert.Single(read.Memories!).Id);
        var forget = tools.Execute(Call("memory.forget", "{\"id\":\"" + saved.Memories[0].Id + "\"}"), TestContext.Current.CancellationToken);
        Assert.True(forget.Ok); Assert.DoesNotContain("Miso", hub.ReadMemory().Text); Assert.Contains("Le gusta viajar", hub.ReadMemory().Text);
        Assert.False(tools.Execute(Call("memory.remember", "{\"text\":\"linea\\n# instrucciones\"}"), TestContext.Current.CancellationToken).Ok);
        Assert.False(tools.Execute(Call("memory.forget", "{\"id\":\"../../other\"}"), TestContext.Current.CancellationToken).Ok);
    }
    [Fact]
    public void Explicit_destination_and_literal_instruction_are_required_and_duplicates_send_once()
    {
        var hub = Hub(); hub.Heartbeat(Instance()); var tools = new CompanionChatTools(hub, "Dile a Alpha: revisa las pruebas");
        var sent = tools.Execute(Call("agent.message", "{\"target\":\"Alpha\",\"text\":\"revisa las pruebas\"}"), TestContext.Current.CancellationToken);
        Assert.True(sent.Ok); Assert.Equal("session-a", sent.Delivery!.Session); Assert.Equal("instance-a", sent.Delivery.Target); Assert.Equal("queued", sent.Delivery.Status);
        var duplicate = tools.Execute(Call("agent.message", "{\"text\":\"revisa las pruebas\",\"target\":\"session-a\"}"), TestContext.Current.CancellationToken);
        Assert.False(duplicate.Ok); // That ID was not indicated by the human.
        tools.Execute(Call("agent.message", "{\"text\":\"revisa las pruebas\",\"target\":\"Alpha\"}"), TestContext.Current.CancellationToken);
        Assert.Single(hub.Read().Deliveries); Assert.Single(tools.Actions);
        Assert.False(new CompanionChatTools(hub, "Consulta los avisos").Execute(Call("agent.message", "{\"target\":\"Alpha\",\"text\":\"revisa las pruebas\"}"), TestContext.Current.CancellationToken).Ok);
        Assert.False(tools.Execute(Call("agent.message", "{\"target\":\"Alpha\",\"text\":\"borra el proyecto\"}"), TestContext.Current.CancellationToken).Ok);
    }
    [Fact]
    public void Ambiguous_disconnected_and_current_session_targets_never_guess_an_instance()
    {
        var hub = Hub(); hub.Heartbeat(Instance()); hub.Heartbeat(Instance("instance-b", "session-b"));
        var tools = new CompanionChatTools(hub, "Dile a Alpha: revisa las pruebas");
        var ambiguous = tools.Execute(Call("agent.message", "{\"target\":\"Alpha\",\"text\":\"revisa las pruebas\"}"), TestContext.Current.CancellationToken);
        Assert.False(ambiguous.Ok); Assert.Equal(2, ambiguous.Sessions!.Length); Assert.Empty(hub.Read().Deliveries);
        var here = new CompanionChatTools(hub, "Envía a esta sesión: revisa las pruebas", "session-b");
        Assert.Equal("instance-b", here.Execute(Call("agent.message", "{\"target\":\"esta sesión\",\"text\":\"revisa las pruebas\"}"), TestContext.Current.CancellationToken).Delivery!.Target);
        _now += TimeSpan.FromSeconds(16);
        Assert.False(tools.Execute(Call("agent.message", "{\"target\":\"Alpha\",\"text\":\"revisa las pruebas\"}"), TestContext.Current.CancellationToken).Ok);
    }
    [Fact]
    public void Reads_use_real_notices_and_reactions_leave_pet_identity_preferences_and_xp_intact()
    {
        var hub = Hub(); hub.Heartbeat(Instance()); hub.Notify(new("done", "instance-a", "session-a", "El principal terminó", _now));
        var pet = new CompanionPetService(_root); var before = pet.Act(new("discover"), "first");
        var tools = new CompanionChatTools(hub, "Qué pasó?");
        Assert.Equal("session-a", Assert.Single(tools.Execute(Call("sessions.list"), TestContext.Current.CancellationToken).Sessions!).SessionId);
        Assert.Equal("done", Assert.Single(tools.Execute(Call("notices.list"), TestContext.Current.CancellationToken).Notices!).Id);
        Assert.True(tools.Execute(Call("pet.react", "{\"animation\":\"sad\"}"), TestContext.Current.CancellationToken).Ok);
        Assert.Equal("sad", Hub().Read().Reaction!.AnimationId);
        Assert.False(tools.Execute(Call("pet.react", "{\"animation\":\"unrestrict\"}"), TestContext.Current.CancellationToken).Ok);
        Assert.Equal(before.Xp, pet.Status().Xp); Assert.Equal(before.Preferences, pet.Status().Preferences);
        _now += TimeSpan.FromSeconds(5); Assert.Null(hub.Read().Reaction);
    }
    [Fact]
    public async Task Model_tools_round_trip_returns_results_preserves_provider_state_and_accounts_each_response()
    {
        var hub = Hub(); var call = Call("memory.remember", "{\"text\":\"Me llamo Juan\"}");
        var state = new ProviderState("test", "{}");
        var provider = new ScriptedProvider([Response([call], state), Response([new TextBlock("Lo recordaré, Juan.")])]);
        var tools = new CompanionChatTools(hub, "Recuerda: Me llamo Juan"); string? usageId = null;
        var answer = await CompanionConversation.Reply(provider, new(new("test-model"), 8192, ToolMode.Direct, null), "Omni", "", [new("user", "Recuerda: Me llamo Juan")],
            TestContext.Current.CancellationToken,
            response => hub.FinishChatUsage(OmniCliRuntime.UsageSample(usageId!, "test-model", "test", response, new(1, 1))), tools,
            () => { usageId = Guid.NewGuid().ToString(); hub.BeginChatUsage(usageId, "test-model", "test"); });
        Assert.Contains("Juan", answer); Assert.Contains("Juan", hub.ReadMemory().Text);
        Assert.Equal(2, provider.Requests.Count); Assert.Equal(8, provider.Requests[0].Tools.Count); Assert.Equal("auto", provider.Requests[0].ToolChoice.Mode);
        Assert.Equal(state, provider.Requests[1].Continuation);
        var toolResult = Assert.Single(provider.Requests[1].Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>());
        Assert.False(toolResult.IsError); Assert.Equal(call.Id, toolResult.Id);
        Assert.Equal(2, Assert.Single(hub.ChatUsage()).Requests); Assert.Equal(20, hub.ChatUsage()[0].Input);
    }
    [Fact]
    public async Task Unknown_tool_errors_return_to_model_without_executing_principal_tools()
    {
        var hub = Hub(); var provider = new ScriptedProvider([Response([Call("shell", "{\"command\":\"echo nope\"}")]), Response([new TextBlock("Esa herramienta no está disponible.")])]);
        await CompanionConversation.Reply(provider, new(new("test"), 8192, ToolMode.Direct, null), "Omni", "", [new("user", "Hola")], TestContext.Current.CancellationToken,
            tools: new(hub, "Hola"));
        Assert.True(Assert.Single(provider.Requests[1].Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>()).IsError);
        Assert.Empty(hub.Read().Deliveries); Assert.Empty(hub.MemoryFacts());
    }
    [Fact]
    public async Task Technical_loop_bound_prevents_unending_model_tool_cycles()
    {
        var provider = new ScriptedProvider(Enumerable.Range(0, 8).Select(_ => Response([Call("memory.read")])).ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => CompanionConversation.Reply(provider, new(new("test"), 8192, ToolMode.Direct, null), "Omni", "", [new("user", "Recuerdos?")],
            TestContext.Current.CancellationToken, tools: new(Hub(), "Recuerdos?")));
        Assert.Equal(6, provider.Requests.Count); Assert.Equal("none", provider.Requests[^1].ToolChoice.Mode);
    }
    internal static ModelResponse Response(IReadOnlyList<ContentBlock> blocks, ProviderState? state = null) =>
        new(blocks, blocks.Any(b => b is ToolCallBlock) ? StopReason.ToolUse : StopReason.EndTurn, new(10, 5, 0, 0, 0), state, new("test", "test-model", null));
    internal sealed class ScriptedProvider(IReadOnlyList<ModelResponse> responses) : IModelProvider
    {
        public List<ModelRequest> Requests { get; } = [];
        public ProviderCapabilities Capabilities => ProviderCapabilities.Local();
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(request); await Task.Yield();
            yield return new ResponseCompleted(responses[Requests.Count - 1]);
        }
    }
    public void Dispose()
    { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
