namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>Grant seguro por tool y fingerprint de claims (ADR-0037 §5).</summary>
public sealed record PermissionGrantRecord(
    GrantId Id,
    string ToolId,
    string ClaimsKey,
    GrantLifetime Lifetime,
    WorkspaceId Workspace,
    RunId? Run,
    DateTimeOffset CreatedAt);

/// <summary>Almacén de grants revocables, aislado por WorkspaceId.</summary>
public interface IPermissionGrantStore
{
    IReadOnlyList<PermissionGrantRecord> List(WorkspaceId workspace, RunId? run = null);

    PermissionGrantRecord? Find(WorkspaceId workspace, RunId? run, string toolId, string claimsKey);

    void Add(PermissionGrantRecord grant, CancellationToken cancellationToken);

    bool Revoke(WorkspaceId workspace, GrantId id, CancellationToken cancellationToken);
}

/// <summary>Policy opcional capaz de persistir una aprobación en un lifetime seleccionado por el usuario.</summary>
public interface IGrantablePermissionPolicy
{
    bool CanCreatePersistentGrants { get; }

    GrantId? RecordApprovedGrant(ToolIntent intent, GrantLifetime lifetime, CancellationToken cancellationToken);
}
