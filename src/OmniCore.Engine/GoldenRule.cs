namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Golden rule (ADR-0041 §2): el estado reconstruido desde el journal es idéntico al estado vivo.
/// Las dos fuentes son independientes:
/// <list type="bullet">
/// <item><b>vivo</b>: lo que el <see cref="EventStream"/> aplicó en memoria mientras escribía, con los
/// payloads tal como se crearon (sin codecs, sin store);</item>
/// <item><b>reconstruido</b>: releer el journal, decodificar cada evento (con upcasters) y aplicarlo.</item>
/// </list>
/// Compara el estado canónico de todas las entidades y las proyecciones de Plan, Tasks y Lanes.
/// Un fallo aquí señala un codec que pierde datos, un store que reordena o una proyección que
/// depende de algo que no está en los eventos.
/// </summary>
public static class GoldenRule
{
    /// <summary>
    /// Devuelve las diferencias (vacío si la regla se cumple). <paramref name="live"/> debe ser el
    /// único escritor de la sesión, porque su historia viva son solo los eventos que escribió.
    /// </summary>
    public static IReadOnlyList<string> Check(IEventCodecRegistry codecs, IReadOnlyList<DomainEvent> journal,
        EventStream live)
    {
        ArgumentNullException.ThrowIfNull(codecs);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(live);

        var diffs = new List<string>();
        Compare("estado canónico", live.LiveState().Snapshot(),
            CanonicalStateTracker.Replay(codecs, journal).Snapshot(), diffs);

        var payloads = live.WrittenPayloads;
        Compare("plan", Describe(PlanProjection.FromPayloads(payloads)), Describe(PlanProjection.Replay(codecs, journal)),
            diffs);
        Compare("tasks", Describe(TaskGraphProjection.FromPayloads(payloads)),
            Describe(TaskGraphProjection.Replay(codecs, journal)), diffs);
        Compare("lanes", Describe(LaneProjection.FromPayloads(payloads)), Describe(LaneProjection.Replay(codecs, journal)),
            diffs);
        return diffs;
    }

    private static IReadOnlyList<string> Describe(PlanProjection plan) =>
        plan.Items()
            .Select(item => item.Id + "|" + item.Description + "|" + item.State + "|" + item.Order + "|"
                + item.ParentId + "|" + item.Required + "|"
                + string.Join(",", item.LinkedTasks.Select(link => link.TaskId + ":" + link.Role + ":" + link.Required)))
            .Order(StringComparer.Ordinal)
            .Prepend("revision=" + plan.Revision())
            .ToArray();

    private static IReadOnlyList<string> Describe(TaskGraphProjection tasks) =>
        tasks.Tasks()
            .Select(task => task.Id + "|" + task.Objective + "|" + task.State + "|"
                + string.Join(",", task.Dependencies.Select(dep => dep.TaskId + ":" + dep.Required)))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> Describe(LaneProjection lanes) =>
        lanes.Lanes()
            .Select(lane => lane.Id + "|" + lane.TaskId + "|" + lane.State)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static void Compare(string what, IReadOnlyList<string> live, IReadOnlyList<string> replayed,
        List<string> diffs)
    {
        var onlyLive = live.Except(replayed, StringComparer.Ordinal).ToArray();
        var onlyReplayed = replayed.Except(live, StringComparer.Ordinal).ToArray();
        if (live.Count == replayed.Count && onlyLive.Length == 0 && onlyReplayed.Length == 0)
        {
            return;
        }

        diffs.Add("golden rule (" + what + "): vivo≠journal; solo vivo=[" + string.Join("; ", onlyLive)
            + "] solo journal=[" + string.Join("; ", onlyReplayed) + "]");
    }
}
