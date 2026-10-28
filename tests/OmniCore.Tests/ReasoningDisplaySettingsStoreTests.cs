namespace OmniCore.Tests;

using OmniCore.Client;

public sealed class ReasoningDisplaySettingsStoreTests
{
    [Fact]
    public void Explicit_visibility_survives_reopening_and_invalid_content_does_not_become_a_request()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-reasoning-display-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "display.reasoning");
        try
        {
            var store = new ReasoningDisplaySettingsStore(path);
            Assert.True(store.Load());
            Assert.False(Directory.Exists(root));
            Assert.True(store.Save(false));
            Assert.False(new ReasoningDisplaySettingsStore(path).Load());
            Assert.True(store.Save(true));
            Assert.True(new ReasoningDisplaySettingsStore(path).Load());
            File.WriteAllText(path, "invalid");
            Assert.True(store.Load());
            Assert.Single(Directory.GetFiles(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
