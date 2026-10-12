using System.Text.RegularExpressions;
using OmniCore.Client;

namespace OmniCore.Tests;

public sealed class PreferencesLocaleContractTests
{
    [Fact]
    public void All_migrated_catalogs_resolve_without_fallback_and_preserve_placeholders()
    {
        var keys = Localization.SpanishKeys.Concat(Localization.RuntimeResourceKeys)
            .Concat(ShellUiResources.Keys).Concat(UiCompletionResources.Keys)
            .Concat(OperationalCommandResources.Keys).Concat(OperationalHostResources.Keys)
            .Concat(RuntimeCompletionResources.Keys).Concat(PolicyPreferencesResources.Keys)
            .Concat(SecurityPreferencesResources.Keys).Concat(DiagnosticsPreferencesResources.Keys)
            .Concat(ModelCatalogPreferencesResources.Keys).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var key in keys)
        {
            var expected = Placeholders(new Localization("en").Resolve(key));
            foreach (var locale in new[] { "es", "en", "fr", "it" })
            {
                var result = new Localization(locale).ResolveDetailed(key);
                Assert.False(result.UsedFallback, $"{locale}: {key}");
                Assert.Equal(locale, result.ResourceLocale);
                Assert.NotEqual(key, result.Text);
                Assert.True(expected.SequenceEqual(Placeholders(result.Text)),
                    $"{locale}: {key}: expected [{string.Join(",", expected)}], actual [{string.Join(",", Placeholders(result.Text))}]");
            }
        }
    }

    private static string[] Placeholders(string text) => Regex.Matches(text, @"\{([A-Za-z0-9_]+)(?:[^{}]*)?\}")
        .Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();
}
