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
using System.Globalization;

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

    private PromptExpanded? _lastPromptExpanded;

    private string? _pendingPromptOrigin;

    private FakeCatalog? _diagnosticCatalog;

    private ModelCapabilityBoundary? _diagnosticBoundary;

    private RunMode _diagnosticMode = RunMode.Plan;

    /// <summary>
    /// Motivo de un bloqueo de la recuperación del Run real al arrancar (null = recuperación ok o
    /// no aplicable). Visible vía <c>LastRecoveryProblem()</c> y <c>Query("state")</c>. NUNCA se
    /// traga un fallo de recuperación: si la raíz falta/corrompe o el store/reconciliador falla, el
    /// Run queda sin reconciliar y NO continuable automáticamente (ADR-0004 §5).
    /// </summary>
    private string? _recoveryProblem;

    private readonly string? _stateFile;

    private readonly IArtifactStore? _artifacts;

    private string? _workspaceRoot;

    public void ConfigureWorkspaceRoot(string workspaceRoot) => _workspaceRoot = Path.GetFullPath(workspaceRoot);

    public OmniServer(IEventStore store, IEventCodecRegistry codecs, IAuditSink audit,
        IArtifactStore? artifacts = null)
    {
        _ = SecretRedactor.Shared;
        _store = store;
        _codecs = codecs;
        _audit = audit;
        _artifacts = artifacts;
        _engine = BuildEngine(store, codecs, audit);
        _stateFile = null;
        LoadLastSession();
        // Recuperación del Run real (ADR-0004 §5): idempotente, no-op sin run persistido.
        RecoverPendingEffects();
    }

    public OmniServer(IEventStore store, IEventCodecRegistry codecs, IAuditSink audit, string stateFile,
        IArtifactStore? artifacts = null)
    {
        _ = SecretRedactor.Shared;
        _store = store;
        _codecs = codecs;
        _audit = audit;
        _artifacts = artifacts;
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
    /// reconcilia con el <c>FilesystemReconciler</c> REAL contra la RAÍZ DURADERA Y VERIFICADA del
    /// workspace del run (el evento <c>WorkspaceRootEstablished</c>, único origen aceptado). NO usa
    /// el executor del sim: un <c>RunResumeService</c> dedicado, idempotente, que rechaza Runs
    /// terminales y nunca re-ejecuta la tool ni re-infere la respuesta.
    ///
    /// Contrato de seguridad (corrección del bloqueo previo a cwd + catch): NUNCA se sustituye una
    /// raíz faltante/corrompida por el cwd del proceso ni se acepta una ruta de display como
    /// autoridad; sin raíz durable verificada o si el store/reconciliador falla, la recuperación
    /// queda BLOQUEADA — visible (<c>_recoveryProblem</c>) y sin reconciliar ni clasificar Applied—
    /// y el Run NO se continúa automáticamente. Los fallos se exponen, no se tragan.
    /// </summary>
    private HostRecoveryResult RecoverPendingEffects()
    {
        if (_lastSessionId is null || _lastRunId is null)
        {
            // Sin run persistido no hay nada que recuperar; estado limpio.
            _recoveryProblem = null;
            return HostRecoveryResult.Ok(0);
        }

        try
        {
            // Sin efectos laterales pendientes no hay nada que reconciliar: no se exige raíz
            // (p. ej. tras un sim con tools falsas que nunca estableció workspace).
            var withoutReconciler = new RunResumeService(_store, _codecs, null, "");
            if (!withoutReconciler.HasPendingSideEffects(_lastSessionId!))
            {
                // Aun así se cierran las lecturas interrumpidas (EffectClass.None): no necesitan
                // reconciliador ni raíz, y si no quedarían en Started para siempre.
                withoutReconciler.Resume(_lastSessionId!, _lastRunId!);
                _recoveryProblem = null;
                return HostRecoveryResult.Ok(0);
            }
        }
        catch (Exception ex)
        {
            return BlockWith("no se pudo inspeccionar el journal del run (" + (ex.Message ?? "?") +
                "): recuperación bloqueada, sin re-ejecutar ni clasificar Applied");
        }

        string? root;
        try
        {
            root = VerifiedWorkspaceRoot(_lastSessionId!);
        }
        catch (Exception ex)
        {
            // Error del journal/resolución al leer la raíz: se expone y se bloquea, sin reconciliar.
            return BlockWith("no se pudo verificar la raíz durable del run (" + (ex.Message ?? "?") +
                "): recuperación bloqueada, sin re-ejecutar ni clasificar Applied");
        }

        if (root is null)
        {
            // Sin autoridad de paths no se inspecciona el workspace. Se cierra conservadoramente
            // como Unresolvable y se publica la InteractionRequest humana; nunca se clasifica
            // Applied ni se re-ejecuta, conforme a ADR-0004 §5bis.
            try
            {
                new RunResumeService(_store, _codecs, null, "").Resume(_lastSessionId!, _lastRunId!);
            }
            catch (Exception)
            {
                // El diagnóstico de bloqueo de raíz sigue siendo válido; el journal tampoco se
                // puede avanzar con seguridad si la publicación/consolidación falla.
            }
            return BlockWith("sin raíz de workspace durable y verificada: recuperación bloqueada," +
                " el run NO se continúa automáticamente; el efecto no verificable queda para resolución humana");
        }

        var service = new RunResumeService(_store, _codecs,
            new FilesystemReconciler(new PathBoundaryValidator()), root!);
        try
        {
            var n = service.Resume(_lastSessionId!, _lastRunId!);
            _recoveryProblem = null;
            return HostRecoveryResult.Ok(n);
        }
        catch (Exception ex)
        {
            // Error del journal/reconciliador DURANTE la recuperación: no se oculta. El run queda
            // sin continuar automáticamente y sin clasificar Applied para el trabajo no resuelto.
            return BlockWith("fallo de recuperación del run (" + (ex.Message ?? "?") +
                "): el Run queda sin continuar automáticamente, sin re-ejecutar ni clasificar Applied");
        }
    }

    /// <summary>Registra un bloqueo visible y lo devuelve como resultado de recuperación.</summary>
    private HostRecoveryResult BlockWith(string reason)
    {
        _recoveryProblem = reason;
        return HostRecoveryResult.Blocked(reason);
    }

    /// <summary>
    /// Raíz del workspace del run reanudable, reconstruida del evento durable y VERIFICADA (ADR-0004 §5).
    /// Devuelve null (recuperación bloqueada) si: no hay evento <c>WorkspaceRootEstablished</c>, la ruta
    /// no es absoluta o no existe como directorio (workspace movido/borrado). NUNCA devuelve el cwd del
    /// proceso ni una ruta de display. Las excepciones del journal (lectura/códec) se propagan al
    /// llamante, que las expone como bloqueo visible.
    /// </summary>
    private string? VerifiedWorkspaceRoot(SessionId sessionId)
    {
        var (canonical, identity) = RecordedWorkspaceRoot(sessionId);
        if (canonical is null || canonical!.Length == 0
            || !Path.IsPathFullyQualified(canonical!))
        {
            return null;
        }

        // El workspace debe seguir existiendo en la misma ubicación, o no se reconcilia nada.
        if (!Directory.Exists(canonical!))
        {
            return null;
        }

        // ADR-0004 §5 / auditoría M3: Directory.Exists no basta — una ruta reemplazada por
        // symlink/junction sigue "existiendo" pero apunta a OTRO árbol. La identidad durable
        // verifica que el marcador dentro del workspace sigue siendo el mismo token; si el
        // token no existe (evento legacy / identidad no establecida) o no coincide (árbol
        // sustituido / marcador borrado), NO se reconcilia nada: falla cerrado y visible.
        if (!WorkspaceRootIdentity.Verify(canonical!, identity ?? ""))
        {
            return null;
        }

        return canonical;
    }

    /// <summary>
    /// Raíz canónica persistida por la sesión (solo el evento <c>WorkspaceRootEstablished</c>),
    /// junto con su identidad durable (el token del marcador dentro del workspace; ADR-0004 §5).
    /// La recuperación usa el token al reabrir para verificar que la ruta no fue sustituida por
    /// symlink/junction. Token vacío = identidad no establecida: la sesión no es recuperable
    /// automáticamente y la recuperación falla cerrado.
    /// </summary>
    private (string? Root, string? Identity) RecordedWorkspaceRoot(SessionId sessionId)
    {
        foreach (var evt in _store.ReadFrom(sessionId, 1))
        {
            if (!evt.Type.ToString().Equals("workspace.root_established", StringComparison.Ordinal))
            {
                continue;
            }

            var established = _codecs.Decode(evt)
                as WorkspaceRootEstablished;
            if (established is not null && established!.CanonicalRoot is not null
                && established!.CanonicalRoot.Length > 0)
            {
                return (established!.CanonicalRoot, established!.DurableIdentity ?? "");
            }

            return (null, null);
        }

        return (null, null);
    }

    private IReadOnlyList<DomainEvent> EventsForRun(IReadOnlyList<DomainEvent> all, RunId runId)
    {
        var events = new List<DomainEvent>();
        var inRun = false;
        foreach (var evt in all)
        {
            if (evt.Type.ToString() == "run.created")
            {
                var created = _codecs.Decode(evt) as RunCreated;
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

        // ADR-0013 §3: todo evento escrito mientras se atiende el comando lleva su CommandId como
        // causa. Un messageId que no es un UUID no puede ser CommandId: el comando se rechaza.
        if (!Guid.TryParse(command.MessageId, out var commandGuid))
        {
            return CommandAck.Fail(command.MessageId, "messageId no es un identificador válido");
        }

        using var causation = CausationScope.Begin(new CommandCausation(new CommandId(commandGuid)));
        var commandName = fields.TryGetValue("cmd", out var c) ? c : null;
        if (commandName == "command.invoke")
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(command.PayloadJson);
                var root = document.RootElement;
                var name = root.GetProperty("name").GetString() ?? "";
                var args = root.TryGetProperty("arguments", out var argumentArray)
                    ? argumentArray.EnumerateArray().Select(value => value.GetString() ?? "").ToArray()
                    : Array.Empty<string>();
                var origin = root.TryGetProperty("origin", out var originValue)
                    ? originValue.GetString() ?? "Typed" : "Typed";
                _lastPromptExpanded = new CommandService().Expand(
                    new CommandInvocation(name, args, origin));
                _pendingPromptOrigin = _lastPromptExpanded.Origin;
                return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
                or KeyNotFoundException)
            {
                return new CommandAck(command.MessageId, "error", ex.Message, RuntimeCommandOutcome.Rejected());
            }
        }

        if (commandName == "sim")
        {
            return RunSim(command, fields);
        }

        if (commandName == "sim.resume")
        {
            return ResumeSim(command);
        }

        // Conversación ↔ Run, interrupción, cancelación e interacciones (ADR-0035, ADR-0034).
        if (commandName is "session.input" or "run.interrupt" or "run.cancel" or "interaction.respond")
        {
            return RunControl(command, commandName, fields);
        }

        if (commandName == "explore.start")
        {
            return StartExplorerRun(command, fields);
        }

        if (commandName == "act")
        {
            return StartActRun(command, fields);
        }

        return CommandAck.Fail(command.MessageId, "comando desconocido en M1");
    }

    /// <summary>
    /// Eventos del protocolo de la sesión en curso desde la secuencia dada (ADR-0013 §1): los eventos
    /// de dominio del journal traducidos por <see cref="ProtocolMapper"/>, seguidos de las
    /// notificaciones del servidor (resumen de una simulación o de un resume).
    /// </summary>
    public IReadOnlyList<WireEnvelope> SubscribeSince(long fromSequence)
    {
        var result = new List<WireEnvelope>();
        if (_lastSessionId is not null)
        {
            result.AddRange(new ProtocolMapper(_codecs, _artifacts).Map(_store.ReadFrom(_lastSessionId, Math.Max(1, fromSequence))));
        }

        result.AddRange(_events);
        return result;
    }

    /// <summary>Publica PlanApproval tras una propuesta aceptada de plan.propose; no contesta por el usuario.</summary>
    public InteractionId? RequestPlanApprovalIfNeeded()
    {
        if (_lastSessionId is null || _lastRunId is null) return null;
        var all = _store.ReadFrom(_lastSessionId, 1);
        var own = EventsForRun(all, _lastRunId);
        var projection = RunProjection.Replay(_lastSessionId, _lastRunId, _codecs, own);
        if (projection.Mode != RunMode.Plan || projection.State != RunState.Running
            || projection.RootTask is null) return null;

        var requested = new HashSet<ToolCallId>();
        long acceptedProposalSequence = 0;
        long lastApprovalResolutionSequence = 0;
        var pendingApproval = new Dictionary<InteractionId, InteractionRequested>();
        foreach (var evt in own)
        {
            switch (_codecs.Decode(evt))
            {
                case ToolCallRequested call when call.ToolName == "plan.propose": requested.Add(call.ToolCallId); break;
                case ToolCallSucceeded succeeded when requested.Contains(succeeded.ToolCallId):
                    acceptedProposalSequence = evt.Sequence;
                    break;
                case InteractionRequested approvalRequest when approvalRequest.Kind == InteractionKind.PlanApproval:
                    pendingApproval[approvalRequest.InteractionId] = approvalRequest;
                    break;
                case InteractionResolved resolved when pendingApproval.ContainsKey(resolved.InteractionId):
                    pendingApproval.Remove(resolved.InteractionId);
                    lastApprovalResolutionSequence = evt.Sequence;
                    break;
            }
        }
        if (pendingApproval.Count > 0) return pendingApproval.Keys.Last();
        if (acceptedProposalSequence <= lastApprovalResolutionSequence) return null;
        var rootLane = LaneProjection.Replay(_codecs, own).ForTask(projection.RootTask!)
            .FirstOrDefault(lane => lane.State == LaneState.Running)?.Id;
        if (rootLane is null) return null;

        var interaction = InteractionId.New();
        var request = new InteractionRequested(interaction, InteractionKind.PlanApproval,
            "{\"operation\":\"plan.approval\",\"reason\":\"El plan fue propuesto por el modelo y requiere aprobación\"}",
            "[{\"id\":\"approve_execute\",\"intent\":\"allow\"},"
                + "{\"id\":\"approve_only\",\"intent\":\"allow\"},"
                + "{\"id\":\"continue_planning\",\"intent\":\"allow\"},"
                + "{\"id\":\"reject\",\"intent\":\"deny\"}]",
            "reject", null, rootLane, projection.RootTask, null, 0, 1);
        // PlanApproval es una espera humana durable: publica la interacción y la transición
        // canónica del Run a AwaitingInput en el mismo commit (ADR-0034/0035/0036).
        new EventStream(_store, _codecs, _lastSessionId).AppendBatch(
            new DomainEventPayload[] { request, new RunAwaitingInput(_lastRunId, rootLane) },
            DurabilityClass.Standard);
        return interaction;
    }

    /// <summary>Resuelve una interacción usando el protocolo tipado del servidor.</summary>
    public CommandAck RespondToInteraction(InteractionId interaction, string optionId) => Send(
        WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "interaction.respond") + ","
            + JsonObj.Field("interactionId", interaction.ToString()) + ","
            + JsonObj.Field("optionId", optionId) + "}"), CancellationToken.None);

    /// <summary>Envía respuesta tipada del cuestionario por IOmniClient (ADR-0034/0045).</summary>
    public CommandAck RespondToQuestionnaire(InteractionId interaction,
        IReadOnlyList<QuestionAnswer> answers, bool cancelled) => Send(
        WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "interaction.respond") + ","
            + JsonObj.Field("responseType", "questionnaire") + ","
            + JsonObj.Field("interactionId", interaction.ToString()) + ","
            + JsonObj.FieldRaw("answers", QuestionnaireCodec.EncodeAnswers(answers)) + ","
            + JsonObj.FieldRaw("cancelled", cancelled ? "true" : "false") + "}"), CancellationToken.None);

    /// <summary>Proyección diagnóstica de la misma catalog/boundary que recibió ExplorerTurn.</summary>
    public void ConfigureToolDiagnostics(FakeCatalog catalog, ModelCapabilityBoundary boundary, RunMode mode)
    {
        _diagnosticCatalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _diagnosticBoundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
        _diagnosticMode = mode;
    }

    public RunMode CurrentRunMode()
    {
        if (_lastSessionId is null || _lastRunId is null) return RunMode.Plan;
        var events = EventsForRun(_store.ReadFrom(_lastSessionId, 1), _lastRunId);
        return RunProjection.Replay(_lastSessionId, _lastRunId, _codecs, events).Mode ?? RunMode.Plan;
    }

    public SessionId? LastSessionId() => _lastSessionId;

    /// <summary>Consume el origen de un PromptCommand para persistirlo en el próximo UserInputReceived.</summary>
    public string? ConsumePromptOrigin()
    {
        var origin = _pendingPromptOrigin;
        _pendingPromptOrigin = null;
        return origin;
    }

    public RunId? LastRunId() => _lastRunId;

    /// <summary>
    /// Motivo (o null) de un bloqueo de la recuperación del Run real al arrancar. Visible para que
    /// el cliente/CLI pueda reflejar que el run NO es continua automáticamente y pida intervención.
    /// </summary>
    public string? LastRecoveryProblem() => _recoveryProblem;

    private CommandAck StartExplorerRun(WireEnvelope command, Dictionary<string, string> fields)
    {
        var objective = fields.TryGetValue("objective", out var value) ? value : null;
        if (string.IsNullOrWhiteSpace(objective))
        {
            return new CommandAck(command.MessageId, "error", "falta el objetivo del Explorer",
                RuntimeCommandOutcome.Rejected());
        }

        return StartRunAct(command, objective!, Path.GetFullPath("."), RunMode.Plan);
    }

    /// <summary>
    /// <c>act</c>: crea un Run Act REAL reutilizando el Explorer (vertical M3, ADR-0035 §3, ADR-0044 §5).
    /// Acepta <c>objective</c> (la instrucción) y opcionalmente <c>workspace</c> (raíz; por defecto el cwd).
    /// Emite <c>SessionCreated</c> + <c>WorkspaceRootEstablished</c> con identidad durable + el run Act
    /// completo (RunCreated/RunStarted/Task/Lane/Plan). El plan llama al Turn de Explorer con las tools
    /// filesystem.read/filesystem.patch bajo la política efectiva; NO es un sustituto del sim y no
    /// autoaprueba Ask (sin cliente interactivo, Ask → Deny, ADR-0003).
    /// </summary>
    private CommandAck StartActRun(WireEnvelope command, Dictionary<string, string> fields)
    {
        var objective = fields.TryGetValue("objective", out var value) ? value : null;
        if (string.IsNullOrWhiteSpace(objective))
        {
            return new CommandAck(command.MessageId, "error", "falta el objetivo del act",
                RuntimeCommandOutcome.Rejected());
        }

        var workspace = fields.TryGetValue("workspace", out var w) ? w : null;
        var workspacePath = workspace is null || workspace!.Length == 0
            ? Path.GetFullPath(".")
            : Path.GetFullPath(workspace!);
        return StartRunAct(command, objective!, workspacePath);
    }

    /// <summary>Creación compartida de un Run Act real con raíz durable y verificable.</summary>
    private CommandAck StartRunAct(WireEnvelope command, string objective, string workspacePath,
        RunMode mode = RunMode.Act)
    {
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var taskId = TaskId.New();
        var laneId = LaneId.New();
        var planId = PlanId.New();
        var stream = new EventStream(_store, _codecs, sessionId);
        var now = DateTimeOffset.UtcNow;
        var budget = new TaskBudget(null, null, null, null);
        var physicalWorkspaceRoot = ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspacePath);
        stream.Append(new SessionCreated(sessionId, WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(physicalWorkspaceRoot)).ToString(),
            workspacePath, ProfileId.New(), now));
        // Origen explícito y seguro de la raíz del run real (ADR-0004 §5): se fija aquí, en la
        // creación de la sesión, y es lo único que la recuperación acepta al arrancar. Nunca se
        // usa el cwd de un proceso posterior ni WorkspaceDisplayPath como autoridad. Se registra
        // además la identidad durable (ruta física resuelta) para que la recuperación verifique
        // que la ruta no fue sustituida por symlink/junction.
        var durableIdentity = WorkspaceRootIdentity.Establish(workspacePath);
        stream.Append(new WorkspaceRootEstablished(sessionId, workspacePath, now, durableIdentity));
        stream.Append(new RunCreated(runId, sessionId, objective, mode,
            ExecutionStrategy.Direct, FailurePolicy.BlockDependents, budget, taskId, now));
        stream.Append(new RunStarted(runId));
        stream.Append(new TaskCreated(taskId, runId, objective, Array.Empty<TaskDependency>(), budget));
        stream.Append(new TaskReady(taskId));
        stream.Append(new LaneCreated(laneId, taskId, ProfileId.New()));
        stream.Append(new LaneStarted(laneId));
        stream.Append(new TaskStarted(taskId, laneId)); // Ready → Running al arrancar su Lane (ADR-0036 §2)
        stream.Append(new PlanCreated(planId, runId, PlanItemId.New(), objective));

        _lastSessionId = sessionId;
        _lastRunId = runId;
        _lastSnapshot = MaterializeFromJournal(sessionId, runId);
        SaveLastSession();
        return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(), sessionId, 0,
            new CommandId(Guid.Parse(command.MessageId)));
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
                    var payload = _codecs.Decode(evt);
                    var runCreated = payload as RunCreated;
                    inOwnRun = runCreated is not null
                        && runCreated!.RunId.ToString().Equals(_lastRunId!.ToString(), StringComparison.Ordinal);
                    continue;
                }

                if (!inOwnRun || !evt.Type.ToString().Equals("lane.created", StringComparison.Ordinal))
                {
                    continue;
                }

                var lanePayload = _codecs.Decode(evt);
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

    private string? ReadPersistedContextSnapshot()
    {
        if (_artifacts is null || _lastSessionId is null || _lastRunId is null) return null;
        try
        {
            var events = EventsForRun(_store.ReadFrom(_lastSessionId, 1), _lastRunId);
            ArtifactRef? snapshotRef = null;
            foreach (var evt in events)
            {
                if (_codecs.Decode(evt) is TurnStarted started && started.ContextSnapshotRef is not null)
                    snapshotRef = started.ContextSnapshotRef;
            }
            if (snapshotRef is null) return null;

            var snapshotJson = _artifacts.GetText(snapshotRef.Hash);
            if (snapshotJson is null) return null;
            using var source = System.Text.Json.JsonDocument.Parse(snapshotJson);
            using var output = new System.IO.MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(output))
            {
                var root = source.RootElement;
                writer.WriteStartObject();
                writer.WriteString("snapshotId", root.GetProperty("snapshotId").GetString());
                writer.WriteString("fingerprint", root.GetProperty("fingerprint").GetString());
                if (root.TryGetProperty("snapshotFingerprint", out var snapshotFingerprint))
                    writer.WriteString("snapshotFingerprint", snapshotFingerprint.GetString());
                writer.WriteNumber("tokenCount", root.GetProperty("tokenCount").GetInt32());
                if (root.TryGetProperty("tokenAccuracy", out var accuracy))
                {
                    writer.WritePropertyName("tokenAccuracy");
                    accuracy.WriteTo(writer);
                }
                if (root.TryGetProperty("tokenBudget", out var budget))
                {
                    writer.WritePropertyName("tokenBudget");
                    budget.WriteTo(writer);
                }
                if (root.TryGetProperty("overflowed", out var overflowed))
                {
                    writer.WritePropertyName("overflowed");
                    overflowed.WriteTo(writer);
                }
                writer.WriteStartArray("items");
                foreach (var item in root.GetProperty("items").EnumerateArray())
                {
                    writer.WriteStartObject();
                    Copy(item, writer, "id", "kind", "tokens", "tokenAccuracy", "priority", "contributor",
                        "category", "source", "scope", "sensitive", "refs");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("diagnostics");
                if (root.TryGetProperty("diagnostics", out var diagnostics))
                {
                    foreach (var diagnostic in diagnostics.EnumerateArray())
                    {
                        writer.WriteStartObject();
                        Copy(diagnostic, writer, "itemId", "decision", "tokens", "reason", "contributor",
                            "category", "source", "scope", "sensitive");
                        writer.WriteEndObject();
                    }
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(output.ToArray());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or KeyNotFoundException
            or InvalidOperationException)
        {
            return null;
        }
    }

    private static void Copy(System.Text.Json.JsonElement source, System.Text.Json.Utf8JsonWriter writer,
        params string[] properties)
    {
        foreach (var name in properties)
            if (source.TryGetProperty(name, out var value))
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }
    }

    public SessionQueryResult? Query(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (name == "commands")
            return new SessionQueryResult("commands", "{\"commands\":[\"explain\"]}");
        if (name == "workspaceStatus")
            return new SessionQueryResult("workspaceStatus", "{" + JsonObj.Field("workingDirectory", _workspaceRoot ?? "") + "}");
        if (name.StartsWith("complete:", StringComparison.Ordinal))
        {
            var prefix = name.Substring("complete:".Length).Replace('\\', '/');
            var root = _workspaceRoot;
            if (root is null || !Directory.Exists(root) || prefix.Split('/').Any(part => part == ".."))
                return new SessionQueryResult("complete", "{\"paths\":[]}");
            var separator = prefix.LastIndexOf('/');
            var directoryPart = separator < 0 ? "" : prefix.Substring(0, separator);
            var filePart = separator < 0 ? prefix : prefix.Substring(separator + 1);
            var directory = Path.GetFullPath(Path.Combine(root, directoryPart.Replace('/', Path.DirectorySeparatorChar)));
            var rootWithSeparator = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!directory.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
                && !directory.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                return new SessionQueryResult("complete", "{\"paths\":[]}");
            var current = Path.GetFullPath(root);
            foreach (var segment in directoryPart.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        return new SessionQueryResult("complete", "{\"paths\":[]}");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    return new SessionQueryResult("complete", "{\"paths\":[]}");
                }
            }
            var paths = Array.Empty<string>();
            if (Directory.Exists(directory))
            {
                try
                {
                    paths = Directory.EnumerateFiles(directory, filePart + "*", SearchOption.TopDirectoryOnly)
                        .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                        .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                        .Where(path => !path.Split('/').Any(part => part is ".git" or ".omnicore" or "bin" or "obj" or "node_modules"))
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Take(50).ToArray();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    paths = Array.Empty<string>();
                }
            }
            return new SessionQueryResult("complete", "{\"paths\":[" + string.Join(",", paths.Select(path => "\"" + JsonObj.Escape(path) + "\"")) + "]}");
        }
        if (name == "state")
        {
            var run = _lastSnapshot is null ? "none" : "M2:" + _lastSnapshot!.TokenCount.ToString();
            return new SessionQueryResult("state", "{\"runtime\":\"omnicore\",\"milestone\":\"M2\","
                + JsonObj.Field("runState", run)
                + "," + JsonObj.Field("recovery", _recoveryProblem is null ? "ok" : "blocked") + "}");
        }

        if (name == "commandOutcome")
        {
            if (_lastPromptExpanded is null) return new SessionQueryResult("commandOutcome", "{\"outcome\":null}");
            return new SessionQueryResult("commandOutcome", "{\"outcome\":{"
                + JsonObj.Field("text", _lastPromptExpanded.Text) + ","
                + JsonObj.Field("origin", _lastPromptExpanded.Origin) + "}}");
        }

        if (name == "tools")
        {
            var catalog = _diagnosticCatalog ?? OmniHost.CreateExplorerTools().Catalog();
            var boundary = _diagnosticBoundary;
            var entries = new List<string>();
            foreach (var definition in catalog.Definitions())
            {
                var tool = catalog.Find(new ToolId(definition.Name));
                if (tool is null) continue;
                var descriptor = tool.Descriptor;
                var effect = descriptor.EffectClass;
                var claims = descriptor.ReadOnly ? ResourceClaims.Empty()
                    : new ResourceClaims(Array.Empty<string>(), new[] { "diagnostic-target" },
                        Array.Empty<NetworkGrant>(), null, Array.Empty<string>());
                var intent = new ToolIntent(ToolCallId.New(), descriptor.Id, "{}", effect, claims,
                    descriptor.Risk, null);
                var boundaryVisible = boundary is null || boundary.IsToolVisible(definition.Name);
                var boundaryDecision = boundary?.Evaluate(intent);
                var modeRejected = _diagnosticMode == RunMode.Plan && !descriptor.ReadOnly;
                var decision = modeRejected ? "reject:PLAN mode"
                    : boundaryDecision is { Allowed: false } ? "reject:ModelCapabilityBoundary"
                    : boundaryDecision is { RequiresAsk: true } ? "ask:ModelCapabilityBoundary"
                    : descriptor.Destructive ? "ask:permission policy"
                    : "permission policy";
                entries.Add("{" + JsonObj.Field("visibleName", definition.Name) + ","
                    + JsonObj.Field("toolId", descriptor.Id.ToString()) + ","
                    + JsonObj.Field("source", descriptor.Source.Kind + ":" + descriptor.Source.Scope
                        + ":" + descriptor.Source.Trust + ":" + descriptor.Source.Owner
                        + "@" + descriptor.Source.Version) + ","
                    + JsonObj.Field("effectClass", effect.ToString()) + ","
                    + JsonObj.Field("decision", decision) + ","
                    + JsonObj.FieldBool("visible", boundaryVisible && !modeRejected) + "}");
            }
            return new SessionQueryResult("tools", "{" + JsonObj.Field("mode", _diagnosticMode.ToString())
                + ",\"tools\":[" + string.Join(",", entries) + "]}");
        }

        if (name == "workingState")
        {
            if (_lastSessionId is not null && _lastRunId is not null)
            {
                try
                {
                    var own = EventsForRun(_store.ReadFrom(_lastSessionId, 1), _lastRunId);
                    var run = RunProjection.Replay(_lastSessionId, _lastRunId, _codecs, own);
                    var plan = PlanProjection.Replay(_codecs, own);
                    var projected = WorkingStateProjector.Project(run, plan);
                    if (projected is not null) _lastWorkingStateText = WorkingStateProjector.Render(projected);
                }
                catch (Exception) { }
            }
            var safe = _lastWorkingStateText is null || _lastWorkingStateText.Length == 0 ? "{}"
                : "{" + JsonObj.Field("workingState", RedactPii(_lastWorkingStateText)) + "}";
            return new SessionQueryResult("workingState", safe);
        }

        if (name == "context")
        {
            var persisted = ReadPersistedContextSnapshot();
            return new SessionQueryResult("context", persisted is null
                ? "{\"snapshot\":null}" : "{\"snapshot\":" + persisted + "}");
        }

        return null;
    }

    /// <summary>Redacta PII del WorkingState antes de exponerlo en queries.</summary>
    private static string RedactPii(string content) =>
        new OmniCore.Domain.PiiRedactor().Redact(content);

    private CommandAck RunSim(WireEnvelope command, Dictionary<string, string> fields)
    {
        try
        {
            // El escenario viaja completo en el comando (ADR-0041 §1); sin él, uno incluido por nombre.
            var scenarioName = fields.TryGetValue("scenario", out var s) ? s : null;
            var scenario = fields.TryGetValue("scenarioYaml", out var yaml) && yaml.Length > 0
                ? ScenarioLoader.Parse(yaml)
                : "with-tool-crash" == scenarioName
                    ? Scenarios.WithToolCrash()
                    : Scenarios.MultiItemPlan();

            // Permisos del escenario + capa del modo del Run (ADR-0037 §4): el executor de esta simulación.
            _engine.SetToolExecutor(ScriptedToolExecutor.WithCoreTools(HostTools.Default().Catalog(),
                new ScriptedPermissionPolicy(scenario.Permissions).WithModeDefaults(scenario.Mode)));
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
            return result.ExitCode == 0
                ? CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                    result.SessionId, 0, new CommandId(Guid.Parse(command.MessageId)))
                : CommandOutcomeAck(command.MessageId, "error", "sim falló: " + string.Join("; ", result.Diagnostics),
                    RuntimeCommandOutcome.Accepted(), result.SessionId, 0,
                    new CommandId(Guid.Parse(command.MessageId)));
        }
        catch (ScenarioFormatException sfe)
        {
            return new CommandAck(command.MessageId, "error", sfe.UserMessage.Render(),
                RuntimeCommandOutcome.Rejected());
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
        var policy = ContextManagementPolicy.Default;
        var usableContext = 8192L;
        var contextPolicyHash = ComputeContextPolicyHash(policy, usableContext);
        var harnessHash = "harness-v1"; // deterministic placeholder for simulation harness
        var fingerprint = new ExecutionFingerprint(
            "qwen38-27b-local", harnessHash, "core-tools", contextPolicyHash, "none", "M2",
            "", counter.Id.Value);
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
        // Consumidor de eventos para auditoría (INV-012, ADR-0043 §1): el Engine no escribe
        // auditoría; aquí se leen los eventos canónicos de permisos del run y cada decisión
        // (Allow/Ask/Deny, incluida la resolución de cada Ask) se registra como metadata con
        // WorkspaceId, SessionId, RunId, timestamp y referencia al evento original (sesión + seq).
        // El sink redacta secretos (ADR-0018 §4): aquí no entra contenido ni credenciales.
        WorkspaceId? workspace = null;
        var toolNames = new Dictionary<ToolCallId, string>();
        foreach (var evt in _store.ReadFrom(result.SessionId, 1))
        {
            var payload = _codecs.Decode(evt);
            switch (payload)
            {
                case SessionCreated created:
                    workspace = WorkspaceId.Parse(created.WorkspaceId);
                    continue;
                case ToolCallRequested requested:
                    toolNames[requested.ToolCallId] = requested.ToolName;
                    continue;
            }

            var details = PermissionAuditDetails(payload, toolNames);
            if (details is null)
            {
                continue;
            }

            _audit.Record(new AuditRecord(
                evt.Type.ToString(),
                workspace,
                result.SessionId,
                result.RunId,
                evt.Timestamp,
                "session:" + result.SessionId + ":" + evt.Sequence,
                details), CancellationToken.None);
        }
    }

    /// <summary>
    /// Metadata de decisión por evento de permiso (null = no auditable): tool, decisión y
    /// resolución de cada Ask —granted/denied con su causa—, capas evaluadas y grant aplicado.
    /// </summary>
    private static Dictionary<string, string>? PermissionAuditDetails(DomainEventPayload payload,
        Dictionary<ToolCallId, string> toolNames)
    {
        switch (payload)
        {
            case PermissionEvaluated evaluated:
            {
                var details = ToolDetails(toolNames, evaluated.ToolCallId);
                details["decision"] = evaluated.Decision.ToString();
                details["layers"] = evaluated.LayersJson;
                if (evaluated.AppliedGrant is not null)
                {
                    details["grant"] = evaluated.AppliedGrant.ToString();
                }

                return details;
            }

            case PermissionRequested ask:
            {
                var details = ToolDetails(toolNames, ask.ToolCallId);
                details["decision"] = "Ask";
                details["interaction"] = ask.InteractionId.ToString();
                return details;
            }

            case PermissionGranted granted:
            {
                var details = ToolDetails(toolNames, granted.ToolCallId);
                details["decision"] = "Allow";
                if (granted.GrantId is not null)
                {
                    details["grant"] = granted.GrantId.ToString();
                }

                if (granted.Lifetime.HasValue)
                {
                    details["lifetime"] = granted.Lifetime.Value.ToString();
                }

                return details;
            }

            case PermissionDenied denied:
            {
                var details = ToolDetails(toolNames, denied.ToolCallId);
                details["decision"] = "Deny";
                details["cause"] = denied.Cause;
                return details;
            }

            default:
                return null;
        }
    }

    private static Dictionary<string, string> ToolDetails(Dictionary<ToolCallId, string> toolNames,
        ToolCallId callId)
    {
        var details = new Dictionary<string, string>();
        if (toolNames.TryGetValue(callId, out var tool))
        {
            details["tool"] = tool;
        }

        return details;
    }

    /// <summary>
    /// Comandos de control del Run sobre la sesión en curso del servidor. Los errores de dominio
    /// vuelven como <c>CommandAck</c> fallido con su motivo (nunca se inventa un estado).
    /// </summary>
    private CommandAck RunControl(WireEnvelope command, string commandName, Dictionary<string, string> fields)
    {
        var control = new RunControlService(_store, _codecs);
        SessionId? outcomeSession = null;
        long outcomeSequenceBefore = 0;
        var explicitOutcome = commandName is "session.input" or "run.interrupt" or "run.cancel"
            or "interaction.respond";
        var commandId = new CommandId(Guid.Parse(command.MessageId));
        try
        {
            switch (commandName)
            {
                case "session.input":
                {
                    var text = fields.TryGetValue("text", out var t) ? t : "";
                    if (text.Length == 0)
                    {
                        if (explicitOutcome)
                        {
                            return CommandOutcomeAck(command.MessageId, "error", "falta 'text'",
                                RuntimeCommandOutcome.Rejected(), null, 0, commandId);
                        }

                        return CommandAck.Fail(command.MessageId, "falta 'text'");
                    }

                    outcomeSession = _lastSessionId;
                    outcomeSequenceBefore = outcomeSession is null ? 0 : _store.CurrentSequence(outcomeSession);
                    var session = outcomeSession ?? StartSession();
                    outcomeSession = session;
                    var mode = fields.TryGetValue("mode", out var m) && m == "plan" ? RunMode.Plan : RunMode.Act;
                    _lastRunId = control.SendInput(session, text, mode, ConsumePromptOrigin());
                    _lastSessionId = session;
                    break;
                }

                case "run.interrupt":
                    outcomeSession = RequireSession();
                    outcomeSequenceBefore = _store.CurrentSequence(outcomeSession);
                    control.Interrupt(outcomeSession, RunFrom(fields));
                    break;
                case "run.cancel":
                    outcomeSession = RequireSession();
                    outcomeSequenceBefore = _store.CurrentSequence(outcomeSession);
                    control.CancelRun(outcomeSession, RunFrom(fields));
                    break;
                case "interaction.respond":
                {
                    var interaction = fields.TryGetValue("interactionId", out var i) && Guid.TryParse(i, out var g)
                        ? new InteractionId(g)
                        : throw new FormatException("interactionId inválido");
                    if (fields.TryGetValue("responseType", out var responseType) && responseType == "questionnaire")
                    {
                        if (_artifacts is null) throw new FormatException("artifact store no disponible");
                        var session = RequireSession();
                        outcomeSession = session;
                        outcomeSequenceBefore = _store.CurrentSequence(session);
                        var questionnaire = new QuestionnaireInteractionService(_store, _codecs, _artifacts);
                        var answers = QuestionnaireCodec.DecodeAnswers(fields.TryGetValue("answers", out var a) ? a : "[]");
                        var cancelled = fields.TryGetValue("cancelled", out var c) && c == "true";
                        var stream = new EventStream(_store, _codecs, session);
                        DomainEventPayload? transition = null;
                        if (_lastRunId is { } activeRun
                            && RunProjection.Replay(session, activeRun, _codecs, _store.ReadFrom(session, 1)).State
                                == RunState.AwaitingInput)
                            transition = new UserInputReceived(activeRun, "[\"QuestionnaireResponse\"]", null,
                                "InteractionResponse(Questionnaire)");
                        var result = questionnaire.Resolve(stream, interaction, answers, cancelled, null, transition);
                        if (!result.Accepted)
                            throw new FormatException(result.AlreadyResolved ? "interacción ya resuelta"
                                : result.UnknownInteraction ? "interacción desconocida"
                                : "respuesta inválida: " + string.Join(",", (result.Errors ?? Array.Empty<QuestionnaireError>())
                                    .Select(e => e.Code.ToString())));
                    }
                    else
                    {
                        var session = RequireSession();
                        var sequenceBeforeResponse = _store.CurrentSequence(session);
                        outcomeSession = session;
                        outcomeSequenceBefore = sequenceBeforeResponse;
                        control.Respond(session, interaction,
                            fields.TryGetValue("optionId", out var o) ? o : "");
                        AuditEffectResolutions(session, sequenceBeforeResponse, interaction);
                        if (control.UnreconciledEffects(session).Count == 0) _recoveryProblem = null;
                    }
                    break;
                }
            }

            SaveLastSession();
            if (explicitOutcome)
            {
                return CommandOutcomeAck(command.MessageId, "ok", null,
                    RuntimeCommandOutcome.Accepted(), outcomeSession, outcomeSequenceBefore, commandId);
            }

            return CommandAck.Ok(command.MessageId);
        }
        catch (UnreconciledEffectException ex)
        {
            var interactionId = ex.Interactions.FirstOrDefault()?.ToString() ?? "none";
            var error = "effects.unresolved(interactionId=" + interactionId + ")";
            if (explicitOutcome)
            {
                return CommandOutcomeAck(command.MessageId, "error", error,
                    RuntimeCommandOutcome.Rejected(), outcomeSession, outcomeSequenceBefore, commandId);
            }

            return CommandAck.Fail(command.MessageId, error);
        }
        catch (Exception ex) when (ex is RunAlreadyActiveException or RunNotActiveException
            or InteractionNotPendingException or InvalidInteractionOptionException or InvalidStateTransitionException
            or FormatException or ArgumentException)
        {
            if (explicitOutcome)
            {
                return CommandOutcomeAck(command.MessageId, "error", ex.Message,
                    RuntimeCommandOutcome.Rejected(), outcomeSession, outcomeSequenceBefore, commandId);
            }

            return CommandAck.Fail(command.MessageId, ex.Message);
        }
    }

    private CommandAck CommandOutcomeAck(string commandMessageId, string status, string? error,
        RuntimeCommandOutcome outcome, SessionId? session, long sequenceBefore, CommandId commandId)
    {
        if (session is null)
        {
            return new CommandAck(commandMessageId, status, error, outcome);
        }

        var causedSequences = _store.ReadFrom(session, sequenceBefore + 1)
            .Where(evt => evt.Causation is CommandCausation causation
                && causation.CommandId == commandId)
            .Select(evt => evt.Sequence)
            .ToArray();
        return causedSequences.Length == 0
            ? new CommandAck(commandMessageId, status, error, outcome)
            : new CommandAck(commandMessageId, status, error, outcome,
                causedSequences.Min(), causedSequences.Max());
    }

    private void AuditEffectResolutions(SessionId session, long sequenceBeforeResponse, InteractionId interaction)
    {
        WorkspaceId? workspace = null;
        foreach (var evt in _store.ReadFrom(session, 1))
        {
            if (_codecs.Decode(evt) is SessionCreated created)
            {
                try { workspace = WorkspaceId.Parse(created.WorkspaceId); }
                catch (FormatException) { workspace = null; }
                break;
            }
        }

        foreach (var evt in _store.ReadFrom(session, sequenceBeforeResponse + 1))
        {
            if (_codecs.Decode(evt) is not ToolCallReconciled { Cause: InteractionCause.User } resolved)
                continue;
            _audit.Record(new AuditRecord("effect.human_resolution", workspace, session, evt.CorrelationId,
                evt.Timestamp, "session:" + session + ":" + evt.Sequence,
                new Dictionary<string, string>
                {
                    ["interactionId"] = interaction.ToString(),
                    ["toolCallId"] = resolved.ToolCallId.ToString(),
                    ["outcome"] = resolved.Outcome.ToString(),
                }), CancellationToken.None);
        }
    }

    private SessionId RequireSession() =>
        _lastSessionId ?? throw new FormatException("no hay una sesión activa");

    private RunId RunFrom(Dictionary<string, string> fields) =>
        fields.TryGetValue("runId", out var r) && Guid.TryParse(r, out var g)
            ? new RunId(g)
            : _lastRunId ?? throw new FormatException("falta runId y no hay un Run en curso");

    /// <summary>Crea una sesión nueva en el journal (primer input sin sesión previa).</summary>
    private SessionId StartSession()
    {
        var session = SessionId.New();
        var workspace = Path.GetFullPath(".");
        new EventStream(_store, _codecs, session).Append(new SessionCreated(session,
            WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(workspace)).ToString(), workspace, ProfileId.New(), DateTimeOffset.UtcNow));
        return session;
    }

    private CommandAck ResumeSim(WireEnvelope command)
    {
        if (_lastSessionId is null || _lastRunId is null)
        {
            return new CommandAck(command.MessageId, "error", "no hay un run previo para reanudar",
                RuntimeCommandOutcome.Rejected());
        }

        var session = _lastSessionId;
        var sequenceBefore = _store.CurrentSequence(session);
        try
        {
            var stream = new EventStream(_store, _codecs, session);
            var reconciled = _engine.Resume(session, _lastRunId!, stream);
            _events.Add(WireEnvelope.Event(Ids.NewV7(), "{" + JsonObj.FieldRaw("type", "\"sim.resumed\"")
                + "," + JsonObj.Field("reconciled", reconciled == 0 ? "0" : "1") + "}"));
            return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                session, sequenceBefore, new CommandId(Guid.Parse(command.MessageId)));
        }
        catch (Exception ex)
        {
            return CommandAck.FailWithCause(command.MessageId, ex.Message ?? "exception",
                ex.StackTrace is null ? "" : string.Join("; ", ex.StackTrace));
        }
    }

    /// <summary>
    /// Computa un hash determinista de la política de contexto efectiva (ContextManagementPolicy + budget).
    /// Versión 1: campos estables de ContextManagementPolicy + budget de tokens utilizables.
    /// </summary>
    private static string ComputeContextPolicyHash(ContextManagementPolicy policy, long usableContext)
    {
        var canonical = string.Join("|",
            "ctx-policy-v1",
            policy.ExternalizeAboveCharacters.ToString(CultureInfo.InvariantCulture),
            policy.CompressBodyCharacters.ToString(CultureInfo.InvariantCulture),
            policy.RecentTailItems.ToString(CultureInfo.InvariantCulture),
            policy.CompactAfterItems.ToString(CultureInfo.InvariantCulture),
            policy.MaxCheckpointCharacters.ToString(CultureInfo.InvariantCulture),
            usableContext.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>
/// Resultado de la recuperación del Run real al arrancar el Host (ADR-0004 §5).
/// <c>Ok</c>: la verificación de raíz pasó y (si había toolcalls huérfanas) se reconciliaron;
/// <c>Reconciled</c> cuenta la reconciliación. <c>Blocked</c>: no se pudo verificar la raíz durable o
/// falló el store/reconciliador; el Run NO se continúa automáticamente y no se reconcilió nada
/// (sin re-ejecutar ni clasificar Applied). El motivo se expone en <c>Reason</c>.
/// </summary>
public sealed record HostRecoveryResult(int Reconciled, string? BlockedReason)
{
    public bool IsBlocked() => BlockedReason is not null;

    public static HostRecoveryResult Ok(int reconciled) => new(reconciled, null);

    public static HostRecoveryResult Blocked(string reason) => new(0, reason);
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
                // Un item sin Tasks vinculadas no se completa solo (ADR-0016 §5): P2 tiene su trabajo.
                new SimulatedTask("T2",
                    new SimulatedLink[] { new SimulatedLink("P2", "implements") },
                    new string[] { "T1" }, "Implementar"),
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
