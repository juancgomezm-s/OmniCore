using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class SessionSecurityPolicyTests
{
    [Fact]
    public void Full_catalog_sentinel_preserves_full_agent_authority_and_rejects_other_unbounded_categories()
    {
        var full = SessionSecurityPolicySet.FromPreset(SessionId.New(), 1, ModelPolicyCategory.FullAgent,
            SessionSecurityProvenance.Native, SessionSecurityPolicySource.UserSelection, DateTimeOffset.UtcNow);
        full.Validate();
        Assert.Equal(int.MaxValue, full.MaxVisibleTools);
        var narrowed = full with { MaxVisibleTools = 6, PolicyRevision = full.PolicyRevision + 1 };
        narrowed.Validate();
        Assert.True(full.IsMonotonicTightening(narrowed));
        Assert.False(narrowed.IsMonotonicTightening(full));
        Assert.Throws<InvalidDataException>(() => (full with { MaxVisibleTools = 65 }).Validate());
        Assert.Throws<InvalidDataException>(() => (full with { Category = ModelPolicyCategory.ObserveOnly }).Validate());
    }

    private sealed class TestPaths(string root) : IPlatformPaths
    {
        public string DataDirectory => Path.Combine(root, "data");
        public string UserDatabasePath => Path.Combine(DataDirectory, "user.db");
        public string ConfigDirectory => Path.Combine(root, "config");
        public string WorkspaceDirectory(string workspaceId) => Path.Combine(DataDirectory, "workspaces", workspaceId);
    }

    private static CommandAck Resume(OmniServer server, SessionId session) => server.SendUserAction(
        WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "conversation.resume")
            + "," + JsonObj.Field("sessionId", session.ToString()) + "}"), CancellationToken.None);

    [Theory]
    [InlineData(ModelPolicyCategory.ObserveOnly)]
    [InlineData(ModelPolicyCategory.PatchOnly)]
    [InlineData(ModelPolicyCategory.ScopedCoder)]
    [InlineData(ModelPolicyCategory.FullAgent)]
    public void Preset_snapshot_round_trips_as_a_durable_session_event(ModelPolicyCategory category)
    {
        var session = SessionId.New();
        var changedAt = new DateTimeOffset(2026, 10, 17, 12, 30, 0, TimeSpan.Zero);
        var policy = SessionSecurityPolicySet.FromPreset(session, 3, category,
            SessionSecurityProvenance.Native, SessionSecurityPolicySource.UserSelection, changedAt);
        var codecs = EventCodecs.Create();
        var json = codecs.CodecFor(policy.Type()).Encode(policy);
        var restored = Assert.IsType<SessionSecurityPolicySet>(
            codecs.CodecFor(policy.Type()).Decode(policy.Type(), json));

        Assert.Equal(json, codecs.CodecFor(restored.Type()).Encode(restored));
        Assert.Equal(policy.ToUserModelPolicy(), restored.ToUserModelPolicy());
        Assert.Equal(session, restored.SessionId);
        Assert.Equal(3, restored.PolicyRevision);
        Assert.Equal(changedAt, restored.ChangedAt);
        Assert.Equal(1, codecs.CurrentVersion(policy.Type()));
    }

    [Fact]
    public void Policy_preset_label_does_not_rebuild_or_amplify_the_snapshot()
    {
        var observed = SessionSecurityPolicySet.FromPreset(SessionId.New(), 1,
            ModelPolicyCategory.ObserveOnly, SessionSecurityProvenance.Native,
            SessionSecurityPolicySource.UserDefault, DateTimeOffset.UtcNow);
        var relabeled = observed with { PresetId = nameof(ModelPolicyCategory.FullAgent) };

        var originalModelPolicy = observed.ToUserModelPolicy();
        var relabeledModelPolicy = relabeled.ToUserModelPolicy();

        Assert.Equal(originalModelPolicy.ToolPolicy.CapabilityCeiling.Order(),
            relabeledModelPolicy.ToolPolicy.CapabilityCeiling.Order());
        Assert.Equal(originalModelPolicy.MutationPolicy.Mode, relabeledModelPolicy.MutationPolicy.Mode);
        Assert.Equal(originalModelPolicy.MutationPolicy.MaxFilesPerTurn,
            relabeledModelPolicy.MutationPolicy.MaxFilesPerTurn);
        Assert.Equal(originalModelPolicy.MaxToolCallsPerTurn, relabeledModelPolicy.MaxToolCallsPerTurn);
        Assert.Equal("session-security:" + nameof(ModelPolicyCategory.ObserveOnly), originalModelPolicy.Source);
        Assert.Equal("session-security:" + nameof(ModelPolicyCategory.FullAgent), relabeledModelPolicy.Source);
    }

    [Fact]
    public void Preset_factory_rejects_nonpositive_revision_and_custom_category()
    {
        var session = SessionId.New();
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentOutOfRangeException>(() => SessionSecurityPolicySet.FromPreset(session, 0,
            ModelPolicyCategory.ObserveOnly, SessionSecurityProvenance.Native,
            SessionSecurityPolicySource.UserDefault, now));
        Assert.Throws<ArgumentException>(() => SessionSecurityPolicySet.FromPreset(session, 1,
            ModelPolicyCategory.Custom, SessionSecurityProvenance.Native,
            SessionSecurityPolicySource.UserSelection, now));
    }

    [Fact]
    public void Durable_policy_validation_rejects_unknown_or_duplicate_capabilities_and_nonfinite_limits()
    {
        var valid = SessionSecurityPolicySet.FromPreset(SessionId.New(), 1,
            ModelPolicyCategory.ScopedCoder, SessionSecurityProvenance.Native,
            SessionSecurityPolicySource.UserSelection, DateTimeOffset.UtcNow);

        Assert.Throws<InvalidDataException>(() => (valid with
        {
            CapabilityCeiling = [.. valid.CapabilityCeiling, valid.CapabilityCeiling[0]]
        }).Validate());
        Assert.Throws<InvalidDataException>(() => (valid with
        {
            CapabilityCeiling = [.. valid.CapabilityCeiling, (ModelToolCapability)int.MaxValue]
        }).Validate());
        Assert.Throws<InvalidDataException>(() => (valid with { MaxRewriteRatio = double.NaN }).Validate());
        Assert.Throws<InvalidDataException>(() => (valid with { PolicyRevision = 0 }).Validate());
    }

    [Theory]
    [InlineData((SessionSecurityProvenance)int.MaxValue, SessionSecurityPolicySource.UserSelection)]
    [InlineData(SessionSecurityProvenance.Native, (SessionSecurityPolicySource)int.MaxValue)]
    public void Preset_factory_rejects_unknown_provenance_and_source(
        SessionSecurityProvenance provenance, SessionSecurityPolicySource source)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionSecurityPolicySet.FromPreset(SessionId.New(), 1,
            ModelPolicyCategory.ObserveOnly, provenance, source, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(SessionSecurityProvenance.Native, SessionSecurityPolicySource.LegacyMigration)]
    [InlineData(SessionSecurityProvenance.Native, SessionSecurityPolicySource.ImportReview)]
    [InlineData(SessionSecurityProvenance.LegacyMigrated, SessionSecurityPolicySource.UserDefault)]
    [InlineData(SessionSecurityProvenance.Imported, SessionSecurityPolicySource.UserDefault)]
    public void Preset_factory_rejects_an_initial_source_that_conflicts_with_provenance(
        SessionSecurityProvenance provenance, SessionSecurityPolicySource source)
    {
        Assert.Throws<ArgumentException>(() => SessionSecurityPolicySet.FromPreset(SessionId.New(), 1,
            ModelPolicyCategory.ObserveOnly, provenance, source, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(SessionSecurityProvenance.Imported)]
    [InlineData(SessionSecurityProvenance.LegacyMigrated)]
    public void User_selection_preserves_existing_imported_or_migrated_provenance(
        SessionSecurityProvenance provenance)
    {
        var source = provenance == SessionSecurityProvenance.Imported
            ? SessionSecurityPolicySource.ImportReview
            : SessionSecurityPolicySource.LegacyMigration;
        var session = SessionId.New();
        var established = SessionSecurityPolicySet.FromPreset(session, 1,
            ModelPolicyCategory.ObserveOnly, provenance, source, DateTimeOffset.UtcNow);
        established.Validate();
        var selected = SessionSecurityPolicySet.FromPreset(established.SessionId, established.PolicyRevision + 1,
            ModelPolicyCategory.PatchOnly, provenance, SessionSecurityPolicySource.UserSelection,
            DateTimeOffset.UtcNow);

        selected.Validate();
        Assert.Equal(provenance, selected.Provenance);
        Assert.Equal(SessionSecurityPolicySource.UserSelection, selected.Source);
    }

    [Fact]
    public void Event_stream_rejects_policy_for_another_session_before_changing_either_journal()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var targetSession = SessionId.New();
        var foreignSession = SessionId.New();
        var stream = new EventStream(store, codecs, targetSession);
        var current = SessionSecurityPolicySet.FromPreset(targetSession, 1,
            ModelPolicyCategory.ObserveOnly, SessionSecurityProvenance.Native,
            SessionSecurityPolicySource.UserDefault, DateTimeOffset.UtcNow);
        stream.Append(current);
        var targetBefore = store.ReadFrom(targetSession, 1);
        var foreignBefore = store.ReadFrom(foreignSession, 1);
        var mismatched = SessionSecurityPolicySet.FromPreset(foreignSession, 2,
            ModelPolicyCategory.PatchOnly, SessionSecurityProvenance.Native,
            SessionSecurityPolicySource.UserSelection, DateTimeOffset.UtcNow);

        Assert.Throws<InvalidDataException>(() => stream.Append(mismatched));

        Assert.Equal(1, store.CurrentSequence(targetSession));
        Assert.Equal(0, store.CurrentSequence(foreignSession));
        Assert.Equal(targetBefore.Select(evt => evt.EventId),
            store.ReadFrom(targetSession, 1).Select(evt => evt.EventId));
        Assert.Equal(foreignBefore.Select(evt => evt.EventId),
            store.ReadFrom(foreignSession, 1).Select(evt => evt.EventId));
    }

    [Fact]
    public async Task Concurrent_resume_of_legacy_session_persists_one_conservative_migration()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var workspace = Path.Combine(Path.GetTempPath(), "omni-security-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var stream = new EventStream(store, codecs, session);
            stream.Append(new SessionCreated(session, WorkspaceId.Of(workspace).ToString(), workspace,
                ProfileId.New(), DateTimeOffset.UtcNow));
            TestRun.Open(stream, session);

            OmniServer NewServer() => new(store, codecs, new InMemoryAuditSink(),
                platformPathsFactory: () => new TestPaths(workspace));
            var firstServer = NewServer();
            var secondServer = NewServer();
            using var start = new ManualResetEventSlim(false);
            var first = Task.Run(() => { start.Wait(); return Resume(firstServer, session); });
            var second = Task.Run(() => { start.Wait(); return Resume(secondServer, session); });
            start.Set();
            var acknowledgements = await Task.WhenAll(first, second);

            Assert.All(acknowledgements, ack => Assert.Equal("ok", ack.Status));
            var events = store.ReadFrom(session, 1).Select(codecs.Decode).OfType<SessionSecurityPolicySet>().ToArray();
            var policy = Assert.Single(events);
            Assert.Equal(1, policy.PolicyRevision);
            Assert.Equal(SessionSecurityProvenance.LegacyMigrated, policy.Provenance);
            Assert.Equal(SessionSecurityPolicySource.LegacyMigration, policy.Source);
            Assert.Equal(ModelPolicyCategory.ObserveOnly, policy.Category);
            Assert.DoesNotContain(events, item => item.Provenance == SessionSecurityProvenance.Native);

            using var firstJson = System.Text.Json.JsonDocument.Parse(
                firstServer.Query("sessionSecurity", CancellationToken.None)!.Json);
            using var secondJson = System.Text.Json.JsonDocument.Parse(
                secondServer.Query("sessionSecurity", CancellationToken.None)!.Json);
            Assert.Equal(firstJson.RootElement.GetRawText(), secondJson.RootElement.GetRawText());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task Concurrent_event_stream_migration_append_is_idempotent_for_shared_store_and_session()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var session = SessionId.New();
        var firstStream = new EventStream(store, codecs, session);
        var secondStream = new EventStream(store, codecs, session);
        SessionSecurityPolicySet Migration() => SessionSecurityPolicySet.FromPreset(session, 1,
            ModelPolicyCategory.ObserveOnly, SessionSecurityProvenance.LegacyMigrated,
            SessionSecurityPolicySource.LegacyMigration, DateTimeOffset.UtcNow);
        var firstMigration = Migration();
        var secondMigration = Migration();

        using var start = new ManualResetEventSlim(false);
        var first = Task.Run(() => { start.Wait(); return firstStream.AppendIfNoEventsOfType(firstMigration.Type(),
            firstMigration, DurabilityClass.Barrier); });
        var second = Task.Run(() => { start.Wait(); return secondStream.AppendIfNoEventsOfType(secondMigration.Type(),
            secondMigration, DurabilityClass.Barrier); });
        start.Set();
        var appended = await Task.WhenAll(first, second);

        Assert.Single(appended, value => value);
        Assert.Single(appended, value => !value);
        Assert.Equal(1, store.CurrentSequence(session));
        var policy = Assert.IsType<SessionSecurityPolicySet>(codecs.Decode(Assert.Single(store.ReadFrom(session, 1))));
        Assert.Equal(1, policy.PolicyRevision);
        Assert.Equal(SessionSecurityProvenance.LegacyMigrated, policy.Provenance);
    }
}

