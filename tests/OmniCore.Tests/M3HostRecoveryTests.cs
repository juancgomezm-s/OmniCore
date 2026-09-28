using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// E2E del Host de la recuperación del Run real (ADR-0004 §5, ADR-0041 §2): el arranque del
/// <c>OmniServer</c> sobre un journal reabierto detecta la ToolCall <c>Started</c>-sin-outcome de un
/// patch interrumpido y la reconcilia con el <c>FilesystemReconciler</c> REAL contra la raíz del
/// workspace del run (reconstruida del <c>SessionCreated</c>), sin re-ejecutar la tool ni re-inferir
/// la respuesta. Verifica: Applied/NotApplied según el hash real del archivo, ausencia de doble
/// efecto (nunca un <c>toolcall.succeeded</c>), y que una SEGUNDA reapertura NO agrega eventos
/// (idempotente). También cubre que un Run terminal no se reanuda.
/// </summary>
public sealed class M3HostRecoveryTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m3-host-recovery", Guid.NewGuid().ToString("N"));
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

    /// <summary>Resultado de escribir el journal interrumpido: rutas + ids reanudables.</summary>
    private sealed record CrashJournal(string StorePath, string StateFile, string Workspace, SessionId SessionId)
    {
    }

    /// <summary>
    /// Escribe en el journal SQLite una sesión de Explorer con un run reanudable cuyo patch quedó
    /// interrumpido tras <c>ToolCallStarted</c> con Barrier (crash antes del outcome), y apunta el
    /// state file a esa sesión/run. El Host reabre ese store en su constructor y detecta el huérfano.
    /// </summary>
    private static CrashJournal WriteInterruptedPatch(bool terminalRun)
    {
        var root = TempDir();
        var ws = root + "\\ws";
        Directory.CreateDirectory(ws);
        var storePath = root + "\\journal.db";
        var stateFile = root + "\\lastsession.txt";
        var original = "linea-uno\nlinea-dos\n";
        var pre = VersionOf(original);
        var updated = "linea-uno\nlinea-dos-C\n";
        var post = VersionOf(updated);
        File.WriteAllText(ws + "\\doc.txt", original);

        var codecs = EventCodecs.Create();
        var store = new SqliteEventStore(storePath);
        try
        {
            var sessionId = SessionId.New();
            var runId = RunId.New();
            var callId = ToolCallId.New();
            var stream = new EventStream(store, codecs, sessionId);
            stream.Append(new SessionCreated(sessionId, WorkspaceId.Of(ws).ToString(), ws, ProfileId.New(),
                DateTimeOffset.UtcNow));
            stream.Append(new RunCreated(runId, sessionId, "corregir un test", RunMode.Act,
                ExecutionStrategy.Direct, FailurePolicy.BlockDependents,
                new TaskBudget(null, null, null, null), TaskId.New(), DateTimeOffset.UtcNow));
            stream.Append(new RunStarted(runId));
            stream.Append(new ToolCallRequested(callId, "pc-1", "filesystem.patch", PatchArgs("doc.txt", pre)));
            stream.Append(new ToolCallPrepared(callId, PatchArgs("doc.txt", pre)));
            stream.Append(new PermissionEvaluated(callId, PermissionDecision.Allow, "{}", null));
            stream.Append(new ToolCallAuthorized(callId));
            var meta = FilesystemReconciliationMetadata.Encode("doc.txt", pre, post);
            stream.Append(new ToolCallStarted(callId, EffectClass.NonIdempotent, meta), DurabilityClass.Barrier);
            if (terminalRun)
            {
                // Un crash puede dejar el Started huérfano de un run que luego se declaró fallido.
                stream.Append(new RunFailed(runId, "terminado tras el crash"));
            }

            File.WriteAllText(stateFile, sessionId.ToString() + "\n" + runId.ToString());
            return new CrashJournal(storePath, stateFile, ws, sessionId);
        }
        finally
        {
            store.Close();
        }
    }

    private static int CountEvents(SqliteEventStore store, SessionId sessionId, string type)
    {
        var codecs = EventCodecs.Create();
        var n = 0;
        foreach (var evt in store.ReadFrom(sessionId, 1))
        {
            if (evt.Type.ToString() == type)
            {
                n += 1;
            }
        }

        return n;
    }

    private static List<ToolCallReconciled> Reconciled(SqliteEventStore store, SessionId sessionId)
    {
        var codecs = EventCodecs.Create();
        var found = new List<ToolCallReconciled>();
        foreach (var evt in store.ReadFrom(sessionId, 1))
        {
            var payload = codecs.CodecFor(evt.Type).Decode(evt.Type, evt.PayloadJson);
            if (payload is ToolCallReconciled r)
            {
                found.Add(r);
            }
        }

        return found;
    }

    [Fact]
    public void Server_startup_reconciles_interrupted_patch_as_applied_and_is_idempotent()
    {
        RunReopenScenario(true, ReconciliationOutcome.Applied);
    }

    [Fact]
    public void Server_startup_reconciles_interrupted_patch_as_not_applied_and_is_idempotent()
    {
        RunReopenScenario(false, ReconciliationOutcome.NotApplied);
    }

    /// <summary>
    /// Escenario E2E: reabrir el Host sobre un journal con patch interrumpido DEBE reconciliar el
    /// efecto contra el hash real (Applied si el archivo quedó en post-hash, NotApplied si sigue en
    /// pre-hash), sin doble efecto, y una segunda reapertura no agrega eventos.
    /// </summary>
    private static void RunReopenScenario(bool fileIsApplied, ReconciliationOutcome expected)
    {
        var crash = WriteInterruptedPatch(false);
        try
        {
            var updated = "linea-uno\nlinea-dos-C\n";
            if (fileIsApplied)
            {
                File.WriteAllText(crash.Workspace + "\\doc.txt", updated);
            }

            // 1.ª reapertura: el propio arranque del Host detecta y reconcilia el Started-huérfano.
            var store1 = new SqliteEventStore(crash.StorePath);
            // El cierre del <c>OmniServer</c> dispara la recuperación al arrancar (efecto probado: eventos).
            new OmniServer(store1, EventCodecs.Create(), new InMemoryAuditSink(), crash.StateFile);

            var results = Reconciled(store1, crash.SessionId);
            Assert.Single(results);
            Assert.Equal(expected, results[0].Outcome);
            Assert.Equal(1, CountEvents(store1, crash.SessionId, "toolcall.effect_unknown"));
            Assert.Equal(1, CountEvents(store1, crash.SessionId, "toolcall.reconciled"));
            // Ausencia de doble efecto: el recovery NUNCA produce un succeeded ni re-ejecuta.
            Assert.Equal(0, CountEvents(store1, crash.SessionId, "toolcall.succeeded"));
            // El archivo no se tocó por el recovery (solo observa): sigue como se dejó tras el crash.
            var expectedContent = fileIsApplied ? updated : "linea-uno\nlinea-dos\n";
            Assert.Equal(expectedContent, File.ReadAllText(crash.Workspace + "\\doc.txt"));
            store1.Close();

            // 2.ª reapertura: idempotente, NO agrega eventos de reconciliación ni de efecto.
            var store2 = new SqliteEventStore(crash.StorePath);
            new OmniServer(store2, EventCodecs.Create(), new InMemoryAuditSink(), crash.StateFile);
            Assert.Equal(1, CountEvents(store2, crash.SessionId, "toolcall.effect_unknown"));
            Assert.Equal(1, CountEvents(store2, crash.SessionId, "toolcall.reconciled"));
            Assert.Equal(0, CountEvents(store2, crash.SessionId, "toolcall.succeeded"));
            Assert.Single(Reconciled(store2, crash.SessionId));
            store2.Close();
        }
        finally
        {
            RmDir(Path.GetDirectoryName(crash.StorePath)!);
        }
    }

    [Fact]
    public void Server_startup_does_not_resume_a_terminal_run()
    {
        var crash = WriteInterruptedPatch(true);
        try
        {
            var store = new SqliteEventStore(crash.StorePath);
            new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink(), crash.StateFile);
            // El Run es terminal (Failed): la recuperación no debe emitir nada.
            Assert.Equal(0, CountEvents(store, crash.SessionId, "toolcall.effect_unknown"));
            Assert.Equal(0, CountEvents(store, crash.SessionId, "toolcall.reconciled"));
            Assert.Equal(0, CountEvents(store, crash.SessionId, "toolcall.succeeded"));
            store.Close();
        }
        finally
        {
            RmDir(Path.GetDirectoryName(crash.StorePath)!);
        }
    }
}