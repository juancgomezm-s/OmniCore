using OmniCore.Abstractions;

namespace OmniCore.Models;

/// <summary>Estado observable del login, para que la UI muestre en qué punto está sin conocer el
/// transporte ni los tokens (ADR-0030: presentación leída del mismo estado).</summary>
public enum ClaudeOAuthLoginPhase
{
    Idle,
    PreparingChallenge,
    Listening,
    AwaitingBrowser,
    Exchanging,
    FetchingProfile,
    Persisting,
    Succeeded,
    Failed,
}

/// <summary>Progreso no secreto del login. Lleva la URL de authorize (el usuario tiene que
/// verla) y el puerto, nunca el code, el verifier ni los tokens (INV-016).</summary>
public sealed record ClaudeOAuthLoginProgress(
    ClaudeOAuthLoginPhase Phase,
    string? AuthorizeUrl = null,
    int? Port = null,
    string? MessageKey = null);

/// <summary>Causas de fallo del login, tipadas (spec §71).</summary>
public enum ClaudeOAuthLoginFailure
{
    Cancelled,
    TimedOut,
    TransportUnavailable,
    BrowserUnavailable,
    CallbackRejected,
    ExchangeRejected,
    Network,
    Protocol,
    Storage,
}

/// <summary>Resultado del login. No devuelve credencial ni tokens: el llamador los relee del
/// almacén si los necesita, así que los secretos no cruzan el borde del servicio (INV-016).</summary>
public sealed record ClaudeOAuthLoginResult(
    bool Success,
    ClaudeOAuthLoginFailure? Failure = null,
    string? Detail = null,
    string? EmailAddress = null,
    string? SubscriptionType = null)
{
    public static ClaudeOAuthLoginResult Ok(string? email, string? subscription) =>
        new(true, null, null, email, subscription);

    public static ClaudeOAuthLoginResult Fail(ClaudeOAuthLoginFailure failure, string? detail = null) =>
        new(false, failure, detail);
}

/// <summary>
/// Intento de login sin callback todavía, para el camino manual. El llamador lo conserva en
/// memoria y lo pasa a <see cref="ClaudeOAuthLogin.CompleteManualAsync"/> con el pegado.
///
/// Lleva el verifier y el state, que son material sensible del intento en curso: por eso la
/// clase es sellada, no serializable, y su ToString esta anulado — si acabara en un log o en un
/// artifact filtraría lo necesario para suplantar el intercambio (INV-016, ADR-0018).
/// </summary>
public sealed class ClaudeOAuthPendingLogin
{
    internal ClaudeOAuthPendingLogin(string authorizeUrl, string state, string codeVerifier, int? port)
    {
        AuthorizeUrl = authorizeUrl;
        State = state;
        CodeVerifier = codeVerifier;
        Port = port;
    }

    /// <summary>URL que el usuario debe abrir en el navegador.</summary>
    public string AuthorizeUrl { get; }

    /// <summary>State que la pagina devolvera pegado despues del '#'..</summary>
    public string State { get; }

    /// <summary>Verifier PKCE correspondiente al challenge de la URL.</summary>
    public string CodeVerifier { get; }

    /// <summary>Puerto del listener, o null en el camino puramente manual.</summary>
    public int? Port { get; }

    public override string ToString() => "claude.oauth.pending";
}

/// <summary>Opciones del login. Ninguna cambia la identidad del cliente: eso viene de la
/// configuración del usuario, no de quien invoca.</summary>
public sealed record ClaudeOAuthLoginOptions
{
    /// <summary>Puerto fijo del listener loopback. Null deja que el SO asigne uno.</summary>
    public int? FixedPort { get; init; }

    /// <summary>Intentar abrir el navegador del sistema. Si false, o si falla, se sigue el modo manual.</summary>
    public bool OpenBrowser { get; init; } = true;

    /// <summary>TTL pedido al exchange (token largo). Null deja el del servidor.</summary>
    public int? ExpiresInSeconds { get; init; }

    /// <summary>Cuánto esperar el callback antes de rendirse. Null usa el valor por defecto.</summary>
    public TimeSpan? CallbackTimeout { get; init; }

    /// <summary>Email preseleccionado en el formulario (<c>login_hint</c>).</summary>
    public string? LoginHint { get; init; }

    /// <summary>Método de login preferido (<c>sso</c>|<c>magic_link</c>|<c>google</c>).</summary>
    public string? LoginMethod { get; init; }

    public string? OrganizationUuid { get; init; }

    public static readonly TimeSpan DefaultCallbackTimeout = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Login OAuth de Claude por cuenta. Orquesta las piezas de F1 a F4 en el orden del flujo de
/// estados del plan (§3.2): PKCE -> listener -> authorize -> callback -> exchange -> perfil ->
/// persistir. No decide política ni permisos: solo produce una credencial en el almacén.
///
/// Es agnóstico del transporte: recibe una factoría que construye el listener, así que el mismo
/// código corre en loopback (primario) y en manual (fallback) sin bifurcar el flujo.
/// </summary>
public sealed class ClaudeOAuthLogin
{
    private readonly ClaudeOAuthClientIdentity _identity;
    private readonly ClaudeOAuthTokenClient _tokens;
    private readonly ClaudeOAuthCredentialStore _credentials;
    private readonly IBrowserLauncher? _browser;
    private readonly Func<CancellationToken, Task<IClaudeOAuthCallbackTransport>> _transportFactory;
    private readonly Action<ClaudeOAuthLoginProgress>? _progress;
    private readonly Func<DateTimeOffset> _utcNow;

    public ClaudeOAuthLogin(
        ClaudeOAuthClientIdentity identity,
        ClaudeOAuthTokenClient tokens,
        ClaudeOAuthCredentialStore credentials,
        Func<CancellationToken, Task<IClaudeOAuthCallbackTransport>> transportFactory,
        IBrowserLauncher? browser = null,
        Action<ClaudeOAuthLoginProgress>? progress = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _browser = browser;
        _progress = progress;
        _utcNow = utcNow ?? DefaultUtcNow;
    }

    private static DateTimeOffset DefaultUtcNow() => DateTimeOffset.UtcNow;

    /// <summary>
    /// Flujo completo por loopback: levanta el listener, abre el navegador, espera el callback e
    /// intercambia. Si el navegador no está disponible, devuelve <see cref="ClaudeOAuthLoginFailure.BrowserUnavailable"/>
    /// con la URL en <see cref="ClaudeOAuthLoginResult.Detail"/> para que el cliente ofrezca el
    /// pegado manual — no se queda esperando un redirect que no va a llegar.
    /// </summary>
    public Task<ClaudeOAuthLoginResult> LoginAsync(
        string secretRef,
        ClaudeOAuthLoginOptions options,
        CancellationToken ct)
        => RunLoopbackAsync(secretRef, options, ct);

    /// <summary>
    /// Primer tramo del camino manual: prepara PKCE y devuelve la URL sin levantar ningun listener.
    /// El usuario abre la URL a mano y la pagina de Anthropic le muestra CODE#STATE para pegar.
    /// No hay espera ni timeout de callback porque no hay socket que escuchar.
    /// </summary>
    public Task<ClaudeOAuthPendingLogin> BeginManualAsync(
        ClaudeOAuthLoginOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        var verifier = ClaudeOAuthPkce.GenerateCodeVerifier();
        var challenge = ClaudeOAuthPkce.GenerateCodeChallenge(verifier);
        var state = ClaudeOAuthPkce.GenerateState();
        var authorizeUrl = ClaudeOAuthAuthorizeUrlBuilder.Build(
            _identity, challenge, state, port: 0, isManual: true,
            orgUuid: options.OrganizationUuid,
            loginHint: options.LoginHint,
            loginMethod: options.LoginMethod);

        Report(new ClaudeOAuthLoginProgress(
            ClaudeOAuthLoginPhase.AwaitingBrowser, authorizeUrl, null, "claude.oauth.awaiting_browser_manual"));
        return Task.FromResult(new ClaudeOAuthPendingLogin(authorizeUrl, state, verifier, port: null));
    }

    private async Task<ClaudeOAuthLoginResult> RunLoopbackAsync(
        string secretRef,
        ClaudeOAuthLoginOptions options,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);
        ArgumentNullException.ThrowIfNull(options);

        var verifier = ClaudeOAuthPkce.GenerateCodeVerifier();
        var challenge = ClaudeOAuthPkce.GenerateCodeChallenge(verifier);
        var state = ClaudeOAuthPkce.GenerateState();
        Report(ClaudeOAuthLoginPhase.PreparingChallenge);

        IClaudeOAuthCallbackTransport transport;
        ClaudeOAuthLoopbackListener listener;
        try
        {
            transport = await _transportFactory(ct).ConfigureAwait(false);
            listener = new ClaudeOAuthLoopbackListener(transport);
            await listener.StartAsync(ct).ConfigureAwait(false);
        }
        catch (OAuthTransportException ex)
        {
            Report(ClaudeOAuthLoginPhase.Failed, messageKey: "claude.oauth.failed.transport");
            return ClaudeOAuthLoginResult.Fail(ClaudeOAuthLoginFailure.TransportUnavailable, ex.Message);
        }
        catch (OperationCanceledException)
        {
            Report(ClaudeOAuthLoginPhase.Failed, messageKey: "claude.oauth.failed.cancelled");
            return ClaudeOAuthLoginResult.Fail(ClaudeOAuthLoginFailure.Cancelled);
        }

        await using (listener)
        {
            var port = transport.Port;
            var authorizeUrl = ClaudeOAuthAuthorizeUrlBuilder.Build(
                _identity, challenge, state, port,
                orgUuid: options.OrganizationUuid,
                loginHint: options.LoginHint,
                loginMethod: options.LoginMethod);

            Report(new ClaudeOAuthLoginProgress(ClaudeOAuthLoginPhase.Listening, authorizeUrl, port));

            if (options.OpenBrowser && _browser is not null)
            {
                var launched = await _browser
                    .LaunchAsync(new Uri(authorizeUrl), ct)
                    .ConfigureAwait(false);
                if (!launched.Launched)
                {
                    // La URL se devuelve en el detalle: es pública (no lleva secrets) y el usuario
                    // puede abrirla a mano. El state y el verifier NO se devuelven nunca.
                    Report(ClaudeOAuthLoginPhase.Failed, authorizeUrl, port, "claude.oauth.failed.browser");
                    return ClaudeOAuthLoginResult.Fail(
                        ClaudeOAuthLoginFailure.BrowserUnavailable,
                        authorizeUrl);
                }
            }

            Report(new ClaudeOAuthLoginProgress(
                ClaudeOAuthLoginPhase.AwaitingBrowser, authorizeUrl, port, "claude.oauth.awaiting_browser"));

            string code;
            try
            {
                using var callbackTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                callbackTimeout.CancelAfter(options.CallbackTimeout ?? ClaudeOAuthLoginOptions.DefaultCallbackTimeout);
                code = await listener.WaitForAuthorizationAsync(state, callbackTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OAuthCallbackException ex)
            {
                Report(ClaudeOAuthLoginPhase.Failed, messageKey: "claude.oauth.failed.callback");
                return ClaudeOAuthLoginResult.Fail(ClaudeOAuthLoginFailure.CallbackRejected, ex.Message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Report(ClaudeOAuthLoginPhase.Failed, messageKey: "claude.oauth.failed.cancelled");
                return ClaudeOAuthLoginResult.Fail(ClaudeOAuthLoginFailure.Cancelled);
            }
            catch (OperationCanceledException)
            {
                Report(ClaudeOAuthLoginPhase.Failed, messageKey: "claude.oauth.failed.timeout");
                return ClaudeOAuthLoginResult.Fail(ClaudeOAuthLoginFailure.TimedOut);
            }

            return await CompleteAsync(secretRef, code, state, verifier,
                redirectUri: $"http://localhost:{port}/callback",
                expiresInSeconds: options.ExpiresInSeconds, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Continúa un login empezado a mano: el usuario abrió la URL (se le dio con
    /// <see cref="ClaudeOAuthLoginOptions.OpenBrowser"/> false o tras un
    /// <see cref="ClaudeOAuthLoginFailure.BrowserUnavailable"/>) y pegó <c>CODE#STATE</c>.
    /// El verifier tiene que ser el mismo del intento original, así que lo guarda el llamador.
    /// </summary>
    public Task<ClaudeOAuthLoginResult> CompleteManualAsync(
        string secretRef,
        string codeAndState,
        ClaudeOAuthPendingLogin pending,
        ClaudeOAuthLoginOptions options,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeAndState);
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(options);

        var hash = codeAndState.IndexOf('#');
        if (hash <= 0 || hash == codeAndState.Length - 1)
        {
            return Task.FromResult(ClaudeOAuthLoginResult.Fail(
                ClaudeOAuthLoginFailure.CallbackRejected,
                "claude.oauth.manual.bad_format"));
        }

        var code = codeAndState[..hash].Trim();
        var state = codeAndState[(hash + 1)..].Trim();
        if (!string.Equals(state, pending.State, StringComparison.Ordinal))
        {
            return Task.FromResult(ClaudeOAuthLoginResult.Fail(
                ClaudeOAuthLoginFailure.CallbackRejected,
                "claude.oauth.callback.state_mismatch"));
        }

        return CompleteAsync(secretRef, code, state, pending.CodeVerifier,
            redirectUri: ClaudeOAuthAuthorizeUrlBuilder.ManualRedirectUri,
            expiresInSeconds: options.ExpiresInSeconds, ct);
    }

    private async Task<ClaudeOAuthLoginResult> CompleteAsync(
        string secretRef,
        string code,
        string state,
        string verifier,
        string redirectUri,
        int? expiresInSeconds,
        CancellationToken ct)
    {
        ClaudeOAuthTokens tokens;
        try
        {
            Report(ClaudeOAuthLoginPhase.Exchanging, messageKey: "claude.oauth.exchanging");
            tokens = await _tokens
                .ExchangeCodeAsync(code, state, verifier, redirectUri, expiresInSeconds, ct)
                .ConfigureAwait(false);
        }
        catch (ClaudeOAuthExchangeInvalidCodeException ex)
        {
            Report(ClaudeOAuthLoginPhase.Failed, messageKey: "claude.oauth.failed.exchange_invalid");
            return ClaudeOAuthLoginResult.Fail(ClaudeOAuthLoginFailure.ExchangeRejected, ex.Message);
        }
        catch (ClaudeOAuthException ex) when (ex is ClaudeOAuthExchangeHttpException
            or ClaudeOAuthNetworkException or ClaudeOAuthProtocolException)
        {
            Report(ClaudeOAuthLoginPhase.Failed, messageKey: "claude.oauth.failed.exchange");
            return ClassifyExchangeFailure(ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Report(ClaudeOAuthLoginPhase.Failed, messageKey: "claude.oauth.failed.cancelled");
            return ClaudeOAuthLoginResult.Fail(ClaudeOAuthLoginFailure.Cancelled);
        }

        ClaudeOAuthProfile? profile = null;
        if (tokens.Scopes.Contains(ClaudeOAuthScopes.Profile, StringComparer.Ordinal))
        {
            // El perfil exige scope user:profile; un token de solo inferencia daría 403 (plan §2.8).
            Report(ClaudeOAuthLoginPhase.FetchingProfile, messageKey: "claude.oauth.fetching_profile");
            profile = await _tokens.FetchProfileAsync(tokens.AccessToken, ct).ConfigureAwait(false);
        }

        try
        {
            Report(ClaudeOAuthLoginPhase.Persisting, messageKey: "claude.oauth.persisting");

            // La identidad de la cuenta viene del perfil, no del wire del token (plan §2.9).
            var withIdentity = tokens with
            {
                AccountUuid = profile?.AccountUuid ?? tokens.AccountUuid,
                OrganizationUuid = profile?.OrganizationUuid ?? tokens.OrganizationUuid,
            };

            var credential = ClaudeOAuthCredential.FromTokens(withIdentity, _utcNow()) with
            {
                EmailAddress = profile?.EmailAddress,
                DisplayName = profile?.DisplayName,
                SubscriptionType = profile?.SubscriptionType,
                RateLimitTier = profile?.RateLimitTier,
            };

            _credentials.Save(secretRef, credential, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Report(ClaudeOAuthLoginPhase.Failed, messageKey: "claude.oauth.failed.cancelled");
            return ClaudeOAuthLoginResult.Fail(ClaudeOAuthLoginFailure.Cancelled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or OmniCore.Domain.SecretValueTooShortException)
        {
            Report(ClaudeOAuthLoginPhase.Failed, messageKey: "claude.oauth.failed.storage");
            return ClaudeOAuthLoginResult.Fail(ClaudeOAuthLoginFailure.Storage, ex.GetType().Name);
        }

        Report(new ClaudeOAuthLoginProgress(ClaudeOAuthLoginPhase.Succeeded, null, null, "claude.oauth.succeeded"));
        return ClaudeOAuthLoginResult.Ok(profile?.EmailAddress, profile?.SubscriptionType);
    }

    private static ClaudeOAuthLoginResult ClassifyExchangeFailure(ClaudeOAuthException ex) => ex switch
    {
        ClaudeOAuthNetworkException => ClaudeOAuthLoginResult.Fail(ClaudeOAuthLoginFailure.Network, ex.Message),
        ClaudeOAuthProtocolException => ClaudeOAuthLoginResult.Fail(ClaudeOAuthLoginFailure.Protocol, ex.Message),
        _ => ClaudeOAuthLoginResult.Fail(ClaudeOAuthLoginFailure.ExchangeRejected, ex.Message),
    };

    private void Report(ClaudeOAuthLoginPhase phase, string? messageKey = null) =>
        _progress?.Invoke(new ClaudeOAuthLoginProgress(phase, null, null, messageKey));

    private void Report(ClaudeOAuthLoginPhase phase, string authorizeUrl, int port, string messageKey) =>
        _progress?.Invoke(new ClaudeOAuthLoginProgress(phase, authorizeUrl, port, messageKey));

    private void Report(ClaudeOAuthLoginProgress progress) => _progress?.Invoke(progress);
}
