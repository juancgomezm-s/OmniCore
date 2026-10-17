using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using System.Text.Json;

namespace OmniCore.Tests;

public sealed class SessionSecurityInFlightReplayTests
{
    private sealed class TestPaths(string root) : IPlatformPaths
    {
        public string DataDirectory => Path.Combine(root, "data");
        public string UserDatabasePath => Path.Combine(DataDirectory, "user.db");
        public string ConfigDirectory => Path.Combine(root, "config");
        public string WorkspaceDirectory(string workspaceId) => Path.Combine(DataDirectory, "workspaces", workspaceId);
    }

    [Fact]
    public void Startup_with_owned_legacy_session_migrates_once_before_query_but_query_only_host_does_not_write()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-session-security-startup-" + Guid.NewGuid().ToString("N"));
        var workspaceA = Path.Combine(root, "workspace-a");
        var workspaceB = Path.Combine(root, "workspace-b");
        Directory.CreateDirectory(workspaceA);
        Directory.CreateDirectory(workspaceB);
        var journal = Path.Combine(root, "journal.db");
        var paths = new TestPaths(root);
        SqliteEventStore? ownerStore = null;
        SqliteEventStore? observerStore = null;
        SqliteEventStore? orphanStore = null;
        OmniServer? owner = null;
        OmniServer? observer = null;
        OmniServer? orphanServer = null;
        try
        {
            var ownerSession = SessionId.New();
            var queryOnlySession = SessionId.New();
            var codecs = EventCodecs.Create();
            var stateFile = Path.Combine(root, "active-session.txt");
            using (var seed = new SqliteEventStore(journal))
            {
                var physicalRoot = Path.GetFullPath(workspaceA);
                var rootIdentity = WorkspaceRootIdentity.Establish(physicalRoot);
                Assert.True(WorkspaceRootIdentity.Verify(physicalRoot, rootIdentity));
                var ownerStream = new EventStream(seed, codecs, ownerSession);
                ownerStream.AppendBatch(new DomainEventPayload[]
                {
                    new SessionCreated(ownerSession, WorkspaceId.Of(physicalRoot).ToString(), "<workspace hidden>",
                        ProfileId.New(), DateTimeOffset.UtcNow),
                    new WorkspaceRootEstablished(ownerSession, physicalRoot, DateTimeOffset.UtcNow,
                        rootIdentity),
                }, DurabilityClass.Barrier);
                new EventStream(seed, codecs, queryOnlySession).Append(new SessionCreated(queryOnlySession,
                    WorkspaceId.Of(workspaceB).ToString(), workspaceB, ProfileId.New(), DateTimeOffset.UtcNow));
            }

            File.WriteAllText(stateFile, ownerSession + "\n-\n");
            ownerStore = new SqliteEventStore(journal);
            owner = new OmniServer(ownerStore, codecs, new InMemoryAuditSink(), stateFile,
                platformPathsFactory: () => paths);
            var ownerQuery = owner.Query("sessionSecurity", CancellationToken.None);
            using var ownerJson = JsonDocument.Parse(ownerQuery!.Json);
            Assert.Equal("LegacyMigrated", ownerJson.RootElement.GetProperty("provenance").GetString());
            Assert.Equal(1, ownerJson.RootElement.GetProperty("revision").GetInt64());
            var migrated = ownerStore.ReadFrom(ownerSession, 1).Select(codecs.Decode)
                .OfType<SessionSecurityPolicySet>().ToArray();
            Assert.Equal(SessionSecurityPolicySource.LegacyMigration, Assert.Single(migrated).Source);
            var created = Assert.Single(ownerStore.ReadFrom(ownerSession, 1).Select(codecs.Decode)
                .OfType<SessionCreated>());
            Assert.Equal("<workspace hidden>", created.WorkspaceDisplayPath);
            var rootAuthority = Assert.Single(ownerStore.ReadFrom(ownerSession, 1).Select(codecs.Decode)
                .OfType<WorkspaceRootEstablished>());
            Assert.True(WorkspaceRootIdentity.Verify(rootAuthority.CanonicalRoot, rootAuthority.DurableIdentity));

            ownerStore.AcquireSessionWriterLease(queryOnlySession);
            File.WriteAllText(stateFile, queryOnlySession + "\n-\n");
            observerStore = new SqliteEventStore(journal);
            observer = new OmniServer(observerStore, codecs, new InMemoryAuditSink(), stateFile,
                platformPathsFactory: () => paths);
            var observerBefore = observerStore.CurrentSequence(queryOnlySession);
            var observedSecurity = observer.Query("sessionSecurity", CancellationToken.None);
            Assert.Equal("null", observedSecurity!.Json);
            Assert.Equal(observerBefore, observerStore.CurrentSequence(queryOnlySession));
            Assert.Empty(observerStore.ReadFrom(queryOnlySession, 1).Select(codecs.Decode)
                .OfType<SessionSecurityPolicySet>());

            var orphanSession = SessionId.New();
            File.WriteAllText(stateFile, orphanSession + "\n" + RunId.New() + "\n");
            orphanStore = new SqliteEventStore(journal);
            orphanServer = new OmniServer(orphanStore, codecs, new InMemoryAuditSink(), stateFile,
                platformPathsFactory: () => paths);
            Assert.NotNull(orphanServer.LastRecoveryProblem());
            Assert.Equal("null", orphanServer.Query("sessionSecurity", CancellationToken.None)!.Json);
            Assert.Equal(0, orphanStore.CurrentSequence(orphanSession));
            Assert.Empty(orphanStore.ReadFrom(orphanSession, 1));
        }
        finally
        {
            owner?.ClosePersistentStore();
            observer?.ClosePersistentStore();
            orphanServer?.ClosePersistentStore();
            ownerStore?.Dispose();
            observerStore?.Dispose();
            orphanStore?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData(ReconciliationOutcome.Applied, "Accepted", false)]
    [InlineData(ReconciliationOutcome.NotApplied, "Accepted", false)]
    [InlineData(ReconciliationOutcome.Conflict, "Deferred", false)]
    [InlineData(ReconciliationOutcome.Unresolvable, "Deferred", false)]
    [InlineData(ReconciliationOutcome.Unresolvable, "Deferred", true)]
    public void Security_change_waits_for_uncertain_effect_and_uses_replayed_reconciliation(
        ReconciliationOutcome resolution, string expected, bool failedUnknown = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-session-security-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = SessionId.New();
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var stream = new EventStream(store, codecs, session);
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            stream.Append(new SessionCreated(session, WorkspaceId.Of(workspace).ToString(), workspace,
                ProfileId.New(), DateTimeOffset.UtcNow));
            var run = TestRun.Open(stream, session);
            var current = SessionSecurityPolicySet.FromPreset(session, 1, ModelPolicyCategory.ObserveOnly,
                SessionSecurityProvenance.Native, SessionSecurityPolicySource.UserDefault, DateTimeOffset.UtcNow);
            stream.Append(current, DurabilityClass.Barrier);

            var server = new OmniServer(store, codecs, new InMemoryAuditSink(),
                platformPathsFactory: () => new TestPaths(root));
            var resume = server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd",
                "conversation.resume") + "," + JsonObj.Field("sessionId", session.ToString()) + "}"),
                CancellationToken.None);
            Assert.Equal("ok", resume.Status);

            var call = ToolCallId.New();
            var turn = TurnId.New();
            stream.Append(new TurnStarted(turn, run.RootLane));
            using (ExecutionScope.Begin(new ExecutionScopeState(run.RunId, run.RootTask, run.RootLane, turn)))
            {
                stream.AppendBatch(new DomainEventPayload[]
                {
                    new ToolCallRequested(call, "pc", "fake.write", "{}"),
                    new ToolCallPrepared(call, "{}"),
                    new PermissionEvaluated(call, PermissionDecision.Allow, "[]", null),
                    new ToolCallAuthorized(call),
                    new ToolCallStarted(call, EffectClass.NonIdempotent, "{}"),
                    failedUnknown
                        ? new ToolCallFailed(call, "effect remains unknown", EffectOutcome.Unknown)
                        : new ToolCallEffectUnknown(call, EffectClass.NonIdempotent),
                }, DurabilityClass.Barrier);
                stream.Append(new TurnInterrupted(turn));
            }
            CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1));
            var fields = new Dictionary<string, string>
            {
                ["sessionId"] = session.ToString(),
                ["expectedRevision"] = "1",
                ["category"] = nameof(ModelPolicyCategory.ObserveOnly),
            };
            CommandAck TrySet() => server.SetSessionSecurityPolicy(
                WireEnvelope.Command(Ids.NewV7(), "{}"), fields);

            var beforeResolution = TrySet();
            Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Deferred, beforeResolution.Outcome?.Kind);

            if (failedUnknown)
            {
                // A failed/unknown outcome is not a valid source for ToolCallReconciled in the
                // canonical state machine. It remains uncertain and must keep the policy deferred.
                Assert.Equal(OmniCore.Protocol.RuntimeCommandOutcomeKind.Deferred, TrySet().Outcome?.Kind);
                Assert.Equal(1, store.ReadFrom(session, 1).Select(codecs.Decode)
                    .OfType<SessionSecurityPolicySet>().Last().PolicyRevision);
                CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1));
                return;
            }

            stream.Append(new ToolCallReconciled(call, resolution, "test recovery"), DurabilityClass.Barrier);
            CanonicalStateTracker.Replay(codecs, store.ReadFrom(session, 1));
            var afterResolution = TrySet();
            var shouldAccept = expected == "Accepted";
            Assert.Equal(shouldAccept ? OmniCore.Protocol.RuntimeCommandOutcomeKind.Accepted
                : OmniCore.Protocol.RuntimeCommandOutcomeKind.Deferred, afterResolution.Outcome?.Kind);
            Assert.Equal(shouldAccept ? 2 : 1,
                store.ReadFrom(session, 1).Select(codecs.Decode).OfType<SessionSecurityPolicySet>().Last().PolicyRevision);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
