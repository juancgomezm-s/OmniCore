using System.Text;
using OmniCore.Models;

namespace OmniCore.Tests;

/// <summary>
/// Tests deterministas para ClaudeOAuthPkce (§2.1 del plan).
/// Sin red, sin autenticación real.
/// </summary>
public sealed class ClaudeOAuthPkceTests
{
    [Fact]
    public void GenerateCodeVerifier_returns_43_char_base64url_string()
    {
        var verifier = ClaudeOAuthPkce.GenerateCodeVerifier();

        Assert.Equal(43, verifier.Length);
        // Base64url: solo [A-Za-z0-9_-], sin '=' padding
        Assert.Matches("^[A-Za-z0-9_-]{43}$", verifier);
    }

    [Fact]
    public void GenerateState_returns_43_char_base64url_string()
    {
        var state = ClaudeOAuthPkce.GenerateState();

        Assert.Equal(43, state.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", state);
    }

    [Fact]
    public void GenerateCodeChallenge_produces_SHA256_of_verifier()
    {
        var verifier = "test-verifier-string-for-deterministic-test";
        var expectedHash = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(verifier)))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var challenge = ClaudeOAuthPkce.GenerateCodeChallenge(verifier);

        Assert.Equal(expectedHash, challenge);
    }

    [Fact]
    public void GenerateCodeChallenge_with_known_vector_matches_expected()
    {
        // Vector conocido: verifier fijo → challenge determinístico
        var verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        // SHA-256 de ese verifier en base64url
        var expected = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        var challenge = ClaudeOAuthPkce.GenerateCodeChallenge(verifier);

        Assert.Equal(expected, challenge);
    }

    [Fact]
    public void GenerateCodeVerifier_returns_different_values_per_call()
    {
        var v1 = ClaudeOAuthPkce.GenerateCodeVerifier();
        var v2 = ClaudeOAuthPkce.GenerateCodeVerifier();

        Assert.NotEqual(v1, v2);
    }

    [Fact]
    public void Base64UrlEncode_strips_padding_and_replaces_chars()
    {
        // bytes que producen '+' y '/' en base64 estándar
        var bytes = new byte[] { 0xFB, 0xFF, 0xFE };
        var encoded = ClaudeOAuthPkce.Base64UrlEncode(bytes);

        Assert.DoesNotContain("+", encoded);
        Assert.DoesNotContain("/", encoded);
        Assert.DoesNotContain("=", encoded);
    }
}
