namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Ejecuta un escenario de simulación contra el runtime real (ADR-0041 §2): crea la sesión,
/// el Run, las Tasks/Lanes, y avanza los Turns con el ScriptedModelProvider + FakeTool, todo
/// persistido en el journal. Al final verifica la golden rule: reconstruir el estado desde el
/// journal coincide con el estado vivo.
/// </summary>
public sealed class SimulationEngine
{
    private readonly IEventStore _store;

    private readonly IEventCodecRegistry _codecs;


    private readonly IAuditSink _audit;

    private readonly PlanService _planService;

    private readonly ProgressReconciler _reconciler;

    private IToolExecutor? _toolExecutor;

    /// <summary>Reconcilador de efectos de filesystem inyectado (ADR-0004 §4); null = conservador.</summary>
    private readonly IFilesystemReconciler? _fsReconciler;

    private readonly string _workspaceRoot;

    private bool _crashed;

    private readonly Dictionary<string, PlanItemId> _symbolicItems = new();

    /// <summary>Respuesta al PlanApproval del escenario en curso (null = nadie responde).</summary>
    private string? _planApproval;

    /// <summary>Umbral del watchdog del escenario en curso (ADR-0036 §7).</summary>
    private int _stallThreshold = ProgressReconciler.DefaultStallThresholdTurns;

    public SimulationEngine(IEventStore store, IEventCodecRegistry codecs, IAuditSink audit)
    {
        _store = store;
        _codecs = codecs;
        _audit = audit;
        _planService = new PlanService();
        _reconciler = new ProgressReconciler();
        _toolExecutor = null;
        _fsReconciler = null;
        _workspaceRoot = "sim";
    }

    public SimulationEngine(IEventStore store, IEventCodecRegistry codecs, IAuditSink audit,
        IToolExecutor toolExecutor)
    {
        _store = store;
        _codecs = codecs;
        _audit = audit;
        _planService = new PlanService();
        _reconciler = new ProgressReconciler();
        _toolExecutor = toolExecutor;
        _fsReconciler = null;
        _workspaceRoot = "sim";
    }

    /// <summary>
    /// Con conciliador de filesystem y raíz real (ADR-0004 §4): el resume puede clasificar los
    /// efectos Started-sin-outcome contra rutas/hashes reales. <c>workspaceRoot</c> es el directorio
    /// sobre el que operaron las tools; <c>reconciler == null</c> conserva el camino conservador.
    /// </summary>
    public SimulationEngine(IEventStore store, IEventCodecRegistry codecs, IAuditSink audit,
        IToolExecutor toolExecutor, IFilesystemReconciler? reconciler, string workspaceRoot)
    {
        _store = store;
        _codecs = codecs;
        _audit = audit;
        _planService = new PlanService();
        _reconciler = new ProgressReconciler();
        _toolExecutor = toolExecutor;
        _fsReconciler = reconciler;
        _workspaceRoot = workspaceRoot;
    }

    /// <summary>Permite sustituir el executor (test/sim). El engine no conoce implementaciones.</summary>
    public void SetToolExecutor(IToolExecutor toolExecutor) => _toolExecutor = toolExecutor;

    /// <summary>Resultado de una simulación: exit code 0 si el estado final coincide con el esperado.</summary>
    public sealed class RunResult
    {
        public SessionId SessionId { get; }

        public RunId RunId { get; }

        public int ExitCode { get; }

        public IReadOnlyList<string> Diagnostics { get; }

        public RunProjection Run { get; }

        public PlanProjection Plan { get; }

        public TaskGraphProjection Tasks { get; }

        public LaneProjection Lanes { get; }

        public WorkingState? WorkingState { get; }

        public RunResult(SessionId sessionId, RunId runId, int exitCode, IReadOnlyList<string> diagnostics,
            RunProjection run, PlanProjection plan, TaskGraphProjection tasks, LaneProjection lanes,
            WorkingState? workingState)
        {
            SessionId = sessionId;
            RunId = runId;
            ExitCode = exitCode;
            Diagnostics = diagnostics;
            Run = run;
            Plan = plan;
            Tasks = tasks;
            Lanes = lanes;
            WorkingState = workingState;
        }
    }

    public RunResult Execute(SimulationScenario scenario, CancellationToken cancellationToken,
        SessionId? sessionIdentity = null)
    {
        // The Host keeps one engine instance for repeated `omni sim` commands. Fault-injection
        // and symbolic plan state belong to one scenario and must not bleed into later executions.
        _crashed = false;
        _symbolicItems.Clear();
        _planApproval = scenario.PlanApproval;
        _stallThreshold = scenario.StallThresholdTurns ?? ProgressReconciler.DefaultStallThresholdTurns;
        var sessionId = sessionIdentity ?? SessionId.New();
        var runId = RunId.New();
        var rootTaskId = TaskId.New();
        var rootLaneId = LaneId.New();
        var planId = PlanId.New();
        var session = new Session(sessionId, new WorkspaceRef(WorkspaceId.Parse("sim"), "sim"), ProfileId.New(),
            DateTimeOffset.Now);

        var stream = new EventStream(_store, _codecs, sessionId);
        stream.Append(new SessionCreated(sessionId, session.Workspace.Id.ToString(), session.Workspace.DisplayPath, session.Profile, session.CreatedAt));

        stream.Append(new RunCreated(runId, sessionId, scenario.Input, scenario.Mode, ExecutionStrategy.Direct,
            FailurePolicy.BlockDependents, new TaskBudget(null, null, null, null), rootTaskId, DateTimeOffset.Now));
        stream.Append(new RunStarted(runId));

        // Task raíz del Run (ADR-0035 §2): la declara RunCreated y existe como cualquier otra Task.
        stream.Append(new TaskCreated(rootTaskId, runId, scenario.Input, Array.Empty<TaskDependency>(),
            new TaskBudget(null, null, null, null)));

        var taskIds = new Dictionary<string, TaskId>();
        foreach (var task in scenario.Tasks)
        {
            var tid = TaskId.New();
            taskIds[task.Id] = tid;
            var deps = new List<TaskDependency>();
            foreach (var dep in task.DependsOn)
            {
                deps.Add(new TaskDependency(Entities.Task(taskIds, dep), true));
            }

            stream.Append(new TaskCreated(tid, runId, task.Objective, deps.ToArray(),
                new TaskBudget(null, null, null, null)));
        }

        // PlanCreated ya crea el item raíz: añadirlo otra vez sería un segundo evento para la misma
        // creación (INV-027) y lo duplicaría en la proyección.
        var rootItemId = PlanItemId.New();
        stream.Append(new PlanCreated(planId, runId, rootItemId, scenario.Input));

        // El "modelo" divide el objetivo en pasos: Plan rev.2 (ADR-0016 §6) con los pasos como
        // hijos del item raíz, que pasa a ser un contenedor derivado (ADR-0035 §3, ADR-0036 §4).
        if (scenario.Plan.Count > 0)
        {
            stream.Append(new PlanRevised(planId, runId, 2,
                "[{\"kind\":\"Split\",\"cause\":\"Model\",\"item\":\"" + rootItemId + "\"}]", MutationImpact.Minor));
        }

        var itemIds = new Dictionary<string, PlanItemId>();
        itemIds["P0"] = rootItemId;
        var order = 2;
        // El mapa simbólico es la misma tabla (los ids textuales del modelo → guids del run).
        foreach (var kv in itemIds)
        {
            _symbolicItems[kv.Key] = kv.Value;
        }

        foreach (var m in scenario.Plan)
        {
            var pid = PlanItemId.New();
            itemIds[m.Id] = pid;
            _symbolicItems[m.Id] = pid;
            var deps = new List<PlanItemId>();
            foreach (var d in m.DependsOn)
            {
                deps.Add(Entities.PlanItem(itemIds, d));
            }

            stream.Append(new PlanItemAdded(pid, planId, m.Text, order++, rootItemId, deps.ToArray(), true,
                new Dictionary<string, string>()));
        }

        foreach (var task in scenario.Tasks)
        {
            var tid = Entities.Task(taskIds, task.Id);
            foreach (var link in task.Links)
            {
                var pid = Entities.PlanItem(itemIds, link.Item);
                var role = ParseRole(link.Role);
                stream.Append(new PlanItemLinked(pid, new PlanItemLink(tid, role != LinkRole.Supports, role)));
            }
        }

        // Task raíz + lane raíz: los Turns del agente principal viven aquí (ADR-0035 §2).
        stream.Append(new TaskReady(rootTaskId));
        stream.Append(new LaneCreated(rootLaneId, rootTaskId, ProfileId.New()));
        stream.Append(new LaneStarted(rootLaneId));
        stream.Append(new TaskStarted(rootTaskId, rootLaneId));

        // Arrancar cada Task del escenario con su lane (Running). La simulación asume que
        // ejecutar la tool = completar la task, y el reconciler corrige el plan (R1/R2).
        foreach (var task in scenario.Tasks)
        {
            var tid = Entities.Task(taskIds, task.Id);
            var laneId = LaneId.New();
            stream.Append(new TaskReady(tid));
            stream.Append(new LaneCreated(laneId, tid, ProfileId.New()));
            stream.Append(new LaneStarted(laneId));
            stream.Append(new TaskStarted(tid, laneId));
        }

        // Fingerprint determinista de los Turnos del escenario (ADR-0017, M1 con componentes
        // simulados): todos los TurnStarted del run la registran, igual que los turnos reales.
        var fingerprint = SimFingerprint(scenario);
        ExecuteTurns(scenario, stream, rootLaneId, cancellationToken, fingerprint);

        // Watchdog de progreso (ADR-0016 §9, ADR-0036 §7): si el item actual lleva N Turns en
        // InProgress sin señal, emitir ProgressStalled. En M1 el umbral es constante.
        EmitStallIfNeeded(stream, sessionId, runId);

        // Tras un crash inyectado, el Run queda interrumpido (sin pasar los gates): se reanuda
        // con Resume() (ADR-0041 §2). El run vivo queda en el estado previo.
        if (!_crashed)
        {
            ContinueToCompletion(sessionId, runId, stream, scenario.Mode);
        }

        var finalTail = stream.EventsSince(1);
        var finalRun = RunProjection.Replay(sessionId, runId, _codecs, finalTail);
        var planProj = PlanProjection.Replay(_codecs, finalTail);
        var tasksProj = TaskGraphProjection.Replay(_codecs, finalTail);
        var lanesProj = LaneProjection.Replay(_codecs, finalTail);

        var diagnostics = new List<string>();
        if (_crashed)
        {
            diagnostics.Add("crash inyectado; el Run queda interrumpido (usa --resume)");
        }

        var exit = VerifyExpectations(scenario, finalRun, planProj, diagnostics);

        // Golden rule (ADR-0041 §2): el journal reconstruye exactamente el estado vivo. Este stream
        // es el único escritor de la sesión durante la simulación.
        var golden = GoldenRule.Check(_codecs, finalTail, stream);
        if (golden.Count > 0)
        {
            diagnostics.AddRange(golden);
            exit = exit == 0 ? 3 : exit;
        }

        var workingState = _crashed ? null : WorkingStateProjector.Project(finalRun, planProj);
        return new RunResult(sessionId, runId, exit, diagnostics.ToArray(), finalRun, planProj, tasksProj,
            lanesProj, workingState);
    }

    /// <summary>
    /// Watchdog de progreso (ADR-0016 §9, ADR-0036 §7): si el item actual (R7) lleva el umbral de
    /// Turns en InProgress sin señal de progreso —contando solo los Turns de sus Lanes vinculadas o,
    /// sin vínculos, de la Lane raíz— emite ProgressStalled.
    /// </summary>
    private void EmitStallIfNeeded(EventStream stream, SessionId sessionId, RunId runId)
    {
        var tail = stream.EventsSince(1);
        var plan = PlanProjection.Replay(_codecs, tail);
        var current = _reconciler.CurrentItem(plan);
        var item = current is null ? null : plan.Item(current);
        if (item is null || item.State != PlanItemState.InProgress)
        {
            return;
        }

        var lanes = LaneProjection.Replay(_codecs, tail);
        var relevant = item.LinkedTasks.SelectMany(link => lanes.ForTask(link.TaskId)).Select(lane => lane.Id).ToHashSet();
        if (relevant.Count == 0 && RunProjection.Replay(sessionId, runId, _codecs, tail).RootTask is { } root)
        {
            relevant = lanes.ForTask(root).Select(lane => lane.Id).ToHashSet();
        }

        var turns = ProgressWatchdog.TurnsWithoutProgress(_codecs, tail, relevant);
        if (new ProgressWatchdog(_stallThreshold).IsStalled(turns))
        {
            stream.Append(new ProgressStalled(item.Id, turns, DateTimeOffset.Now));
        }
    }

    private void ContinueToCompletion(SessionId sessionId, RunId runId, EventStream stream, RunMode mode)
    {
        if (mode == RunMode.Plan && !EmitPlanApproval(sessionId, runId, stream, _planApproval))
        {
            return; // el Run queda esperando al usuario o cerrado como Planned
        }

        var tail = stream.EventsSince(1);
        var runProj = RunProjection.Replay(sessionId, runId, _codecs, tail);
        var tasksProj = TaskGraphProjection.Replay(_codecs, tail);
        var planProj = PlanProjection.Replay(_codecs, tail);
        new RunCoupon(runProj, tasksProj, planProj).CheckCompletionAndGate(_planService, _reconciler, _store,
            _codecs, sessionId, stream);
    }

    /// <summary>
    /// PLAN → ACT (ADR-0035 §4): el Run pide PlanApproval con opciones decididas por el servidor
    /// y la respuesta del usuario (en la sim, la del escenario) la resuelve:
    /// <list type="bullet">
    /// <item><c>approve_execute</c>: InteractionResolved + RunModeChanged(Plan → Act); sigue el Run.</item>
    /// <item><c>approve_only</c>: InteractionResolved; el Run termina como <c>Planned</c>.</item>
    /// <item><c>reject</c> o sin respuesta: el Run queda esperando al usuario en su Lane raíz.</item>
    /// </list>
    /// Devuelve true si el Run debe continuar hacia sus gates.
    /// </summary>
    private bool EmitPlanApproval(SessionId sessionId, RunId runId, EventStream stream, string? answer)
    {
        var interaction = InteractionId.New();
        var tail = stream.EventsSince(1);
        var run = RunProjection.Replay(sessionId, runId, _codecs, tail);
        var rootLane = run.RootTask is null ? null
            : LaneProjection.Replay(_codecs, tail).ForTask(run.RootTask).FirstOrDefault()?.Id;
        stream.Append(new InteractionRequested(
            interaction,
            InteractionKind.PlanApproval,
            "{\"operation\":\"plan.approval\"}",
            "[{\"id\":\"approve_execute\",\"intent\":\"allow\"},{\"id\":\"approve_only\",\"intent\":\"allow\"},"
                + "{\"id\":\"reject\",\"intent\":\"deny\"}]",
            "reject",
            null,
            rootLane,
            run.RootTask,
            null,
            0,
            1));

        // Mismo efecto que RespondToInteraction (RunControlService). Sin respuesta no hay cliente
        // interactivo: ADR-0003 → Deny y el Run termina Planned (ADR-0035 §4.5).
        var control = new RunControlService(_store, _codecs);
        var option = answer ?? "approve_only";
        var resolved = new InteractionResolved(interaction, answer ?? "reject",
            answer is null ? InteractionCause.NoClient : InteractionCause.User);
        var effect = control.PlanApprovalEffect(stream.EventsSince(1), sessionId, runId, option);
        var batch = new List<DomainEventPayload> { resolved };
        batch.AddRange(effect);
        stream.AppendBatch(batch, DurabilityClass.Standard);
        return option == "approve_execute";
    }

    /// <summary>
    /// Reanuda un run tras un crash (ADR-0004 §5, ADR-0041 §2). La lógica vive en
    /// <c>RunResumeService</c> (idempotente, scoped por Run, rechaza Runs terminales) para que el
    /// sim y el Host compartan la misma implementación sin duplicarla. Este overload usa la raíz
    /// de workspace configurada en el engine.
    /// </summary>
    public int Resume(SessionId sessionId, RunId runId, EventStream stream)
    {
        return Resume(sessionId, runId, stream, _workspaceRoot);
    }

    /// <summary>
    /// Variante con raíz de workspace explícita: delega en <c>RunResumeService</c>, que relee el
    /// hash real del archivo con el reconciliador inyectado _fsReconciler para clasificar
    /// Applied/NotApplied/Conflict contra pre/post. Sin reconciliador o sin metadatos →
    /// Unresolvable (falla cerrado, nunca Applied). El parámetro <c>stream</c> se conserva por
    /// compatibilidad de API; el servicio escribe su propio EventStream sobre el mismo store.
    /// </summary>
    public int Resume(SessionId sessionId, RunId runId, EventStream stream, string workspaceRoot)
    {
        return new RunResumeService(_store, _codecs, _fsReconciler, workspaceRoot).Resume(sessionId, runId);
    }

    private void ExecuteTurns(SimulationScenario scenario, EventStream stream, LaneId laneId,
        CancellationToken cancellationToken, ExecutionFingerprint fingerprint)
    {
        var laneActions = scenario.Turns.TryGetValue("root", out var rootActions) ? rootActions : null;
        if (laneActions is null)
        {
            foreach (var kv in scenario.Turns)
            {
                if (kv.Key == "T1")
                {
                    laneActions = kv.Value;
                    break;
                }
            }
        }

        if (laneActions is null)
        {
            return;
        }

        foreach (var action in laneActions)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (action.Tool is not null)
            {
                RunToolCall(action, stream, laneId, cancellationToken, scenario.FaultAtTool, fingerprint);
            }
            else if (action.IsComplete)
            {
                var turnId = TurnId.New();
                stream.Append(new TurnStarted(turnId, laneId, fingerprint));
                stream.Append(new TurnCompleted(turnId));
            }
        }
    }

    private void RunToolCall(SimulatedTurnAction action, EventStream stream, LaneId laneId,
        CancellationToken cancellationToken, string? faultAtTool, ExecutionFingerprint fingerprint)
    {
        var toolName = action.Tool != null ? action.Tool! : throw new InvalidOperationException("tool null");
        var callId = ToolCallId.New();
        if (_toolExecutor is not null)
        {
            // Pipeline real (INV-001): el modelo emite request; el runtime decide y ejecuta.
            // plan.propose en el sim recibe una mutación concreta (start del primer item
            // simbólico) para que la tool valide y declare el efecto de verdad (P0-6).
            var callArgs = "{}";
            if (toolName == "plan.propose")
            {
                callArgs = SimPlanArgs();
            }

            var validated = new ValidatedToolCall(callId, new ToolId(toolName), "pc-" + toolName, callArgs);
            // ToolCallRequested/Prepared los emite el ToolRuntime al inicio del pipeline (P0-1):
            // el ciclo durable Requested → Prepared → PermissionEvaluated → … es invariable.
            if (faultAtTool == toolName)
            {
                // Crash inyectado tras el Started: el proceso "muere" sin persistir el outcome.
                // Se emite empezado con efecto para que el resume vea una ToolCall Started sin
                // outcome (ADR-0004 §2) y la reconcilie sin duplicar.
                var crashIntent = new ToolIntent(callId, validated.ToolId, "{}", EffectClass.Reconcilable,
                    ResourceClaims.Empty(), ToolRisk.Low, null);
                AppendAuthorizedChain(stream, callId, toolName, callArgs);
                stream.Append(new ToolCallStarted(callId, crashIntent.Effect, null), DurabilityClass.Barrier);
                _crashed = true;
                var crashTurn = TurnId.New();
                stream.Append(new TurnStarted(crashTurn, laneId, fingerprint));
                stream.Append(new TurnAbandoned(crashTurn, "crash inyectado (simulación)"));
                return;
            }

            // Con stream: un Started con efecto se confirma con Barrier antes de ejecutar (INV-014), y
            // los outcomes se escriben en un solo lote atómico.
            // Un Ask solo se aprueba si el escenario lo responde; si no, se deniega (ADR-0003).
            var approves = string.Equals(action.Answer, "approve", StringComparison.OrdinalIgnoreCase);
            var outcome = _toolExecutor.ExecuteTool(validated, approves, cancellationToken, stream);
            stream.AppendBatch(outcome.Events, DurabilityClass.Standard);

            // plan.propose: si la tool declaró una mutación válida (JSON de la mutación en Preview,
            // no el summary legible), PlanService la aplica contra las proyecciones del run
            // (ADR-0016 §3; INV-017: decide el runtime). Fix P0: Preview, no Summary.
            if (toolName == "plan.propose" && outcome.Succeeded && outcome.FinalState == ToolCallState.Succeeded
                && outcome.Effect == EffectOutcome.Applied && outcome.Preview is not null)
            {
                ApplyPlanProposal(stream, outcome.Preview!);
            }

            var doneTurn = TurnId.New();
            stream.Append(new TurnStarted(doneTurn, laneId, fingerprint));
            stream.Append(new TurnCompleted(doneTurn));
            return;
        }

        // Fallback sin pipeline (tests que no inyectan executor): la misma cadena canónica que
        // produce el pipeline real, para que el journal sea válido (ADR-0004 §2, ADR-0036 §5).
        var effect = ParseEffect(action.Effect);
        AppendAuthorizedChain(stream, callId, toolName, "{}");
        stream.Append(new ToolCallStarted(callId, effect, null),
            effect == EffectClass.None ? DurabilityClass.Standard : DurabilityClass.Barrier);
        stream.Append(new ToolCallSucceeded(callId, "{\"summary\":\"ok\"}"));
        var fallbackTurn = TurnId.New();
        stream.Append(new TurnStarted(fallbackTurn, laneId, fingerprint));
        stream.Append(new TurnCompleted(fallbackTurn));
    }

    /// <summary>ModelKey fija del "modelo" simulado: determinista, nunca un modelo real.</summary>
    private const string SimModelKey = "sim.scripted";

    private const string SimContextPolicy = "sim-context-v1";

    private const string SimOverrides = "none";

    private const string SimBuild = "M1-sim";

    /// <summary>
    /// Fingerprint determinista de los Turnos simulados (ADR-0017, EPIC-009): escenario + model
    /// key falso fijo + hash del toolkit del sim. El Engine no puede ver el catálogo concreto
    /// (INV-008), así que el hash del toolkit cubre la superficie de tools que el escenario
    /// ejerce (las reglas de permisos y las tools invocadas), que es lo que el turno recibió.
    /// Determinista por construcción → el replay del journal reconstruye el mismo fingerprint y
    /// la golden rule sigue verificando el estado vivo contra el reconstruido.
    /// </summary>
    internal static ExecutionFingerprint SimFingerprint(SimulationScenario scenario)
    {
        return new ExecutionFingerprint(
            SimModelKey,
            HashOf("sim-scenario:" + scenario.Name),
            HashOf(ToolkitSurface(scenario)),
            SimContextPolicy,
            SimOverrides,
            SimBuild);
    }

    /// <summary>Superficie canónica de tools del escenario, ordenada y con longitudes para que
    /// sea inambigua ("a","bc" vs "ab","c").</summary>
    private static string ToolkitSurface(SimulationScenario scenario)
    {
        var tools = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var tool in scenario.Permissions.Keys)
        {
            tools.Add(tool);
        }

        foreach (var actions in scenario.Turns.Values)
        {
            foreach (var act in actions)
            {
                if (act.Tool is not null)
                {
                    tools.Add(act.Tool!);
                }
            }
        }

        var canonical = new System.Text.StringBuilder();
        foreach (var tool in tools)
        {
            canonical.Append(tool.Length).Append(':').Append(tool).Append(';');
        }

        return canonical.ToString();
    }

    private static string HashOf(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value)));

    /// <summary>Requested → Prepared → PermissionEvaluated(Allow) → Authorized, previo al Started.</summary>
    private static void AppendAuthorizedChain(EventStream stream, ToolCallId callId, string toolName, string args)
    {
        stream.AppendBatch(new DomainEventPayload[] {
            new ToolCallRequested(callId, "pc-" + toolName, toolName, args),
            new ToolCallPrepared(callId, args),
            new PermissionEvaluated(callId, PermissionDecision.Allow, "[]", null),
            new ToolCallAuthorized(callId),
        }, DurabilityClass.Standard);
    }

    private static EffectClass ParseEffect(string? effect) =>
        effect == "applied" ? EffectClass.Reconcilable : EffectClass.None;

    /// <summary>Args JSON para plan.propose en el sim: start del primer item simbólico (P0).</summary>
    private string SimPlanArgs()
    {
        var first = FirstSymbolicItem();
        return "{\"kind\":\"start\",\"itemId\":\"" + first + "\"}";
    }

    /// <summary>Primer paso real del plan (el raíz, P0, es un contenedor si el escenario lo dividió).</summary>
    private string FirstSymbolicItem()
    {
        foreach (var kv in _symbolicItems)
        {
            if (kv.Key != "P0")
            {
                return kv.Key;
            }
        }

        return "P0";
    }

    /// <summary>
    /// Aplica la mutación declarada por `plan.propose` vía PlanService contra las proyecciones
    /// del run (ADR-0016 §3). Los ids simbólicos (p. ej. "P1") se resuelven a los guids del
    /// run; si la mutación no aplica el estado, PlanService la rechaza (sin eventos).
    /// </summary>
    private void ApplyPlanProposal(EventStream stream, string mutationJson)
    {
        var plan = Mutations.ResolveDeclared(mutationJson, _symbolicItems);
        if (plan is null)
        {
            return;
        }

        var tail = stream.EventsSince(1);
        var planProj = PlanProjection.Replay(_codecs, tail);
        var tasksProj = TaskGraphProjection.Replay(_codecs, tail);
        var lanesProj = LaneProjection.Replay(_codecs, tail);
        var planService = new PlanService();
        var result = planService.Apply(planProj, tasksProj, lanesProj, plan!);
        if (!result.Accepted)
        {
            return;
        }

        foreach (var evt in result.Events)
        {
            stream.Append(evt);
        }
    }

    private int VerifyExpectations(SimulationScenario scenario, RunProjection run, PlanProjection plan,
        List<string> diagnostics)
    {
        var exit = 0;
        var expected = scenario.ExpectedRunState;
        if (expected.Length > 0 && run.State != ParseRunState(expected))
        {
            diagnostics.Add("Run esperado " + expected + ", real " + run.State);
            exit = 1;
        }

        // Estado esperado por item, con los ids simbólicos del escenario (P1, P2…).
        foreach (var kv in scenario.ExpectedPlan)
        {
            var item = _symbolicItems.TryGetValue(kv.Key, out var id) ? plan.Item(id) : null;
            if (item is null)
            {
                diagnostics.Add("PlanItem " + kv.Key + " esperado " + kv.Value + ", no existe");
                exit = 1;
            }
            else if (!item.State.ToString().Equals(kv.Value, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add("PlanItem " + kv.Key + " esperado " + kv.Value + ", real " + item.State);
                exit = 1;
            }
        }

        return exit;
    }

    private static RunState ParseRunState(string text)
    {
        switch (text)
        {
            case "Completed": return RunState.Completed;
            case "CompletedWithIssues": return RunState.CompletedWithIssues;
            case "Running": return RunState.Running;
            case "Failed": return RunState.Failed;
            case "Cancelled": return RunState.Cancelled;
            case "AwaitingInput": return RunState.AwaitingInput;
            default: return RunState.Failed;
        }
    }

    private static LinkRole ParseRole(string role)
    {
        if (role == "verifies")
        {
            return LinkRole.Verifies;
        }

        if (role == "supports")
        {
            return LinkRole.Supports;
        }

        return LinkRole.Implements;
    }
}

/// <summary>Resolvores de ids del escenario (simbólicos a reales).</summary>
public sealed class Entities
{
    public static TaskId Task(Dictionary<string, TaskId> map, string key) =>
        map.TryGetValue(key, out var v) ? v! : TaskId.New();

    public static PlanItemId PlanItem(Dictionary<string, PlanItemId> map, string key) =>
        map.TryGetValue(key, out var v) ? v! : PlanItemId.New();
}
