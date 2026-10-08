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

/// <summary>Real journal/CAS/interaction integration, with a scripted provider (no authenticated usage).</summary>
public sealed class ExplorerTurnDurableProviderStateTests
{
    private static readonly QuestionnaireSchema Schema = new("Choose", null,
        new QuestionField[] { new("approach", "Which?", null, QuestionKind.SingleChoice,
            new[] { new QuestionOption("safe", "Safe", null) }, null, true, null, null, null) });

    [Theory]
    [InlineData("same")]
    [InlineData("route")]
    [InlineData("model")]
    [InlineData("endpoint")]
    [InlineData("provider")]
    [InlineData("protocol")]
    [InlineData("profile")]
    [InlineData("missing-binding")]
    [InlineData("null")]
    [InlineData("corrupt")]
    [InlineData("redacted")]
    public void Resume_after_reopen_replays_only_verified_same_destination_state(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-durable-provider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore? store = null;
        try
        {
            store = new SqliteEventStore(journal);
            var codecs = EventCodecs.Create();
            var redactor = new SecretRedactor();
            if (scenario == "redacted") redactor.RegisterSecret("exact-provider-marker");
            var artifacts = new FileArtifactStore(root, redactor);
            var session = SessionId.New();
            var run = TestRun.Open(store, session, mode: RunMode.Plan);
            var service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var catalog = new FakeCatalog().Add(new UserAskTool());
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var physicalRoute = ModelRoute.DefaultForModel("scripted", "fixture-provider",
                "http://127.0.0.1:9901", ProviderFamily.OpenAiChatCompatible);
            var selection = new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct,
                null, physicalRoute.Id, physicalRoute);
            var state = new ProviderState("fixture.kind", "{\"marker\":\"exact-provider-marker\",\"value\":\"café\\nsecond\"}");
            ExplorerTurn MakeTurn(Func<ModelRequest, CancellationToken, ModelResponse> complete) => new(
                complete, executor, catalog,
                new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                new ExecutionFingerprint("scripted", "h", "t", "c", "o", "M3"), selection,
                store!, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(), questionnaires: service);

            var suspended = MakeTurn((request, _) =>
            {
                Assert.Null(request.Continuation);
                return new ModelResponse(new ContentBlock[] { new ToolCallBlock(ToolCallId.New(),
                    "provider-question", "user.ask", QuestionnaireCodec.EncodeSchema(Schema)) },
                    StopReason.ToolUse, new TokenUsage(10, 2, 0, 0, 0), scenario == "null" ? null : state,
                    new ProviderMetadata("scripted", "", null));
            }).Ask("ask", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.InputRequired, suspended.StopReason);
            var completion = store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelStepCompleted>().Single();
            var response = artifacts.GetText(completion.ResponseArtifact!.Hash)!;
            Assert.DoesNotContain("exact-provider-marker", response, StringComparison.Ordinal);
            Assert.All(store.ReadFrom(session, 1), evt => Assert.DoesNotContain("exact-provider-marker", evt.PayloadJson));
            if (scenario == "redacted")
            {
                Assert.Equal(10, completion.Usage.Input);
                Assert.Equal(2, completion.Usage.Output);
                Assert.Single(service.Pending(session));
                Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnAbandoned>());
            }
            if (scenario == "same")
            {
                using var doc = System.Text.Json.JsonDocument.Parse(response);
                var hash = doc.RootElement.GetProperty("providerState").GetProperty("StateRef")
                    .GetProperty("Hash").GetProperty("Value").GetString()!;
                var sweep = new ArtifactGc(root).Sweep(journal, TimeSpan.Zero, false,
                    DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);
                Assert.True(sweep.LiveReferenced > 0);
                Assert.DoesNotContain("exact-provider-marker", artifacts.GetText(ContentHash.Sha256(hash))!);
            }
            if (scenario == "corrupt")
            {
                using var doc = System.Text.Json.JsonDocument.Parse(response);
                var hash = doc.RootElement.GetProperty("providerState").GetProperty("StateRef")
                    .GetProperty("Hash").GetProperty("Value").GetString()!;
                File.WriteAllText(Path.Combine(root, "blobs", "sha256", hash[..2], hash.Substring(2, 2), hash), "tampered");
            }
            store.Close();
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(root);
            service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var stateFile = Path.Combine(root, "last-session.txt");
            File.WriteAllText(stateFile, session + "\n" + run.RunId);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);
            Assert.Equal("ok", server.RespondToQuestionnaire(suspended.PendingInteractionId!,
                new[] { new QuestionAnswer("approach", new[] { "safe" }, null, null) }, false).Status);
            if (scenario == "route") selection = new(selection.Model, 8192, ToolMode.Direct, null, new RouteId("other/route"));
            if (scenario == "model") selection = new(new ModelIdValue("other"), 8192, ToolMode.Direct, null, selection.RouteId);
            if (scenario == "missing-binding") selection = new(selection.Model, 8192, ToolMode.Direct, null, selection.RouteId);
            if (scenario is "endpoint" or "provider" or "protocol" or "profile")
            {
                var changedRoute = ModelRoute.DefaultForModel("scripted",
                    scenario == "provider" ? "other-provider" : physicalRoute.ProviderId,
                    scenario == "endpoint" ? "http://127.0.0.1:9902" : physicalRoute.Endpoint,
                    scenario == "protocol" ? ProviderFamily.AnthropicMessages : physicalRoute.Protocol,
                    scenario == "profile" ? "other-profile" : physicalRoute.Profile);
                Assert.Equal(physicalRoute.Id, changedRoute.Id);
                Assert.NotEqual(physicalRoute.CanonicalJson(), changedRoute.CanonicalJson());
                selection = new(selection.Model, 8192, ToolMode.Direct, null, changedRoute.Id, changedRoute);
            }
            var calls = 0;
            var result = MakeTurn((request, _) =>
            {
                calls++;
                if (scenario is "same" or "redacted") Assert.Equal(state, request.Continuation);
                else Assert.Null(request.Continuation);
                return new ModelResponse(new ContentBlock[] { new TextBlock("done") }, StopReason.EndTurn,
                    new TokenUsage(4, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "", null));
            }).Ask("continue", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(scenario == "corrupt" ? 0 : 1, calls);
            Assert.Equal(scenario == "corrupt" ? StopReason.Error : StopReason.EndTurn, result.StopReason);
            Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>());
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
