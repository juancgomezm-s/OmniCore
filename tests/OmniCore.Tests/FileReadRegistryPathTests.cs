namespace OmniCore.Tests;

using OmniCore.Abstractions;
using Xunit;

public class FileReadRegistryPathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "omni-frr-root");

    [Fact]
    public void RelativeDotPrefixedAndAbsoluteSpellingsAreTheSameFile()
    {
        var reg = new FileReadRegistry(Root);
        reg.RecordRead("src/a.cs", "v1");

        Assert.True(reg.Matches("./src/a.cs", "v1"));
        Assert.True(reg.Matches("src/x/../a.cs", "v1"));
        Assert.True(reg.Matches(Path.Combine(Root, "src", "a.cs"), "v1"));
        Assert.Equal(1, reg.Size());
    }

    [Fact]
    public void RelativeSpellingsUnifyWithoutRoot()
    {
        var reg = new FileReadRegistry();
        reg.RecordRead("./src//a.cs", "v1");
        Assert.True(reg.HasRead("src/a.cs"));
        Assert.True(reg.HasRead("src\\b\\..\\a.cs"));
    }

    [Fact]
    public void DifferentFileIsNotRead()
    {
        var reg = new FileReadRegistry(Root);
        reg.RecordRead("src/a.cs", "v1");
        Assert.False(reg.HasRead("src/b.cs"));
        Assert.False(reg.Matches("src/b.cs", "v1"));
        Assert.False(reg.Matches("src/a.cs", "v2"));
    }

    [Fact]
    public void CaseInsensitiveOnlyOnCaseInsensitivePlatforms()
    {
        var reg = new FileReadRegistry(Root);
        reg.RecordRead("src/a.cs", "v1");
        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        Assert.Equal(expected, reg.Matches("SRC/A.cs", "v1"));
    }
}
