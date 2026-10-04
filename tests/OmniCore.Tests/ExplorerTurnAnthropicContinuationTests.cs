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

public sealed partial class ExplorerTurnAnthropicContinuationTests
{
    [Fact]
    public void Same_ask_threads_signed_thinking_through_real_adapter_and_tool_pipeline_without_network()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-anthropic-cont-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            File.WriteAllText(Path.Combine(root, "fixture.txt"), "local-wiring-fixture-payload");
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var catalog = OmniHost.CreateExplorerTools().Catalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
                {
                    ["filesystem.read"] = PermissionDecision.Allow,
                }), root);
            using var handler = new TwoSseHandler();
            var provider = CreateProvider(handler);
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var audit = new InMemoryAuditSink();
            var selection = new ModelSelection(new ModelIdValue("claude-fixture"), 8192, ToolMode.Direct, null);
            var turn = new ExplorerTurn((request, ct) => CompleteLocally(provider, request, ct)
                    .GetAwaiter().GetResult(), executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3"), selection,
                store, codecs, artifacts, audit, new RedactionPolicy());

            var result = turn.Ask("read fixture.txt", "system", session, run.RunId, run.RootLane, "",
                TestContext.Current.CancellationToken);
            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal("listo", result.FinalText);
            Assert.Equal(2, handler.RequestBodies.Count);
            using var body = JsonDocument.Parse(handler.RequestBodies[1]);
            var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            Assert.Equal(3, messages.Length);
            Assert.Equal("user", messages[0].GetProperty("role").GetString());
            Assert.Contains("read fixture.txt", messages[0].GetProperty("content").GetRawText());
            var assistantIndex = Array.FindIndex(messages, m => m.GetProperty("role").GetString() == "assistant");
            Assert.True(assistantIndex >= 0);
            var blocks = messages[assistantIndex].GetProperty("content").EnumerateArray().ToArray();
            var thinking = Assert.Single(blocks, b => b.GetProperty("type").GetString() == "thinking");
            Assert.Equal("SIG-LOCAL-FIXTURE", thinking.GetProperty("signature").GetString());
            Assert.Equal("Voy a revisar fixture.txt", thinking.GetProperty("thinking").GetString());
            var text = Assert.Single(blocks, b => b.GetProperty("type").GetString() == "text");
            Assert.Equal("Voy a leer fixture.txt", text.GetProperty("text").GetString());
            var call = Assert.Single(blocks, b => b.GetProperty("type").GetString() == "tool_use");
            Assert.Equal("toolu_fixture", call.GetProperty("id").GetString());
            var resultIndex = Array.FindIndex(messages, m => m.GetProperty("role").GetString() == "user"
                && m.GetProperty("content").EnumerateArray().Any(b => b.GetProperty("type").GetString() == "tool_result"));
            Assert.True(resultIndex > assistantIndex);
            var toolResult = Assert.Single(messages[resultIndex].GetProperty("content").EnumerateArray(),
                b => b.GetProperty("type").GetString() == "tool_result");
            Assert.Equal("toolu_fixture", toolResult.GetProperty("tool_use_id").GetString());
            Assert.False(toolResult.TryGetProperty("is_error", out var isError) && isError.GetBoolean());
            Assert.Contains("local-wiring-fixture-payload", toolResult.GetProperty("content").GetRawText());
            // Native replay above proves the synthetic signature existed. Ordinary journal envelopes
            // must not contain it. Directly referenced fixture text blobs are checked separately below;
            // this does not recursively inspect blob references or audit sinks.
            var journalEvents = store.ReadFrom(session, 1).ToArray();
            Assert.NotEmpty(journalEvents);
            foreach (var journalEvent in journalEvents)
                Assert.DoesNotContain("SIG-LOCAL-FIXTURE", journalEvent.PayloadJson, StringComparison.Ordinal);
            var directRefs = journalEvents.SelectMany(evt => evt.ArtifactRefs).ToArray();
            Assert.NotEmpty(directRefs);
            foreach (var reference in directRefs)
            {
                var artifactText = artifacts.GetText(reference.Hash);
                Assert.NotNull(artifactText);
                Assert.DoesNotContain("SIG-LOCAL-FIXTURE", artifactText, StringComparison.Ordinal);
            }
            var auditRecords = audit.Records();
            Assert.NotEmpty(auditRecords);
            Assert.Contains(auditRecords, record => record.EventName == "turn.spend"
                && record.Run == run.RunId);
            foreach (var record in auditRecords)
                foreach (var detail in record.Details)
                {
                    Assert.DoesNotContain("SIG-LOCAL-FIXTURE", detail.Key, StringComparison.Ordinal);
                    Assert.DoesNotContain("SIG-LOCAL-FIXTURE", detail.Value, StringComparison.Ordinal);
                }
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<ModelResponse> CompleteLocally(IModelProvider provider, ModelRequest request,
        CancellationToken cancellationToken)
    {
        await foreach (var item in provider.StreamAsync(request, cancellationToken))
            if (item is ResponseCompleted completed) return completed.Response;
        throw new InvalidOperationException("Scripted provider did not complete a response.");
    }
}
