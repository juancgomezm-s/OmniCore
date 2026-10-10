using System.Collections.Concurrent;
using System.Net;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;

namespace OmniCore.Tests;

/// <summary>
/// Tests del camino de cuenta en el adaptador Messages (§2.8 y §7 del plan, grupo 8). El wire se
/// guiona con QueueHandler: sin red, sin login real (CLAUDE.md: priorizar tests deterministas).
/// </summary>
public sealed class AnthropicMessagesProviderOAuthTests
{
    private const string BetaHeader = "oauth-2025-04-20";

    [Fact]
    public async System.Threading.Tasks.Task A_subscriber_request_carries_bearer_beta_and_protection()
    {
        var handler = new ScriptedHandler(_ => Sse(MinimalStream()));
        var oauth = new FakeCredentialSource(new ClaudeOAuthBearer("at-live", "acc-1",
            ["user:profile", "user:inference"]));
        var provider = CreateProvider(handler, oauth: oauth);

        await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        var headers = Assert.Single(handler.Requests).Headers;
        Assert.Equal("Bearer at-live", headers["Authorization"]);
        Assert.Equal(BetaHeader, headers["anthropic-beta"]);
        Assert.Equal("true", headers["x-anthropic-additional-protection"]);
        Assert.Equal("acc-1", headers["x-account-uuid"]);
        Assert.False(headers.ContainsKey("x-api-key"), "el camino de cuenta no manda API key");
    }

    [Fact]
    public async System.Threading.Tasks.Task An_api_key_request_still_carries_x_api_key_and_no_bearer()
    {
        // Regresion: el camino API key (ADR-0011 §3) sigue igual despues de anadirle el de cuenta.
        var handler = new ScriptedHandler(_ => Sse(MinimalStream()));
        var provider = CreateProvider(handler, auth: AuthConfig.ApiKey("anthropic-key"),
            secrets: new FixedSecrets("sk-secret"));

        await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        var headers = Assert.Single(handler.Requests).Headers;
        Assert.Equal("sk-secret", headers["x-api-key"]);
        Assert.False(headers.ContainsKey("Authorization"));
        Assert.False(headers.ContainsKey("anthropic-beta"));
    }

    [Fact]
    public async System.Threading.Tasks.Task The_user_agent_comes_from_the_identity_with_the_version_filled()
    {
        var handler = new ScriptedHandler(_ => Sse(MinimalStream()));
        var provider = CreateProvider(handler,
            oauth: new FakeCredentialSource(Bearer()),
            options: new AnthropicProviderOptions
            {
                OAuthUserAgentTemplate = "claude-cli/{version} (external, cli)",
                ClientVersion = "9.9.9",
            });

        await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal("claude-cli/9.9.9 (external, cli)", Assert.Single(handler.Requests).Headers["User-Agent"]);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_401_forces_one_refresh_and_retries_once()
    {
        var handler = new ScriptedHandler(
            _ => Error(HttpStatusCode.Unauthorized, "token expirado"),
            _ => Sse(MinimalStream()));
        var oauth = new FakeCredentialSource(Bearer("at-old"), Bearer("at-new"));

        var provider = CreateProvider(handler, oauth: oauth);
        var events = await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        Assert.IsType<ResponseCompleted>(events[^1]);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Bearer at-old", handler.Requests[0].Headers["Authorization"]);
        Assert.Equal("Bearer at-new", handler.Requests[1].Headers["Authorization"]);
        Assert.Equal(1, oauth.ForceRefreshCalls);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_second_401_surfaces_as_AuthenticationFailed()
    {
        var handler = new ScriptedHandler(
            _ => Error(HttpStatusCode.Unauthorized, "no"),
            _ => Error(HttpStatusCode.Unauthorized, "tampoco"));
        var oauth = new FakeCredentialSource(Bearer(), Bearer("at-second"));
        var provider = CreateProvider(handler, oauth: oauth);

        var ex = await Assert.ThrowsAsync<ModelProviderException>(() =>
            Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Equal("AuthenticationFailed", ex.Kind);
        // Un solo refuerzo forzado: el segundo 401 ya no vuelve a refrescar, o un token muerto
        // generaria una tormenta de RPC contra el endpoint.
        Assert.Equal(1, oauth.ForceRefreshCalls);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_dead_session_does_not_retry_at_all()
    {
        // ForceRefresh devuelve null cuando la sesion ya no es recuperable (logout, token muerto):
        // reintentar gastaria un request para nada y taparia el mensaje de "vuelve a loguearte".
        var handler = new ScriptedHandler(
            _ => Error(HttpStatusCode.Unauthorized, "no"),
            _ => Sse(MinimalStream()));
        var oauth = new FakeCredentialSource(Bearer()) { Recoverable = false };
        var provider = CreateProvider(handler, oauth: oauth);

        var ex = await Assert.ThrowsAsync<ModelProviderException>(() =>
            Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Equal("AuthenticationFailed", ex.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async System.Threading.Tasks.Task No_session_fails_before_touching_the_network()
    {
        var handler = new ScriptedHandler(_ => Sse(MinimalStream()));
        var provider = CreateProvider(handler, oauth: new FakeCredentialSource());

        var ex = await Assert.ThrowsAsync<ModelProviderException>(() =>
            Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));

        Assert.Equal("AuthenticationFailed", ex.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async System.Threading.Tasks.Task GetAsync_is_called_once_per_successful_turn()
    {
        var handler = new ScriptedHandler(_ => Sse(MinimalStream()));
        var oauth = new FakeCredentialSource(Bearer());
        var provider = CreateProvider(handler, oauth: oauth);

        await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(1, oauth.GetCalls);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_token_without_inference_scope_still_uses_the_bearer_path()
    {
        // Lo que decide la cabecera es la credencial efectiva, no el modelo ni el nombre del
        // provider (INV-007). Un token de solo inferencia sigue siendo Bearer.
        var handler = new ScriptedHandler(_ => Sse(MinimalStream()));
        var provider = CreateProvider(handler,
            oauth: new FakeCredentialSource(new ClaudeOAuthBearer("at", null, ["user:inference"])));

        await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal("Bearer at", Assert.Single(handler.Requests).Headers["Authorization"]);
    }

    [Fact]
    public void An_oauth_source_on_a_non_oauth_descriptor_is_rejected()
    {
        // Un descriptor API key con fuente OAuth nunca refrescaria nada: el 401 se silenciaria.
        Assert.Throws<ArgumentException>(() => new AnthropicMessagesProvider(
            new ProviderDescriptor("anthropic", ProviderFamily.AnthropicMessages, "https://api.example.test",
                AuthConfig.ApiKey("k"), false, false, true),
            new FixedSecrets("sk"),
            () => new HttpClient(new ScriptedHandler(_ => Sse(MinimalStream())), disposeHandler: false),
            oauth: new FakeCredentialSource(Bearer())));
    }

    [Fact]
    public async System.Threading.Tasks.Task The_beta_header_is_configurable_and_can_be_switched_off()
    {
        var withBeta = new ScriptedHandler(_ => Sse(MinimalStream()));
        await Collect(CreateProvider(withBeta, oauth: new FakeCredentialSource(Bearer()))
            .StreamAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(BetaHeader, Assert.Single(withBeta.Requests).Headers["anthropic-beta"]);

        var withoutBeta = new ScriptedHandler(_ => Sse(MinimalStream()));
        await Collect(CreateProvider(withoutBeta,
                oauth: new FakeCredentialSource(Bearer()),
                options: new AnthropicProviderOptions { OAuthBetaHeader = string.Empty })
            .StreamAsync(Request(), TestContext.Current.CancellationToken));
        Assert.False(Assert.Single(withoutBeta.Requests).Headers.ContainsKey("anthropic-beta"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Additional_protection_is_configurable()
    {
        var off = new ScriptedHandler(_ => Sse(MinimalStream()));
        await Collect(CreateProvider(off,
                oauth: new FakeCredentialSource(Bearer()),
                options: new AnthropicProviderOptions { SendAdditionalProtection = false })
            .StreamAsync(Request(), TestContext.Current.CancellationToken));
        Assert.False(Assert.Single(off.Requests).Headers.ContainsKey("x-anthropic-additional-protection"));
    }

    // ---- Ayudas ---------------------------------------------------------------------------

    private static AnthropicMessagesProvider CreateProvider(
        HttpMessageHandler handler,
        AuthConfig? auth = null,
        ISecretProvider? secrets = null,
        AnthropicProviderOptions? options = null,
        IClaudeOAuthCredentialSource? oauth = null) =>
        new(new ProviderDescriptor("anthropic", ProviderFamily.AnthropicMessages, "https://api.example.test",
            auth ?? (oauth is null ? AuthConfig.None() : new AuthConfig(AuthKind.OAuth, "anthropic-oauth")),
            false, false, true), secrets ?? new FixedSecrets(""),
            () => new HttpClient(handler, disposeHandler: false), options, oauth);

    private static ClaudeOAuthBearer Bearer(string access = "at-live") =>
        new(access, "acc-1", ["user:profile", "user:inference"]);

    private static ModelRequest Request(string model = "model-a") =>
        new(new ModelSelection(new ModelIdValue(model), 4096, ToolMode.Direct, null, maxOutputTokens: 2048),
            [new ModelMessage(MessageRole.User, [new TextBlock("hola")])], null, [],
            ToolChoice.Auto(), null, null, null, null);

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    private static HttpResponseMessage Error(HttpStatusCode status, string message) => new(status)
    {
        Content = new StringContent(
            "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"" + message + "\"}}",
            Encoding.UTF8, "application/json"),
    };

    private static string MinimalStream(string stop = "end_turn") =>
        "event: message_start\n"
        + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"role\":\"assistant\",\"model\":\"m\","
        + "\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n"
        + "event: content_block_delta\n"
        + "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hola\"}}\n\n"
        + "event: message_delta\n"
        + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"" + stop + "\",\"usage\":{\"output_tokens\":1}}}\n\n"
        + "event: message_stop\n"
        + "data: {\"type\":\"message_stop\"}\n\n";

    private static async System.Threading.Tasks.Task<List<ModelStreamEvent>> Collect(
        IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var result = new List<ModelStreamEvent>();
        await foreach (var item in stream) result.Add(item);
        return result;
    }

    private sealed record SentRequest(string Url, Dictionary<string, string> Headers, string Body);

    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _queue = new(responses);

        public List<SentRequest> Requests { get; } = [];

        protected override async System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Un valor de cabecera multivalor segun RFC 7230 (producto + comentario) se Une con
            // espacio, que es como lo ve el servidor; unir con coma inventaba un separador.
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(" ", h.Value),
                StringComparer.OrdinalIgnoreCase);
            Requests.Add(new SentRequest(request.RequestUri!.ToString(), headers,
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return _queue.TryDequeue(out var next)
                ? next(request)
                : throw new InvalidOperationException("No queda ninguna respuesta guionada.");
        }
    }

    private sealed class FixedSecrets(string value) : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) => Secret.Of(value);
    }

    /// <summary>Fuente de credencial guionada: devuelve una lista fija y cuenta las llamadas.</summary>
    private sealed class FakeCredentialSource(params ClaudeOAuthBearer[] sequence) : IClaudeOAuthCredentialSource
    {
        private readonly ClaudeOAuthBearer[] _sequence = sequence.Length > 0
            ? sequence
            : [null!];

        private int _next;

        public int GetCalls { get; private set; }

        public int ForceRefreshCalls { get; private set; }

        /// <summary>false simula la sesion muerta: el refresco ya no es posible.</summary>
        public bool Recoverable { get; init; } = true;

        public ValueTask<ClaudeOAuthBearer?> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls++;
            return ValueTask.FromResult(Pick());
        }

        public ValueTask<ClaudeOAuthBearer?> ForceRefreshAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ForceRefreshCalls++;
            return ValueTask.FromResult(Recoverable ? Pick() : null);
        }

        private ClaudeOAuthBearer? Pick()
        {
            var index = _next;
            _next = Math.Min(index + 1, _sequence.Length - 1);
            return _sequence[index];
        }
    }
}
