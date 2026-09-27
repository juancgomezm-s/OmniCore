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

    public ScriptedToolExecutor(FakeCatalog catalog, ScriptedPermissionPolicy policy)
    {
        _runtime = ToolRuntime.For(catalog, policy, payload =>
        {
            _events.Add(payload);
            return VoidBox.Instance;
        });
        _workspaceRoot = "sim";
    }

    public ScriptedToolExecutor(FakeCatalog catalog, ScriptedPermissionPolicy policy, string workspaceRoot)
    {
        _runtime = ToolRuntime.For(catalog, policy, payload =>
        {
            _events.Add(payload);
            return VoidBox.Instance;
        });
        _workspaceRoot = workspaceRoot;
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

    public ToolOutcome ExecuteTool(ValidatedToolCall validated, bool userApprovesAsk,
        CancellationToken cancellationToken)
    {
        var before = _events.Count;
        var prepContext = new ToolPreparationContext(_workspaceRoot, DateTimeOffset.Now);
        var execContext = new ToolExecutionContext(_workspaceRoot);
        var outcome = _runtime.Run(validated, prepContext, execContext, userApprovesAsk, cancellationToken);
        var emitted = _events.Count - before;
        var events = new DomainEventPayload[emitted];
        for (var i = 0; i < emitted; i++)
        {
            events[i] = _events[before + i];
        }

        return outcome.Succeeded
            ? ToolOutcome.Ok(outcome.Summary ?? "ok", outcome.Effect, outcome.FinalState, events)
            : ToolOutcome.Failed(outcome.Summary ?? "fallo", outcome.FinalState, events);
    }
}