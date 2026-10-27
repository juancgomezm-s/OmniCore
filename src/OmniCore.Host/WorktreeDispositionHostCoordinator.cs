namespace OmniCore.Host;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Host-owned coordinator for a human-admitted disposition of an exact terminal lane tree.</summary>
internal sealed class WorktreeDispositionHostCoordinator : IWorktreeDispositionCoordinator
{
    private readonly Func<string, WorktreeDispositionOperation, WorktreeDispositionClaim?> _resolve;
    private readonly Func<WorktreeDispositionClaim, bool> _validate;
    private readonly Func<WorktreeDispositionClaim, CancellationToken, Task<WorktreeCleanupOutcome>> _apply;
    private readonly IProtectedArtifactStore _protectedArtifacts;
    private readonly IArtifactStore _artifacts;
    private readonly ConcurrentDictionary<string, (WorktreeDispositionClaim Claim, ArtifactRef Receipt)> _prepared = new(StringComparer.Ordinal);

    internal WorktreeDispositionHostCoordinator(
        Func<string, WorktreeDispositionOperation, WorktreeDispositionClaim?> resolve,
        Func<WorktreeDispositionClaim, bool> validate,
        Func<WorktreeDispositionClaim, CancellationToken, Task<WorktreeCleanupOutcome>> apply,
        IArtifactStore artifacts)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _validate = validate ?? throw new ArgumentNullException(nameof(validate));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _protectedArtifacts = artifacts as IProtectedArtifactStore
            ?? throw new ArgumentException("Protected disposition receipts are required.", nameof(artifacts));
    }

    public ToolIntent ResolveAuthorizationIntent(ToolIntent intent, ToolPreparationContext preparation,
        ToolExecutionContext execution, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!preparation.IsNativeUserAction || !execution.IsNativeUserAction)
            throw new ToolPreflightException(ToolErrorCode.PermissionDenied,
                "Worktree disposition requires an admitted native user action.");
        var operation = Operation(intent.ToolId.ToString());
        var claim = _resolve(intent.NormalizedArgumentsJson, operation);
        if (claim is null)
            throw new ToolPreflightException(ToolErrorCode.StaleWrite,
                "The reviewed worktree ownership or terminal lane scope changed.");
        return new ToolIntent(intent.ToolCallId, intent.ToolId, intent.NormalizedArgumentsJson,
            intent.Effect, new ResourceClaims(intent.Claims.Reads, intent.Claims.Writes,
                intent.Claims.Network, intent.Claims.Process, intent.Claims.Secrets, claim), intent.Risk,
            intent.Reconciliation, intent.EffectiveMutationOperation, intent.RequiresPhysicalCreateCapability,
            intent.EffectiveMutationHistoryRevision, intent.EffectiveMutationProposal);
    }

    public ReconciliationSpec Prepare(AuthorizedToolIntent authorized, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var claim = authorized.Intent.Claims.WorktreeDisposition
            ?? throw new ToolPreflightException(ToolErrorCode.PermissionDenied, "Disposition ownership is not authorized.");
        if (!context.IsNativeUserAction || !ReferenceEquals(context.Artifacts, _artifacts) || !_validate(claim))
            throw new ToolPreflightException(ToolErrorCode.StaleWrite,
                "The reviewed worktree changed before disposition was prepared.");
        var receiptJson = JsonSerializer.Serialize(new WorktreeDispositionReceipt(1, claim),
            WorktreeDispositionJsonContext.Default.WorktreeDispositionReceipt);
        var receipt = _protectedArtifacts.PutProtectedText(receiptJson,
            "worktree-disposition:" + claim.WorkspaceId + ":" + authorized.Intent.ToolCallId,
            "application/json", ArtifactKind.Patch);
        if (!_artifacts.Verify(receipt.Hash, receipt.Size))
            throw new InvalidDataException("Protected worktree disposition receipt failed verification.");
        _prepared[authorized.Intent.ToolCallId.ToString()] = (claim, receipt);
        return new ReconciliationSpec(null, null, null)
        {
            BeforeStateRef = receipt,
            DurableMetadataJson = receiptJson,
            Reversibility = Reversibility.Unknown,
        };
    }

    public bool ValidateEnvelope(ToolIntent intent, ToolExecutionContext context,
        ReconciliationSpec reconciliation)
    {
        try
        {
            if (context.Artifacts is not IProtectedArtifactStore protectedArtifacts
                || !ReferenceEquals(context.Artifacts, _artifacts)
                || intent.Claims.WorktreeDisposition is not { } claim
                || reconciliation.BeforeStateRef is not { } reference
                || string.IsNullOrWhiteSpace(reconciliation.DurableMetadataJson)
                || reconciliation.StartedEntityEvent is not null
                || reconciliation.ExpectedPreHash is not null || reconciliation.ExpectedPostHash is not null
                || reconciliation.IdempotencyKey is not null || reconciliation.Reversibility != Reversibility.Unknown
                || !_artifacts.Verify(reference.Hash, reference.Size)) return false;
            var purpose = "worktree-disposition:" + claim.WorkspaceId + ":" + intent.ToolCallId;
            var protectedJson = protectedArtifacts.GetProtectedText(reference, purpose);
            if (!string.Equals(protectedJson, reconciliation.DurableMetadataJson, StringComparison.Ordinal)) return false;
            var durable = JsonSerializer.Deserialize(protectedJson,
                WorktreeDispositionJsonContext.Default.WorktreeDispositionReceipt);
            return durable is { Version: 1 } && durable.Claim == claim;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or JsonException or ArgumentException)
        { return false; }
    }

    public async Task<ToolResult> ApplyAsync(AuthorizedToolIntent authorized, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_prepared.TryRemove(authorized.Intent.ToolCallId.ToString(), out var prepared)
            || prepared.Claim != authorized.Intent.Claims.WorktreeDisposition
            || !context.IsNativeUserAction || !ReferenceEquals(context.Artifacts, _artifacts)
            || !_artifacts.Verify(prepared.Receipt.Hash, prepared.Receipt.Size)
            || !_validate(prepared.Claim))
            return Error(ToolErrorCode.StaleWrite, "The reviewed worktree changed; it was not removed.");

        if (context.WorkspaceWriteLeases is null)
            return Error(ToolErrorCode.ToolFailure, "Workspace mutation exclusion is unavailable.");
        IDisposable? lease;
        try { lease = context.WorkspaceWriteLeases.Acquire(prepared.Claim.WorkspaceRoot, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return Error(ToolErrorCode.ToolFailure, "Workspace mutation exclusion could not be acquired."); }
        if (lease is null) return Error(ToolErrorCode.ToolFailure, "Workspace mutation exclusion is unavailable.");
        try
        {
            if (!_validate(prepared.Claim))
                return Error(ToolErrorCode.StaleWrite, "The reviewed worktree changed; it was not removed.");
            var outcome = await _apply(prepared.Claim, cancellationToken).ConfigureAwait(false);
            if (!outcome.Removed)
                return Unknown(ToolErrorCode.UnknownEffect,
                    "Worktree disposition did not complete: " + (outcome.Error?.ToString() ?? "unknown"));
            return new ToolResult(prepared.Claim.Operation == WorktreeDispositionOperation.Cleanup
                    ? "Terminal worktree cleaned up and retained safely."
                    : "Reviewed terminal worktree archived and discarded safely.",
                null, null, 0, false, EffectOutcome.Applied);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Unknown(ToolErrorCode.UnknownEffect, "Worktree disposition outcome is unknown; do not retry automatically.");
        }
        finally { lease?.Dispose(); }
    }

    private static WorktreeDispositionOperation Operation(string toolId) => toolId switch
    {
        "worktree.cleanup" => WorktreeDispositionOperation.Cleanup,
        "worktree.discard" => WorktreeDispositionOperation.Discard,
        _ => throw new ToolPreflightException(ToolErrorCode.PermissionDenied, "Unknown disposition operation."),
    };

    private static ToolResult Error(ToolErrorCode code, string summary) =>
        new(summary, null, null, 0, false, EffectOutcome.None, true, code);

    private static ToolResult Unknown(ToolErrorCode code, string summary) =>
        new(summary, null, null, 0, false, EffectOutcome.Unknown, true, code);

}

internal sealed record WorktreeDispositionReceipt(int Version, WorktreeDispositionClaim Claim);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(WorktreeDispositionReceipt))]
internal sealed partial class WorktreeDispositionJsonContext : JsonSerializerContext;
