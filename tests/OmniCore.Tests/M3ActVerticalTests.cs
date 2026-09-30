using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// E2E de la vertical M3 <c>act</c> (ADR-0035 §3, ADR-0044 §5): se crea un Run Act REAL por
/// invocación (comando <c>act</c> del servidor, NO sim), se ofrece filesystem.read/filesystem.patch
/// bajo la política efectiva y los permisos existentes, el Turn de Explorer se ejecuta con un modelo
/// FAKE determinista, y turn/toolcall/outcome quedan PERSISTIDOS en un journal SQLite que se RECABRE
/// (otro OmniServer sobre el mismo archivo) para verificar la reapertura y la integridad de la
/// identidad durable del workspace. Se cubre:
///  - modelo no clasificado (ObserveOnly) ⇒ denegación de escritura: el patch se rechaza y el archivo
///    queda intacto, aunque el modelo haya leído antes;
///  - PatchOnly ⇒ patch con lectura previa y token vigente ⇒ éxito, persistido como succeeded;
///  - PatchOnly ⇒ un reemplazo (fake.write clasificado ReplaceFile) se RECHAZA;
///  - la reapertura conserva los eventos y la recuperación no se bloquea (identidad verificada).
/// No se usa sim como sustituto y NUNCA se autoaprueba Ask (sin cliente, Ask → Deny, ADR-0003).
/// </summary>
public sealed class M3ActVerticalTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m3-act", Guid.NewGuid().ToString("N"));
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

    private static string PatchArgs(string path, string token, string oldText, string newText)
        => "{\"path\":\"" + path + "\",\"expectedVersion\":\"" + token
           + "\",\"oldText\":\"" + oldText + "\",\"newText\":\"" + newText + "\"}";

    private static EffectiveModelPolicy EffectiveFor(ModelPolicyCategory category)
    {
        var key = ModelPolicyKey.For("fake-provider", "fake-model");
        var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 8, GuidanceLevel.Full, 3,
            PlanControl.ModelDriven, 8);
        StoredModelPolicy? stored = category == ModelPolicyCategory.ObserveOnly
            ? null
            : new StoredModelPolicy(key, 1, ModelPolicyPresets.For(category),
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        return EffectiveModelPolicy.Resolve(key, stored, harness);
    }

    // ---- Modelo fake determinista ----

    private static ModelResponse End() =>
        new ModelResponse(new ContentBlock[] { new TextBlock("listo") }, StopReason.EndTurn,
            new TokenUsage(2, 2, 0, 0, 0), null, new ProviderMetadata("", "", null));

    private static ModelResponse ToolCall(string name, string argsJson) =>
        new ModelResponse(new ContentBlock[] { new ToolCallBlock(ToolCallId.New(), "call-" + name, name, argsJson) },
            StopReason.ToolUse, new TokenUsage(2, 2, 0, 0, 0), null, new ProviderMetadata("", "", null));

    private static int ToolResults(ModelRequest request)
    {
        var n = 0;
        foreach (var m in request.Messages)
        {
            foreach (var b in m.Content)
            {
                if (m.Role == MessageRole.Tool && b is ToolResultBlock) n += 1;
            }
        }

        return n;
    }

    // ---- Arranque de un Run Act real sobre journal SQLite persistente ----

    private sealed record ActServer(OmniServer Server, SqliteEventStore Store, SessionId SessionId,
        RunId RunId, LaneId LaneId, string Workspace, string DataDir);

    /// <summary>
    /// Crea un server in-process sobre un journal SQLite NUEVO, envía el comando <c>act</c> (Run Act
    /// de verdad, con raíz durable e identidad) y devuelve el server + ids + rutas. El journal queda
    /// listo para reabrirse después con otro OmniServer.
    /// </summary>
    private static ActServer StartActServer(string dataDir, string workspace, string objective)
    {
        var journal = Path.Combine(dataDir, "journal.db");
        var stateFile = Path.Combine(dataDir, "lastsession.txt");
        var store = new SqliteEventStore(journal);
        var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink(), stateFile);
        var payload = "{\"cmd\":\"act\",\"objective\":\"" + objective + "\",\"workspace\":\"" + workspace + "\"}";
        var ack = server.Send(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
        Assert.True(ack.Status == "ok", "el comando act crea el Run Act. error=" + (ack.Error ?? ""));
        return new ActServer(server, store, server.LastSessionId()!, server.LastRunId()!,
            server.LastLaneId()!, workspace, dataDir);
    }

    private static ExplorerTurn BuildTurn(ActServer cx, EffectiveModelPolicy effective, FakeCatalog catalog,
        Func<ModelRequest, CancellationToken, ModelResponse> complete)
    {
        var boundary = new ModelCapabilityBoundary(effective);
        // El MISMO executor que el CLI `act` (OmniHost.CreateActExecutor): permisos por modo Act
        // con la política efectiva como frontera. Sin cliente interactivo, un Ask sería Deny (no se
        // autoaprueba); aquí no hay Ask (lectura/escritura dentro del workspace en modo Act con
        // perfil autónomo).
        var executor = OmniHost.CreateActExecutor(catalog, cx.Workspace, boundary);
        var materializer = new ContextMaterializer(new FakeTokenCounter(), new IContextContributor[0]);
        var fingerprint = new ExecutionFingerprint("fake-model", "harness-h", "core-tools-1",
            "heuristic:chars4/1", "none", "M3", effective.Fingerprint());
        var selection = new ModelSelection(new ModelIdValue("fake-model"), 8192, ToolMode.Direct, null);
        var artifacts = new FileArtifactStore(Path.Combine(cx.Workspace, ".omnicore-art"));
        var turn = new ExplorerTurn(complete, executor, catalog, materializer, fingerprint, selection,
            cx.Server.AcquireStore(), cx.Server.AcquireCodecs(), artifacts, new InMemoryAuditSink(),
            new RedactionPolicy(), boundary: boundary);
        return turn;
    }

    private static IReadOnlyList<DomainEvent> ReopenEvents(ActServer cx)
    {
        // Cerrar la sesión original y reabrir OTRO OmniServer sobre el MISMO journal: la reapertura
        // dispara la recuperación (verifica la identidad durable del workspace) y conserva los eventos.
        cx.Store.Close();
        var store2 = new SqliteEventStore(Path.Combine(cx.DataDir, "journal.db"));
        var server2 = new OmniServer(store2, EventCodecs.Create(), new InMemoryAuditSink(),
            Path.Combine(cx.DataDir, "lastsession.txt"));
        Assert.True(server2.LastRecoveryProblem() is null,
            "la reapertura verifica la identidad durable y no se bloquea");
        var events = store2.ReadFrom(cx.SessionId, 1);
        store2.Close();
        server2.DisposeIfPossible();
        return events;
    }

    /// <summary>Denegación de escritura (ObserveOnly): el patch se rechaza y el archivo queda intacto.</summary>
    [Fact]
    public void Act_observe_only_denies_write_even_after_read_and_reopens_persisting_rejection()
    {
        var dataDir = TempDir();
        var ws = Path.Combine(dataDir, "workspace");
        Directory.CreateDirectory(ws);
        var original = "linea-uno\nlinea-dos\n";
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        var cx = StartActServer(dataDir, ws, "corrige este test");
        try
        {
            var catalog = OmniHost.CreateActTools().Catalog();
            var effective = EffectiveFor(ModelPolicyCategory.ObserveOnly);
            var turn = BuildTurn(cx, effective, catalog, (req, tok) =>
            {
                var n = ToolResults(req);
                if (n == 0) return ToolCall("filesystem.read", "{\"path\":\"doc.txt\"}");
                if (n == 1) return ToolCall("filesystem.patch",
                    PatchArgs("doc.txt", VersionOf(original), "linea-dos", "linea-dos-X"));
                return End();
            });

            var result = turn.Ask("corrige este test", "sys {context}", cx.SessionId, cx.RunId,
                cx.LaneId, "", CancellationToken.None);

            // El modelo leyó (ok) y luego intentó patch: ObserveOnly lo deniega, archivo intacto.
            Assert.True(result.ToolCalls.Count >= 2, "read + patch intentado");
            Assert.True(result.ToolCalls[0].Succeeded, "filesystem.read ok");
            Assert.False(result.ToolCalls[1].Succeeded, "ObserveOnly deniega la escritura. summary=" + result.ToolCalls[1].Summary);
            Assert.Equal(original, File.ReadAllText(Path.Combine(ws, "doc.txt")));
            Assert.DoesNotContain(result.ToolCalls, t => t.Succeeded && t.ToolName == "filesystem.patch");

            // Persistido: la LECTURA ok (read) + el patch RECHAZADO sobre el mismo journal; el
            // turno terminó. El succeeded observado es de filesystem.read; el patch nunca succeeded.
            var types = cx.Store.ReadFrom(cx.SessionId, 1).Select(e => e.Type.ToString()).ToArray();
            Assert.Contains("toolcall.requested", types);
            Assert.Contains("toolcall.rejected", types);
            Assert.Contains("toolcall.succeeded", types); // la lectura previa sí ok
            Assert.Contains("turn.completed", types);
            Assert.True(result.ToolCalls[0].Succeeded, "read succeeded");
            Assert.False(result.ToolCalls[1].Succeeded, "patch rejected");

            // Reapertura: la denegación del patch sobrevive en el journal y no se bloquea la identidad.
            var reopened = ReopenEvents(cx).Select(e => e.Type.ToString()).ToArray();
            Assert.Contains("toolcall.rejected", reopened);
            Assert.Contains("toolcall.succeeded", reopened); // la lectura previa
            Assert.Equal(original, File.ReadAllText(Path.Combine(ws, "doc.txt")));
        }
        finally
        {
            RmDir(dataDir);
        }
    }

    /// <summary>PatchOnly: patch válido con lectura previa + token vigente ⇒ éxito, persistido y reabierto.</summary>
    [Fact]
    public void Act_patch_only_applies_patch_with_prior_read_and_reopens_persisting_success()
    {
        var dataDir = TempDir();
        var ws = Path.Combine(dataDir, "workspace");
        Directory.CreateDirectory(ws);
        var original = "linea-uno\nlinea-dos\n";
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        var token = VersionOf(original);
        var cx = StartActServer(dataDir, ws, "corrige este test");
        try
        {
            var catalog = OmniHost.CreateActTools().Catalog();
            var effective = EffectiveFor(ModelPolicyCategory.PatchOnly);
            var turn = BuildTurn(cx, effective, catalog, (req, tok) =>
            {
                var n = ToolResults(req);
                if (n == 0) return ToolCall("filesystem.read", "{\"path\":\"doc.txt\"}");
                if (n == 1) return ToolCall("filesystem.patch",
                    PatchArgs("doc.txt", token, "linea-dos", "linea-dos-C"));
                return End();
            });

            var result = turn.Ask("corrige este test", "sys {context}", cx.SessionId, cx.RunId,
                cx.LaneId, "", CancellationToken.None);

            Assert.True(result.ToolCalls.Count >= 2, "read + patch (" + result.ToolCalls.Count + ")");
            Assert.True(result.ToolCalls[0].Succeeded, "filesystem.read ok");
            Assert.True(result.ToolCalls[1].Succeeded, "filesystem.patch ok. summary=" + result.ToolCalls[1].Summary);
            Assert.Equal("linea-uno\nlinea-dos-C\n", File.ReadAllText(Path.Combine(ws, "doc.txt")));

            var types = cx.Store.ReadFrom(cx.SessionId, 1).Select(e => e.Type.ToString()).ToArray();
            Assert.Contains("toolcall.succeeded", types);
            Assert.Contains("turn.completed", types);

            var reopened = ReopenEvents(cx).Select(e => e.Type.ToString()).ToArray();
            Assert.Contains("toolcall.succeeded", reopened);
            Assert.Equal("linea-uno\nlinea-dos-C\n", File.ReadAllText(Path.Combine(ws, "doc.txt")));
        }
        finally
        {
            RmDir(dataDir);
        }
    }

    /// <summary>PatchOnly no puede reemplazar: fake.write (ReplaceFile) se rechaza en el runtime.</summary>
    [Fact]
    public void Act_patch_only_rejects_replace_and_reopens_persisting_rejection()
    {
        var dataDir = TempDir();
        var ws = Path.Combine(dataDir, "workspace");
        Directory.CreateDirectory(ws);
        File.WriteAllText(Path.Combine(ws, "doc.txt"), "linea-uno\nlinea-dos\n");
        var cx = StartActServer(dataDir, ws, "corrige este test");
        try
        {
            // Catálogo de action + una tool de REEMPLAZO (fake.write, clasificada ReplaceFile en la
            // frontera): PatchOnly debe rechazarla aunque los permisos la permitirían.
            var catalog = OmniHost.CreateActTools().Catalog().Add(new FakeTool("fake.write", "sim write",
                EffectClass.Reconcilable, false));
            var effective = EffectiveFor(ModelPolicyCategory.PatchOnly);
            var turn = BuildTurn(cx, effective, catalog, (req, tok) =>
                ToolResults(req) == 0
                    ? ToolCall("fake.write", "{\"path\":\"doc.txt\"}")
                    : End());

            var result = turn.Ask("reemplaza el archivo", "sys {context}", cx.SessionId, cx.RunId,
                cx.LaneId, "", CancellationToken.None);

            Assert.True(result.ToolCalls.Count >= 1, "el reemplazo se intentó");
            Assert.False(result.ToolCalls[0].Succeeded,
                "PatchOnly no permite el reemplazo completo. summary=" + result.ToolCalls[0].Summary);

            var types = cx.Store.ReadFrom(cx.SessionId, 1).Select(e => e.Type.ToString()).ToArray();
            Assert.Contains("toolcall.requested", types);
            Assert.Contains("toolcall.rejected", types);
            Assert.DoesNotContain("toolcall.succeeded", types);

            var reopened = ReopenEvents(cx).Select(e => e.Type.ToString()).ToArray();
            Assert.Contains("toolcall.rejected", reopened);
            Assert.DoesNotContain("toolcall.succeeded", reopened);
        }
        finally
        {
            RmDir(dataDir);
        }
    }
}

/// <summary>Extensión de test para liberar recursos del server (best-effort).</summary>
internal static class ActServerDispose
{
    public static void DisposeIfPossible(this object server)
    {
        // OmniServer no expone Close; el store se cierra explícitamente en el caller.
        _ = server;
    }
}