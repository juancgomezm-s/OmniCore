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

// Offline provider fixtures; real journal/CAS and runtime. No authenticated qualification claim.
public sealed class ModeAuthorityFingerprintIntegrationTests
{
    private static readonly ExecutionFingerprint Baseline = new("fixture", "h", "t", "c", "o", "build");

    private static RunModeAuthority Authority(RunId run, string objective) => new(run, 1, RunMode.Act,
        ExecutionStrategy.Direct, ProductEffort.UltraCode, false, true, 1,
        RunModeAuthority.ObjectiveDigestFor(objective), 1,
        new ModeSwitchAuthorization(Guid.NewGuid(), 1, 1, RunModeAuthority.ObjectiveDigestFor(objective), 1,
            [RunMode.Act, RunMode.Plan], new ModeSwitchLimits(0, 0, 2, 4, 60, 0m), DateTimeOffset.UtcNow));

    [Fact]
    public void Authority_component_is_exact_canonical_idempotent_and_never_inherited_when_absent()
    {
        WithDirectory(root =>
        {
            var artifacts = new FileArtifactStore(root);
            var catalog = new FakeCatalog();
            var authority = Authority(RunId.New(), "fixture objective");
            ExecutionFingerprint Apply(ExecutionFingerprint baseline, RunModeAuthority? value) =>
                RuntimeFingerprintFactory.WithTurnConfiguration(baseline, catalog, [], "system", null,
                    artifacts, modeAuthority: value);
            var original = Apply(Baseline, authority);
            var component = Assert.Single(original.Components, part => part.Name == "run.mode_authority");
            Assert.NotNull(component.Content);
            Assert.True(artifacts.Verify(component.Content.Hash, component.Content.Size));
            using var json = JsonDocument.Parse(artifacts.GetText(component.Content.Hash)!);
            var value = json.RootElement.GetProperty("authority");
            Assert.Equal(authority.RunId.ToString(), value.GetProperty("runId").GetString());
            Assert.True(value.GetProperty("autoModeSwitch").GetBoolean());
            Assert.Equal(authority.Authorization!.AuthorizationId,
                value.GetProperty("authorization").GetProperty("authorizationId").GetGuid());
            Assert.Equal(4, value.GetProperty("authorization").GetProperty("limits").GetProperty("maxToolCalls").GetInt32());
            Assert.Equal(original.Hash(), Apply(original, authority).Hash());
            Assert.NotEqual(original.Hash(), Apply(Baseline, authority with { ModePinned = true, AutoModeSwitch = false }).Hash());
            Assert.Equal(Apply(Baseline, null).Hash(), Apply(original, null).Hash());
            Assert.Throws<ArgumentException>(() => Apply(Baseline, authority with { ObjectiveDigest = "different" }));
        });
    }

    [Fact]
    public void Normal_turn_records_explicit_Run_authority_and_roots_its_content_in_journal()
    {
        WithDirectory(root =>
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var stream = new EventStream(store, codecs, session);
            var run = TestRun.Open(stream, session, "fixture objective");
            var authority = Authority(run.RunId, "fixture objective");
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane));
            stream.Append(new RunModeAuthoritySelected(authority, "fixture-create", "RunCreated"));
            var artifacts = new FileArtifactStore(root);
            var calls = 0;
            var result = Turn(root, store, codecs, artifacts, () => calls++).Ask("hello", "system", session,
                run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(1, calls);
            var envelope = Assert.Single(store.ReadFrom(session, 1), evt => codecs.Decode(evt) is TurnStarted);
            var fingerprint = ((TurnStarted)codecs.Decode(envelope)).Fingerprint!;
            var component = Assert.Single(fingerprint.Components, part => part.Name == "run.mode_authority");
            Assert.Contains(component.Content!, envelope.ArtifactRefs);
            using var json = JsonDocument.Parse(artifacts.GetText(component.Content!.Hash)!);
            Assert.Equal(authority.Authorization!.AuthorizationId,
                json.RootElement.GetProperty("authority").GetProperty("authorization").GetProperty("authorizationId").GetGuid());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reopen_resume_preserves_original_authority_evidence_without_reactivating_revoked_authority(bool legacyFingerprint)
    {
        WithDirectory(root =>
        {
            var journal = Path.Combine(root, "journal.db");
            var store = new SqliteEventStore(journal);
            try
            {
                var artifacts = new FileArtifactStore(root);
                var codecs = EventCodecs.Create();
                var session = SessionId.New();
                var stream = new EventStream(store, codecs, session);
                var run = TestRun.Open(stream, session, "fixture objective");
                var authority = Authority(run.RunId, "fixture objective");
                var catalog = new FakeCatalog();
                var turnId = TurnId.New();
                var fingerprint = RuntimeFingerprintFactory.WithTurnConfiguration(Baseline, catalog, [],
                    "Contexto del workspace (fuentes del run):\nsystem\nFingerprint: fixture · c", null, artifacts,
                    Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<LaneCreated>()).AgentProfile,
                    modeAuthority: legacyFingerprint ? null : authority);
                using (ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane)))
                {
                    stream.Append(new RunModeAuthoritySelected(authority, "fixture-create", "RunCreated"));
                    stream.Append(new TurnStarted(turnId, run.RootLane, fingerprint));
                    stream.Append(new RunModeAuthorityRevoked(run.RunId, 2, authority.Authorization!.AuthorizationId,
                        "disable adaptive", "fixture-revoke", "User"));
                }
                store.Close();
                store = new SqliteEventStore(journal);
                var calls = 0;
                var result = Turn(root, store, codecs, artifacts, () => calls++).Ask("", "system", session,
                    run.RunId, run.RootLane, "", CancellationToken.None);
                Assert.Equal(StopReason.EndTurn, result.StopReason);
                Assert.Equal(1, calls);
                var started = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>());
                Assert.Equal(turnId, started.TurnId);
                Assert.Equal(fingerprint.Hash(), started.Fingerprint!.Hash());
                var replayed = RunProjection.Replay(session, run.RunId, codecs, store.ReadFrom(session, 1)).ModeAuthority!;
                Assert.False(replayed.AutoModeSwitch);
                Assert.Null(replayed.Authorization);
                Assert.Equal(2, replayed.Revision);
                foreach (var component in started.Fingerprint.Components)
                    Assert.True(artifacts.Verify(component.Content!.Hash, component.Content.Size));
            }
            finally
            {
                store.Close();
                using var connection = new SqliteConnection("DataSource=" + journal);
                SqliteConnection.ClearPool(connection);
            }
        });
    }

    private static ExplorerTurn Turn(string root, IEventStore store, EventCodecs codecs, IArtifactStore artifacts, Action call)
    {
        var catalog = new FakeCatalog();
        return new ExplorerTurn((_, _) =>
        {
            call();
            return new ModelResponse([new TextBlock("fixture response")], StopReason.EndTurn,
                new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "", null));
        }, ScriptedToolExecutor.WithWorkspace(catalog, new ScriptedPermissionPolicy([]), root), catalog,
            new ContextMaterializer(new FakeTokenCounter(), []), Baseline,
            new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null), store, codecs, artifacts,
            new InMemoryAuditSink(), new RedactionPolicy(), recordEffectiveFingerprint: true);
    }

    private static void WithDirectory(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-authority-fingerprint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, true); }
    }
}
