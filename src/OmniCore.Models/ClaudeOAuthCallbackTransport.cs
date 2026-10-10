namespace OmniCore.Models;

/// <summary>
/// Petición de callback recibida por el transporte. El path y los query params llegan ya
/// parseados: el transporte es el único que conoce el formato del wire (HTTP, stdin, etc.).
/// </summary>
public sealed record ClaudeOAuthCallbackRequest(
    string Path,
    IReadOnlyDictionary<string, string?> Query);

/// <summary>
/// Respuesta que la lógica del listener pide al transporte que envíe al navegador.
/// </summary>
public sealed record ClaudeOAuthCallbackResponse
{
    private ClaudeOAuthCallbackResponse(int status, string? location, string? body)
    {
        Status = status;
        Location = location;
        Body = body;
    }

    public int Status { get; }
    public string? Location { get; }
    public string? Body { get; }

    public static ClaudeOAuthCallbackResponse Redirect(string location) =>
        new(302, location, null);

    public static ClaudeOAuthCallbackResponse BadRequest(string message) =>
        new(400, null, message);

    public static ClaudeOAuthCallbackResponse NotFound() =>
        new(404, null, null);
}

/// <summary>
/// Transporte del callback OAuth. Aísla la lógica determinista del listener (validación de
/// state, rutas, redirect de éxito) del acceso al SO (sockets, http.sys, stdin).
///
/// Dos implementaciones en producción:
///  - <see cref="HttpListenerCallbackTransport"/>: loopback HTTP, primario.
///  - <see cref="ClaudeOAuthManualCallbackTransport"/>: el usuario pega code#state, fallback.
///
/// Los tests usan un fake en memoria: sin puertos, sin red, deterministas.
/// </summary>
public interface IClaudeOAuthCallbackTransport : IAsyncDisposable
{
    /// <summary>
    /// Puerto efectivo en uso. 0 en transportes que no lo usan (p. ej. manual).
    /// </summary>
    int Port { get; }

    /// <summary>
    /// Empieza a aceptar callbacks. Debe devolver antes de la primera <see cref="AcceptAsync"/>.
    /// </summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Espera la siguiente petición de callback. Cancela con <paramref name="ct"/>.
    /// </summary>
    ValueTask<ClaudeOAuthCallbackRequest> AcceptAsync(CancellationToken ct);

    /// <summary>
    /// Envía la respuesta correspondiente a una petición previamente aceptada.
    /// </summary>
    ValueTask RespondAsync(ClaudeOAuthCallbackRequest request, ClaudeOAuthCallbackResponse response, CancellationToken ct);
}
