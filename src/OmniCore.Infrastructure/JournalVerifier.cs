namespace OmniCore.Infrastructure;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Código de cada problema detectado por <see cref="JournalVerifier"/> (spec §71: tipados).</summary>
public enum JournalIssueCode
{
    /// <summary>El archivo del journal no existe, no es una base SQLite o no se puede abrir read-only.</summary>
    JournalUnreadable,

    /// <summary>El session_id almacenado no es un GUID válido.</summary>
    SessionIdInvalid,

    /// <summary>La sesión pedida no tiene eventos en el journal (solo VerifySession).</summary>
    SessionNotFound,

    /// <summary>La secuencia no es un entero ≥ 1.</summary>
    SequenceInvalid,

    /// <summary>El primer evento del stream no tiene seq 1 (ADR-0001 §3).</summary>
    SequenceStartsNotAtOne,

    /// <summary>Hueco: la seq salta por encima de la esperada en orden de almacenamiento.</summary>
    SequenceGap,

    /// <summary>Regresión o duplicado: la seq queda por debajo de la esperada.</summary>
    SequenceOutOfOrder,

    /// <summary>Un campo del envelope no parsea (EventId, timestamp, ids, causación, schema_version, payload).</summary>
    EnvelopeFieldInvalid,

    /// <summary>EventType sin codec registrado: schema futuro sin upcaster (ADR-0013 §2).</summary>
    UnknownEventType,

    /// <summary>El payload no decodifica con el codec de su EventType.</summary>
    PayloadDecodeFailed,

    /// <summary>
    /// El payload persistido no es el JSON canónico del objeto que decodifica: fue mutado o
    /// escrito por un writer ajeno al runtime (que siempre persiste via codecs, ADR-0013 §2).
    /// El runtime escribe con codecs (JSON canónico) y redacta ANTES de persistir, así que lo
    /// almacenado debe coincidir exactamente con el re-encode del objeto decodificado.
    /// </summary>
    PayloadTampered,

    /// <summary>El schema_version del envelope no coincide con el que declara el payload.</summary>
    SchemaVersionMismatch,

    /// <summary>La columna artifacts del envelope no sigue el contrato "id|alg;hash;...".</summary>
    ArtifactRefMalformed,

    /// <summary>Hash malformado en una referencia (no "sha256" + 64 hex minúsculas; ADR-0001 §4).</summary>
    ArtifactHashInvalid,

    /// <summary>El blob referenciado no existe (ADR-0001 §7: ArtifactMissing).</summary>
    ArtifactMissing,

    /// <summary>El blob existe pero su contenido no verifica contra el hash (ArtifactCorrupted).</summary>
    ArtifactCorrupted,

    /// <summary>La ruta del blob atraviesa links fuera de blobs/sha256 (frontera de FileArtifactStore).</summary>
    ArtifactLinkEscape,

    /// <summary>El blob existe pero es ilegible (IOException/UnauthorizedAccess al leer).</summary>
    ArtifactUnreadable,

    /// <summary>El contenido verifica por hash, pero el tamaño no coincide con ArtifactRef.Size.</summary>
    ArtifactSizeMismatch,
}

/// <summary>Un problema del journal, localizado con precisión (sesión, seq, evento, campo).</summary>
public sealed class JournalIssue
{
    /// <summary>Código tipado del problema.</summary>
    public JournalIssueCode Code { get; }

    /// <summary>SessionId tal como está almacenado (crudo; puede no ser un GUID válido).</summary>
    public string? SessionId { get; }

    /// <summary>Secuencia del evento afectado, si se pudo leer.</summary>
    public long? Sequence { get; }

    /// <summary>EventId tal como está almacenado (crudo).</summary>
    public string? EventId { get; }

    /// <summary>EventType tal como está almacenado (crudo).</summary>
    public string? EventType { get; }

    /// <summary>Campo o referencia afectada (p. ej. "payload", "artifacts[2]", "ModelCompleted.responseArtifact").</summary>
    public string? Field { get; }

    /// <summary>Diagnóstico preciso, en español, con los valores esperados y encontrados.</summary>
    public string Detail { get; }

    public JournalIssue(JournalIssueCode code, string? sessionId, long? sequence, string? eventId,
        string? eventType, string? field, string detail)
    {
        Code = code;
        SessionId = sessionId;
        Sequence = sequence;
        EventId = eventId;
        EventType = eventType;
        Field = field;
        Detail = detail;
    }
}

/// <summary>Reporte estructurado de una verificación: nunca una excepción, nunca un problema oculto.</summary>
public sealed class JournalVerificationReport
{
    /// <summary>Ruta del journal verificada.</summary>
    public string JournalPath { get; }

    /// <summary>false: ni siquiera se pudo abrir (existe un único problema JournalUnreadable).</summary>
    public bool JournalOpened { get; }

    /// <summary>Cantidad de streams (session_id distintos) examinados.</summary>
    public long SessionCount { get; }

    /// <summary>Cantidad de filas de evento examinadas.</summary>
    public long EventCount { get; }

    /// <summary>Cantidad de referencias a artifacts verificadas (del envelope y del payload).</summary>
    public long ArtifactRefCount { get; }

    /// <summary>Problemas encontrados en total, aunque <see cref="Issues"/> esté truncada.</summary>
    public long TotalIssueCount { get; }

    /// <summary>true si <see cref="Issues"/> no lista todos los problemas (tope de MaxIssues).</summary>
    public bool IssuesTruncated { get; }

    /// <summary>Problemas listados (a lo más MaxIssues), en orden de descubrimiento.</summary>
    public IReadOnlyList<JournalIssue> Issues { get; }

    /// <summary>true si no se encontró ningún problema.</summary>
    public bool Ok => TotalIssueCount == 0;

    public JournalVerificationReport(string journalPath, bool journalOpened, long sessionCount, long eventCount,
        long artifactRefCount, long totalIssueCount, bool issuesTruncated, IReadOnlyList<JournalIssue> issues)
    {
        JournalPath = journalPath;
        JournalOpened = journalOpened;
        SessionCount = sessionCount;
        EventCount = eventCount;
        ArtifactRefCount = artifactRefCount;
        TotalIssueCount = totalIssueCount;
        IssuesTruncated = issuesTruncated;
        Issues = issues;
    }

    /// <summary>Resumen de una línea, en español, para CLI y logs.</summary>
    public string SummaryLine()
    {
        var head = "verify-journal: " + Path.GetFileName(JournalPath) + " — " + SessionCount + " sesión(es), "
            + EventCount + " evento(s), " + ArtifactRefCount + " ref(s) de artifacts";
        if (Ok)
        {
            return head + " — OK";
        }

        var listed = Issues.Count == (int) TotalIssueCount
            ? TotalIssueCount.ToString()
            : Issues.Count + " de " + TotalIssueCount;
        return head + " — " + TotalIssueCount + " problema(s) (se listan " + listed + ")";
    }
}

/// <summary>
/// Núcleo read-only de <c>omni verify-journal</c> (ADR-0001 §7, M4): recorre los eventos SQLite
/// de una sesión (un stream) o de todo el journal, valida la secuencia y el orden, decodifica
/// el payload con el codec registrado de su EventType (upcasters vacíos en M1, ADR-0013 §2) y
/// verifica las referencias a artifacts por hash y tamaño mediante
/// <see cref="FileArtifactStore"/>.
/// <para>
/// Reutiliza los contratos existentes: el schema de <see cref="SqliteEventStore"/> (tabla
/// events y su columna artifacts "id|alg|hash;...", ver <c>Parts.Artifacts</c>), el registro
/// <see cref="IEventCodecRegistry"/> y el sondeo de blobs de FileArtifactStore. No re-deriva
/// nada de los eventos: solo lee y comprueba lo persistido.
/// </para>
/// <para>
/// Nunca muta el journal: abre la base con Mode=ReadOnly — un intento de escritura falla en el
/// driver — y no ejecuta ninguna sentencia que no sea un SELECT. Si el journal quedó con un
/// hot journal tras un crash, SQLite no puede abrirlo read-only y eso se reporta como
/// JournalUnreadable (abrirlo una vez con el runtime lo recupera y permite verificar).
/// </para>
/// <para>
/// Tampoco oculta corrupción: cada campo dañado produce como máximo un problema con su código
/// y ubicación, el recorrido sigue tras el primer fallo (resincronizando la secuencia esperada
/// para no desbordar el reporte con errores en cascada) y el conteo total nunca se trunca.
/// </para>
/// <para>
/// Límites documentados del núcleo M4: las referencias del envelope llevan hash pero no tamaño
/// (el formato de la columna no lo incluye), así que se verifican por hash; las referencias del
/// payload (p. ej. <c>ModelCompleted.ResponseArtifact</c>) se verifican por hash y tamaño. El
/// verificador no comprueba la máquina de estados ni la causalidad entre eventos: eso es
/// replay, no verify. La metadata de artifacts (tabla artifacts/artifact_refs del ADR-0001 §5)
/// aún no existe como schema durable; cuando llegue, este verificador debe añadirla al
/// recorrido.
/// </para>
/// </summary>
public sealed class JournalVerifier
{
    private const int DefaultMaxIssues = 500;

    private readonly IEventCodecRegistry _codecs;

    private readonly FileArtifactStore _artifacts;

    private readonly int _maxIssues;

    public JournalVerifier(IEventCodecRegistry codecs, FileArtifactStore artifacts, int maxIssues = DefaultMaxIssues)
    {
        ArgumentNullException.ThrowIfNull(codecs);
        ArgumentNullException.ThrowIfNull(artifacts);
        if (maxIssues < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxIssues), "maxIssues debe ser ≥ 1");
        }

        _codecs = codecs;
        _artifacts = artifacts;
        _maxIssues = maxIssues;
    }

    /// <summary>Verifica todas las sesiones del journal (cada stream, en el orden en que aparecen).</summary>
    public JournalVerificationReport VerifyJournal(string journalPath, CancellationToken cancellationToken = default)
    {
        return Verify(journalPath, null, reportMissingSession: false, cancellationToken);
    }

    /// <summary>Verifica un solo stream: los eventos de esa sesión, en orden de almacenamiento.</summary>
    public JournalVerificationReport VerifySession(string journalPath, string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        return Verify(journalPath, sessionId, reportMissingSession: true, cancellationToken);
    }

    private JournalVerificationReport Verify(string journalPath, string? sessionFilter,
        bool reportMissingSession, CancellationToken cancellationToken)
    {
        var result = new Result(journalPath, _maxIssues);

        if (!File.Exists(journalPath))
        {
            result.Add(null, null, null, null, JournalIssueCode.JournalUnreadable, "journal",
                "el archivo no existe: " + journalPath);
            return result.Build(journalOpened: false);
        }

        System.Data.Common.DbConnection? conn = null;
        try
        {
            conn = OpenReadOnly(journalPath);
            var rows = ReadRows(conn, sessionFilter, cancellationToken);
            VerifyRows(rows, sessionFilter, reportMissingSession, result, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Journal ausente, no es una base SQLite, schema sin tabla events, o un hot
            // journal que SQLite no puede recuperar en modo read-only: reporte, no crash.
            result.Add(sessionFilter, null, null, null, JournalIssueCode.JournalUnreadable, "journal",
                "no se pudo abrir/leer la base: " + ex.Message);
            return result.Build(journalOpened: false);
        }
        finally
        {
            conn?.Close();
        }

        return result.Build(journalOpened: true);
    }

    private void VerifyRows(List<RawRow> rows, string? sessionFilter, bool reportMissingSession, Result result,
        CancellationToken cancellationToken)
    {
        // Cache de sondeos por hash: un blob referenciado por N eventos se lee una vez por
        // verificación (el contenido es inmutable; el reporte es por referencia).
        var probes = new Dictionary<string, BlobProbe>();

        if (rows.Count == 0)
        {
            if (reportMissingSession)
            {
                result.Add(sessionFilter, null, null, null, JournalIssueCode.SessionNotFound, "session_id",
                    "la sesión no tiene eventos en este journal");
            }

            return;
        }

        if (sessionFilter is null)
        {
            // Todo el journal: cada session_id distinto es un stream, en orden de primera
            // aparición; dentro del stream se respeta el orden de almacenamiento (id).
            var scans = new Dictionary<string, SessionScan>();
            var order = new List<SessionScan>();
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!scans.TryGetValue(row.SessionId, out var scan))
                {
                    scan = new SessionScan(row.SessionId);
                    scans[row.SessionId] = scan;
                    order.Add(scan);
                    CheckSessionId(scan, result);
                }

                ScanRow(row, scan, result, probes, cancellationToken);
            }

            foreach (var scan in order)
            {
                result.SessionCount += 1;
                result.EventCount += scan.EventCount;
            }

            return;
        }

        // Un solo stream pedido explícitamente.
        var single = new SessionScan(sessionFilter);
        CheckSessionId(single, result);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanRow(row, single, result, probes, cancellationToken);
        }

        result.SessionCount = 1;
        result.EventCount = rows.Count;
    }

    private static void CheckSessionId(SessionScan scan, Result result)
    {
        if (IsGuid(scan.RawSessionId))
        {
            return;
        }

        result.Add(scan.RawSessionId, null, null, null, JournalIssueCode.SessionIdInvalid, "session_id",
            "session_id no es un GUID válido: '" + scan.RawSessionId + "'");
    }

    /// <summary>Valida una fila completa: secuencia, envelope, refs del sobre, decode y refs del payload.</summary>
    private void ScanRow(RawRow row, SessionScan scan, Result result, Dictionary<string, BlobProbe> probes,
        CancellationToken cancellationToken)
    {
        scan.EventCount += 1;

        // ---- Secuencia y orden (ADR-0001 §3: por sesión, monotónica y sin huecos desde 1).
        if (!row.Seq.HasValue)
        {
            result.Add(scan, row, JournalIssueCode.SequenceInvalid, "seq",
                "seq no es un entero: '" + row.SeqRaw + "'");
        }
        else if (row.Seq.Value < 1)
        {
            result.Add(scan, row, JournalIssueCode.SequenceInvalid, "seq", "seq < 1: " + row.Seq.Value);
        }
        else if (!scan.SeenFirst)
        {
            scan.SeenFirst = true;
            if (row.Seq.Value != 1)
            {
                result.Add(scan, row, JournalIssueCode.SequenceStartsNotAtOne, "seq",
                    "el primer evento del stream tiene seq " + row.Seq.Value + ", se esperaba 1");
            }

            scan.ExpectedSeq = row.Seq.Value + 1;
        }
        else if (row.Seq.Value > scan.ExpectedSeq)
        {
            result.Add(scan, row, JournalIssueCode.SequenceGap, "seq",
                "hueco: se esperaba seq " + scan.ExpectedSeq + ", se encontró " + row.Seq.Value
                + " (faltan " + (row.Seq.Value - scan.ExpectedSeq) + " evento(s))");
            scan.ExpectedSeq = row.Seq.Value + 1;
        }
        else if (row.Seq.Value < scan.ExpectedSeq)
        {
            result.Add(scan, row, JournalIssueCode.SequenceOutOfOrder, "seq",
                "regresión o duplicado: se esperaba seq " + scan.ExpectedSeq + ", se encontró "
                + row.Seq.Value + " (orden de almacenamiento violado)");
            scan.ExpectedSeq = row.Seq.Value + 1;
        }
        else
        {
            scan.ExpectedSeq = row.Seq.Value + 1;
        }

        // ---- Envelope (ADR-0001 §3).
        if (!IsGuid(row.EventId))
        {
            result.Add(scan, row, JournalIssueCode.EnvelopeFieldInvalid, "event_id",
                "event_id " + (row.EventId is null ? "NULL" : "'" + row.EventId + "'") + " no es un GUID válido");
        }

        var typeValid = false;
        if (row.EventType is null || row.EventType.Length == 0)
        {
            result.Add(scan, row, JournalIssueCode.EnvelopeFieldInvalid, "event_type", "event_type es NULL o vacío");
        }
        else if (!IsValidEventType(row.EventType))
        {
            result.Add(scan, row, JournalIssueCode.EnvelopeFieldInvalid, "event_type",
                "event_type no es un identificador estable válido: '" + row.EventType + "'");
        }
        else
        {
            typeValid = true;
        }

        if (!row.SchemaVersion.HasValue)
        {
            result.Add(scan, row, JournalIssueCode.EnvelopeFieldInvalid, "schema_version",
                "schema_version no es un entero: '" + row.SchemaRaw + "'");
        }
        else if (row.SchemaVersion.Value < 1)
        {
            result.Add(scan, row, JournalIssueCode.EnvelopeFieldInvalid, "schema_version",
                "schema_version < 1: " + row.SchemaVersion.Value);
        }

        if (!TryParseTimestamp(row.Timestamp, out _))
        {
            result.Add(scan, row, JournalIssueCode.EnvelopeFieldInvalid, "timestamp",
                "timestamp no es una fecha: " + (row.Timestamp is null ? "NULL" : "'" + row.Timestamp + "'"));
        }

        CheckGuidColumn(scan, row, result, "causation", row.Causation, IsCausationValid);
        CheckGuidColumn(scan, row, result, "correlation", row.Correlation, IsGuid);
        CheckGuidColumn(scan, row, result, "run_id", row.RunId, IsGuid);
        CheckGuidColumn(scan, row, result, "task_id", row.TaskId, IsGuid);
        CheckGuidColumn(scan, row, result, "lane_id", row.LaneId, IsGuid);
        CheckGuidColumn(scan, row, result, "turn_id", row.TurnId, IsGuid);
        CheckGuidColumn(scan, row, result, "plan_item_id", row.PlanItemId, IsGuid);
        CheckGuidColumn(scan, row, result, "toolcall_id", row.ToolCallId, IsGuid);

        // ---- Referencias del envelope (columna artifacts, formato Parts.Artifacts "id|alg|hash;...").
        if (row.Artifacts is not null && row.Artifacts.Length > 0)
        {
            VerifyEnvelopeRefs(scan, row, result, probes, cancellationToken);
        }

        // ---- Decode del payload con el codec del tipo (upcasters vacíos en M1, ADR-0013 §2).
        if (row.Payload is null)
        {
            result.Add(scan, row, JournalIssueCode.EnvelopeFieldInvalid, "payload", "payload es NULL");
        }
        else if (typeValid)
        {
            VerifyPayload(scan, row, result, probes, cancellationToken);
        }
    }

    private void VerifyEnvelopeRefs(SessionScan scan, RawRow row, Result result, Dictionary<string, BlobProbe> probes,
        CancellationToken cancellationToken)
    {
        var entries = row.Artifacts!.Split(';');
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            var field = "artifacts[" + i + "]";
            if (entry.Length == 0)
            {
                result.Add(scan, row, JournalIssueCode.ArtifactRefMalformed, field,
                    "entrada vacía en la posición " + i + " de la columna artifacts");
                continue;
            }

            var parts = entry.Split('|');
            if (parts.Length != 3)
            {
                result.Add(scan, row, JournalIssueCode.ArtifactRefMalformed, field,
                    "la entrada no sigue 'id|alg;hash' con 3 campos: '" + entry + "'");
                continue;
            }

            if (!IsGuid(parts[0]))
            {
                result.Add(scan, row, JournalIssueCode.ArtifactRefMalformed, field,
                    "el id del artifact no es un GUID válido: '" + parts[0] + "'");
                continue;
            }

            // El hash manda (ADR-0001 §4): "sha256" exacto y 64 hex minúsculas. La columna del
            // envelope no lleva tamaño, así que la ref del sobre se verifica por hash.
            if (!IsSha256Hex64(parts[1], parts[2]))
            {
                result.Add(scan, row, JournalIssueCode.ArtifactHashInvalid, field,
                    "hash malformado (se espera sha256 + 64 hex minúsculas): '" + parts[1] + ":" + parts[2] + "'");
                continue;
            }

            VerifyBlobRef(scan, row, result, probes, artifactId: parts[0], hashAlgorithm: parts[1],
                hashValue: parts[2], field: field, expectedSize: null, cancellationToken: cancellationToken);
        }
    }

    private void VerifyPayload(SessionScan scan, RawRow row, Result result, Dictionary<string, BlobProbe> probes,
        CancellationToken cancellationToken)
    {
        IDomainEventCodec codec;
        try
        {
            codec = _codecs.CodecFor(EventType.Of(row.EventType!));
        }
        catch (UnknownEventTypeException)
        {
            result.Add(scan, row, JournalIssueCode.UnknownEventType, "event_type",
                "EventType '" + row.EventType + "' sin codec registrado: schema futuro sin upcaster (ADR-0013 §2)");
            return;
        }

        DomainEventPayload payload;
        try
        {
            payload = codec.Decode(EventType.Of(row.EventType!), row.Payload!);
        }
        catch (EventParseException ex)
        {
            result.Add(scan, row, JournalIssueCode.PayloadDecodeFailed, "payload",
                "el payload no decodifica como '" + row.EventType + "' (schema v" + row.SchemaVersion + "): "
                + (ex.Detail ?? ex.Message));
            return;
        }

        // El payload persistido debe ser el JSON canónico del objeto que decodifica: el runtime
        // SIEMPRE persiste payloads via codecs (ADR-0013 §2) y la redacción ocurre ANTES de
        // persistir (ADR-0018 §4), así que lo almacenado ya ES la forma canónica. Un payload
        // mutado o de otro tipo decodifica de forma leniente (nulls) y re-serializa distinto:
        // se detecta comparando contra el re-encode canónico, sin metadatos por evento.
        var canonical = codec.Encode(payload);
        if (!string.Equals(canonical, row.Payload, StringComparison.Ordinal))
        {
            result.Add(scan, row, JournalIssueCode.PayloadTampered, "payload",
                "el payload persistido no es el JSON canónico de '" + row.EventType
                + "': fue mutado, mezclado con otro tipo o escrito por un writer ajeno");
        }

        if (row.SchemaVersion.HasValue && payload.SchemaVersion() != row.SchemaVersion.Value)
        {
            result.Add(scan, row, JournalIssueCode.SchemaVersionMismatch, "schema_version",
                "el envelope declara schema_version " + row.SchemaVersion.Value + " pero el payload '"
                + row.EventType + "' declara " + payload.SchemaVersion());
        }

        // Referencias a artifacts del payload (hash + tamaño; ADR-0001 §4). Cada payload con
        // artifact declara su campo; los nuevos tipos se añaden aquí, igual que al registro de codecs.
        foreach (var (artifactRef, fieldName) in PayloadArtifactRefs(payload))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var field = payload.Type() + "." + fieldName;
            if (artifactRef.Hash is null || !IsSha256Hex64(artifactRef.Hash.Algorithm, artifactRef.Hash.Value))
            {
                result.Add(scan, row, JournalIssueCode.ArtifactHashInvalid, field,
                    "hash malformado en la ref del payload (se espera sha256 + 64 hex minúsculas)");
                continue;
            }

            VerifyBlobRef(scan, row, result, probes, artifactId: artifactRef.Id.ToString(),
                hashAlgorithm: artifactRef.Hash.Algorithm, hashValue: artifactRef.Hash.Value, field: field,
                expectedSize: artifactRef.Size, cancellationToken: cancellationToken);
        }
    }

    /// <summary>Sondea el blob una vez por hash (cache) y traduce el estado a un problema preciso.</summary>
    private void VerifyBlobRef(SessionScan scan, RawRow row, Result result, Dictionary<string, BlobProbe> probes,
        string artifactId, string hashAlgorithm, string hashValue, string field, long? expectedSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        result.ArtifactRefCount += 1;

        if (!probes.TryGetValue(hashValue, out var probe))
        {
            probe = _artifacts.Probe(new ContentHash(hashAlgorithm, hashValue));
            probes[hashValue] = probe;
        }

        var where = "artifact " + artifactId + " (hash " + hashAlgorithm + ":" + hashValue + ")";
        switch (probe.Status)
        {
            case BlobStatus.Ok:
                var actualSize = (long) System.Text.Encoding.UTF8.GetByteCount(probe.Content!);
                if (expectedSize.HasValue && actualSize != expectedSize.Value)
                {
                    result.Add(scan, row, JournalIssueCode.ArtifactSizeMismatch, field,
                        where + ": el contenido ocupa " + actualSize
                        + " bytes UTF-8 pero la ref declara tamaño " + expectedSize.Value);
                }

                return;
            case BlobStatus.Missing:
                result.Add(scan, row, JournalIssueCode.ArtifactMissing, field, where + ": el blob no existe");
                return;
            case BlobStatus.Corrupted:
                result.Add(scan, row, JournalIssueCode.ArtifactCorrupted, field,
                    where + ": el contenido no verifica contra el hash (aunque conserve la longitud)");
                return;
            case BlobStatus.LinkEscape:
                result.Add(scan, row, JournalIssueCode.ArtifactLinkEscape, field,
                    where + ": la ruta del blob atraviesa un link fuera de blobs/sha256");
                return;
            case BlobStatus.Unreadable:
                result.Add(scan, row, JournalIssueCode.ArtifactUnreadable, field,
                    where + ": el blob es ilegible (desapareció a mitad o sin permisos)");
                return;
            case BlobStatus.HashInvalid:
                // No llega: el hash se valida antes de sondear. Fail-closed por si cambia.
                result.Add(scan, row, JournalIssueCode.ArtifactHashInvalid, field,
                    where + ": hash malformado al sondear el store");
                return;
        }
    }

    /// <summary>Refs a artifacts del payload tipado (sin reflexión; se extiende con los tipos nuevos).</summary>
    private static List<(ArtifactRef Ref, string Field)> PayloadArtifactRefs(DomainEventPayload payload)
    {
        var refs = new List<(ArtifactRef, string)>();
        if (payload is UserInputReceived input && input.ContentRef is not null)
        {
            refs.Add((input.ContentRef, "contentRef"));
        }

        if (payload is AssistantMessageRecorded message && message.ContentRef is not null)
        {
            refs.Add((message.ContentRef, "contentRef"));
        }

        if (payload is ModelCompleted model && model.ResponseArtifact is not null)
        {
            refs.Add((model.ResponseArtifact, "responseArtifact"));
        }

        return refs;
    }

    private void CheckGuidColumn(SessionScan scan, RawRow row, Result result, string field, string? raw,
        Func<string, bool> isValid)
    {
        // El escritor bindea null como "" (limitación del driver, ver SqliteEventStore.S):
        // "" es null legítimo; cualquier otro valor debe parsear.
        if (raw is null || raw.Length == 0)
        {
            return;
        }

        if (!isValid(raw))
        {
            result.Add(scan, row, JournalIssueCode.EnvelopeFieldInvalid, field,
                "el campo '" + field + "' no es válido: '" + raw + "'");
        }
    }

    // ---- Lectura de filas crudas (ruta real de SQLite, tolerante a valores corruptos).

    private static List<RawRow> ReadRows(System.Data.Common.DbConnection conn, string? sessionFilter,
        CancellationToken cancellationToken)
    {
        var cmd = conn.CreateCommand()!;
        cmd.CommandText = "SELECT id, session_id, seq, event_id, event_type, schema_version, timestamp, " +
            "causation, correlation, run_id, task_id, lane_id, turn_id, plan_item_id, toolcall_id, payload, artifacts " +
            "FROM events" + (sessionFilter is null ? "" : " WHERE session_id = :sid") + " ORDER BY id";
        if (sessionFilter is not null)
        {
            var p = cmd.CreateParameter()!;
            p.ParameterName = "sid";
            p.Value = sessionFilter;
            cmd.Parameters.Add(p);
        }

        var rows = new List<RawRow>();
        var reader = cmd.ExecuteReader()!;
        foreach (System.Data.Common.DbDataRecord record in reader)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(ReadRow(record));
        }

        return rows;
    }

    private static RawRow ReadRow(System.Data.Common.DbDataRecord row)
    {
        var seq = AsLong(row, 2, out var seqRaw);
        var schemaVersion = AsLong(row, 5, out var schemaRaw);
        return new RawRow(
            AsText(row, 1) ?? "",
            seq, seqRaw,
            AsText(row, 3),
            AsText(row, 4),
            schemaVersion, schemaRaw,
            AsText(row, 6),
            AsText(row, 7),
            AsText(row, 8),
            AsText(row, 9),
            AsText(row, 10),
            AsText(row, 11),
            AsText(row, 12),
            AsText(row, 13),
            AsText(row, 14),
            AsText(row, 15),
            AsText(row, 16));
    }

    /// <summary>NULL (o DBNull) → null. Sin trim: el valor se reporta tal como está.</summary>
    private static string? AsText(System.Data.Common.DbDataRecord row, int index)
    {
        var value = ValueOrNull(row, index);
        return value is null ? null : value.ToString();
    }

    private static long? AsLong(System.Data.Common.DbDataRecord row, int index, out string? raw)
    {
        var value = ValueOrNull(row, index);
        raw = value is null ? null : value.ToString();
        if (value is long l)
        {
            return l;
        }

        if (value is int i)
        {
            return (long) i;
        }

        return raw is not null && long.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static object? ValueOrNull(System.Data.Common.DbDataRecord row, int index)
    {
        if (row.IsDBNull(index))
        {
            return null;
        }

        return row.GetValue(index);
    }

    /// <summary>Abre el journal en modo estrictamente read-only: cualquier escritura falla en el driver.</summary>
    private static System.Data.Common.DbConnection OpenReadOnly(string journalPath)
    {
        return Microsoft.Data.Sqlite.SqliteFactory.Instance!.CreateDataSource(
            "DataSource=" + journalPath + ";Mode=ReadOnly")!.OpenConnection()!;
    }

    // ---- Validaciones de formato, contra los mismos contratos del escritor.

    private static bool IsGuid(string? text)
    {
        if (text is null || text.Length == 0)
        {
            return false;
        }

        try
        {
            EventId.ParseGuidText(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentNullException)
        {
            return false;
        }
    }

    /// <summary>Causación según el contrato de Parts: "event:guid", "command:guid" o vacío.</summary>
    private static bool IsCausationValid(string raw)
    {
        try
        {
            // "" es null legítimo (el escritor bindea null como ""); cualquier otro valor debe
            // resolver a una causación conocida: el prefijo que no se reconoce se reporta.
            return Parts.ParseCausation(raw) is not null;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentNullException)
        {
            return false;
        }
    }

    /// <summary>Identificador estable del EventType (mismas reglas que EventType.Of, ADR-0013 §3).</summary>
    private static bool IsValidEventType(string text)
    {
        if (text.Length == 0 || text.Any(ch => ch == ' ' || ch == '\t'))
        {
            return false;
        }

        return true;
    }

    /// <summary>Hash según ADR-0001 §4: algoritmo "sha256" exacto y 64 hex minúsculas.</summary>
    private static bool IsSha256Hex64(string algorithm, string value)
    {
        if (!string.Equals(algorithm, "sha256", StringComparison.Ordinal) || value is null || value.Length != 64)
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

    private static bool TryParseTimestamp(string? raw, out DateTimeOffset parsed)
    {
        if (raw is null || raw.Length == 0)
        {
            parsed = default;
            return false;
        }

        // El escritor persiste DateTimeOffset con la cultura activa: se intenta primero la
        // cultura actual y luego la invariante (el journal puede moverse entre máquinas).
        if (DateTimeOffset.TryParse(raw, out parsed))
        {
            return true;
        }

        return DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out parsed);
    }

    /// <summary>Fila cruda tal como está en SQLite: cada campo es tolerante a la corrupción.</summary>
    private sealed class RawRow
    {
        public RawRow(string sessionId, long? seq, string? seqRaw, string? eventId, string? eventType,
            long? schemaVersion, string? schemaRaw, string? timestamp, string? causation, string? correlation,
            string? runId, string? taskId, string? laneId, string? turnId, string? planItemId, string? toolCallId,
            string? payload, string? artifacts)
        {
            SessionId = sessionId;
            Seq = seq;
            SeqRaw = seqRaw;
            EventId = eventId;
            EventType = eventType;
            SchemaVersion = schemaVersion;
            SchemaRaw = schemaRaw;
            Timestamp = timestamp;
            Causation = causation;
            Correlation = correlation;
            RunId = runId;
            TaskId = taskId;
            LaneId = laneId;
            TurnId = turnId;
            PlanItemId = planItemId;
            ToolCallId = toolCallId;
            Payload = payload;
            Artifacts = artifacts;
        }

        public string SessionId { get; }

        public long? Seq { get; }

        public string? SeqRaw { get; }

        public string? EventId { get; }

        public string? EventType { get; }

        public long? SchemaVersion { get; }

        public string? SchemaRaw { get; }

        public string? Timestamp { get; }

        public string? Causation { get; }

        public string? Correlation { get; }

        public string? RunId { get; }

        public string? TaskId { get; }

        public string? LaneId { get; }

        public string? TurnId { get; }

        public string? PlanItemId { get; }

        public string? ToolCallId { get; }

        public string? Payload { get; }

        public string? Artifacts { get; }
    }

    /// <summary>Estado del recorrido de un stream: secuencia esperada y conteo de eventos.</summary>
    private sealed class SessionScan
    {
        public SessionScan(string rawSessionId)
        {
            RawSessionId = rawSessionId;
        }

        public string RawSessionId { get; }

        /// <summary>Siguiente seq esperada según el orden de almacenamiento (id) del stream.</summary>
        public long ExpectedSeq { get; set; } = 1;

        public bool SeenFirst { get; set; }

        public long EventCount { get; set; }
    }

    /// <summary>Acumulador del reporte: cuenta todos los problemas y lista hasta el tope.</summary>
    private sealed class Result
    {
        private readonly string _journalPath;

        private readonly int _maxIssues;

        private readonly List<JournalIssue> _issues = new();

        public Result(string journalPath, int maxIssues)
        {
            _journalPath = journalPath;
            _maxIssues = maxIssues;
        }

        public long TotalIssueCount { get; private set; }

        public long SessionCount { get; set; }

        public long EventCount { get; set; }

        public long ArtifactRefCount { get; set; }

        public void Add(string? sessionId, long? sequence, string? eventId, string? eventType,
            JournalIssueCode code, string? field, string detail)
        {
            TotalIssueCount += 1;
            if (_issues.Count < _maxIssues)
            {
                _issues.Add(new JournalIssue(code, sessionId, sequence, eventId, eventType, field, detail));
            }
        }

        public void Add(SessionScan scan, RawRow row, JournalIssueCode code, string field, string detail)
        {
            Add(scan.RawSessionId, row.Seq, row.EventId, row.EventType, code, field, detail);
        }

        public JournalVerificationReport Build(bool journalOpened)
        {
            var truncated = _issues.Count < TotalIssueCount;
            return new JournalVerificationReport(_journalPath, journalOpened, SessionCount, EventCount,
                ArtifactRefCount, TotalIssueCount, truncated, _issues.ToArray());
        }
    }
}
