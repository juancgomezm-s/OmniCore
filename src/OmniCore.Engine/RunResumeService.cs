namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Reanudación segura y reutilizable de un Run tras un crash (ADR-0004 §5, ADR-0041 §2): detecta
/// en el journal las ToolCalls <c>Started</c>-sin-outcome DENTRO del Run pedido, emite
/// <c>ToolCallEffectUnknown</c> —solo si aún no se emitió— y las reconcilia contra los metadatos
/// canónicos persistidos en su propio <c>ToolCallStarted</c> (ruta + hashes pre/post; ADR-0004 §4),
/// SIN re-ejecutar el efecto. Es IDEMPOTENTE y está scoped por Run. Lo usan por igual el
/// <c>SimulationEngine</c> (resume del sim) y el Host (recuperación al arrancar/continuar el Run
/// real de Explorer), de forma que la lógica no se duplica.
///
/// Contrato de seguridad: sin reconciliador o sin metadatos → <c>Unresolvable</c> (falla cerrado,
/// nunca <c>Applied</c>). Nunca reanuda un Run terminal ni toolcalls huérfanas de otros Runs.
/// </summary>
public sealed class RunResumeService
{
    private readonly IEventStore _store;

    private readonly IEventCodecRegistry _codecs;

    private readonly IFilesystemReconciler? _fsReconciler;

    private readonly string _workspaceRoot;

    public RunResumeService(IEventStore store, IEventCodecRegistry codecs,
        IFilesystemReconciler? reconciler, string workspaceRoot)
    {
        _store = store;
        _codecs = codecs;
        _fsReconciler = reconciler;
        _workspaceRoot = workspaceRoot;
    }

    /// <summary>
    /// Reanuda el <c>runId</c> de la sesión y devuelve cuántas ToolCalls se reconciliaron.
    /// Idempotente: <c>ToolCallEffectUnknown</c> es un estado INTERMEDIO (ADR-0004 §2) y no
    /// terminal, así que un crash entre <c>EffectUnknown</c> y <c>Reconciled</c> continúa la
    /// reconciliación en el siguiente resume sin duplicar el EffectUnknown; una ToolCall ya
    /// resuelta (Succeeded/Failed/Reconciled) no se vuelve a tocar. No resuelve un Run terminal
    /// ni toolcalls de otro Run.
    /// </summary>
    public int Resume(SessionId sessionId, RunId runId) =>
        ResumeRun(sessionId, runId) + ReconcileTerminalRuns(sessionId);

    /// <summary>
    /// Reconcilia los <c>ToolCallEffectUnknown</c> sin resolver de los Runs TERMINALES de la sesión
    /// (p. ej. cancelados tras un crash), con el mismo reconciliador y resultados que en un Run
    /// activo, sin tocar el estado del Run. Sin esto quedarían pendientes para siempre (ADR-0004 §5).
    /// Idempotente: una ToolCall ya resuelta no se vuelve a tocar.
    /// </summary>
    public int ReconcileTerminalRuns(SessionId sessionId)
    {
        var tail = _store.ReadFrom(sessionId, 1);
        var starts = new List<int>();
        for (var i = 0; i < tail.Count; i++)
        {
            if (_codecs.Decode(tail[i]) is RunCreated)
            {
                starts.Add(i);
            }
        }

        var stream = new EventStream(_store, _codecs, sessionId);
        var count = 0;
        for (var k = 0; k < starts.Count; k++)
        {
            var from = starts[k];
            var to = k + 1 < starts.Count ? starts[k + 1] : tail.Count;
            var runId = ((RunCreated)_codecs.Decode(tail[from])).RunId;
            var slice = OwnEvents(tail, new[] { from, to });
            if (!RunProjection.Replay(sessionId, runId, _codecs, slice).IsTerminal())
            {
                continue;
            }

            var started = new Dictionary<ToolCallId, ToolCallStarted>();
            var unknown = new List<ToolCallId>();
            var resolved = new HashSet<ToolCallId>();
            foreach (var evt in slice)
            {
                switch (_codecs.Decode(evt))
                {
                    case ToolCallStarted st: started[st.ToolCallId] = st; break;
                    case ToolCallEffectUnknown u: unknown.Add(u.ToolCallId); break;
                    case ToolCallReconciled r: resolved.Add(r.ToolCallId); break;
                    case ToolCallSucceeded su: resolved.Add(su.ToolCallId); break;
                    case ToolCallFailed f: resolved.Add(f.ToolCallId); break;
                }
            }

            foreach (var id in unknown.Where(id => !resolved.Contains(id)))
            {
                var json = started.TryGetValue(id, out var st) ? st.ReconciliationJson : null;
                stream.Append(ReconcileCall(id, json));
                count += 1;
            }
        }

        return count;
    }

    private ToolCallReconciled ReconcileCall(ToolCallId id, string? reconciliationJson)
    {
        FilesystemReconciliation result = _fsReconciler is not null && reconciliationJson is not null
            ? _fsReconciler!.Reconcile(_workspaceRoot, reconciliationJson!, CancellationToken.None)
            : FilesystemReconciliation.Unresolvable(
                "metadatos de reconciliación ausentes o sin reconciliador: falla cerrado, sin re-ejecutar");
        return new ToolCallReconciled(id, result.Outcome, result.Detail);
    }

    private int ResumeRun(SessionId sessionId, RunId runId)
    {
        var tail = _store.ReadFrom(sessionId, 1);
        var range = RunEventRange(tail, _codecs, runId);
        if (range is null)
        {
            return 0; // el run no está en la sesión: nada que reconciliar
        }

        // ADR-0004 §5.5: un Run solo es reanudable si su estado proyectado no es terminal.
        var runProjection = RunProjection.Replay(sessionId, runId, _codecs, OwnEvents(tail, range!));
        if (runProjection.IsTerminal())
        {
            return 0;
        }

        var from = range![0];
        var to = range![1];
        var startedNoOutcome = new Dictionary<ToolCallId, ToolCallStarted>();
        // Solo Succeeded/Failed/Reconciled son terminales (ADR-0004 §2). EffectUnknown es un estado
        // INTERMEDIO: si el crash fue DESPUÉS de emitirlo y ANTES de Reconciled, la ToolCall sigue
        // sin outcome y el siguiente resume debe continuar la reconciliación sin duplicarlo.
        var terminal = new HashSet<ToolCallId>();
        var alreadyUnknown = new HashSet<ToolCallId>();
        for (var i = from; i < to; i++)
        {
            var payload = _codecs.Decode(tail[i]);
            if (payload is ToolCallStarted started)
            {
                startedNoOutcome[started.ToolCallId] = started;
            }
            else if (payload is ToolCallEffectUnknown unknown)
            {
                alreadyUnknown.Add(unknown.ToolCallId);
            }
            else if (payload is ToolCallSucceeded || payload is ToolCallFailed
                || payload is ToolCallReconciled)
            {
                var id = ToolCallIdOf(payload);
                if (id is not null)
                {
                    terminal.Add(id!);
                }
            }
        }

        var stream = new EventStream(_store, _codecs, sessionId);
        var reconciled = 0;
        foreach (var entry in startedNoOutcome)
        {
            var id = entry.Key;
            var started = entry.Value;
            if (terminal.Contains(id))
            {
                continue; // idempotencia: ya resuelta por su outcome (o un Reconciled previo)
            }

            if (started.EffectClass == EffectClass.None && !alreadyUnknown.Contains(id))
            {
                // ADR-0004 §3: una tool sin efectos (EffectClass.None) se reejecuta siempre: no hay
                // nada que reconciliar ni que bloquee el Run. Se cierra como fallida-sin-efecto
                // (Started → Failed, ADR-0036) para que el modelo pueda reintentarla.
                stream.Append(new ToolCallFailed(id,
                    "interrupted by crash before completion; no side effects, safe to retry", EffectOutcome.None));
                reconciled += 1;
                continue;
            }

            if (!alreadyUnknown.Contains(id))
            {
                // Solo el primer resume emite EffectUnknown; si ya está en el journal (crash
                // posterior a EffectUnknown), se continúa sin duplicarlo.
                stream.Append(new ToolCallEffectUnknown(id, started.EffectClass));
            }

            FilesystemReconciliation result;
            if (_fsReconciler is not null && started.ReconciliationJson is not null)
            {
                result = _fsReconciler!.Reconcile(_workspaceRoot, started.ReconciliationJson!,
                    CancellationToken.None);
            }
            else
            {
                // Sin metadatos/reconciliador: falla cerrado. Nunca se re-ejecuta ni se clasifica.
                result = FilesystemReconciliation.Unresolvable(
                    "metadatos de reconciliación ausentes o sin reconciliador: falla cerrado, sin re-ejecutar");
            }

            stream.Append(new ToolCallReconciled(id, result.Outcome, result.Detail));
            reconciled += 1;
        }

        return reconciled;
    }

    /// <summary>Eventos del Run pedido (slice del rango [from, to)) para proyectar su estado.</summary>
    private static IReadOnlyList<DomainEvent> OwnEvents(IReadOnlyList<DomainEvent> tail, int[] range)
    {
        var events = new List<DomainEvent>();
        for (var i = range[0]; i < range[1]; i++)
        {
            events.Add(tail[i]);
        }

        return events;
    }

    /// <summary>Extrae el ToolCallId de un evento de outcome (estos campos son no-nulos).</summary>
    private static ToolCallId? ToolCallIdOf(DomainEventPayload payload)
    {
        if (payload is ToolCallSucceeded s) return s.ToolCallId;
        if (payload is ToolCallFailed f) return f.ToolCallId;
        if (payload is ToolCallReconciled r) return r.ToolCallId;
        if (payload is ToolCallEffectUnknown u) return u.ToolCallId;
        return null;
    }

    /// <summary>
    /// Rango de secuencias [from, to) de los eventos atribuibles al run pedido: desde su RunCreated
    /// hasta el RunCreated del siguiente run (o el final de la sesión). Un solo run activo por
    /// sesión (ADR-0035). Devuelve null si el run no aparece en la sesión.
    /// </summary>
    private static int[]? RunEventRange(IReadOnlyList<DomainEvent> tail, IEventCodecRegistry codecs,
        RunId runId)
    {
        var from = -1;
        var to = tail.Count;
        for (var i = 0; i < tail.Count; i++)
        {
            var payload = codecs.Decode(tail[i]);
            if (payload is not RunCreated run)
            {
                continue;
            }

            if (run.RunId == runId)
            {
                from = i;
            }
            else if (from >= 0)
            {
                to = i;
                break;
            }
        }

        if (from < 0)
        {
            return null;
        }

        return new int[] { from, to };
    }
}