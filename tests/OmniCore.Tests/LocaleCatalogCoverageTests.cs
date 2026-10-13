using System.Text.RegularExpressions;
using OmniCore.Client;

namespace OmniCore.Tests;

public sealed class LocaleCatalogCoverageTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Standalone_model_profile_controls_have_all_labels_and_options(string locale)
    {
        var labels = ModelPolicyProfileLabels.For(locale);
        foreach (var property in typeof(ModelPolicyProfileLabels).GetProperties()
            .Where(property => property.PropertyType == typeof(string)))
            Assert.False(string.IsNullOrWhiteSpace((string?)property.GetValue(labels)), property.Name);
        foreach (var key in ModelPolicyProfileText.Keys)
        {
            var text = ModelPolicyProfileText.Get(locale, key);
            Assert.False(string.IsNullOrWhiteSpace(text), key);
            Assert.NotEqual(key, text);
        }
        Assert.Equal(ModelPolicyProfileLabels.For("es"), ModelPolicyProfileLabels.For(null));
    }

    [Fact]
    public void Every_first_party_catalog_key_resolves_in_all_locales_without_fallback_or_argument_drift()
    {
        var keys = Localization.SpanishKeys
            .Concat(Localization.RuntimeResourceKeys)
            .Concat(LegacyLocaleResources.Keys)
            .Concat(PreferenceCategoryCatalog.Keys)
            .Concat(PreferencesUiResources.Keys)
            .Concat(AccountsPreferencesResources.Keys)
            .Concat(SessionPreferencesResources.Keys)
            .Concat(ClientCommandResources.Keys)
            .Concat(ShellUiResources.Keys)
            .Concat(UiCompletionResources.Keys)
            .Concat(RuntimeCompletionResources.Keys)
            .Concat(OperationalCommandResources.Keys)
            .Concat(OperationalHostResources.Keys)
            .Concat(PolicyPreferencesResources.Keys)
            .Concat(SecurityPreferencesResources.Keys)
            .Concat(DiagnosticsPreferencesResources.Keys)
            .Concat(ModelCatalogPreferencesResources.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (var key in keys)
        {
            var expectedArguments = Arguments(new Localization("en").Resolve(key));
            foreach (var locale in new[] { "es", "en", "fr", "it" })
            {
                var result = new Localization(locale).ResolveDetailed(key);
                Assert.False(result.UsedFallback, $"{locale}: {key}");
                Assert.Equal(locale, result.ResourceLocale);
                Assert.NotEqual(key, result.Text);
                Assert.False(string.IsNullOrWhiteSpace(result.Text), $"{locale}: {key}");
                Assert.Equal(expectedArguments, Arguments(result.Text));
            }
        }
    }

    private static string[] Arguments(string text) => Regex.Matches(text, @"\{([A-Za-z0-9_]+)(?:[^{}]*)?\}")
        .Select(match => match.Groups[1].Value)
        .Order(StringComparer.Ordinal)
        .ToArray();
}
