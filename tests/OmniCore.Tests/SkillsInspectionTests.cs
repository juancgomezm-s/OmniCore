namespace OmniCore.Tests;

using OmniCore.Client;

public sealed class SkillsInspectionTests
{
    [Theory]
    [InlineData("es", "Fuentes locales", "espera tu decisión")]
    [InlineData("en", "Local agent sources", "awaiting your decision")]
    [InlineData("fr", "Sources locales", "en attente")]
    [InlineData("it", "Fonti locali", "in attesa")]
    public void External_source_inspection_is_localized_and_does_not_display_source_bodies(string locale,
        string title, string awaiting)
    {
        const string json = """{"workspaceTrusted":true,"externalMemoryAllowed":false,"sources":[{"kind":"Memory","adapter":"Claude","scope":"Project","path":"~/project/MEMORY.md","sha256":"","state":"awaiting-consent","diagnostic":"Ask decision required","requiresConsent":true,"content":"must not be disclosed"}]}""";
        var formatted = ExternalAgentSourcesPresentation.Format(json, new Localization(locale));
        Assert.Contains(title, formatted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(awaiting, formatted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("~/project/MEMORY.md", formatted);
        Assert.DoesNotContain("must not be disclosed", formatted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("es", "disponibles", "proyecto", "activada")]
    [InlineData("en", "Available", "project", "activated")]
    [InlineData("fr", "disponibles", "projet", "activée")]
    [InlineData("it", "disponibili", "progetto", "attivata")]
    public void Skills_inspection_uses_complete_scope_and_lifecycle_resources(string locale,
        string title, string scope, string state)
    {
        const string json = """{"workspaceTrusted":true,"session":"session","skills":[{"id":"compile.safe","scope":"Project","trust":"Project","state":"Activated","budgetTokens":512,"modelInvocable":true,"diagnostic":null}]}""";
        var formatted = SkillsInspectionPresentation.Format(json, new Localization(locale));
        Assert.Contains(title, formatted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(scope, formatted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(state, formatted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compile.safe", formatted);
        Assert.DoesNotContain("Activated", formatted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Skill_validator_gate_copy_is_complete_in_supported_locales(string locale)
    {
        var copy = new Localization(locale);
        foreach (var key in new[] { "cli.runtime.skill.validator.name", "cli.runtime.skill.validator.passed",
                     "cli.runtime.skill.validator.failed", "cli.runtime.extension.validator.name",
                     "cli.runtime.extension.validator.result" })
        {
            var resolved = copy.ResolveDetailed(key);
            Assert.False(resolved.UsedFallback);
            Assert.Equal(locale, resolved.ResourceLocale);
            Assert.NotEqual(key, resolved.Text);
        }
    }

    [Theory]
    [InlineData("es", "selección automática")]
    [InlineData("en", "automatic selection")]
    [InlineData("fr", "sélection automatique")]
    [InlineData("it", "selezione automatica")]
    public void Skills_inspection_shows_automatic_lifecycle_origin(string locale, string expected)
    {
        var json = """{"skills":[{"id":"compile.safe","scope":"User","trust":"Trusted","state":"Released","budgetTokens":128,"modelInvocable":false,"diagnostic":"released","activationOrigin":"Automatic"}]}""";
        Assert.Contains(expected, SkillsInspectionPresentation.Format(json, new Localization(locale)),
            StringComparison.OrdinalIgnoreCase);
    }
}
