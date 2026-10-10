using System.Net;
using OmniCore.Abstractions;
using OmniCore.Models;

namespace OmniCore.Tests;

/// <summary>
/// Tests del flujo completo de login (§3.2, §4 del plan): F1 + F2 + F3 + F4 encadenados contra
/// transporte, handler HTTP y launcher falsos. Es lo que afirma que las cuatro fases se pasan los
/// valores correctas entre sí, cosa que los tests por separado no pueden decir.
/// </summary>
public sealed class ClaudeOAuthLoginTests : IDisposable
{
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string AuthorizeUrl = "https://claude.com/cai/oauth/authorize";
    private const string ProfileUrl = "https://api.anthropic.com/api/oauth/profile";
    private const string ClientId = "cid-login-0001";
    private const string SecretRef = "anthropic-oauth";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "omni-oauth-vertical-" + Guid.NewGuid().ToString("N"));

    public ClaudeOAuthLoginTests() => Directory.CreateDirectory(_directory);

    private string MetadataPath() => Path.Combine(_directory, "claude-oauth-account.json");

    // ---- Flujo loopback de punta a punta ---------------------------------------------------

    [Fact]
    public async Task Loopback_login_end_to_end_persists_a_usable_credential()
    {
        var h = NewHarness();
        h.Http.EnqueueJson(HttpStatusCode.OK, TokenWire());
        h.Http.EnqueueJson(HttpStatusCode.OK, ProfileWire());

        var result = await h.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success, "fallo: " + result.Failure + " " + result.Detail);
        Assert.Null(result.Failure);
        Assert.Equal(2, h.Http.CallCount);
        Assert.Equal(TokenUrl, h.Http.Requests[0].Url.ToString());
        Assert.Equal(ProfileUrl, h.Http.Requests[1].Url.ToString());
        Assert.Equal("Bearer at-login", h.Http.Requests[1].Authorization);
        Assert.Collection(h.Http.Requests,
            r => Assert.Equal(HttpMethod.Post, r.Method),
            r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Equal("u@example.com", result.EmailAddress);
        Assert.Equal("max", result.SubscriptionType);

        var credential = h.Credentials.Load(SecretRef, TestContext.Current.CancellationToken);
        Assert.NotNull(credential);
        Assert.Equal("at-login", credential!.AccessToken);
        Assert.Equal("rt-login", credential.RefreshToken);
        Assert.Equal(ClientId, credential.ClientId);
        Assert.Equal(Now.AddSeconds(3600), credential.ExpiresAt);
        Assert.Equal("acc-1", credential.AccountUuid);
        Assert.Equal("org-1", credential.OrganizationUuid);
        Assert.True(credential.CanDoInference);

        // Plan, tier y email son datos de presentacion, no de refresco: van a la metadata NO
        // secreta (se leen sin descifrar) y no al blob cifrado. Afirmar contra el credential
        // recargado seria esperar campos que su wire no lleva a proposito.
        var info = h.Credentials.ReadAccountInfo(SecretRef, TestContext.Current.CancellationToken);
        Assert.NotNull(info);
        Assert.Equal("max", info!.SubscriptionType);
        Assert.Equal("tier-5x", info.RateLimitTier);
        Assert.Equal("u@example.com", info.EmailAddress);
        Assert.Equal("acc-1", info.AccountUuid);
        Assert.True(info.HasInferenceScope);
    }

    [Fact]
    public async Task The_exchange_carries_the_verifier_matching_the_authorize_challenge()
    {
        // Invariante que cruza F1 con F3: si el login mandara un verifier distinto del challenge
        // puesto en la URL, Anthropic rechazaría el exchange.
        var h = NewHarness();
        h.Http.EnqueueJson(HttpStatusCode.OK, TokenWire());
        h.Http.EnqueueJson(HttpStatusCode.OK, ProfileWire());

        await h.RunAsync(TestContext.Current.CancellationToken);

        var sentVerifier = h.Http.LastTokenRequest.Json["code_verifier"].GetString()!;
        Assert.Equal(h.AuthorizeQuery["code_challenge"], ClaudeOAuthPkce.GenerateCodeChallenge(sentVerifier));
        Assert.Equal("S256", h.AuthorizeQuery["code_challenge_method"]);
    }

    [Fact]
    public async Task The_redirect_uri_of_the_exchange_matches_the_one_in_the_authorize_url()
    {
        // client.ts manda el mismo redirect_uri en authorize y en exchange; si difirieran en el
        // número de puerto el exchange fallaría. El puerto lo pone el transporte, así que solo se
        // puede comprobar viendo las dos salidas del mismo login.
        var h = NewHarness();
        h.Http.EnqueueJson(HttpStatusCode.OK, TokenWire());
        h.Http.EnqueueJson(HttpStatusCode.OK, ProfileWire());
        h.Port = 41877;

        await h.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("http://localhost:41877/callback", h.AuthorizeQuery["redirect_uri"]);
        Assert.Equal("http://localhost:41877/callback", h.Http.LastTokenRequest.Json["redirect_uri"].GetString());
    }

    [Fact]
    public async Task The_state_validated_by_the_listener_is_the_one_published_in_the_authorize_url()
    {
        // Si el listener esperara otro state, el harness (que responde con el de la URL) vería un
        // rechazo y el login no completaría.
        var h = NewHarness();
        h.Http.EnqueueJson(HttpStatusCode.OK, TokenWire());
        h.Http.EnqueueJson(HttpStatusCode.OK, ProfileWire());

        var result = await h.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success, "fallo: " + result.Failure + " " + result.Detail);
        Assert.False(string.IsNullOrWhiteSpace(h.AuthorizeQuery["state"]));
    }

    [Fact]
    public async Task Progress_phases_follow_the_state_machine_of_the_plan()
    {
        var h = NewHarness();
        h.Http.EnqueueJson(HttpStatusCode.OK, TokenWire());
        h.Http.EnqueueJson(HttpStatusCode.OK, ProfileWire());

        await h.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            ClaudeOAuthLoginPhase.PreparingChallenge,
            ClaudeOAuthLoginPhase.Listening,
            ClaudeOAuthLoginPhase.AwaitingBrowser,
            ClaudeOAuthLoginPhase.Exchanging,
            ClaudeOAuthLoginPhase.FetchingProfile,
            ClaudeOAuthLoginPhase.Persisting,
            ClaudeOAuthLoginPhase.Succeeded,
        ], h.Phases);
    }

    [Fact]
    public async Task The_browser_gets_the_same_url_the_progress_announced()
    {
        var browser = new FakeBrowserLauncher();
        var h = NewHarness(browser: browser);
        h.Http.EnqueueJson(HttpStatusCode.OK, TokenWire());
        h.Http.EnqueueJson(HttpStatusCode.OK, ProfileWire());

        var result = await h.Login.LoginAsync(SecretRef, new ClaudeOAuthLoginOptions(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, "fallo: " + result.Failure + " " + result.Detail);
        var launched = Assert.Single(browser.Launched);
        Assert.Equal(h.AuthorizeUrl, launched.AbsoluteUri);
    }

    // ---- Caminos de fallo -------------------------------------------------------------------

    [Fact]
    public async Task Browser_unavailable_returns_the_url_and_spends_no_http_call()
    {
        var browser = new FakeBrowserLauncher(launch: false);
        var h = NewHarness(browser: browser);

        var result = await h.Login.LoginAsync(SecretRef, new ClaudeOAuthLoginOptions(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ClaudeOAuthLoginFailure.BrowserUnavailable, result.Failure);
        // La URL es publica: el usuario tiene que poder abrirla a mano.
        Assert.StartsWith(AuthorizeUrl + "?", result.Detail, StringComparison.Ordinal);
        Assert.Single(browser.Launched);
        // Sin navegador no hay redirect que esperar: nunca se llega al exchange.
        Assert.Empty(h.Http.Requests);
        Assert.Null(h.Credentials.Load(SecretRef, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejected_code_leaves_no_credential_behind()
    {
        // Un exchange 401 no puede dejar media credencial escrita: el siguiente arranque tiene
        // que ver "no hay sesion", no un token inservible.
        var h = NewHarness();
        h.Http.EnqueueJson(HttpStatusCode.Unauthorized, """{"error":"invalid_request"}""");

        var result = await h.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ClaudeOAuthLoginFailure.ExchangeRejected, result.Failure);
        Assert.Null(h.Credentials.Load(SecretRef, TestContext.Current.CancellationToken));
        Assert.Null(h.Credentials.ReadAccountInfo(SecretRef, TestContext.Current.CancellationToken));
        Assert.Empty(h.Memory!.Values);
    }

    [Fact]
    public async Task Callback_timeout_reports_TimedOut_without_hanging_the_caller()
    {
        // Sin auto-response el listener espera; el timeout del login tiene que cortarlo.
        var transport = new FakeClaudeOAuthCallbackTransport();
        var http = new FakeClaudeOAuthHttpMessageHandler();
        var login = new ClaudeOAuthLogin(
            Identity(),
            new ClaudeOAuthTokenClient(Identity(), () => new HttpClient(http) { Timeout = Timeout.InfiniteTimeSpan },
                utcNow: () => Now),
            new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), MetadataPath()),
            _ => Task.FromResult<IClaudeOAuthCallbackTransport>(transport),
            progress: null,
            utcNow: () => Now);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await login.LoginAsync(SecretRef, new ClaudeOAuthLoginOptions
        {
            OpenBrowser = false,
            CallbackTimeout = TimeSpan.FromMilliseconds(150),
        }, TestContext.Current.CancellationToken);
        sw.Stop();

        Assert.Equal(ClaudeOAuthLoginFailure.TimedOut, result.Failure);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "el timeout no cortó (" + sw.Elapsed.TotalSeconds + "s)");
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Cancelling_mid_flight_reports_Cancelled()
    {
        var transport = new FakeClaudeOAuthCallbackTransport();
        var http = new FakeClaudeOAuthHttpMessageHandler();
        var login = new ClaudeOAuthLogin(
            Identity(),
            new ClaudeOAuthTokenClient(Identity(), () => new HttpClient(http) { Timeout = Timeout.InfiniteTimeSpan },
                utcNow: () => Now),
            new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), MetadataPath()),
            _ => Task.FromResult<IClaudeOAuthCallbackTransport>(transport),
            utcNow: () => Now);
        using var cts = new CancellationTokenSource();

        var pending = login.LoginAsync(SecretRef, new ClaudeOAuthLoginOptions { OpenBrowser = false }, cts.Token);
        cts.Cancel();
        var result = await pending;

        Assert.Equal(ClaudeOAuthLoginFailure.Cancelled, result.Failure);
    }

    [Fact]
    public async Task Network_failure_at_the_token_endpoint_is_classified_as_Network()
    {
        var h = NewHarness();
        h.Http.EnqueueThrow(new HttpRequestException("sin ruta al endpoint"));

        var result = await h.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ClaudeOAuthLoginFailure.Network, result.Failure);
        Assert.Null(h.Credentials.Load(SecretRef, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Storage_failure_after_a_good_exchange_reports_Storage_not_Success()
    {
        // El token ya esta en la mano: si no se puede persistir, el login NO puede decir que
        // funciono, o el usuario veria "sesion iniciada" sin sesion.
        var h = NewHarness(throwingStore: true);
        h.Http.EnqueueJson(HttpStatusCode.OK, TokenWire());
        h.Http.EnqueueJson(HttpStatusCode.OK, ProfileWire());

        var result = await h.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ClaudeOAuthLoginFailure.Storage, result.Failure);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Transport_failure_is_reported_as_TransportUnavailable()
    {
        var http = new FakeClaudeOAuthHttpMessageHandler();
        var login = new ClaudeOAuthLogin(
            Identity(),
            new ClaudeOAuthTokenClient(Identity(), () => new HttpClient(http) { Timeout = Timeout.InfiniteTimeSpan },
                utcNow: () => Now),
            new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), MetadataPath()),
            _ => throw new OAuthTransportException(OAuthTransportFailure.AccessDenied, 0),
            utcNow: () => Now);

        var result = await login.LoginAsync(SecretRef, new ClaudeOAuthLoginOptions { OpenBrowser = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(ClaudeOAuthLoginFailure.TransportUnavailable, result.Failure);
    }

    [Fact]
    public async Task A_foreign_callback_state_is_rejected_before_any_exchange()
    {
        // CSRF: si el redirect trae un state ajeno, no se llama al exchange ni se gasta el code.
        var transport = new FakeClaudeOAuthCallbackTransport();
        var http = new FakeClaudeOAuthHttpMessageHandler();
        var urls = new List<string>();
        var login = new ClaudeOAuthLogin(
            Identity(),
            new ClaudeOAuthTokenClient(Identity(), () => new HttpClient(http) { Timeout = Timeout.InfiniteTimeSpan },
                utcNow: () => Now),
            new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), MetadataPath()),
            _ => Task.FromResult<IClaudeOAuthCallbackTransport>(transport),
            progress: p =>
            {
                if (p.AuthorizeUrl is { } url)
                {
                    urls.Add(url);
                    transport.Enqueue(ClaudeOAuthLoopbackListener.CallbackPath,
                        ("code", "the-auth-code"), ("state", "state-de-otro-intento"));
                }
            },
            utcNow: () => Now);

        var result = await login.LoginAsync(SecretRef, new ClaudeOAuthLoginOptions { OpenBrowser = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(ClaudeOAuthLoginFailure.CallbackRejected, result.Failure);
        Assert.Empty(http.Requests);
        Assert.NotEmpty(urls);
    }

    // ---- Camino manual ----------------------------------------------------------------------

    [Fact]
    public async Task Manual_login_completes_from_the_pending_session_it_handed_out()
    {
        var h = NewHarness();
        h.Http.EnqueueJson(HttpStatusCode.OK, TokenWire());
        h.Http.EnqueueJson(HttpStatusCode.OK, ProfileWire());

        var pending = await h.Login.BeginManualAsync(new ClaudeOAuthLoginOptions(),
            TestContext.Current.CancellationToken);
        var result = await h.Login.CompleteManualAsync(SecretRef, "the-manual-code#" + pending.State, pending,
            new ClaudeOAuthLoginOptions(), TestContext.Current.CancellationToken);

        Assert.True(result.Success, "fallo: " + result.Failure + " " + result.Detail);
        Assert.Equal("the-manual-code", h.Http.LastTokenRequest.Json["code"].GetString());
        Assert.Equal(ClaudeOAuthAuthorizeUrlBuilder.ManualRedirectUri,
            h.Http.LastTokenRequest.Json["redirect_uri"].GetString());
        Assert.Equal(ClaudeOAuthAuthorizeUrlBuilder.ManualRedirectUri,
            Query(pending.AuthorizeUrl)["redirect_uri"]);
        Assert.Equal(Query(pending.AuthorizeUrl)["code_challenge"],
            ClaudeOAuthPkce.GenerateCodeChallenge(h.Http.LastTokenRequest.Json["code_verifier"].GetString()!));
        Assert.NotNull(h.Credentials.Load(SecretRef, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Manual_begin_opens_no_listener_and_makes_no_http_call()
    {
        // No hay socket que escuchar: empezar un login manual no puede reservar puertos ni gastar
        // pedidos, o el fallback costaria lo mismo que el camino primario.
        var h = NewHarness();

        var pending = await h.Login.BeginManualAsync(new ClaudeOAuthLoginOptions(),
            TestContext.Current.CancellationToken);

        Assert.Null(pending.Port);
        Assert.Empty(h.Http.Requests);
        Assert.Equal(ClaudeOAuthLoginPhase.AwaitingBrowser, h.Phases[^1]);
    }

    [Fact]
    public async Task Manual_login_rejects_a_state_that_is_not_the_one_handed_out()
    {
        var h = NewHarness();
        var pending = await h.Login.BeginManualAsync(new ClaudeOAuthLoginOptions(),
            TestContext.Current.CancellationToken);

        var result = await h.Login.CompleteManualAsync(SecretRef, "code#state-ajeno", pending,
            new ClaudeOAuthLoginOptions(), TestContext.Current.CancellationToken);

        Assert.Equal(ClaudeOAuthLoginFailure.CallbackRejected, result.Failure);
        Assert.Empty(h.Http.Requests);
    }

    [Fact]
    public async Task Manual_login_rejects_a_paste_without_the_hash_separator()
    {
        var h = NewHarness();
        var pending = await h.Login.BeginManualAsync(new ClaudeOAuthLoginOptions(),
            TestContext.Current.CancellationToken);

        var result = await h.Login.CompleteManualAsync(SecretRef, "solo-el-code", pending,
            new ClaudeOAuthLoginOptions(), TestContext.Current.CancellationToken);

        Assert.Equal(ClaudeOAuthLoginFailure.CallbackRejected, result.Failure);
        Assert.Empty(h.Http.Requests);
    }

    [Fact]
    public async Task Pending_session_does_not_leak_the_verifier_or_state_through_ToString()
    {
        // INV-016: la sesion pendiente lleva material sensible del intento en curso. Un ToString
        // defectuoso lo volcaría en cualquier log que la formatee.
        var h = NewHarness();

        var pending = await h.Login.BeginManualAsync(new ClaudeOAuthLoginOptions(),
            TestContext.Current.CancellationToken);

        var rendered = pending.ToString();
        Assert.DoesNotContain(pending.State, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(pending.CodeVerifier, rendered, StringComparison.Ordinal);
    }

    // ---- Perfil ------------------------------------------------------------------------------

    [Fact]
    public async Task Profile_is_skipped_when_the_token_lacks_the_profile_scope()
    {
        // Un token de solo inferencia no puede llamar a /api/oauth/profile: daria 403 en cada
        // login (plan §2.8).
        var h = NewHarness();
        h.Http.EnqueueJson(HttpStatusCode.OK, TokenWire(scope: "user:inference"));

        var result = await h.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success, "fallo: " + result.Failure + " " + result.Detail);
        Assert.Equal(1, h.Http.CallCount);
        Assert.Null(result.SubscriptionType);
        Assert.True(h.Credentials.Load(SecretRef, TestContext.Current.CancellationToken)!.CanDoInference);
    }

    [Fact]
    public async Task A_failed_profile_fetch_still_logs_the_user_in()
    {
        // El perfil es enrichment: perderlo no puede tumbar un login que ya tiene tokens validos
        // (client.ts:330-341 cae al valor almacenado en vez de fallar).
        var h = NewHarness();
        h.Http.EnqueueJson(HttpStatusCode.OK, TokenWire());
        h.Http.EnqueueJson(HttpStatusCode.Forbidden, "");

        var result = await h.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Success, "fallo: " + result.Failure + " " + result.Detail);
        Assert.Null(result.SubscriptionType);
        Assert.NotNull(h.Credentials.Load(SecretRef, TestContext.Current.CancellationToken));
    }

    // ---- Secretos fuera del borde ------------------------------------------------------------

    [Fact]
    public async Task Nothing_secret_crosses_into_progress_or_results()
    {
        var h = NewHarness();
        h.Http.EnqueueJson(HttpStatusCode.OK, TokenWire(access: "SUPERSECRET-access", refresh: "SUPERSECRET-refresh"));
        h.Http.EnqueueJson(HttpStatusCode.OK, ProfileWire());

        var result = await h.RunAsync(TestContext.Current.CancellationToken);

        foreach (var text in h.ProgressSnapshots.SelectMany(p => new[] { p.AuthorizeUrl, p.MessageKey })
                     .OfType<string>().Append(result.Detail ?? string.Empty))
        {
            Assert.DoesNotContain("SUPERSECRET-access", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SUPERSECRET-refresh", text, StringComparison.Ordinal);
        }

        // La URL de authorize lleva el challenge (hash del verifier), nunca el verifier: si alguien
        // lo pusiera alli, el exchange seria reproducible desde el log.
        foreach (var url in h.ProgressSnapshots.Select(p => p.AuthorizeUrl).OfType<string>())
        {
            var challenge = Query(url)["code_challenge"];
            Assert.NotEqual(Query(url).GetValueOrDefault("code_verifier"), challenge);
            Assert.False(Query(url).ContainsKey("code_verifier"), "la URL no puede llevar el verifier");
        }
    }

    [Fact]
    public async Task Two_logins_of_the_same_provider_do_not_reuse_state_or_verifier()
    {
        // Dos intentos seguidos no pueden compartir PKCE: el segundo reutilizando el state del
        // primero seria un CSRF contra uno mismo.
        var first = NewHarness();
        first.Http.EnqueueJson(HttpStatusCode.OK, TokenWire());
        first.Http.EnqueueJson(HttpStatusCode.OK, ProfileWire());
        await first.RunAsync(TestContext.Current.CancellationToken);

        var second = NewHarness();
        second.Http.EnqueueJson(HttpStatusCode.OK, TokenWire());
        second.Http.EnqueueJson(HttpStatusCode.OK, ProfileWire());
        await second.RunAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(first.AuthorizeQuery["state"], second.AuthorizeQuery["state"]);
        Assert.NotEqual(first.AuthorizeQuery["code_challenge"], second.AuthorizeQuery["code_challenge"]);
    }

    // ---- Arnes ---------------------------------------------------------------------------

    private readonly List<IDisposable> _disposables = [];

    private sealed class Harness(
        ClaudeOAuthLogin login,
        SelfAuthorizingTransport transport,
        FakeClaudeOAuthHttpMessageHandler http,
        ClaudeOAuthCredentialStore credentials,
        List<ClaudeOAuthLoginPhase> phases,
        List<ClaudeOAuthLoginProgress> snapshots,
        Func<string?> authorizeUrl,
        InMemoryCredentialStore? memory) : IDisposable
    {
        public ClaudeOAuthLogin Login => login;

        public FakeClaudeOAuthHttpMessageHandler Http => http;

        public ClaudeOAuthCredentialStore Credentials => credentials;

        public IReadOnlyList<ClaudeOAuthLoginPhase> Phases => phases;

        public IReadOnlyList<ClaudeOAuthLoginProgress> ProgressSnapshots => snapshots;

        /// <summary>URL de authorize tal y como la anuncio el propio login.</summary>
        public string AuthorizeUrl => authorizeUrl() ?? throw new InvalidOperationException(
            "El login nunca anuncio una URL de authorize.");

        public Dictionary<string, string> AuthorizeQuery => Query(AuthorizeUrl);

        public int Port
        {
            get => transport.Port;
            set => transport.Port = value;
        }

        /// <summary>Almacen en memoria, para afirmar que no quedo nada escrito.</summary>
        public InMemoryCredentialStore Memory => memory ?? throw new InvalidOperationException(
            "Este arnes usa un store que lanza, no uno en memoria.");

        public Task<ClaudeOAuthLoginResult> RunAsync(CancellationToken ct) =>
            login.LoginAsync(SecretRef, new ClaudeOAuthLoginOptions { OpenBrowser = false }, ct);

        public void Dispose() => transport.Dispose();
    }

    private Harness NewHarness(FakeBrowserLauncher? browser = null, bool throwingStore = false)
    {
        var id = Identity();
        var transport = new SelfAuthorizingTransport();
        var http = new FakeClaudeOAuthHttpMessageHandler();
        var memory = throwingStore ? null : new InMemoryCredentialStore();
        var credentials = new ClaudeOAuthCredentialStore(
            throwingStore ? new ThrowingCredentialStore() : memory!,
            MetadataPath());
        var phases = new List<ClaudeOAuthLoginPhase>();
        var snapshots = new List<ClaudeOAuthLoginProgress>();
        string? announcedUrl = null;

        var login = new ClaudeOAuthLogin(
            id,
            new ClaudeOAuthTokenClient(id, () => new HttpClient(http) { Timeout = Timeout.InfiniteTimeSpan },
                utcNow: () => Now, profileUrl: id.ProfileUrl),
            credentials,
            ct => Task.FromResult<IClaudeOAuthCallbackTransport>(transport),
            browser,
            progress: p =>
            {
                phases.Add(p.Phase);
                snapshots.Add(p);
                if (p.Phase == ClaudeOAuthLoginPhase.Listening && p.AuthorizeUrl is { } url)
                {
                    announcedUrl = url;
                    transport.Announce(url);
                }
            },
            utcNow: () => Now);

        var harness = new Harness(login, transport, http, credentials, phases, snapshots,
            () => announcedUrl, memory);
        _disposables.Add(harness);
        return harness;
    }

    /// <summary>
    /// Transporte que se auto-responde con el state leido de la URL de authorize que el login
    /// publica en el progreso. El state y el verifier son deliberadamente invisibles desde fuera
    /// (INV-016): si los tests tuvieran que sacarlos por otra via estarian probando una fuga.
    /// </summary>
    private sealed class SelfAuthorizingTransport : IClaudeOAuthCallbackTransport, IDisposable
    {
        private readonly TaskCompletionSource<ClaudeOAuthCallbackRequest> _request =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Port { get; set; } = 41234;

        public ClaudeOAuthCallbackResponse? LastResponse { get; private set; }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public void Announce(string authorizeUrl)
        {
            var state = Query(authorizeUrl)["state"];
            _request.TrySetResult(new ClaudeOAuthCallbackRequest("/callback",
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["code"] = "the-auth-code",
                    ["state"] = state,
                }));
        }

        public async ValueTask<ClaudeOAuthCallbackRequest> AcceptAsync(CancellationToken ct) =>
            await _request.Task.WaitAsync(ct).ConfigureAwait(false);

        public ValueTask RespondAsync(
            ClaudeOAuthCallbackRequest request,
            ClaudeOAuthCallbackResponse response,
            CancellationToken ct)
        {
            LastResponse = response;
            return ValueTask.CompletedTask;
        }

        public void Dispose() => _request.TrySetCanceled();

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static ClaudeOAuthClientIdentity Identity() => new(
        ClientId: ClientId,
        UserAgentTemplate: "agent/{version} (test)",
        Scopes: new[] { "user:profile", "user:inference" },
        AuthorizeUrl: AuthorizeUrl,
        TokenUrl: TokenUrl)
    { ProfileUrl = ProfileUrl };

    private static Dictionary<string, string> Query(string url)
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

    private static string TokenWire(
        string access = "at-login",
        string refresh = "rt-login",
        string scope = "user:profile user:inference") =>
        $$"""{"access_token":"{{access}}","refresh_token":"{{refresh}}","expires_in":3600,"scope":"{{scope}}"}""";

    private static string ProfileWire() =>
        """{"account":{"uuid":"acc-1","email":"u@example.com","display_name":"Jo"},"organization":{"uuid":"org-1","organization_type":"claude_max","rate_limit_tier":"tier-5x"}}""";

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            d.Dispose();
        }

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>ICredentialStore que falla al escribir: simula disco lleno o sin permisos para
/// afirmar que el login lo reporta como Storage en vez de como éxito.</summary>
internal sealed class ThrowingCredentialStore : ICredentialStore
{
    public void Save(string key, string value, CancellationToken cancellationToken) =>
        throw new IOException("disco lleno");

    public string? Load(string key, CancellationToken cancellationToken) => null;

    public void Delete(string key, CancellationToken cancellationToken)
    {
    }
}
