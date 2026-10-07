using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;

namespace OmniCore.Tests;

/// <summary>
/// Regression (M4 checkpoint continuation): un <see cref="ContextCheckpointRecorded"/> persistido
/// por la compactación REAL de un ExplorerTurn sobrevive al cierre del journal SQLite y a su
/// reapertura por una segunda instancia del store (lo que hace el Host al arrancar), y llega al
/// ModelRequest de un ExplorerTurn recién construido por la vía de restauración de producción:
/// ReadLatestCheckpoint (journal reabierto + artifact content-addressed) → ContextCheckpointContributor
/// → ContextMaterializer → RenderContext. El WorkingState de continuación observado en la petición
/// es FRESCO: se regenera cada Turn a partir del argumento plan de Ask (ADR-0016 §7) — este test
/// NO demuestra replay del plan canónico ni que el WorkingState del turno anterior se persistiera.
/// La petición se captura con un provider scripted; el checkpoint esperado no se inyecta a mano —
/// lo escribe la compactación de producción del propio Turn. El journal sigue append-only.
/// </summary>
public sealed class CheckpointContinuationRegressionTests
{
    [Fact]
    public void Checkpoint_restored_after_store_reopen_reaches_new_turn_request_with_fresh_working_state()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-continuation-" + Guid.NewGuid().ToString("N"));
        var journalPath = Path.Combine(root, "journal.db");
        Directory.CreateDirectory(root);
        SqliteEventStore? store = null;
        try
        {
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var fingerprint = new ExecutionFingerprint("scripted", "harness", "tools", "policy", "overrides", "M4");
            var selection = new ModelSelection(new ModelIdValue("scripted"), 3000, ToolMode.Direct, null);
            var tools = OmniHost.CreateExplorerTools();
            var executor = OmniHost.CreateExplorerExecutor(tools.Catalog(), Path.GetTempPath());
            var cancellation = TestContext.Current.CancellationToken;
            TestRun.Opened? openedRun = null;

            // --- Sesión original: tres turns reales. Con RecentTail=2 y CompactAfter=2, el tercer
            // Ask compacta la historia vieja y escribe exactamente un checkpoint durable; luego el
            // journal se cierra limpio.
            ContextCheckpointRecorded checkpointA;
            string[] payloadsA;
            long sequenceA;
            {
                store = new SqliteEventStore(journalPath);
                var artifacts = new FileArtifactStore(root);
                openedRun = TestRun.Open(store, session);
                var answers = new[] { "answer one", "answer two", "answer three" };
                var call = 0;
                var materializer = new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>());
                var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 4, GuidanceLevel.Off, 1,
                    PlanControl.RuntimeDriven, 4, new ContextManagementPolicy(4096, 1200, 2, 2, 6000));
                var turn = new ExplorerTurn((request, token) =>
                {
                    Assert.NotNull(request);
                    return EndTurn(answers[call++]);
                }, executor, tools.Catalog(), materializer, fingerprint, selection, store, codecs, artifacts,
                    new InMemoryAuditSink(), new RedactionPolicy(), harness);
                var questions = new[] { "question one", "question two", "question three" };
                for (var i = 0; i < questions.Length; i++)
                {
                    var result = turn.Ask(questions[i], "continue the run", session, openedRun.RunId,
                        openedRun.RootLane, "Plan rev.3; P" + (i + 1) + " in progress", cancellation);
                    Assert.Equal(StopReason.EndTurn, result.StopReason);
                }

                var persistedA = store.ReadFrom(session, 1).ToArray();
                payloadsA = persistedA.Select(e => e.PayloadJson).ToArray();
                sequenceA = store.CurrentSequence(session);
                checkpointA = Assert.IsType<ContextCheckpointRecorded>(codecs.Decode(Assert.Single(persistedA,
                    e => e.Type.Equals(EventType.Of("context.checkpoint_recorded")))));
                Assert.True(checkpointA.ThroughEventSequence > 0);
                Assert.InRange(checkpointA.ThroughEventSequence, 1, sequenceA);
                store.Close();
                store = null;
            }

            // --- Reapertura: segundas instancias de store y artifacts sobre el MISMO directorio y
            // un ExplorerTurn NUEVO cuyo provider scripted captura la petición del modelo.
            ModelRequest? restoredRequest = null;
            {
                var artifacts = new FileArtifactStore(root);
                store = new SqliteEventStore(journalPath);
                Assert.Equal(payloadsA, store.ReadFrom(session, 1).Select(e => e.PayloadJson));
                Assert.Equal(sequenceA, store.CurrentSequence(session));

                var restoredCheckpoint = Assert.IsType<ContextCheckpointRecorded>(codecs.Decode(
                    Assert.Single(store.ReadFrom(session, 1),
                        e => e.Type.Equals(EventType.Of("context.checkpoint_recorded")))));
                Assert.Equal(checkpointA.CheckpointId, restoredCheckpoint.CheckpointId);
                Assert.Equal(checkpointA.ThroughEventSequence, restoredCheckpoint.ThroughEventSequence);
                Assert.Equal(checkpointA.CheckpointArtifact.Hash, restoredCheckpoint.CheckpointArtifact.Hash);

                // El contenido del checkpoint es durable: la segunda instancia de artifacts lo
                // relee por hash y contiene la historia que la compactación de producción retiró.
                using var checkpointJson = System.Text.Json.JsonDocument.Parse(
                    artifacts.GetText(restoredCheckpoint.CheckpointArtifact.Hash)!);
                var checkpoint = checkpointJson.RootElement;
                Assert.Contains("answer one", checkpoint.GetProperty("summary").GetString()!,
                    StringComparison.Ordinal);
                Assert.Contains(checkpoint.GetProperty("facts").EnumerateArray(),
                    fact => fact.GetString()!.Contains("question two", StringComparison.Ordinal));
                Assert.True(checkpoint.GetProperty("compactedThroughItemIndex").GetInt32() > 0);
                Assert.Equal("deterministic-v1", checkpoint.GetProperty("metaModelFingerprint").GetString());

                var materializer = new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>());
                // Cola reciente amplia: este turn NO vuelve a compactar, así que lo que llegue a la
                // petición tiene que venir del checkpoint restaurado, no de uno recién creado.
                var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 4, GuidanceLevel.Off, 1,
                    PlanControl.RuntimeDriven, 4, new ContextManagementPolicy(4096, 1200, 6, 2, 6000));
                var turn = new ExplorerTurn((request, token) =>
                {
                    restoredRequest = request;
                    return EndTurn("answer four");
                }, executor, tools.Catalog(), materializer, fingerprint, selection, store, codecs, artifacts,
                    new InMemoryAuditSink(), new RedactionPolicy(), harness);

                var continued = turn.Ask("question four", "continue the run", session, openedRun!.RunId,
                    openedRun.RootLane, "Plan rev.9; continuation validated", cancellation);
                Assert.Equal(StopReason.EndTurn, continued.StopReason);

                Assert.NotNull(restoredRequest);
                var instructions = restoredRequest!.Instructions!;
                // El checkpoint restaurado —mismo ThroughEventSequence que el persistido antes del
                // cierre— llega al request del Turn recién construido.
                Assert.Contains("Context checkpoint through event " + restoredCheckpoint.ThroughEventSequence,
                    instructions, StringComparison.Ordinal);
                Assert.Contains("answer one", instructions, StringComparison.Ordinal);
                Assert.Contains("question two", instructions, StringComparison.Ordinal);
                // El WorkingState de continuación llega al request y es FRESCO: se regenera cada
                // Turn a partir del argumento plan de ESTE Ask (ADR-0016 §7: nunca se compacta ni
                // se restaura del checkpoint).
                Assert.Contains("Plan rev.9; continuation validated", instructions, StringComparison.Ordinal);
                // El WorkingState del turno ANTERIOR ("Plan rev.3; P3 in progress") NO llega: este
                // test NO prueba persistencia del WorkingState previo ni replay del plan canónico.
                Assert.DoesNotContain("Plan rev.3", instructions, StringComparison.Ordinal);

                // Vía checkpoint, no replay crudo de mensajes: esos turns fueron compactados FUERA
                // de la conversación, así que no pueden llegar como mensajes crudos de la historia.
                var messageText = string.Join("\n", restoredRequest.Messages.SelectMany(m => m.Content)
                    .OfType<TextBlock>().Select(block => block.Text));
                Assert.Contains("question four", messageText, StringComparison.Ordinal);
                Assert.DoesNotContain("answer one", messageText, StringComparison.Ordinal);
                Assert.DoesNotContain("question two", messageText, StringComparison.Ordinal);

                // El journal sigue append-only tras la reapertura: prefijo intacto, solo creció, y
                // no se registró ningún checkpoint nuevo (el del request es el restaurado).
                var final = store.ReadFrom(session, 1).Select(e => e.PayloadJson).ToArray();
                Assert.Equal(payloadsA, final.Take(payloadsA.Length));
                Assert.True(final.Length > payloadsA.Length);
                Assert.Single(store.ReadFrom(session, 1),
                    e => e.Type.Equals(EventType.Of("context.checkpoint_recorded")));
                Assert.True(store.CurrentSequence(session) > sequenceA);
                store.Close();
                store = null;
            }
        }
        finally
        {
            store?.Close();
            // Close() devuelve la conexión al pool de Microsoft.Data.Sqlite: sin liberar el pool de
            // ESTE journal, Windows retiene el handle y no se puede borrar el directorio temporal.
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(
                new Microsoft.Data.Sqlite.SqliteConnection("DataSource=" + journalPath));
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // CARACTERIZACIÓN DEL COMPORTAMIENTO ACTUAL — no política deseada, no cierre.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// CARACTERIZACIÓN del comportamiento ACTUAL del código: NO es una política de recuperación
    /// deseada, NO es un cierre de M4 y NO conlleva ningún cambio de producción. Un
    /// <see cref="ContextCheckpointRecorded"/> persistido en el journal cuyo artifact es JSON
    /// sintácticamente VÁLIDO pero SIN la propiedad requerida "compactedThroughItemIndex" hace que
    /// la preparación del contexto restaurado lance internamente
    /// <see cref="KeyNotFoundException"/> (ReadLatestCheckpoint solo captura
    /// <see cref="System.Text.Json.JsonException"/>, mientras que <c>JsonElement.GetProperty</c>
    /// lanza <see cref="KeyNotFoundException"/> cuando la propiedad falta). Esa excepción NO
    /// escapa del <c>Ask</c>: el catch exterior de Ask la convierte en un
    /// <see cref="ExplorerTurn.TurnResult"/> con <see cref="StopReason.Error"/> (y texto null). El provider
    /// NUNCA se invoca — el fallo ocurre en la preparación, antes de la llamada al modelo. Este
    /// test congela ese comportamiento presente — sin endosarlo — para que un cambio futuro hacia
    /// una política de recuperación (p. ej. tratar el artifact como checkpoint ausente) deba
    /// actualizar esta caracterización de forma deliberada.
    /// </summary>
    [Fact]
    public void Characterization_current_behavior_checkpoint_artifact_missing_compacted_item_index_becomes_Ask_error_result_before_provider()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-checkpoint-characterization-"
            + Guid.NewGuid().ToString("N"));
        var journalPath = Path.Combine(root, "journal.db");
        Directory.CreateDirectory(root);
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journalPath);
            var artifacts = new FileArtifactStore(root);
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var openedRun = TestRun.Open(store, session);
            var stream = new EventStream(store, codecs, session);

            // JSON sintácticamente válido (JsonDocument.Parse lo acepta limpio) pero SIN la
            // propiedad requerida "compactedThroughItemIndex": el fallo es de campo ausente, no de
            // sintaxis, así que el catch de JsonException en ReadLatestCheckpoint no lo cubre.
            var artifact = artifacts.PutText(
                "{\"summary\":\"caracterización: checkpoint sin el índice de compactación\",\"facts\":[\"hecho uno\"]}",
                "application/vnd.omnicore.context-checkpoint+json", ArtifactKind.ContextSnapshot,
                Sensitivity.Sensitive);
            stream.Append(new ContextCheckpointRecorded("characterization-checkpoint", openedRun.RunId,
                store.CurrentSequence(session), artifact, "deterministic-v1"));

            var tools = OmniHost.CreateExplorerTools();
            var executor = OmniHost.CreateExplorerExecutor(tools.Catalog(), Path.GetTempPath());
            var fingerprint = new ExecutionFingerprint("scripted", "harness", "tools", "policy", "overrides", "M4");
            var selection = new ModelSelection(new ModelIdValue("scripted"), 3000, ToolMode.Direct, null);
            var materializer = new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>());
            var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 4, GuidanceLevel.Off, 1,
                PlanControl.RuntimeDriven, 4, new ContextManagementPolicy(4096, 1200, 6, 2, 6000));
            var providerInvoked = false;
            var turn = new ExplorerTurn((request, token) =>
            {
                providerInvoked = true;
                return EndTurn("unreachable");
            }, executor, tools.Catalog(), materializer, fingerprint, selection, store, codecs, artifacts,
                new InMemoryAuditSink(), new RedactionPolicy(), harness);

            // Comportamiento actual (no deseado): la KeyNotFoundException interna al preparar el
            // contexto restaurado del checkpoint corrupto-por-campo NO escapa de Ask — el catch
            // exterior la convierte en un TurnResult con StopReason.Error y texto null — y el
            // provider scripted NUNCA se invoca.
            var result = turn.Ask("question one", "continue the run", session, openedRun.RunId,
                openedRun.RootLane, "Plan rev.1; characterization", TestContext.Current.CancellationToken);
            Assert.Equal(StopReason.Error, result.StopReason);
            Assert.Null(result.FinalText);
            Assert.False(providerInvoked);
        }
        finally
        {
            store?.Close();
            // Cleanup aislado de este test: mismo patrón de pool que arriba, sobre SU journal.
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(
                new Microsoft.Data.Sqlite.SqliteConnection("DataSource=" + journalPath));
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static ModelResponse EndTurn(string text) => new(new ContentBlock[] { new TextBlock(text) },
        StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
        new ProviderMetadata("scripted", "continuation-test", null));
}
