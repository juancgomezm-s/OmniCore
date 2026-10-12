namespace OmniCore.Tests;

using OmniCore.Client;
using OmniCore.Protocol;

public sealed class UiCompletionResourcesTests
{
    [Fact]
    public void Supplemental_enum_labels_cover_four_locales_and_preserve_unknown_identifiers()
    {
        Assert.Equal(new[] { "Sesión", "Session", "Session", "Sessione" },
            new[] { "es", "en", "fr", "it" }.Select(locale => UiCompletionResources.EnumLabel("scope", "Session", locale)));
        Assert.Equal(new[] { "Preguntar", "Ask", "Demander", "Chiedi" },
            new[] { "es", "en", "fr", "it" }.Select(locale => UiCompletionResources.EnumLabel("security.decision", "Ask", locale)));
        Assert.Equal(new[] { "Repetible", "Rerunnable", "Réexécutable", "Ripetibile" },
            new[] { "es", "en", "fr", "it" }.Select(locale => UiCompletionResources.EnumLabel("effect", "Rerunnable", locale)));

        Assert.Equal("VendorOnly", UiCompletionResources.EnumLabel("scope", "VendorOnly", "fr"));
        Assert.Equal("UnknownState", UiCompletionResources.EnumLabel("security.decision", "UnknownState", "it"));
        Assert.Equal(UiCompletionResources.Keys.Count, UiCompletionResources.Keys.Distinct(StringComparer.Ordinal).Count());
        foreach (var locale in new[] { "es", "en", "fr", "it" })
        foreach (var key in UiCompletionResources.Keys)
            Assert.NotEqual(key, new Localization(locale).Resolve(key));
    }

    [Fact]
    public void Usage_presentation_localizes_its_human_labels_but_keeps_provider_units()
    {
        Assert.Equal("session 1k jet.", UsagePresentation.Tokens(new TokenTotals(600, 400, 0, 0), "fr"));
        Assert.Equal("crédits $17.42", UsagePresentation.Remaining(
            new Metric<QuotaInfo>(MetricAvailability.Reported,
                new QuotaInfo(QuotaKind.Credits, 17.42m, null, "USD", null), "fixture"), "fr"));
        Assert.Equal("requests 60%", UsagePresentation.Remaining(
            new Metric<QuotaInfo>(MetricAvailability.Reported,
                new QuotaInfo(QuotaKind.RateLimitWindow, 60, 100, "requests", null), "fixture"), "fr"));
    }
}
