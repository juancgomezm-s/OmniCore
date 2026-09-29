namespace OmniCore.Infrastructure;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// SHA-256 para el content addressing del Artifact Store (ADR-0001 §4–§5). Usa la
/// implementación de la BCL sobre los bytes UTF-8 del contenido: la identidad de un blob es el
/// hash de los bytes que se escriben en disco, no de una proyección de los caracteres.
/// </summary>
public static class Sha256
{
    /// <summary>Hash SHA-256 en hex minúscula de los bytes UTF-8 del texto.</summary>
    public static string Hex(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Hex(Encoding.UTF8.GetBytes(content));
    }

    /// <summary>Hash SHA-256 en hex minúscula de una secuencia de bytes.</summary>
    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
