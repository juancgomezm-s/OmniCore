namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
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
    private readonly ToolRuntime _runtime;

    private readonly List<DomainEventPayload> _events = new();

    private readonly string _workspaceRoot;

    /// <summary>
    /// Frontera de capacidad del modelo activa (ADR-0044 §5). Cuando no es null, su
    /// <c>ReadRegistry</c> por-Run viaja al ToolExecutionContext: filesystem.read registra y
    /// filesystem.patch exige lectura previa. null = semántica M2 (sin política de modelo).
    /// </summary>
    private readonly ModelCapabilityBoundary? _boundary;

    public ScriptedToolExecutor(FakeCatalog catalog, ScriptedPermissionPolicy policy)
    {
        _runtime = ToolRuntime.For(catalog, policy, payload =>
        {
            _events.Add(payload);
            return VoidBox.Instance;
        });
        _workspaceRoot = "sim";
        _boundary = null;
    }

    public ScriptedToolExecutor(FakeCatalog catalog, ScriptedPermissionPolicy policy, string workspaceRoot)
    {
        _runtime = ToolRuntime.For(catalog, policy, payload =>
        {
            _events.Add(payload);
            return VoidBox.Instance;
        });
        _workspaceRoot = workspaceRoot;
        _boundary = null;
    }

    /// <summary>
    /// Con frontera de capacidad del modelo (ADR-0044 §5): el ToolRuntime evalúa la frontera
    /// tras Prepare (antes de permisos) y de nuevo antes de ejecutar. La frontera restringe;
    /// jamás autoriza. null conserva la semántica de M2 (sin frontera).
    /// </summary>
    public ScriptedToolExecutor(FakeCatalog catalog, ScriptedPermissionPolicy policy, string workspaceRoot,
        ModelCapabilityBoundary? boundary)
    {
        _runtime = ToolRuntime.For(catalog, policy, payload =>
        {
            _events.Add(payload);
            return VoidBox.Instance;
        }, boundary);
        _workspaceRoot = workspaceRoot;
        _boundary = boundary;
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

    public ToolOutcome ExecuteTool(ValidatedToolCall validated, bool userApprovesAsk,
        CancellationToken cancellationToken)
    {
        var before = _events.Count;
        var prepContext = new ToolPreparationContext(_workspaceRoot, DateTimeOffset.Now);
        // ADR-0044 §5: cuando hay frontera de capacidad (política del modelo), el registro de
        // lecturas efectivas por-Run viaja en el contexto para que las tools exijan lectura previa.
        var execContext = new ToolExecutionContext(_workspaceRoot, _boundary?.ReadRegistry());
        var outcome = _runtime.Run(validated, prepContext, execContext, userApprovesAsk, cancellationToken);
        var emitted = _events.Count - before;
        var events = new DomainEventPayload[emitted];
        for (var i = 0; i < emitted; i++)
        {
            events[i] = _events[before + i];
        }

        return outcome.Succeeded
            ? ToolOutcome.Ok(outcome.Summary ?? "ok", outcome.Preview, outcome.Effect, outcome.FinalState, events)
            : ToolOutcome.Failed(outcome.Summary ?? "fallo", outcome.Preview, outcome.FinalState, events);
    }
}