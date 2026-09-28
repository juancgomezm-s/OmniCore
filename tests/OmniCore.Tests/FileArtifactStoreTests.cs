using OmniCore.Domain;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>
/// Pruebas deterministas del endurecimiento de FileArtifactStore.Verify/GetText (M4,
/// ADR-0001 §4–§7): el ContentHash se valida antes de construir cualquier ruta (sha256 +
/// 64 hex minúsculas), un blob ausente o alterado no verifica, y un hash malformado,
/// de otro algoritmo o con traversal no accede al filesystem fuera de blobs/sha256.
/// </summary>
public sealed class FileArtifactStoreTests
{
    private const string ValidHex64 = "0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void PutText_GetText_Verify_roundtrip_is_true_for_intact_blob()
    {
        var (root, data) = NewDirs();
        try
        {
            var store = new FileArtifactStore(data);
            var content = "hola mundo";

            var artifact = store.PutText(content, "text/plain", ArtifactKind.Other, Sensitivity.Normal);

            Assert.Equal(content, store.GetText(artifact.Hash));
            Assert.True(store.Verify(artifact.Hash, artifact.Size), "un blob recién escrito debe verificar");
        }
        finally
        {
            TryDeleteTree(root);
        }
    }

    [Fact]
    public void Verify_is_false_and_GetText_null_for_absent_blob()
    {
        var (root, data) = NewDirs();
        try
        {
            var store = new FileArtifactStore(data);
            var absent = ContentHash.Sha256(ValidHex64);

            Assert.Null(store.GetText(absent));
            Assert.False(store.Verify(absent, 0));
            // La consulta de un hash válido pero ausente no crea ni toca directorios.
            Assert.False(Directory.Exists(Path.Combine(data, "blobs")));
        }
        finally
        {
            TryDeleteTree(root);
        }
    }

    [Fact]
    public void Verify_is_false_for_altered_blob_even_with_same_length()
    {
        var (root, data) = NewDirs();
        try
        {
            var store = new FileArtifactStore(data);
            var artifact = store.PutText("contenido original", "text/plain", ArtifactKind.Other, Sensitivity.Normal);

            // Sabotaje en disco con la MISMA longitud (18 caracteres): el tamaño no delata
            // la alteración; la detecta el hash del contenido.
            var hex = artifact.Hash.Value;
            var blobPath = Path.Combine(data, "blobs", "sha256", hex.Substring(0, 2), hex.Substring(2, 2), hex);
            File.WriteAllText(blobPath, "contenido alterado");

            Assert.False(store.Verify(artifact.Hash, artifact.Size));
            Assert.False(store.Verify(artifact.Hash, 0));
        }
        finally
        {
            TryDeleteTree(root);
        }
    }

    [Fact]
    public void Verify_size_matches_text_characters_not_bytes_today()
    {
        var (root, data) = NewDirs();
        try
        {
            var store = new FileArtifactStore(data);
            // "café" son 4 caracteres pero 5 bytes UTF-8: fija la semántica vigente de
            // ArtifactRef.Size (longitud del texto). Si el contrato pasa a contar bytes,
            // este test debe voltearse junto con PutText y Verify (decisión pendiente).
            var artifact = store.PutText("café", "text/plain", ArtifactKind.Other, Sensitivity.Normal);

            Assert.Equal(4L, artifact.Size);
            Assert.True(store.Verify(artifact.Hash, 4));
            Assert.False(store.Verify(artifact.Hash, 5));
        }
        finally
        {
            TryDeleteTree(root);
        }
    }

    [Fact]
    public void Malformed_hash_never_reaches_the_filesystem()
    {
        var (root, data) = NewDirs();
        try
        {
            // Centinelas fuera de blobs/sha256: cualquier acceso (lectura o escritura) a
            // través de un traversal los leería o los dejaría modificados.
            var sibling = Path.Combine(root, "sibling.txt");
            var outside = Path.Combine(data, "outside-blobs.txt");
            File.WriteAllText(sibling, "hermano-fuera");
            File.WriteAllText(outside, "fuera-de-blobs");

            var store = new FileArtifactStore(data);
            var hashes = new ContentHash[]
            {
                // Otro algoritmo (o casing distinto): el contrato es "sha256" exacto.
                new("sha1", ValidHex64),
                new("SHA256", ValidHex64),
                new("", ValidHex64),
                new(null!, ValidHex64),
                // Longitud incorrecta, mayúsculas, caracteres fuera del alfabeto hex.
                new("sha256", new string('0', 63)),
                new("sha256", new string('0', 65)),
                new("sha256", new string('A', 64)),
                new("sha256", "g" + new string('0', 63)),
                new("sha256", new string(' ', 64)),
                new("sha256", "\0" + new string('0', 63)),
                // Traversal y variantes: nunca deben construirse como rutas.
                new("sha256", ".."),
                new("sha256", "../outside-blobs.txt"),
                new("sha256", "..\\..\\sibling.txt"),
                new("sha256", "../../" + new string('.', 60)),
                new("sha256", "%2e%2e%2f" + new string('0', 56)),
                new("sha256", new string('.', 64)),
            };

            foreach (var hash in hashes)
            {
                Assert.Null(store.GetText(hash));
                Assert.False(store.Verify(hash, 0));
                Assert.False(store.Verify(hash, long.MaxValue));
            }

            // Sin efectos: los centinelas siguen intactos y el directorio de datos solo
            // contiene lo que creó el test (ni siquiera existe blobs/ todavía).
            Assert.Equal("hermano-fuera", File.ReadAllText(sibling));
            Assert.Equal("fuera-de-blobs", File.ReadAllText(outside));
            Assert.Equal(new[] { "outside-blobs.txt" },
                Directory.EnumerateFileSystemEntries(data).Select(Path.GetFileName)
                    .OrderBy(name => name, StringComparer.Ordinal).ToArray());
            Assert.Equal(new[] { "data", "sibling.txt" },
                Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName)
                    .OrderBy(name => name, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            TryDeleteTree(root);
        }
    }

    private static (string Root, string Data) NewDirs()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-m4-artstore-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        return (root, data);
    }

    private static void TryDeleteTree(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception)
        {
            // best-effort: es un directorio temporal creado por el propio test
        }
    }
}
