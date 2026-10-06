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
    public void Signed_thinking_survives_journal_reopen_and_replays_through_native_adapter_fixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-anthropic-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            var schema = new QuestionnaireSchema("Choose", null, new QuestionField[] {
                new("approach", "Which?", null, QuestionKind.SingleChoice,
                    new[] { new QuestionOption("safe", "Safe", null) }, null, true, null, null, null) });
            var first = ToolUseTurnStream.Replace("filesystem.read", "user.ask", StringComparison.Ordinal)
                .Replace("\"{\\\"path\\\":\\\"fixture.txt\\\"}\"",
                    JsonSerializer.Serialize(QuestionnaireCodec.EncodeSchema(schema)), StringComparison.Ordinal);
            using var handler = new TwoSseHandler(first);
            var provider = CreateProvider(handler);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(root);
            store = new SqliteEventStore(journal);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var service = new QuestionnaireInteractionService(store, codecs, artifacts);
            ExplorerTurn MakeTurn() => new((request, ct) => CompleteLocally(provider, request, ct).GetAwaiter().GetResult(),
                executor, catalog, new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3"),
                new ModelSelection(new ModelIdValue("claude-fixture"), 8192, ToolMode.Direct, null),
                store!, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(), questionnaires: service);
            var suspended = MakeTurn().Ask("ask", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.InputRequired, suspended.StopReason);
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
            Assert.Equal(2, handler.RequestBodies.Count);
            using var body = JsonDocument.Parse(handler.RequestBodies[1]);
            var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            var assistant = Assert.Single(messages, m => m.GetProperty("role").GetString() == "assistant"
                && m.GetProperty("content").EnumerateArray().Any(b => b.GetProperty("type").GetString() == "tool_use"));
            var blocks = assistant.GetProperty("content").EnumerateArray().ToArray();
            var thinking = Assert.Single(blocks, b => b.GetProperty("type").GetString() == "thinking");
            Assert.Equal("SIG-LOCAL-FIXTURE", thinking.GetProperty("signature").GetString());
            Assert.Equal("Voy a revisar fixture.txt", thinking.GetProperty("thinking").GetString());
            Assert.True(Array.FindIndex(blocks, b => b.GetProperty("type").GetString() == "thinking")
                < Array.FindIndex(blocks, b => b.GetProperty("type").GetString() == "tool_use"));
            Assert.Contains(messages, m => m.GetProperty("role").GetString() == "user"
                && m.GetProperty("content").EnumerateArray().Any(b => b.GetProperty("type").GetString() == "tool_result"
                    && b.GetProperty("tool_use_id").GetString() == "toolu_fixture"));
            Assert.All(store.ReadFrom(session, 1), evt => Assert.DoesNotContain("SIG-LOCAL-FIXTURE", evt.PayloadJson));
        }
        finally
        {
            store?.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
