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

// Offline provider fixture through the real Turn/context/journal/CAS pipeline, not authenticated usage.
public sealed class ExplorerTurnFingerprintIntegrationTests
{
    [Fact]
    public void Lane_agent_profile_changes_effective_fingerprint_and_survives_journal_reopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-profile-fingerprint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journal = Path.Combine(root, "journal.db");
        SqliteEventStore store = new(journal);
        try
        {
            var codecs = EventCodecs.Create();
            var artifacts = new FileArtifactStore(root);
            var catalog = new FakeCatalog();
            var baseline = new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build");
            var sessions = new List<SessionId>();
            var calls = 0;
            ExecutionFingerprint Run(ProfileId profile)
            {
                var session = SessionId.New();
                sessions.Add(session);
                var run = TestRun.Open(new EventStream(store, codecs, session), session, agentProfile: profile);
                var turn = new ExplorerTurn((_, _) =>
                {
                    calls++;
                    return new ModelResponse(new ContentBlock[] { new TextBlock("fixture response") },
                        StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
                        new ProviderMetadata("fixture", "", null));
                }, ScriptedToolExecutor.WithWorkspace(catalog,
                    new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root), catalog,
                    new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                    baseline, new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
                    store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                    recordEffectiveFingerprint: true);
                Assert.Equal(StopReason.EndTurn, turn.Ask("hello", "same instruction", session, run.RunId,
                    run.RootLane, "", CancellationToken.None).StopReason);
                var fingerprint = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>()).Fingerprint;
                Assert.NotNull(fingerprint);
                var component = Assert.Single(fingerprint.Components, part => part.Name == "agent.profile");
                Assert.NotNull(component.Content);
                Assert.True(artifacts.Verify(component.Content.Hash, component.Content.Size));
                using var json = JsonDocument.Parse(artifacts.GetText(component.Content.Hash)!);
                Assert.Equal(profile.ToString(), json.RootElement.GetProperty("profileId").GetString());
                return fingerprint;
            }
            var firstProfile = ProfileId.New();
            var first = Run(firstProfile);
            var same = Run(firstProfile);
            var different = Run(ProfileId.New());
            Assert.Equal(first.Hash(), same.Hash()); // Session/Run/Lane identity is not reusable profile identity.
            Assert.NotEqual(first.Hash(), different.Hash());
            Assert.Equal(first.Components.Where(part => part.Name != "agent.profile")
                    .Select(part => (part.Name, part.Version, part.Hash, part.Content!.Size, part.Content.MediaType,
                        part.Content.Kind, part.Content.Sensitivity, part.Content.Redacted)),
                different.Components.Where(part => part.Name != "agent.profile")
                    .Select(part => (part.Name, part.Version, part.Hash, part.Content!.Size, part.Content.MediaType,
                        part.Content.Kind, part.Content.Sensitivity, part.Content.Redacted)));
            Assert.Equal(3, calls);
            store.Close();
            store = new SqliteEventStore(journal);
            var reopened = sessions.Select(session => Assert.Single(store.ReadFrom(session, 1)
                .Select(codecs.Decode).OfType<TurnStarted>()).Fingerprint!.Hash()).ToArray();
            Assert.Equal(new[] { first.Hash(), same.Hash(), different.Hash() }, reopened);
            foreach (var session in sessions)
            {
                var envelope = Assert.Single(store.ReadFrom(session, 1), evt => codecs.Decode(evt) is TurnStarted);
                var fingerprint = ((TurnStarted)codecs.Decode(envelope)).Fingerprint!;
                var profileComponent = Assert.Single(fingerprint.Components, part => part.Name == "agent.profile");
                Assert.Contains(profileComponent.Content!, envelope.ArtifactRefs);
                Assert.True(artifacts.Verify(profileComponent.Content!.Hash, profileComponent.Content.Size));
            }
        }
        finally
        {
            store.Close();
            using var connection = new SqliteConnection("DataSource=" + journal);
            SqliteConnection.ClearPool(connection);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Journal_fingerprint_matches_the_actual_filtered_provider_tool_plan_and_step_snapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-turn-fingerprint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var catalog = new FakeCatalog().Add(FakeTool.Read("visible.read")).Add(FakeTool.Write("hidden.write"));
            var artifacts = new FileArtifactStore(root);
            var baseline = new ExecutionFingerprint("fixture-model", "harness", "legacy-tools", "context", "none", "fixture-build");
            var selection = new ModelSelection(new ModelIdValue("fixture-model"), 8192, ToolMode.Direct, null);
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            ModelRequest? received = null;
            var turn = new ExplorerTurn((request, token) =>
            {
                received = request;
                return new ModelResponse(new ContentBlock[] { new TextBlock("fixture response") },
                    StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
                    new ProviderMetadata("fixture", "", null));
            }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                baseline, selection, store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 1, GuidanceLevel.Full, 1,
                    PlanControl.Assisted, 4), recordEffectiveFingerprint: true);

            Assert.Equal(StopReason.EndTurn, turn.Ask("hello", "fixture instruction", session, run.RunId,
                run.RootLane, "", CancellationToken.None).StopReason);
            Assert.NotNull(received);
            Assert.Equal("visible.read", Assert.Single(received.Tools).Name);
            var started = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>());
            Assert.NotNull(started.Fingerprint);
            var expected = RuntimeFingerprintFactory.WithTurnConfiguration(baseline, catalog, received.Tools,
                "Contexto del workspace (fuentes del run):\nfixture instruction\nFingerprint: fixture-model · context", null,
                agentProfile: Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<LaneCreated>()).AgentProfile);
            Assert.Equal(expected.Hash(), started.Fingerprint.Hash());
            var step = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<ModelStepStarted>());
            Assert.NotNull(step.ContextSnapshotRef);
            using var snapshot = JsonDocument.Parse(artifacts.GetText(step.ContextSnapshotRef.Hash)!);
            Assert.Equal(started.Fingerprint.Hash(), snapshot.RootElement.GetProperty("fingerprint").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Legacy_open_turn_overflow_abandons_once_without_a_duplicate_start_or_provider_call()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-resume-overflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var stream = new EventStream(store, codecs, session);
            var run = TestRun.Open(stream, session);
            var id = TurnId.New();
            var fingerprint = new ExecutionFingerprint("fixture-model", "h", "t", "c", "o", "fixture-build");
            stream.Append(new TurnStarted(id, run.RootLane, fingerprint));
            var catalog = new FakeCatalog();
            var executor = ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root);
            var calls = 0;
            var turn = new ExplorerTurn((request, token) =>
            {
                calls++;
                throw new InvalidOperationException("Overflow must precede provider invocation.");
            }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
                fingerprint, new ModelSelection(new ModelIdValue("fixture-model"), 1, ToolMode.Direct, null),
                store, codecs, new FileArtifactStore(root), new InMemoryAuditSink(), new RedactionPolicy());

            var result = turn.Ask("", "protected system prompt", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.ContextOverflow, result.StopReason);
            Assert.Equal(0, calls);
            var payloads = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            Assert.Equal(id, Assert.Single(payloads.OfType<TurnStarted>()).TurnId);
            Assert.Equal(id, Assert.Single(payloads.OfType<TurnAbandoned>()).TurnId);
            Assert.Empty(payloads.OfType<ModelStepStarted>());
        }
        finally { Directory.Delete(root, true); }
    }
}
