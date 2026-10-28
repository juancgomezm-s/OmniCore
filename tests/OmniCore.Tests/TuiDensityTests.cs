using OmniCore.Cli;
using OmniCore.Client;

namespace OmniCore.Tests;

public sealed class TuiDensityTests
{
    [Fact]
    public void Density_store_is_opt_in_and_can_restore_normal_without_touching_other_settings()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-density-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var unrelated = Path.Combine(root, "animations.json");
        File.WriteAllText(unrelated, "original");
        try
        {
            var path = Path.Combine(root, "view.sidebar-density");
            Assert.False(new TuiDensityStore(path).Read());
            new TuiDensityStore(path).Save(true);
            Assert.True(new TuiDensityStore(path).Read());
            new TuiDensityStore(path).Save(false);
            Assert.False(new TuiDensityStore(path).Read());
            Assert.Equal("original", File.ReadAllText(unrelated));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("COMPACT")]
    [InlineData("compact compact compact compact compact")]
    public void Unknown_or_oversized_settings_preserve_the_existing_normal_layout(string value)
    {
        var path = Path.GetTempFileName();
        try { File.WriteAllText(path, value); Assert.False(new TuiDensityStore(path).Read()); }
        finally { File.Delete(path); }
    }

}
