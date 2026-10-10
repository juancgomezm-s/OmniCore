namespace OmniCore.Models;

/// <summary>
/// Construye la URL de autorización OAuth con los parámetros requeridos por el protocolo.
/// Equivalente a buildAuthUrl en packages/provider/src/oauth/client.ts:138-205.
/// </summary>
public static class ClaudeOAuthAuthorizeUrlBuilder
{
    /// <summary>
    /// Redirect del modo manual: la pagina de Anthropic muestra AUTHORIZATION_CODE#STATE y el
    /// usuario lo pega. Es una URL publica, no un secreto (auth-code-listener.ts).
    /// </summary>
    public const string ManualRedirectUri = "https://platform.claude.com/oauth/code/callback";

    /// <summary>
    /// Construye la URL de authorize con todos los parámetros necesarios.
    /// </summary>
    /// <param name="identity">Identidad del cliente (client_id, scopes, endpoints).</param>
    /// <param name="codeChallenge">PKCE code challenge (S256).</param>
    /// <param name="state">State anti-CSRF.</param>
    /// <param name="port">Puerto del listener loopback.</param>
    /// <param name="isManual">Si true, usa MANUAL_REDIRECT_URL en vez de localhost callback.</param>
    /// <param name="inferenceOnly">Si true, solo pide scope user:inference (token largo).</param>
    /// <param name="orgUuid">UUID de organización opcional.</param>
    /// <param name="loginHint">Email pre-poblado en el formulario de login.</param>
    /// <param name="loginMethod">Método de login preferido (sso|magic_link|google).</param>
    public static string Build(
        ClaudeOAuthClientIdentity identity,
        string codeChallenge,
        string state,
        int port,
        bool isManual = false,
        bool inferenceOnly = false,
        string? orgUuid = null,
        string? loginHint = null,
        string? loginMethod = null)
    {
        var redirectUri = isManual
            ? ManualRedirectUri
            : $"http://localhost:{port}/callback";

        var scope = inferenceOnly
            ? "user:inference"
            : string.Join(' ', identity.Scopes);

        var query = new List<string>
        {
            "code=true",
            $"client_id={Uri.EscapeDataString(identity.ClientId)}",
            "response_type=code",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"scope={Uri.EscapeDataString(scope)}",
            $"code_challenge={Uri.EscapeDataString(codeChallenge)}",
            "code_challenge_method=S256",
            $"state={Uri.EscapeDataString(state)}",
        };

        if (!string.IsNullOrEmpty(orgUuid))
            query.Add($"orgUUID={Uri.EscapeDataString(orgUuid)}");
        if (!string.IsNullOrEmpty(loginHint))
            query.Add($"login_hint={Uri.EscapeDataString(loginHint)}");
        if (!string.IsNullOrEmpty(loginMethod))
            query.Add($"login_method={Uri.EscapeDataString(loginMethod)}");

        return $"{identity.AuthorizeUrl}?{string.Join('&', query)}";
    }
}
