namespace OmniCore.Tests;

using OmniCore.Client;
using OmniCore.Cli;
using OmniCore.Host;
using OmniCore.Infrastructure;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class CliLocaleRuntimeIntegrationTests
{
    [Fact]
    public async Task Default_invocation_opens_tui_interactively_and_exits_cleanly_without_a_terminal()
    {
        var tuiLaunched = false;
        var interactiveCode = await CliApp.RunDefaultInvocationAsync(true, () =>
        {
            tuiLaunched = true;
            return Task.FromResult(17);
        });
        Assert.Equal(17, interactiveCode);
        Assert.True(tuiLaunched);

        var expectedFallback = new Localization(CliApp.ActiveEffectiveLocale)
            .Resolve("cli.tui.interactive_required");
        var output = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            var nonInteractiveCode = await CliApp.RunDefaultInvocationAsync(false, () =>
            {
                Assert.Fail("The TUI must not start when the terminal is redirected.");
                return Task.FromResult(-1);
            });
            Assert.Equal(2, nonInteractiveCode);
        }
        finally { Console.SetError(output); }

        Assert.Equal(expectedFallback, captured.ToString().Trim());

        var localeOnly = LocaleArgumentParser.Parse(["--locale", "fr"]);
        Assert.Empty(localeOnly.Arguments);
        Assert.Equal("fr", localeOnly.ExplicitLocale);
    }

    [Fact]
    public async Task Cli_startup_resolves_cli_environment_user_and_default_and_updates_plain_localization()
    {
        using var fixture = new Fixture();
        var previousDirectory = Environment.CurrentDirectory;
        var priorData = Environment.GetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable);
        var priorConfig = Environment.GetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable);
        var priorLocale = Environment.GetEnvironmentVariable("OMNI_LOCALE");
        OmniCliRuntime? runtime = null;
        OmniCliRuntime? previousRuntime = null;
        try
        {
            Environment.CurrentDirectory = fixture.Workspace;
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, fixture.Data);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, fixture.Config);
            File.WriteAllText(Path.Combine(fixture.Config, "settings.yaml"), "ui: { locale: it }\n");
            Environment.SetEnvironmentVariable("OMNI_LOCALE", "en");
            runtime = OmniCliRuntime.Create(fixture.Workspace);
            previousRuntime = CliApp.UseRuntimeForTests(runtime);
            var explicitHelp = await RunAsync("--locale", "fr-CA", "--help");
            Assert.Equal(0, explicitHelp.Code);
            Assert.Contains("omni : moteur d’agents local-first", explicitHelp.Output, StringComparison.Ordinal);
            Assert.Contains("interface interactive (TUI)", explicitHelp.Output, StringComparison.Ordinal);
            AssertLocale(runtime, "fr", "fr", LocaleSelectionSource.CommandLine, "outil");
            CliApp.UpdateLocaleFromPreferences("it");
            AssertLocale(runtime, "fr", "fr", LocaleSelectionSource.CommandLine, "outil");

            await RunAsync("--help");
            AssertLocale(runtime, "en", "en", LocaleSelectionSource.Environment, "tool");
            CliApp.UpdateLocaleFromPreferences("fr");
            AssertLocale(runtime, "en", "en", LocaleSelectionSource.Environment, "tool");

            Environment.SetEnvironmentVariable("OMNI_LOCALE", null);
            await RunAsync("--help");
            AssertLocale(runtime, "it", "it", LocaleSelectionSource.UserSetting, "strumento");
            CliApp.UpdateLocaleFromPreferences("fr");
            AssertLocale(runtime, "it", "fr", LocaleSelectionSource.UserSetting, "outil");
            CliApp.UpdateLocaleFromPreferences("it"); // Cancelling the preview restores the initial value.
            AssertLocale(runtime, "it", "it", LocaleSelectionSource.UserSetting, "strumento");
            Assert.Equal("it", new UserLocaleConfiguration(OmniHost.CreatePlatformPaths()).Read().Locale);

            File.Delete(Path.Combine(fixture.Config, "settings.yaml"));
            await RunAsync("--help");
            AssertLocale(runtime, "es", "es", LocaleSelectionSource.Default, "herramienta");
            CliApp.UpdateLocaleFromPreferences("fr");
            AssertLocale(runtime, "es", "fr", LocaleSelectionSource.Default, "outil");
        }
        finally
        {
            if (previousRuntime is not null) CliApp.UseRuntimeForTests(previousRuntime);
            runtime?.Dispose();
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, priorData);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, priorConfig);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", priorLocale);
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    [Fact]
    public async Task Missing_locale_value_returns_error_before_dispatching_a_mutating_command()
    {
        using var fixture = new Fixture();
        var previousDirectory = Environment.CurrentDirectory;
        var priorData = Environment.GetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable);
        var priorConfig = Environment.GetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable);
        var priorLocale = Environment.GetEnvironmentVariable("OMNI_LOCALE");
        OmniCliRuntime? runtime = null;
        OmniCliRuntime? previousRuntime = null;
        try
        {
            Environment.CurrentDirectory = fixture.Workspace;
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, fixture.Data);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, fixture.Config);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", "en");
            File.WriteAllText(Path.Combine(fixture.Config, "settings.yaml"), "ui: { locale: it }\n");
            runtime = OmniCliRuntime.Create(fixture.Workspace);
            previousRuntime = CliApp.UseRuntimeForTests(runtime);

            var result = await RunAsync("trust", "--locale");

            Assert.Equal(2, result.Code);
            Assert.Contains("was not run", result.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(fixture.Data, "trust.yaml")));
            Assert.Equal("en", runtime.UiLocale);
        }
        finally
        {
            if (previousRuntime is not null) CliApp.UseRuntimeForTests(previousRuntime);
            runtime?.Dispose();
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, priorData);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, priorConfig);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", priorLocale);
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    private static void AssertLocale(OmniCliRuntime runtime, string startupLocale, string effectiveLocale,
        LocaleSelectionSource source,
        string expectedToolLabel)
    {
        Assert.Equal(effectiveLocale, runtime.UiLocale);
        Assert.Equal(effectiveLocale, CliApp.ActiveEffectiveLocale);
        Assert.Equal((startupLocale, source), (CliApp.ActiveLocaleSelection.Locale, CliApp.ActiveLocaleSelection.Source));
        Assert.Equal(expectedToolLabel, runtime.Localize!("plain.role.tool", new Dictionary<string, string>()));

        var output = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            var state = ClientState.Empty();
            var withTool = new ClientState(state.Header,
                new ConversationModel([new ConversationBlock("tool", ConversationRole.Tool, "ok", "tool")]),
                state.Sidebar, state.Composer, state.StatusLine, state.Overlays, state.Connection);
            new PlainRenderer(runtime.UiLocale).Render(withTool);
        }
        finally { Console.SetOut(output); }
        Assert.Contains("[" + expectedToolLabel + "] ok", captured.ToString(), StringComparison.Ordinal);
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
    {
        var output = Console.Out;
        var error = Console.Error;
        using var capturedOutput = new StringWriter();
        using var capturedError = new StringWriter();
        Console.SetOut(capturedOutput);
        Console.SetError(capturedError);
        try
        {
            var code = await CliApp.RunAsync(args);
            return (code, capturedOutput.ToString(), capturedError.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "omnicore-cli-locale-" + Guid.NewGuid().ToString("N"));
        public string Workspace { get; }
        public string Data { get; }
        public string Config { get; }

        public Fixture()
        {
            Workspace = Path.Combine(_root, "workspace");
            Data = Path.Combine(_root, "data");
            Config = Path.Combine(_root, "config");
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(Config);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
