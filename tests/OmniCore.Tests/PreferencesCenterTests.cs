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
    [InlineData("fr", "mémoire", "context_memory")]
    [InlineData("it", "barra laterale", "interface")]
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

    [Fact]
    public void Local_engine_argument_editor_roundtrips_literal_argv_without_shell_parsing()
    {
        string[] original = ["", "\"\"", "\\\"\\\"", "\\", "C:/models/My model.gguf",
            "a \"quoted\" argument", "--model", "{port}"];
        var encoded = TuiApp.EncodeLocalArguments(original);
        Assert.Equal(original, TuiApp.DecodeLocalArguments(encoded));
    }
}
