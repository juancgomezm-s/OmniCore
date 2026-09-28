namespace OmniCore.Infrastructure;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Artifact Store content-addressed en filesystem (ADR-0041 §3). Blobs en
/// blobs/sha256/&lt;2&gt;/&lt;2&gt;/&lt;hash&gt; (ADR-0001 §5). Escritura temporal → rename
/// atómico cuando la API de la plataforma lo permita; en M1 con WriteAllText directo.
/// <para>
/// Lectura (GetText/Verify): el <c>ContentHash</c> se valida ANTES de construir cualquier
/// ruta (ADR-0001 §4: algoritmo "sha256" exacto y valor de 64 dígitos hexadecimales en
/// minúsculas). Un hash malformado, de otro algoritmo o con traversal nunca toca el
/// filesystem: GetText devuelve null y Verify false, sin salir de blobs/sha256.
/// </para>
/// </summary>
public sealed class FileArtifactStore : IArtifactStore
{
    private const string HashAlgorithm = "sha256";
    private const int HashHexLength = 64;

    private readonly string _blobsRoot;

    public FileArtifactStore(string dataDirectory)
    {
        _blobsRoot = Path.Combine(dataDirectory, "blobs", HashAlgorithm);
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

        // ArtifactRef.Size conserva la semántica vigente (M2): longitud del texto redactado
        // en CARACTERES (safe.Length), y Verify compara contra eso. Si Size debe pasar a
        // contar bytes UTF-8 (o depender del media type) es una decisión de contrato
        // pendiente (ADR-0001 §4): impacta el schema durable y los eventos ya escritos.
        return new ArtifactRef(ArtifactId.New(), ContentHash.Sha256(hash), (long) safe.Length, mediaType, kind,
            sensitivity);
    }

    public string? GetText(ContentHash hash)
    {
        // Hash inválido → null sin construir ruta ni acceder a disco (ver TryBlobPath).
        return TryBlobPath(hash, out var blobPath) && File.Exists(blobPath)
            ? File.ReadAllText(blobPath)
            : null;
    }

    public bool Verify(ContentHash hash, long expectedSize)
    {
        if (!TryBlobPath(hash, out var blobPath) || !File.Exists(blobPath))
        {
            return false;
        }

        // Una sola lectura del blob: releer el archivo (como se hacía antes) permite que un
        // cambio entre lecturas haga verificar un tamaño que no corresponde al contenido
        // cuyo hash se acaba de comprobar.
        var content = File.ReadAllText(blobPath);

        // La integridad la decide el hash, no el tamaño: un blob alterado falla aquí aunque
        // conserve la longitud. expectedSize usa la semántica vigente de ArtifactRef.Size
        // (caracteres del texto, ver PutText).
        return Sha256.Hex(content) == hash.Value
            && (long) content.Length == expectedSize;
    }

    /// <summary>
    /// Valida el hash y solo entonces construye la ruta del blob. Devuelve false si el
    /// algoritmo no es "sha256" exacto o el valor no son 64 dígitos hexadecimales en
    /// minúsculas: en ese caso no se accede al filesystem en absoluto.
    /// </summary>
    private bool TryBlobPath(ContentHash hash, out string blobPath)
    {
        blobPath = string.Empty;

        // Con el alfabeto restringido a [0-9a-f] y longitud fija de 64, ningún componente
        // del nombre puede contener separadores de ruta ni '..', así que la ruta construida
        // es estructuralmente incapaz de salir de blobs/sha256 (traversal imposible).
        if (hash is null
            || !string.Equals(hash.Algorithm, HashAlgorithm, StringComparison.Ordinal)
            || !IsLowercaseHex64(hash.Value))
        {
            return false;
        }

        blobPath = BlobPath(hash.Value);
        return true;
    }

    private static bool IsLowercaseHex64(string value)
    {
        if (value is null || value.Length != HashHexLength)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Requiere 64 hex minúsculas: lo garantiza TryBlobPath o Sha256.Hex (PutText).</summary>
    private string BlobPath(string hashHex)
    {
        return Path.Combine(_blobsRoot, hashHex.Substring(0, 2), hashHex.Substring(2, 2), hashHex);
    }
}
