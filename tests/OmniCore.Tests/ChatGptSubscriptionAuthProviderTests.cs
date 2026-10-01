namespace OmniCore.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using OmniCore.Abstractions;
using OmniCore.Host;
using OmniCore.Models;

/// <summary>
/// Login con la suscripción de ChatGPT (ADR-0011 §3.4), sin red ni navegador.
/// Los tokens de fixture se registran en el <see cref="SecretRedactorRegistry"/> process-wide
/// (ADR-0018 §3) al guardarse la sesión: deben contener al menos un carácter ajeno al alfabeto
/// de los GUID (hex + '-'), como la 's' de "access-1". Un valor como "acc-1" coincide con
/// subcadenas de GUIDs aleatorios (p. ej. "…-bacc-1f2a…"), y la redacción de payloads los
/// corrompería en tests paralelos (EventParseException al releer el journal).
/// </summary>
public sealed class ChatGptSubscriptionAuthProviderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Authorization_url_uses_pkce_s256_the_public_client_and_honest_originator()
    {
        var auth = Create(new FakeHandler(), new MemoryStore());
        var (verifier, challenge) = ChatGptSubscriptionAuthProvider.CreatePkcePair();

        var url = new Uri(auth.BuildAuthorizationUrl("st-1", challenge));
        var query = HttpUtility.ParseQueryString(url.Query);

        Assert.Equal("auth.openai.com", url.Host);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("app_EMoamEEZ73f0CkXaXp7hrann", query["client_id"]);
        Assert.Equal("http://localhost:1455/auth/callback", query["redirect_uri"]);
        Assert.Equal("openid profile email offline_access", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("omnicore", query["originator"]);
        Assert.Equal("st-1", query["state"]);
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, query["code_challenge"]);
        Assert.DoesNotContain(verifier, url.ToString());
    }

    [Fact]
    public async System.Threading.Tasks.Task Browser_login_exchanges_the_code_and_stores_tokens_with_the_account_from_the_jwt()
    {
        var handler = new FakeHandler(_ => TokenResponse("access-1", "ref-1", 3600, Jwt("acct-7788")));
        var store = new MemoryStore();
        string? shownUrl = null;
        var listener = new FakeListener(url => new Dictionary<string, string> { ["code"] = "code-123", ["state"] = StateOf(url) });
        var auth = Create(handler, store, listener: listener);

        var status = await auth.LoginWithBrowserAsync(url => { shownUrl = url; listener.Url = url; }, TestContext.Current.CancellationToken);

        Assert.True(status.LoggedIn);
        Assert.Equal("****7788", status.AccountIdMasked);
        var form = HttpUtility.ParseQueryString(handler.Bodies.Single());
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("code-123", form["code"]);
        Assert.False(string.IsNullOrEmpty(form["code_verifier"]));
        var credential = await auth.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal("access-1", credential.AccessToken);
        Assert.Equal("acct-7788", credential.AccountId);
        Assert.DoesNotContain("access-1", shownUrl!);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_callback_with_a_different_state_is_rejected_and_nothing_is_stored()
    {
        var store = new MemoryStore();
        var listener = new FakeListener(_ => new Dictionary<string, string> { ["code"] = "c", ["state"] = "forged" });
        var auth = Create(new FakeHandler(), store, listener: listener);

        var ex = await Assert.ThrowsAsync<ChatGptAuthException>(() => auth.LoginWithBrowserAsync(url => listener.Url = url,
            TestContext.Current.CancellationToken));

        Assert.Equal("stateMismatch", ex.Kind);
        Assert.Null(store.Load(ChatGptSubscriptionAuthProvider.CredentialKey, CancellationToken.None));
    }

    [Fact]
    public async System.Threading.Tasks.Task Tokens_close_to_expiry_are_refreshed_preventively_and_the_refresh_token_is_kept_if_not_rotated()
    {
        var now = T0;
        var handler = new FakeHandler(
            _ => TokenResponse("access-1", "ref-1", 600, Jwt("acct-1")),
            _ => TokenResponse("access-2", null, 3600, null));
        var listener = ApprovingListener();
        var auth = Create(handler, new MemoryStore(), clock: () => now, listener: listener);
        await auth.LoginWithBrowserAsync(url => listener.Url = url, TestContext.Current.CancellationToken);

        now = T0.AddMinutes(6); // quedan 4 min < margen de 5 min
        var credential = await auth.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal("access-2", credential.AccessToken);
        Assert.Equal("acct-1", credential.AccountId);
        var refresh = HttpUtility.ParseQueryString(handler.Bodies[1]);
        Assert.Equal("refresh_token", refresh["grant_type"]);
        Assert.Equal("ref-1", refresh["refresh_token"]);
    }

    [Fact]
    public async System.Threading.Tasks.Task Rejected_refresh_is_authentication_failed_and_missing_session_is_not_logged_in()
    {
        var notLogged = Create(new FakeHandler(), new MemoryStore());
        var missing = await Assert.ThrowsAsync<ChatGptAuthException>(() => notLogged.GetAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("notLoggedIn", missing.Kind);

        var now = T0;
        var handler = new FakeHandler(
            _ => TokenResponse("access-1", "ref-1", 60, Jwt("a")),
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\"}") });
        var listener = ApprovingListener();
        var auth = Create(handler, new MemoryStore(), clock: () => now, listener: listener);
        await auth.LoginWithBrowserAsync(url => listener.Url = url, TestContext.Current.CancellationToken);
        now = T0.AddHours(2);

        var ex = await Assert.ThrowsAsync<ModelProviderException>(() => auth.GetAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("AuthenticationFailed", ex.Kind);
    }

    [Fact]
    public async System.Threading.Tasks.Task Device_code_login_polls_until_approved_then_stores_the_session()
    {
        var handler = new FakeHandler(
            _ => Json("{\"device_auth_id\":\"dev-1\",\"user_code\":\"ABCD-1234\",\"interval\":1}"),
            _ => new HttpResponseMessage(HttpStatusCode.Forbidden),
            _ => Json("{\"authorization_code\":\"code-x\",\"code_verifier\":\"ver-x\"}"),
            _ => TokenResponse("access-d", "ref-d", 3600, Jwt("acct-dev1")));
        string? shownCode = null;
        var auth = Create(handler, new MemoryStore());

        var status = await auth.LoginWithDeviceCodeAsync((_, code) => shownCode = code, TestContext.Current.CancellationToken);

        Assert.Equal("ABCD-1234", shownCode);
        Assert.True(status.LoggedIn);
        Assert.Equal("code-x", HttpUtility.ParseQueryString(handler.Bodies[^1])["code"]);
        Assert.Equal("ver-x", HttpUtility.ParseQueryString(handler.Bodies[^1])["code_verifier"]);
    }

    [Fact]
    public async System.Threading.Tasks.Task Logout_removes_the_session_and_status_never_exposes_tokens()
    {
        var store = new MemoryStore();
        var listener = ApprovingListener();
        var auth = Create(new FakeHandler(_ => TokenResponse("secret-access-token", "secret-refresh", 3600, Jwt("acct-9999"))), store,
            listener: listener);
        await auth.LoginWithBrowserAsync(url => listener.Url = url, TestContext.Current.CancellationToken);

        var status = auth.Status(CancellationToken.None);
        Assert.DoesNotContain("secret", status.ToString());
        auth.Logout(CancellationToken.None);
        Assert.False(auth.Status(CancellationToken.None).LoggedIn);
    }

    [Fact]
    public void Account_id_is_read_from_the_openai_auth_claim()
    {
        Assert.Equal("acct-42", ChatGptSubscriptionAuthProvider.AccountIdFromJwt(Jwt("acct-42")));
        Assert.Null(ChatGptSubscriptionAuthProvider.AccountIdFromJwt("not-a-jwt"));
        Assert.Null(ChatGptSubscriptionAuthProvider.AccountIdFromJwt(null));
    }

    private static ChatGptSubscriptionAuthProvider Create(FakeHandler handler, ICredentialStore store,
        Func<DateTimeOffset>? clock = null, IOAuthCallbackListener? listener = null) =>
        new(store, () => new HttpClient(handler, disposeHandler: false), null, clock ?? (() => T0),
            listener ?? new FakeListener(_ => new Dictionary<string, string>()), static (_, _) => System.Threading.Tasks.Task.CompletedTask,
            "OmniCore.Tests.ChatGpt." + Guid.NewGuid().ToString("N"));

    private static FakeListener ApprovingListener() =>
        new(url => new Dictionary<string, string> { ["code"] = "c", ["state"] = StateOf(url) });

    private static string StateOf(string? url) => url is null ? "" : HttpUtility.ParseQueryString(new Uri(url).Query)["state"] ?? "";

    private static string Jwt(string accountId)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return B64("{\"alg\":\"none\"}") + "." + B64("{\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"" + accountId + "\"}}") + ".sig";
    }

    private static HttpResponseMessage TokenResponse(string access, string? refresh, int expiresIn, string? idToken) =>
        Json("{\"access_token\":\"" + access + "\"" + (refresh is null ? "" : ",\"refresh_token\":\"" + refresh + "\"")
            + ",\"expires_in\":" + expiresIn + (idToken is null ? "" : ",\"id_token\":\"" + idToken + "\"") + "}");

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class MemoryStore : ICredentialStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();
        public void Save(string key, string value, CancellationToken cancellationToken) => _values[key] = value;
        public string? Load(string key, CancellationToken cancellationToken) => _values.TryGetValue(key, out var v) ? v : null;
        public void Delete(string key, CancellationToken cancellationToken) => _values.TryRemove(key, out _);
    }

    private sealed class FakeListener(Func<string?, Dictionary<string, string>> respond) : IOAuthCallbackListener
    {
        private readonly TaskCompletionSource<string> _url = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Url { get => _url.Task.IsCompleted ? _url.Task.Result : null; set => _url.TrySetResult(value ?? ""); }
        public async System.Threading.Tasks.Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(int port, string path, CancellationToken cancellationToken)
        {
            // Como el navegador real: el callback llega después de mostrar la URL.
            var url = await _url.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return respond(url);
        }
    }

    private sealed class FakeHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new(responses);
        public List<string> Bodies { get; } = [];
        protected override async System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return _responses.TryDequeue(out var next) ? next(request) : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }
}
