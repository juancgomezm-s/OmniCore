namespace OmniCore.Infrastructure;

using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using OmniCore.Abstractions;

/// <summary>
/// ICredentialStore sobre disco (ADR-0011 §3.1, ADR-0018). Cada valor se cifra antes de tocar
/// el archivo, y el archivo vive en el directorio de datos del usuario, nunca en el repo:
/// <list type="bullet">
/// <item>Windows: DPAPI con ámbito <c>CurrentUser</c> (<c>enc:v2:dpapi:…</c>). Solo el mismo
/// usuario en la misma máquina puede descifrar.</item>
/// <item>Linux: AES-256-GCM con clave de 32 bytes en Secret Service/libsecret y archivo 0600.
/// La clave histórica se migra sólo después de verificar su persistencia nativa.</item>
/// <item>macOS: AES-256-GCM con clave local 0600, provisional hasta Keychain.</item>
/// </list>
/// Un valor que no se puede descifrar (formato antiguo XOR, archivo de otra máquina o
/// manipulado) se trata como ausente: nunca se devuelve el texto cifrado como si fuera la key.
/// Nunca se loguea el valor.
/// </summary>
public sealed class FileCredentialStore : ICredentialStore
{
    private const string DpapiPrefix = "enc:v2:dpapi:";

    private const string AesGcmPrefix = "enc:v2:aesgcm:";

    private const int KeySize = 32;

    private const int NonceSize = 12;

    private const int TagSize = 16;

    /// <summary>Entropía adicional de DPAPI: liga el blob a OmniCore.</summary>
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("OmniCore.FileCredentialStore.v2");

    private static readonly UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _path;

    private readonly string _keyPath;
    private readonly ICredentialKeyStore? _keyStore;

    public FileCredentialStore(string path) : this(path,
        OperatingSystem.IsLinux() ? new LinuxSecretServiceKeyStore(path) : null) { }

    internal FileCredentialStore(string path, ICredentialKeyStore? keyStore)
    {
        _path = path;
        _keyPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", "credentials.key");
        _keyStore = keyStore;
    }

    public void Save(string key, string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length < Secret.MinimumLength)
            throw new OmniCore.Domain.SecretValueTooShortException(Secret.MinimumLength);
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireStoreLease(cancellationToken);
        EnsureFile();

        var map = LoadMap();
        map[key] = Protect(value, cancellationToken);
        WriteMap(map);
    }

    public string? Load(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireStoreLease(cancellationToken);
        EnsureFile();
        var map = LoadMap();
        if (!map.TryGetValue(key, out var stored)) return null;
        var value = Unprotect(stored, cancellationToken);
        if (value is not null)
        {
            if (value.Length < Secret.MinimumLength)
                throw new OmniCore.Domain.SecretValueTooShortException(Secret.MinimumLength);
            SecretRedactorRegistry.Register(value);
        }
        return value;
    }

    public void Delete(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireStoreLease(cancellationToken);
        EnsureFile();
        var map = LoadMap();
        map.Remove(key);
        WriteMap(map);
    }

    /// <summary>Borrado físico del archivo de credenciales (no solo la entrada).</summary>
    public void Purge()
    {
        if (_keyStore is not null)
        {
            using var lease = AcquireStoreLease(CancellationToken.None);
            File.Delete(_path);
            File.Delete(_keyPath);
            _keyStore.Delete(CancellationToken.None);
            return;
        }
        TryDelete(_path);
        TryDelete(_keyPath);
    }

    /// <summary>Ruta del archivo (para que la prueba de cierre lo inspeccione).</summary>
    public string PathValue() => _path;

    private void EnsureFile()
    {
        if (!File.Exists(_path))
        {
            var parent = Path.GetDirectoryName(_path);
            if (parent is not null && parent.Length > 0 && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }

            WriteMap(new Dictionary<string, string>());
        }
    }

    private string Protect(string value, CancellationToken cancellationToken)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        if (OperatingSystem.IsWindows() && _keyStore is null)
        {
            var blob = ProtectedData.Protect(plain, DpapiEntropy, DataProtectionScope.CurrentUser);
            return DpapiPrefix + Convert.ToBase64String(blob);
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        var key = CredentialKey(create: true, cancellationToken)!;
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plain, cipher, tag);
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        var packed = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, NonceSize);
        cipher.CopyTo(packed, NonceSize + TagSize);
        return AesGcmPrefix + Convert.ToBase64String(packed);
    }

    private string? Unprotect(string stored, CancellationToken cancellationToken)
    {
        // Unavailable Secret Service must not become a missing API key or create a replacement key.
        var key = stored.StartsWith(AesGcmPrefix, StringComparison.Ordinal)
            ? CredentialKey(create: false, cancellationToken) : null;
        try
        {
            if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows())
                {
                    return null;
                }

                var blob = Convert.FromBase64String(stored.Substring(DpapiPrefix.Length));
                return Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(blob, DpapiEntropy, DataProtectionScope.CurrentUser));
            }

            if (stored.StartsWith(AesGcmPrefix, StringComparison.Ordinal))
            {
                if (key is null)
                {
                    return null;
                }

                var packed = Convert.FromBase64String(stored.Substring(AesGcmPrefix.Length));
                if (packed.Length < NonceSize + TagSize)
                {
                    return null;
                }

                var nonce = packed.AsSpan(0, NonceSize);
                var tag = packed.AsSpan(NonceSize, TagSize);
                var cipher = packed.AsSpan(NonceSize + TagSize);
                var plain = new byte[cipher.Length];
                using var aes = new AesGcm(key, TagSize);
                aes.Decrypt(nonce, cipher, tag, plain);
                return Encoding.UTF8.GetString(plain);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }

        // Formato desconocido o antiguo (XOR de M2): no se interpreta.
        return null;
    }

    private FileStream? AcquireStoreLease(CancellationToken cancellationToken)
    {
        if (_keyStore is null) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var started = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var options = new FileStreamOptions
                { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;
                return new FileStream(_path + ".keyring.lock", options);
            }
            catch (IOException) when (started.Elapsed < TimeSpan.FromSeconds(10)) { Thread.Sleep(10); }
        }
    }

    private byte[]? CredentialKey(bool create, CancellationToken cancellationToken)
    {
        if (_keyStore is null) return create || File.Exists(_keyPath) ? LoadOrCreateKey() : null;
        var native = _keyStore.Load(cancellationToken);
        if (native is not null && native.Length != KeySize)
            throw new CryptographicException("The native credential wrapping key is invalid.");
        if (File.Exists(_keyPath))
        {
            var legacy = File.ReadAllBytes(_keyPath);
            try
            {
                if (legacy.Length != KeySize || native is not null && !CryptographicOperations.FixedTimeEquals(legacy, native))
                    throw new CryptographicException("The existing credential wrapping keys do not agree; migration was not applied.");
                if (native is null)
                {
                    _keyStore.Save(legacy, cancellationToken);
                    native = _keyStore.Load(cancellationToken);
                }
                if (native is null || !CryptographicOperations.FixedTimeEquals(legacy, native))
                    throw new CryptographicException("The native credential wrapping key could not be verified; the original was preserved.");
                File.Delete(_keyPath);
                return native;
            }
            finally { CryptographicOperations.ZeroMemory(legacy); }
        }
        if (native is not null || !create) return native;
        if (LoadMap().Values.Any(value => value.StartsWith(AesGcmPrefix, StringComparison.Ordinal)))
            throw new CryptographicException("The credential wrapping key is missing; existing credentials were preserved.");
        var generated = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            _keyStore.Save(generated, cancellationToken);
            var verified = _keyStore.Load(cancellationToken);
            if (verified is null || !CryptographicOperations.FixedTimeEquals(generated, verified))
                throw new CryptographicException("The native credential wrapping key could not be verified.");
            return verified;
        }
        finally { CryptographicOperations.ZeroMemory(generated); }
    }

    /// <summary>
    /// Clave AES local histórica/macOS. Se crea de forma exclusiva con modo 0600 desde el
    /// primer byte, sin lanzar procesos <c>chmod</c>.
    /// </summary>
    private byte[] LoadOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            var existing = File.ReadAllBytes(_keyPath);
            if (existing.Length == KeySize)
            {
                return existing;
            }

            throw new CryptographicException("credentials.key tiene un tamaño inválido");
        }

        var key = RandomNumberGenerator.GetBytes(KeySize);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerOnly;
        }

        try
        {
            using var stream = new FileStream(_keyPath, options);
            stream.Write(key);
            stream.Flush(flushToDisk: true);
            return key;
        }
        catch (IOException) when (File.Exists(_keyPath))
        {
            // Otro proceso la creó a la vez: se usa la suya.
            return LoadOrCreateKey();
        }
    }

    private Dictionary<string, string> LoadMap()
    {
        var map = new Dictionary<string, string>();
        if (!File.Exists(_path))
        {
            return map;
        }

        var lines = File.ReadAllText(_path, Encoding.UTF8).Replace("\r\n", "\n").Replace("\r", "\n");
        foreach (var line in lines.Split('\n'))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            map[DecodeKey(line.Substring(0, eq))] = line.Substring(eq + 1);
        }

        return map;
    }

    /// <summary>Escritura atómica (temporal + rename) con modo 0600 fuera de Windows.</summary>
    private void WriteMap(Dictionary<string, string> map)
    {
        var parts = new List<string>();
        foreach (var kv in map)
        {
            parts.Add(EncodeKey(kv.Key) + "=" + kv.Value);
        }

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(string.Join("\n", parts));
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerOnly;
        }

        try
        {
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    // Los valores cifrados son Base64 (sin '=' problemáticos antes del prefijo ni saltos de
    // línea); solo la clave necesita escape.
    private static string EncodeKey(string key) =>
        key.Replace("%", "%25").Replace("=", "%3D").Replace("\n", "%0A").Replace("\r", "%0D");

    private static string DecodeKey(string key) =>
        key.Replace("%0D", "\r").Replace("%0A", "\n").Replace("%3D", "=").Replace("%25", "%");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
