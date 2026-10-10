namespace OmniCore.Domain;

/// <summary>Base exacta desde la que se prepara una Lane aislada (ADR-0021).</summary>
public enum WorktreeBase
{
    Head,
    SnapshotOfWorkingTree,
    PatchOverlay,
}

/// <summary>Tratamiento explícito de archivos ignorados del workspace.</summary>
public abstract record IgnoredFilesPolicy
{
    private IgnoredFilesPolicy() { }

    /// <summary>No copia archivos ignorados, incluidos binarios y credenciales locales.</summary>
    public sealed record NonePolicy : IgnoredFilesPolicy;

    /// <summary>Política reservada para allowlists revisadas; el backend inicial la rechaza.</summary>
    public sealed record AllowlistPolicy(IReadOnlyList<string> Globs) : IgnoredFilesPolicy;

    public static IgnoredFilesPolicy None { get; } = new NonePolicy();
}

/// <summary>Solicitud para una operación GitWorktree. El root de datos debe estar fuera del repo.</summary>
public sealed record WorktreeCreateRequest(
    WorkspaceId WorkspaceId,
    LaneId LaneId,
    string RepositoryPath,
    string WorkspaceDataRoot,
    WorktreeBase Base = WorktreeBase.SnapshotOfWorkingTree,
    IgnoredFilesPolicy? IgnoredFiles = null);

/// <summary>Identidad observada del repositorio sin exponer configuración ni contenido.</summary>
public sealed record WorktreeRepositoryIdentity(
    string RepoRoot,
    string GitCommonDirectory,
    string HeadCommit,
    string? Branch);

/// <summary>Identidad inmutable del worktree y su snapshot privado.</summary>
public sealed record WorktreeIdentity(
    WorkspaceId WorkspaceId,
    LaneId LaneId,
    string RepositoryRoot,
    string GitCommonDirectory,
    string Head,
    string? Branch,
    string SnapshotCommit,
    string SnapshotTree,
    string UserIndexTree,
    string WorktreePath,
    string SnapshotRef,
    string OwnershipId,
    string OwnershipMetadataPath,
    string WorkspaceDataRoot,
    DateTimeOffset CreatedAtUtc)
{
    public WorktreeBase Base { get; init; } = WorktreeBase.SnapshotOfWorkingTree;

    public WorktreeOwnership Ownership => new(OwnershipMetadataPath, OwnershipId, WorkspaceId.ToString(), LaneId.ToString(),
        WorkspaceDataRoot, RepositoryRoot, GitCommonDirectory, WorktreePath, SnapshotRef, SnapshotCommit);
}

/// <summary>Petición para calcular una propuesta 3-way; esta operación no aplica cambios.</summary>
public sealed record WorktreeIntegrationPreviewRequest(WorktreeIdentity Worktree);

/// <summary>Estado observado y propuesta inmutable para integrar una Lane después de SnapshotCommit.</summary>
public sealed record WorktreeIntegrationPreview(
    string ProposalId,
    string RepositoryRoot,
    string GitCommonDirectory,
    string BaseCommit,
    string OursCommit,
    string TheirsCommit,
    string? MergeTree,
    string WorkspaceHeadAtCapture,
    string? WorkspaceBranchAtCapture,
    bool WorkspaceHeadChanged,
    bool WorkspaceBranchChanged,
    IReadOnlyList<WorktreeIntegrationConflict> Conflicts,
    IReadOnlyList<WorktreeIntegrationChange> Changes,
    DateTimeOffset CreatedAtUtc)
{
    public bool HasConflicts => Conflicts.Count > 0;
}

/// <summary>Path conflictivo sin contenido ni texto crudo del proceso Git.</summary>
public sealed record WorktreeIntegrationConflict(string RelativePath);

/// <summary>Pre/post hashes que permiten una futura aplicación por archivo reconciliable.</summary>
public sealed record WorktreeIntegrationChange(
    string RelativePath,
    string? ExpectedPreSha256,
    string? ExpectedPostSha256,
    string? ExpectedPreMode,
    string? ExpectedPostMode);

public sealed record WorktreeIntegrationPreviewOutcome(WorktreeIntegrationPreview? Preview, GitWorktreeErrorCode? Error)
{
    public bool Succeeded => Preview is not null && Error is null;
    public static WorktreeIntegrationPreviewOutcome Success(WorktreeIntegrationPreview preview) => new(preview, null);
    public static WorktreeIntegrationPreviewOutcome Failure(GitWorktreeErrorCode error) => new(null, error);
}

/// <summary>Datos requeridos para retirar solo el worktree creado por OmniCore.</summary>
public sealed record WorktreeOwnership(string MetadataPath, string OwnershipId, string WorkspaceId, string LaneId,
    string DataRoot, string RepositoryRoot, string GitCommonDirectory, string WorktreePath,
    string SnapshotRef, string SnapshotCommit);

public enum GitWorktreeErrorCode
{
    NoRepository,
    MissingHead,
    UnsupportedBase,
    UnsupportedIgnoredFilesPolicy,
    SecretPathPresent,
    UnsafePath,
    UnsupportedFilter,
    UnsupportedSubmoduleState,
    UnsupportedIndex,
    UnsupportedTransform,
    Conflict,
    WorktreeDirty,
    GitOutputTruncated,
    InvalidPath,
    PathOutsideDataRoot,
    OwnershipMismatch,
    UnsupportedOwnershipMetadata,
    UnsupportedMergeDriver,
    WorkspaceChanged,
    GitCommandFailed,
}

public sealed record WorktreeCreateOutcome(WorktreeIdentity? Worktree, GitWorktreeErrorCode? Error)
{
    public bool Succeeded => Worktree is not null && Error is null;
    public static WorktreeCreateOutcome Success(WorktreeIdentity worktree) => new(worktree, null);
    public static WorktreeCreateOutcome Failure(GitWorktreeErrorCode error) => new(null, error);
}

public sealed record WorktreeInspectOutcome(WorktreeRepositoryIdentity? Identity, GitWorktreeErrorCode? ErrorCode)
{
    public bool Succeeded => Identity is not null && ErrorCode is null;
    public static WorktreeInspectOutcome Success(WorktreeRepositoryIdentity identity) => new(identity, null);
    public static WorktreeInspectOutcome Failure(GitWorktreeErrorCode error) => new(null, error);
}

public sealed record WorktreeCleanupOutcome(bool Removed, GitWorktreeErrorCode? Error)
{
    public static WorktreeCleanupOutcome Success() => new(true, null);
    public static WorktreeCleanupOutcome Failure(GitWorktreeErrorCode error) => new(false, error);
}
