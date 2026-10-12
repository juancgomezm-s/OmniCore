using OmniCore.Cli;
using OmniCore.Client;
using OmniCore.Host;

namespace OmniCore.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class CliExplainHelpTests
{
    [Fact]
    public async Task Help_and_catalog_distinguish_plain_context_report_from_model_prompt_in_all_locales()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-cli-explain-help-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);

        var priorData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var priorConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var priorLocale = Environment.GetEnvironmentVariable("OMNI_LOCALE");
        var priorOut = Console.Out;
        OmniCliRuntime? priorRuntime = null;
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", config);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", null);
            priorRuntime = CliApp.UseRuntimeForTests(OmniCliRuntime.Create(workspace));

            foreach (var locale in new[] { "es", "en", "fr", "it" })
            {
                using var output = new StringWriter();
                Console.SetOut(output);
                Assert.Equal(0, await CliApp.RunAsync(["--locale", locale, "help"]));

                var help = output.ToString();
                var text = new Localization(locale);
                Assert.Contains(text.Resolve("cli.usage.ask"), help, StringComparison.Ordinal);
                Assert.Contains(text.Resolve("cli.usage.explain"), help, StringComparison.Ordinal);
                Assert.Contains(text.Resolve("commands.explain.help"), help, StringComparison.Ordinal);

                var catalog = CommandCatalogPresentation.Read(
                    "{\"commands\":[{\"id\":\"core:explain\",\"name\":\"explain\",\"description\":\"raw\",\"source\":\"BuiltIn:core@1\",\"enabled\":true,\"arguments\":[\"path\"]}]}",
                    text);
                var explain = Assert.Single(catalog, row => row.Id == "core:explain");
                Assert.Equal(text.Resolve("catalog.explain"), explain.Description);
                Assert.False(explain.ExternalDescription);
            }
        }
        finally
        {
            Console.SetOut(priorOut);
            if (priorRuntime is not null) CliApp.UseRuntimeForTests(priorRuntime);
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", priorData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", priorConfig);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", priorLocale);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
