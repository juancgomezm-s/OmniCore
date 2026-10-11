using OmniCore.Client;

namespace OmniCore.Tests;

public sealed class ToolsInspectionTests
{
    [Fact]
    public void Preserves_reported_metadata_hidden_tools_unicode_and_unknown_future_fields()
    {
        var snapshot = ToolsInspectionJson.Decode("""
            {"mode":"Plan","future":{},"tools":[
              {"visibleName":"lectura.ñ","toolId":"read","source":"Core:acción@1","effectClass":"None","decision":"permission policy","visible":true},
              {"visibleName":"escritura","toolId":"write","source":"plugin@2","effectClass":"Write","decision":"reject:PLAN mode","visible":false,"future":1}]}
            """)!;
        Assert.Equal("Plan", snapshot.Mode);
        Assert.Equal(2, snapshot.Tools.Count);
        Assert.Equal("lectura.ñ", snapshot.Tools[0].VisibleName);
        Assert.Equal("Core:acción@1", snapshot.Tools[0].Source);
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
}
