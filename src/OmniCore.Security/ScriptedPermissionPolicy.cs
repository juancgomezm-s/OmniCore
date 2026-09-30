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
public sealed class ScriptedPermissionPolicy : IPermissionPolicy, IGrantablePermissionPolicy
{
    private readonly Dictionary<string, PermissionDecision> _toolDecisions = new();

    private readonly Dictionary<string, PermissionDecision> _userDecisions = new(StringComparer.Ordinal);

    private RunMode _modeForDefaults;

    private bool _useModeDefaults;

    private IPermissionGrantStore? _grantStore;

    private WorkspaceId? _grantWorkspace;

    private RunId? _grantRun;

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

    /// <summary>Conecta grants del usuario, aislados por workspace y Run (ADR-0037 §5).</summary>
    public ScriptedPermissionPolicy WithGrantStore(IPermissionGrantStore store, WorkspaceId workspace, RunId? run)
    {
        _grantStore = store ?? throw new ArgumentNullException(nameof(store));
        _grantWorkspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _grantRun = run;
        return this;
    }

    /// <summary>Configura una regla UserPolicy (distinta de restricciones de repo/escenario).</summary>
    public ScriptedPermissionPolicy WithUserPolicyTool(string tool, PermissionDecision decision)
    {
        _userDecisions[tool] = decision;
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

    /// <summary>
    /// Evalúa el intent contra la tabla + (opcional) defaults por modo. La decisión final es el
    /// mínimo de TODAS las capas (Deny &lt; Ask &lt; Allow, ADR-0037 §2): la capa de modo se aplica
    /// siempre que esté activa, también cuando la tabla ya dijo Ask (Ask ∧ Deny = Deny).
    /// </summary>
    public PermissionDecisionRecord Evaluate(ToolIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var toolDecision = ToolDecision(intent.ToolId.ToString());
        var layers = new List<LayerDecision>();
        layers.Add(new LayerDecision("scripted-policy", toolDecision, "scenario"));
        var final = toolDecision;
        if (_userDecisions.TryGetValue(intent.ToolId.ToString(), out var userDecision))
        {
            layers.Add(new LayerDecision("UserPolicy", userDecision, "user-policy"));
            final = Min(final, userDecision);
        }

        if (_useModeDefaults)
        {
            var resource = Classify(intent);
            var mode = ModeDefaultsPolicy.Instance().ForRunMode(_modeForDefaults, resource);
            layers.Add(new LayerDecision("mode-defaults", mode, "modo " + _modeForDefaults + " · " + resource));
            final = Min(final, mode);
        }

        if (final == PermissionDecision.Ask && _grantStore is not null && _grantWorkspace is not null
            && IsGrantableAsk(layers))
        {
            var grant = _grantStore.Find(_grantWorkspace!, _grantRun, intent.ToolId.ToString(),
                FilePermissionGrantStore.ClaimsKey(intent));
            if (grant is not null)
            {
                layers.Add(new LayerDecision("permission-grant", PermissionDecision.Allow,
                    grant.Lifetime + ":" + grant.Id));
                final = PermissionDecision.Allow;
                return new PermissionDecisionRecord(final, layers.ToArray(), grant.Id);
            }
        }

        return new PermissionDecisionRecord(final, layers.ToArray(), null);
    }

    /// <summary>Guarda una aprobación persistente solo cuando los Ask originales provienen de política/grupo.</summary>
    public bool CanCreatePersistentGrants => _grantStore is not null && _grantWorkspace is not null;

    public GrantId? RecordApprovedGrant(ToolIntent intent, GrantLifetime lifetime,
        CancellationToken cancellationToken)
    {
        if (lifetime == GrantLifetime.Once) return null;
        if (_grantStore is null || _grantWorkspace is null)
            throw new InvalidOperationException("La política no tiene conectado un almacén de grants.");
        if (lifetime is not (GrantLifetime.Run or GrantLifetime.Workspace))
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        var evaluated = Evaluate(intent);
        if (evaluated.Final != PermissionDecision.Ask || !IsGrantableAsk(evaluated.Layers))
            throw new PermissionDeniedException(intent.ToolId.ToString(),
                "solo se puede guardar un grant para Ask de UserPolicy o del perfil");
        if (lifetime == GrantLifetime.Run && _grantRun is null)
            throw new InvalidOperationException("No hay RunId para crear un grant de Run.");
        var grant = new PermissionGrantRecord(GrantId.New(), intent.ToolId.ToString(),
            FilePermissionGrantStore.ClaimsKey(intent), lifetime, _grantWorkspace!,
            lifetime == GrantLifetime.Run ? _grantRun : null, DateTimeOffset.UtcNow);
        _grantStore.Add(grant, cancellationToken);
        return grant.Id;
    }

    private static bool IsGrantableAsk(IReadOnlyList<LayerDecision> layers)
    {
        var asks = layers.Where(layer => layer.Decision == PermissionDecision.Ask).ToArray();
        return asks.Length > 0 && asks.All(layer => layer.Layer is "UserPolicy" or "user-policy"
            or "profile" or "mode-defaults");
    }

    private static PermissionDecision Min(PermissionDecision a, PermissionDecision b)
    {
        if (a == PermissionDecision.Deny || b == PermissionDecision.Deny) return PermissionDecision.Deny;
        if (a == PermissionDecision.Ask || b == PermissionDecision.Ask) return PermissionDecision.Ask;
        return PermissionDecision.Allow;
    }

    /// <summary>
    /// Clasifica el intent por lo que DECLARA (claims y clase de efecto; ADR-0014 §2), no solo
    /// por el nombre de la tool. Falla cerrado: un intent con efecto que no encaja en ninguna
    /// categoría conocida se trata como proceso externo o desconocido (Ask en Act, Deny en
    /// Plan), nunca como lectura. Solo un intent sin efecto y sin claims sensibles es lectura.
    /// </summary>
    internal static PermissionResource Classify(ToolIntent intent)
    {
        var toolName = intent.ToolId.ToString();

        // Superficies con semántica fija conocida (tools Core y simulación de M1).
        switch (toolName)
        {
            case "shell.exec":
                return PermissionResource.Shell;
            case "plan.propose":
            case "reference.resolve":
            case "tool.search":
                return intent.Claims.Secrets.Count > 0 ? PermissionResource.SecretPaths
                    : PermissionResource.PlanAndReferenceTools;
            case "fake.write":
                return PermissionResource.WriteInsideWorkspace;
            case "fake.test":
                return PermissionResource.BuildTestProcess;
        }

        var claims = intent.Claims;
        if (claims.Secrets.Count > 0)
        {
            return PermissionResource.SecretPaths;
        }

        if (claims.Process is not null)
        {
            return claims.Process.EffectClass switch
            {
                "Observational" => PermissionResource.ObservationalProcess,
                "Rerunnable" or "WorkspaceEffect" => PermissionResource.BuildTestProcess,
                _ => PermissionResource.ExternalOrUnknownProcess,
            };
        }

        if (claims.Network.Count > 0)
        {
            return PermissionResource.OtherNetwork;
        }

        if (claims.Writes.Count > 0)
        {
            return PermissionResource.WriteInsideWorkspace;
        }

        return intent.Effect == EffectClass.None
            ? PermissionResource.ReadInsideWorkspace
            : PermissionResource.ExternalOrUnknownProcess;
    }

    /// <summary>
    /// Construye un intent autorizado. El constructor del AuthorizedToolIntent es internal en
    /// este assembly, así que es el único punto donde se materializa (INV-018). Ask y Deny se
    /// lanzan como PermissionDeniedException: la resolución de Ask la decide el Engine.
    /// </summary>
    public AuthorizedToolIntent Authorize(ToolIntent intent)
    {
        var decision = Evaluate(intent);
        if (decision.Final != PermissionDecision.Allow)
        {
            throw new PermissionDeniedException(intent.ToolId.ToString(), "decisión " + decision.Final);
        }

        return new AuthorizedToolIntent(intent, decision, "omnicore.security");
    }

    /// <summary>
    /// Ask aprobado por interacción: se materializa el intent autorizado con la decisión efectiva
    /// Allow y el grant concedido (INV-002: la aprobación vale para esta ejecución). Antes se
    /// re-evalúa la política: si alguna capa dice Deny, la aprobación no lo levanta.
    /// </summary>
    public AuthorizedToolIntent AuthorizeApproved(ToolIntent intent, GrantId? approvedGrant)
    {
        // Una aprobación humana solo levanta un Ask, nunca un Deny (ADR-0037 §2): se comprueba
        // que la política haya pedido Ask para este intent antes de materializar la autorización.
        var evaluated = Evaluate(intent);
        if (evaluated.Final == PermissionDecision.Deny)
        {
            throw new PermissionDeniedException(intent.ToolId.ToString(),
                "la aprobación no puede levantar un Deny de la política");
        }

        var layers = evaluated.Layers
            .Append(new LayerDecision("interaction", PermissionDecision.Allow, "approved-by-user"))
            .ToArray();
        var decision = new PermissionDecisionRecord(PermissionDecision.Allow, layers, approvedGrant);
        return new AuthorizedToolIntent(intent, decision, "omnicore.security:approved");
    }
}
