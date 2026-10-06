namespace OmniCore.Infrastructure;

using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Resultado de sondear un blob: distingue el motivo exacto del rechazo.</summary>
public enum BlobStatus
{
    /// <summary>El blob existe y su contenido verifica contra el hash (Content en el probe).</summary>
    Ok,

    /// <summary>Hash malformado (algoritmo o valor): no se consultó el filesystem.</summary>
    HashInvalid,

    /// <summary>No hay archivo en la ruta del blob.</summary>
    Missing,

    /// <summary>La ruta atraviesa un link (symlink/junction) fuera de blobs/sha256.</summary>
    LinkEscape,

    /// <summary>La lectura falló (desapareció a mitad o es ilegible).</summary>
    Unreadable,

    /// <summary>El contenido no corresponde al hash, aunque conserve la longitud.</summary>
    Corrupted,
}

/// <summary>
/// Sondeo read-only de un blob (ADR-0001 §7): una única lectura verificada y el motivo exacto
/// del rechazo. Lo consume <c>JournalVerifier</c> (verify-journal, M4) para reportar
/// Ausente y Corrupto como problemas distintos en lugar de colapsarlos a un null.
/// </summary>
public sealed class BlobProbe
{
    /// <summary>Por qué el blob pasó o no.</summary>
    public BlobStatus Status { get; }

    /// <summary>Contenido leído; solo distinto de null cuando Status es Ok.</summary>
    public string? Content { get; }

    private BlobProbe(BlobStatus status, string? content)
    {
        Status = status;
        Content = content;
    }

    /// <summary>Construye un probe (factory explícita, sin reflexión).</summary>
    public static BlobProbe Of(BlobStatus status, string? content) => new(status, content);
}

/// <summary>
/// Artifact Store content-addressed en filesystem (ADR-0001 §5, ADR-0041 §3). Blobs en
/// blobs/sha256/&lt;2&gt;/&lt;2&gt;/&lt;hash&gt;. El texto se redacta antes de hashear y escribir;
/// la publicación usa temporal → flush → rename atómico y las refs registran si hubo redacción.
/// <para>
/// Lectura (GetText/Verify): el <c>ContentHash</c> se valida ANTES de construir cualquier
/// ruta (ADR-0001 §4: algoritmo "sha256" exacto y valor de 64 dígitos hexadecimales en
/// minúsculas). Un hash malformado, de otro algoritmo o con traversal nunca toca el
/// filesystem: GetText devuelve null y Verify false, sin salir de blobs/sha256.
/// </para>
/// <para>
/// Toda lectura verifica el hash (ADR-0001 §7): GetText, Verify y Probe comparten una lectura
/// única del blob (una sola pasada, sin releer el archivo) y rechazan el contenido que no
/// corresponde al <c>ContentHash</c> — aunque conserve la longitud — o cuya ruta atraviesa un
/// link (symlink o junction) que resuelve fuera de blobs/sha256. <see cref="Probe"/> expone
/// el motivo exacto del rechazo (hash malformado, ausente, link, ilegible o corrupto) para
/// verify-journal (M4), sin colapsar todo a null.
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
public sealed class FileArtifactStore : IArtifactStore, IArtifactPublicationLease
{
    private const string HashAlgorithm = "sha256";
    private const int HashHexLength = 64;

    private readonly string _dataDirectory;
    private readonly string _blobsRoot;
    private readonly ISecretRedactor? _redactor;

    private readonly ThreadLocal<FileStream?> _publicationLease = new();

    public IDisposable AcquirePublicationLease(CancellationToken cancellationToken)
    {
        if (_publicationLease.Value is not null) throw new InvalidOperationException("Publication lease is already held.");
        var lease = ArtifactStoreLease.Acquire(_dataDirectory, cancellationToken);
        _publicationLease.Value = lease;
        return new PublicationScope(this, lease, Environment.CurrentManagedThreadId);
    }

    private sealed class PublicationScope(FileArtifactStore owner, FileStream lease, int thread) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            if (Environment.CurrentManagedThreadId != thread)
                throw new InvalidOperationException("Publication lease must be disposed by its owning thread.");
            owner._publicationLease.Value = null;
            lease.Dispose();
            _disposed = true;
        }
    }

    public FileArtifactStore(string dataDirectory, ISecretRedactor? redactor = null)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _blobsRoot = Path.Combine(_dataDirectory, "blobs", HashAlgorithm);
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
        using (_publicationLease.Value is null ? ArtifactStoreLease.Acquire(_dataDirectory, CancellationToken.None) : null)
        {
            if (!File.Exists(blobPath)) WriteAtomically(blobPath, bytes);
            // Touch even a deduplicated blob: it is now in-flight content until the caller
            // appends the event reference, so the configured GC grace period must protect it.
            File.SetLastWriteTimeUtc(blobPath, DateTime.UtcNow);
        }

        return new ArtifactRef(ArtifactId.New(), ContentHash.Sha256(hash), bytes.LongLength, mediaType, kind,
            sensitivity, redacted);
    }

    public string? GetText(ContentHash hash)
    {
        var probe = ProbeBlob(hash);
        if (probe.Status == BlobStatus.Corrupted)
            throw new InvalidDataException("artifact corrupto: el contenido no coincide con su hash " + hash.Value);
        return probe.Content;
    }

    public bool Verify(ContentHash hash, long expectedSize)
    {
        var probe = ProbeBlob(hash);
        return probe.Status == BlobStatus.Ok && probe.Content is not null
            && (long) Encoding.UTF8.GetByteCount(probe.Content) == expectedSize;
    }

    /// <summary>
    /// Sondeo read-only (verify-journal, M4): la misma lectura única y verificada de
    /// GetText/Verify, pero distingue el motivo del rechazo (hash malformado, ausente,
    /// link, ilegible o corrupto) en lugar de colapsarlo a null.
    /// </summary>
    public BlobProbe Probe(ContentHash hash) => ProbeBlob(hash);

    /// <summary>
    /// Lectura única y verificada compartida por GetText, Verify y Probe (ADR-0001 §7): lee el
    /// blob UNA sola vez y comprueba el hash de lo leído. Devuelve el estado exacto si el hash
    /// es malformado, el blob no existe, la ruta atraviesa links, la lectura falla o el
    /// contenido no corresponde al <c>ContentHash</c>.
    /// </summary>
    private BlobProbe ProbeBlob(ContentHash hash)
    {
        // Hash inválido → sin construir ruta ni acceder a disco (ver TryBlobPath).
        if (!TryBlobPath(hash, out var blobPath))
        {
            return BlobProbe.Of(BlobStatus.HashInvalid, null);
        }

        if (!File.Exists(blobPath))
        {
            return BlobProbe.Of(BlobStatus.Missing, null);
        }

        // Un link (symlink o junction) en la ruta haría leer contenido fuera de
        // blobs/sha256: la lectura se rechaza (frontera best-effort, ver límite en la
        // documentación de la clase).
        if (!BlobPathStaysInsideBlobs(blobPath))
        {
            return BlobProbe.Of(BlobStatus.LinkEscape, null);
        }

        try
        {
            // UNA sola lectura: releer el archivo permitiría que un cambio entre lecturas
            // haga pasar un contenido distinto del que se acaba de verificar (carrera).
            var bytes = File.ReadAllBytes(blobPath);
            return Sha256.Hex(bytes) == hash.Value
                ? BlobProbe.Of(BlobStatus.Ok, Encoding.UTF8.GetString(bytes))
                : BlobProbe.Of(BlobStatus.Corrupted, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // El blob desapareció entre la comprobación y la lectura, o es ilegible: el
            // contenido no es recuperable, y eso es exactamente Unreadable en este contrato.
            return BlobProbe.Of(BlobStatus.Unreadable, null);
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
            try { File.Move(temp, blobPath, overwrite: false); }
            catch (IOException) when (File.Exists(blobPath)) { /* Another writer published identical content. */ }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Requiere 64 hex minúsculas: lo garantiza TryBlobPath o Sha256.Hex (PutText).</summary>
    private string BlobPath(string hashHex)
    {
        return Path.Combine(_blobsRoot, hashHex.Substring(0, 2), hashHex.Substring(2, 2), hashHex);
    }
}
