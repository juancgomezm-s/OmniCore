namespace OmniCore.Host;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Infrastructure;
using OmniCore.Tools;

/// <summary>Composes the existing single-file reconciler with the M7 integration batch reader.
/// Batch records route only by their exact durable kind and fail closed without protected CAS.</summary>
internal sealed class WorktreeIntegrationAwareReconciler : IArtifactAwareFilesystemReconciler
{
    private readonly IFilesystemReconciler _files;
    private readonly WorktreeIntegrationHostCoordinator? _integration;

    public WorktreeIntegrationAwareReconciler(string workspaceRoot, IArtifactStore? artifacts,
        IFilesystemReconciler files, string? expectedDataRoot = null)
    {
        _files = files;
        if (artifacts is not null)
        {
            try
            {
                var physical = ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspaceRoot);
                var workspace = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(physical));
                // A test/host composition may supply the already-derived durable workspace
                // data root. It is never read from reconciliation metadata.
                var dataRoot = expectedDataRoot is null
                    ? Path.Combine(new DefaultPlatformPaths().DataDirectory, "workspaces", workspace.ToString())
                    : Path.GetFullPath(expectedDataRoot);
                _integration = new WorktreeIntegrationHostCoordinator(physical, dataRoot, artifacts,
                    new GitWorktreeStore(SystemProcessRuntime.Instance()));
            }
            catch (Exception) { _integration = null; }
        }
    }

    public FilesystemReconciliation Reconcile(string workspaceRoot, string reconciliationJson,
        CancellationToken cancellationToken) => Reconcile(workspaceRoot, reconciliationJson, null, cancellationToken);

    public FilesystemReconciliation Reconcile(string workspaceRoot, string reconciliationJson,
        ArtifactRef? beforeStateRef, CancellationToken cancellationToken)
    {
        if (HasIntegrationKind(reconciliationJson))
            return _integration?.Reconcile(workspaceRoot, reconciliationJson, beforeStateRef, cancellationToken)
                ?? FilesystemReconciliation.Unresolvable("protected integration recovery is unavailable");
        return _files.Reconcile(workspaceRoot, reconciliationJson, cancellationToken);
    }

    private static bool HasIntegrationKind(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("kind", out var kind)
                && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == "worktree.integration.batch";
        }
        catch (JsonException) { return false; }
    }
}
