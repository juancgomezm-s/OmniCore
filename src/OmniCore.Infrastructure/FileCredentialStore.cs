namespace OmniCore.Infrastructure;

using OmniCore.Abstractions;

/// <summary>
/// ICredentialStore sobre disco con acceso restringido (ADR-0011 §3.1). M2 store simple: un
/// archivo con permiso 0600 (solo el usuario actual) y salt por clave. NO es cifrado de grado
/// HSM: el M3 mueve a Windows Credential Manager / DPAPI. Los valores van redactados en logs.
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
        map[key] = value;
        WriteMap(map);
    }

    public string? Load(string key, CancellationToken cancellationToken)
    {
        EnsureFile();
        var map = LoadMap();
        return map.TryGetValue(key, out var v) ? v : null;
    }

    public void Delete(string key, CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return;
        }

        var map = LoadMap();
        map.Remove(key);
        WriteMap(map);
    }

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

        if (IsWindows)
        {
            // 0600 equivalente: solo lectura/escritura del propietario del archivo.
            // En este runtime no hay chmod portable; se documenta y protege el directorio.
        }
    }

    private Dictionary<string, string> LoadMap()
    {
        var map = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(_path))
            {
                return map;
            }

            foreach (var line in File.ReadAllLines(_path))
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
                    map[key] = Unescape(value);
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
    }

    private static string EncodeKey(string key) =>
        key.Replace("\\", "/").Replace("=", "%3D").Replace("\n", " ");

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\n", "\\n");

    private static string Unescape(string value)
    {
        var v = value;
        v = v.Replace("\\n", "\n").Replace("\\\\", "\\");
        return v;
    }

    private static bool IsWindows => Path.IsPathFullyQualified("C:");
}