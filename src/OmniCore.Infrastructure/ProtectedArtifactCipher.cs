namespace OmniCore.Infrastructure;

using System.Security.Cryptography;
using System.Text;

/// <summary>Local checkpoint protection, with the same platform boundary as the credential
/// store: DPAPI CurrentUser on Windows, an owner-only local AES key elsewhere. No credential
/// or provider configuration is read or modified. Ciphertext is not portable across owners.</summary>
internal sealed class ProtectedArtifactCipher(string dataDirectory)
{
    private const string DpapiPrefix = "omni-protected:v1:dpapi:";
    private const string AesPrefix = "omni-protected:v1:aesgcm:";
    private const int TagSize = 16;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static InvalidDataException Invalid() => new("Protected artifact is invalid.");

    public string Protect(string content, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        var plain = Utf8.GetBytes(content);
        try
        {
            var binding = Binding(purpose);
            if (OperatingSystem.IsWindows())
                return DpapiPrefix + Convert.ToBase64String(ProtectedData.Protect(plain, binding, DataProtectionScope.CurrentUser));
            var key = LoadKey(create: true);
            try
            {
                var packed = new byte[12 + TagSize + plain.Length];
                RandomNumberGenerator.Fill(packed.AsSpan(0, 12));
                using var aes = new AesGcm(key, TagSize);
                aes.Encrypt(packed.AsSpan(0, 12), plain, packed.AsSpan(12 + TagSize), packed.AsSpan(12, TagSize), binding);
                return AesPrefix + Convert.ToBase64String(packed);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        catch (Exception) { throw Invalid(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public string Unprotect(string encrypted, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        byte[]? plain = null;
        try
        {
            var binding = Binding(purpose);
            if (encrypted.StartsWith(DpapiPrefix, StringComparison.Ordinal) && OperatingSystem.IsWindows())
                plain = ProtectedData.Unprotect(Convert.FromBase64String(encrypted[DpapiPrefix.Length..]), binding, DataProtectionScope.CurrentUser);
            else if (encrypted.StartsWith(AesPrefix, StringComparison.Ordinal) && !OperatingSystem.IsWindows())
            {
                var packed = Convert.FromBase64String(encrypted[AesPrefix.Length..]);
                if (packed.Length < 12 + TagSize) throw Invalid();
                var key = LoadKey(create: false);
                try
                {
                    plain = new byte[packed.Length - 12 - TagSize];
                    using var aes = new AesGcm(key, TagSize);
                    aes.Decrypt(packed.AsSpan(0, 12), packed.AsSpan(12 + TagSize), packed.AsSpan(12, TagSize), plain, binding);
                }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
            else throw Invalid();
            return Utf8.GetString(plain);
        }
        catch (Exception) { throw Invalid(); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    private static byte[] Binding(string purpose) => SHA256.HashData(Utf8.GetBytes("OmniCore.ProtectedArtifact.v1\n" + purpose));

    private byte[] LoadKey(bool create)
    {
        if (OperatingSystem.IsWindows()) throw Invalid();
        var path = Path.Combine(dataDirectory, "protected-artifacts.key");
        if (File.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Invalid();
            var permissions = File.GetUnixFileMode(path);
            if ((permissions & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0) throw Invalid();
            var key = File.ReadAllBytes(path);
            if (key.Length != 32) { CryptographicOperations.ZeroMemory(key); throw Invalid(); }
            return key;
        }
        if (!create) throw Invalid();
        Directory.CreateDirectory(dataDirectory);
        // Excludes concurrent initialization and makes a partially written key unreachable
        // to another publisher. Callers already holding the publication lease serialize it.
        var generated = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var file = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            file.Write(generated);
            file.Flush(flushToDisk: true);
            return generated;
        }
        catch (IOException) when (File.Exists(path))
        {
            CryptographicOperations.ZeroMemory(generated);
            throw Invalid(); // No recursion or replacement of a key owned by another writer.
        }
        catch { CryptographicOperations.ZeroMemory(generated); throw; }
    }
}
