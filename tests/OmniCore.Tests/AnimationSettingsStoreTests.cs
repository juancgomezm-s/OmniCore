using OmniCore.Client;
using OmniCore.Cli;

namespace OmniCore.Tests;

public sealed class AnimationSettingsStoreTests
{
    [Fact]
    public void Saves_only_dedicated_file_and_restores_all_preferences()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-animations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var protectedPath = Path.Combine(root, "providers.yaml");
            File.WriteAllText(protectedPath, "do not touch");
            var path = Path.Combine(root, "tui-animations.json");
            var preferences = new AnimationPreferences(false, true, "conveyor", "omega-pulse", "orbit-dots", "block-wave", 300);
            var store = new AnimationSettingsStore(path);
            Assert.Equal(new AnimationPreferences(), store.Load());
            Assert.False(File.Exists(path));
            Assert.True(store.Save(preferences));
            Assert.Equal(preferences, new AnimationSettingsStore(path).Load());
            Assert.Equal("do not touch", File.ReadAllText(protectedPath));
            Assert.Equal(2, Directory.GetFiles(root).Length);
            Assert.False(store.Save(preferences with { Answer = "script.sh" }));
            Assert.Equal(preferences, store.Load());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("broken json")]
    [InlineData("null")]
    [InlineData("{\"reasoning\":\"script.sh\"}")]
    [InlineData("{\"intervalMilliseconds\":1}")]
    [InlineData("{\"provider\":\"not-a-preference\"}")]
    public void Invalid_config_is_preserved_and_falls_back_without_crashing(string text)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-animations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "tui-animations.json");
            File.WriteAllText(path, text);
            var store = new AnimationSettingsStore(path);
            Assert.Equal(new AnimationPreferences(), store.Load());
            Assert.NotNull(store.Notice);
            Assert.Equal(text, File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Save_failure_does_not_overwrite_another_resource()
    {
        var path = Path.Combine(Path.GetTempPath(), "omni-animations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var store = new AnimationSettingsStore(path);
            Assert.False(store.Save(new()));
            Assert.Equal("save-failed", store.Notice);
            Assert.True(Directory.Exists(path));
        }
        finally { Directory.Delete(path, true); }
    }
}
