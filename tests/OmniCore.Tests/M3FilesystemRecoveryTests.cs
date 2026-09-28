using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// Deterministas de la reconciliacion REAL de filesystem tras un crash (ADR-0004 §4, ADR-0041 §2):
/// el journal persiste toolcall.started con commit Barrier ANTES del efecto y, desde esta entrega,
/// lleva ademas la serializacion canonica de los metadatos de reconciliacion (ruta + hashes pre/post).
/// El resume reabre SQLite, detecta la ToolCall Started-sin-outcome DENTRO del run y la clasifica
/// contra el hash real del archivo — Applied / NotApplied / Conflict — sin re-ejecutar. Verifica
/// SQLite cerrado/reabierto, crash antes/despues del write, hashes pre/post/conflicto, idempotencia
/// de reanudar varias veces, ruta sospechosa (falla cerrado) y la maquina de estados del ciclo durable.
/// </summary>
public sealed class M3FilesystemRecoveryTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m3-fs-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RmDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (Exception)
        {
        }
    }

    private static string VersionOf(string content)
        => FilesystemPatchTool.VersionToken(System.Text.Encoding.UTF8.GetBytes(content));

    private static string PatchArgs(string relPath, string expectedVersion)
    {
        return "{\"path\":\"" + relPath + "\",\"expectedVersion\":\"" + expectedVersion
            + "\",\"oldText\":\"" + "linea-dos" + "\",\"newText\":\"" + "linea-dos-C" + "\"}";
    }

    /// <summary>Escribe en un SqliteEventStore el run + la cadena durable de una ToolCall Started-sin-outcome
    /// (crash) con commit Barrier, y cierra el almacen: replica el journal en el instante previo al efecto.</summary>
    private static void WriteCrashJournal(string storePath, SessionId sessionId, RunId runId, ToolCallId callId,
        string path, string preHash, string? startedJson)
    {
        var codecs = EventCodecs.Create();
        var store = new SqliteEventStore(storePath);
        try
        {
            var stream = new EventStream(store, codecs, sessionId);
            stream.Append(new RunCreated(runId, sessionId, "corregir un test", RunMode.Act,
                ExecutionStrategy.Direct, FailurePolicy.BlockDependents, new TaskBudget(null, null, null, null),
                TaskId.New(), DateTimeOffset.Now));
            stream.Append(new RunStarted(runId));
            stream.Append(new ToolCallRequested(callId, "pc-1", "filesystem.patch", PatchArgs(path, preHash)));
            stream.Append(new ToolCallPrepared(callId, PatchArgs(path, preHash)));
            stream.Append(new PermissionEvaluated(callId, PermissionDecision.Allow, "{}", null));
            stream.Append(new ToolCallAuthorized(callId));
            stream.Append(new ToolCallStarted(callId, EffectClass.NonIdempotent, startedJson), DurabilityClass.Barrier);
        }
        finally
        {
            store.Close();
        }
    }

    /// <summary>Devuelve los eventos ToolCallReconciled decodificados del journal de una sesion.</summary>
    private static List<ToolCallReconciled> Reconciled(SqliteEventStore store, SessionId sessionId)
    {
        var codecs = EventCodecs.Create();
        var tail = store.ReadFrom(sessionId, 1);
        var found = new List<ToolCallReconciled>();
        foreach (var evt in tail)
        {
            var payload = codecs.CodecFor(evt.Type).Decode(evt.Type, evt.PayloadJson);
            if (payload is ToolCallReconciled r)
            {
                found.Add(r);
            }
        }

        return found;
    }

    private static int CountEvents(SqliteEventStore store, SessionId sessionId, string type)
    {
        var codecs = EventCodecs.Create();
        var tail = store.ReadFrom(sessionId, 1);
        var n = 0;
        foreach (var evt in tail)
        {
            if (evt.Type.ToString() == type)
            {
                n += 1;
            }
        }

        return n;
    }

    // ---------------------------------------------------------------- maquina de estados
    [Fact]
    public void State_machine_accepts_started_to_effect_unknown_to_reconciled()
    {
        var id = ToolCallId.New();
        var stStarted = StateMachines.ApplyToolCall(ToolCallState.Authorized,
            new ToolCallStarted(id, EffectClass.NonIdempotent, null));
        Assert.Equal(ToolCallState.Started, stStarted);
        var stUnknown = StateMachines.ApplyToolCall(stStarted, new ToolCallEffectUnknown(id, EffectClass.NonIdempotent));
        Assert.Equal(ToolCallState.EffectUnknown, stUnknown);
        var stDone = StateMachines.ApplyToolCall(stUnknown,
            new ToolCallReconciled(id, ReconciliationOutcome.Applied, "x"));
        Assert.Equal(ToolCallState.Reconciled, stDone);
    }

    // ------------------------------------------------ la serializacion canonica viaja en Started
    [Fact]
    public void Real_pipeline_persists_canonical_reconciliation_metadata_in_started()
    {
        var ws = TempDir();
        var original = "linea-uno\nlinea-dos\n";
        var updated = "linea-uno\nlinea-dos-C\n";
        File.WriteAllText(ws + "\\doc.txt", original);
        try
        {
            var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService());
            var executor = ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(),
                ScriptedPermissionPolicy.WithTool("filesystem.patch", PermissionDecision.Allow), ws);
            var callId = ToolCallId.New();
            var validated = new ValidatedToolCall(callId, new ToolId("filesystem.patch"), "pc-1",
                PatchArgs("doc.txt", VersionOf(original)));
            var outcome = executor.ExecuteTool(validated, true, TestContext.Current.CancellationToken);
            Assert.True(outcome.Succeeded, "el patch debe aplicarse con exito en el pipeline real");
            ToolCallStarted? started = null;
            foreach (var e in outcome.Events)
            {
                if (e is ToolCallStarted s)
                {
                    started = s;
                }
            }

            Assert.False(started is null, "el pipeline debe emitir ToolCallStarted");
            Assert.False(started!.ReconciliationJson is null, "el Started debe llevar metadatos de reconciliacion");
            var meta = FilesystemReconciliationMetadata.Parse(started!.ReconciliationJson!);
            Assert.False(meta is null, "los metadatos deben parsear a la forma canonica");
            Assert.Equal("doc.txt", meta!.Path);
            Assert.Equal(VersionOf(original), meta!.ExpectedPreHash);
            Assert.Equal(VersionOf(updated), meta!.ExpectedPostHash);
        }
        finally
        {
            RmDir(ws);
        }
    }

    // ------------------------------------------------ clasificacion: SQLite cerrado/reabierto
    [Fact]
    public void Crash_after_write_is_classified_applied()
    {
        RunCrashClassification("applied", true, false, false);
    }

    [Fact]
    public void Crash_before_write_is_classified_not_applied()
    {
        RunCrashClassification("not-applied", false, false, false);
    }

    [Fact]
    public void Crash_with_unrelated_edit_is_classified_conflict()
    {
        RunCrashClassification("conflict", false, false, false);
    }

    [Fact]
    public void Suspicious_path_fails_closed_unresolvable()
    {
        RunCrashClassification("outside", false, true, false);
    }

    [Fact]
    public void Missing_metadata_fails_closed_unresolvable()
    {
        RunCrashClassification("no-meta", true, false, true);
    }

    /// <summary>Ejecuta el escenario completo de clasificacion sobre SQLite cerrado/reabierto con
    /// FilesystemReconciler real, y verifica outcome + idempotencia de reanudar dos veces.</summary>
    private static void RunCrashClassification(string caseName, bool appliedFlag, bool outsidePathFlag, bool noMetaFlag)
    {
        var root = TempDir();
        var ws = root + "\\ws";
        Directory.CreateDirectory(ws);
        var storePath = root + "\\journal.db";
        var original = "linea-uno\nlinea-dos\n";
        var updated = "linea-uno\nlinea-dos-C\n";
        var pre = VersionOf(original);
        var post = VersionOf(updated);
        File.WriteAllText(ws + "\\doc.txt", original);
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var callId = ToolCallId.New();
        try
        {
            string? startedJson = null;
            if (!noMetaFlag)
            {
                if (outsidePathFlag)
                {
                    startedJson = "{\"kind\":\"filesystem.patch\",\"path\":\"../fuera.txt\","
                        + "\"expectedPreHash\":\"" + pre + "\",\"expectedPostHash\":\"" + post + "\"}";
                }
                else
                {
                    startedJson = FilesystemReconciliationMetadata.Encode("doc.txt", pre, post);
                }
            }

            WriteCrashJournal(storePath, sessionId, runId, callId, "doc.txt", pre, startedJson);

            var store = new SqliteEventStore(storePath);
            if (appliedFlag)
            {
                File.WriteAllText(ws + "\\doc.txt", updated);
            }
            else if (caseName == "conflict")
            {
                File.WriteAllText(ws + "\\doc.txt", "contenido ajeno al pre y al post");
            }

            var engine = new SimulationEngine(store, EventCodecs.Create(), new InMemoryAuditSink(), ScriptedToolExecutor.Default(),
                new FilesystemReconciler(new PathBoundaryValidator()), ws);
            var stream2 = new EventStream(store, EventCodecs.Create(), sessionId);
            var reconciled = engine.Resume(sessionId, runId, stream2, ws);
            Assert.Equal(1, reconciled);

            var results = Reconciled(store, sessionId);
            Assert.Single(results);
            Assert.Equal(callId, results[0].ToolCallId);
            Assert.Equal(1, CountEvents(store, sessionId, "toolcall.effect_unknown"));
            Assert.Equal(0, CountEvents(store, sessionId, "toolcall.succeeded"));
            Assert.Equal(ExpectedOutcome(caseName), results[0].Outcome);
            store.Close();

            // Idempotencia: reanudar de nuevo no duplica eventos ni reconcilia nada nuevo.
            var store2 = new SqliteEventStore(storePath);
            var engine2 = new SimulationEngine(store2, EventCodecs.Create(), new InMemoryAuditSink(), ScriptedToolExecutor.Default(),
                new FilesystemReconciler(new PathBoundaryValidator()), ws);
            var stream3 = new EventStream(store2, EventCodecs.Create(), sessionId);
            var reconciledAgain = engine2.Resume(sessionId, runId, stream3, ws);
            Assert.Equal(0, reconciledAgain);
            Assert.Equal(1, CountEvents(store2, sessionId, "toolcall.reconciled"));
            Assert.Equal(1, CountEvents(store2, sessionId, "toolcall.effect_unknown"));
            store2.Close();
        }
        finally
        {
            RmDir(root);
        }
    }

    private static ReconciliationOutcome ExpectedOutcome(string caseName)
    {
        switch (caseName)
        {
            case "applied":
                return ReconciliationOutcome.Applied;
            case "not-applied":
                return ReconciliationOutcome.NotApplied;
            case "conflict":
                return ReconciliationOutcome.Conflict;
            default:
                return ReconciliationOutcome.Unresolvable;
        }
    }

    // ------------------------------ serializacion canonica / ruta sospechosa
    [Fact]
    public void Canonical_metadata_roundtrips_and_rejects_suspicious_paths()
    {
        var pre = VersionOf("a");
        var post = VersionOf("b");
        var encoded = FilesystemReconciliationMetadata.Encode("src/archivo.txt", pre, post);
        var meta = FilesystemReconciliationMetadata.Parse(encoded);
        Assert.False(meta is null);
        Assert.Equal("src/archivo.txt", meta!.Path);
        Assert.Equal(pre, meta!.ExpectedPreHash);
        Assert.Equal(post, meta!.ExpectedPostHash);

        Assert.True(FilesystemReconciliationMetadata.Parse(null) is null);
        Assert.True(FilesystemReconciliationMetadata.Parse("no-json") is null);
        Assert.True(FilesystemReconciliationMetadata.Parse("{\"kind\":\"otro\"}") is null);
        Assert.True(FilesystemReconciliationMetadata.Parse(
            FilesystemReconciliationMetadata.Encode("", pre, post)) is null);
        Assert.True(FilesystemReconciliationMetadata.Parse(
            FilesystemReconciliationMetadata.Encode("/absoluta.txt", pre, post)) is null);
        Assert.True(FilesystemReconciliationMetadata.Parse(
            FilesystemReconciliationMetadata.Encode("C:/unidad.txt", pre, post)) is null);
        Assert.True(FilesystemReconciliationMetadata.Parse(
            FilesystemReconciliationMetadata.Encode("../escape.txt", pre, post)) is null);
        Assert.True(FilesystemReconciliationMetadata.Parse(
            FilesystemReconciliationMetadata.Encode("a/../b.txt", pre, post)) is null);
        Assert.True(FilesystemReconciliationMetadata.Parse("{\"kind\":\"filesystem.patch\",\"path\":\"x.txt\","
            + "\"expectedPreHash\":\"no-es-sha\",\"expectedPostHash\":\"\"}") is null);
    }
}