using OmniCore.Host;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>Real libsecret + Secret Service; run inside an isolated, unlocked test keyring session.</summary>
public sealed class LinuxSecretServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-native-keyring-" + Guid.NewGuid().ToString("N"));
    private readonly List<FileCredentialStore> _stores = [];

    [Fact]
    public void Host_factory_uses_native_keyring_and_reopened_profiles_remain_isolated()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "Requires Linux Secret Service.");
        Assert.Equal("1", Environment.GetEnvironmentVariable("OMNICORE_TEST_SECRET_SERVICE"));
        foreach (var name in new[] { "profile-a", "profile-b" })
        {
            var directory = Path.Combine(_root, name);
            Directory.CreateDirectory(directory);
            var store = Assert.IsType<FileCredentialStore>(OmniHost.CreateCredentialStore(directory));
            _stores.Add(store);
            store.Save("fixture", "fixture-native-credential-" + name, TestContext.Current.CancellationToken);
            Assert.False(File.Exists(Path.Combine(directory, "credentials.key")));
        }
        foreach (var name in new[] { "profile-a", "profile-b" })
        {
            var directory = Path.Combine(_root, name);
            var reopened = OmniHost.CreateCredentialStore(directory);
            Assert.Equal("fixture-native-credential-" + name, reopened.Load("fixture", TestContext.Current.CancellationToken));
        }
        _stores[0].Purge();
        Assert.Equal("fixture-native-credential-profile-b", _stores[1].Load("fixture", TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        foreach (var store in _stores) store.Purge();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
