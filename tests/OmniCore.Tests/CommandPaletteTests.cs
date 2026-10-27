using OmniCore.Client;

namespace OmniCore.Tests;

public sealed class CommandPaletteTests
{
    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("it")]
    public void Palette_resources_resolve_in_all_locales(string locale)
    {
        var text = new Localization(locale);
        foreach (var key in CommandPaletteResources.Keys)
        {
            var result = text.ResolveDetailed(key);
            Assert.False(result.UsedFallback); Assert.Equal(locale, result.ResourceLocale);
            Assert.NotEqual(key, result.Text);
        }
    }

    [Fact]
    public void Search_is_fuzzy_accent_insensitive_and_keeps_canonical_ids()
    {
        var rows = CommandPalette.Build(null, new("es"), new([]), false);
        Assert.Contains(CommandPalette.Search(rows, "prfrncs"), row => row.Target.Id == "preferences.open");
        Assert.Contains(CommandPalette.Search(rows, "SELECCIONAR MODELO"), row => row.Target.Id == "model.select");
        Assert.Empty(CommandPalette.Search(rows, "zzzzzzzzzzzzzzzzzzzzzzzzzzzz"));
        Assert.Contains(CommandPalette.Search(rows, "run.cancel"), row => !row.Enabled && row.Target.Id == "run.cancel");
    }

    [Fact]
    public void Qualified_choices_keep_availability_independent_of_ambiguous_short_name()
    {
        var rows = CommandPalette.Build("""
            {"catalog":{"commands":[
              {"id":"project:review","name":"review","source":"Project:A@1","enabled":false,"canonicalEnabled":true,"sourceFingerprint":"one"},
              {"id":"user:review","name":"review","source":"User:B@1","enabled":false,"canonicalEnabled":true,"sourceFingerprint":"two"},
              {"id":"extension:blocked","name":"blocked","source":"Extension:C@1","enabled":false,"canonicalEnabled":false,"disabledReason":"reason"}
            ]}}
            """, new("en"), new([]), false);
        var reviews = CommandPalette.Search(rows, "review").Take(2).ToArray();
        Assert.Equal(2, reviews.Length);
        Assert.All(reviews, row => Assert.True(row.Enabled));
        Assert.Equal(2, reviews.Select(row => row.Target.Id).Distinct().Count());
        Assert.False(Assert.Single(rows, row => row.Target.Id == "extension:blocked").Enabled);
        Assert.All(reviews, row => Assert.NotEmpty(row.SourceFingerprint!));
        Assert.All(reviews, row => Assert.Contains(row.Target.Id, row.Title));
    }

    [Fact]
    public void Shortcut_hints_follow_effective_resolution_and_conditions()
    {
        var context = new KeyBindingContext([new("input", true)]);
        var entry = Assert.Single(CommandPalette.Build(null, new("en"), context, false), row => row.Target.Id == "model.select");
        var service = new KeyBindingService([
            new("F3", new(KeyBindingTargetKind.Action, "sidebar.toggle"), Source: KeyBindingSource.User, SourceId: "User"),
            new("F7", new(KeyBindingTargetKind.Command, "context"), "input", Source: KeyBindingSource.User, SourceId: "User")]);
        Assert.Empty(CommandPalette.Bindings(entry, service, context));
        var inspect = Assert.Single(CommandPalette.Build(null, new("en"), context, false), row => row.Target.Id == "context.inspect");
        Assert.Equal("F7", Assert.Single(CommandPalette.Bindings(inspect, service, context)).Definition.Keys);
        Assert.Empty(CommandPalette.Bindings(inspect, service, new([])));
    }
}
