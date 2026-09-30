namespace OmniCore.Infrastructure;

using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Artifact Store content-addressed en filesystem (ADR-0001 §5, ADR-0041 §3). Blobs en
/// blobs/sha256/&lt;2&gt;/&lt;2&gt;/&lt;hash&gt;. El hash y el tamaño se calculan sobre los bytes UTF-8
/// que se escriben; la escritura es temporal → flush → rename, y la lectura verifica el hash
/// (un blob alterado nunca se devuelve como si fuera el original).
/// </summary>
public sealed class FileArtifactStore : IArtifactStore
{
    private readonly string _blobsRoot;

    private readonly ISecretRedactor? _redactor;

    public FileArtifactStore(string dataDirectory, ISecretRedactor? redactor = null)
    {
        _blobsRoot = Path.Combine(dataDirectory, "blobs", "sha256");
        _redactor = redactor ?? SecretRedactorRegistry.Current;
    }

    public ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity)
    {
        // Redacción obligatoria del contenido antes de persistir (ADR-0018 §4): ningún blob
        // del store lleva secretos en claro.
        var safe = new RedactionPolicy().Redact(content);
        if (_redactor is not null) safe = _redactor.Redact(safe);
        var redacted = !string.Equals(safe, content, StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(safe);
        var hash = Sha256.Hex(bytes);
        var blobPath = BlobPath(hash);
        if (!File.Exists(blobPath))
        {
            WriteAtomically(blobPath, bytes);
        }

        return new ArtifactRef(ArtifactId.New(), ContentHash.Sha256(hash), bytes.LongLength, mediaType, kind,
            sensitivity, redacted);
    }

    /// <summary>
    /// Devuelve el contenido si el blob existe y su hash coincide. Un blob ausente devuelve null;
    /// uno cuyo contenido no corresponde a su hash lanza <see cref="InvalidDataException"/>
    /// (ADR-0001 §7: ArtifactCorrupted), nunca se entrega en silencio.
    /// </summary>
    public string? GetText(ContentHash hash)
    {
        var blobPath = BlobPath(hash.Value);
        if (!File.Exists(blobPath))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(blobPath);
        if (!Sha256.Hex(bytes).Equals(hash.Value, StringComparison.Ordinal))
        {
            throw new InvalidDataException("artifact corrupto: el contenido no coincide con su hash " + hash.Value);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    public bool Verify(ContentHash hash, long expectedSize)
    {
        var blobPath = BlobPath(hash.Value);
        if (!File.Exists(blobPath))
        {
            return false;
        }

        var bytes = File.ReadAllBytes(blobPath);
        return bytes.LongLength == expectedSize && Sha256.Hex(bytes).Equals(hash.Value, StringComparison.Ordinal);
    }

    private static void WriteAtomically(string blobPath, byte[] bytes)
    {
        var parent = Path.GetDirectoryName(blobPath)!;
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, "." + Path.GetFileName(blobPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temp, blobPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(blobPath))
            {
                // Otro escritor publicó el mismo blob (mismo hash → mismo contenido).
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private string BlobPath(string hashHex)
    {
        if (hashHex.Length < 4)
        {
            return Path.Combine(_blobsRoot, hashHex);
        }

        return Path.Combine(_blobsRoot, hashHex.Substring(0, 2), hashHex.Substring(2, 2), hashHex);
    }
}
