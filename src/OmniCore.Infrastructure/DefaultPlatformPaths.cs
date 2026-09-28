namespace OmniCore.Infrastructure;

using OmniCore.Abstractions;

/// <summary>
/// Rutas por plataforma (ADR-0038 §2): datos del usuario fuera del repo. Windows:
/// `%LOCALAPPDATA%\OmniCore`; Linux: `$XDG_DATA_HOME/omnicore` (o `~/.local/share/omnicore`);
/// macOS: `~/Library/Application Support/OmniCore` (best-effort). ADR-0039 §2.
/// </summary>
public sealed class DefaultPlatformPaths : IPlatformPaths
{
    public string DataDirectory { get; }

    public string UserDatabasePath { get; }

    public DefaultPlatformPaths() : this(null)
    {
    }

    /// <summary>Sobrescribe el directorio (tests deterministas con temp dir).</summary>
    public DefaultPlatformPaths(string? dataDirectoryOverride)
    {
        var data = dataDirectoryOverride is not null && dataDirectoryOverride!.Length > 0
            ? dataDirectoryOverride!
            : ResolveDataDirectory();
        DataDirectory = data;
        UserDatabasePath = Path.Combine(data, "user.db");
        if (!Directory.Exists(data))
        {
            Directory.CreateDirectory(data);
        }
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
            var appSupport = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (appSupport.Length > 0)
            {
                return Path.Combine(appSupport, "Library", "Application Support", "OmniCore");
            }
        }

        // Linux (y fallback): XDG_DATA_HOME → ~/.local/share.
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (xdg is not null && xdg!.Length > 0 && Path.IsPathRooted(xdg))
        {
            return Path.Combine(xdg, "omnicore");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0
            ? Path.Combine(home, ".local", "share", "omnicore")
            : Path.Combine(".", ".omnicore-data");
    }
}
