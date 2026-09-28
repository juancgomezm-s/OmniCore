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
/// <para>
/// Toda lectura verifica el hash (ADR-0001 §7): GetText y Verify comparten una lectura
/// única del blob (una sola pasada, sin releer el archivo) y devuelven null/false si el
/// contenido no corresponde al <c>ContentHash</c> — aunque conserve la longitud — o si
/// la ruta del blob atraviesa un link (symlink o junction) que resuelve fuera de
/// blobs/sha256.
/// </para>
/// <para>
/// Límite documentado de la frontera de symlinks (NO está cerrada): la comprobación de
/// links y la lectura no son atómicas — la BCL no expone openat/O_NOFOLLOW — así que un
/// link intercambiado entre ambas puede desviar la lectura fuera del árbol de blobs. En
/// ese caso la integridad del contenido sigue garantizada (el hash se comprueba sobre lo
/// leído); lo que puede romperse transitoriamente es la propiedad "las lecturas no salen
/// de blobs/sha256". La frontera defendida es el árbol BAJO blobs/sha256: si el propio
/// directorio de datos o blobs/ son links, es configuración del workspace (ADR-0039),
/// no tampering del store.
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
        // Lectura verificada (ADR-0001 §7): null ante hash malformado, blob ausente, ruta
        // que atraviesa links fuera de blobs/sha256, o contenido que no corresponde al hash.
        return TryReadVerifiedText(hash);
    }

    public bool Verify(ContentHash hash, long expectedSize)
    {
        // La integridad la decide el hash (dentro de TryReadVerifiedText), no el tamaño: un
        // blob alterado falla aunque conserve la longitud. expectedSize usa la semántica
        // vigente de ArtifactRef.Size (caracteres del texto, ver PutText).
        var content = TryReadVerifiedText(hash);
        return content is not null && (long) content.Length == expectedSize;
    }

    /// <summary>
    /// Lectura única y verificada compartida por GetText y Verify (ADR-0001 §7): lee el
    /// blob UNA sola vez y comprueba el hash de lo leído. Devuelve null si el hash es
    /// malformado, el blob no existe, la ruta atraviesa links o el contenido no corresponde
    /// al <c>ContentHash</c>.
    /// </summary>
    private string? TryReadVerifiedText(ContentHash hash)
    {
        // Hash inválido → null sin construir ruta ni acceder a disco (ver TryBlobPath).
        if (!TryBlobPath(hash, out var blobPath) || !File.Exists(blobPath))
        {
            return null;
        }

        // Un link (symlink o junction) en la ruta haría leer contenido fuera de
        // blobs/sha256: la lectura se rechaza (frontera best-effort, ver límite en la
        // documentación de la clase).
        if (!BlobPathStaysInsideBlobs(blobPath))
        {
            return null;
        }

        try
        {
            // UNA sola lectura: releer el archivo permitiría que un cambio entre lecturas
            // haga pasar un contenido distinto del que se acaba de verificar (carrera).
            var content = File.ReadAllText(blobPath);
            return Sha256.Hex(content) == hash.Value ? content : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // El blob desapareció entre la comprobación y la lectura, o es ilegible: el
            // contenido no es recuperable, y eso es exactamente null en este contrato.
            return null;
        }
    }

    /// <summary>
    /// Rechaza rutas de blob que atraviesen links. El store nunca crea symlinks ni
    /// junctions, así que cualquier link — en el leaf o en un directorio intermedio — es
    /// manipulación y podría hacer leer contenido fuera de blobs/sha256, apunte adonde
    /// apunte. Fail-closed: si no se puede garantizar que la ruta está limpia, no se lee.
    /// <para>
    /// La frontera defendida es el árbol BAJO blobs/sha256 (ver limitación en la
    /// documentación de la clase: la comprobación no es atómica con la lectura).
    /// </para>
    /// </summary>
    private bool BlobPathStaysInsideBlobs(string blobPath)
    {
        try
        {
            // GetFullPath es puramente léxico (no resuelve links), pero canónica separadores
            // y segmentos ".." para que el walk contra _blobsRoot sea comparable, sea cual
            // sea la forma en que se construyó la ruta.
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_blobsRoot));
            var leaf = Path.GetFullPath(blobPath);

            // Directorios intermedios: ninguno puede ser un link (symlink en Unix; symlink
            // o junction en Windows, que Directory.ResolveLinkTarget también resuelve).
            for (var dir = Path.GetDirectoryName(leaf);
                 dir is not null && !string.Equals(dir, root, StringComparison.Ordinal);
                 dir = Path.GetDirectoryName(dir))
            {
                if (Directory.ResolveLinkTarget(dir, returnFinalTarget: false) is not null)
                {
                    return false;
                }
            }

            // El leaf tampoco puede ser un link, apunte adonde apunte.
            return File.ResolveLinkTarget(leaf, returnFinalTarget: false) is null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
