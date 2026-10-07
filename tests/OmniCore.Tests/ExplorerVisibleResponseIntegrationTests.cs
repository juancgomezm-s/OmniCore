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

namespace OmniCore.Tests;

/// <summary>Scripted generation, real SQLite/CAS and next-Turn context; no authenticated provider.</summary>
public sealed class ExplorerVisibleResponseIntegrationTests
{
    [Theory]
    [InlineData("first ", "second")]
    [InlineData("| A | B |\n", "|---|---|\n| 1 | 2 |")]
    [InlineData("```cs\n", "int value = 42;\n```")]
    [InlineData("secret sk-super-secret-value ", "safe tail")]
    [InlineData("complete answer", "")]
    public void Full_visible_answer_survives_terminal_artifacts_reopen_and_next_turn_context(
        string first, string second)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-visible-answer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(root, new SecretRedactor());
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var requests = new List<ModelRequest>();
            var redaction = new RedactionPolicy();
            var expected = redaction.Redact(first + second);
            var response = new ModelResponse([
                new ReasoningBlock("private fixture reasoning", ReasoningVisibility.Full, null),
                new TextBlock(first), new TextBlock(second)], StopReason.EndTurn,
                new TokenUsage(7, 5, 0, 0, 0), null, new ProviderMetadata("fixture", "scripted", null));
            ExplorerTurn MakeTurn(ModelResponse answer) => new((request, _) =>
            {
                requests.Add(request);
                return answer;
            }, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "fixture"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
                store!, codecs, artifacts, new InMemoryAuditSink(), redaction,
                maximumGenerationRequestAttempts: 1);

            var result = MakeTurn(response).Ask("first question", "system", session, run.RunId,
                run.RootLane, "", TestContext.Current.CancellationToken);
            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(expected, result.FinalText);
            var events = store.ReadFrom(session, 1);
            var firstTurn = Assert.Single(events.Select(codecs.Decode).OfType<TurnStarted>());
            var assistant = Assert.Single(events.Select(codecs.Decode).OfType<AssistantMessageRecorded>());
            var textReference = Assert.IsType<ArtifactRef>(assistant.ContentRef);
            var summary = Assert.Single(events.Select(codecs.Decode).OfType<ModelCompleted>());
            var step = Assert.Single(events.Select(codecs.Decode).OfType<ModelStepCompleted>());
            Assert.Equal(expected, artifacts.GetText(textReference.Hash));
            foreach (var reference in new[] { summary.ResponseArtifact!, step.ResponseArtifact! })
            {
                using var json = JsonDocument.Parse(artifacts.GetText(reference.Hash)!);
                Assert.Equal(expected, json.RootElement.GetProperty("response").GetString());
            }
            Assert.Equal(firstTurn.TurnId, assistant.TurnId);
            Assert.Equal(firstTurn.TurnId, summary.TurnId);
            Assert.Single(events.Select(codecs.Decode).OfType<TurnCompleted>());

            var connection = (SqliteConnection)store.Connection;
            store.Close();
            SqliteConnection.ClearPool(connection);
            connection.Dispose();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(root, new SecretRedactor());
            Assert.Equal(expected, artifacts.GetText(textReference.Hash));
            var next = MakeTurn(response with { Content = [new TextBlock("continued")] }).Ask(
                "next question", "system", session, run.RunId, run.RootLane, "",
                TestContext.Current.CancellationToken);
            Assert.Equal(StopReason.EndTurn, next.StopReason);
            Assert.Equal(2, requests.Count);
            var priorAnswer = Assert.Single(requests[1].Messages, message => message.Role == MessageRole.Assistant);
            Assert.Equal(expected, string.Concat(priorAnswer.Content.OfType<TextBlock>().Select(block => block.Text)));
            Assert.DoesNotContain("private fixture reasoning", priorAnswer.Content.OfType<TextBlock>()
                .Select(block => block.Text));
            Assert.Equal(2, store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>()
                .Select(turn => turn.TurnId).Distinct().Count());
        }
        finally
        {
            if (store is not null)
            {
                var connection = (SqliteConnection)store.Connection;
                store.Close();
                SqliteConnection.ClearPool(connection);
                connection.Dispose();
            }
            Directory.Delete(root, true);
        }
    }
}
