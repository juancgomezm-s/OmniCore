using System.Diagnostics;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class CompanionSessionChannelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-lateral-" + Guid.NewGuid().ToString("N"));
    private sealed class Paths(string root) : IPlatformPaths
    {
        public string DataDirectory => Path.Combine(root, "data");
        public string UserDatabasePath => Path.Combine(DataDirectory, "user.db");
        public string ConfigDirectory => Path.Combine(root, "config");
        public string WorkspaceDirectory(string id) => Path.Combine(DataDirectory, "workspaces", id);
    }
    private static CompanionInstance Peer(string id) => new(id, "project-" + id, "session-" + id, null, "Disponible", DateTimeOffset.UtcNow, [], SupportsLateral: true);
    private CompanionCoordinator Hub() => new(_root);

    [Fact]
    public async Task Pet_creates_a_durable_session_without_switching_the_current_chat_or_starting_work()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "journal.db"); var pointer = Path.Combine(_root, "last-session");
        var codecs = EventCodecs.Create(); string created; string selected;
        using (var store = new SqliteEventStore(path))
        {
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), pointer, platformPathsFactory: () => new Paths(_root));
            Assert.Equal("ok", server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.new\"}"), TestContext.Current.CancellationToken).Status);
            selected = server.LastSessionId()!.ToString(); var original = File.ReadAllText(pointer);
            using var ambient = ExecutionScope.Begin(new(RunId.New(), TurnId: TurnId.New(), ToolCallId: ToolCallId.New()));
            var tools = new CompanionChatTools(Hub(), "Crea una sesión Pruebas", selected, server.CreateCompanionSession);
            var call = CompanionToolsTests.Call("sessions.create", "{\"title\":\"Pruebas\"}");
            var provider = new CompanionToolsTests.ScriptedProvider([
                CompanionToolsTests.Response([call]), CompanionToolsTests.Response([new TextBlock("Sesión creada.")])]);
            await CompanionConversation.Reply(provider, new(new("test"), 8192, ToolMode.Direct, null), "Omni", "",
                [new("user", "Crea una sesión Pruebas")], TestContext.Current.CancellationToken, tools: tools);
            var result = tools.Execute(call, TestContext.Current.CancellationToken);
            Assert.True(result.Ok); created = result.CreatedSession!.SessionId;
            Assert.False(result.CreatedSession.Running); Assert.NotEqual(selected, created);
            Assert.Equal(selected, server.LastSessionId()!.ToString()); Assert.Equal(original, File.ReadAllText(pointer));
            Assert.Equal(result, tools.Execute(CompanionToolsTests.Call("sessions.create", "{\"title\":\"Pruebas\"}"), TestContext.Current.CancellationToken));
            Assert.Equal(result, tools.Execute(CompanionToolsTests.Call("sessions.create", "{ \"title\" : \"Pruebas\" }"), TestContext.Current.CancellationToken));
            var journal = store.ReadFrom(SessionId.Parse(created), 1);
            Assert.All(journal, e => { Assert.Null(e.RunId); Assert.Null(e.TurnId); Assert.Null(e.ToolCallId); });
            var events = journal.Select(codecs.Decode).ToArray();
            Assert.Single(events.OfType<SessionCreated>()); Assert.Single(events.OfType<SessionSecurityPolicySet>());
            Assert.Equal("Pruebas", Assert.Single(events.OfType<SessionTitleChanged>()).Title);
            Assert.Empty(events.OfType<RunCreated>()); Assert.Empty(events.OfType<UserInputReceived>());
            Assert.Single(tools.Actions);
            using var other = new SqliteEventStore(path);
            Assert.True(other.AcquireSessionWriterLease(SessionId.Parse(created))); // Creation retained no foreign writer lease.
        }
        using (var store = new SqliteEventStore(path))
        {
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), pointer, platformPathsFactory: () => new Paths(_root));
            Assert.Equal(selected, server.LastSessionId()!.ToString());
            Assert.Contains(ConversationJson.Decode(server.Query("conversations", TestContext.Current.CancellationToken)!.Json)!.Conversations,
                row => row.Id == created && row.Title == "Pruebas" && !row.Current);
        }
    }

    [Fact]
    public void Same_mailbox_separates_human_instructions_from_lateral_peer_data_and_supports_replies()
    {
        var hub = Hub(); hub.Heartbeat(Peer("a")); hub.Heartbeat(Peer("b"));
        var human = new CompanionDelivery(Guid.NewGuid().ToString(), "b", "session-b", null, "instruction", "Revisa las pruebas");
        hub.Send(human);
        var id = Guid.NewGuid().ToString(); var message = hub.SendPeer("a", "session-a", "b", "¿Terminó tu revisión?", id);
        Assert.Equal("agent", message.Origin); Assert.Equal("lateral", message.Relation);
        Assert.Equal("user", human.Origin); Assert.Equal("human", human.Relation);
        Assert.Equal(message, hub.SendPeer("a", "session-a", "b", "¿Terminó tu revisión?", id));
        Assert.Equal(human.Id, hub.Claim("b", "session-b", null, false)!.Id);
        Assert.Null(hub.Claim("b", "session-b", null, false)); // No automatic turn from peer text.
        var read = new CompanionCoordinator(_root).ReadPeer("b", "session-b");
        var received = Assert.Single(read.Messages); Assert.Equal(id, received.Id); Assert.Equal("a", received.SenderInstance);
        Assert.Equal("session-a", received.SenderSession); Assert.Equal("accepted", received.Status);
        Assert.Equal(read.Messages, hub.ReadPeer("b", "session-b").Messages); // Lost-response retry remains readable.
        Assert.Empty(hub.ReadPeer("b", "session-b", read.Next).Messages);
        Assert.Empty(hub.ReadPeer("a", "session-a").Messages); Assert.Empty(hub.ReadPeer("b", "different-session").Messages);
        hub.SendPeer("b", "session-b", "session-a", "Sí, te paso el resultado", Guid.NewGuid().ToString());
        Assert.Equal("b", Assert.Single(hub.ReadPeer("a", "session-a").Messages).SenderInstance);
        Assert.Equal(3, hub.Read().Deliveries.Length); // One durable mailbox, no parallel transport.
    }

    [Fact]
    public void Changed_senders_ambiguous_destinations_and_conflicting_retries_do_not_send()
    {
        var hub = Hub(); hub.Heartbeat(Peer("a")); hub.Heartbeat(Peer("b"));
        var id = Guid.NewGuid().ToString(); hub.SendPeer("a", "session-a", "b", "Dato", id);
        Assert.Throws<InvalidOperationException>(() => hub.SendPeer("a", "session-a", "b", "Otro dato", id));
        Assert.Throws<InvalidOperationException>(() => hub.SendPeer("a", "session-forged", "b", "Dato", Guid.NewGuid().ToString()));
        hub.Heartbeat(Peer("b") with { Id = "other-b" });
        Assert.Throws<InvalidOperationException>(() => hub.SendPeer("a", "session-a", "session-b", "Dato", Guid.NewGuid().ToString()));
        Assert.Throws<ArgumentException>(() => hub.SendPeer("a", "session-a", "a", "Dato", Guid.NewGuid().ToString()));
        Assert.Single(hub.Read().Deliveries);
    }

    [Fact]
    public void Headless_principals_accept_peer_data_but_not_human_orders_and_older_instances_are_not_targeted()
    {
        var hub = Hub(); hub.Heartbeat(Peer("a")); hub.Heartbeat(Peer("b") with { AcceptsInstructions = false });
        Assert.Throws<InvalidOperationException>(() => hub.Send(new(Guid.NewGuid().ToString(), "b", "session-b", null, "instruction", "Trabaja")));
        hub.SendPeer("a", "session-a", "b", "Dato de coordinación", Guid.NewGuid().ToString());
        Assert.Single(hub.ReadPeer("b", "session-b").Messages);
        hub.Heartbeat(Peer("old") with { SupportsLateral = false });
        Assert.Throws<InvalidOperationException>(() => hub.SendPeer("a", "session-a", "old", "Dato", Guid.NewGuid().ToString()));
        Assert.Single(hub.Read().Deliveries);
    }

    [Fact]
    public void Mailbox_cursor_remains_monotonic_after_old_deliveries_are_pruned()
    {
        var now = DateTimeOffset.UtcNow; var hub = new CompanionCoordinator(_root, () => now);
        hub.Heartbeat(Peer("a")); hub.Heartbeat(Peer("b"));
        hub.SendPeer("a", "session-a", "b", "Antes", Guid.NewGuid().ToString());
        var first = hub.ReadPeer("b", "session-b");
        now = now.AddDays(8); hub.Heartbeat(Peer("a")); hub.Heartbeat(Peer("b"));
        Assert.Empty(hub.Read().Deliveries);
        hub.SendPeer("a", "session-a", "b", "Después", Guid.NewGuid().ToString());
        var next = hub.ReadPeer("b", "session-b", first.Next);
        Assert.Equal("Después", Assert.Single(next.Messages).Text); Assert.True(next.Next > first.Next);
    }

    [Fact]
    public async Task Peer_message_and_reply_cross_real_processes_without_window_focus()
    {
        var hub = Hub(); hub.Heartbeat(Peer("a")); hub.Heartbeat(Peer("b"));
        hub.SendPeer("a", "session-a", "b", "Resultado de a", Guid.NewGuid().ToString());
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(CompanionSessionChannelTests).Assembly.Location);
        start.ArgumentList.Add("-method"); start.ArgumentList.Add("*CompanionSessionChannelTests.Lateral_child_probe"); start.ArgumentList.Add("-noColor");
        start.Environment["OMNI_LATERAL_TEST_ROOT"] = _root;
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(child.ExitCode == 0, await output + await error);
        Assert.Equal("Respuesta de b", Assert.Single(hub.ReadPeer("a", "session-a").Messages).Text);
    }
    [Fact]
    public void Lateral_child_probe()
    {
        var path = Environment.GetEnvironmentVariable("OMNI_LATERAL_TEST_ROOT"); if (path is null) return;
        var hub = new CompanionCoordinator(path);
        Assert.Equal("Resultado de a", Assert.Single(hub.ReadPeer("b", "session-b").Messages).Text);
        hub.SendPeer("b", "session-b", "a", "Respuesta de b", Guid.NewGuid().ToString());
    }

    [Fact]
    public void Principal_tools_use_the_normal_permission_pipeline_and_host_owned_sender()
    {
        var hub = Hub(); hub.Heartbeat(Peer("a")); hub.Heartbeat(Peer("b"));
        var catalog = new FakeCatalog();
        foreach (var tool in AgentCommunicationTool.Create(hub, Peer("a"))) catalog.Add(tool);
        var events = new List<DomainEventPayload>();
        var runtime = ToolRuntime.For(catalog, new ScriptedPermissionPolicy([]).WithModeDefaults(RunMode.Plan),
            payload => { events.Add(payload); return VoidBox.Instance; });
        var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("agents.send"), "peer-call", "{\"target\":\"b\",\"text\":\"Resultado verificado\"}");
        var outcome = runtime.Run(call, new ToolPreparationContext(_root, DateTimeOffset.UtcNow), new ToolExecutionContext(_root), false,
            TestContext.Current.CancellationToken);
        Assert.True(outcome.Succeeded);
        Assert.Equal("a", Assert.Single(hub.ReadPeer("b", "session-b").Messages).SenderInstance);
        var denied = ToolRuntime.For(catalog, ScriptedPermissionPolicy.WithTool("agents.send", PermissionDecision.Deny), _ => VoidBox.Instance)
            .Run(new ValidatedToolCall(ToolCallId.New(), new ToolId("agents.send"), "denied-peer-call", call.NormalizedArgumentsJson), new ToolPreparationContext(_root, DateTimeOffset.UtcNow), new ToolExecutionContext(_root), false,
                TestContext.Current.CancellationToken);
        Assert.False(denied.Succeeded); Assert.Single(hub.Read().Deliveries);
        Assert.All(new[] { "agents.list", "agents.send", "agents.read" }, name => Assert.Equal(ModelToolCapability.PlanProposal, ModelCapabilityBoundary.CoreTools[name]));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
