namespace OmniCore.Host;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;

/// <summary>Composes only trusted, consented project tools into an Act catalog.</summary>
public static class ProjectExtensionTools
{
    public static IReadOnlyList<ITool> Load(string workspaceRoot, IPlatformPaths paths, bool workspaceTrusted,
        IExtensionRpcInvoker invoker)
    {
        var ready = ProjectExtensionPackageCatalog.Ready(workspaceRoot, paths, workspaceTrusted);
        var tools = new List<ITool>();
        foreach (var package in ready)
        {
            var extension = package.Extension;
            foreach (var declaration in extension.Manifest!.Provides?.Tools ?? [])
            {
                var schemaFile = ResolvePackageFile(extension.Directory, declaration.Schema!);
                using var schema = JsonDocument.Parse(File.ReadAllText(schemaFile));
                if (schema.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Extension tool schemas must be JSON objects: " + declaration.Schema);
                tools.Add(new ExtensionTool(extension, declaration, schema.RootElement.GetRawText(), invoker,
                    workspaceRoot, () => ProjectExtensionPackageCatalog.Ready(workspaceRoot, paths, workspaceTrusted)
                        .Any(current => current.Capabilities.Contains("tool.register")
                            && current.Extension.Manifest?.Id == extension.Manifest?.Id
                            && current.Extension.Trust == extension.Trust
                            && current.Extension.ManifestSha256 == extension.ManifestSha256
                            && current.Extension.PackageSha256 == extension.PackageSha256)));
            }
        }
        return tools;
    }

    private static string ResolvePackageFile(string packageDirectory, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("Extension schema paths must be package-relative.");
        var root = Path.GetFullPath(packageDirectory);
        var file = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, file);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !File.Exists(file) || HasReparsePointInPath(root, file))
            throw new InvalidDataException("Extension tool schema must exist inside its package.");
        return file;
    }

    private static bool HasReparsePointInPath(string root, string file)
    {
        var current = Path.GetDirectoryName(file)!;
        while (!string.Equals(current, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) return true;
            current = parent;
        }
        return (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0;
    }
}
