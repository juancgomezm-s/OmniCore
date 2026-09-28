using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// Tests adversariales de la IDENTIDAD DURADERA del workspace (ADR-0004 §5, auditoría M3):
/// una raíz sustituida por symlink/junction, reemplazada por otro árbol o sin identidad
/// registrada nunca debe pasar la verificación y reconciliar (clasificar Applied) contra el
/// árbol equivocado. La recuperación FALLA CERRADO: bloqueada, visible, sin re-ejecutar ni
/// clasificar nada. Se cubren Windows (junction vía mklink) y POSIX (symlink vía API) según
/// la plataforma, más un caso determinista cross-platform (sustitución por otro árbol con
/// distinto marcador) que no depende de privilegios para crear enlaces.
/// </summary>
public sealed class M3WorkspaceIdentityTests
{
    private static string TempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m3-identity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RmDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception)
        {
        }
    }

    private static string VersionOf(string content)
        => FilesystemPatchTool.VersionToken(System.Text.Encoding.UTF8.GetBytes(content));

    private static string PatchArgs(string relPath, string expectedVersion) =>
        "{\"path\":\"" + relPath + "\",\"expectedVersion\":\"" + expectedVersion
        + "\",\"oldText\":\"" + "linea-dos" + "\",\"newText\":\"" + "linea-dos-C" + "\"}";

    private sealed record Setup(string StorePath, string StateFile, SessionId SessionId);

    /// <summary>
    /// Escribe un journal con un run reanudable cuyo patch quedó interrumpido (Started sin outcome),
    /// apuntando la raíz canónica a <c>rootPath</c> y registrando <c>durableIdentity</c> (vacío =
    /// sin identidad duradera, p. ej. evento legacy). El archivo doc.txt queda en pre-hash.
    /// </summary>
    private static Setup WriteInterruptedPatch(string rootPath, string durableIdentity)
    {
        var tmp = TempRoot();
        var storePath = tmp + "\\journal.db";
        var stateFile = tmp + "\\lastsession.txt";
        var original = "linea-uno\nlinea-dos\n";
        var pre = VersionOf(original);
        File.WriteAllText(rootPath + "\\doc.txt", original);

        var codecs = EventCodecs.Create();
        var store = new SqliteEventStore(storePath);
        try
        {
            var sessionId = SessionId.New();
            var runId = RunId.New();
            var callId = ToolCallId.New();
            var stream = new EventStream(store, codecs, sessionId);
            stream.Append(new SessionCreated(sessionId, WorkspaceId.Of(rootPath).ToString(), rootPath,
                ProfileId.New(), DateTimeOffset.UtcNow));
            stream.Append(new WorkspaceRootEstablished(sessionId, rootPath, DateTimeOffset.UtcNow,
                durableIdentity ?? ""));
            stream.Append(new RunCreated(runId, sessionId, "corregir un test", RunMode.Act,
                ExecutionStrategy.Direct, FailurePolicy.BlockDependents,
                new TaskBudget(null, null, null, null), TaskId.New(), DateTimeOffset.UtcNow));
            stream.Append(new RunStarted(runId));
            stream.Append(new ToolCallRequested(callId, "pc-1", "filesystem.patch", PatchArgs("doc.txt", pre)));
            stream.Append(new ToolCallPrepared(callId, PatchArgs("doc.txt", pre)));
            stream.Append(new PermissionEvaluated(callId, PermissionDecision.Allow, "{}", null));
            stream.Append(new ToolCallAuthorized(callId));
            var meta = FilesystemReconciliationMetadata.Encode("doc.txt", pre, VersionOf("linea-uno\nlinea-dos-C\n"));
            stream.Append(new ToolCallStarted(callId, EffectClass.NonIdempotent, meta), DurabilityClass.Barrier);
            File.WriteAllText(stateFile, sessionId.ToString() + "\n" + runId.ToString());
            return new Setup(storePath, stateFile, sessionId);
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
            if (evt.Type.ToString() == type) n += 1;
        }

        return n;
    }

    private static void AssertBlocked(OmniServer server, SqliteEventStore store, SessionId sessionId)
    {
        Assert.True(server.LastRecoveryProblem() is not null,
            "la recuperación con identidad no verificada debe ser VISIBLE (bloqueada)");
        Assert.Contains("blocked", server.Query("state", CancellationToken.None)!.Json);
        // Sin reconciliar y sin doble efecto: nunca effect_unknown/reconciled/succeeded.
        Assert.Equal(0, CountEvents(store, sessionId, "toolcall.effect_unknown"));
        Assert.Equal(0, CountEvents(store, sessionId, "toolcall.reconciled"));
        Assert.Equal(0, CountEvents(store, sessionId, "toolcall.succeeded"));
    }

    /// <summary>
    /// Case 1 — evento sin identidad durable (legacy / fallo al establecer): la ruta EXISTE
    /// pero la identidad no está registrada. Verificación falla cerrado → bloqueada. Es la
    /// limitación contratual documentada: sin identidad durable en el contrato, no se puede
    /// probar que la raíz no fue sustituida, así que no se reconcilia.
    /// </summary>
    [Fact]
    public void Recovery_blocks_when_no_durable_identity_in_event_even_if_root_exists()
    {
        var root = TempRoot() + "\\ws";
        Directory.CreateDirectory(root);
        var established = WorkspaceRootIdentity.Establish(root);
        Assert.False(string.IsNullOrEmpty(established), "el marcador se escribió (premisa del test)");
        var setup = WriteInterruptedPatch(root, ""); // identidad vacía en el evento
        try
        {
            var store = new SqliteEventStore(setup.StorePath);
            var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink(), setup.StateFile);
            AssertBlocked(server, store, setup.SessionId);
            Assert.Contains("raíz", server.LastRecoveryProblem()!);
            store.Close();
        }
        finally
        {
            RmDir(Path.GetDirectoryName(setup.StorePath)!);
        }
    }

    /// <summary>
    /// Case 2 — determinista cross-platform: la raíz física se sustituye por OTRO árbol (sin
    /// el marcador legítimo). Es el sustituto de la auditoría para el caso sin privilegios.
    /// </summary>
    [Fact]
    public void Recovery_blocks_when_root_swapped_for_another_tree_before_reopen()
    {
        var tmp = TempRoot();
        var ws = tmp + "\\ws";
        Directory.CreateDirectory(ws);
        var token = WorkspaceRootIdentity.Establish(ws);
        var setup = WriteInterruptedPatch(ws, token);
        try
        {
            // Sustituir el árbol: renombrar el legítimo y crear otro en la misma ruta sin marcador.
            Directory.Move(ws, tmp + "\\ws-moved");
            Directory.CreateDirectory(ws);
            File.WriteAllText(ws + "\\doc.txt", "linea-uno\nlinea-dos\n"); // mismo contenido

            var store = new SqliteEventStore(setup.StorePath);
            var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink(), setup.StateFile);
            AssertBlocked(server, store, setup.SessionId);
            Assert.Contains("raíz", server.LastRecoveryProblem()!);
            store.Close();
        }
        finally
        {
            RmDir(Path.GetDirectoryName(setup.StorePath)!);
        }
    }

    /// <summary>
    /// Case 3 — sustitución por symlink/junction (auditoría exacta): la raíz legítima se
    /// reemplaza por un ENLACE que apunta a otro árbol con su PROPIO marcador distinto. El
    /// enlace hace que Directory.Exists siga devolviendo true pero la identidad no coincide →
    /// bloqueada. Windows usa junction (sin admin); POSIX usa symlink. Si la plataforma no
    /// puede crear el enlace (sin developer mode / herramienta), el caso se omite honestamente:
    /// no se afirma nada sobre enlaces, y la lógica de mismatch ya queda cubierta por Case 2.
    /// </summary>
    [Fact]
    public void Recovery_blocks_when_root_replaced_by_symlink_or_junction_to_other_tree()
    {
        var tmp = TempRoot();
        try
        {
            var fake = tmp + "\\fake-tree";
            Directory.CreateDirectory(fake);
            var fakeToken = WorkspaceRootIdentity.Establish(fake);

            var linkPath = tmp + "\\ws-link";
            if (!TryCreateDirectoryLink(linkPath, fake))
            {
                // Sin enlace disponible en este entorno: caso omitido (no afirmamos nada real).
                return;
            }

            // Identidad LEGÍTIMA registrada en el evento, garantizada DIFERENTE del marcador
            // del árbol sustituto (ese árbol NO la posee) → verificación debe fallar.
            string legitToken;
            do { legitToken = Guid.NewGuid().ToString("N"); } while (legitToken == fakeToken);

            // La raíz canónica registrada ES el enlace; doc.txt se crea (vía el enlace) en el
            // árbol sustituto. Reabrir verá Directory.Exists==true pero identidad != legítima.
            var setup = WriteInterruptedPatch(linkPath, legitToken);
            var store = new SqliteEventStore(setup.StorePath);
            try
            {
                var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink(),
                    setup.StateFile);
                AssertBlocked(server, store, setup.SessionId);
                Assert.Contains("raíz", server.LastRecoveryProblem()!);
            }
            finally
            {
                store.Close();
                RmDir(Path.GetDirectoryName(setup.StorePath)!);
            }
        }
        finally
        {
            RmDir(tmp);
        }
    }

    /// <summary>Crea un enlace de directorio (junction en Windows sin admin, symlink en POSIX).</summary>
    private static bool TryCreateDirectoryLink(string linkPath, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("mklink");
                psi.ArgumentList.Add("/J");
                psi.ArgumentList.Add(linkPath);
                psi.ArgumentList.Add(target);
                using var p = System.Diagnostics.Process.Start(psi);
                p!.WaitForExit(15000);
                return p.ExitCode == 0 && Directory.Exists(linkPath);
            }

            System.IO.Directory.CreateSymbolicLink(linkPath, target);
            return Directory.Exists(linkPath);
        }
        catch (Exception)
        {
            return false;
        }
    }
}