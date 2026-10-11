namespace OmniCore.Host;

using OmniCore.Domain;

/// <summary>Canonical bounded accounting for external built-in tool activity.</summary>
internal static class ExternalLaneAccounting
{
    internal static long ToolUseCount(IEnumerable<ExternalLaneUsageRecorded> receipts,
        IEnumerable<ExternalLaneActivityObserved>? activities = null)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        var receiptEntries = receipts.ToArray();
        var activityEntries = activities?.ToArray() ?? [];
        if (receiptEntries.Any(receipt => receipt.ToolUseCount < 0)
            || activityEntries.Any(activity => activity.ToolUseCount < 0)
            || receiptEntries.GroupBy(receipt => receipt.ExecutionId).Any(group => group.Count() != 1)
            || activityEntries.GroupBy(activity => activity.ExecutionId).Any(group => group.Count() != 1))
            throw new InvalidDataException("External lane tool usage receipts are invalid or duplicated.");
        var receiptsByExecution = receiptEntries.ToDictionary(receipt => receipt.ExecutionId);
        foreach (var activity in activityEntries)
            if (receiptsByExecution.TryGetValue(activity.ExecutionId, out var receipt)
                && (receipt.LaneId != activity.LaneId || receipt.ToolUseCount != activity.ToolUseCount))
                throw new InvalidDataException("External lane activity and usage receipts disagree.");
        long total = 0;
        var activitiesByExecution = activityEntries.ToDictionary(activity => activity.ExecutionId);
        foreach (var receipt in receiptEntries)
            total = checked(total + (activitiesByExecution.TryGetValue(receipt.ExecutionId, out var activity)
                ? activity.ToolUseCount : receipt.ToolUseCount));
        foreach (var activity in activityEntries)
            if (!receiptsByExecution.ContainsKey(activity.ExecutionId)) total = checked(total + activity.ToolUseCount);
        return total;
    }
}
