using OmniCore.Client;

namespace OmniCore.Tests;

public sealed class PreferenceInputTests
{
    [Theory]
    [InlineData("es", "1.5")]
    [InlineData("en", "1.5")]
    [InlineData("fr", "1,5")]
    [InlineData("it", "1,5")]
    public void Human_money_uses_locale_without_ambiguous_grouping(string locale, string input)
    {
        Assert.True(PreferenceInput.TryMoney(input, locale, out var value));
        Assert.Equal(1.5m, value);
        Assert.False(PreferenceInput.TryMoney("1,234.5", locale, out _));
        Assert.False(PreferenceInput.TryMoney("-1", locale, out _));
        Assert.True(PreferenceInput.TryMoney("", locale, out value));
        Assert.Null(value);
    }

    [Theory]
    [InlineData("es", "Permitir")]
    [InlineData("en", "Allow")]
    [InlineData("fr", "Autoriser")]
    [InlineData("it", "Consenti")]
    public void Localized_permissions_map_only_to_the_same_canonical_values(string locale, string input)
    {
        Assert.Equal("Allow", PreferenceInput.CanonicalValue("security.decision", input, locale, "Allow", "Ask", "Deny"));
        Assert.Equal("Deny", PreferenceInput.CanonicalValue("security.decision", "deny", locale, "Allow", "Ask", "Deny"));
        Assert.Equal("unexpected", PreferenceInput.CanonicalValue("security.decision", "unexpected", locale, "Allow", "Ask", "Deny"));
    }
}
