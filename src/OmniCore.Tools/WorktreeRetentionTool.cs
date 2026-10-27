namespace OmniCore.Tools;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Native-human-only pruning of exact, expired private refs from one terminal historical Run.</summary>
public sealed class WorktreeRetentionTool : ITool, ITrustedWorktreeRetentionTool,
    IHostResolvedAuthorizationIntent, INativeUserActionOnlyTool
{
    private readonly IWorktreeRetentionCoordinator _coordinator;
    private readonly ToolDescriptor _descriptor = new(new ToolId("worktree.retention"),
        "Prunes only the exact expired private snapshot references reviewed for a terminal Run in this Session.",
        new InputSchema("{\"type\":\"object\",\"properties\":{\"ownerRunId\":{\"type\":\"string\"},\"ownershipId\":{\"type\":\"string\"},\"removedAtUtc\":{\"type\":\"string\"},\"references\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"expectedObjectId\":{\"type\":\"string\"}},\"required\":[\"name\",\"expectedObjectId\"],\"additionalProperties\":false},\"minItems\":1,\"maxItems\":2}},\"required\":[\"ownerRunId\",\"ownershipId\",\"removedAtUtc\",\"references\"],\"additionalProperties\":false}"),
        ["worktree", "retention"], readOnly: false, destructive: true, risk: ToolRisk.High,
        source: ComponentSource.Core(), protection: ToolProtection.Protected,
        effectClass: EffectClass.Reconcilable);

    public WorktreeRetentionTool(IWorktreeRetentionCoordinator coordinator) =>
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public ToolDescriptor Descriptor => _descriptor;

    public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
    {
        if (!context.IsNativeUserAction) return Rejected("Worktree retention requires an admitted native user action.");
        if (!TryParse(call.NormalizedArgumentsJson, out _, out _, out _, out _))
            return Rejected("A reviewed terminal owner Run, ownership and exact ref set are required.");
        return new Prepared(new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
            EffectClass.Reconcilable, ResourceClaims.Empty(), ToolRisk.High, null));
    }

    public ToolIntent ResolveHostAuthorizationIntent(ToolIntent intent, ToolPreparationContext preparation,
        ToolExecutionContext execution, CancellationToken cancellationToken)
    {
        if (!preparation.IsNativeUserAction || !TryParse(intent.NormalizedArgumentsJson, out _, out _, out _, out _))
            throw new ToolPreflightException(ToolErrorCode.PermissionDenied, "Worktree retention requires a native user action.");
        return _coordinator.ResolveAuthorizationIntent(intent, preparation, execution, cancellationToken);
    }

    public ReconciliationSpec? DescribeReconciliation(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken) => _coordinator.Prepare(intent, context, cancellationToken);

    public bool ValidateWorktreeRetentionEnvelope(ToolIntent intent, ToolExecutionContext context,
        ReconciliationSpec reconciliation) => _coordinator.ValidateEnvelope(intent, context, reconciliation);

    public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (intent.Intent.ToolId.ToString() != Descriptor.Id.ToString() || !context.IsNativeUserAction)
            throw new InvalidOperationException("Worktree retention lost its native user scope.");
        return _coordinator.ApplyAsync(intent, context, cancellationToken);
    }

    private static PreparationRejected Rejected(string message) => new(message, null, ToolErrorCode.InvalidArguments);

    public static bool TryParse(string json, out string ownerRunId, out string ownershipId,
        out DateTimeOffset removedAtUtc, out WorktreeSnapshotReference[] references)
    {
        ownerRunId = ownershipId = string.Empty;
        removedAtUtc = default;
        references = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4
                || !root.TryGetProperty("ownerRunId", out var runNode) || runNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("ownershipId", out var ownershipNode) || ownershipNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("removedAtUtc", out var removedNode) || removedNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("references", out var refsNode) || refsNode.ValueKind != JsonValueKind.Array
                || refsNode.GetArrayLength() is < 1 or > 2) return false;
            ownerRunId = runNode.GetString()!;
            ownershipId = ownershipNode.GetString()!;
            if (!Guid.TryParseExact(ownerRunId, "D", out var runGuid) || ownerRunId != new RunId(runGuid).ToString()
                || !Guid.TryParseExact(ownershipId, "N", out _) || !DateTimeOffset.TryParse(removedNode.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind,
                    out removedAtUtc) || removedAtUtc.Offset != TimeSpan.Zero) return false;
            var items = new List<WorktreeSnapshotReference>();
            foreach (var item in refsNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 2
                    || !item.TryGetProperty("name", out var nameNode) || nameNode.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("expectedObjectId", out var oidNode) || oidNode.ValueKind != JsonValueKind.String)
                    return false;
                var name = nameNode.GetString()!;
                var oid = oidNode.GetString()!;
                if (!(name.StartsWith("refs/omnicore/snapshots/", StringComparison.Ordinal)
                    || name.StartsWith("refs/omnicore/retained-lanes/", StringComparison.Ordinal))
                    || oid.Length is not (40 or 64) || !oid.All(Uri.IsHexDigit)) return false;
                items.Add(new(name, oid));
            }
            references = items.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
            return references.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() == references.Length;
        }
        catch (JsonException) { return false; }
    }
}
