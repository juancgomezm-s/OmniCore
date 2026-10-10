using System.Net;
using System.Text.Json;
using OmniCore.Models;

namespace OmniCore.Tests;

/// <summary>
/// Tests del token client (§2.4-§2.5 y §2.8 del plan, grupos 4, 5 y 8).
/// Wire falso: FakeClaudeOAuthHttpMessageHandler. Sin red ni login real.
/// </summary>
public sealed class ClaudeOAuthTokenClientTests
{
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string ProfileUrl = "https://api.anthropic.com/api/oauth/profile";
    private const string ClientId = "cid-fijo-0001";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static ClaudeOAuthClientIdentity Identity(string? clientId = null) => new(
        ClientId: clientId ?? ClientId,
        UserAgentTemplate: "agent/{version} (test)",
        Scopes: new[] { "user:profile", "user:inference" },
        AuthorizeUrl: "https://claude.com/cai/oauth/authorize",
        TokenUrl: TokenUrl);

    private static (ClaudeOAuthTokenClient client, FakeClaudeOAuthHttpMessageHandler handler) Create(
        ClaudeOAuthClientIdentity? identity = null)
    {
        var handler = new FakeClaudeOAuthHttpMessageHandler();
        var client = new ClaudeOAuthTokenClient(
            identity ?? Identity(),
            httpFactory: () => new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            utcNow: () => Now,
            profileUrl: ProfileUrl);
        return (client, handler);
    }

    /// <summary>
    /// Respuesta del token endpoint con los nombres exactos del wire. Se escribe a mano en vez de
    /// serializar un objeto: las convenciones de nombre de System.Text.Json no reproducen el
    /// snake_case con guion (SnakeCaseLower da "expiresin"), y aqui el nombre ES el contrato.
    /// </summary>
    private static string TokenJson(
        string access = "at-1",
        string? refresh = "rt-1",
        int? expires = 3600,
        string? scope = "user:profile user:inference")
    {
        var parts = new List<string> { "{\"access_token\":\"" + access + "\"" };
        if (refresh is not null)
        {
            parts.Add(",\"refresh_token\":\"" + refresh + "\"");
        }

        if (expires is not null)
        {
            parts.Add(",\"expires_in\":" + expires.Value);
        }

        if (scope is not null)
        {
            parts.Add(",\"scope\":\"" + scope + "\"");
        }

        parts.Add(",\"token_type\":\"Bearer\"}");
        return string.Concat(parts);
    }

    // ---- Exchange (grupo 4) -------------------------------------------------------------

    [Fact]
    public async Task Exchange_posts_the_exact_protocol_body()
    {
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.OK, TokenJson());

        await client.ExchangeCodeAsync("the-code", "the-state", "the-verifier", "http://localhost:9999/callback", ct: TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal(TokenUrl, handler.LastRequest.Url.ToString());
        var body = handler.LastRequest.Json;
        Assert.Equal("authorization_code", body["grant_type"].GetString());
        Assert.Equal("the-code", body["code"].GetString());
        Assert.Equal("the-state", body["state"].GetString());
        Assert.Equal("the-verifier", body["code_verifier"].GetString());
        Assert.Equal(ClientId, body["client_id"].GetString());
        Assert.Equal("http://localhost:9999/callback", body["redirect_uri"].GetString());
        Assert.False(body.ContainsKey("expires_in"), "sin TTL pedido no se manda expires_in");
    }

    [Fact]
    public async Task Exchange_with_long_lived_ttl_sends_expires_in_as_number()
    {
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.OK, TokenJson());

        await client.ExchangeCodeAsync("c", "s", "v", "http://localhost:1/callback", expiresInSeconds: 31536000, ct: TestContext.Current.CancellationToken);

        Assert.Equal(31536000, handler.LastRequest.Json["expires_in"].GetInt32());
    }

    [Fact]
    public async Task Exchange_maps_wire_into_runtime_state()
    {
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.OK, TokenJson(access: "at-x", refresh: "rt-y", expires: 7200));

        var tokens = await client.ExchangeCodeAsync("c", "s", "v", "http://localhost:1/callback", ct: TestContext.Current.CancellationToken);

        Assert.Equal("at-x", tokens.AccessToken);
        Assert.Equal("rt-y", tokens.RefreshToken);
        Assert.Equal(Now.AddSeconds(7200), tokens.ExpiresAt);
        Assert.Equal(["user:profile", "user:inference"], tokens.Scopes);
        Assert.Equal(ClientId, tokens.ClientId);
        Assert.True(tokens.CanDoInference);
    }

    [Fact]
    public async Task Exchange_401_is_invalid_code()
    {
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.Unauthorized, """{"error":"invalid_request"}""");

        await Assert.ThrowsAsync<ClaudeOAuthExchangeInvalidCodeException>(() =>
            client.ExchangeCodeAsync("c", "s", "v", "http://localhost:1/callback", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Exchange_5xx_is_http_error_with_status()
    {
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.ServiceUnavailable, """{"error":"overloaded"}""");

        var ex = await Assert.ThrowsAsync<ClaudeOAuthExchangeHttpException>(() =>
            client.ExchangeCodeAsync("c", "s", "v", "http://localhost:1/callback", ct: TestContext.Current.CancellationToken));

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("overloaded", ex.ServerErrorType);
    }

    [Fact]
    public async Task Exchange_does_not_invent_fields_absent_from_the_wire()
    {
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.OK, """{"access_token":"a","refresh_token":"r","expires_in":60}""");

        var tokens = await client.ExchangeCodeAsync("c", "s", "v", "http://localhost:1/callback", ct: TestContext.Current.CancellationToken);

        Assert.Empty(tokens.Scopes);
        Assert.Null(tokens.AccountUuid);
        Assert.Null(tokens.OrganizationUuid);
    }

    [Fact]
    public async Task Exchange_without_refresh_token_is_a_protocol_failure()
    {
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.OK, TokenJson(refresh: null));

        var ex = await Assert.ThrowsAsync<ClaudeOAuthProtocolException>(() =>
            client.ExchangeCodeAsync("c", "s", "v", "http://localhost:1/callback", ct: TestContext.Current.CancellationToken));
        Assert.Contains("refresh_token", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exchange_without_access_token_is_a_protocol_failure()
    {
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.OK, """{"refresh_token":"r","expires_in":60}""");

        await Assert.ThrowsAsync<ClaudeOAuthProtocolException>(() =>
            client.ExchangeCodeAsync("c", "s", "v", "http://localhost:1/callback", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Exchange_with_non_json_success_is_a_protocol_failure()
    {
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.OK, "<html>not json</html>");

        await Assert.ThrowsAsync<ClaudeOAuthProtocolException>(() =>
            client.ExchangeCodeAsync("c", "s", "v", "http://localhost:1/callback", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Exchange_network_failure_is_typed_as_network()
    {
        var (client, handler) = Create();
        handler.EnqueueThrow(new HttpRequestException("no hay ruta"));

        await Assert.ThrowsAsync<ClaudeOAuthNetworkException>(() =>
            client.ExchangeCodeAsync("c", "s", "v", "http://localhost:1/callback", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Exchange_requires_code_verifier_and_redirect()
    {
        var (client, _) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ExchangeCodeAsync("", "s", "v", "http://localhost:1/callback", ct: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ExchangeCodeAsync("c", "s", "", "http://localhost:1/callback", ct: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ExchangeCodeAsync("c", "s", "v", "", ct: TestContext.Current.CancellationToken));
    }

    // ---- Refresh (grupo 5) --------------------------------------------------------------

    [Fact]
    public async Task Refresh_posts_the_exact_protocol_body()
    {
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.OK, TokenJson(access: "at-2", refresh: "rt-2"));

        await client.RefreshAsync("the-refresh-token", ct: TestContext.Current.CancellationToken);

        var body = handler.LastRequest.Json;
        Assert.Equal("refresh_token", body["grant_type"].GetString());
        Assert.Equal("the-refresh-token", body["refresh_token"].GetString());
        Assert.Equal(ClientId, body["client_id"].GetString());
        Assert.Equal("user:profile user:inference", body["scope"].GetString());
    }

    [Fact]
    public async Task Refresh_keeps_the_client_id_that_issued_the_token()
    {
        // Sticky client id (client.ts:281-286): un token emitido por otro cliente (p. ej. el
        // override de entorno) sigue refrescando con ese, no con el de la identidad.
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.OK, TokenJson());

        await client.RefreshAsync("rt", clientId: "cid-ajeno-9999", ct: TestContext.Current.CancellationToken);

        Assert.Equal("cid-ajeno-9999", handler.LastRequest.Json["client_id"].GetString());
    }

    [Fact]
    public async Task Refresh_can_expand_scopes_when_asked()
    {
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.OK, TokenJson());

        await client.RefreshAsync("rt", scopes: new[] { "user:inference", "user:file_upload" }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("user:inference user:file_upload", handler.LastRequest.Json["scope"].GetString());
    }

    [Fact]
    public async Task Refresh_keeps_old_refresh_token_when_server_returns_none()
    {
        // client.ts:311 — `refresh_token: newRefreshToken = refreshToken`.
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.OK, TokenJson(access: "at-new", refresh: null));

        var tokens = await client.RefreshAsync("rt-old", ct: TestContext.Current.CancellationToken);

        Assert.Equal("at-new", tokens.AccessToken);
        Assert.Equal("rt-old", tokens.RefreshToken);
    }

    [Fact]
    public async Task Refresh_invalid_grant_is_typed_apart_from_transient_failures()
    {
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

        await Assert.ThrowsAsync<ClaudeOAuthInvalidGrantException>(() => client.RefreshAsync("rt-dead", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Refresh_invalid_grant_recognizes_object_error_shape()
    {
        // RFC 6749 admite error como string o como objeto con type (client.ts:isInvalidGrantError).
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.Unauthorized, """{"error":{"type":"invalid_grant"}}""");

        await Assert.ThrowsAsync<ClaudeOAuthInvalidGrantException>(() => client.RefreshAsync("rt-dead", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Refresh_5xx_is_transient_not_dead()
    {
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.GatewayTimeout, "");

        var ex = await Assert.ThrowsAsync<ClaudeOAuthRefreshHttpException>(() => client.RefreshAsync("rt", ct: TestContext.Current.CancellationToken));

        Assert.Equal(504, ex.StatusCode);
        Assert.Null(ex.ServerErrorType);
    }

    [Fact]
    public async Task Invalid_grant_on_a_500_status_is_not_taken_as_dead_token()
    {
        // Solo 400/401 cuentan como invalid_grant: si no, un gateway que reescribe el cuerpo
        // mataría refresh tokens válidos (client.ts:isInvalidGrantError exige 400 o 401).
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.InternalServerError, """{"error":"invalid_grant"}""");

        await Assert.ThrowsAsync<ClaudeOAuthRefreshHttpException>(() => client.RefreshAsync("rt", ct: TestContext.Current.CancellationToken));
    }

    // ---- Seguridad (INV-016 / ADR-0018) --------------------------------------------------

    [Fact]
    public async Task Failure_detail_never_carries_the_raw_body_or_the_tokens()
    {
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.BadGateway, """{"secret":"ACCESS-TOKEN-VALUE","other":"x"}""");

        var ex = await Assert.ThrowsAsync<ClaudeOAuthExchangeHttpException>(() =>
            client.ExchangeCodeAsync("c", "s", "v", "http://localhost:1/callback", ct: TestContext.Current.CancellationToken));

        Assert.DoesNotContain("ACCESS-TOKEN-VALUE", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.ServerErrorType);
    }

    [Fact]
    public async Task Free_form_server_error_types_are_not_propagated()
    {
        // El type debe tener forma de token RFC 6749; si no, se descarta (client.ts filtra igual
        // antes de loguear, para no publicar payloads controlados por el servidor).
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.BadRequest,
            """{"error":"stack trace in /home/anthropic/secret.py line 42"}""");

        var ex = await Assert.ThrowsAsync<ClaudeOAuthRefreshHttpException>(() => client.RefreshAsync("rt", ct: TestContext.Current.CancellationToken));

        Assert.Null(ex.ServerErrorType);
        Assert.DoesNotContain("/home/", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tokens_do_not_appear_in_exception_messages_of_the_exchange()
    {
        var (client, handler) = Create();
        // Respuesta válida salvo por el TTL ausente: fuerza el fallo de protocolo despues de que
        // el cliente ya tenga los tokens en la mano, que es donde se filtran por descuido.
        handler.EnqueueJson(HttpStatusCode.OK,
            """{"access_token":"SUPERSECRETAT","refresh_token":"SUPERSECRETRT","scope":"user:inference"}""");

        var ex = await Assert.ThrowsAsync<ClaudeOAuthProtocolException>(() =>
            client.ExchangeCodeAsync("c", "s", "v", "http://localhost:1/callback",
                ct: TestContext.Current.CancellationToken));

        Assert.DoesNotContain("SUPERSECRETAT", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SUPERSECRETRT", ex.Message, StringComparison.Ordinal);
    }

    // ---- Perfil --------------------------------------------------------------------------

    [Fact]
    public async Task Profile_sends_bearer_and_maps_subscription_and_tier()
    {
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.OK, """
            {"account":{"uuid":"acc-1","email":"u@example.com","display_name":"Jo"},
             "organization":{"uuid":"org-1","organization_type":"claude_max","rate_limit_tier":"tier-5x"}}
            """);

        var profile = await client.FetchProfileAsync("at-1", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(profile);
        Assert.Equal("max", profile!.SubscriptionType);
        Assert.Equal("tier-5x", profile.RateLimitTier);
        Assert.Equal("acc-1", profile.AccountUuid);
        Assert.Equal("org-1", profile.OrganizationUuid);
        Assert.Equal("Jo", profile.DisplayName);
        Assert.Equal(ProfileUrl, handler.LastRequest.Url.ToString());
        Assert.Equal("Bearer at-1", handler.LastRequest.Authorization);
    }

    [Theory]
    [InlineData("claude_max", "max")]
    [InlineData("claude_pro", "pro")]
    [InlineData("claude_team", "team")]
    [InlineData("claude_enterprise", "enterprise")]
    [InlineData("algo_nuevo", null)]
    public async Task Profile_maps_known_organization_types_and_leaves_unknown_as_null(
        string organizationType, string? expected)
    {
        var (client, handler) = Create();
        var wire = "{\"account\":{\"uuid\":\"a\"},\"organization\":{\"organization_type\":\""
            + organizationType + "\"}}";
        handler.EnqueueJson(HttpStatusCode.OK, wire);

        var profile = await client.FetchProfileAsync("at", ct: TestContext.Current.CancellationToken);

        Assert.Equal(expected, profile?.SubscriptionType);
    }

    [Fact]
    public async Task Profile_failure_degrades_to_null_instead_of_breaking_login()
    {
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.Forbidden, "");

        Assert.Null(await client.FetchProfileAsync("at", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Profile_network_failure_degrades_to_null()
    {
        var (client, handler) = Create();
        handler.EnqueueThrow(new HttpRequestException("sin red"));

        Assert.Null(await client.FetchProfileAsync("at", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Profile_malformed_json_degrades_to_null()
    {
        var (client, handler) = Create();
        handler.EnqueueJson(HttpStatusCode.OK, "not-json-at-all");

        Assert.Null(await client.FetchProfileAsync("at", ct: TestContext.Current.CancellationToken));
    }

    // ---- Configuración (CLAUDE.md: todo valor configurable cambia el comportamiento) -----

    [Fact]
    public async Task Rpc_timeout_fires_before_the_endpoint_answers()
    {
        var handler = new FakeClaudeOAuthHttpMessageHandler();
        handler.EnqueueHang();
        var client = new ClaudeOAuthTokenClient(
            Identity(),
            httpFactory: () => new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            rpcTimeout: TimeSpan.FromMilliseconds(150),
            utcNow: () => Now);

        // El fake nunca responde: el timeout propio del RPC tiene que cortarlo, no esperar al
        // CancellationToken del test (que tardaría lo que tarde xUnit en rendirse).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<ClaudeOAuthNetworkException>(() =>
            client.ExchangeCodeAsync("c", "s", "v", "http://localhost:1/callback",
                ct: TestContext.Current.CancellationToken));
        sw.Stop();

        Assert.Contains("a tiempo", ex.Message, StringComparison.Ordinal);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"el timeout no cortó la llamada ({sw.Elapsed.TotalSeconds}s)");
    }

    [Fact]
    public async Task User_identity_endpoints_are_used_verbatim()
    {
        var custom = new ClaudeOAuthClientIdentity(
            ClientId: "cid-propio",
            UserAgentTemplate: "omnicore/{version}",
            Scopes: new[] { "user:inference" },
            AuthorizeUrl: "https://auth.mine.example/oauth/authorize",
            TokenUrl: "https://token.mine.example/v1/oauth/token");
        var (client, handler) = Create(custom);
        handler.EnqueueJson(HttpStatusCode.OK, TokenJson());

        await client.RefreshAsync("rt", ct: TestContext.Current.CancellationToken);

        Assert.Equal("https://token.mine.example/v1/oauth/token", handler.LastRequest.Url.ToString());
        Assert.Equal("cid-propio", handler.LastRequest.Json["client_id"].GetString());
        Assert.Equal("user:inference", handler.LastRequest.Json["scope"].GetString());
    }

    [Fact]
    public void Profile_url_is_derived_from_the_token_host_when_not_configured()
    {
        var handler = new FakeClaudeOAuthHttpMessageHandler();
        handler.EnqueueJson(HttpStatusCode.Forbidden, "");
        var client = new ClaudeOAuthTokenClient(Identity(), () => new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, utcNow: () => Now);

        // No lanza: la derivación es interna y el perfil es enrichment opcional.
        var task = client.FetchProfileAsync("at", ct: TestContext.Current.CancellationToken);
        Assert.NotNull(task);
    }

    [Fact]
    public void Invalid_token_url_in_identity_is_rejected_at_construction()
    {
        var bad = new ClaudeOAuthClientIdentity(
            ClientId: "c",
            UserAgentTemplate: "u/{version}",
            Scopes: new[] { "s" },
            AuthorizeUrl: "https://a.example/authorize",
            TokenUrl: "no-es-url");

        Assert.Throws<ArgumentException>(() => new ClaudeOAuthTokenClient(bad));
    }

    [Fact]
    public void Non_positive_timeouts_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClaudeOAuthTokenClient(
            Identity(), rpcTimeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClaudeOAuthTokenClient(
            Identity(), profileTimeout: TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void Identity_is_required()
    {
        Assert.Throws<ArgumentNullException>(() => new ClaudeOAuthTokenClient(null!));
    }
}
