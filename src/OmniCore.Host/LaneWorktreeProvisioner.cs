using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Infrastructure;

namespace OmniCore.Host;

internal sealed record LaneWorktreeProvisionOutcome(WorktreeIdentity? Identity, TaskPacket? Packet,
    ArtifactRef? TaskPacketRef, GitWorktreeErrorCode? Error, IPreparedArtifact? PreparedArtifact = null,
    IDisposable? PublicationLease = null)
{
    public bool Succeeded => Identity is not null && TaskPacketRef is not null && Error is null;
}

/// <summary>Host-owned F1 provisioning. It creates only an external private worktree and a
/// protected-by-sensitivity TaskPacket artifact; journal publication remains with the caller.</summary>
internal sealed class LaneWorktreeProvisioner(IProcessRuntime processes, IArtifactStore artifacts)
{
    internal const string TaskPacketMediaType = "application/vnd.omnicore.task-packet+json";
    internal const int MaximumTaskPacketBytes = 1024 * 1024;

    internal async Task<LaneWorktreeProvisionOutcome> CreateAsync(string repositoryRoot,
        string workspaceDataRoot, SessionId session, RunId run, TaskCreated task, Delegation delegation,
        ExecutionId execution, AgentProfile profile, string workingState, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var physicalRoot = ProjectIdentity.ResolvePhysicalWorkspaceRoot(repositoryRoot);
        var workspaceId = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(physicalRoot));
        if (delegation.PacketRef.Kind != ArtifactKind.Other
            || delegation.PacketRef.MediaType != ContextInheritanceService.DelegationPacketMediaType
            || delegation.PacketRef.Sensitivity != Sensitivity.Sensitive
            || delegation.PacketRef.Size <= 0 || delegation.PacketRef.Size > ContextInheritanceService.MaximumDelegationPacketBytes
            || !artifacts.Verify(delegation.PacketRef.Hash, delegation.PacketRef.Size))
            return new(null, null, null, GitWorktreeErrorCode.UnsupportedOwnershipMetadata);

        var created = await new GitWorktreeStore(processes).CreateAsync(new WorktreeCreateRequest(
            workspaceId, delegation.ChildLaneId, physicalRoot, workspaceDataRoot,
            WorktreeBase.SnapshotOfWorkingTree, IgnoredFilesPolicy.None), cancellationToken).ConfigureAwait(false);
        if (!created.Succeeded) return new(null, null, null, created.Error);
        var identity = created.Worktree!;
        if (identity.WorkspaceId.ToString() != workspaceId.ToString()
            || identity.LaneId != delegation.ChildLaneId
            || identity.Base != WorktreeBase.SnapshotOfWorkingTree)
            return new(identity, null, null, GitWorktreeErrorCode.OwnershipMismatch);

        var objective = BoundText(task.Objective);
        var boundedWorkingState = BoundText(workingState);
        if (objective is null || boundedWorkingState is null)
            return new(identity, null, null, GitWorktreeErrorCode.UnsupportedOwnershipMetadata);
        var packet = new TaskPacket(1, session, run, task.TaskId, delegation.ChildLaneId,
            execution, delegation.DelegationId, delegation.ParentExecutionId, profile.Id,
            objective, task.Budget, delegation.PacketRef, identity.OwnershipId,
            identity.RepositoryRoot, identity.GitCommonDirectory, identity.WorktreePath,
            identity.Head, identity.SnapshotCommit, identity.SnapshotTree,
            WorktreeBase.SnapshotOfWorkingTree, boundedWorkingState);
        var json = JsonSerializer.Serialize(packet, TaskPacketJsonContext.Default.TaskPacket);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumTaskPacketBytes)
            return new(identity, packet, null, GitWorktreeErrorCode.UnsupportedOwnershipMetadata);
        if (artifacts is not IArtifactPreparationStore preparation)
            return new(identity, packet, null, GitWorktreeErrorCode.UnsupportedOwnershipMetadata);
        var prepared = preparation.PrepareText(json, TaskPacketMediaType, ArtifactKind.Other, Sensitivity.Sensitive);
        if (prepared.Reference.Kind != ArtifactKind.Other || prepared.Reference.MediaType != TaskPacketMediaType
            || prepared.Reference.Sensitivity != Sensitivity.Sensitive
            || prepared.Reference.Size is <= 0 or > MaximumTaskPacketBytes)
            return new(identity, packet, null, GitWorktreeErrorCode.UnsupportedOwnershipMetadata);
        cancellationToken.ThrowIfCancellationRequested();
        return new(identity, packet, prepared.Reference, null, prepared);
    }

    /// <summary>Creates a second, read-only workflow Lane binding to the already-owned Implement
    /// worktree. It creates no directory or Git ref; the caller must prove the exact canonical
    /// previous workflow stage and revalidate the captured tree before execution.</summary>
    internal LaneWorktreeProvisionOutcome BindVerifiedImplementation(WorktreeIdentity identity,
        SessionId session, RunId run, TaskCreated task, Delegation delegation, ExecutionId execution,
        AgentProfile profile, string workingState, WorkflowImplementationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(binding);
        if (!AgentPermissionScopeSubset.IsWorkflowVerifierReadOnly(profile.PermissionCeiling)
            || binding.LaneId != identity.LaneId || binding.OwnershipId != identity.OwnershipId
            || !IsOid(binding.Tree) || !IsOid(binding.Head))
            return new(null, null, null, GitWorktreeErrorCode.OwnershipMismatch);

        var objective = BoundText(task.Objective);
        var boundedWorkingState = BoundText(workingState);
        if (objective is null || boundedWorkingState is null
            || delegation.PacketRef.Kind != ArtifactKind.Other
            || delegation.PacketRef.MediaType != ContextInheritanceService.DelegationPacketMediaType
            || delegation.PacketRef.Sensitivity != Sensitivity.Sensitive
            || delegation.PacketRef.Size <= 0
            || delegation.PacketRef.Size > ContextInheritanceService.MaximumDelegationPacketBytes
            || !artifacts.Verify(delegation.PacketRef.Hash, delegation.PacketRef.Size)
            || artifacts is not IArtifactPreparationStore preparation)
            return new(identity, null, null, GitWorktreeErrorCode.UnsupportedOwnershipMetadata);

        var packet = new TaskPacket(1, session, run, task.TaskId, delegation.ChildLaneId,
            execution, delegation.DelegationId, delegation.ParentExecutionId, profile.Id,
            objective, task.Budget, delegation.PacketRef, identity.OwnershipId,
            identity.RepositoryRoot, identity.GitCommonDirectory, identity.WorktreePath,
            identity.Head, identity.SnapshotCommit, identity.SnapshotTree,
            WorktreeBase.SnapshotOfWorkingTree, boundedWorkingState, binding);
        var json = JsonSerializer.Serialize(packet, TaskPacketJsonContext.Default.TaskPacket);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumTaskPacketBytes)
            return new(identity, packet, null, GitWorktreeErrorCode.UnsupportedOwnershipMetadata);
        var prepared = preparation.PrepareText(json, TaskPacketMediaType, ArtifactKind.Other, Sensitivity.Sensitive);
        if (prepared.Reference.Kind != ArtifactKind.Other || prepared.Reference.MediaType != TaskPacketMediaType
            || prepared.Reference.Sensitivity != Sensitivity.Sensitive
            || prepared.Reference.Size is <= 0 or > MaximumTaskPacketBytes)
            return new(identity, packet, null, GitWorktreeErrorCode.UnsupportedOwnershipMetadata);
        return new(identity, packet, prepared.Reference, null, prepared);
    }

    /// <summary>Publishes on the caller's synchronous Host thread and holds the CAS lease until
    /// the caller's journal Barrier. Publication leases are thread-bound; they must not cross the
    /// asynchronous Git provisioning boundary.</summary>
    internal LaneWorktreeProvisionOutcome Publish(LaneWorktreeProvisionOutcome pending,
        CancellationToken cancellationToken)
    {
        if (!pending.Succeeded || pending.Identity is null || pending.Packet is null
            || pending.TaskPacketRef is null || pending.PreparedArtifact is null
            || artifacts is not IArtifactPublicationLease publication)
            return new(pending.Identity, pending.Packet, null,
                pending.Error ?? GitWorktreeErrorCode.UnsupportedOwnershipMetadata);
        var lease = publication.AcquirePublicationLease(cancellationToken);
        try
        {
            var prepared = pending.PreparedArtifact;
            if (prepared.Publish() != pending.TaskPacketRef || pending.TaskPacketRef.Size is <= 0 or > MaximumTaskPacketBytes)
            {
                lease.Dispose();
                return new(pending.Identity, pending.Packet, null, GitWorktreeErrorCode.UnsupportedOwnershipMetadata);
            }
            if (!artifacts.Verify(pending.TaskPacketRef.Hash, pending.TaskPacketRef.Size))
            {
                lease.Dispose();
                return new(pending.Identity, pending.Packet, null, GitWorktreeErrorCode.UnsupportedOwnershipMetadata);
            }
            var durableText = artifacts.GetText(pending.TaskPacketRef.Hash);
            if (durableText is null)
            {
                lease.Dispose();
                return new(pending.Identity, pending.Packet, null, GitWorktreeErrorCode.UnsupportedOwnershipMetadata);
            }
            if (System.Text.Encoding.UTF8.GetByteCount(durableText) > MaximumTaskPacketBytes)
            {
                lease.Dispose();
                return new(pending.Identity, pending.Packet, null, GitWorktreeErrorCode.UnsupportedOwnershipMetadata);
            }
            var durablePacket = JsonSerializer.Deserialize(durableText, TaskPacketJsonContext.Default.TaskPacket);
            if (!Matches(durablePacket, pending.Packet, pending.Identity))
            {
                lease.Dispose();
                return new(pending.Identity, pending.Packet, null, GitWorktreeErrorCode.UnsupportedOwnershipMetadata);
            }
            return new(pending.Identity, durablePacket!, pending.TaskPacketRef, null, null, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static bool Matches(TaskPacket? durable, TaskPacket expected, WorktreeIdentity identity) =>
        durable is not null && durable.SchemaVersion == 1
        && durable.SessionId == expected.SessionId && durable.RunId == expected.RunId
        && durable.TaskId == expected.TaskId && durable.LaneId == expected.LaneId
        && durable.ExecutionId == expected.ExecutionId && durable.DelegationId == expected.DelegationId
        && durable.ParentExecutionId == expected.ParentExecutionId && durable.ProfileId == expected.ProfileId
        && durable.Budget == expected.Budget
        && durable.OwnershipId == identity.OwnershipId
        && SamePath(durable.RepositoryRoot, identity.RepositoryRoot)
        && SamePath(durable.GitCommonDirectory, identity.GitCommonDirectory)
        && SamePath(durable.WorktreePath, identity.WorktreePath)
        && durable.HeadCommit == identity.Head && durable.SnapshotCommit == identity.SnapshotCommit
        && durable.SnapshotTree == identity.SnapshotTree && durable.Base == WorktreeBase.SnapshotOfWorkingTree
        && durable.SelectedProjectionRef == expected.SelectedProjectionRef
        && durable.WorkflowImplementation == expected.WorkflowImplementation
        && System.Text.Encoding.UTF8.GetByteCount(durable.Objective) <= 64 * 1024
        && System.Text.Encoding.UTF8.GetByteCount(durable.WorkingState) <= 64 * 1024;

    private static bool SamePath(string left, string right) => string.Equals(
        ProjectIdentity.CanonicalWorkspacePath(left), ProjectIdentity.CanonicalWorkspacePath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? BoundText(string value)
    {
        var redacted = new PiiRedactor().Redact(value ?? string.Empty);
        return System.Text.Encoding.UTF8.GetByteCount(redacted) <= 64 * 1024 ? redacted : null;
    }

    private static bool IsOid(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(TaskPacket))]
internal sealed partial class TaskPacketJsonContext : JsonSerializerContext;
