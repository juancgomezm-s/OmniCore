using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// Pruebas deterministas del M3 P0: en el pipeline real del Explorer (ScriptedToolExecutor →
/// ToolRuntime → ExplorerTurn) el <c>ToolCallStarted</c> de un intent con efecto se persiste con
/// <c>DurabilityClass.Barrier</c> ANTES de que la tool ejecute (ADR-0002 §2, ADR-0004 §2). Verifica
/// el orden con un spy de <c>IEventStore</c>, que el replay conserva una sola cadena válida, y que
/// un fallo entre el Barrier y la ejecución produce <c>ToolCallEffectUnknown</c> (reconciliación
/// conservadora) sin duplicar el efecto.
/// </summary>
public sealed class M3BarrierOrderingTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m3-barrier", Guid.NewGuid().ToString("N"));
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

    private static string PatchArgs(string relPath, string expectedVersion, string oldText, string newText)
    {
        return "{\"path\":\"" + relPath + "\",\"expectedVersion\":\"" + expectedVersion +
            "\",\"oldText\":" + JsonEscape(oldText) + ",\"newText\":" + JsonEscape(newText) + "}";
    }

    private static string JsonEscape(string s)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c.ToString(),
            });
        }

        return sb.Append('"').ToString();
    }

    private static ModelResponse End() =>
        new ModelResponse(new ContentBlock[] { new TextBlock("ok") }, StopReason.EndTurn,
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
                if (b is ToolResultBlock) n++;
            }
        }

        return n;
    }

    private static string[] ToolCallChain(SpyingEventStore spy)
    {
        var kinds = new List<string>();
        foreach (var w in spy.Writes)
        {
            if (w.Type.StartsWith("toolcall.")) kinds.Add(w.Type);
        }

        return kinds.ToArray();
    }

    /// <summary>
    /// Spy de <c>IEventStore</c>: registra, en orden, cada (tipo, durabilidad) escrito y, en el
    /// instante en que llega un <c>toolcall.started</c> con Barrier, captura si el archivo objetivo
    /// seguía intacto (debe estarlo: todavía no se ejecutó la tool). Sin estado compartido entre
    /// Runs: el espía es por-test.
    /// </summary>
    private sealed class SpyingEventStore : IEventStore
    {
        public sealed record Write(string Type, DurabilityClass Durability, long Sequence);

        public readonly List<Write> Writes = new();

        private readonly List<DomainEvent> _stored = new();

        public int StartedBarrierCalls;
        public bool FileIntactAtStartedBarrier = true;
        public string TargetFile = "";

        public string OriginalContent = "";

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
                    // Nunca debe haber un Started Standard para un intent con efecto.
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

    /// <summary>
    /// Tool fake que DECLARA un efecto (Reconcilable) pero lanza en ExecuteAsync: simula el fallo
    /// entre el commit Barrier (Started ya persistido) y el efecto real. El ToolRuntime lo traduce
    /// a <c>ToolCallEffectUnknown</c> (ADR-0004 §2) de forma conservadora.
    /// </summary>
    private sealed class ThrowingEffectTool : ITool
    {
        private readonly ToolDescriptor _descriptor;

        public ThrowingEffectTool()
        {
            _descriptor = new ToolDescriptor(
                new ToolId("boom.write"),
                "Escritura que falla tras el Started (test)",
                new InputSchema("{}"),
                new string[0],
                false,
                false,
                ToolRisk.Low,
                ComponentSource.Core(),
                ToolProtection.None);
        }

        public ToolDescriptor Descriptor => _descriptor;

        public ToolPreparation Prepare(ValidatedToolCall call, ToolPreparationContext context)
        {
            var reconciliation = new ReconciliationSpec("pre", "post", call.ToolCallId.ToString());
            var intent = new ToolIntent(call.ToolCallId, call.ToolId, call.NormalizedArgumentsJson,
                EffectClass.Reconcilable, ResourceClaims.Empty(), ToolRisk.Low, reconciliation);
            return new Prepared(intent);
        }

        public Task<ToolResult> ExecuteAsync(AuthorizedToolIntent intent, ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("crash simulado entre Barrier y efecto");
        }
    }

    [Fact]
    public void Started_barrier_is_written_before_filesystem_patch_changes_the_file()
    {
        var ws = TempDir();
        var original = "linea-uno\nlinea-dos\n";
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(), includeMutationTools: true);
        var executor = ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(),
            ScriptedPermissionPolicy.WithTool("filesystem.patch", PermissionDecision.Allow), ws);
        var spy = new SpyingEventStore();
        spy.TargetFile = Path.Combine(ws, "doc.txt");
        spy.OriginalContent = original;
        var sessionId = SessionId.New();
        var turn = MakeTurn(executor, hostTools.Catalog(), spy, ws, (request, token) =>
        {
            if (ToolResults(request) == 0)
            {
                return ToolCall("filesystem.patch", PatchArgs("doc.txt", VersionOf(original),
                    "linea-dos", "linea-dos-C"));
            }

            return End();
        });

        turn.Ask("parchea el archivo", "sys", sessionId, TestRun.OpenRun(spy, sessionId), "ws", CancellationToken.None);

        try
        {
            Assert.Equal(1, spy.StartedBarrierCalls);
            Assert.True(spy.FileIntactAtStartedBarrier,
                "el archivo debía seguir intacto cuando se escribió el Started con Barrier");
            Assert.True(File.ReadAllText(Path.Combine(ws, "doc.txt")) == "linea-uno\nlinea-dos-C\n",
                "el efecto debe aplicarse tras el Barrier");
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Replay_contains_a_single_valid_toolcall_chain_in_order()
    {
        var ws = TempDir();
        var original = "linea-uno\nlinea-dos\n";
        File.WriteAllText(Path.Combine(ws, "doc.txt"), original);
        var hostTools = new HostTools(new PathBoundaryValidator(), new PlanService(), includeMutationTools: true);
        var executor = ScriptedToolExecutor.WithWorkspace(hostTools.Catalog(),
            ScriptedPermissionPolicy.WithTool("filesystem.patch", PermissionDecision.Allow), ws);
        var spy = new SpyingEventStore();
        spy.TargetFile = Path.Combine(ws, "doc.txt");
        spy.OriginalContent = original;
        var sessionId = SessionId.New();
        var turn = MakeTurn(executor, hostTools.Catalog(), spy, ws, (request, token) =>
        {
            if (ToolResults(request) == 0)
            {
                return ToolCall("filesystem.patch", PatchArgs("doc.txt", VersionOf(original),
                    "linea-dos", "linea-dos-C"));
            }

            return End();
        });

        turn.Ask("parchea el archivo", "sys", sessionId, TestRun.OpenRun(spy, sessionId), "ws", CancellationToken.None);

        try
        {
            Assert.Equal(
                new string[] { "toolcall.requested", "toolcall.prepared", "toolcall.permission_evaluated",
                    "toolcall.authorized", "toolcall.started", "toolcall.succeeded" },
                ToolCallChain(spy));
            // El Started se escribe EXACTAMENTE UNA vez, con Barrier.
            var startedCount = 0;
            foreach (var w in spy.Writes)
            {
                if (w.Type == "toolcall.started")
                {
                    startedCount += 1;
                    Assert.Equal(DurabilityClass.Barrier, w.Durability);
                }
            }

            Assert.Equal(1, startedCount);
            // El contenido final persiste el efecto.
            Assert.True(File.ReadAllText(Path.Combine(ws, "doc.txt")) == "linea-uno\nlinea-dos-C\n");
        }
        finally
        {
            RmDir(ws);
        }
    }

    [Fact]
    public void Failure_between_barrier_and_execute_yields_effect_unknown_without_duplicating_effect()
    {
        var ws = TempDir();
        var catalog = new FakeCatalog().Add(new ThrowingEffectTool());
        var executor = ScriptedToolExecutor.WithWorkspace(catalog,
            ScriptedPermissionPolicy.WithTool("boom.write", PermissionDecision.Allow), ws);
        var spy = new SpyingEventStore();
        var sessionId = SessionId.New();
        var turn = MakeTurn(executor, catalog, spy, ws, (request, token) =>
        {
            if (ToolResults(request) == 0)
            {
                return ToolCall("boom.write", "{\"path\":\"doc.txt\"}");
            }

            return End();
        });

        var result = turn.Ask("escribe", "sys", sessionId, TestRun.OpenRun(spy, sessionId), "ws", CancellationToken.None);

        try
        {
            Assert.Equal(1, spy.StartedBarrierCalls);
            Assert.True(spy.FileIntactAtStartedBarrier);
            // Started (Barrier) seguido de EffectUnknown: el runtime no sabe si el efecto ocurrió
            // (ADR-0004 §2). El Started se escribe UNA sola vez: no se duplica el efecto.
            Assert.Equal(new string[] { "toolcall.requested", "toolcall.prepared",
                "toolcall.permission_evaluated", "toolcall.authorized", "toolcall.started",
                "toolcall.effect_unknown" }, ToolCallChain(spy));
            var startedCount = 0;
            foreach (var w in spy.Writes)
            {
                if (w.Type == "toolcall.started")
                {
                    startedCount += 1;
                    Assert.Equal(DurabilityClass.Barrier, w.Durability);
                }
            }

            Assert.Equal(1, startedCount);
            Assert.True(result.ToolCalls.Count >= 1, "la tool debe intentarse");
            Assert.False(result.ToolCalls[0].Succeeded);
        }
        finally
        {
            RmDir(ws);
        }
    }

    private static ExplorerTurn MakeTurn(IToolExecutor executor, FakeCatalog catalog, IEventStore store,
        string ws, Func<ModelRequest, CancellationToken, ModelResponse> complete)
    {
        var materializer = new ContextMaterializer(new FakeTokenCounter(), new IContextContributor[0]);
        var fingerprint = new ExecutionFingerprint("m", "h", "t", "c", "o", "M3");
        var selection = new ModelSelection(new ModelIdValue("m"), 8192, ToolMode.Direct, null);
        return new ExplorerTurn(complete, executor, catalog, materializer, fingerprint, selection,
            store, EventCodecs.Create(), new FileArtifactStore(Path.Combine(ws, ".omnicore-barrier-art")),
            new InMemoryAuditSink(), new RedactionPolicy());
    }
}