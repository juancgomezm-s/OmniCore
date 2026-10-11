using OmniCore.Abstractions;
using OmniCore.Host;
using OmniCore.Models;

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
