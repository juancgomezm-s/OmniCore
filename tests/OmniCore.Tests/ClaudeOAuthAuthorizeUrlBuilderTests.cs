using OmniCore.Models;

namespace OmniCore.Tests;

/// <summary>
/// Tests para ClaudeOAuthAuthorizeUrlBuilder (§2.2 del plan).
/// Verifica la construcción correcta de la URL de authorize con todos los parámetros.
/// </summary>
public sealed class ClaudeOAuthAuthorizeUrlBuilderTests
{
    private static readonly ClaudeOAuthClientIdentity TestIdentity = new(
        ClientId: "test-client-id",
        UserAgentTemplate: "test/{version}",
        Scopes: new[] { "user:profile", "user:inference", "user:sessions" },
        AuthorizeUrl: "https://example.com/oauth/authorize",
        TokenUrl: "https://example.com/oauth/token");

    [Fact]
    public void Build_includes_all_required_parameters()
    {
        var url = ClaudeOAuthAuthorizeUrlBuilder.Build(
            identity: TestIdentity,
            codeChallenge: "test-challenge",
            state: "test-state",
            port: 8080);

        Assert.Contains("code=true", url);
        Assert.Contains($"client_id={Uri.EscapeDataString(TestIdentity.ClientId)}", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains($"redirect_uri={Uri.EscapeDataString("http://localhost:8080/callback")}", url);
        Assert.Contains($"scope={Uri.EscapeDataString(string.Join(' ', TestIdentity.Scopes))}", url);
        Assert.Contains("code_challenge=test-challenge", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("state=test-state", url);
    }

    [Fact]
    public void Build_with_manual_redirect_uses_manual_url()
    {
        var url = ClaudeOAuthAuthorizeUrlBuilder.Build(
            identity: TestIdentity,
            codeChallenge: "challenge",
            state: "state",
            port: 8080,
            isManual: true);

        Assert.Contains($"redirect_uri={Uri.EscapeDataString("https://platform.claude.com/oauth/code/callback")}", url);
    }

    [Fact]
    public void Build_with_inferenceOnly_uses_single_scope()
    {
        var url = ClaudeOAuthAuthorizeUrlBuilder.Build(
            identity: TestIdentity,
            codeChallenge: "challenge",
            state: "state",
            port: 8080,
            inferenceOnly: true);

        Assert.Contains($"scope={Uri.EscapeDataString("user:inference")}", url);
        Assert.DoesNotContain("user:profile", url);
    }

    [Fact]
    public void Build_with_optional_params_includes_them()
    {
        var url = ClaudeOAuthAuthorizeUrlBuilder.Build(
            identity: TestIdentity,
            codeChallenge: "challenge",
            state: "state",
            port: 8080,
            orgUuid: "org-123",
            loginHint: "user@example.com",
            loginMethod: "sso");

        Assert.Contains("orgUUID=org-123", url);
        Assert.Contains($"login_hint={Uri.EscapeDataString("user@example.com")}", url);
        Assert.Contains("login_method=sso", url);
    }

    [Fact]
    public void Build_escapes_special_characters_in_parameters()
    {
        var url = ClaudeOAuthAuthorizeUrlBuilder.Build(
            identity: TestIdentity,
            codeChallenge: "chal+len/ge=",
            state: "st+ate/",
            port: 8080,
            loginHint: "user+tag@example.com");

        // Los caracteres especiales deben estar escapados
        Assert.DoesNotContain("chal+len/ge=", url);
        Assert.Contains("chal", url);
        Assert.DoesNotContain("st+ate/", url);
        Assert.Contains("st", url);
    }

    [Fact]
    public void Build_starts_with_authorize_url()
    {
        var url = ClaudeOAuthAuthorizeUrlBuilder.Build(
            identity: TestIdentity,
            codeChallenge: "challenge",
            state: "state",
            port: 8080);

        Assert.StartsWith(TestIdentity.AuthorizeUrl + "?", url);
    }
}
