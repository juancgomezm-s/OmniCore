using OmniCore.Models;

namespace OmniCore.Tests;

/// <summary>
/// Transporte falso en memoria: sin puertos, sin sockets, sin http.sys. Alimenta peticiones de
/// callback directamente y registra las respuestas, igual que ScriptedModelProvider hace con el
/// wire del modelo (CLAUDE.md: priorizar tests deterministas).
/// </summary>
internal sealed class FakeClaudeOAuthCallbackTransport : IClaudeOAuthCallbackTransport
{
    private readonly Queue<ClaudeOAuthCallbackRequest> _incoming = new();
    private readonly TaskCompletionSource<bool> _item = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<(ClaudeOAuthCallbackRequest Request, ClaudeOAuthCallbackResponse Response)> _responses = [];
    private bool _started;

    public int Port { get; set; } = 54321;

    public IReadOnlyList<(ClaudeOAuthCallbackRequest Request, ClaudeOAuthCallbackResponse Response)> Responses => _responses;

    public ClaudeOAuthCallbackResponse LastResponse => _responses.Count > 0
        ? _responses[^1].Response
        : throw new InvalidOperationException("El listener no respondió a ninguna petición.");

    public void Enqueue(string path, params (string Key, string? Value)[] query)
    {
        var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in query)
        {
            dict[key] = value;
        }

        _incoming.Enqueue(new ClaudeOAuthCallbackRequest(path, dict));
        _item.TrySetResult(true);
    }

    public Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _started = true;
        return Task.CompletedTask;
    }

    public bool Started => _started;

    public async ValueTask<ClaudeOAuthCallbackRequest> AcceptAsync(CancellationToken ct)
    {
        while (_incoming.Count == 0)
        {
            using var linked = ct.Register(() => _item.TrySetCanceled(ct));
            await _item.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        return _incoming.Dequeue();
    }

    public ValueTask RespondAsync(
        ClaudeOAuthCallbackRequest request,
        ClaudeOAuthCallbackResponse response,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _responses.Add((request, response));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Transporte que se auto-responde: lee el state y el redirect_uri de la URL de authorize que el
/// propio login publica en el progreso, y devuelve un callback con ese state. Es el equivalente a
/// lo que hace un navegador real en el camino loopback, sin navegador.
///
/// Existe porque el state y el verifier son deliberadamente invisibles desde fuera del login
/// (INV-016): si los tests tuvieran que sacarlos por otra vía, estarían probando una fuga.
/// </summary>
internal sealed class AutoAuthorizingCallbackTransport : IClaudeOAuthCallbackTransport
{
    private readonly TaskCompletionSource<ClaudeOAuthCallbackRequest> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ClaudeOAuthCallbackResponse? _response;
    private int _port = 41234;

    /// <summary>Code que se devuelve en el callback.</summary>
    public string AuthorizationCode { get; set; } = "the-auth-code";

    public ClaudeOAuthCallbackResponse? Response => _response;

    public int Port => Volatile.Read(ref _port);

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Se llama con la URL de authorize que el login anunció. Con ella se construye el callback
    /// exacto que mandaría Anthropic: mismo state y mismo redirect_uri.
    /// </summary>
    public void AnnounceAuthorizeUrl(string authorizeUrl)
    {
        var query = ParseQuery(authorizeUrl);
        if (!query.TryGetValue("state", out var state))
        {
            throw new InvalidOperationException("La URL de authorize no trae state.");
        }

        _request.TrySetResult(new ClaudeOAuthCallbackRequest(
            "/callback",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["code"] = AuthorizationCode,
                ["state"] = state,
            }));
    }

    public void AnnouncePort(int port) => Interlocked.Exchange(ref _port, port);

    public async ValueTask<ClaudeOAuthCallbackRequest> AcceptAsync(CancellationToken ct) =>
        await _request.Task.WaitAsync(ct).ConfigureAwait(false);

    public ValueTask RespondAsync(
        ClaudeOAuthCallbackRequest request,
        ClaudeOAuthCallbackResponse response,
        CancellationToken ct)
    {
        _response = response;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Dictionary<string, string> ParseQuery(string url)
    {
        var marker = url.IndexOf('?');
        var query = marker >= 0 ? url[(marker + 1)..] : string.Empty;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0)
            {
                result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return result;
    }
}
