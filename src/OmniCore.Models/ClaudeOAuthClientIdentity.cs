namespace OmniCore.Models;

/// <summary>
/// Identidad de cliente OAuth configurable por el usuario.
/// No tiene valores por defecto: se lee de la configuración del usuario (ADR-0039 §2).
/// El runtime es agnóstico respecto a qué valores use el usuario.
/// </summary>
public sealed record ClaudeOAuthClientIdentity(
    string ClientId,
    string UserAgentTemplate,   // con {version} para sustituir en BuildUserAgent
    IReadOnlyList<string> Scopes,
    string AuthorizeUrl,
    string TokenUrl)
{
    /// <summary>
    /// URL de <c>GET /api/oauth/profile</c>. Opcional: si falta, el cliente la deriva del host de
    /// TokenUrl. Declararla permite apuntar el perfil a otro endpoint sin mover el token.
    /// </summary>
    public string? ProfileUrl { get; init; }

    public string? RolesUrl { get; init; }

    /// <summary>
    /// Construye el User-Agent final reemplazando {version} por la versión dada.
    /// Ejemplo: "claude-cli/2.1.88 (external, cli)" → "omnicore/1.0.0 (windows; x64)".
    /// </summary>
    public string BuildUserAgent(string version) =>
        UserAgentTemplate.Replace("{version}", version);
}
