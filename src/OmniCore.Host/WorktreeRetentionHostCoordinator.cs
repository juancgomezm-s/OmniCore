namespace OmniCore.Host;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Tools;

internal sealed class WorktreeRetentionHostCoordinator : IWorktreeRetentionCoordinator
{
    private readonly Func<string, WorktreeRetentionClaim?> _resolve;
    private readonly Func<WorktreeRetentionClaim, bool> _validate;
    private readonly Func<WorktreeRetentionClaim, CancellationToken, Task<WorktreeRetentionPruneOutcome>> _apply;
    private readonly IProtectedArtifactStore _protectedArtifacts;
    private readonly IArtifactStore _artifacts;
    private readonly ConcurrentDictionary<string, (WorktreeRetentionClaim Claim, ArtifactRef Receipt)> _prepared = new(StringComparer.Ordinal);

    internal WorktreeRetentionHostCoordinator(Func<string, WorktreeRetentionClaim?> resolve,
        Func<WorktreeRetentionClaim, bool> validate,
        Func<WorktreeRetentionClaim, CancellationToken, Task<WorktreeRetentionPruneOutcome>> apply,
        IArtifactStore artifacts)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _validate = validate ?? throw new ArgumentNullException(nameof(validate));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _protectedArtifacts = artifacts as IProtectedArtifactStore
            ?? throw new ArgumentException("Protected retention receipts are required.", nameof(artifacts));
    }

    public ToolIntent ResolveAuthorizationIntent(ToolIntent intent, ToolPreparationContext preparation,
        ToolExecutionContext execution, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!preparation.IsNativeUserAction || !execution.IsNativeUserAction)
            throw new ToolPreflightException(ToolErrorCode.PermissionDenied, "Retention requires a native user action.");
        var claim = _resolve(intent.NormalizedArgumentsJson);
        if (claim is null) throw new ToolPreflightException(ToolErrorCode.StaleWrite,
            "The reviewed historical Run or exact refs are no longer eligible.");
        var claims = new ResourceClaims(intent.Claims.Reads, intent.Claims.Writes, intent.Claims.Network,
            intent.Claims.Process, intent.Claims.Secrets, intent.Claims.WorktreeDisposition, claim);
        return new ToolIntent(intent.ToolCallId, intent.ToolId, intent.NormalizedArgumentsJson, intent.Effect,
            claims, intent.Risk, intent.Reconciliation, intent.EffectiveMutationOperation,
            intent.RequiresPhysicalCreateCapability, intent.EffectiveMutationHistoryRevision,
            intent.EffectiveMutationProposal);
    }

    public ReconciliationSpec Prepare(AuthorizedToolIntent authorized, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var claim = authorized.Intent.Claims.WorktreeRetention
            ?? throw new ToolPreflightException(ToolErrorCode.PermissionDenied, "Retention ownership is not authorized.");
        if (!context.IsNativeUserAction || !ReferenceEquals(context.Artifacts, _artifacts) || !_validate(claim))
            throw new ToolPreflightException(ToolErrorCode.StaleWrite, "Reviewed refs changed before retention was prepared.");
        var json = JsonSerializer.Serialize(new WorktreeRetentionReceipt(1, claim),
            WorktreeRetentionJsonContext.Default.WorktreeRetentionReceipt);
        var receipt = _protectedArtifacts.PutProtectedText(json,
            "worktree-retention:" + claim.WorkspaceId + ":" + authorized.Intent.ToolCallId,
            "application/json", ArtifactKind.Patch);
        if (!_artifacts.Verify(receipt.Hash, receipt.Size)) throw new InvalidDataException("Retention receipt failed verification.");
        _prepared[authorized.Intent.ToolCallId.ToString()] = (claim, receipt);
        return new ReconciliationSpec(null, null, null)
        { BeforeStateRef = receipt, DurableMetadataJson = json, Reversibility = Reversibility.Unknown };
    }

    public bool ValidateEnvelope(ToolIntent intent, ToolExecutionContext context, ReconciliationSpec reconciliation)
    {
        try
        {
            if (context.Artifacts is not IProtectedArtifactStore protectedArtifacts
                || !ReferenceEquals(context.Artifacts, _artifacts) || intent.Claims.WorktreeRetention is not { } claim
                || reconciliation.BeforeStateRef is not { } reference || string.IsNullOrWhiteSpace(reconciliation.DurableMetadataJson)
                || reconciliation.StartedEntityEvent is not null || reconciliation.ExpectedPreHash is not null
                || reconciliation.ExpectedPostHash is not null || reconciliation.IdempotencyKey is not null
                || reconciliation.Reversibility != Reversibility.Unknown || !_artifacts.Verify(reference.Hash, reference.Size)) return false;
            var purpose = "worktree-retention:" + claim.WorkspaceId + ":" + intent.ToolCallId;
            var protectedJson = protectedArtifacts.GetProtectedText(reference, purpose);
            if (!string.Equals(protectedJson, reconciliation.DurableMetadataJson, StringComparison.Ordinal)) return false;
            var durable = JsonSerializer.Deserialize(protectedJson, WorktreeRetentionJsonContext.Default.WorktreeRetentionReceipt);
            return durable is { Version: 1 } && SameClaim(durable.Claim, claim);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or JsonException or ArgumentException) { return false; }
    }

    public async Task<ToolResult> ApplyAsync(AuthorizedToolIntent authorized, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_prepared.TryRemove(authorized.Intent.ToolCallId.ToString(), out var prepared)
            || authorized.Intent.Claims.WorktreeRetention is not { } expectedClaim
            || !SameClaim(prepared.Claim, expectedClaim) || !context.IsNativeUserAction
            || !ReferenceEquals(context.Artifacts, _artifacts) || !_artifacts.Verify(prepared.Receipt.Hash, prepared.Receipt.Size)
            || !_validate(prepared.Claim))
            return Error(ToolErrorCode.StaleWrite, "Reviewed refs changed; none were pruned.");
        if (context.WorkspaceWriteLeases is null) return Error(ToolErrorCode.ToolFailure, "Workspace mutation exclusion is unavailable.");
        IDisposable? lease;
        try { lease = context.WorkspaceWriteLeases.Acquire(prepared.Claim.WorkspaceRoot, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return Error(ToolErrorCode.ToolFailure, "Workspace mutation exclusion could not be acquired."); }
        if (lease is null) return Error(ToolErrorCode.ToolFailure, "Workspace mutation exclusion is unavailable.");
        try
        {
            if (!_validate(prepared.Claim)) return Error(ToolErrorCode.StaleWrite, "Reviewed refs changed; none were pruned.");
            var result = await _apply(prepared.Claim, cancellationToken).ConfigureAwait(false);
            if (result.Error is not null || result.OutcomeUnknown || result.PrunedReferences != prepared.Claim.References.Count)
                return result.OutcomeUnknown || result.PrunedReferences > 0
                    ? Unknown("Retention outcome is partial or uncertain; do not retry automatically.")
                    : Error(result.Error == GitWorktreeErrorCode.WorkspaceChanged ? ToolErrorCode.StaleWrite : ToolErrorCode.ToolFailure,
                        "The exact reviewed refs were preserved: " + (result.Error?.ToString() ?? "unknown"));
            return new ToolResult("Expired private snapshot references pruned.", null, null, 0, false, EffectOutcome.Applied);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return Unknown("Retention outcome is unknown; do not retry automatically."); }
        finally { lease.Dispose(); }
    }

    private static bool SameClaim(WorktreeRetentionClaim left, WorktreeRetentionClaim right) =>
        left.SessionId == right.SessionId && left.ActionRunId == right.ActionRunId
        && left.TargetOwnerRunId == right.TargetOwnerRunId && left.WorkspaceId == right.WorkspaceId
        && left.WorkspaceRoot == right.WorkspaceRoot && left.LaneId == right.LaneId
        && left.OwnershipId == right.OwnershipId && left.SnapshotCommit == right.SnapshotCommit
        && left.GitCommonDirectory == right.GitCommonDirectory && left.WorktreePath == right.WorktreePath
        && left.RemovedAtUtc == right.RemovedAtUtc
        && left.References.SequenceEqual(right.References);

    private static ToolResult Error(ToolErrorCode code, string message) =>
        new(message, null, null, 0, false, EffectOutcome.None, true, code);
    private static ToolResult Unknown(string message) =>
        new(message, null, null, 0, false, EffectOutcome.Unknown, true, ToolErrorCode.UnknownEffect);
}

internal sealed record WorktreeRetentionReceipt(int Version, WorktreeRetentionClaim Claim);
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(WorktreeRetentionReceipt))]
internal sealed partial class WorktreeRetentionJsonContext : JsonSerializerContext;
