namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Resolves current Host-owned identities and trust; model arguments never select a partition.</summary>
internal static class ScopedMemoryAccess
{
    internal static IReadOnlyList<MemoryScope> Allowed(IPlatformPaths paths, string workspaceRoot)
    {
        var physical = ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspaceRoot);
        List<MemoryScope> scopes = [MemoryScope.ForWorkspace(WorkspaceId.Of(
            ProjectIdentity.CanonicalWorkspacePath(physical))), MemoryScope.ForUser()];
        if (new WorkspaceTrustStore(paths).IsTrusted(physical))
            scopes.Add(MemoryScope.ForProject(ProjectIdentity.FromWorkspaceRoot(physical)));
        return scopes;
    }

    internal static MemoryScope Resolve(IPlatformPaths paths, string workspaceRoot, string name)
    {
        var level = name switch
        {
            "workspace" => ScopeLevel.Workspace,
            "project" => ScopeLevel.Project,
            "global" => ScopeLevel.User,
            _ => throw new ArgumentException("client.memory.scope_invalid"),
        };
        return Allowed(paths, workspaceRoot).SingleOrDefault(scope => scope.Level == level)
            ?? throw new InvalidOperationException("client.memory.trust_required");
    }
}
