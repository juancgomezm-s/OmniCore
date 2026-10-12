using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class HumanCommandAliasTests
{
    [Fact]
    public void Every_spelling_is_unambiguous_reserved_and_resolves_to_its_canonical_command()
    {
        foreach (var entry in HumanCommandAliases.Entries)
            foreach (var locale in new[] { "es", "en", "fr", "it" })
            {
                var name = HumanCommandAliases.DisplayName(entry.Canonical, locale);
                Assert.Equal(entry.Canonical, HumanCommandAliases.CanonicalName(name));
                Assert.Equal(entry.Canonical, HumanCommandAliases.CanonicalName(name.ToUpperInvariant()));
                Assert.Equal(name is not ("explain" or "orq-auth"), CommandRegistry.IsReserved(name));
                Assert.True(CommandLineParser.TryParse("/" + name, out var invocation));
                Assert.Equal(entry.Canonical, invocation!.Name);
            }
    }

    [Theory]
    [InlineData("/OUTILS", "tools")]
    [InlineData("/préférences", "preferences")]
    [InlineData("/preferences", "preferences")]
    [InlineData("/competenze", "skills")]
    [InlineData("/memoria", "memory")]
    public void Slash_aliases_preserve_free_arguments_and_machine_ids(string input, string name)
    {
        Assert.True(CommandLineParser.TryParseArguments(new[] { input, "C:\\plan\\tareas.txt", "tools", "", "{\"mode\":\"plan\"}" }, out var invocation));
        Assert.Equal(name, invocation!.Name);
        Assert.Equal(new[] { "C:\\plan\\tareas.txt", "tools", "", "{\"mode\":\"plan\"}" }, invocation.Arguments);
    }

    [Fact]
    public void Only_finite_grammar_positions_are_localized()
    {
        Assert.True(CommandLineParser.TryParse("/memoria sesion agregar 'liberar memoria'", out var invocation));
        Assert.Equal(new[] { "session", "add", "liberar memoria" }, invocation!.Arguments);
        Assert.True(CommandLineParser.TryParse("/habilidades cargar 'liberar'", out invocation));
        Assert.Equal(new[] { "load", "liberar" }, invocation!.Arguments);
        Assert.True(CommandLineParser.TryParse("/myextension sesion agregar 'liberar memoria'", out invocation));
        Assert.Equal(new[] { "sesion", "agregar", "liberar memoria" }, invocation!.Arguments);
        Assert.Equal(new[] { "model", "policy", "show", "modelo" }, HumanCommandAliases.NormalizeCli(new[] { "modelo", "politica", "mostrar", "modelo" }));
        Assert.Equal(new[] { "ask", "modelo", "politica" }, HumanCommandAliases.NormalizeCli(new[] { "preguntar", "modelo", "politica" }));
        Assert.Equal(new[] { "memory", "--", "sesion", "agregar" }, HumanCommandAliases.NormalizeCli(new[] { "memoria", "--", "sesion", "agregar" }));
    }

    [Fact]
    public void Head_normalization_does_not_rewrite_prompt_quotes_paths_or_whitespace()
    {
        Assert.Equal("/act  \"C:\\espacio\\plan.txt\"\n -- /herramientas", HumanCommandAliases.NormalizeSlashHead("/actuar  \"C:\\espacio\\plan.txt\"\n -- /herramientas"));
        Assert.Equal("revisa /herramientas", HumanCommandAliases.NormalizeSlashHead("revisa /herramientas"));
        Assert.Equal("/custom:outils foo", HumanCommandAliases.NormalizeSlashHead("/custom:outils foo"));
        Assert.Equal("/sidebar settings  'usuario archivo'", HumanCommandAliases.NormalizeSlashSyntax("/panel ajustes  'usuario archivo'"));
        Assert.Equal("/act  'activar memoria'", HumanCommandAliases.NormalizeSlashSyntax("/actuar  'activar memoria'"));
        Assert.Equal("/reasoning default high", HumanCommandAliases.NormalizeSlashSyntax("/ragionamento predefinito alto"));
    }

    [Fact]
    public void Aliases_use_the_same_authorized_client_actions()
    {
        foreach (var command in ClientCommandRegistry.Default.Commands)
            foreach (var locale in new[] { "es", "en", "fr", "it" })
            {
                var alias = HumanCommandAliases.DisplayName(command.Name, locale);
                Assert.True(ClientCommandRegistry.Default.TryResolve(new(alias, ["literal"], "Typed"), out var action));
                Assert.Equal(command.ActionId, action!.ActionId);
                Assert.Equal(new[] { "literal" }, action.Arguments);
                Assert.Equal("Typed", action.Origin);
            }
    }

    [Fact]
    public void Extension_cannot_shadow_localized_builtin_and_failed_registration_is_atomic()
    {
        foreach (var alias in new[] { "herramientas", "outils", "strumenti", "PREFERENCES", "EXPLICAR" })
        {
            var registry = new CommandRegistry();
            var source = new ComponentSource(SourceKind.Extension, ScopeLevel.Project, TrustLevel.Project, "sample", "1");
            var handler = new Handler(new("sample:demo", "demo", [alias], CommandKind.Prompt, "demo", [], source));
            Assert.Throws<InvalidOperationException>(() => registry.Register(handler));
            Assert.Throws<InvalidOperationException>(() => registry.Resolve("demo"));
            Assert.Throws<InvalidOperationException>(() => registry.Resolve("sample:demo"));
        }
    }

    private sealed class Handler(CommandDescriptor descriptor) : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = descriptor;
        public ValueTask<HostCommandOutcome> HandleAsync(IReadOnlyList<string> arguments, CommandContext context,
            CancellationToken cancellationToken) => ValueTask.FromResult<HostCommandOutcome>(new PromptCommandRequested("text", "test"));
    }

    [Theory]
    [InlineData("--idioma")]
    [InlineData("--langue")]
    [InlineData("--lingua")]
    [InlineData("--locale")]
    public void Locale_flag_aliases_preserve_child_arguments_after_separator(string option)
    {
        var result = OmniCore.Cli.LocaleArgumentParser.Parse(["help", option, "fr", "--", option, "it"]);
        Assert.Equal("fr", result.ExplicitLocale);
        Assert.Equal(new[] { "help", "--", option, "it" }, result.Arguments);
        Assert.Equal("it", OmniCore.Cli.LocaleArgumentParser.Parse([option + "=it", "help"]).ExplicitLocale);
        Assert.True(OmniCore.Cli.LocaleArgumentParser.Parse([option]).HasMissingLocaleValue);
    }
}
