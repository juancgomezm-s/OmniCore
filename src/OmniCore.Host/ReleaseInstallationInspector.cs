namespace OmniCore.Host;

using System.Text.Json;
using OmniCore.Protocol;

/// <summary>Identifies how this CLI was installed before any update touches the active files.</summary>
public sealed class ReleaseInstallationInspector : IReleaseInstallationInspector
{
    private readonly string _baseDirectory;
    private readonly string? _npmPathOverride;
    private readonly string? _nodePathOverride;

    public ReleaseInstallationInspector(string? baseDirectory = null, string? npmPath = null,
        string? nodePath = null)
    {
        _baseDirectory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _npmPathOverride = npmPath;
        _nodePathOverride = nodePath;
    }

    public ReleaseInstallation Inspect()
    {
        var packageDirectory = PackageDirectory(_baseDirectory);
        if (packageDirectory is not null)
        {
            var package = ReadPackage(packageDirectory);
            if (package.Name == "omnicore-cli")
            {
                if (IsNpmGlobal(packageDirectory))
                {
                    var npm = _npmPathOverride ?? FindOnPath("npm.cmd", "npm");
                    var node = _nodePathOverride ?? FindOnPath("node.exe", "node");
                    var prefix = NpmPrefix(packageDirectory);
                    return new(ReleaseInstallKind.NpmGlobal, packageDirectory, npm, node, package.Version,
                        npm is null ? "npm_not_found" : node is null ? "node_not_found" : "npm_prefix_layout", prefix);
                }
                return new(ReleaseInstallKind.ReleasePackage, packageDirectory,
                    _npmPathOverride, _nodePathOverride, package.Version, "package_not_npm_global");
            }
        }

        var checkout = FindCheckout(_baseDirectory);
        if (checkout is not null)
        {
            var package = ReadPackage(checkout);
            return new(ReleaseInstallKind.DevelopmentCheckout, checkout, _npmPathOverride, _nodePathOverride,
                package.Version, "development_checkout");
        }
        return new(ReleaseInstallKind.Unknown, null, _npmPathOverride, _nodePathOverride, null, "install_kind_unknown");
    }

    private static string? PackageDirectory(string baseDirectory)
    {
        var current = new DirectoryInfo(baseDirectory);
        if (current.Name.Equals("npm", StringComparison.OrdinalIgnoreCase)
            && current.Parent?.Name.Equals("dist", StringComparison.OrdinalIgnoreCase) == true
            && current.Parent.Parent is { } package)
            return package.FullName;
        if (current.Name.Equals("omnicore-cli", StringComparison.OrdinalIgnoreCase)
            && current.Parent?.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) == true)
            return current.FullName;
        return null;
    }

    private static bool IsNpmGlobal(string packageDirectory)
    {
        var package = new DirectoryInfo(packageDirectory);
        return package.Name.Equals("omnicore-cli", StringComparison.OrdinalIgnoreCase)
            && package.Parent?.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? NpmPrefix(string packageDirectory)
    {
        var package = new DirectoryInfo(packageDirectory);
        var modules = package.Parent;
        if (modules is null || !modules.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) return null;
        var parent = modules.Parent;
        if (parent is null) return null;
        // npm uses <prefix>/node_modules on Windows and <prefix>/lib/node_modules
        // on Unix. This also targets an OmniCore-managed side-by-side prefix.
        if (parent.Name.Equals("lib", StringComparison.OrdinalIgnoreCase)) parent = parent.Parent;
        return parent?.FullName;
    }

    private static string? FindCheckout(string baseDirectory)
    {
        var current = new DirectoryInfo(baseDirectory);
        for (var depth = 0; depth < 10 && current is not null; depth++, current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "OmniCore.slnx"))
                && Directory.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;
        }
        return null;
    }

    private static (string? Name, string? Version) ReadPackage(string directory)
    {
        var path = Path.Combine(directory, "package.json");
        if (!File.Exists(path)) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            var name = root.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                ? nameValue.GetString() : null;
            var version = root.TryGetProperty("version", out var versionValue) && versionValue.ValueKind == JsonValueKind.String
                ? versionValue.GetString() : null;
            return (name, version);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, null);
        }
    }

    private static string? FindOnPath(params string[] names)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidateDirectory;
            try { candidateDirectory = directory.Trim().Trim('"'); }
            catch (ArgumentException) { continue; }
            if (candidateDirectory.Length == 0) continue;
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(candidateDirectory, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }
}
