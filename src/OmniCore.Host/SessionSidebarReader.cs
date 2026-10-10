using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Protocol;

namespace OmniCore.Host;

/// <summary>Projects existing canonical state without filesystem scans, provider calls or journal writes.</summary>
internal static class SessionSidebarReader
{
    public static SessionSidebarSnapshot Read(IEventStore store, IEventCodecRegistry codecs,
        SessionId session, RunId? run, bool recoveryBlocked)
    {
        var all = store.ReadFrom(session, 1).Where(e => e.SessionId == session).ToArray();
        var sequence = all.LastOrDefault()?.Sequence ?? 0;
        // A session has no canonical title yet: use its first Run objective as a stable display fallback.
        var title = all.Where(e => e.Type.ToString() == "run.created").Select(codecs.Decode)
            .OfType<RunCreated>().FirstOrDefault(e => e.SessionId == session)?.Objective ?? "";
        static string Safe(string text) => new PiiRedactor().Redact(text);
        var empty = new SessionSidebarSnapshot(session.ToString(), run?.ToString(), sequence,
            Safe(title), null, null, null, null, recoveryBlocked, false);
        if (run is null) return empty;
        // Membership is durable envelope ownership, never proximity to the last run.created.
        var own = all.Where(e => e.RunId == run).ToArray();
        if (!own.Any(e => e.Type.ToString() == "run.created")) return empty with { ProjectionUnavailable = true };
        try
        {
            var projected = RunProjection.Replay(session, run, codecs, own);
            var plan = PlanProjection.Replay(codecs, own);
            var latest = plan.Latest();
            var view = latest is null ? null : new SidebarPlan(latest.Id.ToString(), latest.RunId.ToString(),
                latest.Revision, plan.Items().Select(item => new SidebarPlanItem(item.Id.ToString(),
                    item.ParentId?.ToString(), Safe(item.Description), item.State.ToString(), item.Order)).ToArray());
            if (view is not null && view.RunId != run.ToString())
                return empty with { ProjectionUnavailable = true };
            // La actividad se deriva del journal del Run (INV-027); sin Lane raíz o con la Lane cerrada no hay.
            var rootLane = projected.RootTask is { } rootTask
                ? LaneProjection.Replay(codecs, own).ForTask(rootTask).FirstOrDefault()?.Id : null;
            var activity = rootLane is null ? LaneActivity.None : LaneActivityProjection.Derive(codecs, own, rootLane);
            return empty with { Objective = Safe(projected.Objective ?? ""), Mode = projected.Mode?.ToString().ToLowerInvariant(),
                RunState = projected.State.ToString(), Plan = view,
                Activity = activity == LaneActivity.None ? null : activity.ToString() };
        }
        catch (InvalidStateTransitionException)
        {
            // Invalid journal state is explicitly unavailable, never a fabricated empty/healthy plan.
            return empty with { ProjectionUnavailable = true };
        }
    }
}
