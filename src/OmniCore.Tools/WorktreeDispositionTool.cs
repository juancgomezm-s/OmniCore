namespace OmniCore.Tools;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Native-human-only disposition of one terminal, Host-owned lane worktree.</summary>
public sealed class WorktreeDispositionTool : ITool, ITrustedWorktreeDispositionTool,
    IHostResolvedAuthorizationIntent, INativeUserActionOnlyTool
{
    private readonly ToolDescriptor _descriptor;
    private readonly WorktreeDispositionOperation _operation;
    private readonly IWorktreeDispositionCoordinator _coordinator;

    public WorktreeDispositionTool(WorktreeDispositionOperation operation,
        IWorktreeDispositionCoordinator coordinator)
    {
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        _operation = operation;
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        var toolId = operation == WorktreeDispositionOperation.Cleanup ? "worktree.cleanup" : "worktree.discard";
        var description = operation == WorktreeDispositionOperation.Cleanup
            ? "Retires a terminal lane worktree after integration or when it is unchanged."
            : "Archives and discards the exact reviewed tree of a terminal lane worktree.";
        _descriptor = new ToolDescriptor(new ToolId(toolId), description,
            new InputSchema("{\"type\":\"object\",\"properties\":{\"laneId\":{\"type\":\"string\"},\"laneHead\":{\"type\":\"string\"},\"laneTree\":{\"type\":\"string\"}},\"required\":[\"laneId\",\"laneHead\",\"laneTree\"],\"additionalProperties\":false}"),
            ["worktree", "disposition", operation.ToString().ToLowerInvariant()], readOnly: false,
            destructive: true, risk: ToolRisk.High, source: ComponentSource.Core(),
            protection: ToolProtection.Protected, effectClass: EffectClass.Reconcilable);
    }

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        if (!context.IsNativeUserAction)
            return Rejected("Worktree disposition requires an admitted native user action.");
        if (!TryParse(call.NormalizedArgumentsJson, out var laneId, out var laneHead, out var laneTree))
            return Rejected("A public Lane id and its reviewed head/tree are required.");
        var intent = new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
            EffectClass.Reconcilable, ResourceClaims.Empty(), ToolRisk.High, null);
        return new Prepared(intent);
    }

    public ToolIntent ResolveHostAuthorizationIntent(ToolIntent intent, ToolPreparationContext preparation,
        ToolExecutionContext execution, CancellationToken cancellationToken)
    {
        if (!preparation.IsNativeUserAction || !TryParse(intent.NormalizedArgumentsJson,
                out _, out _, out _))
            throw new ToolPreflightException(ToolErrorCode.PermissionDenied,
                "Worktree disposition requires an admitted native user action.");
        return _coordinator.ResolveAuthorizationIntent(intent, preparation, execution, cancellationToken);
    }

    public ReconciliationSpec? DescribeReconciliation(AuthorizedToolIntent intent,
        ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (intent.Intent.ToolId.ToString() != Descriptor.Id.ToString())
            throw new InvalidOperationException("Worktree disposition authorization belongs to another operation.");
        return _coordinator.Prepare(intent, context, cancellationToken);
    }

    public bool ValidateWorktreeDispositionEnvelope(ToolIntent intent, ToolExecutionContext context,
        ReconciliationSpec reconciliation) => _coordinator.ValidateEnvelope(intent, context, reconciliation);

    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (intent.Intent.ToolId.ToString() != Descriptor.Id.ToString() || !context.IsNativeUserAction)
            throw new InvalidOperationException("Worktree disposition action lost its native user scope.");
        return _coordinator.ApplyAsync(intent, context, cancellationToken);
    }

    private static PreparationRejected Rejected(string message) =>
        new(message, null, ToolErrorCode.InvalidArguments);

    private static bool TryParse(string json, out string laneId, out string laneHead, out string laneTree)
    {
        laneId = laneHead = laneTree = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3
                || !root.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(["laneId", "laneHead", "laneTree"])
                || !root.TryGetProperty("laneId", out var lane) || lane.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("laneHead", out var head) || head.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("laneTree", out var tree) || tree.ValueKind != JsonValueKind.String) return false;
            laneId = lane.GetString() ?? "";
            laneHead = head.GetString() ?? "";
            laneTree = tree.GetString() ?? "";
            return Guid.TryParseExact(laneId, "D", out var laneGuid) && laneId == new LaneId(laneGuid).ToString()
                && IsGitObjectId(laneHead) && IsGitObjectId(laneTree);
        }
        catch (JsonException) { return false; }
    }

    private static bool IsGitObjectId(string value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
}
