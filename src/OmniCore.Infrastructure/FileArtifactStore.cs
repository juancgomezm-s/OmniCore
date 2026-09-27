namespace OmniCore.Infrastructure;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Artifact Store content-addressed en filesystem (ADR-0041 §3). Blobs en
/// blobs/sha256/&lt;2&gt;/&lt;2&gt;/&lt;hash&gt; (ADR-0001 §5). Escritura temporal → rename
/// atómico cuando la API de la plataforma lo permita; en M1 con WriteAllText directo.
/// </summary>
public sealed class FileArtifactStore : IArtifactStore
{
    private readonly string _blobsRoot;

    public FileArtifactStore(string dataDirectory)
    {
        _blobsRoot = Path.Combine(dataDirectory, "blobs", "sha256");
    }

    public ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity)
    {
        // Redacción obligatoria del contenido antes de persistir (ADR-0018 §4): ningún blob
        // del store lleva secretos en claro.
        var safe = new OmniCore.Domain.RedactionPolicy().Redact(content);
        var hash = Sha256.Hex(safe);
        var blobPath = BlobPath(hash);
        if (!File.Exists(blobPath))
        {
            var parent = Path.GetDirectoryName(blobPath)!;
            Directory.CreateDirectory(parent);
            File.WriteAllText(blobPath, safe);
        }

        return new ArtifactRef(ArtifactId.New(), ContentHash.Sha256(hash), (long) safe.Length, mediaType, kind,
            sensitivity);
    }

    public string? GetText(ContentHash hash)
    {
        var blobPath = BlobPath(hash.Value);
        return File.Exists(blobPath) ? File.ReadAllText(blobPath) : null;
    }

    public bool Verify(ContentHash hash, long expectedSize)
    {
        var blobPath = BlobPath(hash.Value);
        return File.Exists(blobPath)
            && Sha256.Hex(File.ReadAllText(blobPath)) == hash.Value
            && (long) File.ReadAllText(blobPath).Length == expectedSize;
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