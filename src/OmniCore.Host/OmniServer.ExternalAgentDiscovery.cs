namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;

public sealed partial class OmniServer
{
    private readonly record struct ExternalAgentProjectScope(string? Root, string? Identity, bool Trusted);

    private ExternalAgentProjectScope ResolveExternalAgentProjectScope(IPlatformPaths paths, bool workspaceTrusted)
    {
        var active = _externalAgentActiveDirectory();
        var root = ExternalAgentDiscoveryCatalog.DiscoveryProjectRoot(_workspaceRoot, active);
        if (root is null) return new(null, null, false);
        var sameAsWorkspace = _workspaceRoot is not null
            && Path.GetFullPath(root).Equals(Path.GetFullPath(_workspaceRoot),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var trusted = sameAsWorkspace ? workspaceTrusted : new WorkspaceTrustStore(paths).IsTrusted(root);
        return new(root, ExternalAgentDiscoveryCatalog.ProjectIdentity(root), trusted);
    }

    private IReadOnlyList<DiscoveredSkill> DiscoverAllSkillSources(IPlatformPaths paths,
        string? workspaceData, SessionId? session, bool workspaceTrusted, CancellationToken cancellationToken)
    {
        var builtIn = new SkillSourceCatalog().DiscoverWithExtensions(_workspaceRoot, paths.DataDirectory,
            workspaceData, session, workspaceTrusted, ProjectExtensionPackageCatalog.Ready(
                _workspaceRoot ?? Directory.GetCurrentDirectory(), paths, workspaceTrusted));
        var memoryConsent = new ExternalAgentMemoryConsentStore(paths.DataDirectory);
        var project = ResolveExternalAgentProjectScope(paths, workspaceTrusted);
        var portable = _externalAgentCatalogFactory().Discover(project.Root, _externalAgentActiveDirectory(),
                project.Trusted, memoryConsent.IsAllowed(), cancellationToken,
                project.Trusted && project.Identity is not null && memoryConsent.IsProjectAllowed(project.Identity) ? project.Identity : null,
                path => new WorkspaceTrustStore(paths).IsTrusted(path))
            .Where(source => source.Kind == ExternalAgentSourceKind.Skill && source.Skill is not null)
            .Select(source => source.Skill!);
        return SkillManifestLoader.ResolvePrecedence(builtIn.Where(skill => skill.State != SkillLifecycleState.Released)
            .Concat(portable));
    }
}
