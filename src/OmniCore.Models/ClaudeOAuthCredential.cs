namespace OmniCore.Models;

/// <summary>
/// Credencial OAuth de Claude persistida por el usuario. Es lo único que viaja al almacén
/// cifrado, así que lleva todos los campos que el runtime necesita para volver a usar el token
/// sin repetir el flujo de login.
///
/// No es un registro plano: <see cref="ClientId"/> guarda el client id con el que el token se
/// emitió, no el de la identidad actual. Sin eso, cambiar la configuración rompería el refresh de
/// los tokens ya obtenidos (client.ts:281-286, plan §2.5).
/// </summary>
public sealed record ClaudeOAuthCredential(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<string> Scopes,
    string ClientId)
{
    /// <summary>Serie del wire: se reconstruye al leer, nunca se interpreta a mano.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Identidad no secreta de la cuenta, para /doctor y la status line.</summary>
    public string? AccountUuid { get; init; }

    public string? OrganizationUuid { get; init; }

    public string? EmailAddress { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>Plan informado por el servidor (max/pro/team/enterprise). Null si no se sabe.</summary>
    public string? SubscriptionType { get; init; }

    /// <summary>Nivel de rate limit informado. Null si no se sabe: nunca se estima (ADR-0031).</summary>
    public string? RateLimitTier { get; init; }

    /// <summary>Cuándo se obtuvo el credential, para mostrar la antigüedad sin descifrarlo.</summary>
    public DateTimeOffset AuthenticatedAt { get; init; }

    /// <summary>True si el token sirve para inferencia bajo suscripción claude.ai.</summary>
    public bool CanDoInference =>
        Scopes.Contains(ClaudeOAuthScopes.Inference, StringComparer.Ordinal);

    /// <summary>
    /// El token necesita refresco antes de usarse, con el margen de 5 minutos de referencia
    /// (client.ts:isOAuthTokenExpired). Un ExpiresAt desconocido obliga a refrescar: mejor un
    /// RPC de más que un 401 en medio de un turno.
    /// </summary>
    public bool NeedsRefresh(DateTimeOffset now, TimeSpan? margin = null)
    {
        var slack = margin ?? DefaultRefreshMargin;
        return ExpiresAt - slack <= now;
    }

    public static readonly TimeSpan DefaultRefreshMargin = TimeSpan.FromMinutes(5);

    /// <summary>Convierte unos tokens recién emitidos en credencial persistente.</summary>
    public static ClaudeOAuthCredential FromTokens(ClaudeOAuthTokens tokens, DateTimeOffset authenticatedAt) =>
        new(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAt, tokens.Scopes, tokens.ClientId)
        {
            AccountUuid = tokens.AccountUuid,
            OrganizationUuid = tokens.OrganizationUuid,
            AuthenticatedAt = authenticatedAt,
        };

    /// <summary>Proyección a estado de runtime, para el provider y el coordinador de refresh.</summary>
    public ClaudeOAuthTokens ToTokens() =>
        new(AccessToken, RefreshToken, ExpiresAt, Scopes, ClientId, AccountUuid, OrganizationUuid);
}
