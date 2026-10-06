namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Infrastructure;

internal static class UserDailyBudgetContinuation
{
    internal static decimal Limit(UserWorkspaceSpendReader? reader, IEventCodecRegistry codecs,
        string day, decimal baseline)
    {
        var result = baseline;
        if (reader is null) return result;
        foreach (var workspace in reader.ReadOtherWorkspaces())
        {
            // Pair request/resolution within its original journal. Copied IDs in another
            // workspace must never supply a missing half of a consent interaction.
            var limit = BudgetContinuation.Limit(workspace.Events.Where(evt =>
                evt.Type.ToString() is "interaction.requested" or "interaction.resolved" or "interaction.expired"),
                codecs, new SessionId(Guid.Empty), new RunId(Guid.Empty), day, "daily", baseline);
            result = Math.Max(result, limit);
        }
        return result;
    }
}
