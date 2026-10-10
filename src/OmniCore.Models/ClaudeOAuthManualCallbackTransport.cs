namespace OmniCore.Models;

/// <summary>
/// Transporte para el modo manual: el navegador no puede volver a localhost, así que la página
/// de Anthropic muestra <c>AUTHORIZATION_CODE#STATE</c> y el usuario lo pega.
/// Equivalente al MANUAL_REDIRECT_URL + parsing por '#' de auth-code-listener.ts.
///
/// No escucha en ningún puerto: Port es 0 y la URL de authorize usa MANUAL_REDIRECT_URL.
/// La respuesta va al texto que ve el usuario, no a un socket.
/// </summary>
public sealed class ClaudeOAuthManualCallbackTransport : IClaudeOAuthCallbackTransport
{
    /// <summary>Ruta sintética: el callback manual no tiene path real.</summary>
    public const string ManualPath = "/callback";

    private readonly Func<CancellationToken, Task<string>> _readInput;
    private ClaudeOAuthCallbackRequest? _pending;
    private string? _lastBody;
    private int _status;
    private string? _location;

    /// <param name="readInput">
    /// Fuente del pegado. Inyectada para que los tests no lean stdin ni pidan al usuario.
    /// </param>
    public ClaudeOAuthManualCallbackTransport(Func<CancellationToken, Task<string>> readInput)
    {
        _readInput = readInput ?? throw new ArgumentNullException(nameof(readInput));
    }

    public int Port => 0;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async ValueTask<ClaudeOAuthCallbackRequest> AcceptAsync(CancellationToken ct)
    {
        var raw = (await _readInput(ct).ConfigureAwait(false)).Trim();

        // Formato AUTHORIZATION_CODE#STATE. El code puede traer query params pegados si el
        // usuario copia la URL entera en vez del valor: se quedan fuera al primer carácter
        // que no cabe en un code OAuth.
        var hash = raw.IndexOf('#');
        var code = hash >= 0 ? raw[..hash] : raw;
        var state = hash >= 0 ? raw[(hash + 1)..] : null;

        _pending = new ClaudeOAuthCallbackRequest(ManualPath, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["code"] = code,
            ["state"] = state,
        });

        return _pending;
    }

    public ValueTask RespondAsync(
        ClaudeOAuthCallbackRequest request,
        ClaudeOAuthCallbackResponse response,
        CancellationToken ct)
    {
        if (!ReferenceEquals(request, _pending))
        {
            throw new InvalidOperationException("La petición no es la aceptada por este transporte.");
        }

        _status = response.Status;
        _location = response.Location;
        _lastBody = response.Body;
        return ValueTask.CompletedTask;
    }

    /// <summary>Estado de la última respuesta, para mostrarlo al usuario o assertear en tests.</summary>
    public (int Status, string? Location, string? Body) LastResponse => (_status, _location, _lastBody);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
