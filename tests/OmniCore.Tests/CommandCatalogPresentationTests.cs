using OmniCore.Client;

namespace OmniCore.Tests;

public sealed class CommandCatalogPresentationTests
{
    [Theory]
    [InlineData("es", "herramientas")]
    [InlineData("en", "tools")]
    [InlineData("fr", "outils")]
    [InlineData("it", "strumenti")]
    public void Catalog_keeps_canonical_identity_and_localizes_owned_descriptions(string locale, string alias)
    {
        var text = new Localization(locale);
        var rows = CommandCatalogPresentation.Read("""
            {"commands":[{"id":"core:explain","name":"explain","description":"raw","source":"BuiltIn:core@1","enabled":true,"arguments":["path"]},
            {"id":"ext:review","name":"review","description":"original package text","source":"Extension:ext@1","enabled":false,"disabledReason":"external diagnostic","arguments":[],"secret":"NEVER SHOW"}]}
            """, text);
        var tools = Assert.Single(rows, row => row.Name == "tools");
        Assert.Equal(alias, tools.DisplayName);
        var explain = Assert.Single(rows, row => row.Name == "explain");
        Assert.Equal(text.Resolve("catalog.explain"), explain.Description);
        Assert.False(explain.ExternalDescription);
        var external = Assert.Single(rows, row => row.Id == "ext:review");
        Assert.Equal("original package text", external.Description);
        Assert.True(external.ExternalDescription);
        Assert.False(external.Enabled);
        Assert.DoesNotContain("NEVER SHOW", string.Join(" ", rows));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"commands\":null}")]
    public void Malformed_host_catalog_keeps_local_commands_available(string json)
    {
        var rows = CommandCatalogPresentation.Read(json, new("fr"));
        Assert.Contains(rows, row => row.Name == "preferences");
        Assert.Contains(rows, row => row.Name == "tools");
    }

    [Fact]
    public void Actual_host_envelope_uses_nested_catalog_and_preserves_external_translation_ownership()
    {
        var catalog = new OmniCore.Protocol.CommandCatalogSnapshot([
            new("ext:inspect", "inspect", [], "Extension", "Original", [], "Extension:ext@1", true, null)
            { UiDescriptions = new Dictionary<string, string> { ["it"] = "Ispeziona il progetto" } }]);
        var json = "{\"commands\":[\"inspect\"],\"catalog\":" + OmniCore.Protocol.CommandCatalogJson.Encode(catalog) + "}";
        var italian = Assert.Single(CommandCatalogPresentation.Read(json, new("it")), row => row.Id == "ext:inspect");
        Assert.Equal("Ispeziona il progetto", italian.Description);
        Assert.False(italian.ExternalDescription);
        Assert.True(Assert.Single(CommandCatalogPresentation.Read(json, new("fr")), row => row.Id == "ext:inspect").ExternalDescription);
    }
}
