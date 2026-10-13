using OmniCore.Client;

namespace OmniCore.Tests;

public sealed class ToolsInspectionTests
{
    [Fact]
    public void Preserves_reported_metadata_hidden_tools_unicode_and_unknown_future_fields()
    {
        var snapshot = ToolsInspectionJson.Decode("""
            {"mode":"Plan","future":{},"tools":[
              {"visibleName":"lectura.ñ","toolId":"read","source":"Core:acción@1","description":"Lee información: acción, pingüino, ¿sí?","effectClass":"None","decision":"permission policy","visible":true},
              {"visibleName":"escritura","toolId":"write","source":"plugin@2","effectClass":"Write","decision":"reject:PLAN mode","visible":false,"future":1}]}
            """)!;
        Assert.Equal("Plan", snapshot.Mode);
        Assert.Equal(2, snapshot.Tools.Count);
        Assert.Equal("lectura.ñ", snapshot.Tools[0].VisibleName);
        Assert.Equal("Core:acción@1", snapshot.Tools[0].Source);
        Assert.Equal("Lee información: acción, pingüino, ¿sí?", snapshot.Tools[0].Description);
        Assert.Null(snapshot.Tools[1].Description); // Compatible with catalogs from older hosts.
        Assert.False(snapshot.Tools[1].Visible);
        Assert.Equal("reject:PLAN mode", snapshot.Tools[1].Decision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"tools\":null}")]
    [InlineData("{\"tools\":[{}]}")]
    [InlineData("{\"tools\":[null]}")]
    [InlineData("{\"mode\":42,\"tools\":[]}")]
    [InlineData("{\"tools\":[{\"visibleName\":\"x\",\"toolId\":\"x\",\"source\":\"core\",\"effectClass\":\"None\",\"decision\":\"policy\",\"visible\":\"true\"}]}")]
    public void Invalid_catalog_is_rejected_without_dropping_rows_or_inventing_metadata(string? json) =>
        Assert.Null(ToolsInspectionJson.Decode(json));

    [Fact]
    public void Empty_catalog_and_absent_mode_remain_distinct_from_invalid_data()
    {
        var snapshot = ToolsInspectionJson.Decode("{\"tools\":[]}")!;
        Assert.Null(snapshot.Mode);
        Assert.Empty(snapshot.Tools);
    }

    [Fact]
    public void Built_in_descriptions_are_localized_without_changing_original_metadata()
    {
        var row = new ToolInspectionRow("read alias", "filesystem.read", "BuiltIn:BuiltIn:Core:core@1",
            "None", "permission policy", true) { Description = "Original model-facing description" };
        Assert.Equal("Lee una página acotada de líneas de un archivo del proyecto sin modificarlo. Indica offset desde la línea 1 y limit hasta 500; muestra numeración, continuación y hash del archivo completo.",
            ToolInspectionPresentation.Description(row, Localization.Spanish()));
        Assert.Equal("Reads a bounded page of project file lines without changing it. Set offset from line 1 and limit up to 500; the result shows line numbers, continuation and the full file hash.",
            ToolInspectionPresentation.Description(row, Localization.English()));
        Assert.Equal("Original model-facing description", row.Description);
        foreach (var id in new[] { "user.ask", "filesystem.read", "filesystem.list", "search.text", "reference.resolve",
            "plan.propose", "mode.propose", "filesystem.write", "filesystem.patch", "process.exec", "shell.exec",
            "artifact.read", "core.agents.mailbox.receive", "worktree.integrate", "tool.search" })
            Assert.True(Localization.HasInBoth("client.tool." + id + ".description"), id);
    }

    [Fact]
    public void Unknown_and_external_tools_keep_their_own_description_or_explicit_missing_state()
    {
        var external = new ToolInspectionRow("filesystem.read", "filesystem.read", "Plugin:Workspace:Untrusted:extra@1",
            "None", "future:decision", false) { Description = "Custom behavior" };
        Assert.Equal("Descripción del proveedor (idioma original): Custom behavior",
            ToolInspectionPresentation.Description(external, Localization.Spanish()));
        Assert.Contains("Custom behavior", ToolInspectionPresentation.Description(external with
            { Source = "BuiltIn:BuiltIn:Core:core@2" }, Localization.Spanish()));
        Assert.Contains("Custom behavior", ToolInspectionPresentation.Description(external with
            { Source = "BuiltIn:BuiltIn:Core:core@1", ToolId = "future.tool" }, Localization.Spanish()));
        Assert.Equal("El proveedor no informó qué hace esta herramienta.",
            ToolInspectionPresentation.Description(external with { Description = null }, Localization.Spanish()));
        Assert.Contains("no reconocida", ToolInspectionPresentation.Decision(external, Localization.Spanish()));
    }

    [Theory]
    [InlineData("reject:PLAN mode", "Bloqueada en Plan")]
    [InlineData("reject:ModelCapabilityBoundary", "Bloqueada por los límites")]
    [InlineData("ask:ModelCapabilityBoundary", "exigen confirmación")]
    [InlineData("ask:permission policy", "exige confirmación")]
    [InlineData("permission policy", "se evalúan al solicitar")]
    public void Policy_explanations_do_not_turn_diagnostic_visibility_into_authorization(string decision, string expected)
    {
        var row = new ToolInspectionRow("tool", "tool", "source", "None", decision, true);
        Assert.Contains(expected, ToolInspectionPresentation.Decision(row, Localization.Spanish()));
        Assert.Equal(decision, row.Decision);
    }
}
