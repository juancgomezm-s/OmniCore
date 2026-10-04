using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>
/// Pruebas deterministas del verificador read-only del journal (M4, ADR-0001 §7): el journal
/// íntegro verifica OK con sus refs de artifacts; la corrupción adversarial — vía SQL crudo
/// contra la base, como la produciría un fallo de disco o un tampering — se reporta con el
/// código exacto por campo (secuencia, envelope, codec, schema, refs de artifacts y blobs) sin
/// ocultar conteos; y el recorrido nunca muta el archivo que verifica.
/// <para>
/// Las corrupciones se aplican con una conexión SQLite de bajonivel sobre el journal cerrado
/// para simular exactamente el estado que un escritor dañado dejaría en disco (no lo que el
/// runtime puede producir por API).
/// </para>
/// </summary>
public sealed class JournalVerifierTests
{
    private const int MaxIssues = 1000;

    [Fact]
    public void Valid_journal_with_envelope_and_payload_artifact_refs_verifies_ok()
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        var artifact = fx.Artifacts.PutText("respuesta completa del modelo", "text/plain",
            ArtifactKind.ModelResponse, Sensitivity.Normal);
        fx.AppendValidTurn(session, artifact);

        var report = fx.VerifyAll();

        Assert.True(report.Ok, "un journal recién escrito debe verificar: " + string.Join("; ", AllDetails(report)));
        Assert.Equal(1, report.SessionCount);
        Assert.Equal(4, report.EventCount);
        // 1 ref en el envelope (ModelCompleted) + 1 ref en el payload (AssistantMessageRecorded).
        Assert.Equal(2, report.ArtifactRefCount);
        Assert.True(report.JournalOpened);
        Assert.Equal(0, report.TotalIssueCount);
        Assert.Contains("— OK", report.SummaryLine());

        // La misma sesión, verificada sola, da el mismo resultado de conteos.
        var single = fx.VerifySession(session);
        Assert.True(single.Ok);
        Assert.Equal(4, single.EventCount);
        Assert.Equal(2, single.ArtifactRefCount);
    }

    [Fact]
    public void Multiple_sessions_verify_independently_even_interleaved()
    {
        using var fx = NewFixture();
        var a = SessionId.New();
        var b = SessionId.New();
        fx.AppendValidTurn(a, null);
        fx.AppendValidTurn(b, null);
        fx.AppendValidTurn(a, null);

        var report = fx.VerifyAll();

        Assert.True(report.Ok, string.Join("; ", AllDetails(report)));
        Assert.Equal(2, report.SessionCount);
        Assert.Equal(12, report.EventCount);
    }

    [Fact]
    public void Session_filter_with_unknown_session_reports_session_not_found()
    {
        using var fx = NewFixture();
        fx.AppendValidTurn(SessionId.New(), null);

        var report = fx.VerifySession(SessionId.New());

        Assert.False(report.Ok);
        Assert.Equal(1, report.TotalIssueCount);
        Assert.Equal(JournalIssueCode.SessionNotFound, report.Issues[0].Code);
        Assert.Equal(0, report.EventCount);
    }

    [Fact]
    public void Invalid_session_id_in_a_row_reports_session_id_invalid()
    {
        using var fx = NewFixture();
        fx.AppendValidTurn(SessionId.New(), null);
        fx.CloseStore();
        fx.RawSql("INSERT INTO events (session_id, seq, event_id, event_type, schema_version, timestamp, " +
                  "causation, correlation, run_id, task_id, lane_id, turn_id, plan_item_id, toolcall_id, payload, " +
                  "artifacts) VALUES ('no-es-un-guid', 1, :eid, 'session.created', 1, :ts, '', '', '', '', '', '', " +
                  "'', '', :payload, '')",
            ("eid", EventId.New().ToString()), ("ts", DateTimeOffset.Now.ToString()),
            ("payload", JournalFixture.ValidSessionCreatedPayload(SessionId.New(), fx)));

        var report = fx.VerifyAll();

        Assert.False(report.Ok);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(JournalIssueCode.SessionIdInvalid, issue.Code);
        Assert.Equal("no-es-un-guid", issue.SessionId);
    }

    [Fact]
    public void Sequence_must_start_at_one()
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        fx.AppendValidTurn(session, null);
        fx.CloseStore();
        // Un único evento con seq 5: el stream no empieza en 1 (ADR-0001 §3).
        fx.RawSql("DELETE FROM events WHERE session_id = :sid", ("sid", session.ToString()));
        fx.RawSql("INSERT INTO events (session_id, seq, event_id, event_type, schema_version, timestamp, " +
                  "causation, correlation, run_id, task_id, lane_id, turn_id, plan_item_id, toolcall_id, payload, " +
                  "artifacts) VALUES (:sid, 5, :eid, 'session.created', 1, :ts, '', '', '', '', '', '', '', '', " +
                  ":payload, '')",
            ("sid", session.ToString()), ("eid", EventId.New().ToString()),
            ("ts", DateTimeOffset.Now.ToString()), ("payload", JournalFixture.ValidSessionCreatedPayload(session, fx)));

        var report = fx.VerifySession(session);

        Assert.False(report.Ok);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(JournalIssueCode.SequenceStartsNotAtOne, issue.Code);
        Assert.Equal(5, issue.Sequence);
    }

    [Fact]
    public void Deleted_event_between_two_events_reports_sequence_gap_without_cascade()
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        // session.created, run.created, turn.started, turn.completed (seq 1..4): borrar seq 3.
        fx.AppendValidTurn(session, null);
        fx.CloseStore();
        fx.RawSql("DELETE FROM events WHERE session_id = :sid AND seq = 3", ("sid", session.ToString()));

        var report = fx.VerifySession(session);

        // Exactamente un hueco: la seq 4 encaja con la esperada tras el resync (sin cascada).
        Assert.False(report.Ok);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(JournalIssueCode.SequenceGap, issue.Code);
        Assert.Equal(4, issue.Sequence);
        Assert.Contains("faltan", issue.Detail);
        Assert.Equal(3, report.EventCount);
    }

    [Fact]
    public void Out_of_storage_order_sequence_reports_regression()
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        fx.AppendValidTurn(session, null);
        fx.CloseStore();
        // Solo dos eventos: al intercambiar sus seq, el primero empieza mal (2) y el segundo
        // regresa por debajo de lo esperado.
        fx.RawSql("DELETE FROM events WHERE session_id = :sid AND seq > 2", ("sid", session.ToString()));
        fx.RawSql("UPDATE events SET seq = 99 WHERE session_id = :sid AND seq = 1", ("sid", session.ToString()));
        fx.RawSql("UPDATE events SET seq = 1 WHERE session_id = :sid AND seq = 2", ("sid", session.ToString()));
        fx.RawSql("UPDATE events SET seq = 2 WHERE session_id = :sid AND seq = 99", ("sid", session.ToString()));

        var report = fx.VerifySession(session);

        Assert.False(report.Ok);
        Assert.Equal(2, report.TotalIssueCount);
        Assert.Equal(JournalIssueCode.SequenceStartsNotAtOne, report.Issues[0].Code);
        Assert.Equal(JournalIssueCode.SequenceOutOfOrder, report.Issues[1].Code);
        Assert.Equal(1, report.Issues[1].Sequence);
    }

    [Fact]
    public void Non_integer_sequence_reports_sequence_invalid()
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        fx.AppendValidTurn(session, null);
        fx.CloseStore();
        // SQLite acepta texto en una columna INTEGER (tipado dinámico): simula corrupción.
        // Se corrompe el ÚLTIMO evento para aislar el problema (un seq inválido no permite
        // resincronizar, así que un evento intermedio arrastraría un hueco en cascada).
        fx.RawSql("UPDATE events SET seq = 'abc' WHERE session_id = :sid AND seq = 4",
            ("sid", session.ToString()));

        var report = fx.VerifySession(session);

        Assert.False(report.Ok);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(JournalIssueCode.SequenceInvalid, issue.Code);
        Assert.Null(issue.Sequence);
        Assert.Contains("abc", issue.Detail);
    }

    [Fact]
    public void Corrupted_envelope_fields_report_envelope_field_invalid_per_field()
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        fx.AppendValidTurn(session, null);
        fx.CloseStore();
        var sid = session.ToString();

        fx.RawSql("UPDATE events SET event_id = 'no-es-un-guid' WHERE session_id = :sid AND seq = 1", ("sid", sid));
        fx.RawSql("UPDATE events SET timestamp = 'no-es-una-fecha' WHERE session_id = :sid AND seq = 2", ("sid", sid));
        fx.RawSql("UPDATE events SET causation = 'weird:123' WHERE session_id = :sid AND seq = 3", ("sid", sid));
        fx.RawSql("UPDATE events SET correlation = 'deadbeef' WHERE session_id = :sid AND seq = 4", ("sid", sid));

        var report = fx.VerifySession(session);

        Assert.False(report.Ok);
        Assert.Equal(4, report.TotalIssueCount);
        Assert.All(report.Issues, i => Assert.Equal(JournalIssueCode.EnvelopeFieldInvalid, i.Code));
        Assert.Equal("event_id", report.Issues[0].Field);
        Assert.Equal("timestamp", report.Issues[1].Field);
        Assert.Equal("causation", report.Issues[2].Field);
        Assert.Equal("correlation", report.Issues[3].Field);
    }

    [Fact]
    public void Non_integer_schema_version_reports_envelope_field_invalid()
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        fx.AppendValidTurn(session, null);
        fx.CloseStore();
        fx.RawSql("UPDATE events SET schema_version = 'x' WHERE session_id = :sid AND seq = 1",
            ("sid", session.ToString()));

        var report = fx.VerifySession(session);

        Assert.False(report.Ok);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(JournalIssueCode.EnvelopeFieldInvalid, issue.Code);
        Assert.Equal("schema_version", issue.Field);
    }

    [Fact]
    public void Unknown_event_type_is_reported_not_crashed()
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        fx.AppendValidTurn(session, null);
        fx.CloseStore();
        // Tipo con formato válido pero sin codec registrado: schema del futuro (ADR-0013 §2).
        fx.RawSql("UPDATE events SET event_type = 'future.event' WHERE session_id = :sid AND seq = 1",
            ("sid", session.ToString()));

        var report = fx.VerifySession(session);

        Assert.False(report.Ok);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(JournalIssueCode.UnknownEventType, issue.Code);
        Assert.Equal("future.event", issue.EventType);
    }

    [Fact]
    public void Payload_of_a_different_type_reports_payload_tampered()
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        fx.AppendValidTurn(session, null);
        fx.CloseStore();
        var run = RunId.New();
        // session.created con el payload canónico de run.created: el runtime nunca escribiría esa
        // combinación. El decode leniente no basta para detectarlo; el round-trip canónico sí.
        var payload = fx.Encode(new RunCreated(run, session, "objetivo", RunMode.Act,
            ExecutionStrategy.Direct, FailurePolicy.FailRun, new TaskBudget(null, null, null, null),
            TaskId.New(), DateTimeOffset.Now));
        fx.RawSql("UPDATE events SET payload = :payload WHERE session_id = :sid AND seq = 1",
            ("payload", payload), ("sid", session.ToString()));

        var report = fx.VerifySession(session);

        Assert.False(report.Ok);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(JournalIssueCode.PayloadTampered, issue.Code);
    }

    [Fact]
    public void Undecodable_payload_reports_payload_decode_failed()
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        fx.AppendValidTurn(session, null);
        fx.CloseStore();
        fx.RawSql("UPDATE events SET payload = '{\"turn\": no-cierra' WHERE session_id = :sid AND seq = 1",
            ("sid", session.ToString()));

        var report = fx.VerifySession(session);

        Assert.False(report.Ok);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(JournalIssueCode.PayloadDecodeFailed, issue.Code);
    }

    [Fact]
    public void Envelope_schema_version_mismatching_payload_is_reported()
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        fx.AppendValidTurn(session, null);
        fx.CloseStore();
        fx.RawSql("UPDATE events SET schema_version = 2 WHERE session_id = :sid AND seq = 1",
            ("sid", session.ToString()));

        var report = fx.VerifySession(session);

        Assert.False(report.Ok);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(JournalIssueCode.SchemaVersionMismatch, issue.Code);
    }

    [Fact]
    public void Malformed_artifacts_column_reports_artifact_ref_malformed()
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        fx.AppendValidTurn(session, null);
        fx.CloseStore();
        fx.RawSql("UPDATE events SET artifacts = 'zz' WHERE session_id = :sid AND seq = 1",
            ("sid", session.ToString()));
        fx.RawSql("UPDATE events SET artifacts = 'no-es-un-guid|sha256|deadbeef' WHERE session_id = :sid AND seq = 2",
            ("sid", session.ToString()));

        var report = fx.VerifySession(session);

        Assert.False(report.Ok);
        Assert.Equal(2, report.TotalIssueCount);
        Assert.All(report.Issues, i => Assert.Equal(JournalIssueCode.ArtifactRefMalformed, i.Code));
    }

    [Fact]
    public void Bad_hash_in_artifact_ref_reports_artifact_hash_invalid()
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        fx.AppendValidTurn(session, null);
        fx.CloseStore();
        fx.RawSql("UPDATE events SET artifacts = :a WHERE session_id = :sid AND seq = 1",
            ("a", ArtifactId.New() + "|md5|" + new string('a', 32)), ("sid", session.ToString()));
        fx.RawSql("UPDATE events SET artifacts = :a WHERE session_id = :sid AND seq = 2",
            ("a", ArtifactId.New() + "|sha256|" + new string('A', 64)), ("sid", session.ToString()));

        var report = fx.VerifySession(session);

        Assert.False(report.Ok);
        Assert.Equal(2, report.TotalIssueCount);
        Assert.All(report.Issues, i => Assert.Equal(JournalIssueCode.ArtifactHashInvalid, i.Code));
    }

    [Fact]
    public void Envelope_artifact_ref_with_wrong_size_reports_artifact_size_mismatch()
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        var artifact = fx.Artifacts.PutText("contenido", "text/plain", ArtifactKind.Other, Sensitivity.Normal);
        fx.AppendValidTurn(session, null);
        fx.CloseStore();
        var envelope = string.Join("|", artifact.Id, artifact.Hash.Algorithm, artifact.Hash.Value,
            artifact.Size + 1, artifact.MediaType, artifact.Kind, artifact.Sensitivity,
            artifact.Redacted ? "1" : "0");
        fx.RawSql("UPDATE events SET artifacts = :a WHERE session_id = :sid AND seq = 1",
            ("a", envelope), ("sid", session.ToString()));

        var report = fx.VerifySession(session);

        Assert.False(report.Ok);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(JournalIssueCode.ArtifactSizeMismatch, issue.Code);
    }

    [Theory]
    [InlineData("not-a-size")]
    [InlineData("-1")]
    public void Envelope_artifact_ref_with_invalid_size_reports_malformed_ref(string invalidSize)
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        var artifact = fx.Artifacts.PutText("contenido", "text/plain", ArtifactKind.Other, Sensitivity.Normal);
        fx.AppendValidTurn(session, null);
        fx.CloseStore();
        var envelope = string.Join("|", artifact.Id, artifact.Hash.Algorithm, artifact.Hash.Value,
            invalidSize, artifact.MediaType, artifact.Kind, artifact.Sensitivity,
            artifact.Redacted ? "1" : "0");
        fx.RawSql("UPDATE events SET artifacts = :a WHERE session_id = :sid AND seq = 1",
            ("a", envelope), ("sid", session.ToString()));

        var report = fx.VerifySession(session);

        Assert.False(report.Ok);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(JournalIssueCode.ArtifactRefMalformed, issue.Code);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("2")]
    [InlineData("")]
    public void Envelope_artifact_ref_with_invalid_redacted_token_reports_malformed_ref(string invalidRedacted)
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        var artifact = fx.Artifacts.PutText("contenido", "text/plain", ArtifactKind.Other, Sensitivity.Normal);
        fx.AppendValidTurn(session, null);
        fx.CloseStore();
        var envelope = string.Join("|", artifact.Id, artifact.Hash.Algorithm, artifact.Hash.Value,
            artifact.Size, artifact.MediaType, artifact.Kind, artifact.Sensitivity, invalidRedacted);
        fx.RawSql("UPDATE events SET artifacts = :a WHERE session_id = :sid AND seq = 1",
            ("a", envelope), ("sid", session.ToString()));

        var report = fx.VerifySession(session);

        Assert.False(report.Ok);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(JournalIssueCode.ArtifactRefMalformed, issue.Code);
    }

    [Theory]
    [InlineData(5, "Bogus")]
    [InlineData(6, "Bogus")]
    [InlineData(5, "999")]
    [InlineData(6, "999")]
    [InlineData(5, "0")]
    [InlineData(6, "0")]
    [InlineData(5, " Other")]
    [InlineData(6, "sensitive")]
    public void Envelope_artifact_ref_with_noncanonical_enum_is_rejected_by_verifier_and_reader(
        int fieldIndex, string invalidToken)
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        var artifact = fx.Artifacts.PutText("contenido", "text/plain", ArtifactKind.Other, Sensitivity.Normal);
        fx.AppendValidTurn(session, null);
        fx.CloseStore();
        var fields = new[] { artifact.Id.ToString(), artifact.Hash.Algorithm, artifact.Hash.Value,
            artifact.Size.ToString(System.Globalization.CultureInfo.InvariantCulture), artifact.MediaType,
            artifact.Kind.ToString(), artifact.Sensitivity.ToString(), "0" };
        fields[fieldIndex] = invalidToken;
        fx.RawSql("UPDATE events SET artifacts = :a WHERE session_id = :sid AND seq = 1",
            ("a", string.Join("|", fields)), ("sid", session.ToString()));

        var report = fx.VerifySession(session);

        Assert.False(report.Ok);
        Assert.Equal(JournalIssueCode.ArtifactRefMalformed, Assert.Single(report.Issues).Code);
        var reopened = new SqliteEventStore(fx.JournalPath);
        try
        {
            Assert.Throws<InvalidOperationException>(() => reopened.ReadFrom(session, 1));
        }
        finally
        {
            reopened.Close();
        }
    }

    [Fact]
    public void Missing_blob_reports_artifact_missing()
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        var artifact = fx.Artifacts.PutText("contenido que luego desaparece", "text/plain", ArtifactKind.Other,
            Sensitivity.Normal);
        // La ref vive solo en el payload: un reporte limpio, un problema por referencia rota.
        fx.AppendEvent(session, EventType.Of("model.completed"), 1,
            fx.Encode(new ModelCompleted(TurnId.New(), artifact)));
        fx.DeleteBlob(artifact.Hash);

        var report = fx.VerifySession(session);

        Assert.False(report.Ok);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(JournalIssueCode.ArtifactMissing, issue.Code);
    }

    [Fact]
    public void Altered_blob_reports_artifact_corrupted_for_envelope_and_payload_refs()
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        var artifact = fx.Artifacts.PutText("respuesta del modelo", "text/plain", ArtifactKind.ModelResponse,
            Sensitivity.Normal);
        // La ref vive en el envelope y en el payload: el mismo blob alterado se reporta en ambas.
        fx.AppendEvent(session, EventType.Of("model.completed"), 1,
            fx.Encode(new ModelCompleted(TurnId.New(), artifact)), refs: new[] { artifact });
        fx.RewriteBlob(artifact.Hash, "respuesta saboteada");

        var report = fx.VerifySession(session);

        Assert.False(report.Ok);
        Assert.Equal(2, report.TotalIssueCount);
        Assert.All(report.Issues, i => Assert.Equal(JournalIssueCode.ArtifactCorrupted, i.Code));
        Assert.Equal(2, report.ArtifactRefCount);
    }

    [Fact]
    public void Payload_ref_with_wrong_size_reports_artifact_size_mismatch()
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        var artifact = fx.Artifacts.PutText("respuesta del modelo", "text/plain", ArtifactKind.ModelResponse,
            Sensitivity.Normal);
        var wrongSize = new ArtifactRef(artifact.Id, artifact.Hash, artifact.Size + 7, artifact.MediaType,
            artifact.Kind, artifact.Sensitivity);
        fx.AppendEvent(session, EventType.Of("model.completed"), 1,
            fx.Encode(new ModelCompleted(TurnId.New(), wrongSize)));

        var report = fx.VerifySession(session);

        Assert.False(report.Ok);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(JournalIssueCode.ArtifactSizeMismatch, issue.Code);
    }

    [Fact]
    public void Issue_list_is_truncated_but_total_count_is_not()
    {
        using var fx = NewFixture(maxIssues: 2);
        var session = SessionId.New();
        fx.AppendValidTurn(session, null);
        fx.CloseStore();
        fx.RawSql("UPDATE events SET event_id = 'x' WHERE session_id = :sid AND seq = 1",
            ("sid", session.ToString()));
        fx.RawSql("UPDATE events SET event_id = 'x' WHERE session_id = :sid AND seq = 2",
            ("sid", session.ToString()));
        fx.RawSql("UPDATE events SET event_id = 'x' WHERE session_id = :sid AND seq = 3",
            ("sid", session.ToString()));

        var report = fx.VerifyAll();

        Assert.False(report.Ok);
        Assert.Equal(3, report.TotalIssueCount);
        Assert.Equal(2, report.Issues.Count);
        Assert.True(report.IssuesTruncated);
        Assert.Contains("3 problema(s) (se listan 2 de 3)", report.SummaryLine());
    }

    [Fact]
    public void Verification_never_mutates_the_journal_file()
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        fx.AppendValidTurn(session, null);
        fx.AppendValidTurn(SessionId.New(), null);
        fx.CloseStore();
        var before = FileBytes(fx.JournalPath);

        var report = fx.VerifyAll();
        var after = FileBytes(fx.JournalPath);

        Assert.True(report.Ok, string.Join("; ", AllDetails(report)));
        Assert.Equal(before, after);
    }

    [Fact]
    public void Missing_journal_file_is_reported_as_unreadable()
    {
        using var fx = NewFixture();
        var report = fx.VerifyAllAt(Path.Combine(fx.Root, "no-existe.db"));

        Assert.False(report.Ok);
        Assert.False(report.JournalOpened);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(JournalIssueCode.JournalUnreadable, issue.Code);
    }

    [Fact]
    public void Non_sqlite_file_is_reported_as_unreadable()
    {
        using var fx = NewFixture();
        var bogus = Path.Combine(fx.Root, "bogus.db");
        File.WriteAllText(bogus, "esto no es una base sqlite");
        var report = fx.VerifyAllAt(bogus);

        Assert.False(report.Ok);
        Assert.False(report.JournalOpened);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(JournalIssueCode.JournalUnreadable, issue.Code);
    }

    [Fact]
    public void Empty_journal_verifies_ok_with_zero_sessions()
    {
        using var fx = NewFixture();
        var report = fx.VerifyAll();

        Assert.True(report.Ok);
        Assert.Equal(0, report.SessionCount);
        Assert.Equal(0, report.EventCount);
        Assert.True(report.JournalOpened);
    }

    [Fact]
    public void Events_written_by_the_runtime_EventStream_verify_ok_without_false_positives()
    {
        using var fx = NewFixture();
        var session = SessionId.New();
        var artifacts = new FileArtifactStore(fx.DataDir);
        var artifact = artifacts.PutText("salida con acentos á é í ó ú y emojis 🚀", "text/plain",
            ArtifactKind.ModelResponse, Sensitivity.Normal);

        // El writer real del runtime (codec + redacción de payload antes de persistir) produce
        // contenido adversarial para el round-trip canónico: unicode, caracteres HTML, escapes,
        // decimales y DateTimeOffset. Todos deben verificar OK.
        var stream = new OmniCore.Engine.EventStream(fx.Store, fx.Codecs, session);
        var run = RunId.New();
        stream.Append(new SessionCreated(session, WorkspaceId.Of(fx.Root).ToString(),
            "c:\\ruta\\á & <entorno> \"comillas\"", ProfileId.New(), DateTimeOffset.Now));
        stream.Append(new RunCreated(run, session, "objetivo <con> ángulos & amper",
            RunMode.Act, ExecutionStrategy.Direct, FailurePolicy.FailRun,
            new TaskBudget(1000.55m, 987654321L, 12, null), TaskId.New(), DateTimeOffset.Now));
        stream.Append(new RunStarted(run));
        stream.Append(new UserInputReceived(run,
            "{\"texto\":\"á é í 🚀 <script>alert(1)</script> \\\\n \\\"quotes\\\"\"}", artifact));
        var turn = TurnId.New();
        stream.Append(new TurnStarted(turn, LaneId.New()));
        stream.Append(new ModelCompleted(turn, artifact));

        var report = fx.VerifyAll();

        Assert.True(report.Ok, string.Join("; ", AllDetails(report)));
        Assert.Equal(6, report.EventCount);
        // El runtime ahora persiste las refs TANTO en el envelope (para indexar, ADR-0001 §3)
        // COMO en el payload. Se verifican 2 refs de envelope + 2 refs de payload = 4.
        Assert.Equal(4, report.ArtifactRefCount);
    }

    // ------------------------------------------------------------------ helpers

    private static JournalFixture NewFixture(int maxIssues = MaxIssues) => new(maxIssues);

    private static string AllDetails(JournalVerificationReport report) =>
        string.Join(" | ", report.Issues.Select(i => i.Code + ": " + i.Detail));

    private static byte[] FileBytes(string path)
    {
        // FileShare amplio: el driver SQLite puede retener un handle al journal (pooling) y la
        // lectura del hash no debe fallar por eso; el invariant probado son los bytes.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>Journal real (SqliteEventStore) + Artifact Store real en un directorio temporal.</summary>
    private sealed class JournalFixture : IDisposable
    {
        public JournalFixture(int maxIssues)
        {
            Root = Path.Combine(Path.GetTempPath(), "omnicore-m4-verify-journal-" + Guid.NewGuid().ToString("N"));
            DataDir = Path.Combine(Root, "data");
            Directory.CreateDirectory(DataDir);
            JournalPath = Path.Combine(Root, "journal.db");
            Artifacts = new FileArtifactStore(DataDir);
            Store = new SqliteEventStore(JournalPath);
            Codecs = EventCodecs.Create();
            Verifier = new JournalVerifier(Codecs, Artifacts, maxIssues);
        }

        public string Root { get; }

        public string DataDir { get; }

        public string JournalPath { get; }

        public FileArtifactStore Artifacts { get; }

        public SqliteEventStore Store { get; }

        public EventCodecs Codecs { get; }

        public JournalVerifier Verifier { get; }

        public JournalVerificationReport VerifyAll() => Verifier.VerifyJournal(JournalPath);

        public JournalVerificationReport VerifyAllAt(string path) => Verifier.VerifyJournal(path);

        public JournalVerificationReport VerifySession(SessionId session) =>
            Verifier.VerifySession(JournalPath, session.ToString());

        public void CloseStore() => Store.Close();

        /// <summary>
        /// Agrega un turno completo bien formado (session.created + run.created + turn.started +
        /// model.completed con artifact opcional) y devuelve la sesión.
        /// </summary>
        public SessionId AppendValidTurn(SessionId session, ArtifactRef? artifact)
        {
            var run = RunId.New();
            var turn = TurnId.New();

            AppendEvent(session, EventType.Of("session.created"), 1,
                ValidSessionCreatedPayload(session, this));
            AppendEvent(session, EventType.Of("run.created"), 1,
                Encode(new RunCreated(run, session, "objetivo de prueba", RunMode.Act,
                    ExecutionStrategy.Direct, FailurePolicy.FailRun, new TaskBudget(null, null, null, null),
                    TaskId.New(), DateTimeOffset.Now)),
                runId: run, correlationId: run);
            AppendEvent(session, EventType.Of("turn.started"), Codecs.CurrentVersion(EventType.Of("turn.started")),
                Encode(new TurnStarted(turn, LaneId.New())), runId: run, correlationId: run,
                turnId: turn);
            AppendEvent(session, EventType.Of("model.completed"), 1,
                Encode(new ModelCompleted(turn, artifact)), runId: run, correlationId: run,
                turnId: turn, refs: artifact is null ? null : new[] { artifact });
            return session;
        }

        public void AppendEvent(SessionId session, EventType type, int schemaVersion, string payloadJson,
            RunId? runId = null, RunId? correlationId = null, TurnId? turnId = null, ArtifactRef[]? refs = null)
        {
            var evt = DomainEvent.Create(session, type, schemaVersion, null, correlationId, runId, null, null, turnId,
                null, null, refs ?? Array.Empty<ArtifactRef>(), payloadJson);
            Store.Append(session, evt, DurabilityClass.Standard, CancellationToken.None);
        }

        /// <summary>Codifica con el codec registrado del tipo (como escribe el runtime, no por interfaz).</summary>
        public string Encode(DomainEventPayload payload) => Codecs.CodecFor(payload.Type()).Encode(payload);

        /// <summary>Payload bien formado de session.created para filas insertadas con SQL crudo.</summary>
        public static string ValidSessionCreatedPayload(SessionId session, JournalFixture fx) =>
            fx.Encode(new SessionCreated(session, WorkspaceId.Of(fx.Root).ToString(), fx.Root,
                ProfileId.New(), DateTimeOffset.Now));

        public void RawSql(string sql, params (string Name, object Value)[] args)
        {
            var conn = SqliteFactory.Instance!.CreateDataSource("DataSource=" + JournalPath)!.OpenConnection()!;
            try
            {
                var cmd = conn.CreateCommand()!;
                cmd.CommandText = sql;
                foreach (var (name, value) in args)
                {
                    var p = cmd.CreateParameter()!;
                    p.ParameterName = name;
                    p.Value = value;
                    cmd.Parameters.Add(p);
                }

                cmd.ExecuteNonQuery();
            }
            finally
            {
                conn.Close();
            }
        }

        public void DeleteBlob(ContentHash hash) => File.Delete(BlobPath(hash));

        public void RewriteBlob(ContentHash hash, string content) => File.WriteAllText(BlobPath(hash), content);

        private string BlobPath(ContentHash hash)
        {
            var hex = hash.Value;
            return Path.Combine(DataDir, "blobs", hash.Algorithm, hex.Substring(0, 2), hex.Substring(2, 2), hex);
        }

        public void Dispose()
        {
            Store.Close();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Mejor esfuerzo en Windows: si algo queda bloqueado, lo deja el sistema.
            }
        }
    }
}
