namespace OmniCore.Tests;

using OmniCore.Client;

public sealed class SessionPreferencesPresentationTests
{
    [Fact]
    public void Session_preferences_resources_have_real_four_language_coverage_for_every_key()
    {
        var keys = SessionPreferencesResources.Keys;
        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            Assert.True(SessionPreferencesResources.ContainsKey(key));
            var texts = new List<string>();
            foreach (var locale in new[] { "es", "en", "fr", "it" })
            {
                Assert.True(SessionPreferencesResources.TryTranslate(locale, key, out var text), $"{locale}: {key}");
                Assert.False(string.IsNullOrWhiteSpace(text));
                texts.Add(text);
            }
            Assert.Contains(texts, value => !string.IsNullOrWhiteSpace(value));
        }
    }

    [Fact]
    public void Context_presentation_is_typed_and_keeps_unknown_or_estimated_metrics_honest()
    {
        var data = SessionPreferencesPresentation.Decode("context", """
            {"snapshot":{"snapshotId":"snap-1","fingerprint":"fp","tokenCount":17,
            "tokenAccuracy":"Estimated","tokenBudget":null,"overflowed":false,
            "items":[{"id":"context:fixture","kind":"Memory","tokens":17,"source":"Session",
            "scope":"Session","sensitive":true,"refs":["memory:fixture"],"content":"never expose"}],
            "diagnostics":[{"itemId":"ignored","decision":"Omitted","reason":"budget"}],
            "privateSecret":"not a UI field"}}
            """);

        Assert.True(data.Available);
        var snapshot = Assert.IsType<ContextSnapshotPresentation>(data.Context);
        Assert.Equal(17, snapshot.TokenCount);
        Assert.Equal("Estimated", snapshot.TokenAccuracy);
        Assert.Null(snapshot.TokenBudget);
        Assert.True(Assert.Single(snapshot.Items).Sensitive);
        Assert.Equal("memory:fixture", Assert.Single(snapshot.Items[0].References));
        Assert.Single(snapshot.Diagnostics);
        Assert.DoesNotContain("privateSecret", System.Text.Json.JsonSerializer.Serialize(snapshot));
        Assert.DoesNotContain("never expose", System.Text.Json.JsonSerializer.Serialize(snapshot));
    }

    [Fact]
    public void Memory_presentation_is_allow_listed_and_sanitizes_renderable_text()
    {
        var data = SessionPreferencesPresentation.Decode("memory.session", """
            {"scope":"Session","session":"session-id","records":[{"id":"m1","kind":"Fact",
            "content":"line one\nline two","source":"UserStated","importance":0.7,"confidence":1,
            "createdAt":"2026-10-11T00:00:00Z","tags":["fixture"],"instruction":"do not show"}]}
            """);
        var memory = Assert.Single(data.Memory);
        Assert.Equal("line one\nline two", memory.Content);
        Assert.DoesNotContain("instruction", System.Text.Json.JsonSerializer.Serialize(memory));
        Assert.Equal("line one line two", SessionPreferencesPresentation.SafeText(memory.Content));
    }

    [Fact]
    public void Invalid_or_wrong_shaped_query_json_is_not_presented_as_an_empty_inventory()
    {
        Assert.False(SessionPreferencesPresentation.Decode("memory.session", "not-json").Available);
        Assert.False(SessionPreferencesPresentation.Decode("skills", "{\"skills\":[]}").Available);
        Assert.False(SessionPreferencesPresentation.Decode("extensions", """
            {"workspaceTrusted":false,"session":null,"extensions":[null]}
            """).Available);
        Assert.False(SessionPreferencesPresentation.Decode("context", "{\"snapshot\":{\"items\":\"unknown\",\"diagnostics\":[]}}").Available);
    }
}
