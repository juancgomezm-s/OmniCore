using OmniCore.Cli;

namespace OmniCore.Tests;

public sealed class TuiPresentationPolishTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(30)]
    public void Long_path_is_bounded_and_keeps_its_tail(int width)
    {
        const string path = "C:\\Users\\user\\source\\repos\\OmniCore";
        var abbreviated = TuiApp.AbbreviatePath(path, width);
        Assert.Equal(width, abbreviated.Length);
        Assert.Contains("…", abbreviated);
        if (width > 1) Assert.EndsWith("e", abbreviated);
        if (width >= 12) Assert.EndsWith("OmniCore", abbreviated);
    }

    [Fact]
    public void Short_path_is_not_changed() => Assert.Equal("OmniCore", TuiApp.AbbreviatePath("OmniCore", 30));
}
