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

    /// <summary>Digest de ocho palabras de 32 bits, en orden big-endian (uso diagnóstico/tests).</summary>
    public static int[] Bytes(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        var words = new int[8];
        for (var index = 0; index < words.Length; index++)
            words[index] = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(digest.AsSpan(index * 4, 4));
        return words;
    }
}
