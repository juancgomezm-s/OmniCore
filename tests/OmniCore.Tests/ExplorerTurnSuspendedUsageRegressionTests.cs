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

/// <summary>
/// Regression: after a questionnaire suspension and completion, recorded usage for the run must cover
/// both segments. The test reads the existing ModelCompleted-backed accounting format; it does not
/// prescribe an additional ModelCompleted event at suspension. Expected RED: only resumed usage
/// (4 input / 1 output) lands in an artifact; the suspended segment's 10 input / 2 output is dropped, so
/// the journaled total for the run is 4/1 instead of the intended 14/3.
/// </summary>
public sealed class ExplorerTurnSuspendedUsageRegressionTests
{
    private static readonly QuestionnaireSchema Schema = new QuestionnaireSchema("Choose an approach", null,
        new QuestionField[] { new QuestionField("approach", "Which approach?", null,
            QuestionKind.SingleChoice, new[] { new QuestionOption("safe", "Safe", null),
                new QuestionOption("fast", "Fast", null) }, null, true, null, null, null) });

    [Fact]
    public void Suspend_then_resume_persists_usage_from_both_turn_segments()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-explorer-suspended-usage-" + Guid.NewGuid().ToString("N"));
        var journal = Path.Combine(root, "journal.db");
        var blobs = Path.Combine(root, "blobs");
        Directory.CreateDirectory(root);
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(blobs);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null);
            var fingerprint = new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3");

            ExplorerTurn MakeTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete) =>
                new ExplorerTurn(complete, executor, catalog,
                    new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                    fingerprint, selection, store!, codecs, artifacts, new InMemoryAuditSink(),
                    new RedactionPolicy(), questionnaires: service);

            // First Ask emits user.ask consuming 10 input / 2 output tokens and suspends the turn.
            var suspended = MakeTurn((_, _) => new ModelResponse(new ContentBlock[] {
                new ToolCallBlock(ToolCallId.New(), "provider-call-1", "user.ask",
                    QuestionnaireCodec.EncodeSchema(Schema))
            }, StopReason.ToolUse, new TokenUsage(10, 2, 0, 0, 0), null,
                new ProviderMetadata("scripted", "", null))).Ask(
                "ask the user", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.InputRequired, suspended.StopReason);
            Assert.NotNull(suspended.PendingInteractionId);
            // The in-memory TurnResult only proves what the provider reported for this segment; it says
            // nothing about persistence, which is verified below from the journal and artifact store.
            Assert.Equal(10, suspended.Usage.Input);
            Assert.Equal(2, suspended.Usage.Output);
            var pending = service.Pending(session);
            Assert.Single(pending);
            var interaction = pending[0].InteractionId;
            var originalTurn = store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt))
                .OfType<TurnStarted>().Single().TurnId;

            // Reopen the store in the same process and resolve via the public interaction server.
            store.Close();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(blobs);
            service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var stateFile = Path.Combine(root, "lastsession.txt");
            File.WriteAllText(stateFile, session + "\n" + run.RunId);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);
            var accepted = server.RespondToQuestionnaire(interaction,
                new[] { new QuestionAnswer("approach", new[] { "safe" }, null, null) }, false);
            Assert.Equal("ok", accepted.Status);
            Assert.Empty(service.Pending(session));

            // Resume the same turn; the provider answers with final text consuming 4 input / 1 output.
            var resumed = MakeTurn((_, _) => new ModelResponse(new ContentBlock[] {
                new TextBlock("recorded")
            }, StopReason.EndTurn, new TokenUsage(4, 1, 0, 0, 0), null,
                new ProviderMetadata("scripted", "", null)));
            var completed = resumed.Ask("continue with the safe approach", "system", session, run.RunId,
                run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, completed.StopReason);

            // One original TurnStarted across suspend + resume.
            var turnIds = store.ReadFrom(session, 1).Select(evt => codecs.Decode(evt))
                .OfType<TurnStarted>().Select(evt => evt.TurnId).ToArray();
            Assert.Single(turnIds);
            Assert.Equal(originalTurn, turnIds[0]);

            // Accounting postcondition: every ModelCompleted for this run references a versioned
            // model-usage JSON artifact; summing the top-level numeric input/output across those
            // records must cover BOTH the suspended segment (10/2) and the resumed segment (4/1),
            // i.e. 14 input / 3 output. Never inferred from the returned TurnResult.
            var records = new List<(long Input, long Output)>();
            foreach (var evt in store.ReadFrom(session, 1))
            {
                if (codecs.Decode(evt) is not ModelCompleted modelCompleted) continue;
                Assert.NotNull(modelCompleted.ResponseArtifact);
                var text = artifacts.GetText(modelCompleted.ResponseArtifact.Hash);
                Assert.False(string.IsNullOrEmpty(text));
                using var document = System.Text.Json.JsonDocument.Parse(text);
                var envelope = document.RootElement;
                Assert.True(envelope.TryGetProperty("omnicoreUsage", out var version)
                    && version.ValueKind == System.Text.Json.JsonValueKind.Number && version.GetInt32() == 1,
                    "model-usage artifact must declare omnicoreUsage version 1");
                if (!envelope.TryGetProperty("runId", out var runIdElement)) continue;
                if (!string.Equals(runIdElement.GetString(), run.RunId.ToString(), StringComparison.Ordinal)) continue;
                Assert.True(envelope.TryGetProperty("input", out var inputElement)
                    && inputElement.ValueKind == System.Text.Json.JsonValueKind.Number,
                    "model-usage artifact must carry a numeric top-level input");
                Assert.True(envelope.TryGetProperty("output", out var outputElement)
                    && outputElement.ValueKind == System.Text.Json.JsonValueKind.Number,
                    "model-usage artifact must carry a numeric top-level output");
                records.Add((inputElement.GetInt64(), outputElement.GetInt64()));
            }
            Assert.NotEmpty(records);
            Assert.Equal(14L, records.Sum(record => record.Input));
            Assert.Equal(3L, records.Sum(record => record.Output));

            var stepStarts = store.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<ModelStepStarted>().Where(step => step.TurnId == originalTurn)
                .OrderBy(step => step.StepIndex).ToArray();
            var stepCompletions = store.ReadFrom(session, 1).Select(codecs.Decode)
                .OfType<ModelStepCompleted>().Where(step => step.TurnId == originalTurn)
                .OrderBy(step => step.StepIndex).ToArray();
            Assert.Equal(new[] { 0, 1 }, stepStarts.Select(step => step.StepIndex));
            Assert.Equal(new[] { 0, 1 }, stepCompletions.Select(step => step.StepIndex));
            Assert.Equal(new long[] { 10, 4 }, stepCompletions.Select(step => step.Usage.Input));
            Assert.Equal(new long[] { 2, 1 }, stepCompletions.Select(step => step.Usage.Output));
            Assert.All(stepCompletions, step => Assert.NotNull(step.ResponseArtifact));
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
