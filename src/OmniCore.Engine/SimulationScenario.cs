namespace OmniCore.Engine;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Escenario de simulación de M1 (ADR-0041 §1): lo que el ScriptedModelProvider "propone" en
/// cada Turn de cada Lane. Cargado por <c>omni sim</c> desde YAML y a prueba directamente.
/// </summary>
public sealed class SimulationScenario
{
    public string Name { get; }

    public RunMode Mode { get; }

    public string Input { get; }

    /// <summary>Mutaciones que el "modelo" propone en el Turn 1 (ADR-0041 §1, plan:).</summary>
    public IReadOnlyList<SimulatedPlanMutation> Plan { get; }

    /// <summary>Tasks del escenario con sus vínculos PlanItem↔Task y dependencias.</summary>
    public IReadOnlyList<SimulatedTask> Tasks { get; }

    /// <summary>Respuestas del modelo por Lane: lista de acciones de turno.</summary>
    public Dictionary<string, IReadOnlyList<SimulatedTurnAction>> Turns { get; }

    /// <summary>Reglas de permisos del escenario (tool → decisión).</summary>
    public Dictionary<string, PermissionDecision> Permissions { get; }

    /// <summary>Estado final esperado del Run (exit code: 0 = coincide).</summary>
    public string ExpectedRunState { get; }

    /// <summary>Estados finales esperados por PlanItem.</summary>
    public Dictionary<string, string> ExpectedPlan { get; }

    /// <summary>
    /// Nombre de la tool en la que se inyecta un crash tras el Started (ADR-0041 §2, ADR-0004):
    /// el proceso "muere" sin persistir el outcome y el resume lo reconcilia sin duplicar.
    /// null = sin fallo.
    /// </summary>
    public string? FaultAtTool { get; }

    /// <summary>
    /// Respuesta del usuario simulado al <c>PlanApproval</c> de un Run en modo PLAN (ADR-0035 §4):
    /// <c>approve_execute</c> (pasa a ACT en el mismo Run), <c>approve_only</c> (termina como
    /// <c>Planned</c>) o <c>reject</c>. null = nadie responde: el Run queda esperando al usuario.
    /// </summary>
    public string? PlanApproval { get; init; }

    public SimulationScenario(string name, RunMode mode, string input,
        IReadOnlyList<SimulatedPlanMutation> plan, IReadOnlyList<SimulatedTask> tasks,
        Dictionary<string, IReadOnlyList<SimulatedTurnAction>> turns,
        Dictionary<string, PermissionDecision> permissions, string expectedRunState,
        Dictionary<string, string> expectedPlan) : this(name, mode, input, plan, tasks, turns, permissions,
        expectedRunState, expectedPlan, null) { }

    public SimulationScenario(string name, RunMode mode, string input,
        IReadOnlyList<SimulatedPlanMutation> plan, IReadOnlyList<SimulatedTask> tasks,
        Dictionary<string, IReadOnlyList<SimulatedTurnAction>> turns,
        Dictionary<string, PermissionDecision> permissions, string expectedRunState,
        Dictionary<string, string> expectedPlan, string? faultAtTool)
    {
        Name = name;
        Mode = mode;
        Input = input;
        Plan = plan;
        Tasks = tasks;
        Turns = turns;
        Permissions = permissions;
        ExpectedRunState = expectedRunState;
        ExpectedPlan = expectedPlan;
        FaultAtTool = faultAtTool;
    }
}

/// <summary>Mutación de plan que el modelo propone en el primer Turn (ADR-0041 §1).</summary>
public sealed class SimulatedPlanMutation
{
    public string Id { get; }

    public string Text { get; }

    public IReadOnlyList<string> DependsOn { get; }

    public SimulatedPlanMutation(string id, string text, IReadOnlyList<string> dependsOn)
    {
        Id = id;
        Text = text;
        DependsOn = dependsOn;
    }
}

/// <summary>Task declarada en el escenario con vínculos a items del plan.</summary>
public sealed class SimulatedTask
{
    public string Id { get; }

    public IReadOnlyList<SimulatedLink> Links { get; }

    public IReadOnlyList<string> DependsOn { get; }

    public string Objective { get; }

    public SimulatedTask(string id, IReadOnlyList<SimulatedLink> links, IReadOnlyList<string> dependsOn,
        string objective)
    {
        Id = id;
        Links = links;
        DependsOn = dependsOn;
        Objective = objective;
    }
}

/// <summary>Vínculo declarativo de una task a un item del plan.</summary>
public sealed class SimulatedLink
{
    public string Item { get; }

    public string Role { get; }

    public SimulatedLink(string item, string role)
    {
        Item = item;
        Role = role;
    }
}

/// <summary>Acción que el modelo ejecuta en un Turn: una tool call o completar (ADR-0041 §1).</summary>
public sealed class SimulatedTurnAction
{
    public string? Tool { get; }

    public string? Result { get; }

    public string? Effect { get; }

    public string? Complete { get; }

    /// <summary>
    /// Respuesta del usuario simulado si la política pide <c>Ask</c> para esta tool: <c>approve</c>
    /// o <c>deny</c>. null = sin respuesta, y sin cliente un Ask se deniega (ADR-0003).
    /// </summary>
    public string? Answer { get; }

    private SimulatedTurnAction(string? tool, string? result, string? effect, string? complete, string? answer = null)
    {
        Tool = tool;
        Result = result;
        Effect = effect;
        Complete = complete;
        Answer = answer;
    }

    public static SimulatedTurnAction ToolCall(string tool, string effect) => new(tool, "ok", effect, null);

    /// <summary>Tool call con la respuesta del usuario a un posible <c>Ask</c>.</summary>
    public static SimulatedTurnAction ToolCall(string tool, string effect, string? answer) =>
        new(tool, "ok", effect, null, answer);

    public static SimulatedTurnAction ToolCallResult(string tool, string result) => new(tool, result, null, null);

    public static SimulatedTurnAction DoneMarker() => new(null, null, null, "done");

    public bool IsComplete => Complete is not null;
}