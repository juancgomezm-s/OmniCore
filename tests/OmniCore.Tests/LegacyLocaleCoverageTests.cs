namespace OmniCore.Tests;

using System.Text.RegularExpressions;
using OmniCore.Client;

public sealed class LegacyLocaleCoverageTests
{
    [Fact]
    public void French_and_Italian_cover_every_legacy_key_with_matching_placeholders()
    {
        var keys = Localization.SpanishKeys.Distinct(StringComparer.Ordinal).ToArray();

        Assert.Equal(keys.Order(StringComparer.Ordinal), LegacyLocaleResources.Keys.Order(StringComparer.Ordinal));
        foreach (var locale in new[] { "fr", "it" })
        {
            foreach (var key in keys)
            {
                Assert.True(LegacyLocaleResources.ContainsKey(key), $"Legacy key missing from catalog: {key}");
                Assert.True(LegacyLocaleResources.TryTranslate(locale, key, out var translated),
                    $"{locale} translation missing: {key}");
                Assert.False(string.IsNullOrWhiteSpace(translated), $"{locale} translation empty: {key}");

                var spanish = Localization.Spanish().Resolve(key);
                var english = Localization.English().Resolve(key);
                var placeholders = Placeholders(spanish);
                Assert.Equal(placeholders, Placeholders(english));
                Assert.Equal(placeholders, Placeholders(translated));
            }
        }

        Assert.False(LegacyLocaleResources.TryTranslate("es", keys[0], out _));
        Assert.False(LegacyLocaleResources.TryTranslate("en", keys[0], out _));
    }

    private static string[] Placeholders(string text) => Regex.Matches(text, "\\{([A-Za-z0-9_]+)\\}")
        .Select(match => match.Groups[1].Value)
        .Order(StringComparer.Ordinal)
        .ToArray();
}
