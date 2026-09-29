namespace OmniCore.Infrastructure;

using OmniCore.Abstractions;

/// <summary>
/// Rutas por plataforma (ADR-0038 §2): datos y configuración del usuario fuera del repo.
/// Datos — Windows: `%LOCALAPPDATA%\OmniCore`; Linux: `$XDG_DATA_HOME/omnicore` (o
/// `~/.local/share/omnicore`); macOS: `~/Library/Application Support/OmniCore` (best-effort).
/// Configuración — Windows: `%APPDATA%\OmniCore`; Linux: `$XDG_CONFIG_HOME/omnicore` (o
/// `~/.config/omnicore`); macOS: el mismo directorio que los datos. ADR-0039 §2.
/// Las variables de entorno <c>OMNICORE_DATA_DIR</c> y <c>OMNICORE_CONFIG_DIR</c> los reubican
/// (las fija el usuario o el proceso, nunca un repo).
/// </summary>
public sealed class DefaultPlatformPaths : IPlatformPaths
{
    public string DataDirectory { get; }

    public string UserDatabasePath { get; }

    public string ConfigDirectory { get; }

    public DefaultPlatformPaths() : this(null)
    {
    }

    /// <summary>
    /// Sobrescribe el directorio de datos (tests deterministas con temp dir). Un override explícito
    /// es autocontenido: la configuración queda en <c>(data)/config</c> salvo que también se pase,
    /// y las variables de entorno no se aplican. Sin override, rigen <c>OMNICORE_DATA_DIR</c> y
    /// <c>OMNICORE_CONFIG_DIR</c>, y si no, los directorios de la plataforma.
    /// </summary>
    public DefaultPlatformPaths(string? dataDirectoryOverride, string? configDirectoryOverride = null)
    {
        var explicitData = NonEmpty(dataDirectoryOverride);
        var explicitConfig = NonEmpty(configDirectoryOverride);
        string data;
        string config;
        if (explicitData is not null)
        {
            data = explicitData;
            config = explicitConfig ?? Path.Combine(data, "config");
        }
        else
        {
            var envData = NonEmpty(Environment.GetEnvironmentVariable(DataDirVariable));
            data = envData ?? ResolveDataDirectory();
            config = explicitConfig
                ?? NonEmpty(Environment.GetEnvironmentVariable(ConfigDirVariable))
                ?? (envData is not null ? Path.Combine(data, "config") : ResolveConfigDirectory(data));
        }

        DataDirectory = data;
        UserDatabasePath = Path.Combine(data, "user.db");
        ConfigDirectory = config;
        if (!Directory.Exists(data))
        {
            Directory.CreateDirectory(data);
        }
    }

    /// <summary>Variable de entorno que reubica el directorio de datos.</summary>
    public const string DataDirVariable = "OMNICORE_DATA_DIR";

    /// <summary>Variable de entorno que reubica el directorio de configuración.</summary>
    public const string ConfigDirVariable = "OMNICORE_CONFIG_DIR";

    private static string? NonEmpty(string? value) => value is not null && value.Length > 0 ? value : null;

    public string WorkspaceDirectory(string workspaceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceId);
        // El id es un hash hex (ADR-0022 §3); se rechaza cualquier cosa que pueda escapar del
        // directorio de datos.
        foreach (var c in workspaceId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                throw new ArgumentException("WorkspaceId con caracteres no válidos para una ruta", nameof(workspaceId));
            }
        }

        var dir = Path.Combine(DataDirectory, "workspaces", workspaceId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ResolveDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (localAppData.Length > 0)
            {
                return Path.Combine(localAppData, "OmniCore");
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 0)
            {
                return Path.Combine(home, "Library", "Application Support", "OmniCore");
            }
        }

        return XdgDirectory("XDG_DATA_HOME", Path.Combine(".local", "share"));
    }

    private static string ResolveConfigDirectory(string dataDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (appData.Length > 0)
            {
                return Path.Combine(appData, "OmniCore");
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            return dataDirectory;
        }

        return XdgDirectory("XDG_CONFIG_HOME", ".config");
    }

    /// <summary>
    /// `$XDG_*_HOME/omnicore` o `~/&lt;fallback&gt;/omnicore`. Sin perfil de usuario no hay dónde
    /// guardar datos fuera del repo, así que se falla en vez de caer al cwd.
    /// </summary>
    private static string XdgDirectory(string variable, string homeRelativeFallback)
    {
        var xdg = Environment.GetEnvironmentVariable(variable);
        if (xdg is not null && xdg.Length > 0 && Path.IsPathRooted(xdg))
        {
            return Path.Combine(xdg, "omnicore");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length == 0)
        {
            throw new InvalidOperationException(
                "No se puede determinar el directorio del usuario: define " + variable + " o HOME");
        }

        return Path.Combine(home, homeRelativeFallback, "omnicore");
    }
}
