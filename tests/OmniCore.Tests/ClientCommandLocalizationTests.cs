using System.Text.Json;
using System.Text.RegularExpressions;
using OmniCore.Client;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class ClientCommandLocalizationTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Command_and_tool_copy_has_no_fallback_and_preserves_placeholders(string locale)
    {
        var text = new Localization(locale);
        foreach (var key in ClientCommandResources.Keys.Concat(AccountsPreferencesResources.Keys)
            .Concat(SessionPreferencesResources.Keys).Distinct(StringComparer.Ordinal))
        {
            var result = text.ResolveDetailed(key);
            Assert.False(result.UsedFallback, key);
            Assert.Equal(locale, result.ResourceLocale);
            Assert.NotEqual(key, result.Text);
            Assert.False(string.IsNullOrWhiteSpace(result.Text));
            Assert.Equal(Placeholders(Localization.Spanish().Resolve(key)), Placeholders(result.Text));
            Assert.Equal(result.Text, text.ResolveWire(key));
        }
        foreach (var key in ClientCommandResources.LegacyKeys) Assert.True(Localization.HasInBoth(key), key);
        foreach (var command in ClientCommandRegistry.Default.Commands)
            Assert.Contains(command.DescriptionKey, ClientCommandResources.Keys);
        foreach (var action in ClientCommandRegistry.Default.Actions)
            Assert.Contains(action.TitleKey, ClientCommandResources.Keys);
    }

    [Theory]
    [InlineData("es", "Comandos disponibles:", "Uso:", "Lee el contenido")]
    [InlineData("en", "Available commands:", "Usage:", "Reads a project")]
    [InlineData("fr", "Commandes disponibles :", "Utilisation :", "Lit le contenu")]
    [InlineData("it", "Comandi disponibili:", "Uso:", "Legge il contenuto")]
    public async Task Help_usage_and_tool_descriptions_are_localized_without_translating_identifiers(
        string locale, string heading, string usage, string description)
    {
        var client = new Client();
        var context = new ClientContext(client, new Localization(locale));
        var handler = new ClientActionHandler();
        var help = await handler.ExecuteAsync(new(ClientActionIds.HelpShow, [], "Typed"), context, TestContext.Current.CancellationToken);
        Assert.StartsWith(heading, help.Text);
        foreach (var command in ClientCommandRegistry.Default.Commands) Assert.Contains("/" + command.Name + " — ", help.Text);
        foreach (var action in new[] { ClientActionIds.SkillsInspect, ClientActionIds.MemorySession })
        {
            var result = await handler.ExecuteAsync(new(action, ["invalid"], "Typed"), context, TestContext.Current.CancellationToken);
            Assert.StartsWith(usage, result.Text);
        }
        var tool = new ToolInspectionRow("filesystem.read", "filesystem.read", "BuiltIn:BuiltIn:Core:core@1", "None", "permission policy", true)
            { Description = "Original third-party text is not translated." };
        Assert.StartsWith(description, ToolInspectionPresentation.Description(tool, context.Text));
        var external = tool with { Source = "Plugin:Project:Project:foreign@1" };
        Assert.EndsWith(external.Description!, ToolInspectionPresentation.Description(external, context.Text));
        Assert.Empty(client.Sent);
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public async Task Localized_skill_success_and_errors_do_not_change_wire_arguments(string locale)
    {
        var client = new Client();
        var context = new ClientContext(client, new Localization(locale));
        var handler = new ClientActionHandler();
        var result = await handler.ExecuteAsync(new(ClientActionIds.SkillsInspect, ["load", "original-id"], "Typed"), context, TestContext.Current.CancellationToken);
        Assert.Equal(context.Text.Resolve("client.skills.activated"), result.Text);
        using var command = JsonDocument.Parse(Assert.Single(client.Sent));
        Assert.Equal("skills.activate", command.RootElement.GetProperty("cmd").GetString());
        Assert.Equal("original-id", command.RootElement.GetProperty("skillId").GetString());
        client.Reject = true;
        result = await handler.ExecuteAsync(new(ClientActionIds.SkillsInspect, ["load", "original-id"], "Typed"), context, TestContext.Current.CancellationToken);
        Assert.Equal(context.Text.Resolve("client.skills.rejected"), result.Text);
        result = await handler.ExecuteAsync(new(ClientActionIds.MemorySession, ["session", "add", "contenu original"], "Typed"), context, TestContext.Current.CancellationToken);
        Assert.Equal(context.Text.Resolve("client.memory.trusted_required"), result.Text);
        Assert.Equal(2, client.Sent.Count); // No untrusted memory command was dispatched.
    }

    private static string[] Placeholders(string text) => Regex.Matches(text, "\\{([^{}]+)\\}")
        .Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();

    private sealed class Client : IOmniClient
    {
        internal List<string> Sent { get; } = [];
        internal bool Reject { get; set; }
        public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken)
        {
            Sent.Add(command.PayloadJson);
            return new(command.MessageId, Reject ? "error" : "ok", null,
                Reject ? RuntimeCommandOutcome.Rejected() : RuntimeCommandOutcome.Accepted());
        }
        public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence) => [];
        public SessionQueryResult? Query(string name, CancellationToken cancellationToken) => null;
    }
}
