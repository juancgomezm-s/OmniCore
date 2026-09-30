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
/// Regresión del M3 P0 «Barrier en ExecuteTool»: el puerto del Engine (<see cref="IToolExecutor"/>)
/// exige stream para no perder el commit Barrier (INV-014, ADR-0002 §2 / ADR-0004 §2). Fija tres
/// cosas que la corrección introdujo y no deben volver a romperse:
/// <list type="number">
/// <item><see cref="ExecuteTool_without_stream_throws"/>: la llamada sin stream ya no ejecuta
/// "en silencio" — falla rápido con <see cref="ArgumentNullException"/> (commit 08d8ad4).</item>
/// <item><see cref="Journaled_ExecuteTool_commits_started_as_barrier_before_the_effect"/>: con
/// stream, el <c>ToolCallStarted</c> de un intent con efecto se persiste con
/// <c>DurabilityClass.Barrier</c> ANTES de que la tool toque el archivo, y el outcome posterior
/// NO se persiste aquí: vuelve en <c>ToolOutcome.Events</c> para que lo persista el Engine.</item>
/// <item><see cref="ExecuteToolWithoutJournal_persists_nothing_and_returns_the_full_chain"/>:
/// el pipeline sin journal no escribe NADA y devuelve la cadena completa (incluido el
/// <c>ToolCallFailed</c> con su código tipado) en <c>Events</c>.</item>
/// </list>
/// </summary>
public sealed class M3ExecuteToolBarrierTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m3-executetool", Guid.NewGuid().ToString("N"));
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

    private static string VersionOf(string content) =>
        FilesystemPatchTool.VersionToken(System.Text.Encoding.UTF8.GetBytes(content));

    private static string EightLineFile()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i <= 8; i++) sb.Append("linea-").Append(i).Append('\n');
        return sb.ToString();
    }

    private static EffectiveModelPolicy FullAgent()
    {
        var key = ModelPolicyKey.For("p", "m");
        return EffectiveModelPolicy.Resolve(key,
            new StoredModelPolicy(key, 1, ModelPolicyPresets.For(ModelPolicyCategory.FullAgent),
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 8, GuidanceLevel.Full, 3,
                PlanControl.ModelDriven, 8));
    }

    private static ScriptedToolExecutor Pipeline(string ws, ModelCapabilityBoundary? boundary = null)
    {
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(), includeMutationTools: true);
        var policy = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
        {
            ["filesystem.read"] = PermissionDecision.Allow,
            ["filesystem.patch"] = PermissionDecision.Allow,
        });
        return ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(), policy, ws, boundary);
    }

    private static ValidatedToolCall ReadCall(string path) =>
        new(ToolCallId.New(), new ToolId("filesystem.read"), "pc-read", "{\"path\":\"" + path + "\"}");

    private static ValidatedToolCall PatchCall(string path, string expectedVersion, string oldText,
        string newText) =>
        new(ToolCallId.New(), new ToolId("filesystem.patch"), "pc-patch",
            System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["path"] = path,
                ["expectedVersion"] = expectedVersion,
                ["oldText"] = oldText,
                ["newText"] = newText,
            }));

    [Fact]
    public void ExecuteTool_without_stream_throws()
    {
        var ws = TempDir();
        try
        {
            var executor = Pipeline(ws);

            // INV-014: sin stream no puede haber commit Barrier antes del efecto; el puerto
            // exige stream y falla rápido en lugar de ejecutar en silencio (commit 08d8ad4).
            Assert.Throws<ArgumentNullException>(() =>
                executor.ExecuteTool(ReadCall("doc.txt"), true, CancellationToken.None, null!));
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Journaled_ExecuteTool_commits_started_as_barrier_before_the_effect()
    {
        var ws = TempDir();
        var original = EightLineFile();
        var doc = Path.Combine(ws, "doc.txt");
        File.WriteAllText(doc, original);
        try
        {
            var spy = new SpyingEventStore
            {
                TargetFile = doc,
                OriginalContent = original,
            };
            var stream = new EventStream(spy, EventCodecs.Create(), SessionId.New());
            var executor = Pipeline(ws, new ModelCapabilityBoundary(FullAgent()));

            // Lectura previa (sin journal: EffectClass.None, no hay efecto que proteger).
            var read = executor.ExecuteToolWithoutJournal(ReadCall("doc.txt"), true, CancellationToken.None);
            Assert.True(read.Succeeded, read.Summary ?? "read falló");

            var outcome = executor.ExecuteTool(
                PatchCall("doc.txt", VersionOf(original), "linea-2", "linea-2-C"),
                true, CancellationToken.None, stream);

            Assert.True(outcome.Succeeded, outcome.Summary ?? "patch falló");
            Assert.Contains("linea-2-C", File.ReadAllText(doc));

            // El archivo estaba INTACTO en el instante en que el Started se persistió (Barrier
            // ANTES del efecto), y solo hubo un Started: con Barrier.
            Assert.True(spy.FileIntactAtStartedBarrier, "el Started Barrier precede al efecto");
            Assert.Equal(1, spy.StartedBarrierCalls);

            // Cadena persistida aquí: predecesores (Standard, en orden) + Started (Barrier).
            // El Succeeded NO se persiste en ExecuteTool: vuelve en Events para el Engine.
            Assert.DoesNotContain(spy.Writes, w => w.Type == "toolcall.succeeded");
            var types = spy.Writes.Select(w => w.Type).ToArray();
            Assert.Equal(new[]
            {
                "toolcall.requested", "toolcall.prepared", "toolcall.permission_evaluated", "toolcall.authorized",
                "toolcall.started",
            }, types);
            var startedWrite = Assert.Single(spy.Writes, w => w.Type == "toolcall.started");
            Assert.Equal(DurabilityClass.Barrier, startedWrite.Durability);

            Assert.Contains(outcome.Events, e => e is ToolCallSucceeded);
            Assert.Equal(ToolCallState.Succeeded, outcome.FinalState);
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void ExecuteToolWithoutJournal_persists_nothing_and_returns_the_full_chain()
    {
        var ws = TempDir();
        var original = EightLineFile();
        var doc = Path.Combine(ws, "doc.txt");
        File.WriteAllText(doc, original);
        try
        {
            var spy = new SpyingEventStore();
            var executor = Pipeline(ws, new ModelCapabilityBoundary(FullAgent()));
            var read = executor.ExecuteToolWithoutJournal(ReadCall("doc.txt"), true, CancellationToken.None);
            Assert.True(read.Succeeded, read.Summary ?? "read falló");

            // Modificación EXTERNA (fuera del control del modelo): el token de esa lectura queda
            // obsoleto, así que el parche con el token de la lectura previa es STALE_WRITE.
            var external = original + "linea-extra-externa\n";
            File.WriteAllText(doc, external);

            // Un stream disponible pero NO pasado: el pipeline sin journal no debe escribir nada.
            var unused = new EventStream(spy, EventCodecs.Create(), SessionId.New());
            var outcome = executor.ExecuteToolWithoutJournal(
                PatchCall("doc.txt", VersionOf(original), "linea-2", "linea-2-C"),
                true, CancellationToken.None);

            Assert.False(outcome.Succeeded);
            Assert.Empty(spy.Writes); // cero escrituras: sin stream no hay efecto persistente aquí
            Assert.DoesNotContain(unused.WrittenPayloads, _ => true);

            // La cadena completa (hasta el fallo) vuelve en Events, con el Started del efecto y
            // el Failed con su código tipado.
            var types = outcome.Events.Select(e => e.GetType().Name).ToArray();
            Assert.Equal(new[]
            {
                nameof(ToolCallRequested), nameof(ToolCallPrepared), nameof(PermissionEvaluated),
                nameof(ToolCallAuthorized), nameof(ToolCallStarted), nameof(ToolCallFailed),
            }, types);
            var failed = Assert.Single(outcome.Events.OfType<ToolCallFailed>());
            Assert.Equal(ToolErrorCode.StaleWrite, failed.ErrorCode);
            Assert.Equal(external, File.ReadAllText(doc));
        }
        finally
        {
            RmDir(ws);
        }
    }

    /// <summary>
    /// Spy de <see cref="IEventStore"/>: en el instante del append comprueba que el archivo
    /// objetivo siga intacto (el commit Barrier del Started precede al efecto) y registra todas
    /// las escrituras con su clase de durabilidad.
    /// </summary>
    private sealed class SpyingEventStore : IEventStore
    {
        public sealed record Write(string Type, DurabilityClass Durability, long Sequence);

        public readonly List<Write> Writes = new();

        public int StartedBarrierCalls;
        public bool FileIntactAtStartedBarrier = true;
        public string TargetFile = "";
        public string OriginalContent = "";

        private readonly List<DomainEvent> _stored = new();

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
            AppendBatch(sessionId, [evt], durability, cancellationToken);
        }

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> evts, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
            foreach (var evt in evts)
            {
                var type = evt.Type.ToString();
                if (type == "toolcall.started" && durability == DurabilityClass.Barrier)
                {
                    // En este instante la tool aún NO ejecutó: el archivo debe estar intacto.
                    var intact = TargetFile.Length == 0
                        || !File.Exists(TargetFile)
                        || File.ReadAllText(TargetFile) == OriginalContent;
                    FileIntactAtStartedBarrier = FileIntactAtStartedBarrier && intact;
                    StartedBarrierCalls += 1;
                }
                else if (TargetFile.Length > 0 && type == "toolcall.started")
                {
                    Assert.Fail("el Started de un intent con efecto se persiste con Barrier, no Standard");
                }

                var seq = (long) (_stored.Count + 1);
                Writes.Add(new Write(type, durability, seq));
                _stored.Add(evt);
            }
        }

        public long CurrentSequence(SessionId sessionId) => (long) _stored.Count;

        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive)
        {
            var start = (int) Math.Max(0, fromSequenceInclusive - 1);
            if (start >= _stored.Count) return new DomainEvent[0];
            var tail = new DomainEvent[_stored.Count - start];
            for (var i = 0; i < tail.Length; i++) tail[i] = _stored[start + i];
            return tail;
        }
    }
}
