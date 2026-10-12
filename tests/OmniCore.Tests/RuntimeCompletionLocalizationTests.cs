using OmniCore.Cli;
using OmniCore.Client;

namespace OmniCore.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class RuntimeCompletionLocalizationTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Runtime_completion_catalog_is_complete_for_each_supported_locale(string locale)
    {
        var localization = new Localization(locale);
        Assert.NotEmpty(RuntimeCompletionResources.Keys);
        foreach (var key in RuntimeCompletionResources.Keys)
        {
            var result = localization.ResolveDetailed(key);
            Assert.Equal(locale, result.ResourceLocale);
            Assert.False(result.UsedFallback);
            Assert.NotEqual(key, result.Text);
        }
    }

    [Theory]
    [InlineData("es", "Tokens", "exacta", "permitido", "mensaje del usuario")]
    [InlineData("en", "Tokens", "exact", "allowed", "user message")]
    [InlineData("fr", "Tokens", "exacte", "autorisé", "message utilisateur")]
    [InlineData("it", "Token", "esatta", "consentito", "messaggio utente")]
    public void Plain_diagnostic_translates_known_status_labels_and_keeps_ids(string locale,
        string tokenLabel, string accuracyLabel, string decisionLabel, string userMessageLabel)
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            CliApp.SetLocaleForTests(locale);
            Console.SetOut(output);
            CliApp.RenderDiagnostic("context", """
                {"snapshot":{"fingerprint":"fp-123","tokenCount":2,"tokenBudget":10,"tokenAccuracy":"exact",
                 "items":[{"kind":"userMessage","contributor":"fixture-contributor","tokens":2}],
                 "diagnostics":[{"itemId":"item-456","decision":"allowed","reason":"retained"}]}}
                """);
        }
        finally
        {
            Console.SetOut(original);
            CliApp.SetLocaleForTests("es");
        }

        Assert.Contains(tokenLabel + ": 2/10 (" + accuracyLabel + ")", output.ToString());
        Assert.Contains(userMessageLabel + " · fixture-contributor", output.ToString());
        Assert.Contains("item-456 · " + decisionLabel, output.ToString());
        Assert.Contains("fp-123", output.ToString());
        Assert.Contains("fixture-contributor", output.ToString());
        Assert.Contains("retained", output.ToString());
    }

    [Theory]
    [InlineData("es", "lista")]
    [InlineData("en", "ready")]
    [InlineData("fr", "prête")]
    [InlineData("it", "pronta")]
    public void Extension_diagnostic_translates_known_status_and_wraps_external_detail(string locale, string ready)
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            CliApp.SetLocaleForTests(locale);
            Console.SetOut(output);
            CliApp.RenderDiagnostic("extensions", """
                {"extensions":[{"id":"fixture-extension","version":"1.2.3","status":"Ready","diagnostic":"fixture diagnostic"}]}
                """);
        }
        finally
        {
            Console.SetOut(original);
            CliApp.SetLocaleForTests("es");
        }

        Assert.Contains("fixture-extension@1.2.3 · " + ready, output.ToString());
        Assert.Contains("fixture diagnostic", output.ToString());
        Assert.DoesNotContain("cli.diagnostic", output.ToString());
    }

    [Theory]
    [InlineData("es", "manifiesto no válido", "API incompatible")]
    [InlineData("en", "invalid manifest", "incompatible API")]
    [InlineData("fr", "manifeste invalide", "API incompatible")]
    [InlineData("it", "manifest non valido", "API incompatibile")]
    public void Extension_diagnostic_translates_all_known_discovery_errors(string locale,
        string invalidManifest, string incompatibleApi)
    {
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            CliApp.SetLocaleForTests(locale);
            Console.SetOut(output);
            CliApp.RenderDiagnostic("extensions", """
                {"extensions":[{"status":"InvalidManifest"},{"status":"IncompatibleApi"}]}
                """);
        }
        finally
        {
            Console.SetOut(original);
            CliApp.SetLocaleForTests("es");
        }

        Assert.Contains(invalidManifest, output.ToString());
        Assert.Contains(incompatibleApi, output.ToString());
    }

    [Theory]
    [InlineData("es", "origen:", "Diagnóstico:")]
    [InlineData("en", "source:", "Diagnostic:")]
    [InlineData("fr", "source :", "Diagnostic :")]
    [InlineData("it", "origine:", "Diagnostica:")]
    public void Cli_ack_and_command_origin_wrap_external_copy_in_selected_locale(string locale,
        string originLabel, string diagnosticLabel)
    {
        try
        {
            CliApp.SetLocaleForTests(locale);
            Assert.Contains(originLabel + " fixture-origin", CliApp.FormatCommandOrigin("fixture-origin"));
            Assert.Contains(diagnosticLabel + " external detail",
                CliApp.FormatCommandAckError("fixture-command", "external detail"));
        }
        finally { CliApp.SetLocaleForTests("es"); }
    }
}
