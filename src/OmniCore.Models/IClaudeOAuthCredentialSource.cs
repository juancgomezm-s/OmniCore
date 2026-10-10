namespace OmniCore.Models;

/// <summary>
/// Bearer de una cuenta de Claude, listo para poner en las cabeceras. Solo lo que necesita el
/// wire de inferencia: el access token y la identidad no secreta de la cuenta.
/// </summary>
public sealed record ClaudeOAuthBearer(string AccessToken, string? AccountUuid, IReadOnlyList<string> Scopes)
{
    /// <summary>
    /// True si este credencial es de suscripción claude.ai (scope de inferencia). Decide la
    /// cabecera, nunca el nombre del modelo (INV-007). Equivalente a isClaudeAISubscriber en
    /// authAlias.ts.
    /// </summary>
    public override string ToString() => "claude.oauth.bearer";

    public bool IsSubscriber => Scopes.Contains(ClaudeOAuthScopes.Inference, StringComparer.Ordinal);
}

/// <summary>
/// Borde entre el adaptador Messages y el login OAuth por cuenta. El provider pide un Bearer
/// vigente y, tras un 401, fuerza un refresco — nada más: no ve el almacén cifrado, el coordinador
/// de refresh ni los archivos de configuración (INV-008).
///
/// Es el mismo papel que <see cref="ISubscriptionCredentialSource"/> cumple para la suscripción
/// ChatGPT (ADR-0011 §3.4), con una diferencia: aquí el refresco puede descubrir que la sesión
/// murió y hay que volver a autenticarse, así que devuelve un resultado tipado en vez de lanzar
/// siempre.
/// </summary>
public interface IClaudeOAuthCredentialSource
{
    /// <summary>
    /// Devuelve un Bearer vigente, refrescando si caduca dentro del margen. Null si no hay
    /// sesión: el provider cae al error de autenticación en vez de mandar un request sin credencial.
    /// </summary>
    ValueTask<ClaudeOAuthBearer?> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Fuerza un refresco tras un 401. Null si la sesión ya no es recuperable (logout, token
    /// muerto, credencial borrada): el provider informa AuthenticationFailed.
    /// </summary>
    ValueTask<ClaudeOAuthBearer?> ForceRefreshAsync(CancellationToken cancellationToken);
}
