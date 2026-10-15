using System.Text.Json;
using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>ADR-0024 §3 / ADR-0025 §1: los ClientCommands resuelven a una ClientAction que ejecuta un handler.</summary>
public sealed class ClientActionTests
{
    private sealed class FakeClient : IOmniClient, ITrustedUserActionClient
    {
        internal Dictionary<string, string?> Queries { get; } = new(StringComparer.Ordinal);
        internal List<string> Queried { get; } = [];
        internal List<string> Sent { get; } = [];
        internal List<WireEnvelope> Events { get; } = [];

        public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken)
        {
            Sent.Add(command.PayloadJson);
            return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
        }

        public CommandAck SendUserAction(WireEnvelope command, CancellationToken cancellationToken) => Send(command, cancellationToken);

        public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence) => Events;

        public SessionQueryResult? Query(string name, CancellationToken cancellationToken)
        {
            Queried.Add(name);
            return Queries.TryGetValue(name, out var json) && json is not null ? new SessionQueryResult(name, json) : null;
        }
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
            ["extensions"] = ClientActionIds.ExtensionsInspect,
            ["hooks"] = ClientActionIds.HooksInspect,
            ["openrouter"] = ClientActionIds.OpenRouterInspect, ["mcp"] = ClientActionIds.McpInspect,
            ["commands"] = ClientActionIds.CommandsInspect,
            ["skills"] = ClientActionIds.SkillsInspect,
            ["memory"] = ClientActionIds.MemorySession,
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
            + "{\"id\":\"core:explain\",\"name\":\"explain\",\"source\":\"BuiltIn:core@1\",\"description\":\"Explain repository content\"}]}}";

        var help = Run("help", Context(client)).Text;

        Assert.StartsWith("Comandos disponibles:", help, StringComparison.Ordinal);
        foreach (var command in ClientCommandRegistry.Default.Commands)
        {
            var name = HumanCommandAliases.DisplayName(command.Name, "es");
            Assert.Contains("/" + name + (name == command.Name ? "" : " (/" + command.Name + ")") + " — ", help);
        }
        Assert.Contains("/explicar (/explain) — " + Localization.Spanish().Resolve("catalog.explain"), help);
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
        client.Queries["commands"] = "{\"catalog\":{\"commands\":[{\"id\":\"core:explain\",\"enabled\":true}]}}";
        client.Queries["skills"] = "{\"workspaceTrusted\":true,\"skills\":[{\"id\":\"progress.compile\",\"scope\":\"Project\",\"trust\":\"Trusted\",\"state\":\"Discovered\",\"budgetTokens\":1200,\"modelInvocable\":true}]}";
        Assert.Equal("{\"plan\":true}", Run("plan", Context(client)).Text);
        Assert.Equal("{\"tools\":[]}", Run("tools", Context(client)).Text);
        Assert.Contains("core:explain", Run("commands", Context(client)).Text);
        Assert.Contains("progress.compile", Run("skills", Context(client)).Text);
        var truncated = Run("context", Context(client)).Text;
        Assert.Equal(1801, truncated.Length);
        Assert.EndsWith("…", truncated, StringComparison.Ordinal);

        client.Queries["agents"] = "null";
        Assert.Equal("Sin sesión", Run("tasks", Context(client)).Text);
    }

    [Fact]
    public async Task Hooks_slash_command_and_localized_alias_query_the_host_without_model_or_process_calls()
    {
        var client = new FakeClient();
        const string diagnostic = "{\"workspaceTrusted\":true,\"hooks\":[{\"id\":\"sample.hook.0\",\"state\":\"UnsupportedPoint\",\"enabled\":false}]}";
        client.Queries["hooks"] = diagnostic;
        Assert.Equal(diagnostic, Run("hooks", Context(client)).Text);

        Assert.True(ClientCommandRegistry.Default.TryResolve(new CommandInvocation("ganchos", [], "Typed"),
            out var alias));
        Assert.Equal(ClientActionIds.HooksInspect, alias!.ActionId);
        var response = await new ClientActionHandler().ExecuteAsync(alias, Context(client), CancellationToken.None);
        Assert.Equal(diagnostic, response.Text);
        Assert.Empty(client.Sent);
        Assert.Equal(new[] { "hooks", "hooks" }, client.Queried);
    }

    [Fact]
    public void Tools_action_preserves_the_complete_catalog_instead_of_cutting_json_at_1800_characters()
    {
        var client = new FakeClient();
        var entries = Enumerable.Range(0, 40).Select(index => new
        {
            visibleName = $"tool.{index:00}", toolId = $"tool.{index:00}",
            source = "BuiltIn:BuiltIn:Core:core@1", effectClass = "None",
            decision = "permission policy", visible = true,
        });
        var json = System.Text.Json.JsonSerializer.Serialize(new { mode = "Plan", tools = entries });
        Assert.True(json.Length > 1800);
        client.Queries["tools"] = json;
        var result = Run("tools", Context(client));
        Assert.Equal(json, result.Text);
        Assert.Equal(40, result.Tools!.Tools.Count);
        Assert.Equal("tool.39", result.Tools.Tools[^1].ToolId);
        Assert.Empty(client.Sent);
        Assert.False(result.ExitRequested);
    }

    [Fact]
    public void Tools_action_handles_missing_or_invalid_catalog_without_claiming_authority()
    {
        var client = new FakeClient();
        Assert.Null(Run("tools", Context(client)).Tools);
        client.Queries["tools"] = "{\"tools\":[{\"visibleName\":\"broken\"}]}";
        var es = Run("tools", Context(client));
        Assert.Null(es.Tools);
        Assert.Contains("No se pudo interpretar", es.Text);
        Assert.Contains("Could not read", Run("tools", Context(client, locale: "en")).Text);
        Assert.Empty(client.Sent);
    }

    [Fact]
    public void Skills_command_lists_and_sends_explicit_session_activation_or_release()
    {
        var client = new FakeClient();
        client.Queries["skills"] = "{\"session\":null,\"workspaceTrusted\":true,\"skills\":[{\"id\":\"progress.compile\",\"scope\":\"Project\",\"trust\":\"Trusted\",\"state\":\"Activated\",\"budgetTokens\":1200,\"modelInvocable\":true}]}";

        Assert.Contains("progress.compile", Run("skills", Context(client)).Text);
        var loaded = Run("skills", Context(client), "load", "progress.compile");
        Assert.Contains(Context(client).Text.Resolve("client.skills.state.Activated"), loaded.Text);
        using (var command = JsonDocument.Parse(Assert.Single(client.Sent)))
        {
            Assert.Equal("skills.activate", command.RootElement.GetProperty("cmd").GetString());
            Assert.Equal("progress.compile", command.RootElement.GetProperty("skillId").GetString());
        }

        Run("skills", Context(client), "release", "progress.compile");
        using var released = JsonDocument.Parse(client.Sent[1]);
        Assert.Equal("skills.release", released.RootElement.GetProperty("cmd").GetString());
        Assert.Equal(Context(client).Text.Resolve("client.skills.usage"), Run("skills", Context(client), "other", "x").Text);
    }

    [Fact]
    public void Skills_sources_and_external_memory_controls_use_the_source_query_without_shadowing_agents()
    {
        var client = new FakeClient();
        client.Queries["agent.sources"] = "{\"workspaceTrusted\":true,\"externalMemoryAllowed\":false,\"sources\":[]}";
        var inventory = Run("skills", Context(client), "sources");
        Assert.Contains("Fuentes locales", inventory.Text);
        Assert.Contains("Ask", inventory.Text);
        Assert.Equal(new[] { "agent.sources" }, client.Queried);
        Assert.Empty(client.Sent);
        Assert.False(ClientCommandRegistry.Default.TryResolve(new CommandInvocation("agents", [], "Typed"), out _));
    }

    [Fact]
    public void Memory_command_queries_and_uses_trusted_user_actions_for_changes()
    {
        var client = new FakeClient();
        client.Queries["memory.session"] = "{\"session\":null,\"workspaceTrusted\":false,\"records\":[{\"id\":\"fixture-memory\",\"kind\":\"Fact\",\"content\":\"fixture\",\"source\":\"User\"}],\"candidates\":[]}";

        Assert.Contains("fixture", Run("memory", Context(client), "session").Text);
        Assert.Contains("fixture", Run("memory", Context(client), "session", "add", "keep the test focused").Text);
        using (var add = JsonDocument.Parse(client.Sent[0]))
        {
            Assert.Equal("memory.session.add", add.RootElement.GetProperty("cmd").GetString());
            Assert.Equal("keep the test focused", add.RootElement.GetProperty("content").GetString());
        }

        Run("memory", Context(client), "session", "forget", "abc123");
        using var forget = JsonDocument.Parse(client.Sent[1]);
        Assert.Equal("memory.session.forget", forget.RootElement.GetProperty("cmd").GetString());
        Assert.Equal("abc123", forget.RootElement.GetProperty("memoryId").GetString());
        Assert.Equal(Context(client).Text.Resolve("client.memory.usage"), Run("memory", Context(client), "workspace").Text);
    }

    [Fact]
    public void External_memory_cli_defaults_to_project_scope_and_requires_global_scope_explicitly()
    {
        var client = new FakeClient();
        client.Queries["agent.sources"] = "{\"workspaceTrusted\":true,\"projectRootTrusted\":true,\"externalMemoryAllowed\":false,\"externalProjectMemoryAllowed\":false,\"sources\":[]}";

        Run("memory", Context(client), "external", "allow");
        using (var project = JsonDocument.Parse(client.Sent[0]))
        {
            Assert.Equal("external.memory.consent", project.RootElement.GetProperty("cmd").GetString());
            Assert.Equal("allow", project.RootElement.GetProperty("decision").GetString());
            Assert.Equal("project", project.RootElement.GetProperty("scope").GetString());
        }

        Run("memory", Context(client), "external", "revoke", "global");
        using var global = JsonDocument.Parse(client.Sent[1]);
        Assert.Equal("revoke", global.RootElement.GetProperty("decision").GetString());
        Assert.Equal("global", global.RootElement.GetProperty("scope").GetString());
        Assert.Contains("[project|global]", Context(client).Text.Resolve("client.agent_sources.memory.controls.scoped"));
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
