using System.Security.Cryptography;
using System.Text;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

public sealed class CredentialKeyMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-key-migration-" + Guid.NewGuid().ToString("N"));
    private string StorePath => Path.Combine(_root, "credentials.ini");
    private string KeyPath => Path.Combine(_root, "credentials.key");
    private const string FixtureSecret = "fixture-credential-value-not-a-real-key";

    [Fact]
    public void New_store_uses_native_wrapping_key_and_reopens_without_an_adjacent_key_file()
    {
        var native = new KeyStore();
        var store = new FileCredentialStore(StorePath, native);
        store.Save("fixture", FixtureSecret, TestContext.Current.CancellationToken);

        Assert.Equal(1, native.Saves);
        Assert.NotNull(native.Key);
        Assert.False(File.Exists(KeyPath));
        Assert.DoesNotContain(FixtureSecret, File.ReadAllText(StorePath));
        Assert.Equal(FixtureSecret, new FileCredentialStore(StorePath, native)
            .Load("fixture", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Legacy_key_is_removed_only_after_native_round_trip_and_ciphertext_stays_unchanged()
    {
        var legacy = LegacyStore();
        var before = File.ReadAllBytes(StorePath);
        var native = new KeyStore();

        Assert.Equal(FixtureSecret, new FileCredentialStore(StorePath, native)
            .Load("fixture", TestContext.Current.CancellationToken));

        Assert.Equal(legacy, native.Key);
        Assert.False(File.Exists(KeyPath));
        Assert.Equal(before, File.ReadAllBytes(StorePath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Failed_native_store_or_verification_preserves_the_original_key_and_credentials(bool throws)
    {
        var legacy = LegacyStore();
        var before = File.ReadAllBytes(StorePath);
        var native = new KeyStore { FailSave = throws, DiscardSave = !throws };

        if (throws)
            Assert.Throws<IOException>(() => new FileCredentialStore(StorePath, native)
                .Load("fixture", TestContext.Current.CancellationToken));
        else
            Assert.Throws<CryptographicException>(() => new FileCredentialStore(StorePath, native)
                .Load("fixture", TestContext.Current.CancellationToken));

        Assert.Equal(legacy, File.ReadAllBytes(KeyPath));
        Assert.Equal(before, File.ReadAllBytes(StorePath));
    }

    [Fact]
    public void Conflicting_native_key_does_not_destroy_the_legacy_key_or_update_ciphertext()
    {
        var legacy = LegacyStore();
        var before = File.ReadAllBytes(StorePath);
        var native = new KeyStore { Key = RandomNumberGenerator.GetBytes(32) };

        Assert.Throws<CryptographicException>(() => new FileCredentialStore(StorePath, native)
            .Load("fixture", TestContext.Current.CancellationToken));

        Assert.Equal(legacy, File.ReadAllBytes(KeyPath));
        Assert.Equal(0, native.Saves);
        Assert.Equal(before, File.ReadAllBytes(StorePath));
    }

    [Fact]
    public void Missing_wrapping_key_does_not_replace_it_and_overwrite_existing_credentials()
    {
        _ = LegacyStore();
        File.Delete(KeyPath);
        var before = File.ReadAllBytes(StorePath);
        var native = new KeyStore();
        var store = new FileCredentialStore(StorePath, native);

        Assert.Null(store.Load("fixture", TestContext.Current.CancellationToken));
        Assert.Throws<CryptographicException>(() => store.Save("other", FixtureSecret, TestContext.Current.CancellationToken));
        Assert.Equal(0, native.Saves);
        Assert.Equal(before, File.ReadAllBytes(StorePath));
    }

    [Fact]
    public async Task Concurrent_instances_create_one_key_and_preserve_each_saved_entry()
    {
        var native = new KeyStore();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            new FileCredentialStore(StorePath, native).Save("fixture-" + index, FixtureSecret,
                TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));

        Assert.Equal(1, native.Saves);
        var reopened = new FileCredentialStore(StorePath, native);
        for (var index = 0; index < 8; index++)
            Assert.Equal(FixtureSecret, reopened.Load("fixture-" + index, TestContext.Current.CancellationToken));
    }

    private byte[] LegacyStore()
    {
        Directory.CreateDirectory(_root);
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(KeyPath, key);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var plain = Encoding.UTF8.GetBytes(FixtureSecret);
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        File.WriteAllText(StorePath, "fixture=enc:v2:aesgcm:" + Convert.ToBase64String([.. nonce, .. tag, .. cipher]));
        return key;
    }

    private sealed class KeyStore : ICredentialKeyStore
    {
        public byte[]? Key { get; set; }
        public int Saves { get; private set; }
        public bool FailSave { get; init; }
        public bool DiscardSave { get; init; }
        public byte[]? Load(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Key?.ToArray(); }
        public void Save(byte[] key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailSave) throw new IOException("fixture_keyring_unavailable");
            Saves++;
            if (!DiscardSave) Key = key.ToArray();
        }
        public void Delete(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Key = null; }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
