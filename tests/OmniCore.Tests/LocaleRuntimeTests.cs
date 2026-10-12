namespace OmniCore.Tests;

using System.Text.RegularExpressions;
using OmniCore.Cli;
using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Protocol;

public sealed class LocaleRuntimeTests
{
    [Fact]
    public void Locale_parser_removes_only_global_options_before_the_separator_and_preserves_free_arguments()
    {
        var parsed = LocaleArgumentParser.Parse(
            ["ask", "question with spaces", "--locale", "fr-CA", "--", "--locale", "it", "quoted \"text\""]);

        Assert.Equal("fr-CA", parsed.ExplicitLocale);
        Assert.Empty(parsed.Diagnostics);
        Assert.Equal(new[] { "ask", "question with spaces", "--", "--locale", "it", "quoted \"text\"" },
            parsed.Arguments);
    }

    [Fact]
    public void Locale_parser_handles_equals_and_reports_missing_value_without_consuming_other_options()
    {
        var parsed = LocaleArgumentParser.Parse(["doctor", "--locale=it_IT", "--locale", "--json"]);

        Assert.Equal("it_IT", parsed.ExplicitLocale);
        Assert.Equal(new[] { "doctor", "--locale", "--json" }, parsed.Arguments);
        Assert.Equal(["missing"], parsed.Diagnostics);
        Assert.True(parsed.HasMissingLocaleValue);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--other")]
    [InlineData("--")]
    public void Locale_parser_treats_a_following_option_or_separator_as_missing_and_preserves_it(string next)
    {
        var parsed = LocaleArgumentParser.Parse(["doctor", "--locale", next, "--locale", "fr"]);

        Assert.Equal(next == "--" ? null : "fr", parsed.ExplicitLocale);
        Assert.True(parsed.HasMissingLocaleValue);
        var expected = next == "--"
            ? new[] { "doctor", "--locale", "--", "--locale", "fr" }
            : new[] { "doctor", "--locale", next };
        Assert.Equal(expected, parsed.Arguments);
    }

    [Fact]
    public void Locale_parser_preserves_invalid_explicit_values_for_a_localized_diagnostic()
    {
        var parsed = LocaleArgumentParser.Parse(["doctor", "--locale", "pt-BR", "--json"]);

        Assert.Equal("pt-BR", parsed.ExplicitLocale);
        Assert.Equal(new[] { "doctor", "--json" }, parsed.Arguments);
        var selection = LocaleSelectionResolver.Resolve(parsed.ExplicitLocale, "it", "en");
        Assert.Equal("es", selection.Locale);
        Assert.Equal("pt-BR", selection.InvalidValue);
    }

    [Fact]
    public void Shared_runtime_copy_has_real_translations_for_each_supported_locale()
    {
        Assert.NotEmpty(Localization.RuntimeResourceKeys);
        var spanish = new Localization("es");
        foreach (var locale in UiLocaleCodes.Supported)
        {
            var localization = new Localization(locale);
            foreach (var key in Localization.RuntimeResourceKeys)
            {
                var result = localization.ResolveDetailed(key);
                Assert.False(result.UsedFallback, $"{locale} fell back for {key}");
                Assert.Equal(locale, result.ResourceLocale);
                Assert.NotEqual(key, result.Text);
                Assert.Equal(Placeholders(spanish.Resolve(key)), Placeholders(result.Text));
            }
        }

        Assert.NotEqual("ui.locale.error.scope", new Localization("fr").ResolveWire("ui.locale.error.scope"));
    }

    private static string[] Placeholders(string text) => Regex.Matches(text, "\\{([^{}]+)\\}")
        .Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void User_settings_use_canonical_locale_codes_while_process_overrides_accept_regions()
    {
        var canonicalDiagnostics = new List<ConfigDiagnostic>();
        _ = ConfigLoader.ParseSettings("ui: { locale: fr }", canonicalDiagnostics);
        Assert.Empty(canonicalDiagnostics);

        var regionalDiagnostics = new List<ConfigDiagnostic>();
        _ = ConfigLoader.ParseSettings("ui: { locale: fr-CA }", regionalDiagnostics);
        Assert.Contains(regionalDiagnostics, diagnostic => diagnostic.KeyPath == "ui.locale");
        Assert.Contains(regionalDiagnostics, diagnostic => diagnostic.Message.Key == "ui.locale.error.invalid_value");
        Assert.True(UiLocaleCodes.TryNormalize("fr-CA", out var processLocale));
        Assert.Equal("fr", processLocale);
    }
}
