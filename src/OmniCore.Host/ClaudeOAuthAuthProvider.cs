using OmniCore.Abstractions;
using OmniCore.Models;

namespace OmniCore.Host;

/// <summary>
/// Fuente de Bearer que consume <see cref="AnthropicMessagesProvider"/> cuando el provider
/// autentica por cuenta (ADR-0011 §3.3). Es el borde entre el adaptador y la máquina de refresco:
/// el provider ve un token vigente o null, nunca el almacén cifrado ni el coordinador (INV-008).
///
/// Todo lo sensible se queda dentro: los métodos devuelven <see cref="ClaudeOAuthBearer"/>, que no
/// lleva refresh token, y ese tipo tampoco se serializa ni se loguea (INV-016, ADR-0018).
/// </summary>
public sealed class ClaudeOAuthAuthProvider : IClaudeOAuthCredentialSource
{
    private readonly string _secretRef;
    private readonly ClaudeOAuthCredentialStore _credentials;
    private readonly ClaudeOAuthRefreshCoordinator _refresh;

    public ClaudeOAuthAuthProvider(
        string secretRef,
        ClaudeOAuthCredentialStore credentials,
        ClaudeOAuthRefreshCoordinator refresh)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);
        _secretRef = secretRef;
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
    }

    /// <summary>Clave del almacén bajo la que vive esta sesión.</summary>
    public string SecretRef => _secretRef;

    /// <summary>
    /// Bearer vigente, refrescando si caduca dentro del margen. Null si no hay sesión o si el
    /// refresh token está muerto: el provider lo traduce a AuthenticationFailed.
    /// </summary>
    public ValueTask<ClaudeOAuthBearer?> GetAsync(CancellationToken cancellationToken) =>
        BearerAsync(_refresh.EnsureFreshAsync(_secretRef, cancellationToken), cancellationToken);

    private static async ValueTask<ClaudeOAuthBearer?> BearerAsync(
        System.Threading.Tasks.Task<ClaudeOAuthRefreshOutcome> task,
        CancellationToken ct)
    {
        var outcome = await task.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return ToBearer(outcome.Tokens);
    }

    /// <summary>
    /// Fuerza un refresco tras un 401. Aquí se salta el margen de expiración: el servidor ya dijo
    /// que el token no vale, así que esperar a que caduque es inútil (plan §7, conOAuth401Retry en
    /// http.ts:74+ usa force=true por la misma razón).
    /// </summary>
    public async ValueTask<ClaudeOAuthBearer?> ForceRefreshAsync(CancellationToken cancellationToken)
    {
        var credential = Load(cancellationToken);
        if (credential is null)
        {
            return null;
        }

        if (_refresh.IsDead(credential.RefreshToken))
        {
            return null;
        }

        try
        {
            // El coordinador vuelve a comprobar el mtime y el dead-set tras el lock por su cuenta;
            // aqui solo se asegura de que haya un refresco real, no uno evitado por frescura.
            var tokens = await _refresh
                .ForceRefreshAsync(_secretRef, credential, cancellationToken)
                .ConfigureAwait(false);
            return ToBearer(tokens);
        }
        catch (ClaudeOAuthInvalidGrantException)
        {
            return null;
        }
        catch (ClaudeOAuthException)
        {
            // Transitorio (red, 5xx): no se convierte en "sesión muerta", porque eso borraría una
            // sesión recuperable y obligaría a un /login de más. Se propaga para que el llamador
            // distinga retryable de fatal.
            throw;
        }
    }

    /// <summary>Estado no secreto de la sesión, para /doctor y la status line (ADR-0031).</summary>
    public async System.Threading.Tasks.Task<ClaudeOAuthSessionStatus> InspectAsync(CancellationToken cancellationToken)
    {
        var info = _credentials.ReadAccountInfo(_secretRef, cancellationToken);
        var present = _credentials.Exists(_secretRef, cancellationToken);
        if (!present)
        {
            return ClaudeOAuthSessionStatus.None;
        }

        var dead = IsDead(cancellationToken);
        return new ClaudeOAuthSessionStatus(
            SignedIn: true,
            Dead: dead,
            EmailAddress: info?.EmailAddress,
            DisplayName: info?.DisplayName,
            SubscriptionType: info?.SubscriptionType,
            RateLimitTier: info?.RateLimitTier,
            Scopes: info?.Scopes ?? [],
            ExpiresAt: info?.ExpiresAt,
            AuthenticatedAt: info?.AuthenticatedAt);
    }

    /// <summary>Cierra la sesión: borra credencial y metadata, y olvida el dead-set (logout).</summary>
    public void SignOut(CancellationToken cancellationToken)
    {
        _credentials.Delete(_secretRef, cancellationToken);
        _refresh.ClearDeadSet();
    }

    private bool IsDead(CancellationToken ct)
    {
        var credential = Load(ct);
        return credential is not null && _refresh.IsDead(credential.RefreshToken);
    }

    private ClaudeOAuthCredential? Load(CancellationToken ct) => _credentials.Load(_secretRef, ct);

    private static ClaudeOAuthBearer? ToBearer(ClaudeOAuthTokens? tokens) =>
        tokens is null ? null : new ClaudeOAuthBearer(tokens.AccessToken, tokens.AccountUuid, tokens.Scopes);
}

/// <summary>
/// Estado de la sesión de Claude legible sin secretos. Cada campo nullable significa "no
/// informado", nunca estimado: la UI muestra `—` con eso (ADR-0031).
/// </summary>
public sealed record ClaudeOAuthSessionStatus(
    bool SignedIn,
    bool Dead,
    string? EmailAddress,
    string? DisplayName,
    string? SubscriptionType,
    string? RateLimitTier,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? AuthenticatedAt)
{
    public static readonly ClaudeOAuthSessionStatus None = new(false, false, null, null, null, null, [], null, null);

    /// <summary>Máscara del access token para diagnóstico, sin revelarlo.</summary>
    public bool HasInferenceScope => Scopes.Contains(ClaudeOAuthScopes.Inference, StringComparer.Ordinal);
}
