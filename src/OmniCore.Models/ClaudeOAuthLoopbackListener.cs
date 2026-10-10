namespace OmniCore.Models;

/// <summary>
/// Máquina de estados del callback OAuth sobre un <see cref="IClaudeOAuthCallbackTransport"/>.
/// Contiene toda la lógica del AuthCodeListener de Claude Code
/// (packages/provider/src/oauth/auth-code-listener.ts) salvo el acceso al SO, que queda en el
/// transporte. Es determinista: los tests inyectan peticiones directamente.
/// </summary>
public sealed class ClaudeOAuthLoopbackListener
{
    /// <summary>Ruta que acepta el callback. Cualquier otra devuelve 404.</summary>
    public const string CallbackPath = "/callback";

    /// <summary>
    /// Página a la que se redirige al navegador tras capturar el code. Equivalente a
    /// CLAUDEAI_SUCCESS_URL en oauthConstants.ts:98-99.
    /// </summary>
    public const string SuccessRedirectUrl =
        "https://platform.claude.com/oauth/code/success?app=claude-code";

    private readonly IClaudeOAuthCallbackTransport _transport;

    public ClaudeOAuthLoopbackListener(IClaudeOAuthCallbackTransport transport)
    {
        _transport = transport;
    }

    /// <summary>Puerto efectivo del transporte (0 si no aplica).</summary>
    public int Port => _transport.Port;

    public Task StartAsync(CancellationToken ct) => _transport.StartAsync(ct);

    /// <summary>
    /// Decide la respuesta a una petición de callback y la envía. Devuelve el authorization code
    /// solo cuando la petición es válida; para rutas no-callback devuelve null y sigue.
    /// </summary>
    /// <exception cref="OAuthCallbackException">code ausente o state distinto del esperado.</exception>
    public async Task<string?> HandleAsync(
        ClaudeOAuthCallbackRequest request,
        string expectedState,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!PathsEqual(request.Path, CallbackPath))
        {
            await _transport.RespondAsync(request, ClaudeOAuthCallbackResponse.NotFound(), ct)
                .ConfigureAwait(false);
            return null;
        }

        request.Query.TryGetValue("code", out var code);
        request.Query.TryGetValue("state", out var state);

        if (string.IsNullOrEmpty(code))
        {
            await _transport.RespondAsync(request,
                    ClaudeOAuthCallbackResponse.BadRequest("Authorization code not found"), ct)
                .ConfigureAwait(false);
            throw new OAuthCallbackException(OAuthCallbackFailure.NoCode);
        }

        // Comparación ordinal y por longitud constante: el state no es secreto entre
        // adversarios locales, pero no queremos filtrar información por timing ni por locale.
        if (!FixedTimeEquals(state, expectedState))
        {
            await _transport.RespondAsync(request,
                    ClaudeOAuthCallbackResponse.BadRequest("Invalid state parameter"), ct)
                .ConfigureAwait(false);
            throw new OAuthCallbackException(OAuthCallbackFailure.StateMismatch);
        }

        await _transport.RespondAsync(request,
                ClaudeOAuthCallbackResponse.Redirect(SuccessRedirectUrl), ct)
            .ConfigureAwait(false);

        return code;
    }

    /// <summary>
    /// Acepta callbacks hasta obtener uno válido, o hasta que se cancele.
    /// Las peticiones a otras rutas se responden con 404 y se ignoran: el navegador puede pedir
    /// /favicon.ico u otros recursos mientras espera el redirect.
    /// </summary>
    public async Task<string> WaitForAuthorizationAsync(
        string expectedState,
        CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var request = await _transport.AcceptAsync(ct).ConfigureAwait(false);
            var code = await HandleAsync(request, expectedState, ct).ConfigureAwait(false);
            if (code is not null)
            {
                return code;
            }
        }
    }

    private static bool PathsEqual(string a, string b) =>
        a.Length == b.Length && string.Equals(a, b, StringComparison.Ordinal);

    private static bool FixedTimeEquals(string? a, string b)
    {
        if (a is null || a.Length != b.Length)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }

        return diff == 0;
    }

    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}

/// <summary>Causas de rechazo de un callback, tipadas (spec §71).</summary>
public enum OAuthCallbackFailure
{
    /// <summary>El redirect llegó sin el parámetro <c>code</c>.</summary>
    NoCode,

    /// <summary>El <c>state</c> recibido no coincide con el esperado (posible CSRF).</summary>
    StateMismatch,
}

/// <summary>
/// Un callback fue rechazado. No lleva el code ni el state: pueden contener material sensible
/// y los secretos nunca llegan a logs ni artefactos (INV-016, ADR-0018).
/// </summary>
public sealed class OAuthCallbackException : ClaudeOAuthException
{
    public OAuthCallbackException(OAuthCallbackFailure failure)
        : base(failure == OAuthCallbackFailure.NoCode
            ? "El callback OAuth no traía authorization code."
            : "El state del callback OAuth no coincide con el esperado.")
    {
        Failure = failure;
    }

    public OAuthCallbackFailure Failure { get; }
}
