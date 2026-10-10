using System.Net;
using System.Text;
using System.Text.Json;

namespace OmniCore.Tests;

/// <summary>
/// Handler falso para el token client: registra cada request y devuelve respuestas scripted,
/// igual que ScriptedModelProvider hace con el wire del modelo (CLAUDE.md: priorizar tests
/// deterministas). No abre sockets ni toca Anthropic.
/// </summary>
internal sealed class FakeClaudeOAuthHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _responder = new();
    private readonly List<Recorded> _requests = [];

    public IReadOnlyList<Recorded> Requests => _requests;

    public Recorded LastRequest => _requests.Count > 0
        ? _requests[^1]
        : throw new InvalidOperationException("El cliente no hizo ninguna petición.");

    /// <summary>
    /// Ultima peticion al token endpoint (exchange o refresh), no la ultima en general. Un login
    /// hace dos llamadas —token y perfil— y afirmar contra LastRequest cuando se queria el cuerpo
    /// del exchange da KeyNotFound sobre campos que solo existen alli.
    /// </summary>
    public Recorded LastTokenRequest
    {
        get
        {
            for (var i = _requests.Count - 1; i >= 0; i--)
            {
                if (_requests[i].Body is not null && _requests[i].Json.ContainsKey("grant_type"))
                {
                    return _requests[i];
                }
            }

            throw new InvalidOperationException("No hubo ninguna peticion al token endpoint.");
        }
    }

    public int CallCount => _requests.Count;

    /// <summary>Cola de respuestas, en orden. La última se repite si se agota la cola.</summary>
    public void EnqueueJson(HttpStatusCode status, string json) =>
        _responder.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

    /// <summary>Respuesta diferida: puede mirar el request para decidir (p. ej. afirmar el cuerpo).</summary>
    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        _responder.Enqueue((r, _) => Task.FromResult(responder(r)));

    /// <summary>Lanza al enviar el request (simula fallo de red, no respuesta HTTP).</summary>
    public void EnqueueThrow(Exception exception) => _responder.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(exception));

    /// <summary>
    /// Cuelga hasta que se cancele: simula un endpoint que no responde. Respeta el
    /// CancellationToken, como haría un socket de verdad — si no, el timeout del cliente nunca se
    /// dispara y el test espera a que xUnit se rinda.
    /// </summary>
    public void EnqueueHang() => _responder.Enqueue(async (_, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        throw new System.Diagnostics.UnreachableException();
    });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // Se clonan los datos que interesan: el request real se dispose después.
        var recorded = new Recorded(
            request.Method,
            request.RequestUri!,
            body,
            request.Headers.Authorization?.ToString(),
            TryGetUserAgent(request));
        _requests.Add(recorded);

        var next = _responder.Count > 0 ? _responder.Dequeue() : DefaultResponder;
        return await next(request, cancellationToken).ConfigureAwait(false);
    }

    private static readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> DefaultResponder =
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });

    private static string? TryGetUserAgent(HttpRequestMessage request) =>
        request.Headers.TryGetValues("User-Agent", out var values) ? values.FirstOrDefault() : null;

    internal sealed record Recorded(
        HttpMethod Method,
        Uri Url,
        string? Body,
        string? Authorization,
        string? UserAgent)
    {
        /// <summary>Cuerpo parseado como diccionario: los tests afirman campos concretos.</summary>
        public Dictionary<string, JsonElement> Json => JsonSerializer
            .Deserialize(Body ?? "{}", FakeOAuthWireJsonContext.Default.DictionaryStringJsonElement)!;
    }

}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, JsonElement>))]
internal partial class FakeOAuthWireJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
