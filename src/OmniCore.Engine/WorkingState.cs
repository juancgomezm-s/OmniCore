namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Proyección WorkingState (ADR-0016 §7): se reconstruye en cada Turn desde el Journal.
/// Nunca se poda ni comprime; entra al contexto con Priority=Pinned y se regenera por Turn.
/// </summary>
public sealed class WorkingStateProjector
{
    /// <summary>Construye el WorkingState desde las proyecciones canónicas.</summary>
    public static WorkingState Project(RunProjection run, PlanProjection plan)
    {
        var items = new List<WorkingStateItem>();
        var pending = new List<string>();
        var blockers = new List<string>();
        PlanItemId? current = null;
        TaskId? currentTask = null;

        foreach (var item in plan.Items())
        {
            var task = FirstLinkedTaskId(item);
            items.Add(new WorkingStateItem(item.Id, item.State, item.Description, task));
            if (current is null && item.State == PlanItemState.InProgress)
            {
                current = item.Id;
                currentTask = task;
            }

            if (item.State == PlanItemState.Blocked)
            {
                blockers.Add(item.Description);
            }

            if (item.State == PlanItemState.Pending || item.State == PlanItemState.Ready)
            {
                pending.Add(item.Description);
            }
        }

        var criteria = new string[0];
        var nextWork = NextWork(plan.Items());
        return new WorkingState(
            run.Objective ?? string.Empty,
            plan.Revision(),
            items.ToArray(),
            current,
            currentTask,
            criteria,
            blockers.ToArray(),
            nextWork);
    }

    private static TaskId? FirstLinkedTaskId(PlanItem item) =>
        item.LinkedTasks.Count == 0 ? null : item.LinkedTasks[0].TaskId;

    /// <summary>Describe la siguiente pieza de trabajo esperada (primer Ready/InProgress por orden).</summary>
    private static string? NextWork(IReadOnlyList<PlanItem> items)
    {
        var bestOrder = int.MaxValue;
        string? best = null;
        foreach (var item in items)
        {
            if (item.State == PlanItemState.Ready || item.State == PlanItemState.InProgress)
            {
                if (item.Order < bestOrder)
                {
                    bestOrder = item.Order;
                    best = item.Description;
                }
            }
        }

        return best;
    }

    /// <summary>Representación compacta y estable para el contexto (ADR-0016 §7).</summary>
    public static string Render(WorkingState ws)
    {
        var lines = new List<string>();
        lines.Add("Plan rev." + ws.PlanRevision + " - Objetivo: " + ws.RunObjective);
        foreach (var item in ws.Items)
        {
            var mark = GlyphFor(item.State);
            var suffix = item.Task is null ? "" : "  [" + item.Task + "]";
            lines.Add(mark + " " + item.Description + suffix);
        }

        lines.Add("Blockers: " + (ws.Blockers.Count == 0 ? "-" : string.Join(", ", ws.Blockers))
            + "   Siguiente: " + (ws.NextExpectedWork ?? "-"));
        return string.Join("\n", lines);
    }

    private static string GlyphFor(PlanItemState state)
    {
        switch (state)
        {
            case PlanItemState.Completed: return "✓";
            case PlanItemState.InProgress: return "→";
            case PlanItemState.Blocked: return "◐";
            case PlanItemState.Failed: return "✗";
            case PlanItemState.Skipped: return "–";
            case PlanItemState.Cancelled: return "×";
            default: return "○";
        }
    }
}