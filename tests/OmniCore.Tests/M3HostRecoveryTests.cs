using OmniCore.Abstractions;
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
    /// Emite también el origen durable de la raíz (<c>WorkspaceRootEstablished</c>) salvo que
    /// <c>recordRoot</c> sea false (para el caso adversarial de raíz ausente) o se indique un
    /// <c>rootOverride</c> (raíz cambiada/borrada).
    /// </summary>
    private static CrashJournal WriteInterruptedPatch(bool terminalRun)
        => WriteInterruptedPatch(terminalRun, true, null, "doc.txt");

    private static CrashJournal WriteInterruptedPatch(bool terminalRun, bool recordRoot, string? rootOverride)
        => WriteInterruptedPatch(terminalRun, recordRoot, rootOverride, "doc.txt");

    private static CrashJournal WriteInterruptedPatch(bool terminalRun, bool recordRoot, string? rootOverride,
        string relativePath)
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
            if (recordRoot)
            {
                // El único origen aceptado por la recuperación: la raíz durable y verificada.
                // Además se escribe la identidad durable (marcador dentro del workspace) y viaja
                // en el evento, para que la recuperación verifique que la ruta no fue sustituida
                // por symlink/junction (ADR-0004 §5, auditoría M3).
                var durableIdentity = OmniCore.Host.WorkspaceRootIdentity.Establish(ws);
                stream.Append(new WorkspaceRootEstablished(sessionId,
                    rootOverride is null ? ws : rootOverride!, DateTimeOffset.UtcNow, durableIdentity));
            }

            stream.Append(new RunCreated(runId, sessionId, "corregir un test", RunMode.Act,
                ExecutionStrategy.Direct, FailurePolicy.BlockDependents,
                new TaskBudget(null, null, null, null), TaskId.New(), DateTimeOffset.UtcNow));
            stream.Append(new RunStarted(runId));
            stream.Append(new ToolCallRequested(callId, "pc-1", "filesystem.patch", PatchArgs("doc.txt", pre)));
            stream.Append(new ToolCallPrepared(callId, PatchArgs("doc.txt", pre)));
            stream.Append(new PermissionEvaluated(callId, PermissionDecision.Allow, "{}", null));
            stream.Append(new ToolCallAuthorized(callId));
            var meta = FilesystemReconciliationMetadata.Encode(relativePath, pre, post);
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
            var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink(), crash.StateFile);
            // El Run es terminal (Failed): la recuperación no debe emitir nada ni informar bloqueo.
            Assert.Equal(0, CountEvents(store, crash.SessionId, "toolcall.effect_unknown"));
            Assert.Equal(0, CountEvents(store, crash.SessionId, "toolcall.reconciled"));
            Assert.Equal(0, CountEvents(store, crash.SessionId, "toolcall.succeeded"));
            Assert.Null(server.LastRecoveryProblem());
            store.Close();
        }
        finally
        {
            RmDir(Path.GetDirectoryName(crash.StorePath)!);
        }
    }

    /// <summary>Comprueba un bloqueo de recuperación: visible, sin reconciliar y sin doble efecto.</summary>
    private static void AssertBlocked(OmniServer server, SqliteEventStore store, SessionId sessionId,
        string workspace)
    {
        Assert.True(server.LastRecoveryProblem() is not null,
            "la recuperación bloqueada debe ser VISIBLE (motivo expuesto)");
        Assert.Contains("blocked", server.Query("state", CancellationToken.None)!.Json);
        // Sin reconciliar: no se emite EffectUnknown ni Reconciled y NO se clasifica Applied.
        Assert.Equal(0, CountEvents(store, sessionId, "toolcall.effect_unknown"));
        Assert.Equal(0, CountEvents(store, sessionId, "toolcall.reconciled"));
        Assert.Equal(0, CountEvents(store, sessionId, "toolcall.succeeded"));
        // El archivo queda exactamente como se dejó tras el crash: la recuperación solo observa.
        Assert.Equal("linea-uno\nlinea-dos\n", File.ReadAllText(workspace + "\\doc.txt"));
    }

    [Fact]
    public void Server_startup_blocks_when_durable_root_is_missing()
    {
        // Sesión de un run real PERO SIN el origen durable de raíz: nunca se sustituye por cwd ni
        // por una ruta de display. La recuperación debe bloquearse y quedar visible.
        var crash = WriteInterruptedPatch(false, false, null);
        try
        {
            var store = new SqliteEventStore(crash.StorePath);
            var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink(), crash.StateFile);
            AssertBlocked(server, store, crash.SessionId, crash.Workspace);
            Assert.Contains("raíz", server.LastRecoveryProblem()!);
            store.Close();
        }
        finally
        {
            RmDir(Path.GetDirectoryName(crash.StorePath)!);
        }
    }

    [Fact]
    public void Server_startup_blocks_when_durable_root_is_changed_or_absent()
    {
        // La raíz persistida apunta a un directorio que ya no existe (workspace movido/borrado):
        // verificada sobre el filesystem real → bloqueada, nunca se reconcilia contra otro sitio.
        var root = TempDir();
        var gone = root + "\\moved-workspace";
        var crash = WriteInterruptedPatch(false, true, gone);
        try
        {
            var store = new SqliteEventStore(crash.StorePath);
            var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink(), crash.StateFile);
            AssertBlocked(server, store, crash.SessionId, crash.Workspace);
            Assert.Contains("verificada", server.LastRecoveryProblem()!);
            store.Close();
        }
        finally
        {
            RmDir(Path.GetDirectoryName(crash.StorePath)!);
        }
    }

    [Fact]
    public void Server_startup_surfaces_journal_or_reconciler_error()
    {
        // Error del store/reconciliador durante la recuperación: NO se traga con catch general a 0.
        // El bloqueo queda visible (LastRecoveryProblem + query state) y no se escribe nada.
        var codecs = EventCodecs.Create();
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var stateFile = TempDir() + "\\lastsession.txt";
        File.WriteAllText(stateFile, sessionId.ToString() + "\n" + runId.ToString());
        try
        {
            var store = new ThrowingReadStore(new InMemoryEventStore());
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile);
            Assert.True(server.LastRecoveryProblem() is not null, "un fallo del journal debe ser visible");
            Assert.Contains("blocked", server.Query("state", CancellationToken.None)!.Json);
            // Nada que revisar en el store fallido: InMemoryEventStore no recibió escrituras.
        }
        finally
        {
            RmDir(Path.GetDirectoryName(stateFile)!);
        }
    }

    [Fact]
    public void Server_startup_never_reads_outside_workspace_on_traversal_attempt()
    {
        // Metadatos persistidos con intento de escape del workspace (../ fuera): la recuperación
        // debe fallar cerrado (Unresolvable), NO leer ningún archivo fuera del workspace y nunca
        // clasificar Applied. Este es el riesgo exacto que arregla la no-sustitución por cwd.
        var crash = WriteInterruptedPatch(false, true, null, "..\\outside\\secret.txt");
        try
        {
            var outside = Path.GetDirectoryName(crash.Workspace)! + "\\outside";
            Directory.CreateDirectory(outside);
            File.WriteAllText(outside + "\\secret.txt", "SECRETO-FUERA");

            var store = new SqliteEventStore(crash.StorePath);
            var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink(), crash.StateFile);
            Assert.Null(server.LastRecoveryProblem()); // la raíz es válida: no es un bloqueo
            var results = Reconciled(store, crash.SessionId);
            Assert.Single(results);
            Assert.Equal(ReconciliationOutcome.Unresolvable, results[0].Outcome);
            // El archivo fuera del workspace no fue leído ni modificado por la recuperación.
            Assert.Equal("SECRETO-FUERA", File.ReadAllText(outside + "\\secret.txt"));
            // Detalle de falla cerrado (nunca Applied) visible en el evento reconciliado.
            Assert.Equal(0, CountEvents(store, crash.SessionId, "toolcall.succeeded"));
            store.Close();
        }
        finally
        {
            RmDir(Path.GetDirectoryName(crash.StorePath)!);
        }
    }
}

/// <summary>
/// Envoltorio de <c>IEventStore</c> que falla en la LECTURA (simula un journal corrupto o un error
/// del reconciliador) para probar que la recuperación del Host expone el fallo y no lo traga.
/// </summary>
public sealed class ThrowingReadStore : IEventStore
{
    private readonly IEventStore _inner;

    public IEventStore Inner() => _inner;

    public ThrowingReadStore(IEventStore inner) => _inner = inner;

    public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
        CancellationToken cancellationToken) => _inner.Append(sessionId, evt, durability, cancellationToken);

    public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> evts, DurabilityClass durability,
        CancellationToken cancellationToken) => _inner.AppendBatch(sessionId, evts, durability, cancellationToken);

    public long CurrentSequence(SessionId sessionId) => _inner.CurrentSequence(sessionId);

    public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive)
        => throw new InvalidOperationException("journal falló al leer (test adversarial)");
}