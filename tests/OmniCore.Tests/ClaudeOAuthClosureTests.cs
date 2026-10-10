using System.Net;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Host;
using OmniCore.Models;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed class ClaudeOAuthClosureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-oauth-closure-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Paths Config()
    {
        var paths = new Paths(_root);
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), """
            providers:
              account-alias:
                family: AnthropicMessages
                baseUrl: https://api.example.test
                oauth:
                  clientId: configurable-client
                  userAgent: agent/{version}
                  scopes: [user:profile, user:inference]
                  authorizeUrl: https://auth.example.test/authorize
                  tokenUrl: https://auth.example.test/token
                  profileUrl: https://api.example.test/profile
                  rolesUrl: https://api.example.test/roles
            """);
        return paths;
    }

    private (OmniHost.ClaudeOAuthSession Session, FakeClaudeOAuthHttpMessageHandler Http) Session(Paths paths)
    {
        var http = new FakeClaudeOAuthHttpMessageHandler();
        var session = OmniHost.CreateClaudeOAuthSession(paths, "account-alias",
            httpFactory: () => new HttpClient(http, disposeHandler: false))!;
        return (session, http);
    }

    [Fact]
    public async Task Manual_login_through_runtime_persists_roles_and_logout_removes_credentials()
    {
        var paths = Config();
        var (session, http) = Session(paths);
        http.EnqueueJson(HttpStatusCode.OK, """{"access_token":"access-closure-test","refresh_token":"refresh-closure-test","expires_in":3600,"scope":"user:profile user:inference"}""");
        http.EnqueueJson(HttpStatusCode.OK, """{"account":{"uuid":"account","email":"test@example.test"},"organization":{"organization_type":"claude_pro","rate_limit_tier":"reported-tier"}}""");
        http.EnqueueJson(HttpStatusCode.OK, """{"organization_role":"admin","workspace_role":"member","organization_name":"Reported organization"}""");
        using var runtime = OmniCliRuntime.Create(_root);
        runtime.Localize = Localization.Spanish().Resolve;
        runtime.ClaudeOAuthSessionFactoryForTests = _ => session;
        var lines = new List<string>();
        var result = await runtime.LoginClaudeAsync("account-alias", true, _ =>
        {
            var line = lines.First(l => l.Contains("https://auth.example.test"));
            var url = line[line.IndexOf("https://", StringComparison.Ordinal)..];
            var query = new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            return Task.FromResult<string?>("authorization-code-test#" + query["state"]);
        }, lines.Add, Token);

        Assert.Equal(0, result);
        Assert.Equal(3, http.Requests.Count);
        Assert.Equal("https://api.example.test/roles", http.Requests[2].Url.ToString());
        Assert.Equal("Bearer access-closure-test", http.Requests[2].Authorization);
        var credential = session.Credentials.Load(session.SecretRef, Token)!;
        Assert.Equal("admin", credential.Roles?.OrganizationRole);
        Assert.Equal("pro", credential.SubscriptionType);
        Assert.Equal("reported-tier", credential.RateLimitTier);
        var account = OmniCliRuntime.ClaudeOAuthAccount("account-alias", paths);
        var rendered = UsagePresentation.Account(account, Localization.Spanish());
        Assert.Contains("plan pro", rendered);
        Assert.Contains("reported-tier", rendered);
        Assert.DoesNotContain("access-closure-test", string.Join("\n", lines));
        Assert.DoesNotContain("authorization-code-test", string.Join("\n", lines));
        Assert.Equal(0, runtime.LogoutClaude("account-alias", lines.Add, Token));
        Assert.Null(session.Credentials.Load(session.SecretRef, Token));
        Assert.Null(OmniCliRuntime.ClaudeOAuthAccount("account-alias", paths));
    }

    [Fact]
    public async Task State_mismatch_never_calls_token_endpoint_and_reports_localized_error()
    {
        var (session, http) = Session(Config());
        using var runtime = OmniCliRuntime.Create(_root);
        runtime.Localize = Localization.English().Resolve;
        runtime.ClaudeOAuthSessionFactoryForTests = _ => session;
        var lines = new List<string>();
        var result = await runtime.LoginClaudeAsync("account-alias", true,
            _ => Task.FromResult<string?>("code#wrong-state"), lines.Add, Token);
        Assert.Equal(1, result);
        Assert.Empty(http.Requests);
        Assert.DoesNotContain("claude.oauth.failed", lines[^1]);
        Assert.False(session.Credentials.Exists(session.SecretRef, Token));
    }

    [Fact]
    public async Task Roles_failure_is_optional_but_cancellation_propagates()
    {
        var (session, http) = Session(Config());
        http.EnqueueJson(HttpStatusCode.Forbidden, "{}");
        Assert.Null(await session.Tokens.FetchRolesAsync("fake-access", Token));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Tokens.FetchRolesAsync("fake-access", cancelled.Token));
    }

    [Fact]
    public async Task Disk_reload_and_refresh_preserve_plan_tier_and_roles()
    {
        var store = new ClaudeOAuthCredentialStore(new InMemoryCredentialStore(), Path.Combine(_root, "metadata.json"));
        Directory.CreateDirectory(_root);
        var original = new ClaudeOAuthCredential("expired-access-test", "refresh-token-test", DateTimeOffset.UtcNow.AddHours(-1),
            ["user:inference"], "client") { SubscriptionType = "pro", RateLimitTier = "tier", EmailAddress = "x@example.test",
                Roles = new("admin", "member", "Organization") };
        store.Save("ref", original, Token);
        var coordinator = new ClaudeOAuthRefreshCoordinator(
            (key, ct) => Task.FromResult(store.Load(key, ct)),
            (value, ct) => { store.Save("ref", value, ct); return Task.CompletedTask; },
            (value, _) => Task.FromResult(value.ToTokens() with { AccessToken = "new-access-test", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) }),
            (_, _) => Task.FromResult<IAsyncDisposable>(new NoopLock()),
            ct => Task.FromResult(store.MetadataWrittenAt(ct)));
        await coordinator.EnsureFreshAsync("ref", Token);
        var loaded = store.Load("ref", Token)!;
        Assert.Equal("new-access-test", loaded.AccessToken);
        Assert.Equal("pro", loaded.SubscriptionType);
        Assert.Equal("tier", loaded.RateLimitTier);
        Assert.Equal(original.Roles, loaded.Roles);
        Assert.Equal("pro", store.ReadAccountInfo("ref", Token)?.SubscriptionType);
        Assert.DoesNotContain("new-access-test", loaded.ToString());
        Assert.DoesNotContain("refresh-token-test", loaded.ToTokens().ToString());
    }

    [Fact]
    public void Unknown_account_values_remain_unknown_in_both_locales()
    {
        var account = new AccountConnectionSnapshot("alias", null, null, null);
        var spanish = UsagePresentation.Account(account, Localization.Spanish())!;
        var english = UsagePresentation.Account(account, Localization.English())!;
        Assert.Contains("plan —", spanish);
        Assert.Contains("tier —", english);
        Assert.Contains("vence —", spanish);
        Assert.Contains("expires —", english);
    }

    [Fact]
    public void Configured_roles_endpoint_rejects_plain_http()
    {
        var paths = Config();
        var path = Path.Combine(paths.ConfigDirectory, "providers.yaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("rolesUrl: https:", "rolesUrl: http:"));
        Assert.Throws<ConfigValidationException>(() => OmniHost.LoadUserConfiguration(paths));
    }

    [Fact]
    public async Task Cli_dispatches_manual_login_to_host_without_exposing_the_code()
    {
        var (session, http) = Session(Config());
        http.EnqueueJson(HttpStatusCode.OK, """{"access_token":"access-cli-test","refresh_token":"refresh-cli-test","expires_in":3600,"scope":"user:inference"}""");
        using var runtime = OmniCliRuntime.Create(_root);
        runtime.Localize = Localization.Spanish().Resolve;
        runtime.ClaudeOAuthSessionFactoryForTests = _ => session;
        var previousRuntime = OmniCore.Cli.CliApp.UseRuntimeForTests(runtime);
        var previousIn = Console.In;
        var previousOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetIn(new CallbackReader(() =>
            {
                var text = output.ToString();
                var start = text.IndexOf("https://", StringComparison.Ordinal);
                var url = text[start..].Split('\n')[0].Trim();
                var state = new Uri(url).Query.TrimStart('?').Split('&').First(p => p.StartsWith("state=", StringComparison.Ordinal))[6..];
                return "cli-code-test#" + Uri.UnescapeDataString(state);
            }));
            Assert.Equal(0, await OmniCore.Cli.CliApp.RunAsync(["login", "claude", "account-alias", "--manual"]));
            Assert.NotNull(session.Credentials.Load(session.SecretRef, Token));
            Assert.DoesNotContain("cli-code-test", output.ToString());
            Assert.Equal(0, await OmniCore.Cli.CliApp.RunAsync(["logout", "claude", "account-alias"]));
            Assert.Null(session.Credentials.Load(session.SecretRef, Token));
        }
        finally
        {
            Console.SetIn(previousIn); Console.SetOut(previousOut);
            OmniCore.Cli.CliApp.UseRuntimeForTests(previousRuntime);
        }
    }

    [Fact]
    public void Native_account_is_listed_as_subscription_and_disconnect_uses_its_credential_key()
    {
        var paths = Config();
        var (session, _) = Session(paths);
        session.Credentials.Save(session.SecretRef, new ClaudeOAuthCredential("list-access-test", "list-refresh-test",
            DateTimeOffset.UtcNow.AddHours(1), ["user:inference"], "client") { SubscriptionType = "pro" }, Token);
        var service = new ProviderConnectionService(OmniHost.CreateUserCredentialStore(paths), paths);
        var status = service.List(Token).Single(s => s.ProviderId == "account-alias");
        Assert.Equal(ProviderConnectionMethod.Subscription, status.Method);
        Assert.Equal(ProviderConnectionState.Connected, status.State);
        Assert.Equal(OmniCore.Domain.BillingMode.IncludedQuota, status.BillingMode);
        Assert.True(status.CanDisconnect);
        service.Disconnect("account-alias", Token);
        Assert.Null(session.Credentials.Load(session.SecretRef, Token));
    }

    private sealed class Paths(string root) : IPlatformPaths
    {
        public string DataDirectory => Path.Combine(root, "data");
        public string ConfigDirectory => Path.Combine(root, "config");
        public string UserDatabasePath => Path.Combine(DataDirectory, "user.db");
        public string WorkspaceDirectory(string workspaceId) => Path.Combine(DataDirectory, "workspaces", workspaceId);
    }
    private sealed class NoopLock : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class CallbackReader(Func<string> read) : TextReader
    {
        public override string ReadLine() => read();
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(read());
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
