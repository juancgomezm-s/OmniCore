namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

/// <summary>
/// Implementa IToolExecutor con el pipeline real de M1 (ADR-0014): FakeTools + Scripted
/// Permission Policy + Tool Runtime. Acumula los eventos emitidos por el runtime y los
/// devuelve al Engine para que los persista (ADR-0036 §5). Composición del Host.
/// </summary>
public sealed class ScriptedToolExecutor : IToolExecutor
{
    private readonly FakeCatalog _catalog;

    private readonly IPermissionPolicy _policy;

    private readonly string _workspaceRoot;

    /// <summary>
    /// Frontera de capacidad del modelo activa (ADR-0044 §5). Cuando no es null, su
    /// <c>ReadRegistry</c> por-Run viaja al ToolExecutionContext: filesystem.read registra y
    /// filesystem.patch exige lectura previa. null = semántica M2 (sin política de modelo).
    /// </summary>
    private readonly ModelCapabilityBoundary? _boundary;

    private readonly IExecutableResolver _executableResolver;

    private readonly WeakSandboxConsentState _weakSandboxConsent;

    private readonly IAuditSink? _audit;

    private readonly Func<InteractionRequested, string?>? _interactionResponder;

    private readonly bool _isInteractive;

    public ScriptedToolExecutor(FakeCatalog catalog, ScriptedPermissionPolicy policy)
    {
        _catalog = catalog;
        _policy = policy;
        _workspaceRoot = "sim";
        _boundary = null;
        _executableResolver = new SystemExecutableResolver();
        _weakSandboxConsent = new WeakSandboxConsentState();
    }

    public ScriptedToolExecutor(FakeCatalog catalog, ScriptedPermissionPolicy policy, string workspaceRoot)
    {
        _catalog = catalog;
        _policy = policy;
        _workspaceRoot = workspaceRoot;
        _boundary = null;
        _executableResolver = new SystemExecutableResolver();
        _weakSandboxConsent = new WeakSandboxConsentState();
    }

    /// <summary>
    /// Con frontera de capacidad del modelo (ADR-0044 §5): el ToolRuntime evalúa la frontera
    /// tras Prepare (antes de permisos) y de nuevo antes de ejecutar. La frontera restringe;
    /// jamás autoriza. null conserva la semántica de M2 (sin frontera).
    /// </summary>
    public ScriptedToolExecutor(FakeCatalog catalog, ScriptedPermissionPolicy policy, string workspaceRoot,
        ModelCapabilityBoundary? boundary)
        : this(catalog, policy, workspaceRoot, boundary, null, null, false)
    {
    }

    public ScriptedToolExecutor(FakeCatalog catalog, ScriptedPermissionPolicy policy, string workspaceRoot,
        ModelCapabilityBoundary? boundary, IAuditSink? audit,
        Func<InteractionRequested, string?>? interactionResponder, bool isInteractive,
        WeakSandboxConsentState? weakSandboxConsent = null)
    {
        _catalog = catalog;
        _policy = policy;
        _workspaceRoot = workspaceRoot;
        _boundary = boundary;
        _executableResolver = new SystemExecutableResolver();
        _weakSandboxConsent = weakSandboxConsent ?? new WeakSandboxConsentState();
        _audit = audit;
        _interactionResponder = interactionResponder;
        _isInteractive = isInteractive;
    }

    public static ScriptedToolExecutor Default() =>
        new ScriptedToolExecutor(HostTools.Default().Catalog(),
            ScriptedPermissionPolicy.WithTool("fake.write", PermissionDecision.Allow));

    /// <summary>Executor con el catálogo completo (fake + tools Core) y política por modo.</summary>
    public static ScriptedToolExecutor WithCoreTools(FakeCatalog catalog, ScriptedPermissionPolicy policy) =>
        new ScriptedToolExecutor(catalog, policy);

    /// <summary>Executor con herramientas Core y la raíz real del workspace (para el Turn).</summary>
    public static ScriptedToolExecutor WithWorkspace(FakeCatalog catalog, ScriptedPermissionPolicy policy,
        string workspaceRoot) => new ScriptedToolExecutor(catalog, policy, workspaceRoot);

    /// <summary>
    /// Executor con herramientas Core, raíz real del workspace y la frontera de capacidad del
    /// modelo (ADR-0044 §5). null = sin frontera (semántica M2).
    /// </summary>
    public static ScriptedToolExecutor WithWorkspace(FakeCatalog catalog, ScriptedPermissionPolicy policy,
        string workspaceRoot, ModelCapabilityBoundary? boundary) =>
        new ScriptedToolExecutor(catalog, policy, workspaceRoot, boundary);

    /// <summary>
    /// Pipeline sin journal, solo para tests del pipeline: nada se persiste y todos los eventos
    /// vuelven en <c>ToolOutcome.Events</c>. No forma parte del puerto del Engine
    /// (<see cref="IToolExecutor"/>), que exige stream para no perder el Barrier (INV-014).
    /// </summary>
    public ToolOutcome ExecuteToolWithoutJournal(ValidatedToolCall validated, bool userApprovesAsk,
        CancellationToken cancellationToken) =>
        Run(validated, userApprovesAsk, cancellationToken, null);

    /// <summary>
    /// Pipeline real con escritura en vivo del journal (ADR-0002 §2): cuando el intent declara un
    /// efecto (EffectClass ≠ None), <c>ToolCallStarted</c> se persiste con commit Barrier ANTES de
    /// que la tool ejecute, y los eventos previos del pipeline se persisten en orden (Standard)
    /// para que la secuencia del Started nunca preceda a la de sus predecesores. Los outcomes
    /// posteriores se devuelven en <c>ToolOutcome.Events</c> para que el Engine los persista
    /// (Standard) tras la ejecución. Con <c>stream == null</c> no se persiste nada aquí y la lista
    /// completa vuelve en <c>Events</c> (semántica previa). Estado siempre local a la llamada: no
    /// hay buffers ni callbacks compartidos entre Runs.
    /// </summary>
    public ToolOutcome ExecuteTool(ValidatedToolCall validated, bool userApprovesAsk,
        CancellationToken cancellationToken, EventStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Run(validated, userApprovesAsk, cancellationToken, stream);
    }

    private ToolOutcome Run(ValidatedToolCall validated, bool userApprovesAsk,
        CancellationToken cancellationToken, EventStream? stream)
    {
        // Buffer local a la llamada (evita estado compartido entre Runs). Sin flush, contiene
        // todos los eventos emitidos; tras un flush contiene solo los outcomes.
        var buffered = new List<DomainEventPayload>();

        Func<DomainEventPayload, VoidBox> emit = payload =>
        {
            if (stream is not null && payload is InteractionRequested or InteractionResolved)
            {
                foreach (var evt in buffered) stream.Append(evt);
                buffered.Clear();
                stream.Append(payload);
                return VoidBox.Instance;
            }

            if (stream is not null && payload is ToolCallStarted started
                && started.EffectClass != EffectClass.None)
            {
                // ADR-0004 §2: el Started de un intent con efecto se confirma con commit Barrier
                // ANTES de que la tool ejecute (aquí el ToolRuntime aún no llamó ExecuteAsync).
                // Los eventos previos se escriben antes (Standard, en orden) para no romper la
                // secuencia, y luego este Started como Barrier.
                foreach (var evt in buffered)
                {
                    stream.Append(evt);
                }

                stream.Append(started, DurabilityClass.Barrier);
                buffered.Clear();
                return VoidBox.Instance;
            }

            buffered.Add(payload);
            return VoidBox.Instance;
        };

        var runtime = new ToolRuntime(_catalog, _policy, emit, _boundary, _executableResolver);
        var prepContext = new ToolPreparationContext(_workspaceRoot, DateTimeOffset.Now);
        // ADR-0044 §5: cuando hay frontera de capacidad (política del modelo), el registro de
        // lecturas efectivas por-Run viaja en el contexto para que las tools exijan lectura previa.
        var execContext = new ToolExecutionContext(_workspaceRoot, _boundary?.ReadRegistry(),
            payload => emit(payload), _interactionResponder, _audit, _isInteractive, _weakSandboxConsent);
        var outcome = runtime.Run(validated, prepContext, execContext, userApprovesAsk, cancellationToken);
        var events = buffered.ToArray();

        return outcome.Succeeded
            ? ToolOutcome.Ok(outcome.Summary ?? "ok", outcome.Preview, outcome.Effect, outcome.FinalState, events)
            : ToolOutcome.Failed(outcome.Summary ?? "fallo", outcome.Preview, outcome.FinalState, events);
    }
}