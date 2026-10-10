using OmniCore.Host;
using OmniCore.Models;

namespace OmniCore.Tests;

/// <summary>
/// Tests del borde que consume el provider (§7 del plan). Lo que se afirma aquí es que el
/// adaptador nunca ve un refresh token ni el almacén: solo un Bearer vigente o null (INV-008).
/// </summary>
public sealed class ClaudeOAuthAuthProviderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private const string SecretRef = "anthropic-oauth";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "omni-oauth-f7-" + Guid.NewGuid().ToString("N"));

    public ClaudeOAuthAuthProviderTests() => Directory.CreateDirectory(_directory);

    private string MetadataPath() => Path.Combine(_directory, "account.json");

    [Fact]
    public async Task GetAsync_returns_a_bearer_without_any_refresh_token()
    {
        var (auth, _) = Setup(expiresIn: TimeSpan.FromHours(1));

        var bearer = await auth.GetAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(bearer);
        Assert.Equal("at-0", bearer!.AccessToken);
        // ClaudeOAuthBearer no tiene campo de refresh token: si alguien lo anade, el provider
        // podria filtrarlo al wire y este test lo impide (INV-016).
        Assert.Empty(typeof(ClaudeOAuthBearer).GetProperties()
            .Where(p => p.Name.Contains("refresh", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name));
    }

    [Fact]
    public async Task GetAsync_refreshes_when_the_token_is_inside_the_margin()
    {
        var (auth, env) = Setup(expiresIn: TimeSpan.FromMinutes(2));

        var bearer = await auth.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, env.RefreshCalls);
        Assert.Equal("at-1", bearer!.AccessToken);
    }

    [Fact]
    public async Task No_session_gives_null_rather_than_throwing()
    {
        var (auth, env) = Setup(expiresIn: TimeSpan.FromHours(1), storeNothing: true);

        Assert.Null(await auth.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, env.RefreshCalls);
    }

    [Fact]
    public async Task ForceRefresh_ignores_the_expiry_margin_and_calls_out_anyway()
    {
        // El servidor ya dijo 401: esperar a que caduque seria inutil (plan §7).
        var (auth, env) = Setup(expiresIn: TimeSpan.FromHours(5));

        var bearer = await auth.ForceRefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, env.RefreshCalls);
        Assert.Equal("at-1", bearer!.AccessToken);
    }

    [Fact]
    public async Task ForceRefresh_on_a_dead_token_returns_null_without_calling_out()
    {
        var (auth, env) = Setup(expiresIn: TimeSpan.FromHours(1));
        env.Coordinator.MarkDead("rt-0");

        Assert.Null(await auth.ForceRefreshAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, env.RefreshCalls);
    }

    [Fact]
    public async Task ForceRefresh_marks_the_token_dead_when_the_server_refuses_it()
    {
        var (auth, env) = Setup(expiresIn: TimeSpan.FromHours(1));
        env.FailWith = new ClaudeOAuthInvalidGrantException();

        Assert.Null(await auth.ForceRefreshAsync(TestContext.Current.CancellationToken));
        Assert.True(env.Coordinator.IsDead("rt-0"));
    }

    [Fact]
    public async Task A_transient_failure_propagates_instead_of_looking_like_a_dead_session()
    {
        // Convertir un timeout en "sesión muerta" borraría una sesión recuperable y obligaría a un
        // login de más (plan §11.3).
        var (auth, env) = Setup(expiresIn: TimeSpan.FromHours(1));
        env.FailWith = new ClaudeOAuthNetworkException("sin ruta", inner: null);

        await Assert.ThrowsAsync<ClaudeOAuthNetworkException>(() =>
            auth.ForceRefreshAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Inspect_reports_plan_and_tier_without_reading_the_credential()
    {
        var (auth, env) = Setup(expiresIn: TimeSpan.FromHours(1));
        env.SeedProfile();

        var status = await auth.InspectAsync(TestContext.Current.CancellationToken);

        Assert.True(status.SignedIn);
        Assert.False(status.Dead);
        Assert.Equal("max", status.SubscriptionType);
        Assert.Equal("tier-5x", status.RateLimitTier);
        Assert.Equal("u@example.com", status.EmailAddress);
        Assert.True(status.HasInferenceScope);
        Assert.Equal(0, env.RefreshCalls);
    }

    [Fact]
    public async Task Inspect_of_an_absent_session_is_None()
    {
        var (auth, _) = Setup(expiresIn: TimeSpan.FromHours(1), storeNothing: true);

        var status = await auth.InspectAsync(TestContext.Current.CancellationToken);

        Assert.False(status.SignedIn);
        Assert.Equal(ClaudeOAuthSessionStatus.None.SignedIn, status.SignedIn);
        Assert.Null(status.SubscriptionType);
    }

    [Fact]
    public async Task Inspect_flags_a_dead_session()
    {
        var (auth, env) = Setup(expiresIn: TimeSpan.FromHours(1));
        env.Coordinator.MarkDead("rt-0");

        var status = await auth.InspectAsync(TestContext.Current.CancellationToken);

        Assert.True(status.SignedIn);
        Assert.True(status.Dead);
    }

    [Fact]
    public async Task SignOut_clears_credential_metadata_and_dead_set()
    {
        var (auth, env) = Setup(expiresIn: TimeSpan.FromHours(1));
        env.Coordinator.MarkDead("rt-dead");

        auth.SignOut(TestContext.Current.CancellationToken);

        Assert.Null(await auth.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, env.Coordinator.DeadCount);
        Assert.False((await auth.InspectAsync(TestContext.Current.CancellationToken)).SignedIn);
    }

    [Fact]
    public void The_secret_ref_is_exposed_for_diagnostics_but_not_the_tokens()
    {
        var (auth, _) = Setup(expiresIn: TimeSpan.FromHours(1));

        Assert.Equal(SecretRef, auth.SecretRef);
        Assert.DoesNotContain("at-", auth.SecretRef, StringComparison.Ordinal);
    }

    // ---- Factory del Host -------------------------------------------------------------------

    [Fact]
    public void CreateClaudeOAuthSession_is_null_when_the_provider_declares_no_oauth_section()
    {
        var paths = new TempPaths(_directory);
        WriteConfig(paths.ConfigDirectory, PlainProviders);

        Assert.Null(OmniHost.CreateClaudeOAuthSession(paths, "anthropic"));
    }

    [Fact]
    public void CreateClaudeOAuthSession_wires_every_piece_from_the_users_config()
    {
        var paths = new TempPaths(_directory);
        WriteConfig(paths.ConfigDirectory, OAuthProviders);

        var session = OmniHost.CreateClaudeOAuthSession(paths, "anthropic");

        Assert.NotNull(session);
        Assert.Equal("anthropic", session!.ProviderId);
        Assert.Equal("anthropic-oauth", session.SecretRef);
        Assert.Equal("cid-host-0001", session.Identity.ClientId);
        Assert.Equal("https://platform.claude.com/v1/oauth/token", session.Identity.TokenUrl);
        Assert.NotNull(session.Auth);
        Assert.NotNull(session.Tokens);
        Assert.NotNull(session.Refresh);
        Assert.NotNull(session.Browser);
        // Las dos maquinas de login salen cableadas y apuntan al mismo store: sin esto el login
        // guardaria en un sitio y el provider leerian otro.
        Assert.NotNull(session.CreateLogin());
        Assert.NotNull(session.CreateManualLogin());
    }

    [Fact]
    public void CreateClaudeOAuthSession_honours_a_declared_secret_ref_override()
    {
        var paths = new TempPaths(_directory);
        WriteConfig(paths.ConfigDirectory, OAuthProvidersWithSecretRef);

        var session = OmniHost.CreateClaudeOAuthSession(paths, "anthropic");

        Assert.Equal("mi-clave-oauth", session!.SecretRef);
    }

    [Fact]
    public void An_unknown_provider_yields_no_session_rather_than_throwing()
    {
        var paths = new TempPaths(_directory);
        WriteConfig(paths.ConfigDirectory, OAuthProviders);

        Assert.Null(OmniHost.CreateClaudeOAuthSession(paths, "no-existe"));
    }

    private const string OAuthProviders = """
        providers:
          anthropic:
            family: AnthropicMessages
            baseUrl: https://api.anthropic.com
            oauth:
              clientId: cid-host-0001
              userAgent: "agent/{version} (external, cli)"
              scopes: [user:profile, user:inference]
              authorizeUrl: https://claude.com/cai/oauth/authorize
              tokenUrl: https://platform.claude.com/v1/oauth/token
        """;

    /// <summary>Misma declaracion pero con secretRef explicito (sangria de 6 espacios, la de oauth:).</summary>
    private const string OAuthProvidersWithSecretRef = """
        providers:
          anthropic:
            family: AnthropicMessages
            baseUrl: https://api.anthropic.com
            oauth:
              clientId: cid-host-0001
              userAgent: "agent/{version} (external, cli)"
              scopes: [user:profile, user:inference]
              authorizeUrl: https://claude.com/cai/oauth/authorize
              tokenUrl: https://platform.claude.com/v1/oauth/token
              secretRef: mi-clave-oauth
        """;

    private const string PlainProviders = """
        providers:
          anthropic:
            family: AnthropicMessages
            baseUrl: https://api.anthropic.com
            authRef: anthropic-key
        """;

    private const string Models = """
        models:
          sonnet:
            provider: anthropic
            context: 200000
            recommendedUsableContext: 190000
            maxOutput: 8192
            parametersBillions: 100
        """;

    private static void WriteConfig(string directory, string providersYaml)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "providers.yaml"), Dedent(providersYaml));
        File.WriteAllText(Path.Combine(directory, "models.yaml"), Dedent(Models));
    }

    /// <summary>
    /// Quita la sangria comun del literal raw: los diccionarios YAML no admiten que las claves
    /// raiz esten indentadas, y el literal necesita sangria para leerse en el codigo.
    /// </summary>
    private static string Dedent(string yaml)
    {
        var normalized = yaml.Replace("\r\n", "\n").Trim('\n');
        var lines = normalized.Split('\n');
        var indent = lines.Where(l => !string.IsNullOrWhiteSpace(l))
            .Min(l => l.Length - l.TrimStart().Length);
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart())) + "\n";
    }


    // ---- Arnes ------------------------------------------------------------------------------

    private (ClaudeOAuthAuthProvider auth, Env env) Setup(
        TimeSpan expiresIn, bool storeNothing = false)
    {
        var identity = new ClaudeOAuthClientIdentity(
            ClientId: "cid-test",
            UserAgentTemplate: "agent/{version}",
            Scopes: new[] { "user:profile", "user:inference" },
            AuthorizeUrl: "https://a.example/auth",
            TokenUrl: "https://t.example/token");
        var credentials = new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), MetadataPath());
        var env = new Env(credentials, identity, expiresIn, storeNothing);
        return (new ClaudeOAuthAuthProvider(SecretRef, credentials, env.Coordinator), env);
    }

    private sealed class Env
    {
        public int RefreshCalls;

        public Exception? FailWith;

        public ClaudeOAuthRefreshCoordinator Coordinator { get; }

        private readonly ClaudeOAuthCredentialStore _credentials;
        private readonly TimeSpan _expiresIn;
        private readonly bool _storeNothing;

        public Env(ClaudeOAuthCredentialStore credentials, ClaudeOAuthClientIdentity identity,
            TimeSpan expiresIn, bool storeNothing)
        {
            _credentials = credentials;
            _expiresIn = expiresIn;
            _storeNothing = storeNothing;
            Coordinator = new ClaudeOAuthRefreshCoordinator(
                load: (ref_, ct) => System.Threading.Tasks.Task.FromResult(_credentials.Load(ref_, ct)),
                save: (cred, ct) =>
                {
                    _credentials.Save(SecretRef, cred, ct);
                    return System.Threading.Tasks.Task.CompletedTask;
                },
                refresh: RefreshAsync,
                acquireLock: (_, ct) => System.Threading.Tasks.Task.FromResult<IAsyncDisposable>(new NoopLock()),
                metadataWrittenAt: ct => System.Threading.Tasks.Task.FromResult(_credentials.MetadataWrittenAt(ct)),
                utcNow: () => Now);
            if (!storeNothing)
            {
                Seed(expiresIn);
            }
        }

        public void SeedProfile() => Seed(_expiresIn, profile: true);

        private void Seed(TimeSpan expiresIn, bool profile = false)
        {
            _credentials.Save(SecretRef, new ClaudeOAuthCredential("at-0", "rt-0", Now + expiresIn,
                ["user:profile", "user:inference"], "cid-test")
            {
                AccountUuid = "acc-1",
                EmailAddress = profile ? "u@example.com" : null,
                SubscriptionType = profile ? "max" : null,
                RateLimitTier = profile ? "tier-5x" : null,
                AuthenticatedAt = Now,
            }, CancellationToken.None);
        }

        private System.Threading.Tasks.Task<ClaudeOAuthTokens> RefreshAsync(
            ClaudeOAuthCredential current, CancellationToken ct)
        {
            RefreshCalls++;
            if (FailWith is not null)
            {
                throw FailWith;
            }

            ct.ThrowIfCancellationRequested();
            return System.Threading.Tasks.Task.FromResult(new ClaudeOAuthTokens(
                "at-" + RefreshCalls, "rt-" + RefreshCalls, Now.AddHours(1),
                current.Scopes, current.ClientId, current.AccountUuid, null));
        }

        private sealed class NoopLock : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Rutas temporales para la factory del Host: nada toca los datos reales del usuario.</summary>
    private sealed class TempPaths(string root) : OmniCore.Abstractions.IPlatformPaths
    {
        public string DataDirectory => Path.Combine(root, "data");

        public string UserDatabasePath => Path.Combine(DataDirectory, "user.db");

        public string ConfigDirectory => Path.Combine(root, "config");

        public string WorkspaceDirectory(string workspaceId) => Path.Combine(DataDirectory, "workspaces", workspaceId);
    }

    public void Dispose()
    {
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
