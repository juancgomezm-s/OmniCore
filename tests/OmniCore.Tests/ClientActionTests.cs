using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>ADR-0024 §3 / ADR-0025 §1: los ClientCommands resuelven a una ClientAction que ejecuta un handler.</summary>
public sealed class ClientActionTests
{
    private sealed class FakeClient : IOmniClient
    {
        internal Dictionary<string, string?> Queries { get; } = new(StringComparer.Ordinal);
        internal List<string> Sent { get; } = [];
        internal List<WireEnvelope> Events { get; } = [];

        public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken)
        {
            Sent.Add(command.PayloadJson);
            return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
        }

        public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence) => Events;

        public SessionQueryResult? Query(string name, CancellationToken cancellationToken) =>
            Queries.TryGetValue(name, out var json) && json is not null ? new SessionQueryResult(name, json) : null;
    }

    private static ClientContext Context(FakeClient client, Func<string>? permissions = null, string locale = "es") =>
        new(client, locale == "en" ? Localization.English() : Localization.Spanish(), permissions);

    private static ClientActionInvocation Action(string commandName, params string[] arguments)
    {
        Assert.True(ClientCommandRegistry.Default.TryResolve(new CommandInvocation(commandName, arguments, "Typed"), out var action));
        return action!;
    }

    private static ClientActionResult Run(string commandName, ClientContext context, params string[] arguments) =>
        new ClientActionHandler().ExecuteAsync(Action(commandName, arguments), context, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    [Fact]
    public void The_registry_resolves_every_m1_client_command_to_its_action_and_nothing_else()
    {
        var expected = new Dictionary<string, string>
        {
            ["help"] = ClientActionIds.HelpShow, ["exit"] = ClientActionIds.AppExit, ["plan"] = ClientActionIds.ViewPlan,
            ["tasks"] = ClientActionIds.ViewTasks, ["events"] = ClientActionIds.ViewEvents,
            ["context"] = ClientActionIds.ContextInspect, ["tools"] = ClientActionIds.ToolsInspect,
            ["permissions"] = ClientActionIds.PermissionsList, ["interrupt"] = ClientActionIds.RunInterrupt,
            ["cancel"] = ClientActionIds.RunCancel,
        };
        Assert.Equal(expected.Count, ClientCommandRegistry.Default.Commands.Count);
        foreach (var (name, actionId) in expected)
        {
            Assert.True(ClientCommandRegistry.Default.TryResolve(new CommandInvocation(name.ToUpperInvariant(), ["x"], "Palette"), out var action));
            Assert.Equal(actionId, action!.ActionId);
            Assert.Equal(new[] { "x" }, action.Arguments);
            Assert.Equal("Palette", action.Origin);
        }
        Assert.False(ClientCommandRegistry.Default.TryResolve(new CommandInvocation("explain", [], "Typed"), out _));
        Assert.Equal(expected.Values.Order(StringComparer.Ordinal),
            ClientCommandRegistry.Default.Actions.Select(item => item.Id).Order(StringComparer.Ordinal));
        Assert.Throws<InvalidOperationException>(() => new ClientCommandRegistry(
            [new("plan", ClientActionIds.ViewPlan, "k"), new("PLAN", ClientActionIds.ViewTasks, "k")]));
    }

    [Fact]
    public void Every_client_command_name_is_reserved_in_the_host_so_no_other_source_can_register_it()
    {
        foreach (var command in ClientCommandRegistry.Default.Commands)
            Assert.True(CommandRegistry.IsReserved(command.Name), command.Name + " must be reserved in the Host");
    }

    [Fact]
    public void Every_command_and_action_has_complete_es_and_en_text()
    {
        foreach (var locale in new[] { Localization.Spanish(), Localization.English() })
        {
            foreach (var command in ClientCommandRegistry.Default.Commands)
                Assert.NotEqual(command.DescriptionKey, locale.Resolve(command.DescriptionKey));
            foreach (var action in ClientCommandRegistry.Default.Actions)
                Assert.NotEqual(action.TitleKey, locale.Resolve(action.TitleKey));
        }
    }

    [Fact]
    public void Help_lists_the_client_commands_and_the_hosts_without_duplicating_reserved_names()
    {
        var client = new FakeClient();
        client.Queries["commands"] = "{\"catalog\":{\"commands\":[{\"name\":\"plan\",\"description\":\"dup\"},"
            + "{\"name\":\"explain\",\"description\":\"Explain repository content\"}]}}";

        var help = Run("help", Context(client)).Text;

        Assert.StartsWith("Comandos disponibles:", help, StringComparison.Ordinal);
        foreach (var command in ClientCommandRegistry.Default.Commands) Assert.Contains("/" + command.Name + " — ", help);
        Assert.Contains("/explain — Explain repository content", help);
        Assert.DoesNotContain("dup", help);
        Assert.Contains("se envía al modelo", help);
        Assert.Contains("shows this help", Run("help", Context(client, locale: "en")).Text);
    }

    [Fact]
    public void Exit_asks_the_client_to_close_and_everything_else_does_not()
    {
        var client = new FakeClient();
        Assert.True(Run("exit", Context(client)).ExitRequested);
        Assert.False(Run("help", Context(client)).ExitRequested);
    }

    [Fact]
    public void View_actions_query_the_server_and_degrade_without_data()
    {
        var client = new FakeClient();
        Assert.Equal("Sin datos disponibles.", Run("plan", Context(client)).Text);
        client.Queries["workingState"] = "{\"plan\":true}";
        client.Queries["context"] = new string('x', 2500);
        client.Queries["tools"] = "{\"tools\":[]}";
        Assert.Equal("{\"plan\":true}", Run("plan", Context(client)).Text);
        Assert.Equal("{\"tools\":[]}", Run("tools", Context(client)).Text);
        var truncated = Run("context", Context(client)).Text;
        Assert.Equal(1801, truncated.Length);
        Assert.EndsWith("…", truncated, StringComparison.Ordinal);

        client.Queries["agents"] = "null";
        Assert.Equal("Sin sesión", Run("tasks", Context(client)).Text);
    }

    [Fact]
    public void Events_shows_only_the_most_recent_journal_events_with_their_count()
    {
        var client = new FakeClient();
        Assert.Equal("Todavía no hay eventos.", Run("events", Context(client)).Text);
        for (var index = 1; index <= 25; index++)
            client.Events.Add(WireEnvelope.Event(Ids.NewV7(), "{" + JsonObj.Field("type", "turn.started")
                + "," + JsonObj.Field("seq", index.ToString()) + "}"));
        client.Events.Add(WireEnvelope.Command(Ids.NewV7(), "{}")); // no es un evento: se ignora

        var lines = Run("events", Context(client)).Text.Split('\n');

        Assert.Equal("Últimos 20 de 25 eventos:", lines[0]);
        Assert.Equal(21, lines.Length);
        Assert.Equal("6  turn.started", lines[1]);
        Assert.Equal("25  turn.started", lines[^1]);
    }

    [Fact]
    public void Permissions_uses_the_clients_listing_or_says_it_cannot()
    {
        var client = new FakeClient();
        Assert.Contains("omni permissions list", Run("permissions", Context(client)).Text);
        Assert.Equal("grants aquí", Run("permissions", Context(client, () => "grants aquí")).Text);
    }

    [Fact]
    public void Interrupt_and_cancel_send_the_same_wire_commands_as_any_other_surface()
    {
        var client = new FakeClient();

        Assert.Contains("run.interrupt", Run("interrupt", Context(client)).Text);
        Assert.Contains("run.cancel", Run("cancel", Context(client)).Text);

        Assert.Equal(new[] { "{\"cmd\":\"run.interrupt\"}", "{\"cmd\":\"run.cancel\"}" }, client.Sent);
    }
}
