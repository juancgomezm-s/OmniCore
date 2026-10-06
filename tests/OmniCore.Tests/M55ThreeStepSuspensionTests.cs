using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>ADR0046 exit criterion: real journal/CAS and questionnaire service, scripted model only.</summary>
public sealed class M55ThreeStepSuspensionTests
{
    [Fact]
    public void Three_steps_across_questionnaire_and_reopen_keep_one_turn_and_complete_derived_spend()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-m55-three-step-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var blobs = Path.Combine(root, "artifacts");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(blobs);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var readId = ToolCallId.New();
            var askId = ToolCallId.New();
            var schema = new QuestionnaireSchema("Continue?", null,
                [new QuestionField("choice", "Choice", null, QuestionKind.SingleChoice,
                    [new QuestionOption("yes", "Yes", null)], null, true, null, null, null)]);
            var usages = new TokenUsage[] { new(10, 2, 3, 1, 1), new(20, 3, 4, 2, 2), new(30, 4, 5, 3, 2) };
            var calls = 0;

            ExplorerTurn MakeTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete)
            {
                var catalog = FakeCatalog.Default().Add(new UserAskTool());
                var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                    new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
                return new ExplorerTurn(complete, executor, catalog,
                    new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                    new ExecutionFingerprint("test", "h", "t", "c", "o", "M5.5"),
                    new ModelSelection(new ModelIdValue("test"), 8192, ToolMode.Direct, null),
                    store!, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                    pricing: new ModelPricing(1m, 2m),
                    questionnaires: new QuestionnaireInteractionService(store!, codecs, artifacts));
            }

            var suspended = MakeTurn((request, _) =>
            {
                calls++;
                Assert.InRange(calls, 1, 2); // no third provider invocation before the answer
                if (calls == 2)
                    Assert.Contains(request.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>(),
                        result => result.Id == readId && !result.IsError);
                return new ModelResponse(
                    [new ToolCallBlock(calls == 1 ? readId : askId, "provider-call-" + calls,
                        calls == 1 ? "fake.read" : "user.ask",
                        calls == 1 ? "{}" : QuestionnaireCodec.EncodeSchema(schema))],
                    StopReason.ToolUse, usages[calls - 1], null, new ProviderMetadata("fixture", "test", null),
                    ReportedUsageFields: TokenUsageFields.All);
            }).Ask("read then ask", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.InputRequired, suspended.StopReason);
            Assert.NotNull(suspended.PendingInteractionId);
            Assert.Equal(2, calls);
            var before = store.ReadFrom(session, 1).ToArray();
            var beforePayloads = before.Select(codecs.Decode).ToArray();
            var turn = Assert.Single(beforePayloads.OfType<TurnStarted>()).TurnId;
            Assert.Equal(2, beforePayloads.OfType<ModelStepCompleted>().Count());
            Assert.DoesNotContain(beforePayloads, payload => payload is ModelCompleted or TurnCompleted);
            AssertUsage(store, codecs, artifacts, session, 30, 5, 7, 3, 0.000040m, 2);
            var prefix = before.Select(e => JsonSerializer.Serialize(e)).ToArray();

            store.Close();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(blobs);
            Assert.Equal(prefix, store.ReadFrom(session, 1).Select(e => JsonSerializer.Serialize(e)).ToArray());
            AssertUsage(store, codecs, artifacts, session, 30, 5, 7, 3, 0.000040m, 2);
            var stateFile = Path.Combine(root, "lastsession.txt");
            File.WriteAllText(stateFile, session + "\n" + run.RunId);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);
            Assert.Equal("ok", server.RespondToQuestionnaire(suspended.PendingInteractionId!,
                [new QuestionAnswer("choice", ["yes"], null, null)], false).Status);

            var final = MakeTurn((request, _) =>
            {
                calls++;
                Assert.Equal(3, calls);
                Assert.Contains(request.Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>(),
                    result => result.Id == askId && !result.IsError && result.Content.OfType<TextBlock>()
                        .Any(text => text.Text.Contains("yes", StringComparison.Ordinal)));
                return new ModelResponse([new TextBlock("done")], StopReason.EndTurn, usages[2], null,
                    new ProviderMetadata("fixture", "test", null), ReportedUsageFields: TokenUsageFields.All);
            }).Ask("continue", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);

            Assert.Equal(StopReason.EndTurn, final.StopReason);
            Assert.Equal(3, calls);
            var events = store.ReadFrom(session, 1).ToArray();
            var payloads = events.Select(codecs.Decode).ToArray();
            Assert.Equal(turn, Assert.Single(payloads.OfType<TurnStarted>()).TurnId);
            Assert.Equal(turn, Assert.Single(payloads.OfType<TurnCompleted>()).TurnId);
            Assert.DoesNotContain(payloads, payload => payload is TurnAbandoned or TurnInterrupted);
            var steps = payloads.OfType<ModelStepCompleted>().OrderBy(step => step.StepIndex).ToArray();
            Assert.Equal(new[] { 0, 1, 2 }, steps.Select(step => step.StepIndex));
            Assert.All(steps, step => Assert.Equal(turn, step.TurnId));
            Assert.Equal(usages, steps.Select(step => step.Usage));
            Assert.All(steps, step => Assert.Equal(TokenUsageFields.All, step.ReportedUsageFields));
            Assert.Equal(new decimal?[] { 0.000014m, 0.000026m, 0.000038m }, steps.Select(step => step.CostUsd));
            foreach (var step in steps)
            {
                Assert.NotNull(step.ResponseArtifact);
                Assert.True(artifacts.Verify(step.ResponseArtifact!.Hash, step.ResponseArtifact.Size));
                Assert.Contains(events.Single(e => codecs.Decode(e) is ModelStepCompleted s
                    && s.StepIndex == step.StepIndex).ArtifactRefs,
                    reference => reference.Hash == step.ResponseArtifact.Hash);
            }
            var summary = Assert.Single(payloads.OfType<ModelCompleted>());
            Assert.Equal(turn, summary.TurnId);
            using var json = JsonDocument.Parse(artifacts.GetText(summary.ResponseArtifact!.Hash)!);
            foreach (var (name, value) in new (string, long)[] {
                ("input", 60), ("output", 9), ("cacheRead", 12), ("cacheWrite", 6), ("reasoning", 5) })
                Assert.Equal(value, json.RootElement.GetProperty(name).GetInt64());
            Assert.Equal("0.000078", json.RootElement.GetProperty("costUsd").GetString());
            AssertUsage(store, codecs, artifacts, session, 60, 9, 12, 6, 0.000078m, 3);
            var sequence = store.CurrentSequence(session);

            store.Close();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(blobs);
            Assert.Equal(sequence, store.CurrentSequence(session));
            Assert.Equal(events.Select(e => JsonSerializer.Serialize(e)).ToArray(),
                store.ReadFrom(session, 1).Select(e => JsonSerializer.Serialize(e)).ToArray());
            AssertUsage(store, codecs, artifacts, session, 60, 9, 12, 6, 0.000078m, 3);
            Assert.Equal(sequence, store.CurrentSequence(session)); // snapshots never append or double-count
        }
        finally
        {
            store?.Close();
            using var pool = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(pool);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertUsage(IEventStore store, IEventCodecRegistry codecs, IArtifactStore artifacts,
        SessionId session, long input, long output, long read, long write, decimal cost, int invocations)
    {
        var usage = SessionUsageReporter.ReadConversation(store, codecs, artifacts, session);
        Assert.Equal(session.ToString(), usage.SessionId);
        Assert.Equal(MetricAvailability.Reported, usage.Tokens.Availability);
        Assert.Equal(new TokenTotals(input, output, read, write), usage.Tokens.Value);
        Assert.Equal(input + output, usage.Total.Value); // cache/reasoning are subsets, not added twice
        Assert.Equal(new Money(cost, "USD"), usage.Cost.Value);
        Assert.Equal(MetricAvailability.Estimated, usage.Cost.Availability);
        Assert.Equal(invocations, usage.ModelInvocations);
        Assert.Equal(0, usage.IncompleteInvocations);
        Assert.Equal(usage, SessionUsageReporter.ReadConversation(store, codecs, artifacts, session));
    }
}
