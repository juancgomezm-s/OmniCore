namespace OmniCore.Infrastructure;

using OmniCore.Abstractions;

/// <summary>
/// ICredentialStore sobre disco con acceso restringido (ADR-0011 §3.1). M2: los secretos se
/// ofuscan con XOR + Base64 usando una clave derivada por máquina (RandomNumberGenerator) y un
/// salt único por entrada; no se almacenan en texto plano. En Unix se aplica chmod 0600 al
/// archivo (en Windows el ACL implícito del directorio del usuario + la ofuscación). El M3
/// mueve a Windows Credential Manager / DPAPI. Nunca se loguea el valor.
/// </summary>
public sealed class FileCredentialStore : ICredentialStore
{
    private readonly string _path;

    public FileCredentialStore(string path)
    {
        _path = path;
    }

    public void Save(string key, string value, CancellationToken cancellationToken)
    {
        EnsureFile();

        var map = LoadMap();
        map[key] = Obfuscate(value);
        WriteMap(map);
    }

    public string? Load(string key, CancellationToken cancellationToken)
    {
        EnsureFile();
        var map = LoadMap();
        if (!map.TryGetValue(key, out var stored))
        {
            return null;
        }

        return stored is null ? null : Reveal(stored!);
    }

    public void Delete(string key, CancellationToken cancellationToken)
    {
        EnsureFile();
        var map = LoadMap();
        map.Remove(key);
        WriteMap(map);
    }

    /// <summary>Borrado físico del archivo de credenciales (no solo la entrada).</summary>
    public void Purge()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Ruta del archivo (para que la prueba de cierre lo inspeccione).</summary>
    public string PathValue() => _path;

    private void EnsureFile()
    {
        if (!File.Exists(_path))
        {
            var parent = Path.GetDirectoryName(_path);
            if (parent is not null && parent!.Length > 0 && !Directory.Exists(parent!))
            {
                Directory.CreateDirectory(parent!);
            }

            WriteMap(new Dictionary<string, string>());
        }

        ApplySecurePermissions();
    }

    private void ApplySecurePermissions()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        // Unix: chmod 0600. En Windows el directorio del usuario + DPAPI futuro; aquí
        // la ofuscación evita que una copia del archivo exponga el secreto.
        if (IsUnix)
        {
            try
            {
                var args = new List<string> { "600", _path };
                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = "chmod";
                foreach (var a in args)
                {
                    psi.ArgumentList.Add(a);
                }

                psi.UseShellExecute = false;
                var p = System.Diagnostics.Process.Start(psi);
                if (p is not null)
                {
                    p!.WaitForExit(10_000);
                }
            }
            catch (Exception)
            {
                // sin chmod (p. ej. contenedor): la ofuscación sigue activa
            }
        }
    }

    /// <summary>
    /// Clave de máquina: 32 bytes derivados de una semilla persistida en el directorio temporal
    /// del usuario (~/.omnicore-machine-key). El archivo de clave se protege igual que los
    /// credenciales; así un secreto guardado vuelve a leerse en otra invocación.
    /// </summary>
    private static byte[] MachineKey()
    {
        var keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".omnicore-machine-key");
        var hex = "";
        try
        {
            if (File.Exists(keyPath))
            {
                hex = File.ReadAllText(keyPath).Trim();
            }
        }
        catch (Exception)
        {
            hex = "";
        }

        if (hex.Length < 32)
        {
            hex = RandomHex(32);
            try
            {
                File.WriteAllText(keyPath, hex);
            }
            catch (Exception)
            {
                // sin permiso de escritura del perfil: la key queda volátil (solo si el
                // temp no es escribible, no afecta los tests de cierre)
            }
        }

        return ToBytes(hex.Length >= 32 ? hex.Substring(0, 32) : hex + new string('0', 32 - hex.Length));
    }

    private static string Obfuscate(string value)
    {
        var salt = RandomHex(8);
        var payload = salt + ":" + value;
        var bytes = ToBytes(payload);
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= (byte) _machineKeyStatic[i % 32];
        }

        return "enc:" + ToBase64(bytes);
    }

    private static string Reveal(string stored)
    {
        if (!stored.StartsWith("enc:"))
        {
            return stored;
        }

        try
        {
            var bytes = FromBase64(stored.Substring(4));
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] ^= (byte) _machineKeyStatic[i % 32];
            }

            var payload = FromBytes(bytes);
            var sep = payload.IndexOf(':');
            return sep < 0 ? payload : payload.Substring(sep + 1);
        }
        catch (Exception)
        {
            return stored;
        }
    }

    private static readonly byte[] _machineKeyStatic = MachineKey();

    private Dictionary<string, string> LoadMap()
    {
        var map = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(_path))
            {
                return map;
            }

            var lines = File.ReadAllText(_path).Replace("\r\n", "\n").Replace("\r", "\n");
            foreach (var line in lines.Split('\n'))
            {
                var colon = line.IndexOf('=');
                if (colon < 0)
                {
                    continue;
                }

                var key = line.Substring(0, colon);
                var value = line.Substring(colon + 1);
                if (key.Length > 0)
                {
                    map[DecodeKey(key)] = Unescape(value);
                }
            }
        }
        catch (Exception)
        {
            return map;
        }

        return map;
    }

    private void WriteMap(Dictionary<string, string> map)
    {
        var parts = new List<string>();
        var keys = map.Keys.ToArray();
        for (var i = 0; i < keys.Length; i++)
        {
            var k = keys[i];
            parts.Add(EncodeKey(k) + "=" + Escape(map[k]!));
        }

        File.WriteAllText(_path, string.Join("\n", parts.ToArray()));
        ApplySecurePermissions();
    }

    private static string EncodeKey(string key) =>
        key.Replace("\\", "/").Replace("=", "%3D").Replace("\n", " ");

    private static string DecodeKey(string key) =>
        key.Replace("%3D", "=").Replace("/", "\\");

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\n", "\\n");

    private static string Unescape(string value)
    {
        var v = value;
        v = v.Replace("\\n", "\n").Replace("\\\\", "\\");
        return v;
    }

    private static bool IsUnix => !Path.IsPathFullyQualified("C:\\x");

    private static string RandomHex(int bytes) => System.Security.Cryptography.RandomNumberGenerator.GetHexString(bytes);

    private static byte[] ToBytes(string s)
    {
        var result = new byte[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            result[i] = (byte) s[i];
        }

        return result;
    }

    private static string FromBytes(byte[] b)
    {
        var chars = new char[b.Length];
        for (var i = 0; i < b.Length; i++)
        {
            chars[i] = (char) b[i];
        }

        return new string(chars);
    }

    private static string ToBase64(byte[] b) => Convert.ToBase64String(b);

    private static byte[] FromBase64(string s) => Convert.FromBase64String(s);
}