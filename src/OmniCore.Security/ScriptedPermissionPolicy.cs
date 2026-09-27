namespace OmniCore.Security;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Policy de permisos para la simulación de M1 (ADR-0037 §9, ADR-0041): una tabla de reglas
/// Allow/Ask/Deny cargada del escenario. Combina capas con el mínimo (Deny &lt; Ask &lt; Allow,
/// ADR-0037 §2) y construye el AuthorizedToolIntent (único lugar que puede; el constructor es
/// internal de este assembly, INV-018). No implementa el flujo de Ask (cola de interacciones):
/// aquí Ask se trata como Deny salvo que el escenario tenga una respuesta; eso lo gestiona el
/// Engine (ADR-0034).
/// </summary>
public sealed class ScriptedPermissionPolicy : IPermissionPolicy
{
    private readonly Dictionary<string, PermissionDecision> _toolDecisions = new();

    private RunMode _modeForDefaults;

    private bool _useModeDefaults;

    public ScriptedPermissionPolicy(Dictionary<string, PermissionDecision> toolDecisions)
    {
        foreach (var kv in toolDecisions)
        {
            _toolDecisions[kv.Key] = kv.Value;
        }
    }

    /// <summary>Activa la segunda capa ModeDefaultsPolicy para las tools sin regla explícita.</summary>
    public ScriptedPermissionPolicy WithModeDefaults(RunMode mode)
    {
        _useModeDefaults = true;
        _modeForDefaults = mode;
        return this;
    }

    /// <summary>Tabla de reglas por tool (p. ej. "fake.write" → Ask).</summary>
    public static ScriptedPermissionPolicy WithTool(string tool, PermissionDecision decision)
    {
        var table = new Dictionary<string, PermissionDecision>();
        table[tool] = decision;
        return new ScriptedPermissionPolicy(table);
    }

    public PermissionDecision ToolDecision(string toolName)
    {
        var found = _toolDecisions.TryGetValue(toolName, out var decision);
        return found ? decision! : PermissionDecision.Allow;
    }

    /// <summary>Evalúa el intent contra la tabla + (opcional) defaults por modo. Combina por mínimo.</summary>
    public PermissionDecisionRecord Evaluate(ToolIntent intent)
    {
        var toolDecision = ToolDecision(intent.ToolId.ToString());
        var layers = new List<LayerDecision>();
        layers.Add(new LayerDecision("scripted-policy", toolDecision, "scenario"));
        var final = toolDecision;

        if (_useModeDefaults && toolDecision == PermissionDecision.Allow)
        {
            var mode = ModeDefaultsPolicy.Instance().ForRunMode(_modeForDefaults, Classify(intent.ToolId.ToString()));
            layers.Add(new LayerDecision("mode-defaults", mode, "modo " + _modeForDefaults));
            final = Min(final, mode);
        }

        return new PermissionDecisionRecord(final, layers.ToArray(), null);
    }

    private static PermissionDecision Min(PermissionDecision a, PermissionDecision b)
    {
        if (a == PermissionDecision.Deny || b == PermissionDecision.Deny) return PermissionDecision.Deny;
        if (a == PermissionDecision.Ask || b == PermissionDecision.Ask) return PermissionDecision.Ask;
        return PermissionDecision.Allow;
    }

    private static PermissionResource Classify(string toolName)
    {
        switch (toolName)
        {
            case "filesystem.write":
            case "fake.write":
                return PermissionResource.WriteInsideWorkspace;
            case "process.exec":
            case "fake.test":
                return PermissionResource.BuildTestProcess;
            case "shell.exec":
                return PermissionResource.Shell;
            default:
                return PermissionResource.ReadInsideWorkspace;
        }
    }

    /// <summary>
    /// Construye un intent autorizado. El constructor del AuthorizedToolIntent es internal en
    /// este assembly, así que es el único punto donde se materializa (INV-018). Ask y Deny se
    /// lanzan como PermissionDeniedException: la resolución de Ask la decide el Engine.
    /// </summary>
    public IAuthorizedToolIntent Authorize(ToolIntent intent)
    {
        var decision = Evaluate(intent);
        if (decision.Final != PermissionDecision.Allow)
        {
            throw new PermissionDeniedException(intent.ToolId.ToString(), "decisión " + decision.Final);
        }

        return new AuthorizedToolIntent(intent, decision, "omnicore.security");
    }

    /// <summary>
    /// Ask aprobado por interacción: ya no se re-evalúa (INV-002); se materializa el intent
    /// autorizado con la decisión efectiva Allow y el grant concedido. El grant Once se consume
    /// de forma que una segunda ejecución del mismo intent vuelve a requerir Ask.
    /// </summary>
    public IAuthorizedToolIntent AuthorizeApproved(ToolIntent intent, GrantId? approvedGrant)
    {
        var layers = new LayerDecision[] {
            new LayerDecision("interaction", PermissionDecision.Allow, "approved-by-user"),
        };
        var decision = new PermissionDecisionRecord(PermissionDecision.Allow, layers, approvedGrant);
        return new AuthorizedToolIntent(intent, decision, "omnicore.security:approved");
    }
}
