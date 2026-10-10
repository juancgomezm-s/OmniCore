using System.Security.Cryptography;
using System.Text;

namespace OmniCore.Models;

/// <summary>
/// PKCE S256 para el flujo OAuth de Claude Code.
/// Lógica pura, testeable, sin dependencias externas.
/// </summary>
public static class ClaudeOAuthPkce
{
    /// <summary>
    /// Genera un code verifier de 32 bytes (43 chars base64url).
    /// Equivalente a crypto.randomBytes(32) en Node.js.
    /// </summary>
    public static string GenerateCodeVerifier()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Genera un code challenge SHA-256 del verifier.
    /// Equivalente a createHash('sha256').update(verifier).digest() en Node.js.
    /// </summary>
    public static string GenerateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    /// <summary>
    /// Genera un state anti-CSRF de 32 bytes (43 chars base64url).
    /// </summary>
    public static string GenerateState()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    internal static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
