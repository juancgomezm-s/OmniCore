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
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Runtime.CompilerServices;

/// <summary>
/// OmniServer: sesión de protocolo in-process que expone el runtime detrás de IOmniClient
/// (ADR-0019 §1). Traduce los comandos wire a llamadas del Engine y acumula los eventos wire.
/// En M1 atiende <c>sim</c> y la query <c>state</c>.
/// </summary>
public sealed partial class OmniServer : IOmniClient, ITrustedUserActionClient
{
    private static readonly ConditionalWeakTable<IEventStore, object> ModeAuthorityGates = new();

    private readonly IEventStore _store;

    private readonly object _modeAuthorityMutationGate;

    private readonly IEventCodecRegistry _codecs;

    private readonly IAuditSink _audit;

    private readonly SimulationEngine _engine;

    private readonly List<WireEnvelope> _events = new();

    private SessionId? _lastSessionId;

    private RunId? _lastRunId;

    private ContextSnapshot? _lastSnapshot;

    private string _lastWorkingStateText = "";

    private PromptExpanded? _lastPromptExpanded;

    private WorkflowRequested? _lastWorkflowRequested;

    private WorkflowInvocation? _lastWorkflowInvocation;

    private string? _pendingPromptOrigin;

    private readonly CommandService _commandService = new();

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
    private IPlatformPaths? _sidebarPaths;
    public void ConfigureSidebarPaths(IPlatformPaths paths) => _sidebarPaths = paths;
    private SidebarConfiguration SidebarSettings() => new(_sidebarPaths ?? OmniHost.CreatePlatformPaths(), _workspaceRoot ?? Environment.CurrentDirectory);
    private readonly UserWorkspaceSpendReader? _userSpendReader;
    private readonly string? _userDatabasePath;
    public SessionObservationHub Observability { get; }

    private string? _workspaceRoot;
    private SessionRoutingPolicy _newSessionRoutingPolicy = SessionRoutingPolicy.Empty();
    private AgentProfileConfiguration.Loaded _agentProfiles = new(new AgentProfileRegistry([]), null);

    internal void ConfigureAgentProfiles(AgentProfileConfiguration.Loaded profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _agentProfiles = profiles;
    }

    internal AgentProfile? ResolveLaneAgentProfile(SessionId sessionId, RunId runId, LaneId laneId)
    {
        var lane = new EventStream(_store, _codecs, sessionId).EventsSince(1)
            .Where(evt => evt.RunId == runId).Select(_codecs.Decode)
            .OfType<LaneCreated>().SingleOrDefault(created => created.LaneId == laneId)
            ?? throw new InvalidOperationException("Cannot resolve the requested Run/Lane configuration.");
        if (lane.AgentProfileRevision is null && lane.AgentProfileHash is null) return null;
        if (lane.AgentProfileRevision is null || lane.AgentProfileHash is null)
            throw new InvalidOperationException("Incomplete durable AgentProfile binding.");
        var profile = _agentProfiles.Registry.Find(lane.AgentProfile)
            ?? throw new InvalidOperationException("The Lane's configured AgentProfile is unavailable.");
        if (profile.Revision != lane.AgentProfileRevision || AgentProfileFingerprint.Hash(profile) != lane.AgentProfileHash)
            throw new InvalidOperationException("The Lane's configured AgentProfile has changed; execution is blocked.");
        return profile;
    }

    internal void ConfigureNewSessionRoutingPolicy(SessionRoutingPolicy policy)
    {
        var frozen = policy.Freeze();
        if (frozen.Revision != 1 || frozen.BillingPolicy.Any(mode => mode is BillingMode.Unknown or BillingMode.MeteredCurrency))
            throw new ArgumentException("Initial routing policy cannot grant unconsented metered or unknown routes");
        _newSessionRoutingPolicy = frozen;
    }

    public void ConfigureWorkspaceRoot(string workspaceRoot) => _workspaceRoot = Path.GetFullPath(workspaceRoot);

    public OmniServer(IEventStore store, IEventCodecRegistry codecs, IAuditSink audit,
        IArtifactStore? artifacts = null, UserWorkspaceSpendReader? userSpendReader = null)
    {
        _ = SecretRedactor.Shared;
        _store = store;
        _modeAuthorityMutationGate = ModeAuthorityGates.GetValue(store, static _ => new object());
        _codecs = codecs;
        _audit = audit;
        _artifacts = artifacts;
        _userSpendReader = userSpendReader;
        _userDatabasePath = userSpendReader is null ? null : Path.Combine(userSpendReader.UserDataDirectory, "user.db");
        Observability = new SessionObservationHub(store, codecs, artifacts);
        _engine = BuildEngine(store, codecs, audit);
        _stateFile = null;
        LoadLastSession();
        // Recuperación del Run real (ADR-0004 §5): idempotente, no-op sin run persistido.
        RecoverPendingEffects();
        RecoverPendingAgentOrchestration();
    }

    public OmniServer(IEventStore store, IEventCodecRegistry codecs, IAuditSink audit, string stateFile,
        IArtifactStore? artifacts = null, UserWorkspaceSpendReader? userSpendReader = null)
    {
        _ = SecretRedactor.Shared;
        _store = store;
        _modeAuthorityMutationGate = ModeAuthorityGates.GetValue(store, static _ => new object());
        _codecs = codecs;
        _audit = audit;
        _artifacts = artifacts;
        _userSpendReader = userSpendReader;
        _userDatabasePath = userSpendReader is null ? null : Path.Combine(userSpendReader.UserDataDirectory, "user.db");
        Observability = new SessionObservationHub(store, codecs, artifacts);
        _engine = BuildEngine(store, codecs, audit);
        _stateFile = stateFile;
        LoadLastSession();
        // Recuperación del Run real (ADR-0004 §5): idempotente, no-op sin run persistido.
        RecoverPendingEffects();
        RecoverPendingAgentOrchestration();
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
            new WorktreeIntegrationAwareReconciler(root!, _artifacts,
                new FilesystemReconciler(new PathBoundaryValidator())), root!);
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

    public CommandAck Send(WireEnvelope command, CancellationToken cancellationToken) =>
        SafeCommandAck(SendCore(command, cancellationToken, trustedUserAction: false));

    public CommandAck SendUserAction(WireEnvelope command, CancellationToken cancellationToken) =>
        SafeCommandAck(SendCore(command, cancellationToken, trustedUserAction: true));

    private static string SafeCommandError(string error)
    {
        try { return new PiiRedactor().Redact(error); }
        catch (Exception) { return "No se pudo completar el command."; }
    }

    private static CommandAck SafeCommandAck(CommandAck ack) => ack.Error is null ? ack
        : new CommandAck(ack.CommandId, ack.Status, SafeCommandError(ack.Error), ack.Outcome, ack.FirstSeq, ack.LastSeq);

    private CommandAck SendCore(WireEnvelope command, CancellationToken cancellationToken, bool trustedUserAction,
        WorkflowRuntimeAuthorization? workflowAuthorization = null)
    {
        if (command.MessageType != MessageTypes.Command)
        {
            return new CommandAck(command.MessageId, "error",
                "esperaba un command, recibí " + command.MessageType, RuntimeCommandOutcome.Rejected());
        }

        var fields = JsonObj.Parse(command.PayloadJson);
        var queryName = fields.TryGetValue("query", out var q) ? q : null;
        if (queryName is not null)
        {
            return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.NoOp());
        }

        // ADR-0013 §3: todo evento escrito mientras se atiende el comando lleva su CommandId como
        // causa. Un messageId que no es un UUID no puede ser CommandId: el comando se rechaza.
        if (!Guid.TryParse(command.MessageId, out var commandGuid))
        {
            return new CommandAck(command.MessageId, "error", "messageId no es un identificador válido",
                RuntimeCommandOutcome.Rejected());
        }

        using var causation = CausationScope.Begin(new CommandCausation(new CommandId(commandGuid)));
        var commandName = fields.TryGetValue("cmd", out var c) ? c : null;
        if (commandName == "sidebar.configure")
        {
            if (!trustedUserAction) return new(command.MessageId, "error", "Trusted user action required", RuntimeCommandOutcome.Rejected());
            try
            {
                SidebarSettings().Set(fields.GetValueOrDefault("scope") ?? "", fields.GetValueOrDefault("key") ?? "",
                    fields.GetValueOrDefault("value") ?? "", fields.GetValueOrDefault("revision") ?? "");
                return new(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException
                or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
            { return new(command.MessageId, "error", exception is ConfigValidationException ? "Invalid sidebar settings" : exception.Message, RuntimeCommandOutcome.Rejected()); }
        }
        if (commandName == "delegation.create") return CreateQueuedDelegation(command, trustedUserAction,
            cancellationToken, workflowAuthorization);
        if (commandName == "fanout.create") return CreateFanOutGroup(command, trustedUserAction, cancellationToken);
        if (commandName == "fanout.replace_member") return ReplaceFanOutMember(command, trustedUserAction, cancellationToken);
        if (commandName is "delegation.cancel" or "delegation.disposition" or "delegation.mailbox.send"
            or "execution.join" or "execution.join.cancel")
            return ControlDelegation(command, commandName, trustedUserAction, cancellationToken);
        if (commandName == "command.invoke")
        {
            try
            {
                _lastWorkflowInvocation = null;
                _lastWorkflowRequested = null;
                using var document = System.Text.Json.JsonDocument.Parse(command.PayloadJson);
                var root = document.RootElement;
                var name = root.GetProperty("name").GetString() ?? "";
                var args = root.TryGetProperty("arguments", out var argumentArray)
                    ? argumentArray.EnumerateArray().Select(value => value.GetString() ?? "").ToArray()
                    : Array.Empty<string>();
                var origin = root.TryGetProperty("origin", out var originValue)
                    ? originValue.GetString() ?? "Typed" : "Typed";
                var commandContext = CommandContextForCurrentRun();
                var invoked = _commandService.InvokeAsync(new CommandInvocation(name, args, origin),
                    commandContext,
                    cancellationToken).AsTask().GetAwaiter().GetResult();
                if (invoked is PromptCommandRequested prompt)
                {
                    _lastPromptExpanded = new PromptExpanded(prompt.Text, prompt.Origin);
                    _lastWorkflowRequested = null;
                    _pendingPromptOrigin = prompt.Origin;
                }
                else if (invoked is WorkflowRequested workflow)
                {
                    _lastPromptExpanded = null;
                    _lastWorkflowRequested = workflow;
                    _lastWorkflowInvocation = new WorkflowInvocation(command.MessageId, workflow,
                        commandContext.Session, commandContext.Run);
                    _pendingPromptOrigin = null;
                }
                else throw new InvalidOperationException("Command handler returned an unsupported outcome.");
                return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
                or KeyNotFoundException or ArgumentException)
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
            return RunControl(command, commandName, fields, trustedUserAction);
        }

        if (commandName == "explore.start")
        {
            return StartExplorerRun(command, fields);
        }

        if (commandName is "mode.default.set" or "run.mode.select" or "run.mode.revoke"
            or "reasoning.default.set" or "reasoning.default.revoke"
            or "run.reasoning.select" or "run.reasoning.revoke")
        {
            if (!trustedUserAction)
                return new CommandAck(command.MessageId, "error", "mode and reasoning authority require a trusted user action",
                    RuntimeCommandOutcome.Rejected());
            return commandName switch
            {
                "mode.default.set" => SetDefaultRunMode(command, fields),
                "run.mode.select" => SelectRunMode(command, fields),
                "run.mode.revoke" => RevokeRunModeAuthority(command, fields),
                "reasoning.default.set" => SetDefaultReasoning(command, fields),
                "reasoning.default.revoke" => RevokeDefaultReasoning(command, fields),
                "run.reasoning.select" => SelectRunReasoning(command, fields),
                _ => RevokeRunReasoning(command, fields),
            };
        }

        if (commandName == "act")
        {
            return StartActRun(command, fields);
        }

        return new CommandAck(command.MessageId, "error", "comando desconocido o payload inválido",
            RuntimeCommandOutcome.Rejected());
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
            var journal = _store.ReadFrom(_lastSessionId, 1);
            var events = journal.Where(evt => evt.Sequence >= Math.Max(1, fromSequence)).ToArray();
            result.AddRange(new ProtocolMapper(_codecs, _artifacts).Map(events, journal));
        }

        result.AddRange(_events);
        return result;
    }

    /// <summary>Publica PlanApproval tras una propuesta aceptada de plan.propose; no contesta por el usuario.</summary>
    public InteractionId? RequestPlanApprovalIfNeeded() => RequirePlanApprovalInteraction(RequestPlanApprovalCommand());

    internal static InteractionId? RequirePlanApprovalInteraction(
        (InteractionId? InteractionId, CommandAck Ack, Exception? Failure) publication)
    {
        if (publication.Failure is { } failure)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        if (publication.Ack.Status != "ok"
            || publication.Ack.Outcome?.Kind is not (RuntimeCommandOutcomeKind.Accepted or RuntimeCommandOutcomeKind.NoOp))
            throw new InvalidOperationException(publication.Ack.Error ?? "Plan approval publication was not confirmed");
        return publication.InteractionId;
    }

    /// <summary>Internal command boundary for the conditional PlanApproval publication.</summary>
    internal (InteractionId? InteractionId, CommandAck Ack, Exception? Failure) RequestPlanApprovalCommand()
    {
        var ambientCommand = CausationScope.Current as CommandCausation;
        var commandId = ambientCommand?.CommandId ?? CommandId.New();
        var commandMessageId = commandId.Value.ToString();
        var sessionId = _lastSessionId;
        long? sequenceBefore = null;
        try
        {
            sequenceBefore = sessionId is null ? 0 : _store.CurrentSequence(sessionId);
            using var internalCommand = ambientCommand is null
                ? CausationScope.Begin(new CommandCausation(commandId)) : null;

            CommandAck NoOpAck() => new(commandMessageId, "ok", null, RuntimeCommandOutcome.NoOp());

            if (sessionId is null || _lastRunId is null) return (null, NoOpAck(), null);
            var runId = _lastRunId;
            var all = _store.ReadFrom(sessionId, 1);
            var own = EventsForRun(all, runId);
            var projection = RunProjection.Replay(sessionId, runId, _codecs, own);
            if (projection.Mode != RunMode.Plan || projection.State is not (RunState.Running or RunState.AwaitingInput)
                || projection.RootTask is null) return (null, NoOpAck(), null);

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
            if (pendingApproval.Count > 0) return (pendingApproval.Keys.Last(), NoOpAck(), null);
            if (projection.State != RunState.Running || acceptedProposalSequence <= lastApprovalResolutionSequence)
                return (null, NoOpAck(), null);
            var rootLane = LaneProjection.Replay(_codecs, own).ForTask(projection.RootTask!)
                .FirstOrDefault(lane => lane.State == LaneState.Running)?.Id;
            if (rootLane is null) return (null, NoOpAck(), null);

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
            using var execution = ExecutionScope.Begin(new ExecutionScopeState(runId, projection.RootTask, rootLane));
            new EventStream(_store, _codecs, sessionId).AppendBatch(
                new DomainEventPayload[] { request, new RunAwaitingInput(runId, rootLane) },
                DurabilityClass.Standard);
            var publicationAck = CommandOutcomeAck(commandMessageId, "ok", null,
                RuntimeCommandOutcome.Accepted(), sessionId, sequenceBefore.Value, commandId);
            return (publicationAck.Status == "ok" ? interaction : null, publicationAck, null);
        }
        catch (Exception failure)
        {
            if (sessionId is null || sequenceBefore is null)
                return (null, UnavailableCommandOutcome(commandMessageId), failure);
            try
            {
                // Confirm once: neither a locally generated ID nor an uncertain append is durable proof.
                var persisted = CommandResultEvents(sessionId, sequenceBefore.Value, commandId);
                var interaction = persisted.Select(_codecs.Decode).OfType<InteractionRequested>()
                    .LastOrDefault(request => request.Kind == InteractionKind.PlanApproval)?.InteractionId;
                var ack = persisted.Length == 0
                    ? new CommandAck(commandMessageId, "error", failure.Message, RuntimeCommandOutcome.Rejected())
                    : new CommandAck(commandMessageId, "error", failure.Message, RuntimeCommandOutcome.Accepted(),
                        persisted.Min(evt => evt.Sequence), persisted.Max(evt => evt.Sequence));
                return (interaction, ack, failure);
            }
            catch (Exception)
            {
                return (null, UnavailableCommandOutcome(commandMessageId), failure);
            }
        }
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
        if (_lastSessionId is null || _lastRunId is null) return RunMode.Act;
        var events = EventsForRun(_store.ReadFrom(_lastSessionId, 1), _lastRunId);
        return RunProjection.Replay(_lastSessionId, _lastRunId, _codecs, events).Mode ?? ReadModePreference().Mode;
    }

    public RunModeAuthority? CurrentModeAuthority()
    {
        if (_lastSessionId is null || _lastRunId is null) return null;
        var events = EventsForRun(_store.ReadFrom(_lastSessionId, 1), _lastRunId);
        return RunProjection.Replay(_lastSessionId, _lastRunId, _codecs, events).ModeAuthority;
    }

    private CommandContext CommandContextForCurrentRun()
    {
        if (_lastSessionId is not { } session || _lastRunId is not { } run)
            return new CommandContext(null, null, null, _workspaceRoot, false,
                "Start a Run and authorize Orchestrate mode before requesting a workflow.");
        var journal = _store.ReadFrom(session, 1);
        var projection = RunProjection.Replay(session, run, _codecs, EventsForRun(journal, run));
        var authority = projection.ModeAuthority;
        var authorized = projection.State == RunState.Running && authority?.Mode == RunMode.Orchestrate
            && authority.Authorization is not null
            && authority.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow);
        var reason = authorized ? null : "An active Orchestrate Run with current authorization is required.";
        return new CommandContext(session, run, projection.Mode ?? CurrentRunMode(), _workspaceRoot, authorized, reason);
    }

    public RunReasoningSelectionState? CurrentRunReasoningSelection()
    {
        if (_lastSessionId is null || _lastRunId is null) return null;
        var events = EventsForRun(_store.ReadFrom(_lastSessionId, 1), _lastRunId);
        return RunProjection.Replay(_lastSessionId, _lastRunId, _codecs, events).ReasoningSelection;
    }

    private RunModePreference ReadModePreference()
    {
        if (_userDatabasePath is null) return new RunModePreference(RunMode.Act, 0);
        using var store = new RunModePreferenceStore(_userDatabasePath);
        return store.Read();
    }

    private UserReasoningPreference ReadReasoningPreference()
    {
        if (_userDatabasePath is null) return new UserReasoningPreference(false, null, 0);
        using var store = new ReasoningPreferenceStore(_userDatabasePath);
        return store.Read();
    }

    private static bool TryParseRunMode(string? value, out RunMode mode)
    {
        mode = value?.ToLowerInvariant() switch
        {
            "plan" => RunMode.Plan,
            "act" => RunMode.Act,
            "orq" or "orchestrate" => RunMode.Orchestrate,
            _ => (RunMode)(-1),
        };
        return Enum.IsDefined(mode);
    }

    internal static string ModeAuthorityJson(RunModeAuthority authority, DateTimeOffset utcNow)
    {
        var auth = authority.Authorization;
        var allowed = auth?.AllowedModes ?? Array.Empty<RunMode>();
        var limits = auth?.Limits;
        var authorization = auth is null ? "null" : "{"
            + JsonObj.Field("authorizationId", auth.AuthorizationId.ToString("D"))
            + ",\"allowedModes\":[" + string.Join(",", allowed.Select(mode =>
                "\"" + mode.ToString().ToLowerInvariant() + "\"")) + "],\"limits\":{"
            + "\"maxAgents\":" + limits!.MaxAgents.ToString(CultureInfo.InvariantCulture)
            + ",\"maxDepth\":" + limits.MaxDepth.ToString(CultureInfo.InvariantCulture)
            + ",\"maxTurns\":" + limits.MaxTurns.ToString(CultureInfo.InvariantCulture)
            + ",\"maxToolCalls\":" + limits.MaxToolCalls.ToString(CultureInfo.InvariantCulture)
            + ",\"maxElapsedSeconds\":" + limits.MaxElapsedSeconds.ToString(CultureInfo.InvariantCulture)
            + ",\"maxSpendUsd\":" + limits.MaxSpendUsd.ToString(CultureInfo.InvariantCulture)
            + "}," + JsonObj.Field("grantedAtUtc", auth.GrantedAtUtc!.Value.ToString("O", CultureInfo.InvariantCulture))
            + ",\"planCoverage\":" + (auth.PlanCoverage is not { } coverage ? "null" : "{"
                + JsonObj.Field("runId", coverage.RunId.ToString()) + ","
                + JsonObj.Field("planId", coverage.PlanId.ToString())
                + ",\"planRevision\":" + coverage.PlanRevision.ToString(CultureInfo.InvariantCulture) + ","
                + JsonObj.Field("rootTaskId", coverage.RootTaskId.ToString()) + "}")
            + ",\"expired\":" + (auth.IsExpiredAt(utcNow) ? "true" : "false") + "}";
        return "{" + JsonObj.Field("runId", authority.RunId.ToString())
            + ",\"revision\":" + authority.Revision.ToString(CultureInfo.InvariantCulture)
            + "," + JsonObj.Field("mode", authority.Mode.ToString().ToLowerInvariant())
            + "," + JsonObj.Field("strategy", authority.Strategy.ToString().ToLowerInvariant())
            + "," + JsonObj.Field("effort", authority.ProductEffort.ToString().ToLowerInvariant())
            + ",\"modePinned\":" + (authority.ModePinned ? "true" : "false")
            + ",\"autoModeSwitch\":" + (authority.AutoModeSwitch ? "true" : "false")
            + ",\"effectiveAutoModeSwitch\":" + (authority.IsAutoModeSwitchEffectiveAt(utcNow) ? "true" : "false")
            + "," + JsonObj.Field("objectiveDigest", authority.ObjectiveDigest)
            + ",\"authorization\":" + authorization + "}";
    }

    private CommandAck SetDefaultRunMode(WireEnvelope command, Dictionary<string, string> fields)
    {
        try
        {
            if (_userDatabasePath is null || !fields.TryGetValue("mode", out var rawMode)
                || !TryParseRunMode(rawMode, out var mode)
                || !fields.TryGetValue("expectedRevision", out var rawRevision)
                || !long.TryParse(rawRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var expectedRevision))
                return new CommandAck(command.MessageId, "error",
                    "mode.default.set requires a valid mode, User database, and expectedRevision",
                    RuntimeCommandOutcome.Rejected());

            using var store = new RunModePreferenceStore(_userDatabasePath);
            store.Set(mode, expectedRevision);
            return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
        }
        catch (RunModePreferenceConflictException)
        {
            return new CommandAck(command.MessageId, "error", "Run mode preference revision conflict",
                RuntimeCommandOutcome.Rejected());
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or UnauthorizedAccessException or SqliteException or OverflowException)
        {
            return new CommandAck(command.MessageId, "error", "Could not persist the User Run mode preference",
                RuntimeCommandOutcome.Rejected());
        }
    }

    /// <summary>
    /// Applies one deterministic UltraCode policy decision. This is an internal Host boundary,
    /// not a wire command: the caller cannot choose an origin, grant, objective, limits, or scope
    /// from model/tool output. The Run revision is reloaded while holding the shared store gate.
    /// </summary>
    internal InternalCommandResult ApplyUltraCodePolicyTransition(SessionId sessionId, RunId runId,
        RunMode targetMode, string reason, long expectedAuthorityRevision,
        CancellationToken cancellationToken = default, EventId? proposalEventId = null)
    {
        var commandId = CommandId.New();
        var messageId = commandId.ToString();
        long? sequenceBefore = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_modeAuthorityMutationGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sequenceBefore = _store.CurrentSequence(sessionId);
                var all = _store.ReadFrom(sessionId, 1);
                var created = all.Where(evt => evt.SessionId == sessionId && evt.RunId == runId
                        && _codecs.Decode(evt) is RunCreated runCreated && runCreated.RunId == runId)
                    .ToArray();
                if (created.Length != 1)
                    return PolicyTransitionResult(messageId, "The target Run is absent or ambiguous in this Session",
                        RuntimeCommandOutcome.Rejected());

                var own = EventsForRun(all, runId);
                if (own.Count == 0 || own.Any(evt => IsRunModeEvent(_codecs.Decode(evt))
                    && (evt.SessionId != sessionId || evt.RunId != runId || evt.CorrelationId != runId)))
                    return PolicyTransitionResult(messageId, "The target Run has inconsistent journal scope",
                        RuntimeCommandOutcome.Deferred("RunScopeUnavailable"));
                if (SessionRoutingAuthorization.Read(all, _codecs, sessionId) is null)
                    return PolicyTransitionResult(messageId, null, RuntimeCommandOutcome.Deferred("RoutingPolicyUnavailable"));

                var projection = RunProjection.Replay(sessionId, runId, _codecs, own);
                if (projection.IsTerminal())
                    return PolicyTransitionResult(messageId, "The target Run is terminal",
                        RuntimeCommandOutcome.Rejected());
                if (HasOpenModelStep(own))
                    return PolicyTransitionResult(messageId, null, RuntimeCommandOutcome.Deferred("ModelStepActive"));
                if (HasOpenToolCall(own))
                    return PolicyTransitionResult(messageId, null, RuntimeCommandOutcome.Deferred("ToolCallActive"));
                if (HasPendingModelRouteConsent(own))
                    return PolicyTransitionResult(messageId, null, RuntimeCommandOutcome.Deferred("ModelRouteConsent"));
                if (!IsBoundedPolicyReason(reason))
                    return PolicyTransitionResult(messageId, "Policy transition reason is invalid",
                        RuntimeCommandOutcome.Rejected());
                var durableReason = SecretRedactor.Shared.Redact(reason);

                var current = projection.ModeAuthority;
                if (current is null || current.Revision != expectedAuthorityRevision
                    || current.ProductEffort != ProductEffort.UltraCode || current.ModePinned
                    || !current.AutoModeSwitch || current.Authorization is not { } authorization
                    || !authorization.AllowedModes.Contains(targetMode)
                    || !current.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow))
                    return PolicyTransitionResult(messageId, "A current UltraCode authorization is required",
                        RuntimeCommandOutcome.Rejected());
                if (!Enum.IsDefined(targetMode))
                    return PolicyTransitionResult(messageId, "The target RunMode is unknown",
                        RuntimeCommandOutcome.Rejected());
                if (targetMode == current.Mode)
                    return PolicyTransitionResult(messageId, null, RuntimeCommandOutcome.NoOp());

                if (proposalEventId is not null)
                {
                    var source = all.SingleOrDefault(evt => evt.EventId == proposalEventId);
                    if (source is null || source.RunId != runId
                        || _codecs.Decode(source) is not RunModeProposed proposal
                        || proposal.AuthorityRevision != current.Revision || proposal.From != current.Mode
                        || proposal.To != targetMode
                        || !own.Any(evt => _codecs.Decode(evt) is TurnCompleted completed
                            && completed.TurnId == proposal.TurnId))
                        return PolicyTransitionResult(messageId, "Proposal is not a completed current-authority recommendation",
                            RuntimeCommandOutcome.Rejected());
                    _ = ModeProposalProjection.Replay(sessionId, runId, _codecs, all);
                }

                if (current.Mode == RunMode.Plan && targetMode == RunMode.Act)
                {
                    if (HasPendingInteraction(own))
                        return PolicyTransitionResult(messageId, null, RuntimeCommandOutcome.Deferred("PendingInteraction"));
                    if (authorization.PlanCoverage is not { } coverage)
                        return PolicyTransitionResult(messageId, null, RuntimeCommandOutcome.Deferred("PlanCoverageUnavailable"));
                    var plan = PlanProjection.Replay(_codecs, own).Latest();
                    if (plan is null || coverage.RunId != runId || coverage.PlanId != plan.Id
                        || coverage.PlanRevision != plan.Revision || coverage.RootTaskId != projection.RootTask)
                        return PolicyTransitionResult(messageId, null, RuntimeCommandOutcome.Deferred("PlanCoverageStale"));
                }

                // A mode decision cannot grant a route or consume a pending consent. Any following
                // provider selection still passes the ordinary SessionRoutingAuthorization gate.
                var nextRevision = checked(current.Revision + 1);
                var nextAuthorization = authorization with { AuthorityRevision = nextRevision };
                var next = current with
                {
                    Revision = nextRevision,
                    Mode = targetMode,
                    Authorization = nextAuthorization,
                };
                next.Validate();

                cancellationToken.ThrowIfCancellationRequested();
                using var command = CausationScope.Begin(new CommandCausation(commandId));
                using var execution = ExecutionScope.Begin(new ExecutionScopeState(RunId: runId));
                var batch = new DomainEventPayload[]
                {
                    new RunModeChanged(runId, current.Mode, targetMode, durableReason),
                    new RunModeTransitionAuthorized(runId, current.Mode, targetMode, durableReason,
                        "UltraCodePolicy", messageId, nextRevision, current.ObjectiveRevision,
                        current.ObjectiveDigest, current.PolicyRevision, authorization.AuthorizationId,
                        proposalEventId, current.Mode == RunMode.Plan && targetMode == RunMode.Act
                            ? authorization.PlanCoverage : null),
                    new RunModeAuthoritySelected(next, messageId, "UltraCodePolicy"),
                };
                new EventStream(_store, _codecs, sessionId).AppendBatch(batch, DurabilityClass.Barrier);
                var ack = CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                    sessionId, sequenceBefore.Value, commandId);
                return new InternalCommandResult(ack);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            var ack = sequenceBefore is null
                ? UnavailableCommandOutcome(messageId)
                : FailedDurableCommandAck(messageId, sessionId, sequenceBefore.Value,
                    "Could not persist the UltraCode policy transition", restoreRunIdentity: false);
            return new InternalCommandResult(ack, failure);
        }
    }

    private static InternalCommandResult PolicyTransitionResult(string commandMessageId, string? error,
        RuntimeCommandOutcome outcome) => new(new CommandAck(commandMessageId,
            outcome.Kind == RuntimeCommandOutcomeKind.Rejected ? "error" : "ok", error, outcome));

    private static bool IsBoundedPolicyReason(string reason)
    {
        // Reasons are observable diagnostic text, not an enum or classifier vocabulary.
        // Bound the serialized value without rejecting otherwise meaningful Unicode text.
        return !string.IsNullOrWhiteSpace(reason) && reason.Length <= 512;
    }

    private static bool IsRunModeEvent(DomainEventPayload payload) => payload is
        RunModeChanged or RunModeTransitionAuthorized or RunModeAuthoritySelected or RunModeAuthorityRevoked;

    private bool HasPendingModelRouteConsent(IReadOnlyList<DomainEvent> events)
    {
        var pending = new Dictionary<InteractionId, InteractionKind>();
        foreach (var evt in events)
        {
            var payload = _codecs.Decode(evt);
            switch (payload)
            {
                case InteractionRequested requested when evt.RunId is not null
                    && requested.Kind == InteractionKind.ModelRouteConsent:
                    pending[requested.InteractionId] = requested.Kind;
                    break;
                case InteractionResolved resolved:
                    pending.Remove(resolved.InteractionId);
                    break;
                case InteractionExpired expired:
                    pending.Remove(expired.InteractionId);
                    break;
            }
        }
        return pending.Values.Contains(InteractionKind.ModelRouteConsent);
    }

    private bool HasPendingInteraction(IReadOnlyList<DomainEvent> events)
    {
        var pending = new HashSet<InteractionId>();
        foreach (var evt in events)
        {
            switch (_codecs.Decode(evt))
            {
                case InteractionRequested requested: pending.Add(requested.InteractionId); break;
                case InteractionResolved resolved: pending.Remove(resolved.InteractionId); break;
                case InteractionExpired expired: pending.Remove(expired.InteractionId); break;
            }
        }
        return pending.Count != 0;
    }

    private CommandAck SelectRunMode(WireEnvelope command, Dictionary<string, string> fields)
    {
        lock (_modeAuthorityMutationGate) return SelectRunModeCore(command, fields);
    }

    private CommandAck SelectRunModeCore(WireEnvelope command, Dictionary<string, string> fields)
    {
        var messageId = command.MessageId;
        if (_lastSessionId is not { } session || _lastRunId is not { } run
            || !fields.TryGetValue("mode", out var rawMode) || !TryParseRunMode(rawMode, out var mode))
            return new CommandAck(messageId, "error", "run.mode.select requires an active Run and a valid mode",
                RuntimeCommandOutcome.Rejected());

        var commandId = new CommandId(Guid.Parse(messageId));
        long? before = null;
        try
        {
            var all = _store.ReadFrom(session, 1);
            var own = EventsForRun(all, run);
            var projection = RunProjection.Replay(session, run, _codecs, own);
            if (projection.IsTerminal())
                return new CommandAck(messageId, "error", "the active Run is terminal", RuntimeCommandOutcome.Rejected());
            if (HasOpenModelStep(own))
                return new CommandAck(messageId, "ok", null, RuntimeCommandOutcome.Deferred("ModelStepActive"));
            if (HasOpenToolCall(own))
                return new CommandAck(messageId, "ok", null, RuntimeCommandOutcome.Deferred("ToolCallActive"));

            var current = projection.ModeAuthority ?? RunModeAuthority.Legacy(
                own.Select(_codecs.Decode).OfType<RunCreated>().Single(created => created.RunId == run));
            var effort = fields.TryGetValue("effort", out var rawEffort) ? rawEffort.ToLowerInvariant() : "standard";
            if (effort is not ("standard" or "ultracode"))
                return new CommandAck(messageId, "error", "effort must be standard or ultracode",
                    RuntimeCommandOutcome.Rejected());

            var adaptive = false;
            if (fields.TryGetValue("adaptive", out var rawAdaptive))
            {
                if (rawAdaptive is not ("true" or "false"))
                    return new CommandAck(messageId, "error", "adaptive must be true or false",
                        RuntimeCommandOutcome.Rejected());
                adaptive = rawAdaptive == "true";
            }
            ModeSwitchAuthorization? authorization = null;
            var coverCurrentPlan = false;
            if (fields.TryGetValue("coverCurrentPlan", out var rawPlanCoverage))
            {
                if (rawPlanCoverage is not ("true" or "false") || !adaptive)
                    return new CommandAck(messageId, "error",
                        "coverCurrentPlan requires explicit adaptive UltraCode and a boolean value",
                        RuntimeCommandOutcome.Rejected());
                coverCurrentPlan = rawPlanCoverage == "true";
            }
            var productEffort = effort == "ultracode" ? ProductEffort.UltraCode : ProductEffort.Standard;
            if (adaptive)
            {
                if (productEffort != ProductEffort.UltraCode || !TryReadUltraCodeLimits(fields, out var limits, out var allowedModes))
                    return new CommandAck(messageId, "error",
                        "adaptive mode switching requires explicit UltraCode limits and allowedModes",
                        RuntimeCommandOutcome.Rejected());
                if (!allowedModes.Contains(mode))
                    return new CommandAck(messageId, "error", "allowedModes must include the selected mode",
                        RuntimeCommandOutcome.Rejected());
                var nextRevision = checked(current.Revision + (current.Authorization is null ? 1 : 2));
                ModeSwitchPlanCoverage? planCoverage = null;
                if (coverCurrentPlan)
                {
                    var plan = PlanProjection.Replay(_codecs, own).Latest();
                    if (plan is null || plan.RunId != run || projection.RootTask is not { } rootTask)
                        return new CommandAck(messageId, "ok", null,
                            RuntimeCommandOutcome.Deferred("PlanCoverageUnavailable"));
                    planCoverage = new ModeSwitchPlanCoverage(run, plan.Id, plan.Revision, rootTask);
                }
                authorization = new ModeSwitchAuthorization(Guid.NewGuid(), nextRevision,
                    current.ObjectiveRevision, current.ObjectiveDigest, current.PolicyRevision, allowedModes,
                    limits, DateTimeOffset.UtcNow, planCoverage);
                authorization.Validate();
            }
            else if (fields.ContainsKey("maxAgents") || fields.ContainsKey("maxDepth")
                || fields.ContainsKey("maxTurns") || fields.ContainsKey("maxToolCalls")
                || fields.ContainsKey("maxElapsedSeconds") || fields.ContainsKey("maxSpendUsd")
                || fields.ContainsKey("allowedModes"))
            {
                return new CommandAck(messageId, "error",
                    "mode-switch limits are accepted only with explicit adaptive UltraCode",
                    RuntimeCommandOutcome.Rejected());
            }

            var sequenceBefore = _store.CurrentSequence(session);
            before = sequenceBefore;
            var next = new RunModeAuthority(run,
                checked(current.Revision + (current.Authorization is null ? 1 : 2)), mode,
                current.Strategy, productEffort, !adaptive, adaptive, current.ObjectiveRevision,
                current.ObjectiveDigest, current.PolicyRevision, authorization);
            next.Validate();

            using var scope = ExecutionScope.Begin(new ExecutionScopeState(run, projection.RootTask,
                LastLaneId()));
            var payloads = new List<DomainEventPayload>();
            if (current.Authorization is { } oldAuthorization)
                payloads.Add(new RunModeAuthorityRevoked(run, current.Revision + 1,
                    oldAuthorization.AuthorizationId, "User selected a new mode authority", messageId, "User"));
            if (current.Mode != mode)
                payloads.Add(new RunModeChanged(run, current.Mode, mode, "ExplicitUserSelection"));
            payloads.Add(new RunModeTransitionAuthorized(run, current.Mode, mode,
                "ExplicitUserSelection", "User", messageId, next.Revision, next.ObjectiveRevision,
                next.ObjectiveDigest, next.PolicyRevision, authorization?.AuthorizationId));
            payloads.Add(new RunModeAuthoritySelected(next, messageId, "User"));
            new EventStream(_store, _codecs, session).AppendBatch(payloads, DurabilityClass.Barrier);
            return CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                session, sequenceBefore, commandId);
        }
        catch (Exception failure) when (failure is InvalidOperationException or ArgumentException
            or OverflowException or IOException or UnauthorizedAccessException or SqliteException)
        {
            return before is null ? UnavailableCommandOutcome(messageId)
                : FailedDurableCommandAck(messageId, session, before.Value, "Could not persist Run mode authority");
        }
    }

    private CommandAck RevokeRunModeAuthority(WireEnvelope command, Dictionary<string, string> fields)
    {
        lock (_modeAuthorityMutationGate) return RevokeRunModeAuthorityCore(command, fields);
    }

    private CommandAck RevokeRunModeAuthorityCore(WireEnvelope command, Dictionary<string, string> fields)
    {
        var messageId = command.MessageId;
        if (_lastSessionId is not { } session || _lastRunId is not { } run)
            return new CommandAck(messageId, "error", "run.mode.revoke requires an active Run",
                RuntimeCommandOutcome.Rejected());
        long? before = null;
        try
        {
            var own = EventsForRun(_store.ReadFrom(session, 1), run);
            var projection = RunProjection.Replay(session, run, _codecs, own);
            var current = projection.ModeAuthority;
            if (projection.IsTerminal())
                return new CommandAck(messageId, "error", "the active Run is terminal", RuntimeCommandOutcome.Rejected());
            if (current?.Authorization is not { } authorization || !current.AutoModeSwitch)
                return new CommandAck(messageId, "ok", null, RuntimeCommandOutcome.NoOp());
            if (HasOpenModelStep(own))
                return new CommandAck(messageId, "ok", null, RuntimeCommandOutcome.Deferred("ModelStepActive"));
            if (HasOpenToolCall(own))
                return new CommandAck(messageId, "ok", null, RuntimeCommandOutcome.Deferred("ToolCallActive"));
            var commandId = new CommandId(Guid.Parse(messageId));
            var sequenceBefore = _store.CurrentSequence(session);
            before = sequenceBefore;
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(run, projection.RootTask, LastLaneId()));
            new EventStream(_store, _codecs, session).Append(new RunModeAuthorityRevoked(run,
                checked(current.Revision + 1), authorization.AuthorizationId, "User revoked UltraCode",
                messageId, "User"), DurabilityClass.Barrier);
            return CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                session, sequenceBefore, commandId);
        }
        catch (Exception failure) when (failure is InvalidOperationException or ArgumentException
            or OverflowException or IOException or UnauthorizedAccessException or SqliteException)
        {
            return before is null ? UnavailableCommandOutcome(messageId)
                : FailedDurableCommandAck(messageId, session, before.Value, "Could not revoke mode authority");
        }
    }

    private CommandAck SetDefaultReasoning(WireEnvelope command, Dictionary<string, string> fields)
    {
        if (_userDatabasePath is null || !TryReadExpectedRevision(fields, out var expectedRevision)
            || !TryReadReasoningRequest(fields, out var request))
            return new CommandAck(command.MessageId, "error",
                "reasoning.default.set requires a User database, explicit kind (off is allowed), and expectedRevision",
                RuntimeCommandOutcome.Rejected());
        try
        {
            using var store = new ReasoningPreferenceStore(_userDatabasePath);
            store.Set(request, expectedRevision);
            return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
        }
        catch (ReasoningPreferenceConflictException)
        {
            return new CommandAck(command.MessageId, "error", "User reasoning preference revision conflict",
                RuntimeCommandOutcome.Rejected());
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or ArgumentException or UnauthorizedAccessException or SqliteException or OverflowException)
        {
            return new CommandAck(command.MessageId, "error", "Could not persist the User reasoning preference",
                RuntimeCommandOutcome.Rejected());
        }
    }

    private CommandAck RevokeDefaultReasoning(WireEnvelope command, Dictionary<string, string> fields)
    {
        if (_userDatabasePath is null || !TryReadExpectedRevision(fields, out var expectedRevision))
            return new CommandAck(command.MessageId, "error",
                "reasoning.default.revoke requires a User database and expectedRevision",
                RuntimeCommandOutcome.Rejected());
        try
        {
            using var store = new ReasoningPreferenceStore(_userDatabasePath);
            store.Reset(expectedRevision);
            return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted());
        }
        catch (ReasoningPreferenceConflictException)
        {
            return new CommandAck(command.MessageId, "error", "User reasoning preference revision conflict",
                RuntimeCommandOutcome.Rejected());
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or ArgumentException or UnauthorizedAccessException or SqliteException or OverflowException)
        {
            return new CommandAck(command.MessageId, "error", "Could not revoke the User reasoning preference",
                RuntimeCommandOutcome.Rejected());
        }
    }

    private CommandAck SelectRunReasoning(WireEnvelope command, Dictionary<string, string> fields)
    {
        if (_lastSessionId is not { } session || _lastRunId is not { } run
            || !TryReadExpectedRevision(fields, out var expectedRevision)
            || !TryReadReasoningRequest(fields, out var request))
            return new CommandAck(command.MessageId, "error",
                "run.reasoning.select requires an active Run, explicit kind, and expectedRevision",
                RuntimeCommandOutcome.Rejected());

        long? before = null;
        try
        {
            var own = EventsForRun(_store.ReadFrom(session, 1), run);
            var projection = RunProjection.Replay(session, run, _codecs, own);
            if (projection.IsTerminal())
                return new CommandAck(command.MessageId, "error", "the active Run is terminal",
                    RuntimeCommandOutcome.Rejected());
            if (HasOpenModelStep(own))
                return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Deferred("ModelStepActive"));
            if (HasOpenToolCall(own))
                return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Deferred("ToolCallActive"));
            var currentRevision = projection.ReasoningSelection?.Revision ?? 0;
            if (expectedRevision != currentRevision)
                return new CommandAck(command.MessageId, "error", "Run reasoning preference revision conflict",
                    RuntimeCommandOutcome.Rejected());

            var sequenceBefore = _store.CurrentSequence(session);
            before = sequenceBefore;
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(run, projection.RootTask, LastLaneId()));
            new EventStream(_store, _codecs, session).Append(new RunReasoningPreferenceSelected(run,
                checked(currentRevision + 1), request, "User", command.MessageId), DurabilityClass.Barrier);
            return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                session, sequenceBefore, new CommandId(Guid.Parse(command.MessageId)));
        }
        catch (Exception failure) when (failure is InvalidOperationException or ArgumentException
            or OverflowException or IOException or UnauthorizedAccessException or SqliteException)
        {
            return before is null ? UnavailableCommandOutcome(command.MessageId)
                : FailedDurableCommandAck(command.MessageId, session, before.Value,
                    "Could not persist Run reasoning preference");
        }
    }

    private CommandAck RevokeRunReasoning(WireEnvelope command, Dictionary<string, string> fields)
    {
        if (_lastSessionId is not { } session || _lastRunId is not { } run
            || !TryReadExpectedRevision(fields, out var expectedRevision))
            return new CommandAck(command.MessageId, "error",
                "run.reasoning.revoke requires an active Run and expectedRevision",
                RuntimeCommandOutcome.Rejected());

        long? before = null;
        try
        {
            var own = EventsForRun(_store.ReadFrom(session, 1), run);
            var projection = RunProjection.Replay(session, run, _codecs, own);
            if (projection.IsTerminal())
                return new CommandAck(command.MessageId, "error", "the active Run is terminal",
                    RuntimeCommandOutcome.Rejected());
            var current = projection.ReasoningSelection;
            var revision = current?.Revision ?? 0;
            if (expectedRevision != revision)
                return new CommandAck(command.MessageId, "error", "Run reasoning preference revision conflict",
                    RuntimeCommandOutcome.Rejected());
            if (current is null || !current.HasSelection || current.Source != "User")
                return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.NoOp());
            if (HasOpenModelStep(own))
                return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Deferred("ModelStepActive"));
            if (HasOpenToolCall(own))
                return new CommandAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Deferred("ToolCallActive"));

            var sequenceBefore = _store.CurrentSequence(session);
            before = sequenceBefore;
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(run, projection.RootTask, LastLaneId()));
            new EventStream(_store, _codecs, session).Append(new RunReasoningPreferenceRevoked(run,
                checked(revision + 1), command.MessageId), DurabilityClass.Barrier);
            return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                session, sequenceBefore, new CommandId(Guid.Parse(command.MessageId)));
        }
        catch (Exception failure) when (failure is InvalidOperationException or ArgumentException
            or OverflowException or IOException or UnauthorizedAccessException or SqliteException)
        {
            return before is null ? UnavailableCommandOutcome(command.MessageId)
                : FailedDurableCommandAck(command.MessageId, session, before.Value,
                    "Could not revoke Run reasoning preference");
        }
    }

    private static bool TryReadExpectedRevision(Dictionary<string, string> fields, out long revision)
    {
        revision = 0;
        return fields.TryGetValue("expectedRevision", out var raw)
            && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out revision)
            && revision >= 0;
    }

    private static bool TryReadReasoningRequest(Dictionary<string, string> fields, out ReasoningRequest? request)
    {
        request = null;
        if (!fields.TryGetValue("kind", out var kind) || string.IsNullOrWhiteSpace(kind)) return false;
        if (kind == "off") return !fields.ContainsKey("budgetTokens");
        if (fields.TryGetValue("budgetTokens", out var rawBudget))
        {
            if (!int.TryParse(rawBudget, NumberStyles.None, CultureInfo.InvariantCulture, out var budget)
                || budget < 1024 || kind != "budget")
                return false;
            request = new ReasoningRequest(kind, budget);
        }
        else
        {
            if (kind == "budget") return false;
            request = new ReasoningRequest(kind, null);
        }
        return request.Kind == kind;
    }

    private static string ReasoningRequestJson(ReasoningRequest? request) => request is null
        ? "null"
        : "{" + JsonObj.Field("kind", request.Kind) + ",\"budgetTokens\":"
            + (request.BudgetTokens?.ToString(CultureInfo.InvariantCulture) ?? "null") + "}";

    private static bool TryReadUltraCodeLimits(Dictionary<string, string> fields,
        out ModeSwitchLimits limits, out IReadOnlyList<RunMode> allowedModes)
    {
        limits = null!;
        allowedModes = Array.Empty<RunMode>();
        bool Int(string key, out int value)
        {
            value = 0;
            return fields.TryGetValue(key, out var raw)
                && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
        bool Long(string key, out long value)
        {
            value = 0;
            return fields.TryGetValue(key, out var raw)
                && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
        if (!Int("maxAgents", out var agents) || !Int("maxDepth", out var depth)
            || !Int("maxTurns", out var turns) || !Int("maxToolCalls", out var tools)
            || !Long("maxElapsedSeconds", out var seconds)
            || !fields.TryGetValue("maxSpendUsd", out var spendText)
            || !decimal.TryParse(spendText, NumberStyles.Number, CultureInfo.InvariantCulture, out var spend)
            || !fields.TryGetValue("allowedModes", out var modeText)) return false;
        var modes = new List<RunMode>();
        var modeParts = modeText.Split(',');
        if (modeParts.Length == 0 || modeParts.Any(string.IsNullOrWhiteSpace)) return false;
        foreach (var part in modeParts.Select(value => value.Trim()))
        {
            if (!TryParseRunMode(part, out var parsed) || modes.Contains(parsed)) return false;
            modes.Add(parsed);
        }
        if (modes.Count == 0) return false;
        limits = new ModeSwitchLimits(agents, depth, turns, tools, seconds, spend);
        try { limits.Validate(); }
        catch (ArgumentOutOfRangeException) { return false; }
        allowedModes = modes.AsReadOnly();
        return true;
    }

    private bool HasOpenModelStep(IReadOnlyList<DomainEvent> events)
    {
        var starts = new HashSet<(TurnId Turn, int Step)>();
        foreach (var evt in events)
        {
            switch (_codecs.Decode(evt))
            {
                case ModelStepStarted started:
                    // Duplicate starts are malformed journal state; treating one as open fails closed.
                    if (!starts.Add((started.TurnId, started.StepIndex))) return true;
                    break;
                case ModelStepCompleted completed: starts.Remove((completed.TurnId, completed.StepIndex)); break;
                case ModelStepNotDispatched notDispatched:
                    starts.Remove((notDispatched.TurnId, notDispatched.StepIndex));
                    break;
            }
        }
        return starts.Count > 0;
    }

    private bool HasOpenModelStep(IReadOnlyList<DomainEvent> events, LaneId lane) =>
        HasOpenModelStep(events.Where(evt => evt.LaneId == lane).ToArray());

    /// <summary>
    /// A completed model step does not mean its tool loop is finished: ToolCallStarted is
    /// deliberately journaled after ModelStepCompleted. Do not apply a mode downgrade/revocation
    /// while any tool lifecycle is still nonterminal, including an uncertain effect awaiting
    /// reconciliation.
    /// </summary>
    private bool HasOpenToolCall(IReadOnlyList<DomainEvent> events)
    {
        var calls = new Dictionary<ToolCallId, ToolCallState>();
        foreach (var evt in events)
        {
            var payload = _codecs.Decode(evt);
            if (payload is ToolCallRequested requested)
            {
                if (!calls.TryAdd(requested.ToolCallId, ToolCallState.Requested)) return true;
                continue;
            }

            var id = payload switch
            {
                ToolCallPrepared value => value.ToolCallId,
                ToolCallRejected value => value.ToolCallId,
                PermissionEvaluated value => value.ToolCallId,
                PermissionRequested value => value.ToolCallId,
                PermissionGranted value => value.ToolCallId,
                PermissionDenied value => value.ToolCallId,
                ToolCallAuthorized value => value.ToolCallId,
                ToolCallStarted value => value.ToolCallId,
                ToolCallSucceeded value => value.ToolCallId,
                ToolCallFailed value => value.ToolCallId,
                ToolCallEffectUnknown value => value.ToolCallId,
                ToolCallReconciled value => value.ToolCallId,
                ToolCallCancelled value => value.ToolCallId,
                _ => (ToolCallId?)null,
            };
            if (id is not { } callId) continue;
            if (!calls.TryGetValue(callId, out var state)) return true;
            try { calls[callId] = StateMachines.ApplyToolCall(state, payload); }
            catch (InvalidStateTransitionException) { return true; }
        }

        return calls.Values.Any(state => state is not (ToolCallState.Succeeded or ToolCallState.Failed
            or ToolCallState.Rejected or ToolCallState.Cancelled or ToolCallState.Reconciled));
    }

    private bool HasOpenToolCall(IReadOnlyList<DomainEvent> events, LaneId lane) =>
        HasOpenToolCall(events.Where(evt => evt.LaneId == lane).ToArray());

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

        // `explore.start` is the explicit PLAN/exploration flow; the product default applies to
        // ordinary `session.input`, not to this command's declared semantics.
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
        string workspacePath;
        try
        {
            workspacePath = workspace is null || workspace!.Length == 0
                ? Path.GetFullPath(".")
                : Path.GetFullPath(workspace!);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
            or IOException or UnauthorizedAccessException)
        {
            // Path admission precedes all journal writes. Do not leak the supplied path
            // or mistake an invalid request for an indeterminate committed command.
            return new CommandAck(command.MessageId, "error", "Workspace inválido para act.",
                RuntimeCommandOutcome.Rejected());
        }
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
        try
        {
            var reasoningPreference = ReadReasoningPreference();
            var physicalWorkspaceRoot = ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspacePath);
            // Establish the existing workspace marker before committing its reference. The
            // journal initialization is all-or-nothing; no incomplete Run becomes selectable.
            var durableIdentity = WorkspaceRootIdentity.Establish(workspacePath);
            var creationEvents = new List<DomainEventPayload>
            {
                new SessionCreated(sessionId,
                    WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(physicalWorkspaceRoot)).ToString(),
                    workspacePath, ProfileId.New(), now),
                new SessionRoutingPolicySet(sessionId, _newSessionRoutingPolicy),
                new WorkspaceRootEstablished(sessionId, workspacePath, now, durableIdentity),
                new RunCreated(runId, sessionId, objective, mode,
                    ExecutionStrategy.Direct, FailurePolicy.BlockDependents, budget, taskId, now),
                new RunModeAuthoritySelected(new RunModeAuthority(runId, 1, mode,
                    ExecutionStrategy.Direct, ProductEffort.Standard, false, false, 1,
                    RunModeAuthority.ObjectiveDigestFor(objective), 1, null), command.MessageId, "RunCreated"),
            };
            // Capture only an explicit User choice. Absence remains absence, so a new Run
            // does not manufacture a reasoning selection or alter legacy event counts.
            if (reasoningPreference.HasSelection)
                creationEvents.Add(new RunReasoningPreferenceSelected(runId, 1, reasoningPreference.Request,
                    "UserDefault", "RunCreated", reasoningPreference.Revision));
            creationEvents.AddRange(new DomainEventPayload[]
            {
                new RunStarted(runId),
                new TaskCreated(taskId, runId, objective, Array.Empty<TaskDependency>(), budget),
                new TaskReady(taskId),
                new LaneCreated(laneId, taskId, _agentProfiles.DefaultProfile?.Id ?? ProfileId.New(),
                    _agentProfiles.DefaultProfile?.Revision, _agentProfiles.DefaultProfile is { } rootProfile
                        ? AgentProfileFingerprint.Hash(rootProfile) : null),
                new LaneStarted(laneId),
                new TaskStarted(taskId, laneId),
                new PlanCreated(planId, runId, PlanItemId.New(), objective),
            });
            stream.AppendBatch(creationEvents, DurabilityClass.Barrier);

            _lastSessionId = sessionId;
            _lastRunId = runId;
            _lastSnapshot = null;
            _lastWorkingStateText = "";
            _lastSnapshot = MaterializeFromJournal(sessionId, runId);
            SaveLastSession();
            return CommandOutcomeAck(command.MessageId, "ok", null, RuntimeCommandOutcome.Accepted(), sessionId, 0,
                new CommandId(Guid.Parse(command.MessageId)));
        }
        catch (Exception)
        {
            return FailedDurableCommandAck(command, sessionId, 0,
                "No se pudo completar la creación del Run.", newSession: true);
        }
    }

    /// <summary>Store del servidor (para el Turn de Explorer, que persiste en el mismo journal).</summary>
    internal IEventStore AcquireStore() => _store;

    /// <summary>Registro de codecs del servidor.</summary>
    internal IEventCodecRegistry AcquireCodecs() => _codecs;

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

    /// <summary>
    /// Fingerprint de ejecución del último Turn del Run activo (ADR-0017): qué configuración efectiva recibió.
    /// Solo identidades y hashes, nunca el contenido de los componentes. <c>{"fingerprint":null}</c> si ningún Turn
    /// lo registró.
    /// </summary>
    private string ReadLastTurnFingerprint()
    {
        if (_lastSessionId is null || _lastRunId is null) return "{\"fingerprint\":null}";
        TurnStarted? latest = null;
        foreach (var evt in EventsForRun(_store.ReadFrom(_lastSessionId, 1), _lastRunId))
            if (_codecs.Decode(evt) is TurnStarted { Fingerprint: not null } started) latest = started;
        if (latest?.Fingerprint is not { } fingerprint) return "{\"fingerprint\":null}";
        var components = fingerprint.Components.Select(component => "{" + JsonObj.Field("name", component.Name)
            + "," + JsonObj.Field("version", component.Version) + "," + JsonObj.Field("hash", component.Hash.ToString()) + "}");
        return "{\"fingerprint\":{" + JsonObj.Field("turnId", latest.TurnId.ToString()) + ","
            + JsonObj.Field("modelKey", fingerprint.ModelKey) + "," + JsonObj.Field("build", fingerprint.Build) + ","
            + JsonObj.Field("harnessPolicy", fingerprint.HarnessPolicyHash) + ","
            + JsonObj.Field("toolkit", fingerprint.ToolkitHash) + ","
            + JsonObj.Field("tokenizer", fingerprint.TokenizerHash) + ","
            + JsonObj.Field("contextPolicy", fingerprint.ContextPolicyHash) + ","
            + JsonObj.Field("overrides", fingerprint.OverridesHash) + ","
            + JsonObj.Field("modelPolicy", fingerprint.ModelPolicyHash) + ",\"components\":["
            + string.Join(",", components) + "]}}";
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
        if (name == "sidebarSettings") return new(name, SidebarPreferencesJson.Encode(SidebarSettings().Read()));
        if (name == "sessionSidebar")
            return new SessionQueryResult(name, _lastSessionId is null ? "null"
                : SidebarJson.Encode(SessionSidebarReader.Read(_store, _codecs, _lastSessionId, _lastRunId, _recoveryProblem is not null)));
        if (name == "changedFiles")
            return new SessionQueryResult(name, _lastSessionId is null ? "null"
                : FilesJson.Encode(ChangedFilesReader.Read(_store, _codecs, _lastSessionId, _artifacts)));
        if (name.StartsWith("diff.open:", StringComparison.Ordinal))
            return new SessionQueryResult("diff.open", _lastSessionId is null ? "null"
                : FilesJson.Encode(ChangedFilesReader.ReadDiff(_store, _codecs, _lastSessionId, name[10..], _artifacts)));
        if (name == "agents")
            return new SessionQueryResult(name, _lastSessionId is null ? "null"
                : AgentsJson.Encode(AgentLaneReader.Read(_store, _codecs, _lastSessionId, _lastRunId,
                    _artifacts, Observability.Activities(_lastSessionId))));
        if (name == "sessionObservability" || name.StartsWith("sessionObservability:", StringComparison.Ordinal))
        {
            if (_lastSessionId is null) return new SessionQueryResult("sessionObservability", "null");
            var after = name.Contains(':') && long.TryParse(name[(name.IndexOf(':') + 1)..], out var cursor) ? cursor : 0;
            return new SessionQueryResult("sessionObservability", ObservabilityJson.Encode(Observability.Snapshot(_lastSessionId, after)));
        }
        if (name == "commands")
        {
            var catalog = _commandService.Catalog(CommandContextForCurrentRun());
            var names = new[] { "mode", "ultracode", "reasoning" }.Concat(catalog.Commands.Select(command => command.Name));
            var json = "{\"commands\":[" + string.Join(",", names.Select(value => "\"" + JsonObj.Escape(value) + "\"")) + "],"
                + "\"catalog\":" + CommandCatalogJson.Encode(catalog) + "}";
            return new SessionQueryResult("commands", json);
        }
        if (name == "modePreference")
        {
            var preference = ReadModePreference();
            return new SessionQueryResult(name, "{" + JsonObj.Field("mode", preference.Mode.ToString().ToLowerInvariant())
                + ",\"revision\":" + preference.Revision.ToString(CultureInfo.InvariantCulture) + "}");
        }
        if (name == "modeAuthority")
        {
            var authority = CurrentModeAuthority();
            return new SessionQueryResult(name, authority is null ? "null" : ModeAuthorityJson(authority, DateTimeOffset.UtcNow));
        }
        if (name == "reasoningPreference")
        {
            var preference = ReadReasoningPreference();
            return new SessionQueryResult(name, "{\"hasSelection\":" + (preference.HasSelection ? "true" : "false")
                + ",\"request\":" + ReasoningRequestJson(preference.Request)
                + ",\"revision\":" + preference.Revision.ToString(CultureInfo.InvariantCulture) + "}");
        }
        if (name == "runReasoningPreference")
        {
            var selection = CurrentRunReasoningSelection();
            return new SessionQueryResult(name, selection is null ? "null"
                : "{\"revision\":" + selection.Revision.ToString(CultureInfo.InvariantCulture)
                    + ",\"hasSelection\":" + (selection.HasSelection ? "true" : "false")
                    + ",\"request\":" + ReasoningRequestJson(selection.Request)
                    + "," + JsonObj.Field("source", selection.Source)
                    + ",\"userPreferenceRevision\":" + (selection.UserPreferenceRevision?.ToString(CultureInfo.InvariantCulture) ?? "null")
                    + ",\"hasCapturedUserDefault\":" + (selection.HasCapturedUserDefault ? "true" : "false")
                    + ",\"capturedUserDefault\":" + ReasoningRequestJson(selection.CapturedUserDefault)
                    + ",\"capturedUserPreferenceRevision\":" + (selection.CapturedUserPreferenceRevision?.ToString(CultureInfo.InvariantCulture) ?? "null")
                    + "}");
        }
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

        if (name == "sessionIdentity")
            return new SessionQueryResult("sessionIdentity", "{" + JsonObj.Field("sessionId", _lastSessionId?.ToString() ?? "") + "}");

        if (name == "commandOutcome")
        {
            if (_lastPromptExpanded is { } prompt) return new SessionQueryResult("commandOutcome", "{\"outcome\":{"
                + JsonObj.Field("kind", "promptExpanded") + "," + JsonObj.Field("text", prompt.Text) + ","
                + JsonObj.Field("origin", prompt.Origin) + "}}");
            if (_lastWorkflowRequested is { } workflow) return new SessionQueryResult("commandOutcome", "{\"outcome\":{"
                + JsonObj.Field("kind", "workflowRequested") + "," + JsonObj.Field("workflowId", workflow.Workflow.Id) + ","
                + JsonObj.Field("version", workflow.Workflow.Version) + ","
                + JsonObj.Field("commandId", _lastWorkflowInvocation?.CommandId ?? "") + ","
                + JsonObj.FieldRaw("arguments", "[" + string.Join(",", workflow.Arguments.Select(value => "\"" + JsonObj.Escape(value) + "\"")) + "]") + "}}");
            return new SessionQueryResult("commandOutcome", "{\"outcome\":null}");
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

        if (name == "turnFingerprint")
            return new SessionQueryResult("turnFingerprint", ReadLastTurnFingerprint());

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
        // Keep the attempted session identity even when Execute throws before returning.
        // Never attribute a partial new simulation to the previously selected session.
        var attemptSession = SessionId.New();
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
            var result = _engine.Execute(scenario, CancellationToken.None, attemptSession);
            _lastSessionId = result.SessionId;
            _lastRunId = result.RunId;
            _lastSnapshot = null;
            _lastWorkingStateText = "";
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
            return FailedDurableCommandAck(command, attemptSession, 0, detail + " :: " + stack, newSession: true);
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
    private CommandAck RunControl(WireEnvelope command, string commandName, Dictionary<string, string> fields,
        bool trustedUserAction)
    {
        var control = new RunControlService(_store, _codecs, (day, baseline) =>
            UserDailyBudgetContinuation.Limit(_userSpendReader, _codecs, day, baseline),
            rootProfile: _agentProfiles.DefaultProfile?.Id,
            rootProfileRevision: _agentProfiles.DefaultProfile?.Revision,
            rootProfileHash: _agentProfiles.DefaultProfile is { } rootProfile ? AgentProfileFingerprint.Hash(rootProfile) : null,
            initialReasoningSelection: runId =>
            {
                var preference = ReadReasoningPreference();
                return preference.HasSelection
                    ? new RunReasoningPreferenceSelected(runId, 1, preference.Request,
                        "UserDefault", "RunCreated", preference.Revision)
                    : null;
            });
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
                    // Explicit intent only: invalid kinds never become a FollowUp or a new Run.
                    if (fields.TryGetValue("kind", out var kind) && kind != "followup")
                    {
                        if (kind != "steering") throw new FormatException("kind de session.input inválido");
                        return ReceiveSteeringCommand(command, fields, commandId);
                    }
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
                    var session = outcomeSession ?? SessionId.New();
                    if (outcomeSession is null)
                    {
                        outcomeSession = session; // keep the attempted identity even if initialization fails
                        StartSession(session);
                    }
                    var mode = ReadModePreference().Mode;
                    if (trustedUserAction && fields.TryGetValue("mode", out var requestedMode))
                    {
                        if (!TryParseRunMode(requestedMode, out mode))
                            throw new FormatException("mode de session.input inválido");
                    }
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
                        if (questionnaire.SourceScope(stream, interaction)?.RunId is { } activeRun
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
                        lock (_modeAuthorityMutationGate)
                            control.Respond(session, interaction,
                                fields.TryGetValue("optionId", out var o) ? o : "", command.MessageId);
                        AuditEffectResolutions(session, sequenceBeforeResponse, interaction);
                        if (control.UnreconciledEffects(session).Count == 0) _recoveryProblem = null;
                    }
                    break;
                }
            }

            SaveLastSession();
            if (explicitOutcome)
            {
                if (_artifacts is not null && outcomeSession is { } summarySession)
                    new RunSummaryService(_store, _codecs, _artifacts, OmniCliRuntime.RedactSensitive).EnsureRecorded(summarySession);
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
        catch (Exception)
        {
            return outcomeSession is null
                ? UnavailableCommandOutcome(command.MessageId)
                : FailedDurableCommandAck(command, outcomeSession, outcomeSequenceBefore,
                    "No se pudo completar el command de control.", newSession: _lastSessionId != outcomeSession);
        }
    }

    private CommandAck CommandOutcomeAck(string commandMessageId, string status, string? error,
        RuntimeCommandOutcome outcome, SessionId? session, long sequenceBefore, CommandId commandId)
    {
        if (error is not null) error = SafeCommandError(error);
        if (session is null)
        {
            return new CommandAck(commandMessageId, status, error, outcome);
        }

        long[] causedSequences;
        try
        {
            causedSequences = CommandResultEvents(session, sequenceBefore, commandId)
                .Select(evt => evt.Sequence)
                .ToArray();
        }
        catch (Exception)
        {
            return UnavailableCommandOutcome(commandMessageId);
        }
        return causedSequences.Length == 0
            ? new CommandAck(commandMessageId, status, error, outcome)
            : new CommandAck(commandMessageId, status, error, outcome,
                causedSequences.Min(), causedSequences.Max());
    }

    // Deferred confirmation is not proof of rejection or zero effects. Never expose the
    // journal exception, invent a range, or blindly retry a possibly persisted command.
    private static CommandAck UnavailableCommandOutcome(string commandMessageId) =>
        new(commandMessageId, "error", "No se pudo confirmar el resultado durable del command.",
            RuntimeCommandOutcome.Deferred("JournalOutcomeUnavailable"));

    private DomainEvent[] CommandResultEvents(SessionId session, long sequenceBefore, CommandId commandId) =>
        SelectCommandResultEvents(_store.ReadFrom(session, sequenceBefore + 1), session, sequenceBefore, commandId);

    internal static DomainEvent[] SelectCommandResultEvents(IReadOnlyList<DomainEvent> events,
        SessionId session, long sequenceBefore, CommandId commandId)
    {
        var causedIds = new HashSet<EventId>();
        var result = new List<DomainEvent>();
        // Causal parents precede their children in the session journal. Only roots appended
        // after this invocation and their descendants qualify; chronological adjacency,
        // Run identity, and causes from an earlier invocation are not sufficient.
        foreach (var evt in events.OrderBy(evt => evt.Sequence))
        {
            if (evt.SessionId != session || evt.Sequence <= sequenceBefore) continue;
            if (evt.Causation is CommandCausation command && command.CommandId == commandId
                || evt.Causation is EventCausation parent && causedIds.Contains(parent.EventId))
            {
                causedIds.Add(evt.EventId);
                result.Add(evt);
            }
        }
        return result.ToArray();
    }

    /// <summary>Authoritative admission only; consumption belongs to a ModelStep boundary.</summary>
    private CommandAck ReceiveSteeringCommand(WireEnvelope command, Dictionary<string, string> fields,
        CommandId commandId)
    {
        Guid RequiredId(string name) => fields.TryGetValue(name, out var value) && Guid.TryParse(value, out var id)
            ? id : throw new FormatException(name + " inválido o ausente");
        var session = new SessionId(RequiredId("sessionId"));
        var run = new RunId(RequiredId("runId"));
        var lane = new LaneId(RequiredId("laneId"));
        var turn = new TurnId(RequiredId("turnId"));
        if (_lastSessionId != session) throw new FormatException("steering pertenece a otra sesión");
        var text = fields.TryGetValue("text", out var input) ? input : "";
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("falta 'text'");
        var steeringId = fields.ContainsKey("steeringId")
            ? new SteeringId(RequiredId("steeringId")) : new SteeringId(commandId.Value);
        var events = _store.ReadFrom(session, 1);
        var created = events.Select(_codecs.Decode).OfType<RunCreated>()
            .FirstOrDefault(item => item.RunId == run && item.SessionId == session);
        var requestedLane = LaneProjection.Replay(_codecs, events).Get(lane);
        if (created is null || requestedLane is null
            || !events.Select(_codecs.Decode).OfType<TaskCreated>()
                .Any(task => task.TaskId == requestedLane.TaskId && task.RunId == run))
            throw new FormatException("steering Run/Lane fuera de scope");
        var before = _store.CurrentSequence(session);
        using var execution = ExecutionScope.Begin(new ExecutionScopeState(run, requestedLane.TaskId, lane, turn));
        var accepted = SteeringQueue.TryReceive(_store, _codecs, session, steeringId, run, lane, turn,
            text, fields.TryGetValue("origin", out var origin) ? origin : null);
        var appended = accepted && _store.ReadFrom(session, before + 1).Any(evt =>
            evt.Causation is CommandCausation cause && cause.CommandId == commandId
            && _codecs.Decode(evt) is TurnSteeringReceived received && received.SteeringId == steeringId);
        return CommandOutcomeAck(command.MessageId, accepted ? "ok" : "error",
            accepted ? null : "steering rechazado: identidad en conflicto o Turn no abierto",
            !accepted ? RuntimeCommandOutcome.Rejected()
                : appended ? RuntimeCommandOutcome.Accepted() : RuntimeCommandOutcome.NoOp(),
            session, before, commandId);
    }

    /// <summary>Queues one CLI FollowUp intent as an internal Host command.</summary>
    internal (bool Queued, CommandAck Ack, Exception? Failure) QueueFollowUpPromptCommand(SessionId sessionId, RunId runId,
        LaneId laneId, string prompt, string? origin)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var ambientCommand = CausationScope.Current as CommandCausation;
        var commandId = ambientCommand?.CommandId ?? CommandId.New();
        var commandMessageId = commandId.Value.ToString();
        long? sequenceBefore = null;
        try
        {
            var events = _store.ReadFrom(sessionId, 1);
            var decoded = events.Select(evt => (Event: evt, Payload: _codecs.Decode(evt))).ToArray();
            var created = decoded.Select(pair => pair.Payload).OfType<RunCreated>()
                .FirstOrDefault(item => item.RunId == runId && item.SessionId == sessionId);
            if (created is null)
            {
                return (false, new CommandAck(commandMessageId, "error",
                    "the requested Run does not belong to the requested Session",
                    RuntimeCommandOutcome.Rejected()), null);
            }

            // Filter by envelope Run correlation rather than the current-run interval: one Session
            // may contain lifecycle events for an earlier Run after another Run was created.
            var own = events.Where(evt => evt.CorrelationId == runId || evt.RunId == runId).ToArray();
            var runProjection = RunProjection.Replay(sessionId, runId, _codecs, own);
            if (runProjection.IsTerminal())
            {
                return (false, new CommandAck(commandMessageId, "error", "the requested Run is terminal",
                    RuntimeCommandOutcome.Rejected()), null);
            }

            var lane = decoded.FirstOrDefault(pair => pair.Event.RunId == runId
                && pair.Payload is LaneCreated laneCreated && laneCreated.LaneId == laneId);
            if (lane.Payload is not LaneCreated requestedLane
                || !decoded.Any(pair => pair.Event.RunId == runId
                    && pair.Payload is TaskCreated taskCreated && taskCreated.TaskId == requestedLane.TaskId
                    && taskCreated.RunId == runId))
            {
                return (false, new CommandAck(commandMessageId, "error",
                    "the requested Lane does not belong to the requested Run",
                    RuntimeCommandOutcome.Rejected()), null);
            }

            sequenceBefore = _store.CurrentSequence(sessionId);
            using var internalCommand = ambientCommand is null
                ? CausationScope.Begin(new CommandCausation(commandId)) : null;
            // Queue writes belong to the explicit Run/Lane only; never inherit a caller's unrelated
            // Task/Lane/Turn ambient attribution.
            using var execution = ExecutionScope.Begin(new ExecutionScopeState(runId, requestedLane.TaskId, laneId));
            var queued = FollowUpQueue.TryQueue(_store, _codecs, sessionId, runId, laneId, prompt, origin);
            var outcome = queued ? RuntimeCommandOutcome.Accepted() : RuntimeCommandOutcome.NoOp();
            return (queued, CommandOutcomeAck(commandMessageId, "ok", null, outcome,
                sessionId, sequenceBefore.Value, commandId), null);
        }
        catch (Exception failure)
        {
            var ack = sequenceBefore is null
                ? UnavailableCommandOutcome(commandMessageId)
                : FailedDurableCommandAck(commandMessageId, sessionId, sequenceBefore.Value,
                    failure.Message, restoreRunIdentity: false);
            return (false, ack, failure);
        }
    }

    /// <summary>
    /// Runs one CLI Explorer Ask invocation as an internal Host command, including each Act-loop
    /// iteration. A normal callback return means the invocation was accepted, not that the Run completed.
    /// Failure preserves the original exception (including cancellation) alongside the durable outcome.
    /// Accepted with error describes partial effects, never successful completion or safe blind retry.
    /// </summary>
    internal (ExplorerTurn.TurnResult? Result, CommandAck Ack, Exception? Failure,
        InternalCommandResult? PolicyTransition) ExecuteExplorerTurn(SessionId sessionId,
        RunId runId, Func<CancellationToken, ExplorerTurn.TurnResult> execute,
        CancellationToken cancellationToken, bool readOnlyLane = false, DelegationId? delegationId = null,
        bool waitForCapacity = false)
    {
        ArgumentNullException.ThrowIfNull(execute);
        var ambientCommand = CausationScope.Current as CommandCausation;
        var commandId = ambientCommand?.CommandId ?? CommandId.New();
        var messageId = commandId.Value.ToString();
        long? sequenceBefore = null;
        AgentCapacity.Lease? capacity = null;
        var callbackInvoked = false;
        var ownsDelegationLease = delegationId is { } owned
            && AgentCapacity.For(_store).IsActive(sessionId, runId, owned);
        try
        {
            sequenceBefore = _store.CurrentSequence(sessionId);
            if (!ownsDelegationLease)
            {
                var scheduler = AgentCapacity.For(_store);
                capacity = waitForCapacity
                    ? scheduler.Acquire(sessionId, runId, null, MaxAgentsFor(sessionId, runId), 0,
                        readOnlyLane, cancellationToken)
                    : scheduler.TryAcquire(sessionId, runId, null, MaxAgentsFor(sessionId, runId), 0,
                        readOnlyLane, cancellationToken);
            }
            if (!ownsDelegationLease && capacity is null)
                return (null, CommandOutcomeAck(messageId, "ok", null,
                    RuntimeCommandOutcome.Deferred("WaitingForCapacity"), sessionId,
                    sequenceBefore.Value, commandId), null, null);
            if (_lastSessionId != sessionId || _lastRunId != runId
                || new RunControlService(_store, _codecs).ActiveRun(sessionId) != runId)
            {
                return (null, new CommandAck(messageId, "error",
                    "the requested session/run is not the current active Run",
                    RuntimeCommandOutcome.Rejected()), null, null);
            }

            sequenceBefore = _store.CurrentSequence(sessionId);
            using var internalCommand = ambientCommand is null
                ? CausationScope.Begin(new CommandCausation(commandId)) : null;
            using var agentInvocation = HostAgentInvocation.Begin(_store, sessionId, runId);
            callbackInvoked = true;
            var result = execute(cancellationToken);
            ReconcileTerminatedSupervisors(sessionId, runId);
            var policyTransition = result.StopReason == StopReason.EndTurn
                ? EvaluateCompletedTurnModePolicy(sessionId, runId, result.TurnId, sequenceBefore.Value, commandId, cancellationToken) : null;
            var ack = CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                sessionId, sequenceBefore.Value, commandId);
            return (result, ack, null, policyTransition);
        }
        catch (Exception failure)
        {
            var ack = sequenceBefore is null
                ? UnavailableCommandOutcome(messageId)
                : !callbackInvoked && failure is IOException
                    ? new CommandAck(messageId, "error", SafeCommandError(failure.Message),
                        RuntimeCommandOutcome.Deferred("AdmissionUnavailable"))
                    : FailedDurableCommandAck(messageId, sessionId, sequenceBefore.Value,
                        failure.Message, restoreRunIdentity: false);
            return (null, ack, failure, null);
        }
        finally { capacity?.Dispose(); }
    }

    private int MaxAgentsFor(SessionId session, RunId run)
    {
        var journal = _store.ReadFrom(session, 1).Where(evt => evt.RunId == run).ToArray();
        if (journal.Length == 0) return 1;
        var authority = RunProjection.Replay(session, run, _codecs, journal).ModeAuthority;
        return authority?.Mode == RunMode.Orchestrate
            && authority.Authorization is { } authorization
            && authority.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow)
            ? Math.Max(1, authorization.Limits.MaxAgents) : 1;
    }

    private InternalCommandResult? EvaluateCompletedTurnModePolicy(SessionId session, RunId run,
        TurnId? exactTurn, long before, CommandId commandId, CancellationToken cancellationToken)
    {
        if (exactTurn is null) return null;
        lock (_modeAuthorityMutationGate)
        {
            var all = _store.ReadFrom(session, 1);
            var own = EventsForRun(all, run);
            var authority = RunProjection.Replay(session, run, _codecs, own).ModeAuthority;
            if (authority is null || !authority.IsAutoModeSwitchEffectiveAt(DateTimeOffset.UtcNow)) return null;
            var causal = SelectCommandResultEvents(all, session, before, commandId)
                .Where(evt => evt.RunId == run).ToArray();
            var completed = causal.Select(_codecs.Decode).OfType<TurnCompleted>()
                .SingleOrDefault(completed => completed.TurnId == exactTurn);
            if (completed is null) return null;
            var source = causal.LastOrDefault(evt => evt.Sequence > before
                && _codecs.Decode(evt) is RunModeProposed proposal && proposal.TurnId == completed.TurnId);
            if (source is null || _codecs.Decode(source) is not RunModeProposed proposed) return null;
            // Only the explicit covered-plan predicate is implemented here. A model's reason
            // alone is not an observable product-policy justification for other transitions.
            if (authority.Mode != RunMode.Plan || proposed.To != RunMode.Act)
                return PolicyTransitionResult(CommandId.New().ToString(), null,
                    RuntimeCommandOutcome.Deferred("ModePolicyPredicateUnavailable"));
            return ApplyUltraCodePolicyTransition(session, run, RunMode.Act,
                "Explicitly covered current plan revision; completed turn recommends execution",
                proposed.AuthorityRevision, cancellationToken, source.EventId);
        }
    }

    internal InternalCommandResult EnsureSessionRoutingPolicy(SessionId session)
    {
        var commandId = CommandId.New();
        var messageId = commandId.ToString();
        long? before = null;
        try
        {
            if (_lastSessionId != session)
                return new InternalCommandResult(new CommandAck(messageId, "error", "Session is not selected",
                    RuntimeCommandOutcome.Rejected()));
            before = _store.CurrentSequence(session);
            if (SessionRoutingAuthorization.Read(_store.ReadFrom(session, 1), _codecs, session) is not null)
                return CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.NoOp(), session, before.Value, commandId);
            using var command = CausationScope.Begin(new CommandCausation(commandId));
            new EventStream(_store, _codecs, session).Append(new SessionRoutingPolicySet(session, _newSessionRoutingPolicy));
            return CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.Accepted(), session, before.Value, commandId);
        }
        catch (Exception failure)
        {
            var ack = before is null
                ? UnavailableCommandOutcome(messageId)
                : FailedDurableCommandAck(messageId, session, before.Value, failure.Message, restoreRunIdentity: false);
            return new InternalCommandResult(ack, failure);
        }
    }

    internal (bool Authorized, InteractionId? Interaction, CommandAck Ack, Exception? Failure) AuthorizeModelRoute(
        SessionId session, RunId run, ModelRoute route, BillingMode mode, bool requireConsent = false)
    {
        var commandId = CommandId.New();
        var messageId = commandId.ToString();
        long? before = null;
        try
        {
            var control = new RunControlService(_store, _codecs);
            if (_lastSessionId != session || _lastRunId != run || control.ActiveRun(session) != run)
                return (false, null, new CommandAck(messageId, "error", "Routing authorization requires the selected active Run",
                    RuntimeCommandOutcome.Rejected()), null);
            before = _store.CurrentSequence(session);
            var events = _store.ReadFrom(session, 1);
            var policy = SessionRoutingAuthorization.Read(events, _codecs, session);
            if (policy is null)
                return (false, null, new CommandAck(messageId, "error", "Session routing policy is absent", RuntimeCommandOutcome.Rejected()), null);
            if (!requireConsent && policy.Allows(route, mode))
            {
                var ack = CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.NoOp(), session, before.Value, commandId);
                return (ack.Status == "ok" && ack.Outcome?.Kind == RuntimeCommandOutcomeKind.NoOp, null, ack, null);
            }
            var offer = new SessionRoutingAuthorization.Offer(policy.Revision, AuthorizedModelRoute.From(route, mode));
            var pending = new Dictionary<InteractionId, InteractionRequested>();
            foreach (var evt in events)
            {
                switch (_codecs.Decode(evt))
                {
                    case InteractionRequested request when evt.RunId == run:
                        pending[request.InteractionId] = request; break;
                    case InteractionResolved resolved: pending.Remove(resolved.InteractionId); break;
                    case InteractionExpired expired: pending.Remove(expired.InteractionId); break;
                }
            }
            var existing = pending.Values.FirstOrDefault(request => SessionRoutingAuthorization.Parse(request) == offer);
            if (existing is not null)
            {
                var ack = CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.Deferred("ModelRouteConsent"),
                    session, before.Value, commandId);
                return (false, ack.Status == "ok" ? existing.InteractionId : null, ack, null);
            }
            var interaction = InteractionId.New();
            using var command = CausationScope.Begin(new CommandCausation(commandId));
            var requestEvent = new InteractionRequested(interaction, InteractionKind.ModelRouteConsent,
                SessionRoutingAuthorization.Context(offer),
                "[{\"id\":\"deny\",\"intent\":\"deny\"},{\"id\":\"allow_route\",\"intent\":\"allow\"}]",
                "deny", null, null, null, null, 0, 1);
            var runProjection = RunProjection.Replay(session, run, _codecs, events);
            var runLane = runProjection.RootTask is { } rootTask
                ? LaneProjection.Replay(_codecs, events).ForTask(rootTask)
                    .FirstOrDefault(lane => lane.State == LaneState.Running)
                : null;
            var requestScope = new ExecutionScopeState(run, runProjection.RootTask, runLane?.Id);
            var batch = new List<DomainEventPayload> { requestEvent };
            var scopes = new List<ExecutionScopeState?> { requestScope };
            if (runProjection.State == RunState.Running && pending.Count == 0)
            {
                if (runLane is null)
                    throw new InvalidStateTransitionException("run", "running without a root Lane for route consent",
                        requestEvent.Type().ToString());
                batch.Add(new RunAwaitingInput(run, runLane.Id));
                scopes.Add(requestScope);
            }
            new EventStream(_store, _codecs, session).AppendBatch(batch, DurabilityClass.Barrier, scopes);
            var consentAck = CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.Deferred("ModelRouteConsent"),
                session, before.Value, commandId);
            return (false, consentAck.Status == "ok" ? interaction : null, consentAck, null);
        }
        catch (Exception failure)
        {
            if (before is null) return (false, null, UnavailableCommandOutcome(messageId), failure);
            try
            {
                var persisted = CommandResultEvents(session, before.Value, commandId);
                var observed = persisted.Select(_codecs.Decode).OfType<InteractionRequested>()
                    .LastOrDefault(request => request.Kind == InteractionKind.ModelRouteConsent);
                var ack = persisted.Length == 0
                    ? new CommandAck(messageId, "error", failure.Message, RuntimeCommandOutcome.Rejected())
                    : new CommandAck(messageId, "error", failure.Message, RuntimeCommandOutcome.Accepted(),
                        persisted.Min(evt => evt.Sequence), persisted.Max(evt => evt.Sequence));
                return (false, observed?.InteractionId, ack, failure);
            }
            catch (Exception)
            {
                return (false, null, UnavailableCommandOutcome(messageId), failure);
            }
        }
    }

    internal InternalCommandResult ResolveModelRouteWithoutClient(SessionId session, RunId run, InteractionId interaction)
    {
        var commandId = CommandId.New();
        long? before = null;
        try
        {
            var control = new RunControlService(_store, _codecs);
            var events = _store.ReadFrom(session, 1);
            if (_lastSessionId != session || _lastRunId != run || control.ActiveRun(session) != run
                || events.LastOrDefault(evt => _codecs.Decode(evt) is InteractionRequested request
                    && request.InteractionId == interaction)?.RunId != run)
                return new CommandAck(commandId.ToString(), "error", "Routing interaction does not belong to the selected active Run",
                    RuntimeCommandOutcome.Rejected());
            before = _store.CurrentSequence(session);
            using var command = CausationScope.Begin(new CommandCausation(commandId));
            using var execution = ExecutionScope.Begin(new ExecutionScopeState(RunId: run));
            try
            {
                control.ResolveModelRouteWithoutClient(session, interaction);
                return CommandOutcomeAck(commandId.ToString(), "ok", null, RuntimeCommandOutcome.Accepted(), session, before.Value, commandId);
            }
            catch (Exception ex) when (ex is InvalidInteractionOptionException or InteractionNotPendingException)
            {
                return CommandOutcomeAck(commandId.ToString(), "error", ex.Message, RuntimeCommandOutcome.Rejected(), session, before.Value, commandId);
            }
        }
        catch (Exception failure)
        {
            return new InternalCommandResult(before is null
                ? UnavailableCommandOutcome(commandId.ToString())
                : FailedDurableCommandAck(commandId.ToString(), session, before.Value,
                    failure.Message, restoreRunIdentity: false), failure);
        }
    }

    internal InternalCommandResult ResolveBudgetWithoutClient(SessionId sessionId, RunId runId, InteractionId interactionId)
    {
        var commandId = CommandId.New();
        var messageId = commandId.Value.ToString();
        long? sequenceBefore = null;
        try
        {
            var control = new RunControlService(_store, _codecs);
            if (_lastSessionId != sessionId || _lastRunId != runId || control.ActiveRun(sessionId) != runId)
                return new CommandAck(messageId, "error", "Budget interaction does not belong to the current active Run",
                    RuntimeCommandOutcome.Rejected());
            var requestEvent = _store.ReadFrom(sessionId, 1).LastOrDefault(evt =>
                _codecs.Decode(evt) is InteractionRequested request && request.InteractionId == interactionId);
            if (requestEvent?.RunId != runId)
                return new CommandAck(messageId, "error", "Budget interaction has no matching Run attribution",
                    RuntimeCommandOutcome.Rejected());
            sequenceBefore = _store.CurrentSequence(sessionId);
            using var command = CausationScope.Begin(new CommandCausation(commandId));
            using var execution = ExecutionScope.Begin(new ExecutionScopeState(RunId: runId));
            try
            {
                control.ResolveBudgetWithoutClient(sessionId, interactionId);
                return CommandOutcomeAck(messageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                    sessionId, sequenceBefore.Value, commandId);
            }
            catch (Exception ex) when (ex is InteractionNotPendingException or InvalidInteractionOptionException or RunNotActiveException)
            {
                return CommandOutcomeAck(messageId, "error", ex.Message, RuntimeCommandOutcome.Rejected(),
                    sessionId, sequenceBefore.Value, commandId);
            }
        }
        catch (Exception failure)
        {
            return new InternalCommandResult(sequenceBefore is null
                ? UnavailableCommandOutcome(messageId)
                : FailedDurableCommandAck(messageId, sessionId, sequenceBefore.Value,
                    failure.Message, restoreRunIdentity: false), failure);
        }
    }

    internal InternalCommandResult RecordModelEscalationRequested(SessionId sessionId, ModelEscalationRequested payload) =>
        RecordModelEscalation(sessionId, payload.RunId, payload);

    internal InternalCommandResult RecordModelEscalationApproved(SessionId sessionId, ModelEscalationApproved payload) =>
        RecordModelEscalation(sessionId, payload.RunId, payload);

    internal InternalCommandResult RecordModelEscalationCompleted(SessionId sessionId, ModelEscalationCompleted payload) =>
        RecordModelEscalation(sessionId, payload.RunId, payload);

    private InternalCommandResult RecordModelEscalation(SessionId sessionId, RunId runId, DomainEventPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var ambientCommand = CausationScope.Current as CommandCausation;
        var commandId = ambientCommand?.CommandId ?? CommandId.New();
        var commandMessageId = commandId.Value.ToString();
        long? sequenceBefore = null;
        try
        {
            var belongsToSession = _store.ReadFrom(sessionId, 1)
                .Select(_codecs.Decode)
                .OfType<RunCreated>()
                .Any(created => created.RunId.Equals(runId));
            if (!belongsToSession)
            {
                return new InternalCommandResult(new CommandAck(commandMessageId, "error",
                    "the Run does not belong to the requested Session", RuntimeCommandOutcome.Rejected()));
            }

            sequenceBefore = _store.CurrentSequence(sessionId);
            using var internalCommand = ambientCommand is null
                ? CausationScope.Begin(new CommandCausation(commandId)) : null;
            // Do not inherit unrelated ambient task/lane/turn attribution: this record names only its
            // persisted Run. Payload v2 TurnId/LaneId remain null unless the caller explicitly supplied them.
            using var execution = ExecutionScope.Begin(new ExecutionScopeState(RunId: runId));
            new EventStream(_store, _codecs, sessionId).Append(payload, DurabilityClass.Standard);
            return CommandOutcomeAck(commandMessageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                sessionId, sequenceBefore.Value, commandId);
        }
        catch (Exception failure)
        {
            var ack = sequenceBefore is null
                ? UnavailableCommandOutcome(commandMessageId)
                : FailedDurableCommandAck(commandMessageId, sessionId, sequenceBefore.Value,
                    failure.Message, restoreRunIdentity: false);
            return new InternalCommandResult(ack, failure);
        }
    }

    internal (bool? Completed, CommandAck Ack, Exception? Failure) CheckRunCompletionAndGate(SessionId sessionId, RunId runId,
        Func<EventStream, IReadOnlyList<ExternalCompletionGateResult>>? runExternalGates,
        MutationLedger? mutationLedger)
    {
        var ambientCommand = CausationScope.Current as CommandCausation;
        var commandId = ambientCommand?.CommandId ?? CommandId.New();
        var commandMessageId = commandId.Value.ToString();
        long? sequenceBefore = null;
        try
        {
            var all = _store.ReadFrom(sessionId, 1);
            if (!all.Select(_codecs.Decode).OfType<RunCreated>().Any(created => created.RunId.Equals(runId)))
            {
                return (null, new CommandAck(commandMessageId, "error", "the Run does not belong to the requested Session",
                    RuntimeCommandOutcome.Rejected()), null);
            }

            var own = EventsForRun(all, runId);
            var run = RunProjection.Replay(sessionId, runId, _codecs, own);
            var tasks = TaskGraphProjection.Replay(_codecs, own);
            var plan = PlanProjection.Replay(_codecs, own);
            var laneProjection = LaneProjection.Replay(_codecs, own);
            LaneId? rootLane = null;
            if (run.RootTask is { } rootTask)
            {
                var runningRootLanes = laneProjection.ForTask(rootTask)
                    .Where(lane => lane.State == LaneState.Running).ToArray();
                if (runningRootLanes.Length == 1) rootLane = runningRootLanes[0].Id;
            }

            sequenceBefore = _store.CurrentSequence(sessionId);
            using var internalCommand = ambientCommand is null
                ? CausationScope.Begin(new CommandCausation(commandId)) : null;
            // Gates validate the latest completed intent on the root Lane, not a synthetic Turn
            // or an unrelated ambient/other-Lane attribution. An open latest Turn cannot borrow
            // attribution from an older completed one.
            var sourceTurnEvent = rootLane is null ? null : own.LastOrDefault(evt =>
                _codecs.Decode(evt) is TurnStarted started && started.LaneId == rootLane);
            var sourceTurn = sourceTurnEvent is null ? null : ((TurnStarted)_codecs.Decode(sourceTurnEvent)).TurnId;
            if (sourceTurn is not null && !own.Any(evt =>
                    _codecs.Decode(evt) is TurnCompleted completed && completed.TurnId == sourceTurn))
                sourceTurn = null;
            using var execution = ExecutionScope.Begin(new ExecutionScopeState(runId, run.RootTask, rootLane,
                sourceTurn, ExecutionId: sourceTurn is null ? null : sourceTurnEvent?.ExecutionId));
            var stream = new EventStream(_store, _codecs, sessionId);
            var completed = new RunCoupon(run, tasks, plan).CheckCompletionAndGate(new PlanService(),
                new ProgressReconciler(), _store, _codecs, sessionId, stream,
                runExternalGates is null ? null : () => runExternalGates(stream), mutationLedger);
            if (_artifacts is not null)
                new RunSummaryService(_store, _codecs, _artifacts, OmniCliRuntime.RedactSensitive).EnsureRecorded(sessionId);
            var ack = CommandOutcomeAck(commandMessageId, "ok", null, RuntimeCommandOutcome.Accepted(),
                sessionId, sequenceBefore.Value, commandId);
            return (completed, ack, null);
        }
        catch (Exception failure)
        {
            return (null, sequenceBefore is null
                ? UnavailableCommandOutcome(commandMessageId)
                : FailedDurableCommandAck(commandMessageId, sessionId, sequenceBefore.Value,
                    failure.Message, restoreRunIdentity: false), failure);
        }
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
    private void StartSession(SessionId session)
    {
        var workspace = Path.GetFullPath(".");
        new EventStream(_store, _codecs, session).AppendBatch(new DomainEventPayload[]
        {
            new SessionCreated(session, WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(workspace)).ToString(),
                workspace, ProfileId.New(), DateTimeOffset.UtcNow),
            new SessionRoutingPolicySet(session, _newSessionRoutingPolicy),
        }, DurabilityClass.Barrier);
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
            var stack = ex.StackTrace is null ? "" : string.Join("; ", ex.StackTrace);
            return FailedDurableCommandAck(command, session, sequenceBefore,
                (ex.Message ?? "exception") + " :: " + stack);
        }
    }

    private CommandAck FailedDurableCommandAck(WireEnvelope command, SessionId session,
        long sequenceBefore, string error, bool newSession = false)
        => FailedDurableCommandAck(command.MessageId, session, sequenceBefore, error, newSession);

    private CommandAck FailedDurableCommandAck(string commandMessageId, SessionId session,
        long sequenceBefore, string error, bool newSession = false, bool restoreRunIdentity = true)
    {
        error = SafeCommandError(error);
        var commandId = new CommandId(Guid.Parse(commandMessageId));
        DomainEvent[] persisted;
        try
        {
            persisted = CommandResultEvents(session, sequenceBefore, commandId);
        }
        catch (Exception)
        {
            return UnavailableCommandOutcome(commandMessageId);
        }
        if (persisted.Length == 0)
            return new CommandAck(commandMessageId, "error", error, RuntimeCommandOutcome.Rejected());

        // Internal evaluations can target another persisted Run/Session. Confirm their effects
        // without changing which Run the Host has selected or re-decoding unrelated lifecycle data.
        if (!restoreRunIdentity)
            return new CommandAck(commandMessageId, "error", error, RuntimeCommandOutcome.Accepted(),
                persisted.Min(evt => evt.Sequence), persisted.Max(evt => evt.Sequence));

        if (newSession)
        {
            _lastRunId = null;
            _lastSnapshot = null;
            _lastWorkingStateText = "";
        }
        _lastSessionId = session;
        var created = persisted.Select(_codecs.Decode).OfType<RunCreated>().LastOrDefault();
        if (created is not null)
        {
            if (!Equals(_lastRunId, created.RunId))
            {
                _lastSnapshot = null;
                _lastWorkingStateText = "";
            }
            _lastRunId = created.RunId;
        }
        return new CommandAck(commandMessageId, "error", error, RuntimeCommandOutcome.Accepted(),
            persisted.Min(evt => evt.Sequence), persisted.Max(evt => evt.Sequence));
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
