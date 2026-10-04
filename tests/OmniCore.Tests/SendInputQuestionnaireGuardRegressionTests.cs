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
public sealed class SendInputQuestionnaireGuardRegressionTests
{
    [Fact]
    public void Generic_input_is_journaled_without_resolving_pending_questionnaire()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-input-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var store = new SqliteEventStore(journal);
        try
        {
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(Path.Combine(root, "blobs"));
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var schema = new QuestionnaireSchema("Choose", null, new QuestionField[] {
                new QuestionField("approach", "Which?", null, QuestionKind.SingleChoice,
                    new[] { new QuestionOption("safe", "Safe", null) }, null, true, null, null, null) });
            var calls = 0;
            ModelResponse Complete(ModelRequest request, CancellationToken token)
            {
                calls++;
                return new ModelResponse(new ContentBlock[] {
                    new ToolCallBlock(ToolCallId.New(), "guard-call", "user.ask", QuestionnaireCodec.EncodeSchema(schema))
                }, StopReason.ToolUse, new TokenUsage(2,1,0,0,0), null, new ProviderMetadata("scripted", "", null));
            }
            ExplorerTurn MakeTurn() => new ExplorerTurn(Complete, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3"),
                new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(), questionnaires: service);
            var first = MakeTurn().Ask("ask", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.InputRequired, first.StopReason);
            Assert.NotNull(first.PendingInteractionId);
            Assert.Single(service.Pending(session));
            var original = store.ReadFrom(session,1).Select(e => codecs.Decode(e)).OfType<TurnStarted>().Single().TurnId;
            const string marker = "queued-user-marker";
            Assert.Equal(run.RunId, new RunControlService(store,codecs).SendInput(session,marker,RunMode.Plan,"Composer"));
            var second = MakeTurn().Ask("", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.InputRequired, second.StopReason);
            Assert.Equal(first.PendingInteractionId, second.PendingInteractionId);
            Assert.Equal(1,calls);
            Assert.Equal(first.PendingInteractionId, Assert.Single(service.Pending(session)).InteractionId);
            var events = store.ReadFrom(session,1).Select(e => codecs.Decode(e)).ToArray();
            Assert.Equal(original,Assert.Single(events.OfType<TurnStarted>()).TurnId);
            var input = Assert.Single(events.OfType<UserInputReceived>(),e => e.InputPartsJson.Contains(marker,StringComparison.Ordinal));
            Assert.Equal("Composer",input.Origin);
            Assert.Equal(run.RunId,input.RunId);
            Assert.DoesNotContain(events.OfType<UserInputReceived>(),e => e.Origin == "InteractionResponse(Questionnaire)");
            Assert.Empty(events.OfType<ModelCompleted>());
            Assert.Empty(events.OfType<TurnCompleted>());
        }
        finally
        {
            store.Close();
            using var connection = new SqliteConnection("DataSource="+journal);
            SqliteConnection.ClearPool(connection);
            try { Directory.Delete(root,true); } catch(IOException){} catch(UnauthorizedAccessException){}
        }
    }
}
