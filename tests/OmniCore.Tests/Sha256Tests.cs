using System.Text;
using OmniCore.Domain;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>
/// Sha256 debe ser SHA-256 REAL (FIPS 180-4): la versión anterior fallaba incluso el vector
/// NIST de "abc" (shifts con extensión de signo) y hasheaba chars truncados en vez de bytes
/// UTF-8. Estos tests fijan el contrato de ADR-0001 §5: la dirección de un artifact es el
/// sha256 exacto de los bytes del blob, comprobado contra los vectores NIST oficiales y
/// contra una implementación independiente (System.Security.Cryptography).
/// </summary>
public sealed class Sha256Tests
{
    [Theory]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq",
        "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1")]
    [InlineData("abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu",
        "cf5b16a778af8380036ce59e7b0492370b249b11e8f07a51afac45037afee9d1")]
    public void Nist_vectors(string message, string expected)
    {
        Assert.Equal(expected, Sha256.Hex(message));
    }

    [Fact]
    public void Million_a_vector()
    {
        // Vector NIST de un millón de 'a': exige el schedule y el padding multibloque correctos.
        var million = new string('a', 1_000_000);
        Assert.Equal("cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0",
            Sha256.Hex(million));
    }

    [Theory]
    [InlineData("respuesta del modelo purgado")]
    [InlineData("orfeano-viejo")]
    [InlineData("café ñ ü ¥ ✓ emoji 🚀 texto")] // no-ASCII: el digest es de bytes UTF-8
    [InlineData("línea con retorno\r\n y tab\t")]
    public void Agrees_with_independent_implementation(string content)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var independent = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();
        Assert.Equal(independent, Sha256.Hex(content));
    }

    [Fact]
    public void Artifact_address_is_the_real_sha256_of_the_blob_bytes()
    {
        // ADR-0001 §5: la dirección del artifact debe ser el sha256 de SU CONTENIDO real
        // (los bytes UTF-8 escritos en el blob), no de una función privada cualquiera.
        var root = Path.Combine(Path.GetTempPath(), "omnicore-sha256-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileArtifactStore(root);
            var content = "contenido con acentos ñ y emoji 🚀 para UTF-8";
            var artifact = store.PutText(content, "text/plain", ArtifactKind.ModelResponse,
                Sensitivity.Normal);

            using var sha = System.Security.Cryptography.SHA256.Create();
            var blobPath = Path.Combine(root, "blobs", "sha256",
                artifact.Hash.Value.Substring(0, 2), artifact.Hash.Value.Substring(2, 2),
                artifact.Hash.Value);
            var diskDigest = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(blobPath)))
                .ToLowerInvariant();
            Assert.Equal(artifact.Hash.Value, diskDigest);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Mejor esfuerzo en Windows.
            }
        }
    }

    [Fact]
    public void Bytes_returns_the_digest_words_of_hex()
    {
        // "abc" → digest ba7816bf...: primera palabra big-endian = 0xba7816bf.
        var words = Sha256.Bytes("abc");
        Assert.Equal(unchecked((int) Convert.ToUInt32("ba7816bf", 16)), words[0]);
        Assert.Equal(8, words.Length);
    }
}
