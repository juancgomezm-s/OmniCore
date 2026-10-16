using OmniCore.Client;

namespace OmniCore.Tests;

public sealed class ToolPreferencePresentationTests
{
    private static ToolInspectionRow Tool(string id, string risk = "High", string effect = "NonIdempotent", params string[] tags) =>
        new(id, id, "Extension:User:Trusted:fixture@1", effect, "reject:PLAN mode", false)
        { Tags = tags, Risk = risk, Protection = "Protected" };

    [Fact]
    public void Matches_declared_tags_effect_and_risk_without_turning_diagnostic_visibility_into_permission()
    {
        var source = Tool("filesystem.write", "High", "NonIdempotent", "file", "write");
        var target = Tool("ext.fixture.write", "High", "NonIdempotent", "write", "file");
        Assert.True(ToolPreferencePresentation.IsAlternative(source, target));
        Assert.True(ToolPreferencePresentation.IsAlternative(source, target with { Risk = "Low" }));
        Assert.False(ToolPreferencePresentation.IsAlternative(source, source));
        Assert.False(ToolPreferencePresentation.IsAlternative(source, target with { Risk = "Critical" }));
        Assert.False(ToolPreferencePresentation.IsAlternative(source, target with { Risk = "Future" }));
        Assert.False(ToolPreferencePresentation.IsAlternative(source, target with { Tags = ["process"] }));
        Assert.False(ToolPreferencePresentation.IsAlternative(source, target with { EffectClass = "None" }));
        Assert.False(ToolPreferencePresentation.IsAlternative(source, target with { Source = "BuiltIn:BuiltIn:Core:core@1" }));
        Assert.False(ToolPreferencePresentation.IsAlternative(source, target with { Tags = null }));
        Assert.False(ToolPreferencePresentation.IsAlternative(source with { Risk = null }, target));
        Assert.False(source.Visible); Assert.False(target.Visible);
        Assert.Equal("reject:PLAN mode", target.Decision);
    }

    [Fact]
    public void Decoder_preserves_optional_compatibility_metadata_and_never_invents_it_for_old_hosts()
    {
        var row = ToolsInspectionJson.Decode("""
            {"tools":[{"visibleName":"read","toolId":"read","source":"core","effectClass":"None",
             "decision":"permission policy","visible":true,"risk":"Low","protection":"Protected","tags":["file","read"]}]}
            """)!.Tools.Single();
        Assert.Equal("Low", row.Risk); Assert.Equal("Protected", row.Protection);
        Assert.Equal(new[] { "file", "read" }, row.Tags);
        var old = ToolsInspectionJson.Decode("""
            {"tools":[{"visibleName":"read","toolId":"read","source":"core","effectClass":"None",
             "decision":"permission policy","visible":true}]}
            """)!.Tools.Single();
        Assert.Null(old.Risk); Assert.Null(old.Tags); Assert.Null(old.Protection);
        Assert.False(ToolPreferencePresentation.IsAlternative(old, row));
    }
}
