namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;

public sealed partial class OmniServer
{
    internal IEnumerable<string> SkillInvocationOccupiedNames()
    {
        // A fresh prompt may arrive before any command query. Read current consented command
        // metadata directly, without replaying active skills or retaining revoked extension names.
        var occupied = _commandService.Registry.Descriptors()
            .Where(command => command.Source.Kind is not (SourceKind.Skill or SourceKind.Extension))
            .SelectMany(command => new[] { command.Name }.Concat(command.Aliases))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = _platformPathsFactory();
        var workspace = _workspaceRoot ?? Directory.GetCurrentDirectory();
        var trusted = _workspaceRoot is not null && new WorkspaceTrustStore(paths).IsTrusted(workspace);
        foreach (var package in ProjectExtensionPackageCatalog.Ready(workspace, paths, trusted))
        {
            if (!package.Capabilities.Contains("command.register") || !package.Capabilities.Contains("tool.register")) continue;
            foreach (var command in package.Extension.Manifest!.Provides?.Commands ?? [])
                if (command.Kind == "Extension" && command.Tool is not null && command.Name is { Length: > 0 } name)
                    occupied.Add(name);
        }
        foreach (var alias in new PolicyPreferencesHost(paths, workspace).Read().CommandAliases.Keys)
            occupied.Add(alias);
        return occupied.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
