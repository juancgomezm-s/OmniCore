namespace OmniCore.Abstractions;

using OmniCore.Domain;

/// <summary>
/// Event Store append-only de una sesión (ADR-0002 §1). Un solo escritor por sesión asigna
/// la secuencia; las escrituras de una sesión se serializan en un canal.
/// </summary>
public interface IEventStore
{
    /// <summary>Persiste un evento, asignándole la siguiente secuencia de la sesión.</summary>
    void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability, CancellationToken cancellationToken);

    /// <summary>Persiste un lote de eventos de la misma sesión de forma atómica.</summary>
    void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> evts, DurabilityClass durability,
        CancellationToken cancellationToken);

    /// <summary>Devuelve la última secuencia persistida de la sesión (0 si no hay eventos).</summary>
    long CurrentSequence(SessionId sessionId);

    /// <summary>Replay de una sesión desde una secuencia (inclusive).</summary>
    IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive);
}

/// <summary>Optional read-only view of event types across every session in a workspace journal.</summary>
public interface IWorkspaceJournalReader
{
    /// <summary>Returns all persisted events of this type across sessions.</summary>
    IReadOnlyList<DomainEvent> ReadEvents(EventType type);
}

/// <summary>
/// Artifact Store inmutable y content-addressed (ADR-0001 §4–§6). Blobs en disco; la metadata
/// en el mismo SQLite. Orden obligatorio: primero el blob, después el evento que lo referencia.
/// </summary>
public interface IArtifactStore
{
    /// <summary>Escribe un blob de texto y devuelve su ref (sha256 en streaming).</summary>
    ArtifactRef PutText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity);

    /// <summary>Lee el contenido de un artifact por hash; null si no existe o el contenido no verifica contra el hash.</summary>
    string? GetText(ContentHash hash);

    /// <summary>Confirma que un blob existe y su hash coincide (sin corrupción).</summary>
    bool Verify(ContentHash hash, long expectedSize);
}

/// <summary>Optional exact, authenticated encryption for sensitive opaque checkpoints.
/// Ordinary artifact readers only see ciphertext. The purpose binds decryption to the
/// owning turn, model step and physical destination; implementations must fail closed.</summary>
public interface IProtectedArtifactStore
{
    ArtifactRef PutProtectedText(string content, string purpose, string mediaType, ArtifactKind kind);
    string GetProtectedText(ArtifactRef reference, string purpose);
}

/// <summary>Optional synchronous publication boundary: excludes GC until the caller has
/// committed its referencing event. Acquire/dispose and PutText run on the owning thread.</summary>
public interface IArtifactPublicationLease
{
    IDisposable AcquirePublicationLease(CancellationToken cancellationToken);
}

/// <summary>Optional preparation without CAS writes. The store applies its actual
/// redaction and hashing before returning a store-owned immutable publication handle.</summary>
public interface IArtifactPreparationStore
{
    IPreparedArtifact PrepareText(string content, string mediaType, ArtifactKind kind, Sensitivity sensitivity);
}

/// <summary>Exact future reference; Publish writes the prepared redacted bytes and
/// returns the same reference. Call under the journal publisher's short lease.</summary>
public interface IPreparedArtifact
{
    ArtifactRef Reference { get; }
    ArtifactRef Publish();
}

/// <summary>
/// Codificador de payloads de eventos canónicos (ADR-0013 §2). Cada EventType tiene exactamente
/// un codec; se registran aquí sin reflexión.
/// </summary>
public interface IDomainEventCodec
{
    /// <summary>Devuelve el evento tipado correspondiente al type + payload JSON.</summary>
    DomainEventPayload Decode(EventType type, string payloadJson);

    /// <summary>Serializa el payload a JSON (con sus campos clave descifrables).</summary>
    string Encode(DomainEventPayload payload);
}

/// <summary>
/// Registro de codecs por EventType con upcasters aplicados al leer (ADR-0013 §2). El store nunca
/// se reescribe: un evento persistido en una versión anterior se sube a la actual al decodificarlo.
/// </summary>
public interface IEventCodecRegistry
{
    IDomainEventCodec CodecFor(EventType type);

    /// <summary>Versión de schema actual de un tipo (la que se escribe hoy).</summary>
    int CurrentVersion(EventType type);

    /// <summary>
    /// Decodifica un evento persistido aplicando la cadena de upcasters desde su versión hasta la
    /// actual. Es la única forma correcta de leer el journal.
    /// </summary>
    DomainEventPayload Decode(DomainEvent evt);
}

/// <summary>
/// Sube el payload JSON de un tipo de evento de <see cref="FromVersion"/> a FromVersion + 1
/// (ADR-0013 §2). Puro y determinista: se aplica en cada lectura, nunca reescribe el store.
/// </summary>
public interface IEventUpcaster
{
    EventType Type { get; }

    int FromVersion { get; }

    string Upcast(string payloadJson);
}

/// <summary>
/// Token counter con identidad de tokenizador y exactitud declaradas (ADR-0042 §1). M1 usa el
/// FakeTokenCounter determinista en los tests.
/// </summary>
/// <summary>Sink de eventos de contexto/meta-modelo al Canonical Journal.</summary>
public interface IContextEventSink
{
    ValueTask AppendAsync(DomainEventPayload payload, CancellationToken cancellationToken);
}

/// <summary>Optional journal-owned synchronous boundary. Publishes prepared bytes
/// and the referencing event under one short lease; never carries the lease across await.</summary>
public interface IContextArtifactPublicationSink : IContextEventSink
{
    void AppendPreparedArtifact(IPreparedArtifact artifact, DomainEventPayload payload,
        CancellationToken cancellationToken);
}

public interface ITokenCounter
{
    TokenizerId Id { get; }

    TokenCountAccuracy Accuracy { get; }

    Task<int> CountAsync(ContextItem item, CancellationToken cancellationToken);
}

/// <summary>Exactitud de un conteo de tokens (ADR-0042 §1).</summary>
public enum TokenCountAccuracy
{
    Exact,
    Estimated,
}

/// <summary>Registro de auditoría redactado (ADR-0043 §1). Nunca contenido.</summary>
public sealed class AuditRecord
{
    public string EventName { get; }

    public WorkspaceId? Workspace { get; }

    public SessionId? Session { get; }

    public RunId? Run { get; }

    public DateTimeOffset Timestamp { get; }

    public string? EventRef { get; }

    public Dictionary<string, string> Details { get; }

    public AuditRecord(string eventName, WorkspaceId? workspace, SessionId? session, RunId? run,
        DateTimeOffset timestamp, string? eventRef, Dictionary<string, string> details)
    {
        EventName = eventName;
        Workspace = workspace;
        Session = session;
        Run = run;
        Timestamp = timestamp;
        EventRef = eventRef;
        Details = details;
    }
}

/// <summary>Consumidor de eventos hacia el audit log (INV-012: el Engine no escribe el audit).</summary>
public interface IAuditSink
{
    void Record(AuditRecord record, CancellationToken cancellationToken);
}
