namespace OmniCore.Tests;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;

/// <summary>
/// Proposed extension to the existing partial fixture. It separates adapter continuation
/// (signed provider state) from the visible ReasoningBlock restored in ModelRequest.Messages.
/// The handler is SSE-only and local to the test; no credential or external request is used.
/// </summary>
public sealed partial class ExplorerTurnAnthropicContinuationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reopen_restores_signed_provider_state_and_visible_reasoning_block_separately(bool sensitiveSignature)
    {
        var signature = sensitiveSignature ? "registered-private-signature" : "SIG-LOCAL-FIXTURE";
        var root = Path.Combine(Path.GetTempPath(), "omni-anthropic-reasoning-reopen-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            var schema = new QuestionnaireSchema("Choose", null, new QuestionField[] {
                new("approach", "Which?", null, QuestionKind.SingleChoice,
                    new[] { new QuestionOption("safe", "Safe", null) }, null, true, null, null, null) });
            var firstStream = ToolUseTurnStream.Replace("SIG-LOCAL-FIXTURE", signature, StringComparison.Ordinal)
                .Replace("filesystem.read", "user.ask", StringComparison.Ordinal)
                .Replace("\"{\\\"path\\\":\\\"fixture.txt\\\"}\"",
                    JsonSerializer.Serialize(QuestionnaireCodec.EncodeSchema(schema)), StringComparison.Ordinal);
            using var handler = new TwoSseHandler(firstStream);
            var provider = CreateProvider(handler);
            var codecs = EventCodecs.Create();
            var redactor = new SecretRedactor();
            if (sensitiveSignature) redactor.RegisterSecret(signature);
            var artifacts = new FileArtifactStore(root, redactor);
            store = new SqliteEventStore(journal);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var physicalRoute = ModelRoute.DefaultForModel("claude-fixture", "anthropic",
                "https://api.example.test", ProviderFamily.AnthropicMessages);
            var model = new ModelDefinition("claude-fixture", "anthropic", 8192, 7000, 2048, 1000);
            var profile = new ModelProfileResolver().Resolve(model, null, route: physicalRoute);
            var harness = new HarnessPolicyResolver().Resolve(profile);
            var selection = new ModelSelection(new ModelIdValue("claude-fixture"), 8192,
                ToolMode.Direct, null, physicalRoute.Id, physicalRoute);
            var fingerprint = RuntimeFingerprintFactory.Create(model, profile, harness, selection,
                "harness-fixture", "context-fixture", "model-policy-fixture", "tokenizer-fixture",
                provider: provider);
            var observedRequests = new List<ModelRequest>();

            ExplorerTurn MakeTurn() => new((request, ct) =>
                {
                    observedRequests.Add(request);
                    return CompleteLocally(provider, request, ct).GetAwaiter().GetResult();
                }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                fingerprint, selection, store!, codecs, artifacts, new InMemoryAuditSink(),
                new RedactionPolicy(), questionnaires: service, recordEffectiveFingerprint: true);

            var suspended = MakeTurn().Ask("ask", "system", session, run.RunId, run.RootLane,
                "", CancellationToken.None);
            Assert.Equal(StopReason.InputRequired, suspended.StopReason);
            Assert.Single(observedRequests);
            Assert.Null(observedRequests[0].Continuation);

            store.Close();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(root);
            service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var stateFile = Path.Combine(root, "last-session.txt");
            File.WriteAllText(stateFile, session + "\n" + run.RunId);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);
            Assert.Equal("ok", server.RespondToQuestionnaire(suspended.PendingInteractionId!,
                new[] { new QuestionAnswer("approach", new[] { "safe" }, null, null) }, false).Status);

            Assert.Equal(StopReason.EndTurn, MakeTurn().Ask("continue", "system", session, run.RunId,
                run.RootLane, "", CancellationToken.None).StopReason);
            Assert.Equal(2, observedRequests.Count);
            Assert.Equal(2, handler.RequestBodies.Count);

            // ProviderState is the adapter-specific signed/encrypted continuation channel.
            // Check it directly, independent of the visible content-block assertion below.
            var continuation = Assert.IsType<ProviderState>(observedRequests[1].Continuation);
            using (var state = JsonDocument.Parse(continuation.PayloadJson))
            {
                var thinking = Assert.Single(state.RootElement.GetProperty("thinking").EnumerateArray());
                Assert.Equal(signature, thinking.GetProperty("signature").GetString());
                Assert.Equal("Voy a revisar fixture.txt", thinking.GetProperty("thinking").GetString());
            }

            // The model-message history is a distinct durable contract: a signed ProviderState
            // alone does not prove LoadConversation restored the visible ReasoningBlock.
            var resumedMessages = observedRequests[1].Messages;
            var visibleReasoning = Assert.Single(resumedMessages.SelectMany(message => message.Content)
                .OfType<ReasoningBlock>());
            Assert.Equal("Voy a revisar fixture.txt", visibleReasoning.VisibleText);
            Assert.Equal(ReasoningVisibility.Full, visibleReasoning.Visibility);
            Assert.Null(visibleReasoning.OpaquePayload); // Anthropic signs through ProviderState, not this field.
            var visibleBlocks = resumedMessages.Where(message => message.Role == MessageRole.Assistant)
                .SelectMany(message => message.Content).ToArray();
            Assert.Collection(visibleBlocks,
                block => Assert.IsType<ReasoningBlock>(block),
                block => Assert.Equal("Voy a leer fixture.txt", Assert.IsType<TextBlock>(block).Text),
                block => Assert.IsType<ToolCallBlock>(block));

            // Reopen must not duplicate the tool call/result while restoring reasoning history.
            Assert.Single(resumedMessages.SelectMany(message => message.Content).OfType<ToolCallBlock>());
            Assert.Single(resumedMessages.SelectMany(message => message.Content).OfType<ToolResultBlock>());

            // Confirm that the native adapter still sends exactly one signature-bearing block.
            using var wire = JsonDocument.Parse(handler.RequestBodies[1]);
            var assistant = Assert.Single(wire.RootElement.GetProperty("messages").EnumerateArray(), message =>
                message.GetProperty("role").GetString() == "assistant"
                && message.GetProperty("content").EnumerateArray()
                    .Any(block => block.GetProperty("type").GetString() == "tool_use"));
            var wireThinking = Assert.Single(assistant.GetProperty("content").EnumerateArray(),
                block => block.GetProperty("type").GetString() == "thinking");
            Assert.Equal(signature, wireThinking.GetProperty("signature").GetString());
            Assert.Equal("Voy a revisar fixture.txt", wireThinking.GetProperty("thinking").GetString());

            var events = store.ReadFrom(session, 1);
            Assert.Single(events.Select(codecs.Decode).OfType<TurnStarted>());
            Assert.Equal(2, events.Select(codecs.Decode).OfType<ModelStepCompleted>().Count());
            Assert.All(events, evt => Assert.DoesNotContain(signature, evt.PayloadJson,
                StringComparison.Ordinal));
            foreach (var blob in Directory.EnumerateFiles(Path.Combine(root, "blobs"), "*", SearchOption.AllDirectories))
                Assert.DoesNotContain(signature, File.ReadAllText(blob));
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = journal,
                Pooling = false,
            }.ToString());
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
