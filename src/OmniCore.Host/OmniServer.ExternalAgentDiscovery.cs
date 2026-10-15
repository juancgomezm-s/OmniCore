namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;

public sealed partial class OmniServer
{
    private IReadOnlyList<DiscoveredSkill> DiscoverAllSkillSources(IPlatformPaths paths,
        string? workspaceData, SessionId? session, bool workspaceTrusted)
    {
        var builtIn = new SkillSourceCatalog().DiscoverWithExtensions(_workspaceRoot, paths.DataDirectory,
            workspaceData, session, workspaceTrusted, ProjectExtensionPackageCatalog.Ready(
                _workspaceRoot ?? Directory.GetCurrentDirectory(), paths, workspaceTrusted));
        var memoryConsent = new ExternalAgentMemoryConsentStore(paths.DataDirectory);
        var projectIdentity = _workspaceRoot is null ? null : ExternalAgentDiscoveryCatalog.ProjectIdentity(_workspaceRoot);
        var portable = _externalAgentCatalogFactory().Discover(_workspaceRoot, _externalAgentActiveDirectory(),
                workspaceTrusted, memoryConsent.IsAllowed(), CancellationToken.None,
                projectIdentity is not null && memoryConsent.IsProjectAllowed(projectIdentity) ? projectIdentity : null)
            .Where(source => source.Kind == ExternalAgentSourceKind.Skill && source.Skill is not null)
            .Select(source => source.Skill!);
        return SkillManifestLoader.ResolvePrecedence(builtIn.Where(skill => skill.State != SkillLifecycleState.Released)
            .Concat(portable));
    }
}
