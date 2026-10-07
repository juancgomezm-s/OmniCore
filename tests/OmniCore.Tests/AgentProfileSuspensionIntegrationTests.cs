using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>Real SQLite/CAS suspension and reopen with an offline provider fixture, not qualification.</summary>
public sealed class AgentProfileSuspensionIntegrationTests
{
    [Theory]
    [InlineData("same")]
    [InlineData("revision")]
    [InlineData("content")]
    [InlineData("reasoningrevision")]
    [InlineData("reasoningsource")]
    [InlineData("reasoningrequested")]
    [InlineData("reasoningnone")]
    [InlineData("redacted")]
    public void Suspended_turn_retains_exact_configuration_and_receipts_after_reopen(string change)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-profile-suspension-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        var codecs = EventCodecs.Create();
        var store = new SqliteEventStore(journal);
        try
        {
            var artifacts = new FileArtifactStore(Path.Combine(root, "cas"));
            var id = ProfileId.New();
            var profile = new AgentProfile(id, "fixture", 1,
                PermissionScope.With(["**"], [], [], [], [], false), []);
            var server = new OmniServer(store, codecs, new InMemoryAuditSink(), artifacts);
            server.ConfigureAgentProfiles(new(new AgentProfileRegistry([profile]), profile));
            var ack = server.Send(WireEnvelope.Command(Ids.NewV7(),
                "{\"cmd\":\"act\",\"objective\":\"profile suspension fixture\",\"workspace\":"
                + System.Text.Json.JsonSerializer.Serialize(root) + "}"), TestContext.Current.CancellationToken);
            Assert.Equal("ok", ack.Status);
            var session = server.LastSessionId()!;
            var run = server.LastRunId()!;
            var lane = server.LastLaneId()!;
            var catalog = new FakeCatalog().Add(new UserAskTool());
            var service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var calls = 0;
            var durableStarts = 0;
            void OnStarted(TurnStarted started)
            {
                durableStarts++;
                Assert.Contains(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>(),
                    persisted => persisted.TurnId == started.TurnId);
                Assert.Equal(0, calls);
            }
            var requested = new ReasoningRequest("high", null);
            var resolution = new ReasoningResolution(requested, requested, ReasoningSelectionSource.UserDefault,
                userPreferenceRevision: 1);
            const string fixtureSecret = "sk-super-secret-value";
            var instruction = change == "redacted" ? "system " + fixtureSecret : "system";
            var safeInstruction = new RedactionPolicy().Redact(instruction);
            var schema = new QuestionnaireSchema("Choose", null,
                [new QuestionField("choice", "Which?", null, QuestionKind.SingleChoice,
                    [new QuestionOption("safe", "Safe", null)], null, true, null, null, null)]);
            ExplorerTurn Turn(AgentProfile applied, bool suspend, ReasoningResolution? reasoning)
            {
                var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                    new AgentProfilePermissionPolicy(new ScriptedPermissionPolicy([]), applied,
                        new PathBoundaryValidator(), root), root);
                return new ExplorerTurn((request, _) =>
                {
                    calls++;
                    Assert.Equal(requested, request.Reasoning);
                    Assert.True(resolution.IsEquivalentTo(request.Model.ReasoningResolution));
                    Assert.Contains(safeInstruction, request.Instructions!, StringComparison.Ordinal);
                    Assert.DoesNotContain("changed current instruction", request.Instructions!, StringComparison.Ordinal);
                    Assert.DoesNotContain(fixtureSecret, request.Instructions!, StringComparison.Ordinal);
                    if (suspend)
                        return new ModelResponse([new ToolCallBlock(ToolCallId.New(), "fixture-question", "user.ask",
                            QuestionnaireCodec.EncodeSchema(schema))], StopReason.ToolUse, new TokenUsage(2, 1, 0, 0, 0),
                            null, new ProviderMetadata("fixture", "", null));
                    Assert.Contains(request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(),
                        result => result.Content.OfType<TextBlock>().Any(text => text.Text.Contains("safe", StringComparison.Ordinal)));
                    return new ModelResponse([new TextBlock("fixture completed")], StopReason.EndTurn,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "", null));
                }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), []),
                    new ExecutionFingerprint("fixture", "h", "t", "c", "o", "build"),
                    new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct,
                        reasoning?.AppliedRequest, reasoningResolution: reasoning),
                    store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                    questionnaires: service, recordEffectiveFingerprint: true);
            }

            var suspended = Turn(profile, true, resolution).Ask("ask the user", instruction, session, run, lane, "",
                TestContext.Current.CancellationToken, instructionSnapshot: new TurnInstructionSnapshot(true, instruction),
                turnStarted: OnStarted);
            Assert.Equal(StopReason.InputRequired, suspended.StopReason);
            Assert.NotNull(suspended.PendingInteractionId);
            var original = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>());
            var originalRefs = Assert.Single(store.ReadFrom(session, 1), evt => codecs.Decode(evt) is TurnStarted).ArtifactRefs;
            Assert.Equal("2", Assert.Single(original.Fingerprint!.Components,
                component => component.Name == "agent.profile").Version);
            Assert.Equal(1, calls);
            Assert.Equal(1, durableStarts);
            Assert.Equal(new TurnInstructionSnapshot(true, safeInstruction), original.InstructionSnapshot);
            Assert.DoesNotContain(fixtureSecret, Assert.Single(store.ReadFrom(session, 1),
                evt => codecs.Decode(evt) is TurnStarted).PayloadJson, StringComparison.Ordinal);
            Assert.True(resolution.IsEquivalentTo(original.ReasoningResolution));
            var resolvedComponent = Assert.Single(original.Fingerprint.Components,
                component => component.Name == "model.reasoning.resolved");
            Assert.Contains(resolvedComponent.Content!, originalRefs);
            using (var json = System.Text.Json.JsonDocument.Parse(artifacts.GetText(resolvedComponent.Hash)!))
            {
                var value = json.RootElement.GetProperty("resolution");
                Assert.Equal("UserDefault", value.GetProperty("source").GetString());
                Assert.Equal(1, value.GetProperty("userPreferenceRevision").GetInt64());
                Assert.Equal("high", value.GetProperty("appliedRequest").GetProperty("kind").GetString());
            }
            Close(store);
            store = new SqliteEventStore(journal);
            artifacts = new FileArtifactStore(Path.Combine(root, "cas"));
            service = new QuestionnaireInteractionService(store, codecs, artifacts);
            var stateFile = Path.Combine(root, "state.txt");
            File.WriteAllText(stateFile, session + "\n" + run);
            server = new OmniServer(store, codecs, new InMemoryAuditSink(), stateFile, artifacts);
            Assert.Equal("ok", server.RespondToQuestionnaire(suspended.PendingInteractionId,
                [new QuestionAnswer("choice", ["safe"], null, null)], false).Status);

            var applied = change switch
            {
                "revision" => new AgentProfile(id, "fixture", 2, profile.PermissionCeiling, []),
                "content" => new AgentProfile(id, "changed", 1, profile.PermissionCeiling, []),
                _ => new AgentProfile(id, "fixture", 1, profile.PermissionCeiling, []),
            };
            var before = store.CurrentSequence(session);
            var changedReasoning = change switch
            {
                "reasoningrevision" => new ReasoningResolution(requested, requested, ReasoningSelectionSource.UserDefault,
                    userPreferenceRevision: 2),
                "reasoningsource" => new ReasoningResolution(requested, requested, ReasoningSelectionSource.RunOverride,
                    runPreferenceRevision: 1),
                "reasoningrequested" => new ReasoningResolution(new ReasoningRequest("fixture-other-effort", null),
                    requested, ReasoningSelectionSource.UserDefault, userPreferenceRevision: 1,
                    reductions: [ReasoningReduction.Capability]),
                "reasoningnone" => null,
                _ => resolution,
            };
            var result = Turn(applied, false, changedReasoning).Ask("", "changed current instruction", session, run, lane, "",
                TestContext.Current.CancellationToken, turnStarted: OnStarted);
            if (change is not ("same" or "redacted"))
            {
                Assert.Equal(StopReason.Error, result.StopReason);
                Assert.Contains(change.StartsWith("reasoning", StringComparison.Ordinal) ? "reasoning resolution" : "AgentProfile",
                    result.FinalText!, StringComparison.Ordinal);
                Assert.Equal(1, calls);
                Assert.Equal(before, store.CurrentSequence(session));
                Assert.Empty(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnCompleted>());
                // Restoring the exact reusable configuration can finish the original Turn.
                result = Turn(profile, false, resolution).Ask("", "changed current instruction", session, run, lane, "",
                    TestContext.Current.CancellationToken, turnStarted: OnStarted);
            }
            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(2, calls);
            Assert.Equal(1, durableStarts);
            var restored = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>());
            Assert.Equal(original.TurnId, restored.TurnId);
            Assert.Equal(original.Fingerprint.Hash(), restored.Fingerprint!.Hash());
            Assert.Equal(originalRefs, Assert.Single(store.ReadFrom(session, 1), evt => codecs.Decode(evt) is TurnStarted).ArtifactRefs);
            Assert.Equal(original.TurnId, Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnCompleted>()).TurnId);
            Assert.Equal(2, store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelStepCompleted>().Count());
            Assert.All(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelStepStarted>(),
                step => Assert.True(resolution.IsEquivalentTo(step.ReasoningResolution)));
            foreach (var reference in originalRefs) Assert.True(artifacts.Verify(reference.Hash, reference.Size));
        }
        finally
        {
            Close(store);
            Directory.Delete(root, true);
        }
    }

    private static void Close(SqliteEventStore store)
    {
        store.Close();
        SqliteConnection.ClearPool((SqliteConnection)store.Connection);
        store.Connection.Dispose();
    }
}
