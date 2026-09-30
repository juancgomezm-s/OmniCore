using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>
/// Tests de M4 V2: purga de sesión (auditoría previa, borrado atómico), GC mark-and-sweep de
/// blobs (conjunto vivo, gracia, nunca borra referenciados, dry-run) y retención de auditoría
/// (180 d por defecto, la purga deja traza de sí misma, conserva lo ilegible).
/// Todos deterministas: reloj y contenido explícitos.
/// </summary>
public sealed class MaintenanceTests
{
    // ------------------------------------------------------------------ purga de sesión

    [Fact]
    public void Session_purge_audits_before_deleting_and_removes_events()
    {
        using var fx = NewFixture();
        var session = fx.AppendSessionWithArtifact();
        var other = fx.AppendSessionWithArtifact();
        var audit = new InMemoryAuditSink();
        var purger = new SessionPurger(fx.Store, audit);
        var now = DateTimeOffset.Now;

        var result = purger.Purge(session, null, now, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, result!.EventsDeleted);
        Assert.Equal(0, fx.Store.CountEvents(session));
        Assert.Equal(3, fx.Store.CountEvents(other));
        // Auditoría previa: registrada con la sesión y el conteo, antes del borrado.
        var record = Assert.Single(audit.Records());
        Assert.Equal("session.purged", record.EventName);
        Assert.Equal(session, record.Session);
        Assert.Equal("3", record.Details["events"]);
    }

    [Fact]
    public void Session_purge_of_missing_session_returns_null_without_audit()
    {
        using var fx = NewFixture();
        var audit = new InMemoryAuditSink();
        var purger = new SessionPurger(fx.Store, audit);

        var result = purger.Purge(SessionId.New(), null, DateTimeOffset.Now, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, audit.Count());
    }

    // ------------------------------------------------------------------ GC

    [Fact]
    public void Gc_never_deletes_referenced_blobs()
    {
        using var fx = NewFixture();
        var artifact = fx.StoreArtifact("contenido referenciado");
        var session = SessionId.New();
        fx.AppendEventWithArtifact(session, artifact);
        fx.WriteOrphanBlob("orfeano-viejo", hoursOld: 48);
        var gc = new ArtifactGc(fx.DataDir);

        var result = gc.Sweep(fx.JournalPath, grace: TimeSpan.Zero, dryRun: false, now: DateTimeOffset.Now,
            cancellationToken: CancellationToken.None);

        // El referenciado sobrevive aunque tenga cualquier edad; el huérfano viejo se recoge.
        Assert.True(File.Exists(fx.BlobPath(artifact.Hash.Value)), "el blob referenciado se borró");
        Assert.Equal(1, result.Deleted);
        Assert.False(File.Exists(fx.BlobPathOf("orfeano-viejo")));
    }

    [Fact]
    public void Gc_keeps_externalized_output_referenced_inside_a_journaled_context_snapshot()
    {
        using var fx = NewFixture();
        var output = fx.StoreArtifact("externalized output retained through context reference");
        var outputPath = fx.BlobPath(output.Hash.Value);
        var now = DateTimeOffset.UtcNow;
        File.SetLastWriteTimeUtc(outputPath, now.UtcDateTime.AddDays(-3));
        var snapshot = fx.Artifacts.PutText(
            "[tool output externalized; re-read with artifact.read using hash=\"" + output.Hash.Value + "\"]",
            "application/json", ArtifactKind.ContextSnapshot, Sensitivity.Normal);
        var session = SessionId.New();
        fx.AppendEvent(session, EventType.Of("session.created"), 1,
            fx.Codecs.CodecFor(EventType.Of("session.created")).Encode(
                new SessionCreated(session, "ws", "c:\\ws", ProfileId.New(), now)));
        var turnStarted = new TurnStarted(TurnId.New(), LaneId.New(), null, snapshot);
        fx.AppendEvent(session, turnStarted.Type(), turnStarted.SchemaVersion(),
            fx.Codecs.CodecFor(turnStarted.Type()).Encode(turnStarted), new[] { snapshot });

        var result = new ArtifactGc(fx.DataDir).Sweep(fx.JournalPath, TimeSpan.Zero, dryRun: false, now,
            CancellationToken.None);

        Assert.True(File.Exists(outputPath), "el hash anidado en ContextSnapshot debe considerarse vivo");
        Assert.Equal(2, result.LiveReferenced);
        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public void Gc_keeps_orphans_inside_grace_and_collects_them_after_it()
    {
        using var fx = NewFixture();
        fx.AppendSessionWithArtifact();
        var young = fx.WriteOrphanBlob("orfeano-joven", hoursOld: 2);
        var old = fx.WriteOrphanBlob("orfeano-viejo", hoursOld: 48);
        var gc = new ArtifactGc(fx.DataDir);
        var now = DateTimeOffset.Now;

        // Gracia por defecto (24 h): el joven sobrevive, el viejo se recoge.
        var withGrace = gc.Sweep(fx.JournalPath, ArtifactGc.DefaultGrace, dryRun: false, now,
            CancellationToken.None);
        Assert.True(File.Exists(young));
        Assert.False(File.Exists(old));
        Assert.Equal(1, withGrace.KeptByGrace);
        Assert.Equal(1, withGrace.Deleted);

        // Sin gracia: el joven también se recoge (valor configurable → comportamiento distinto).
        fx.WriteOrphanBlob("orfeano-joven", hoursOld: 2);
        var noGrace = gc.Sweep(fx.JournalPath, TimeSpan.Zero, dryRun: false, now, CancellationToken.None);
        Assert.Equal(1, noGrace.Deleted);
        Assert.False(File.Exists(fx.BlobPathOf("orfeano-joven")));
    }

    [Fact]
    public void Gc_dry_run_deletes_nothing_and_collects_blobs_of_purged_session()
    {
        using var fx = NewFixture();
        var artifact = fx.StoreArtifact("respuesta del modelo purgado");
        var session = fx.AppendEventWithArtifact(SessionId.New(), artifact);
        fx.WriteOrphanBlob("orfeano-viejo", hoursOld: 72);
        var gc = new ArtifactGc(fx.DataDir);
        var now = DateTimeOffset.Now;

        var dry = gc.Sweep(fx.JournalPath, TimeSpan.Zero, dryRun: true, now, CancellationToken.None);
        Assert.True(dry.DryRun);
        // En el dry-run solo el huérfano es candidato: el blob de la sesión sigue referenciado.
        Assert.Equal(1, dry.Deleted);
        Assert.Equal(1, dry.LiveReferenced);
        Assert.True(File.Exists(fx.BlobPath(artifact.Hash.Value)), "dry-run no debe borrar nada");
        Assert.True(File.Exists(fx.BlobPathOf("orfeano-viejo")), "dry-run no debe borrar nada");

        // Purga + GC: el blob de la sesión purgada queda huérfano y lo recoge el GC.
        var audit = new InMemoryAuditSink();
        Assert.NotNull(new SessionPurger(fx.Store, audit).Purge(session, null, now, CancellationToken.None));
        var after = gc.Sweep(fx.JournalPath, TimeSpan.Zero, dryRun: false, now, CancellationToken.None);
        Assert.Equal(2, after.Deleted);
        Assert.False(File.Exists(fx.BlobPath(artifact.Hash.Value)));
    }

    [Fact]
    public void Gc_default_grace_protects_a_deduplicated_in_flight_blob_until_event_commit()
    {
        using var fx = NewFixture();
        var artifact = fx.StoreArtifact("write pending event commit");
        var path = fx.BlobPath(artifact.Hash.Value);
        var now = DateTimeOffset.UtcNow;
        File.SetLastWriteTimeUtc(path, now.UtcDateTime.AddDays(-3));

        // PutText deduplicates this old blob but renews its in-flight lease timestamp.
        fx.Artifacts.PutText("write pending event commit", "text/plain", ArtifactKind.ModelResponse,
            Sensitivity.Normal);
        var result = new ArtifactGc(fx.DataDir).Sweep(fx.JournalPath, ArtifactGc.DefaultGrace,
            dryRun: false, now, CancellationToken.None);

        Assert.True(File.Exists(path), "el blob recién vuelto a publicar debe sobrevivir a la gracia");
        Assert.Equal(1, result.KeptByGrace);
        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public void Gc_marks_payload_hashes_even_when_payload_is_corrupt()
    {
        using var fx = NewFixture();
        var artifact = fx.StoreArtifact("payload corrupto pero hash vivo");
        var session = SessionId.New();
        fx.AppendEventWithArtifact(session, artifact);
        fx.Store.Close();
        // Corrompe el envelope: el GC no debe confiar; el payload sigue mencionando el hash.
        fx.RawSql("UPDATE events SET artifacts = '' WHERE session_id = :sid",
            ("sid", session.ToString()));

        var result = new ArtifactGc(fx.DataDir).Sweep(fx.JournalPath, TimeSpan.Zero, false, DateTimeOffset.Now,
            CancellationToken.None);

        Assert.True(File.Exists(fx.BlobPath(artifact.Hash.Value)),
            "un hash mencionado en el payload debe seguir vivo aunque el envelope corrompa");
        Assert.Equal(0, result.Deleted);
    }

    // ------------------------------------------------------------------ retención de auditoría

    [Fact]
    public void Audit_retention_removes_old_records_keeps_recent_and_leaves_its_own_trace()
    {
        using var fx = NewFixture();
        var sink = new FileAuditSink(fx.DataDir);
        var old = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var recent = DateTimeOffset.Now;
        sink.Record(new AuditRecord("permission.granted", null, SessionId.New(), null, old, null,
            new Dictionary<string, string>()), CancellationToken.None);
        sink.Record(new AuditRecord("session.purged", null, SessionId.New(), null, recent, null,
            new Dictionary<string, string> { ["events"] = "3" }), CancellationToken.None);
        var retention = new AuditRetention(fx.DataDir, sink);
        var now = DateTimeOffset.Now;

        var result = retention.PurgeBefore(now.AddDays(-180), dryRun: false, now: now,
            cancellationToken: CancellationToken.None);

        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.Kept);
        var lines = File.ReadAllLines(FileAuditSink.AuditFilePath(fx.DataDir));
        // El registro conservado + la traza de la propia purga (audit.purge), ambos JSONL.
        Assert.Equal(2, lines.Length);
        using var keptRecord = System.Text.Json.JsonDocument.Parse(lines[0]);
        Assert.Equal("session.purged", keptRecord.RootElement.GetProperty("event").GetString());
        Assert.Equal("3", keptRecord.RootElement.GetProperty("details").GetProperty("events").GetString());
        using var purgeRecord = System.Text.Json.JsonDocument.Parse(lines[1]);
        Assert.Equal("audit.purge", purgeRecord.RootElement.GetProperty("event").GetString());
        Assert.Equal("1", purgeRecord.RootElement.GetProperty("details").GetProperty("removed").GetString());
    }

    [Fact]
    public async Task Audit_purge_command_uses_user_scope_jsonl_and_default_180_day_retention()
    {
        using var fx = NewFixture();
        var sink = new FileAuditSink(fx.DataDir);
        var now = DateTimeOffset.UtcNow;
        sink.Record(new AuditRecord("too.old", null, null, null, now.AddDays(-181), null,
            new Dictionary<string, string>()), CancellationToken.None);
        sink.Record(new AuditRecord("still.retained", null, null, null, now.AddDays(-179), null,
            new Dictionary<string, string>()), CancellationToken.None);
        var output = new StringWriter();

        var exitCode = await MaintenanceCommandHost.AuditPurge(
            new[] { "audit", "purge", "--data-dir", fx.DataDir }, output);

        Assert.Equal(0, exitCode);
        Assert.Contains(FileAuditSink.AuditFilePath(fx.DataDir), output.ToString());
        var lines = File.ReadAllLines(FileAuditSink.AuditFilePath(fx.DataDir));
        Assert.Equal(2, lines.Length);
        Assert.Contains("still.retained", lines[0]);
        Assert.Contains("audit.purge", lines[1]);
        Assert.DoesNotContain("too.old", File.ReadAllText(FileAuditSink.AuditFilePath(fx.DataDir)));
    }

    [Fact]
    public async Task Session_purge_command_keeps_audit_at_user_scope_not_workspace_scope()
    {
        using var fx = NewFixture();
        var session = fx.AppendSessionWithArtifact();
        var userDataDir = Path.Combine(fx.Root, "user-data");
        var output = new StringWriter();

        var exitCode = await MaintenanceCommandHost.SessionPurge(new[] {
            "session", "purge", session.ToString(), "--journal", fx.JournalPath, "--data-dir", userDataDir,
        }, output);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, fx.Store.CountEvents(session));
        Assert.True(File.Exists(FileAuditSink.AuditFilePath(userDataDir)));
        Assert.False(File.Exists(FileAuditSink.AuditFilePath(fx.DataDir)),
            "el registro de purga no debe escribirse bajo los datos del workspace");
        Assert.Contains("session.purged", File.ReadAllText(FileAuditSink.AuditFilePath(userDataDir)));
    }

    [Fact]
    public void Audit_retention_dry_run_has_no_effects()
    {
        using var fx = NewFixture();
        var sink = new FileAuditSink(fx.DataDir);
        var old = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        sink.Record(new AuditRecord("permission.granted", null, null, null, old, null,
            new Dictionary<string, string>()), CancellationToken.None);
        var retention = new AuditRetention(fx.DataDir, sink);
        var file = FileAuditSink.AuditFilePath(fx.DataDir);
        var sizeBefore = new FileInfo(file).Length;

        var result = retention.PurgeBefore(DateTimeOffset.Now, dryRun: true, now: DateTimeOffset.Now,
            cancellationToken: CancellationToken.None);

        Assert.True(result.DryRun);
        Assert.Equal(1, result.Removed);
        Assert.Equal(sizeBefore, new FileInfo(file).Length);
    }

    [Fact]
    public void Audit_retention_keeps_lines_with_unparseable_timestamp()
    {
        using var fx = NewFixture();
        var file = FileAuditSink.AuditFilePath(fx.DataDir);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllLines(file, new[] {
            "{\"event\":\"permission.granted\",\"at\":\"fecha-imposible\"}",
            "{\"event\":\"session.purged\",\"at\":\"2026-01-01T12:00:00.0000000Z\"}",
        });
        var retention = new AuditRetention(fx.DataDir, new FileAuditSink(fx.DataDir));
        var now = DateTimeOffset.Now;

        var result = retention.PurgeBefore(now, dryRun: false, now: now, cancellationToken: CancellationToken.None);

        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.Kept);
        Assert.Equal(1, result.UnparseableKept);
        Assert.Contains("fecha-imposible", File.ReadAllText(file));
    }

    [Fact]
    public void Audit_timestamp_parsing_supports_the_legacy_culture_format()
    {
        // El formato previo al M4 usaba ToString() sensible a la cultura: debe seguir parseándose.
        var legacy = "{event=x,workspace=,session=,run=,at=" + new DateTimeOffset(2026, 3, 1, 10, 30, 0,
            TimeSpan.Zero).ToString() + "}";
        Assert.True(AuditRetention.TryParseTimestamp(legacy, out var at));
        Assert.Equal(2026, at.Year);
    }

    // ------------------------------------------------------------------ fixture

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "omnicore-m4-maintenance-" + Guid.NewGuid().ToString("N"));
            DataDir = Path.Combine(Root, "data");
            Directory.CreateDirectory(DataDir);
            JournalPath = Path.Combine(DataDir, "journal.db");
            Store = new SqliteEventStore(JournalPath);
            Codecs = EventCodecs.Create();
            Artifacts = new FileArtifactStore(DataDir);
        }

        public string Root { get; }

        public string DataDir { get; }

        public string JournalPath { get; }

        public SqliteEventStore Store { get; }

        public EventCodecs Codecs { get; }

        public FileArtifactStore Artifacts { get; }

        public ArtifactRef StoreArtifact(string content) =>
            Artifacts.PutText(content, "text/plain", ArtifactKind.ModelResponse, Sensitivity.Normal);

        public SessionId AppendSessionWithArtifact()
        {
            var session = SessionId.New();
            var artifact = StoreArtifact("respuesta de la sesión " + session);
            return AppendEventWithArtifact(session, artifact);
        }

        public SessionId AppendEventWithArtifact(SessionId session, ArtifactRef artifact)
        {
            AppendEvent(session, EventType.Of("session.created"), 1,
                Codecs.CodecFor(EventType.Of("session.created")).Encode(
                    new SessionCreated(session, "ws", "c:\\ws", ProfileId.New(), DateTimeOffset.Now)));
            AppendEvent(session, EventType.Of("turn.started"), 1,
                Codecs.CodecFor(EventType.Of("turn.started")).Encode(new TurnStarted(TurnId.New(), LaneId.New())));
            var payload = new ModelCompleted(TurnId.New(), artifact);
            AppendEvent(session, EventType.Of("model.completed"), 1,
                Codecs.CodecFor(payload.Type()).Encode(payload),
                artifactRefs: new[] { artifact });
            return session;
        }

        public void AppendEvent(SessionId session, EventType type, int schemaVersion, string payloadJson,
            ArtifactRef[]? artifactRefs = null)
        {
            var evt = DomainEvent.Create(session, type, schemaVersion, null, null, null, null, null, null,
                null, null, artifactRefs ?? Array.Empty<ArtifactRef>(), payloadJson);
            Store.Append(session, evt, DurabilityClass.Standard, CancellationToken.None);
        }

        /// <summary>Escribe un blob huérfano (sin evento) con la edad pedida.</summary>
        public string WriteOrphanBlob(string content, int hoursOld)
        {
            var artifact = StoreArtifact(content);
            var path = BlobPath(artifact.Hash.Value);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-hoursOld));
            return path;
        }

        public string BlobPath(string hash) =>
            Path.Combine(DataDir, "blobs", "sha256", hash.Substring(0, 2), hash.Substring(2, 2), hash);

        public string BlobPathOf(string content) => BlobPath(Sha256Of(content));

        private static string Sha256Of(string content)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

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

        public void Dispose()
        {
            Store.Close();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Mejor esfuerzo en Windows.
            }
        }
    }

    private static Fixture NewFixture() => new();
}
