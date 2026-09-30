using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

/// <summary>
/// EPIC-008 (M1, ADR-0043 §1): el audit log es append-only, persistido y redactado.
/// <see cref="FileAuditSink"/> escribe UNA línea JSON por registro —serializador generado en
/// compilación, sin reflexión—, jamás trunca el contenido previo, y vive en un archivo propio
/// fuera del journal de la sesión: borrar el journal no toca la auditoría. Las decisiones de
/// permisos de un run de sim (Allow/Ask/Deny, incluidos los Ask respondidos) terminan ahí.
/// </summary>
public sealed class FileAuditSinkTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-audit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static AuditRecord Record(string eventName, string session, string run,
        Dictionary<string, string>? details = null) =>
        new(eventName, WorkspaceId.Parse("sim"), SessionId.Parse(session), RunId.Parse(run),
            DateTimeOffset.UtcNow, "session:" + session + ":7", details ?? new Dictionary<string, string>());

    private static string Scenario(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "sim", name + ".yaml");
            if (File.Exists(Path.Combine(dir.FullName, "OmniCore.slnx")) && File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException("escenario no encontrado: " + name);
    }

    /// <summary>Línea JSON del audit parseada con su metadata de decisión.</summary>
    private sealed record AuditJsonLine(string Event, string Workspace, string Session, string Run, string At,
        string EventRef, Dictionary<string, string> Details)
    {
        public string Detail(string key) => Details.TryGetValue(key, out var value) ? value : "";
    }

    private static AuditJsonLine ParseLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var details = new Dictionary<string, string>();
        foreach (var property in root.GetProperty("details").EnumerateObject())
        {
            details[property.Name] = property.Value.GetString() ?? "";
        }

        return new AuditJsonLine(
            root.GetProperty("event").GetString()!,
            root.GetProperty("workspace").GetString()!,
            root.GetProperty("session").GetString()!,
            root.GetProperty("run").GetString()!,
            root.GetProperty("at").GetString()!,
            root.GetProperty("eventRef").GetString()!,
            details);
    }

    private static List<AuditJsonLine> ReadAudit(string auditFile) =>
        File.ReadAllLines(auditFile).Select(ParseLine).ToList();

    private static CommandAck RunScenario(OmniServer server, string yaml)
    {
        var payload = "{" + JsonObj.Field("cmd", "sim") + "," + JsonObj.Field("scenarioYaml", yaml) + "}";
        return server.Send(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
    }

    [Fact]
    public void Two_sink_instances_append_without_truncating_prior_batches()
    {
        var dir = TempDir();
        var session = Guid.NewGuid().ToString();

        // Primera instancia: primer lote.
        var first = new FileAuditSink(dir);
        first.Record(Record("toolcall.permission_evaluated", session, Guid.NewGuid().ToString()),
            CancellationToken.None);
        first.Record(Record("toolcall.permission_granted", session, Guid.NewGuid().ToString()),
            CancellationToken.None);

        // Segunda instancia sobre el mismo directorio (reapertura tras "crash"/otro proceso):
        // debe añadir al final, nunca releer-reescribir ni truncar lo previo.
        var second = new FileAuditSink(dir);
        second.Record(Record("toolcall.permission_denied", session, Guid.NewGuid().ToString()),
            CancellationToken.None);

        var file = FileAuditSink.AuditFilePath(dir);
        Assert.True(File.Exists(file), "el archivo de auditoría existe");
        var lines = File.ReadAllLines(file);
        Assert.Equal(3, lines.Length);
        Assert.Contains("toolcall.permission_evaluated", lines[0], StringComparison.Ordinal);
        Assert.Contains("toolcall.permission_granted", lines[1], StringComparison.Ordinal);
        Assert.Contains("toolcall.permission_denied", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Each_record_is_one_valid_json_line_with_ids_timestamp_and_decision_data()
    {
        var dir = TempDir();
        var session = Guid.NewGuid().ToString();
        var run = Guid.NewGuid().ToString();
        var sink = new FileAuditSink(dir);
        sink.Record(Record("toolcall.permission_evaluated", session, run, new Dictionary<string, string> {
            ["tool"] = "fake.write",
            ["decision"] = "Ask",
        }), CancellationToken.None);

        var file = FileAuditSink.AuditFilePath(dir);
        var lines = File.ReadAllLines(file);
        Assert.Single(lines); // un registro = una línea física

        var parsed = ParseLine(lines[0]);
        Assert.Equal("toolcall.permission_evaluated", parsed.Event);
        Assert.Equal("sim", parsed.Workspace);
        Assert.Equal(session, parsed.Session);
        Assert.Equal(run, parsed.Run);
        Assert.Equal("session:" + session + ":7", parsed.EventRef);
        Assert.True(DateTimeOffset.TryParse(parsed.At, out _), "at es un timestamp ISO-8601");
        Assert.Equal("fake.write", parsed.Detail("tool"));
        Assert.Equal("Ask", parsed.Detail("decision"));
    }

    [Fact]
    public void Secrets_never_reach_the_audit_file()
    {
        // ADR-0018 §4/INV-016: un secreto que llegara a un detalle de la decisión se redacta
        // ANTES de tocar disco. Secretos falsos, nunca reales. La referencia estructural al
        // evento (sesión + seq, ADR-0043 §1) SÍ queda en claro: es un id tipado del runtime.
        var dir = TempDir();
        var session = Guid.NewGuid().ToString();
        var apiKey = "sk-FAKE-a1b2c3d4e5f6-not-real";
        var bearer = "Authorization: Bearer abcdef123456789";
        var sink = new FileAuditSink(dir);
        sink.Record(Record("toolcall.permission_evaluated", session, Guid.NewGuid().ToString(),
            new Dictionary<string, string> {
                ["credential"] = apiKey,
                ["layers"] = bearer,
            }), CancellationToken.None);

        var text = File.ReadAllText(FileAuditSink.AuditFilePath(dir));
        Assert.DoesNotContain(apiKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef123456789", text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
        Assert.Contains("session:" + session + ":7", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Sim_ask_permissions_land_in_the_persistent_audit_file()
    {
        // EPIC-008: `omni sim` usa el sink persistente del workspace (composición de
        // OpenPersistentServer, el mismo camino que el CLI), no el in-memory.
        var dir = TempDir();
        var journal = Path.Combine(dir, "journal.db");
        var server = OmniHost.OpenPersistentServer(journal);

        var ack = RunScenario(server, Scenario("ask-permission"));
        Assert.True(ack.Status == "ok", ack.Error);

        var auditFile = FileAuditSink.AuditFilePath(Path.GetDirectoryName(Path.GetFullPath(journal))!);
        Assert.True(File.Exists(auditFile), "la auditoría del sim se persiste junto al journal");
        var records = ReadAudit(auditFile);

        // Identidad del run (ADR-0043 §1): cada registro lleva workspace, sesión, run y
        // referencia resoluble al evento original (sesión + seq).
        var session = server.LastSessionId()!.ToString();
        var run = server.LastRunId()!.ToString();
        Assert.All(records, r =>
        {
            Assert.Equal("sim", r.Workspace);
            Assert.Equal(session, r.Session);
            Assert.Equal(run, r.Run);
            Assert.StartsWith("session:" + session + ":", r.EventRef, StringComparison.Ordinal);
        });

        // Las decisiones del escenario: 3 Ask evaluados, 3 interacciones pedidas, 1 aprobado
        // (→ Allow) y 2 denegados (→ Deny), cada una con su tool y su dato de decisión.
        var evaluated = records.Where(r => r.Event == "toolcall.permission_evaluated").ToList();
        Assert.Equal(3, evaluated.Count);
        Assert.All(evaluated, e =>
        {
            Assert.Equal("Ask", e.Detail("decision"));
            Assert.Equal("fake.write", e.Detail("tool"));
            Assert.NotEmpty(e.Detail("layers"));
        });

        var requested = records.Where(r => r.Event == "toolcall.permission_requested").ToList();
        Assert.Equal(3, requested.Count);
        Assert.All(requested, r =>
        {
            Assert.Equal("Ask", r.Detail("decision"));
            Assert.NotEmpty(r.Detail("interaction"));
        });

        var granted = records.Where(r => r.Event == "toolcall.permission_granted").ToList();
        Assert.Single(granted);
        Assert.Equal("Allow", granted[0].Detail("decision"));

        var denied = records.Where(r => r.Event == "toolcall.permission_denied").ToList();
        Assert.Equal(2, denied.Count);
        Assert.All(denied, d =>
        {
            Assert.Equal("Deny", d.Detail("decision"));
            Assert.NotEmpty(d.Detail("cause"));
        });
    }

    [Fact]
    public void The_audit_file_survives_deleting_the_session_journal()
    {
        // ADR-0043 §1: la auditoría vive fuera del journal de la sesión y sobrevive a su purga.
        var dir = TempDir();
        var journal = Path.Combine(dir, "journal.db");
        var server = OmniHost.OpenPersistentServer(journal);

        var ack = RunScenario(server, Scenario("ask-permission"));
        Assert.True(ack.Status == "ok", ack.Error);

        var auditFile = FileAuditSink.AuditFilePath(dir);
        Assert.NotEqual(Path.GetFullPath(journal), Path.GetFullPath(auditFile));
        Assert.True(File.Exists(auditFile));
        var before = ReadAudit(auditFile).Count;
        Assert.True(before >= 9, "hay evaluaciones + interacciones + resoluciones registradas");

        // "Purga de la sesión": se borra el journal (archivo); la auditoría sigue íntegra.
        // El pool de conexiones de SQLite retiene el archivo tras Close(), así que se libera.
        (server.AcquireStore() as SqliteEventStore)?.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(journal);
        foreach (var sidecar in new[] { journal + "-wal", journal + "-shm" })
        {
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
        }

        Assert.False(File.Exists(journal));
        Assert.True(File.Exists(auditFile), "la auditoría no vive dentro del journal");
        Assert.Equal(before, ReadAudit(auditFile).Count);
    }
}
