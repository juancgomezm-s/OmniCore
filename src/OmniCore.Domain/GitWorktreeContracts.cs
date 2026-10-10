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
    public WorktreeOwnership Ownership => new(OwnershipMetadataPath, OwnershipId, WorkspaceId.ToString(), LaneId.ToString(),
        WorkspaceDataRoot, RepositoryRoot, GitCommonDirectory, WorktreePath, SnapshotRef, SnapshotCommit);
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
