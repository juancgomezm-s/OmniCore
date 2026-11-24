using OmniCore.Abstractions;
using OmniCore.Host;
using OmniCore.Models;
using System.Net;

namespace OmniCore.Tests;

public sealed class TuiClaudeAccountHostTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-tui-claude-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void List_is_local_masks_identity_and_distinguishes_dead_expired_and_unavailable()
    {
        var paths = new TempPaths(_root);
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), Providers);
        var networkCalls = 0;
        var host = new TuiClaudeAccountHost(paths, providerId =>
        {
            if (providerId == "claude-missing") return null;
            var session = OmniHost.CreateClaudeOAuthSession(paths, providerId,
                httpFactory: () =>
                {
                    Interlocked.Increment(ref networkCalls);
                    return new HttpClient(new ThrowingHandler());
                }, utcNow: () => Now)!;
            var dead = providerId == "claude-dead";
            var expired = providerId == "claude-expired";
            var refresh = "refresh-token-for-" + providerId;
            session.Credentials.Save(session.SecretRef, new ClaudeOAuthCredential(
                "access-token-for-tests", refresh,
                expired ? Now.AddMinutes(-1) : Now.AddHours(1),
                ["user:profile", "user:inference"], session.Identity.ClientId)
            {
                EmailAddress = "user@example.test",
                SubscriptionType = "pro",
                RateLimitTier = "tier-1",
                AuthenticatedAt = Now.AddDays(-1),
            }, CancellationToken.None);
            if (dead) session.Refresh.MarkDead(refresh);
            return session;
        }, () => Now);

        var rows = host.List(CancellationToken.None).ToDictionary(row => row.ProviderId, StringComparer.Ordinal);

        var connected = rows["anthropic"];
        Assert.True(connected.Configured);
        Assert.True(connected.SignedIn);
        Assert.False(connected.Expired);
        Assert.False(connected.Dead);
        Assert.Equal("u***@***", connected.AccountHint);
        Assert.Equal("pro", connected.SubscriptionType);
        Assert.Equal("tier-1", connected.RateLimitTier);
        Assert.DoesNotContain("user@example.test", connected.AccountHint!, StringComparison.Ordinal);

        Assert.True(rows["claude-dead"].Dead);
        Assert.False(rows["claude-dead"].Expired);
        Assert.True(rows["claude-expired"].Expired);
        Assert.False(rows["claude-expired"].Dead);
        Assert.Equal("claude.account.identityUnavailable", rows["claude-missing"].UnavailableReasonKey);
        Assert.False(rows["claude-missing"].CanLogin);
        Assert.Equal(0, networkCalls);
    }

    private const string Providers = """
        providers:
          anthropic:
            family: AnthropicMessages
            baseUrl: https://api.anthropic.com
            oauth:
              clientId: cid-host-0001
              userAgent: "agent/{version}"
              scopes: [user:profile, user:inference]
              authorizeUrl: https://claude.com/cai/oauth/authorize
              tokenUrl: https://platform.claude.com/v1/oauth/token
          claude-dead:
            family: AnthropicMessages
            baseUrl: https://api.anthropic.com
            oauth:
              clientId: cid-host-0001
              userAgent: "agent/{version}"
              scopes: [user:profile, user:inference]
              authorizeUrl: https://claude.com/cai/oauth/authorize
              tokenUrl: https://platform.claude.com/v1/oauth/token
          claude-expired:
            family: AnthropicMessages
            baseUrl: https://api.anthropic.com
            oauth:
              clientId: cid-host-0001
              userAgent: "agent/{version}"
              scopes: [user:profile, user:inference]
              authorizeUrl: https://claude.com/cai/oauth/authorize
              tokenUrl: https://platform.claude.com/v1/oauth/token
          claude-missing:
            family: AnthropicMessages
            baseUrl: https://api.anthropic.com
            oauth:
              clientId: cid-host-0001
              userAgent: "agent/{version}"
              scopes: [user:profile, user:inference]
              authorizeUrl: https://claude.com/cai/oauth/authorize
              tokenUrl: https://platform.claude.com/v1/oauth/token
        """;

    [Fact]
    public async Task Account_catalog_uses_OAuth_pages_and_preserves_existing_model_configuration()
    {
        var requests = 0;
        var (host, _, paths) = CatalogHost(request =>
        {
            requests++;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("access-token-for-tests", request.Headers.Authorization?.Parameter);
            Assert.False(request.Headers.Contains("x-api-key"));
            Assert.Equal("oauth-2025-04-20", request.Headers.GetValues("anthropic-beta").Single());
            Assert.StartsWith("agent/", request.Headers.GetValues("User-Agent").Single());
            Assert.Equal("api.anthropic.com", request.RequestUri!.Host);
            if (requests % 2 == 1)
                return Json("""{"data":[{"id":"claude-fixture-manual"}],"has_more":true,"last_id":"page-one"}""");
            Assert.Contains("after_id=page-one", request.RequestUri.Query);
            return Json("""{"data":[{"id":"claude-fixture-new"}],"has_more":false}""");
        });
        var modelPath = Path.Combine(paths.ConfigDirectory, "models.yaml");
        File.WriteAllText(modelPath, "models:\n  claude-fixture-manual: { provider: anthropic, context: 12345 }\n");
        var before = File.ReadAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"));
        var result = await host.DiscoverModelsAsync("anthropic", CancellationToken.None);
        Assert.True(result.Success, result.ErrorKey);
        Assert.Equal(2, result.AvailableModels);
        Assert.Equal(1, result.RegisteredModels);
        Assert.Equal(2, requests);
        var registry = OmniHost.LoadUserModelRegistry(paths);
        Assert.Equal("anthropic", registry.Model("claude-fixture-new")!.ProviderId);
        Assert.Equal(12345, registry.Model("claude-fixture-manual")!.ContextWindow);
        Assert.Equal(before, File.ReadAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml")));
        Assert.False(File.Exists(Path.Combine(paths.ConfigDirectory, "anthropic-catalog-reasoning.json")));
        Assert.Equal(0, (await host.DiscoverModelsAsync("anthropic", CancellationToken.None)).RegisteredModels);
    }

    [Theory]
    [InlineData(401, "{}", "invalid")]
    [InlineData(403, "{}", "invalid")]
    [InlineData(429, "{}", "rateLimited")]
    [InlineData(200, "{", "catalogInvalid")]
    [InlineData(200, "{}", "catalogInvalid")]
    public async Task Account_catalog_failures_do_not_replace_models_or_sign_out(int code, string body, string error)
    {
        var (host, session, paths) = CatalogHost(_ => new((HttpStatusCode)code) { Content = new StringContent(body) });
        var modelPath = Path.Combine(paths.ConfigDirectory, "models.yaml");
        const string original = "models:\n  claude-fixture-manual: { provider: anthropic, context: 12345 }\n";
        File.WriteAllText(modelPath, original);
        var result = await host.DiscoverModelsAsync("anthropic", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(error, result.ErrorKey);
        Assert.Equal(original, File.ReadAllText(modelPath));
        Assert.True((await session.Auth.InspectAsync(CancellationToken.None)).SignedIn);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Account_catalog_rejects_late_results_after_logout_or_connection_change(bool changeConnection)
    {
        TuiClaudeAccountHost? currentHost = null;
        TempPaths? currentPaths = null;
        var (host, _, paths) = CatalogHost(_ =>
        {
            if (changeConnection)
            {
                var path = Path.Combine(currentPaths!.ConfigDirectory, "providers.yaml");
                File.AppendAllText(path, "\n# changed concurrently\n");
            }
            else currentHost!.Logout("anthropic", CancellationToken.None);
            return Json("""{"data":[{"id":"claude-fixture-late"}]}""");
        });
        currentHost = host; currentPaths = paths;
        Assert.False((await host.DiscoverModelsAsync("anthropic", CancellationToken.None)).Success);
        Assert.False(File.Exists(Path.Combine(paths.ConfigDirectory, "models.yaml")));
    }

    [Fact]
    public async Task Account_catalog_empty_response_and_cancel_do_not_invent_models()
    {
        var requests = 0;
        var (host, _, paths) = CatalogHost(_ => { requests++; return Json("""{"data":[]}"""); });
        var result = await host.DiscoverModelsAsync("anthropic", CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(0, result.AvailableModels);
        Assert.False(File.Exists(Path.Combine(paths.ConfigDirectory, "models.yaml")));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.DiscoverModelsAsync("anthropic", cancelled.Token));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Account_catalog_cancelled_during_download_does_not_publish_models()
    {
        using var cancellation = new CancellationTokenSource();
        var (host, _, paths) = CatalogHost(_ =>
        {
            cancellation.Cancel();
            return Json("""{"data":[{"id":"claude-fixture-cancelled"}]}""");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.DiscoverModelsAsync("anthropic", cancellation.Token));
        Assert.False(File.Exists(Path.Combine(paths.ConfigDirectory, "models.yaml")));
    }

    [Fact]
    public async Task Account_catalog_conflicting_model_does_not_overwrite_or_partially_add_models()
    {
        var (host, _, paths) = CatalogHost(_ => Json("""{"data":[{"id":"claude-fixture-new"},{"id":"already-owned"}]}"""));
        var providerPath = Path.Combine(paths.ConfigDirectory, "providers.yaml");
        File.AppendAllText(providerPath, "\n  other: { family: OpenAiChatCompatible, baseUrl: 'http://127.0.0.1:8080', auth: none }\n");
        var modelPath = Path.Combine(paths.ConfigDirectory, "models.yaml");
        const string models = "models:\n  already-owned: { provider: other, context: 9999 }\n";
        File.WriteAllText(modelPath, models);
        var result = await host.DiscoverModelsAsync("anthropic", CancellationToken.None);
        Assert.Equal("modelConflict", result.ErrorKey);
        Assert.False(result.Success);
        Assert.Equal(models, File.ReadAllText(modelPath));
    }

    private (TuiClaudeAccountHost Host, OmniHost.ClaudeOAuthSession Session, TempPaths Paths) CatalogHost(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var paths = new TempPaths(_root);
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), Providers);
        var session = OmniHost.CreateClaudeOAuthSession(paths, "anthropic",
            httpFactory: () => new HttpClient(new CatalogHandler(respond)), utcNow: () => Now)!;
        session.Credentials.Save(session.SecretRef, new ClaudeOAuthCredential("access-token-for-tests", "refresh-for-tests",
            Now.AddHours(1), ["user:profile", "user:inference"], session.Identity.ClientId), CancellationToken.None);
        return (new TuiClaudeAccountHost(paths, id => id == "anthropic" ? session : null, () => Now), session, paths);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private sealed class CatalogHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class TempPaths(string root) : IPlatformPaths
    {
        public string DataDirectory => Path.Combine(root, "data");
        public string UserDatabasePath => Path.Combine(DataDirectory, "user.db");
        public string ConfigDirectory => Path.Combine(root, "config");
        public string WorkspaceDirectory(string workspaceId) => Path.Combine(DataDirectory, "workspaces", workspaceId);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Inspection must not access the network.");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
