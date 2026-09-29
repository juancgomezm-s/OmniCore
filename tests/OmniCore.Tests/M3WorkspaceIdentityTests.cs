using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// Tests adversariales de la IDENTIDAD DURADERA del workspace (ADR-0004 §5bis): una raíz
/// sustituida por symlink/junction o sin identidad registrada nunca debe pasar la verificación y
/// reconciliar contra el árbol equivocado. La recuperación FALLA CERRADO: bloqueada, visible, sin
/// re-ejecutar ni clasificar nada. La identidad es la ruta física resuelta de la raíz y nunca se
/// escribe ni se lee nada dentro del workspace.
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
        Assert.False(string.IsNullOrEmpty(established), "la raíz tiene identidad (premisa del test)");
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

    [Fact]
    public void Establishing_identity_never_touches_the_workspace()
    {
        // Un repo puede traer .omnicore/workspace-id versionado (incluso como enlace a secretos):
        // la identidad no lo lee, no escribe nada dentro del workspace y no depende de él.
        var root = TempRoot() + "\\ws";
        Directory.CreateDirectory(Path.Combine(root, ".omnicore"));
        File.WriteAllText(Path.Combine(root, ".omnicore", "workspace-id"), "AWS_SECRET=supersecreto");

        var identity = WorkspaceRootIdentity.Establish(root);

        Assert.DoesNotContain("supersecreto", identity);
        Assert.Equal(new[] { "workspace-id" },
            Directory.EnumerateFileSystemEntries(Path.Combine(root, ".omnicore")).Select(Path.GetFileName).ToArray());
        Assert.Single(Directory.EnumerateFileSystemEntries(root));
        Assert.True(WorkspaceRootIdentity.Verify(root, identity));
        Assert.False(WorkspaceRootIdentity.Verify(root, "0123456789abcdef0123456789abcdef"),
            "una identidad del formato anterior (token de marcador) falla cerrado");
        RmDir(Path.GetDirectoryName(root)!);
    }

    /// <summary>
    /// Case 3 — sustitución por symlink/junction (caso de la auditoría): la identidad se establece
    /// sobre el árbol legítimo y, antes de reabrir, esa ruta se reemplaza por un ENLACE a otro árbol
    /// con el mismo contenido. Directory.Exists sigue siendo true, pero la ruta física ya no
    /// coincide → bloqueada. Windows usa junction (sin admin); POSIX, symlink.
    /// </summary>
    [Fact]
    public void Recovery_blocks_when_root_replaced_by_symlink_or_junction_to_other_tree()
    {
        var tmp = TempRoot();
        var ws = tmp + "\\ws";
        Directory.CreateDirectory(ws);
        var identity = WorkspaceRootIdentity.Establish(ws);
        var setup = WriteInterruptedPatch(ws, identity);
        try
        {
            var fake = tmp + "\\fake-tree";
            Directory.CreateDirectory(fake);
            File.WriteAllText(fake + "\\doc.txt", "linea-uno\nlinea-dos\n"); // mismo contenido
            Directory.Move(ws, tmp + "\\ws-moved");
            if (!TryCreateDirectoryLink(ws, fake))
            {
                Assert.Skip("El entorno no permite crear enlaces de directorio.");
            }

            Assert.False(WorkspaceRootIdentity.Verify(ws, identity), "la ruta resuelve a otro árbol");
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
            }
        }
        finally
        {
            RmDir(Path.GetDirectoryName(setup.StorePath)!);
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