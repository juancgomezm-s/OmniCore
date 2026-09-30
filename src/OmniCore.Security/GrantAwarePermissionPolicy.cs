namespace OmniCore.Security;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Añade grants revocables a una política base. Solo convierte Ask cuya procedencia sea
/// perfil/UserPolicy (incluidos defaults de modo); cualquier capa desconocida conserva el Ask.
/// </summary>
public sealed class GrantAwarePermissionPolicy : IPermissionPolicy, IGrantablePermissionPolicy
{
    private readonly IPermissionPolicy _inner;
    private readonly IPermissionGrantStore _grants;
    private readonly WorkspaceId _workspace;
    private readonly RunId? _run;

    public GrantAwarePermissionPolicy(IPermissionPolicy inner, IPermissionGrantStore grants,
        WorkspaceId workspace, RunId? run)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _run = run;
    }

    public PermissionDecisionRecord Evaluate(ToolIntent intent)
    {
        var original = _inner.Evaluate(intent);
        if (original.Final != PermissionDecision.Ask || !IsGrantableAsk(original)) return original;
        var grant = _grants.Find(_workspace, _run, intent.ToolId.ToString(), FilePermissionGrantStore.ClaimsKey(intent));
        if (grant is null) return original;
        var layers = original.Layers.Append(new LayerDecision("permission-grant", PermissionDecision.Allow,
            grant.Lifetime + ":" + grant.Id)).ToArray();
        return new PermissionDecisionRecord(PermissionDecision.Allow, layers, grant.Id);
    }

    public AuthorizedToolIntent Authorize(ToolIntent intent)
    {
        var decision = Evaluate(intent);
        if (decision.Final != PermissionDecision.Allow)
            throw new PermissionDeniedException(intent.ToolId.ToString(), "decisión " + decision.Final);
        // Grant-aware policy is itself in Security, the only assembly allowed to construct this intent.
        return new AuthorizedToolIntent(intent, decision, "omnicore.security:grant-aware");
    }

    public AuthorizedToolIntent AuthorizeApproved(ToolIntent intent, GrantId? approvedGrant)
    {
        var evaluated = _inner.Evaluate(intent);
        if (evaluated.Final == PermissionDecision.Deny)
            throw new PermissionDeniedException(intent.ToolId.ToString(), "la aprobación no puede levantar un Deny de la política");
        var layers = evaluated.Layers.Append(new LayerDecision("interaction", PermissionDecision.Allow,
            "approved-by-user")).ToArray();
        return new AuthorizedToolIntent(intent,
            new PermissionDecisionRecord(PermissionDecision.Allow, layers, approvedGrant),
            "omnicore.security:approved");
    }

    public bool CanCreatePersistentGrants => true;

    public GrantId? RecordApprovedGrant(ToolIntent intent, GrantLifetime lifetime, CancellationToken cancellationToken)
    {
        if (lifetime == GrantLifetime.Once) return null;
        if (lifetime is not (GrantLifetime.Run or GrantLifetime.Workspace))
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Lifetime no habilitado para grants de interacción.");
        var decision = _inner.Evaluate(intent);
        if (decision.Final != PermissionDecision.Ask || !IsGrantableAsk(decision))
            throw new PermissionDeniedException(intent.ToolId.ToString(),
                "solo se puede guardar un grant para Ask de perfil o UserPolicy");
        if (lifetime == GrantLifetime.Run && _run is null)
            throw new InvalidOperationException("No hay RunId para crear un grant de Run.");
        var grant = new PermissionGrantRecord(GrantId.New(), intent.ToolId.ToString(),
            FilePermissionGrantStore.ClaimsKey(intent), lifetime, _workspace,
            lifetime == GrantLifetime.Run ? _run : null, DateTimeOffset.UtcNow);
        _grants.Add(grant, cancellationToken);
        return grant.Id;
    }

    private static bool IsGrantableAsk(PermissionDecisionRecord decision)
    {
        var asks = decision.Layers.Where(layer => layer.Decision == PermissionDecision.Ask).ToArray();
        return asks.Length > 0 && asks.All(layer => layer.Layer is "UserPolicy" or "user-policy"
            or "profile" or "mode-defaults");
    }
}
