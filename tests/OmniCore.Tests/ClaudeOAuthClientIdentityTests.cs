using OmniCore.Models;

namespace OmniCore.Tests;

/// <summary>
/// Tests para ClaudeOAuthClientIdentity (§1 del plan).
/// Verifica que la identidad es parametrizable y que BuildUserAgent funciona.
/// </summary>
public sealed class ClaudeOAuthClientIdentityTests
{
    [Fact]
    public void BuildUserAgent_replaces_version_placeholder()
    {
        var identity = new ClaudeOAuthClientIdentity(
            ClientId: "test-client-id",
            UserAgentTemplate: "test-agent/{version} (test)",
            Scopes: new[] { "scope1" },
            AuthorizeUrl: "https://example.com/authorize",
            TokenUrl: "https://example.com/token");

        var userAgent = identity.BuildUserAgent("1.2.3");

        Assert.Equal("test-agent/1.2.3 (test)", userAgent);
    }

    [Fact]
    public void Identity_stores_all_fields_correctly()
    {
        var scopes = new[] { "user:profile", "user:inference" };
        var identity = new ClaudeOAuthClientIdentity(
            ClientId: "my-client-id",
            UserAgentTemplate: "my-agent/{version}",
            Scopes: scopes,
            AuthorizeUrl: "https://auth.example.com",
            TokenUrl: "https://token.example.com");

        Assert.Equal("my-client-id", identity.ClientId);
        Assert.Equal("my-agent/{version}", identity.UserAgentTemplate);
        Assert.Equal(scopes, identity.Scopes);
        Assert.Equal("https://auth.example.com", identity.AuthorizeUrl);
        Assert.Equal("https://token.example.com", identity.TokenUrl);
    }

    [Fact]
    public void Identity_is_immutable_record()
    {
        var identity1 = new ClaudeOAuthClientIdentity(
            ClientId: "id1",
            UserAgentTemplate: "ua1",
            Scopes: new[] { "s1" },
            AuthorizeUrl: "https://a1.com",
            TokenUrl: "https://t1.com");

        var identity2 = identity1 with { ClientId = "id2" };

        Assert.Equal("id1", identity1.ClientId);
        Assert.Equal("id2", identity2.ClientId);
        Assert.Equal(identity1.UserAgentTemplate, identity2.UserAgentTemplate);
    }
}
