namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

/// <summary>
/// OmniServer: sesión de protocolo in-process que expone el runtime detrás de IOmniClient
/// (ADR-0019 §1). Traduce los comandos wire a llamadas del Engine y acumula los eventos wire.
/// En M1 atiende <c>sim</c> y la query <c>state</c>.
/// </summary>
public sealed class OmniServer : IOmniClient
{
    private readonly IEventStore _store;

    private readonly IEventCodecRegistry _codecs;

    private readonly IAuditSink _audit;

    private readonly SimulationEngine _engine;

    private readonly List<WireEnvelope> _events = new();

    private SessionId? _lastSessionId;

    private RunId? _lastRunId;

    private ContextSnapshot? _lastSnapshot;

    private string _lastWorkingStateText = "";

    private readonly string? _stateFile;

    public OmniServer(IEventStore store, IEventCodecRegistry codecs, IAuditSink audit)
    {
        _store = store;
        _codecs = codecs;
        _audit = audit;
        _engine = BuildEngine(store, codecs, audit);
        _stateFile = null;
        LoadLastSession();
        // Recuperación del Run real (ADR-0004 §5): idempotente, no-op sin run persistido.
        RecoverPendingEffects();
    }

    public OmniServer(IEventStore store, IEventCodecRegistry codecs, IAuditSink audit, string stateFile)
    {
        _store = store;
        _codecs = codecs;
        _audit = audit;
        _engine = BuildEngine(store, codecs, audit);
        _stateFile = stateFile;
        LoadLastSession();
        // Recuperación del Run real (ADR-0004 §5): idempotente, no-op sin run persistido.
        RecoverPendingEffects();
    }

    /// <summary>Composición común: tools Core + defaults por modo (modo Act del sim).</summary>
    internal static SimulationEngine BuildEngine(IEventStore store, IEventCodecRegistry codecs, IAuditSink audit) =>
        new SimulationEngine(store, codecs, audit, ScriptedToolExecutor.WithCoreTools(
            HostTools.Default().Catalog(),
            ScriptedPermissionPolicy.WithTool("fake.write", PermissionDecision.Allow)
                .WithModeDefaults(RunMode.Act)));

    private void LoadLastSession()
    {
        if (_stateFile is null || !File.Exists(_stateFile!))
        {
            return;
        }

        var text = File.ReadAllText(_stateFile!);
        var sep = text.IndexOf('\n');
        if (sep <= 0)
        {
            return;
        }

        _lastSessionId = SessionId.Parse(text.Substring(0, sep));
        _lastRunId = RunId.Parse(text.Substring(sep + 1));

        // Reconstruir el snapshot desde el journal (P0-3): una nueva invocación recupera el
        // contexto que el run de otra invocación materializó, sin re-ejecutar el sim.
        _lastSnapshot = MaterializeFromJournal(_lastSessionId!, _lastRunId!);
    }

    /// <summary>
    /// Reconstruye el ContextSnapshot del último run desde el store persistente (replay del
    /// journal vía las proyecciones, igual que MaterializeSnapshot pero sin RunResult vivo).
    /// Devuelve null si el journal no tiene eventos del run.
    /// </summary>
    private ContextSnapshot? MaterializeFromJournal(SessionId sessionId, RunId runId)
    {
        try
        {
            var tail = EventsForRun(_store.ReadFrom(sessionId, 1), runId);
            if (tail.Count == 0)
            {
                return null;
            }

            var runProj = OmniCore.Engine.RunProjection.Replay(sessionId, runId, _codecs, tail);
            var planProj = OmniCore.Engine.PlanProjection.Replay(_codecs, tail);
            var workingState = OmniCore.Engine.WorkingStateProjector.Project(runProj, planProj);
            if (workingState is null)
            {
                return null;
            }

            _lastWorkingStateText = OmniCore.Engine.WorkingStateProjector.Render(workingState!);
            return MaterializeSnapshot(
                new SimulationEngine.RunResult(sessionId, runId, 0, new string[0], runProj, planProj,
                    OmniCore.Engine.TaskGraphProjection.Replay(_codecs, tail),
                    OmniCore.Engine.LaneProjection.Replay(_codecs, tail), workingState));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void SaveLastSession()
    {
        if (_stateFile is null || _lastSessionId is null || _lastRunId is null)
        {
            return;
        }

        File.WriteAllText(_stateFile!, _lastSessionId!.ToString() + "\n" + _lastRunId!.ToString());
    }

    /// <summary>
    /// Recuperación del Run real al arrancar el Host (ADR-0004 §5, ADR-0041 §2): si hay un run
    /// persistido reanudable, detecta sus ToolCalls <c>Started</c>-sin-outcome (crash) y las
    /// reconcilia con el <c>FilesystemReconciler</c> REAL contra la raíz del workspace que el run
    /// usó (reconstruida del <c>SessionCreated</c> del journal; fallback al cwd del proceso).
    /// NO pasa por el executor del sim: usa un <c>RunResumeService</c> dedicado, idempotente, que
    /// rechaza Runs terminales y nunca re-ejecuta la tool ni re-infere la respuesta. Es un no-op
    /// (<c>0</c>) sin run persistido. Falla cerrado: si la recuperación no corre, no bloquea
    /// el arranque ni amplía permisos; el run del journal queda con su estado incompleto intacto.
    /// Devuelve cuántas ToolCalls se reconciliaron.
    /// </summary>
    private int RecoverPendingEffects()
    {
        if (_lastSessionId is null || _lastRunId is null)
        {
            return 0;
        }

        var workspaceRoot = WorkspaceRootFor(_lastSessionId!);
        if (workspaceRoot is null || workspaceRoot!.Length == 0)
        {
            // Sin raíz segura el reconciliador falla cerrado de todos modos; no hay nada que inferir.
            workspaceRoot = Path.GetFullPath(".");
        }

        var service = new RunResumeService(_store, _codecs,
            new FilesystemReconciler(new PathBoundaryValidator()), workspaceRoot!);
        try
        {
            return service.Resume(_lastSessionId!, _lastRunId!);
        }
        catch (Exception)
        {
            // Falla cerrado: jamás se re-ejecuta una tool ni se amplía un permiso por un fallo
            // de recuperación; el run queda como estaba y se reintenta en el próximo arranque.
            return 0;
        }
    }

    /// <summary>Raíz del workspace del run reanudable, reconstruida del <c>SessionCreated</c> del journal.</summary>
    private string? WorkspaceRootFor(SessionId sessionId)
    {
        try
        {
            foreach (var evt in _store.ReadFrom(sessionId, 1))
            {
                if (!evt.Type.ToString().Equals("session.created", StringComparison.Ordinal))
                {
                    continue;
                }

                var created = _codecs.CodecFor(evt.Type).Decode(evt.Type, evt.PayloadJson) as SessionCreated;
                if (created is not null && created!.WorkspaceDisplayPath is not null
                    && created!.WorkspaceDisplayPath.Length > 0)
                {
                    return created!.WorkspaceDisplayPath;
                }

                return null;
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private IReadOnlyList<DomainEvent> EventsForRun(IReadOnlyList<DomainEvent> all, RunId runId)
    {
        var events = new List<DomainEvent>();
        var inRun = false;
        foreach (var evt in all)
        {
            if (evt.Type.ToString() == "run.created")
            {
                var created = _codecs.CodecFor(evt.Type).Decode(evt.Type, evt.PayloadJson) as RunCreated;
                inRun = created?.RunId == runId;
            }

            if (inRun) events.Add(evt);
        }

        return events;
    }

    public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken)
    {
        if (command.MessageType != MessageTypes.Command)
        {
            return CommandAck.Fail(command.MessageId, "esperaba un command, recibí " + command.MessageType);
        }

        var fields = JsonObj.Parse(command.PayloadJson);
        var queryName = fields.TryGetValue("query", out var q) ? q : null;
        if (queryName is not null)
        {
            return CommandAck.Ok(command.MessageId);
        }

        var commandName = fields.TryGetValue("cmd", out var c) ? c : null;
        if (commandName == "sim")
        {
            return RunSim(command, fields);
        }

        if (commandName == "sim.resume")
        {
            return ResumeSim(command);
        }

        if (commandName == "explore.start")
        {
            return StartExplorerRun(command, fields);
        }

        return CommandAck.Fail(command.MessageId, "comando desconocido en M1");
    }

    public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence) => _events.ToArray();

    public SessionId? LastSessionId() => _lastSessionId;

    public RunId? LastRunId() => _lastRunId;

    private CommandAck StartExplorerRun(WireEnvelope command, Dictionary<string, string> fields)
    {
        var objective = fields.TryGetValue("objective", out var value) ? value : null;
        if (string.IsNullOrWhiteSpace(objective))
        {
            return CommandAck.Fail(command.MessageId, "falta el objetivo del Explorer");
        }

        var sessionId = SessionId.New();
        var runId = RunId.New();
        var taskId = TaskId.New();
        var laneId = LaneId.New();
        var planId = PlanId.New();
        var workspacePath = Path.GetFullPath(".");
        var stream = new EventStream(_store, _codecs, sessionId);
        var now = DateTimeOffset.UtcNow;
        var budget = new TaskBudget(null, null, null, null);
        stream.Append(new SessionCreated(sessionId, WorkspaceId.Of(workspacePath).ToString(),
            workspacePath, ProfileId.New(), now));
        stream.Append(new RunCreated(runId, sessionId, objective!, RunMode.Act,
            ExecutionStrategy.Direct, FailurePolicy.BlockDependents, budget, taskId, now));
        stream.Append(new RunStarted(runId));
        stream.Append(new TaskCreated(taskId, runId, objective!, Array.Empty<TaskDependency>(), budget));
        stream.Append(new TaskReady(taskId));
        stream.Append(new LaneCreated(laneId, taskId, ProfileId.New()));
        stream.Append(new LaneStarted(laneId));
        stream.Append(new PlanCreated(planId, runId, PlanItemId.New(), objective!));

        _lastSessionId = sessionId;
        _lastRunId = runId;
        _lastSnapshot = MaterializeFromJournal(sessionId, runId);
        SaveLastSession();
        return CommandAck.Ok(command.MessageId);
    }

    /// <summary>Store del servidor (para el Turn de Explorer, que persiste en el mismo journal).</summary>
    public IEventStore AcquireStore() => _store;

    /// <summary>Registro de codecs del servidor.</summary>
    public IEventCodecRegistry AcquireCodecs() => _codecs;

    /// <summary>El LaneId del último run (del journal) o null si no hay run persistido.</summary>
    public LaneId? LastLaneId()
    {
        if (_lastSessionId is null || _lastRunId is null)
        {
            return null;
        }

        try
        {
            var tail = _store.ReadFrom(_lastSessionId!, 1);
            // P0-4: el lane del Run actual (no el primero de la sesión): captura desde el
            // RunCreated que coincide con _lastRunId.
            var inOwnRun = false;
            foreach (var evt in tail)
            {
                if (evt.Type.ToString().Equals("run.created", StringComparison.Ordinal))
                {
                    var payload = _codecs.CodecFor(evt.Type).Decode(evt.Type, evt.PayloadJson);
                    var runCreated = payload as RunCreated;
                    inOwnRun = runCreated is not null
                        && runCreated!.RunId.ToString().Equals(_lastRunId!.ToString(), StringComparison.Ordinal);
                    continue;
                }

                if (!inOwnRun || !evt.Type.ToString().Equals("lane.created", StringComparison.Ordinal))
                {
                    continue;
                }

                var lanePayload = _codecs.CodecFor(evt.Type).Decode(evt.Type, evt.PayloadJson);
                if (lanePayload is LaneCreated lane)
                {
                    return lane.LaneId;
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    public SessionQueryResult? Query(string name, CancellationToken cancellationToken)
    {
        if (name == "state")
        {
            var run = _lastSnapshot is null ? "none" : "M2:" + _lastSnapshot!.TokenCount.ToString();
            return new SessionQueryResult("state", "{\"runtime\":\"omnicore\",\"milestone\":\"M2\","
                + JsonObj.Field("runState", run) + "}");
        }

        if (name == "workingState")
        {
            var safe = _lastWorkingStateText is null || _lastWorkingStateText.Length == 0 ? "{}"
                : "{" + JsonObj.Field("workingState", RedactPii(_lastWorkingStateText)) + "}";
            return new SessionQueryResult("workingState", safe);
        }

        if (name == "context")
        {
            if (_lastSnapshot is null)
            {
                return new SessionQueryResult("context", "{\"snapshot\":null}");
            }

            var items = new List<string>();
            foreach (ContextItem item in _lastSnapshot!.Items)
            {
                items.Add("{\"id\":" + JsonObj.Field("id", item.Id)
                    + ",\"kind\":" + JsonObj.Field("kind", item.Kind.ToString())
                    + ",\"tokens\":" + JsonObj.Field("tokens", item.EstimatedTokens.ToString())
                    + ",\"content\":" + JsonObj.Field("content", Redact(item.Content, 80)) + "}");
            }

            return new SessionQueryResult("context",
                "{\"snapshot\":{" + JsonObj.Field("tokens", _lastSnapshot!.TokenCount.ToString())
                + ",\"fingerprint\":" + JsonObj.Field("fingerprint", _lastSnapshot!.Fingerprint.Hash())
                + ",\"workingState\":" + JsonObj.Field("workingState", RedactPii(_lastWorkingStateText))
                + ",\"items\":[" + string.Join(",", items.ToArray()) + "]}}");
        }

        return null;
    }

    /// <summary>
    /// Redacción de PII para diagnóstico (/context y /permissions son supervisors del runtime;
    /// nunca vuelcan contenido completo del workspace, solo cabeceras, ADR-0018).
    /// </summary>
    /// <summary>Redacta PII (keys/bearer/JWT) del WorkingState antes de exponerlo en queries.</summary>
    private static string RedactPii(string content) =>
        new OmniCore.Domain.PiiRedactor().Redact(content);

    private static string Redact(string content, int max)
    {
        // Redacción real: quita secretos (keys, bearer, JWT, cookies) y luego trunca.
        var redacted = new OmniCore.Domain.PiiRedactor().Redact(content);
        var safe = redacted is null ? "" : redacted!;
        if (safe.Length <= max)
        {
            return safe;
        }

        return safe.Substring(0, max) + "…(" + safe.Length + ")";
    }

    private CommandAck RunSim(WireEnvelope command, Dictionary<string, string> fields)
    {
        try
        {
            var scenarioName = fields.TryGetValue("scenario", out var s) ? s : null;
            var scenario = "with-tool-crash" == scenarioName
                ? Scenarios.WithToolCrash()
                : Scenarios.MultiItemPlan();
            var result = _engine.Execute(scenario, CancellationToken.None);
            _lastSessionId = result.SessionId;
            _lastRunId = result.RunId;
            SaveLastSession();
            AuditRun(result);
            var snapshot = MaterializeSnapshot(result);
            _lastSnapshot = snapshot;
            _lastWorkingStateText = result.WorkingState is null
                ? ""
                : OmniCore.Engine.WorkingStateProjector.Render(result.WorkingState!);
            _events.Add(WireEnvelope.Event(Ids.NewV7(), "{" + JsonObj.FieldRaw("type", "\"sim.events\"")
                + "," + JsonObj.Field("exitCode", result.ExitCode == 0 ? "0" : "1")
                + "," + JsonObj.Field("run", result.Run.State.ToString())
                + "," + JsonObj.Field("ctxTokens", snapshot is null ? "0" : snapshot.TokenCount.ToString()) + "}"));
            return result.ExitCode == 0 ? CommandAck.Ok(command.MessageId)
                : CommandAck.Fail(command.MessageId, "sim falló: " + string.Join("; ", result.Diagnostics));
        }
        catch (Exception ex)
        {
            var detail = ex is OmniCore.Infrastructure.EventParseException parse
                ? "Evento " + parse.EventType + ": " + (parse.Detail ?? "?")
                : (ex.Message ?? "exception");
            var stack = ex.StackTrace is null ? "" : string.Join("; ", ex.StackTrace);
            return CommandAck.FailWithCause(command.MessageId, detail, stack);
        }
    }

    /// <summary>
    /// Materializa un ContextSnapshot del run (ADR-0042 §2): WorkingState + conversación, con
    /// conteo de tokens. Es el post-proceso que el Context Engine hace en el Host (el Engine
    /// permanece desacoplado).
    /// </summary>
    private ContextSnapshot? MaterializeSnapshot(SimulationEngine.RunResult result)
    {
        if (result.WorkingState is null)
        {
            return null;
        }

        var counter = new FakeTokenCounter();
        var contributors = new IContextContributor[] {
            new WorkingStateContributor(OmniCore.Engine.WorkingStateProjector.Render(result.WorkingState)),
        };
        var materializer = new ContextMaterializer(counter, contributors);
        var fingerprint = new ExecutionFingerprint(
            "qwen38-27b-local", "harness-v1", "core-tools", "ctx-v1", "none", "M2");
        var request = new MaterializeRequest(result.SessionId, result.RunId, null, null, null,
            _store.CurrentSequence(result.SessionId), fingerprint);
        return materializer.Materialize(request, CancellationToken.None);
    }

    /// <summary>
    /// Alimenta el audit sink con los eventos de permisos del run (ADR-0043 §1, INV-012):
    /// el Engine no escribe auditoría directamente; un consumidor lee los eventos canónicos
    /// y registra los PermissionGranted/Denied como metadata redactada.
    /// </summary>
    private void AuditRun(SimulationEngine.RunResult result)
    {
        var events = _store.ReadFrom(result.SessionId, 1);
        foreach (var evt in events)
        {
            if (evt.Type.ToString() == "toolcall.permission_evaluated"
                || evt.Type.ToString() == "toolcall.permission_granted"
                || evt.Type.ToString() == "toolcall.permission_denied")
            {
                _audit.Record(new AuditRecord(
                    evt.Type.ToString(),
                    null,
                    result.SessionId,
                    result.RunId,
                    evt.Timestamp,
                    "session:" + result.SessionId + ":" + evt.Sequence,
                    new Dictionary<string, string>()), CancellationToken.None);
            }
        }
    }

    private CommandAck ResumeSim(WireEnvelope command)
    {
        if (_lastSessionId is null || _lastRunId is null)
        {
            return CommandAck.Fail(command.MessageId, "no hay un run previo para reanudar");
        }

        try
        {
            var stream = new EventStream(_store, _codecs, _lastSessionId!);
            var reconciled = _engine.Resume(_lastSessionId!, _lastRunId!, stream);
            _events.Add(WireEnvelope.Event(Ids.NewV7(), "{" + JsonObj.FieldRaw("type", "\"sim.resumed\"")
                + "," + JsonObj.Field("reconciled", reconciled == 0 ? "0" : "1") + "}"));
            return CommandAck.Ok(command.MessageId);
        }
        catch (Exception ex)
        {
            return CommandAck.FailWithCause(command.MessageId, ex.Message ?? "exception",
                ex.StackTrace is null ? "" : string.Join("; ", ex.StackTrace));
        }
    }
}

/// <summary>Escenarios de simulación incluidos para los tests deterministas y <c>omni sim</c>.</summary>
public sealed class Scenarios
{
    /// <summary>Plan de varios items con TaskGraph N:M, el caso central del criterio de M1 (ADR-0041).</summary>
    public static SimulationScenario MultiItemPlan()
    {
        var turns = new Dictionary<string, IReadOnlyList<SimulatedTurnAction>>();
        turns["root"] = [
            SimulatedTurnAction.ToolCall("fake.read", "read"),
            SimulatedTurnAction.DoneMarker(),
        ];
        return new SimulationScenario(
            "multi-item-plan",
            RunMode.Act,
            "Corregir el test de autenticación",
            new SimulatedPlanMutation[] {
                new SimulatedPlanMutation("P1", "Inspeccionar auth", new string[0]),
                new SimulatedPlanMutation("P2", "Aplicar fix", new string[] { "P1" }),
                new SimulatedPlanMutation("P3", "Validar", new string[] { "P2" }),
            },
            new SimulatedTask[] {
                new SimulatedTask("T1",
                    new SimulatedLink[] { new SimulatedLink("P1", "implements") },
                    new string[0], "Explorar flujo de auth"),
                new SimulatedTask("T2",
                    new SimulatedLink[] { new SimulatedLink("P2", "implements") },
                    new string[] { "T1" }, "Aplicar el fix"),
                new SimulatedTask("T3",
                    new SimulatedLink[] { new SimulatedLink("P3", "verifies") },
                    new string[] { "T2" }, "Validar el fix"),
            },
            turns,
            AuthPermissions(),
            "Completed",
            new Dictionary<string, string>());
    }

    /// <summary>Permisos del escenario por defecto: escritura Allow, resto Allow (perfil autónomo simulado).</summary>
    public static Dictionary<string, PermissionDecision> AuthPermissions()
    {
        var p = new Dictionary<string, PermissionDecision>();
        p["fake.read"] = PermissionDecision.Allow;
        p["fake.test"] = PermissionDecision.Allow;
        p["fake.write"] = PermissionDecision.Allow;
        return p;
    }

    /// <summary>Escenario con una tool call real en los turns (ejercita ToolRuntime completo).</summary>
    public static SimulationScenario WithTools()
    {
        var turns = new Dictionary<string, IReadOnlyList<SimulatedTurnAction>>();
        turns["root"] = [
            SimulatedTurnAction.ToolCall("fake.write", "applied"),
            SimulatedTurnAction.DoneMarker(),
        ];
        return new SimulationScenario(
            "with-tools",
            RunMode.Act,
            "Escribir un archivo",
            new SimulatedPlanMutation[] {
                new SimulatedPlanMutation("P1", "Escribir", new string[0]),
            },
            new SimulatedTask[] {
                new SimulatedTask("T1",
                    new SimulatedLink[] { new SimulatedLink("P1", "implements") },
                    new string[0], "Escribir objetivo"),
            },
            turns,
            AuthPermissions(),
            "Completed",
            new Dictionary<string, string>());
    }

    /// <summary>Escenario que ejerce plan.propose de verdad (P0-6): el modelo inicia el item.</summary>
    public static SimulationScenario WithPlanPropose()
    {
        var turns = new Dictionary<string, IReadOnlyList<SimulatedTurnAction>>();
        turns["root"] = [
            SimulatedTurnAction.ToolCall("plan.propose", "applied"),
            SimulatedTurnAction.DoneMarker(),
        ];
        return new SimulationScenario(
            "with-plan-propose",
            RunMode.Act,
            "Iniciar el plan",
            new SimulatedPlanMutation[] {
                new SimulatedPlanMutation("P1", "Inspeccionar", new string[0]),
                new SimulatedPlanMutation("P2", "Implementar", new string[] { "P1" }),
            },
            new SimulatedTask[] {
                new SimulatedTask("T1",
                    new SimulatedLink[] { new SimulatedLink("P1", "implements") },
                    new string[0], "Inspeccionar"),
            },
            turns,
            AuthPermissions(),
            "Completed",
            new Dictionary<string, string>());
    }

    /// <summary>Escenario con crash inyectado tras el Started de fake.write (ADR-0041 §2).</summary>
    public static SimulationScenario WithToolCrash()
    {
        var turns = new Dictionary<string, IReadOnlyList<SimulatedTurnAction>>();
        turns["root"] = [
            SimulatedTurnAction.ToolCall("fake.write", "applied"),
            SimulatedTurnAction.DoneMarker(),
        ];
        return new SimulationScenario(
            "with-tool-crash",
            RunMode.Act,
            "Escribir un archivo",
            new SimulatedPlanMutation[] {
                new SimulatedPlanMutation("P1", "Escribir", new string[0]),
            },
            new SimulatedTask[] {
                new SimulatedTask("T1",
                    new SimulatedLink[] { new SimulatedLink("P1", "implements") },
                    new string[0], "Escribir objetivo"),
            },
            turns,
            AuthPermissions(),
            "Running",
            new Dictionary<string, string>(),
            "fake.write");
    }

    /// <summary>WorkingState renderizado de ejemplo para `omni explain` sin LLM (M2).</summary>
    public static string SampleWorkingState()
    {
        var rootItem = OmniCore.Domain.PlanItemId.New();
        var items = new OmniCore.Domain.PlanItem[] {
            new OmniCore.Domain.PlanItem(OmniCore.Domain.PlanItemId.New(), "Explorar el repositorio",
                OmniCore.Domain.PlanItemState.InProgress, 1, null,
                new OmniCore.Domain.PlanItemId[0], new OmniCore.Domain.PlanItemLink[0], true, null,
                new Dictionary<string, string>()),
            new OmniCore.Domain.PlanItem(OmniCore.Domain.PlanItemId.New(), "Resumir estructura",
                OmniCore.Domain.PlanItemState.Ready, 2, null,
                new OmniCore.Domain.PlanItemId[0], new OmniCore.Domain.PlanItemLink[0], true, null,
                new Dictionary<string, string>()),
        };
        _ = rootItem;
        var plan = OmniCore.Engine.PlanProjection.FromItems(OmniCore.Domain.PlanId.New(),
            OmniCore.Domain.RunId.New(), items);
        var run = OmniCore.Engine.RunProjection.ForSample(
            OmniCore.Domain.SessionId.New(), OmniCore.Domain.RunId.New(), "Explícame este repositorio");
        return OmniCore.Engine.WorkingStateProjector.Render(
            OmniCore.Engine.WorkingStateProjector.Project(run, plan));
    }
}
