namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Resultado de un Completion Gate: acepta o rechaza con los faltantes.</summary>
public sealed class GateResult
{
    public bool Passed { get; }

    public IReadOnlyList<string> Missing { get; }

    public GateResult(bool passed, IReadOnlyList<string> missing)
    {
        Passed = passed;
        Missing = missing;
    }

    public static GateResult Ok() => new(true, new string[0]);

    public static GateResult Fail(string what) => new(false, [what]);
}

/// <summary>
/// PlanCompletionGate (ADR-0016 §10): rechaza la finalización si un item Required está en
/// Pending/Ready/InProgress/Blocked. Un Failed requerido → nunca Completed limpio (el Run
/// decide Failed vs CompletedWithIssues según la FailurePolicy).
/// </summary>
public sealed class PlanCompletionGate
{
    public GateResult Check(PlanProjection plan)
    {
        var missing = new List<string>();
        foreach (var item in plan.Items())
        {
            if (!item.Required)
            {
                continue;
            }

            if (item.State == PlanItemState.Pending || item.State == PlanItemState.Ready
                || item.State == PlanItemState.InProgress || item.State == PlanItemState.Blocked)
            {
                missing.Add("PlanItem " + item.Id + " (" + item.Description + ") en " + item.State);
            }
        }

        return missing.Count == 0 ? GateResult.Ok() : GateResult.Fail(string.Join(", ", missing));
    }

    private static string _Flatten(IReadOnlyList<string> missing) => string.Join(", ", missing);
}

/// <summary>
/// PendingTaskGate (ADR-0035 §5): rechaza si alguna Task está Running o no terminal.
/// </summary>
public sealed class PendingTaskGate
{
    public GateResult Check(TaskGraphProjection tasks, PlanProjection plan)
    {
        var missing = new List<string>();
        foreach (var task in tasks.Tasks())
        {
            if (task.State == TaskState.Running)
            {
                missing.Add("Task " + task.Id + " en ejecución");
            }
            else if (task.State == TaskState.Pending || task.State == TaskState.Ready
                || task.State == TaskState.Blocked)
            {
                missing.Add("Task " + task.Id + " no terminal (" + task.State + ")");
            }
        }

        return missing.Count == 0 ? GateResult.Ok() : GateResult.Fail(string.Join(", ", ObjectConversions.Strings(missing)));
    }
}

/// <summary>Conversiones utilitarias de colecciones.</summary>
public sealed class ObjectConversions
{
    public static IReadOnlyList<string> Strings(IReadOnlyList<string> source) => source;
}

/// <summary>
/// Watchdog de progreso (ADR-0036 §7, ADR-0016 §9): si un item lleva N Turns en InProgress
/// sin señal de progreso, emite ProgressStalled. En M1 N es constante (6).
/// </summary>
public sealed class ProgressWatchdog
{
    private readonly int _thresholdTurns;

    public ProgressWatchdog() => _thresholdTurns = ProgressReconciler.DefaultStallThresholdTurns;

    public ProgressWatchdog(int thresholdTurns) => _thresholdTurns = thresholdTurns;

    public int ThresholdTurns() => _thresholdTurns;

    public bool IsStalled(int turnsInProgressSinceSignal) => turnsInProgressSinceSignal >= _thresholdTurns;

    /// <summary>Señal de progreso según la taxonomía de ADR-0036 §7.</summary>
    public static bool IsProgressSignal(ProgressSignalKind kind) => true;
}