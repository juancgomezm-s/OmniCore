namespace OmniCore.Tests;

using OmniCore.Cli;
using OmniCore.Client;
using OmniCore.Protocol;

public sealed class PreferencesCenterTests
{
    [Fact]
    public void Every_preferences_center_resource_is_translated_in_all_supported_locales()
    {
        Assert.NotEmpty(PreferencesUiResources.Keys);
        Assert.Equal(PreferencesUiResources.Keys.Count,
            PreferencesUiResources.Keys.Distinct(StringComparer.Ordinal).Count());
        foreach (var locale in UiLocaleCodes.Supported)
        {
            var localization = new Localization(locale);
            foreach (var key in PreferencesUiResources.Keys)
            {
                var result = localization.ResolveDetailed(key);
                Assert.False(result.UsedFallback, $"{locale} fell back for {key}");
                Assert.Equal(locale, result.ResourceLocale);
                Assert.NotEqual(key, result.Text);
            }
        }
    }

    [Theory]
    [InlineData("es", "local", "local_engines")]
    [InlineData("en", "permissions", "tools_security")]
    [InlineData("fr", "mémoire", "context_sessions")]
    [InlineData("it", "barra laterale", "general")]
    public void Category_search_matches_localized_titles_and_four_language_synonyms(
        string locale, string query, string expectedCategory)
    {
        var categories = PreferenceCategoryCatalog.Resolve(new Localization(locale));
        Assert.Equal(expectedCategory, Assert.Single(PreferencesPresentation.Search(categories, query, locale)).Id);
    }

    [Theory]
    [InlineData(80, false)]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(120, true)]
    public void Preference_layout_switches_at_the_documented_terminal_width(int width, bool wide) =>
        Assert.Equal(wide, PreferencesPresentation.IsWideLayout(width));

    [Theory]
    [InlineData("es", "ejecutable", "local_engines")]
    [InlineData("en", "output limit", "models_execution")]
    [InlineData("fr", "délai", "local_engines")]
    [InlineData("it", "orchestrazione", "models_execution")]
    public void Search_finds_existing_configuration_fields(string locale, string term, string expected)
    {
        var categories = PreferenceCategoryCatalog.Resolve(new Localization(locale));
        Assert.Equal(expected, Assert.Single(PreferencesPresentation.Search(categories, term, locale)).Id);
    }

    [Fact]
    public void Context_and_memory_are_separate_from_diagnostics_in_every_locale()
    {
        foreach (var locale in UiLocaleCodes.Supported)
        {
            var categories = PreferenceCategoryCatalog.Resolve(new Localization(locale));
            var context = Assert.Single(categories, category => category.Id == "context_sessions");
            Assert.False(new Localization(locale).ResolveDetailed("preferences.category.context_sessions.title").UsedFallback);
            Assert.NotEmpty(context.Description);
            Assert.NotEmpty(context.Help);
            Assert.DoesNotContain(PreferencesPresentation.Search(categories, locale switch
                { "en" => "memory", "fr" => "mémoire", "it" => "memoria", _ => "memoria" }, locale),
                category => category.Id == "diagnostics_data");
        }
    }

    [Fact]
    public void Local_engine_argument_editor_roundtrips_literal_argv_without_shell_parsing()
    {
        string[] original = ["", "\"\"", "\\\"\\\"", "\\", "C:/models/My model.gguf",
            "a \"quoted\" argument", "--model", "{port}"];
        var encoded = TuiApp.EncodeLocalArguments(original);
        Assert.Equal(original, TuiApp.DecodeLocalArguments(encoded));
    }
}
