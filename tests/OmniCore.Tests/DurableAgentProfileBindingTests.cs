namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Tools;
using OmniCore.Security;

/// <summary>Private SQLite and configuration fixtures; no authenticated provider qualification.</summary>
public sealed class DurableAgentProfileBindingTests
{
    private static readonly ProfileId Id = ProfileId.Parse("0199a000-0000-7000-8000-000000000001");
    private static AgentProfile Profile(long revision = 1, string name = "explorer") => new(Id, name, revision,
        PermissionScope.With(["**"], [], [], [], [], false), [new ToolId("filesystem.read")]);
    private static AgentProfileConfiguration.Loaded Selection(AgentProfile? profile) =>
        new(new AgentProfileRegistry(profile is null ? [] : [profile]), profile);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Explicit_empty_or_whitespace_argv_is_preserved_and_matches_only_that_complete_argument(string argument)
    {
        var yaml = Yaml.Replace("process: []", "process:\n        - executablePattern: fixture-exe\n          argvPatterns: ["
            + System.Text.Json.JsonSerializer.Serialize(argument) + "]\n          decision: Allow", StringComparison.Ordinal);
        var profile = AgentProfileConfiguration.Load(yaml, ScopeLevel.User).Find(Id)!;
        Assert.Equal(argument, Assert.Single(Assert.Single(profile.PermissionCeiling.Process).ArgvPatterns));
        WithRoot(root =>
        {
            var policy = new AgentProfilePermissionPolicy(new ScriptedPermissionPolicy([]), profile,
                new PathBoundaryValidator(), root);
            static ToolIntent Intent(string[] argv) => new(ToolCallId.New(), new ToolId("fixture.process"), "{}",
                EffectClass.None, new ResourceClaims([], [], [],
                    new ProcessClaim("fixture-exe", argv, "Observational"), []), ToolRisk.Low, null);
            Assert.Equal(PermissionDecision.Allow, policy.Evaluate(Intent([argument])).Final);
            Assert.Equal(PermissionDecision.Deny, policy.Evaluate(Intent([])).Final);
            Assert.Equal(PermissionDecision.Deny, policy.Evaluate(Intent([argument, "extra"])).Final);
            Assert.Equal(PermissionDecision.Deny, policy.Evaluate(Intent(["other"])).Final);
        });
    }

    [Fact]
    public void Lane_v2_roundtrips_exact_binding_and_v1_upcasts_without_inventing_a_definition()
    {
        var codecs = EventCodecs.Create();
        var lane = new LaneCreated(LaneId.New(), TaskId.New(), Id, 1, AgentProfileFingerprint.Hash(Profile()));
        var codec = codecs.CodecFor(lane.Type());
        Assert.Equal(2, codecs.CurrentVersion(lane.Type()));
        Assert.Equal(lane, codec.Decode(lane.Type(), codec.Encode(lane)));
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(codec.Encode(lane))!.AsObject();
        legacy.Remove("AgentProfileRevision");
        legacy.Remove("AgentProfileHash");
        var evt = DomainEvent.Create(SessionId.New(), lane.Type(), 1, null, RunId.New(), null,
            lane.TaskId, lane.LaneId, null, null, null, [], legacy.ToJsonString());
        var restored = Assert.IsType<LaneCreated>(codecs.Decode(evt));
        Assert.Equal(Id, restored.AgentProfile);
        Assert.Null(restored.AgentProfileRevision);
        Assert.Null(restored.AgentProfileHash);
    }

    [Theory]
    [InlineData(0L, true)]
    [InlineData(-1L, true)]
    [InlineData(1L, false)]
    [InlineData(null, true)]
    public void Incomplete_or_invalid_profile_binding_is_rejected_before_creating_lane(long? revision, bool hasHash)
    {
        var codecs = EventCodecs.Create();
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var run = TestRun.Open(store, session);
        var prefix = store.ReadFrom(session, 1).TakeWhile(evt => codecs.Decode(evt) is not LaneCreated).ToArray();
        var tracker = CanonicalStateTracker.Replay(codecs, prefix);
        var invalid = new LaneCreated(run.RootLane, run.RootTask, Id, revision,
            hasHash ? AgentProfileFingerprint.Hash(Profile()) : null);
        Assert.Throws<InvalidStateTransitionException>(() => tracker.Apply(invalid));
        // A failed candidate must leave the same identity available for a valid Lane.
        tracker.Apply(new LaneCreated(run.RootLane, run.RootTask, Id, 1, AgentProfileFingerprint.Hash(Profile())));
    }

    [Fact]
    public void User_selection_is_explicit_and_missing_identity_is_rejected()
    {
        Assert.Null(AgentProfileConfiguration.LoadSelection(Yaml, ScopeLevel.User).DefaultProfile);
        var selected = AgentProfileConfiguration.LoadSelection("defaultProfile: " + Id + "\n" + Yaml, ScopeLevel.User);
        Assert.Equal(Id, selected.DefaultProfile!.Id);
        Assert.Throws<InvalidDataException>(() => AgentProfileConfiguration.LoadSelection(
            "defaultProfile: " + ProfileId.New() + "\n" + Yaml, ScopeLevel.User));
        Assert.Throws<ArgumentException>(() => AgentProfileConfiguration.LoadSelection(Yaml, ScopeLevel.Workspace));
    }

    [Fact]
    public void Normal_loader_reads_only_User_file_and_absence_does_not_promote_workspace_configuration()
    {
        WithRoot(root =>
        {
            var config = Path.Combine(root, "user-config");
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(config);
            Directory.CreateDirectory(workspace);
            var yaml = "defaultProfile: " + Id + "\n" + Yaml;
            File.WriteAllText(Path.Combine(workspace, "agent-profiles.yaml"), yaml);
            var paths = new DefaultPlatformPaths(Path.Combine(root, "data"), config);
            Assert.Null(OmniHost.LoadAgentProfiles(paths).DefaultProfile);
            File.WriteAllText(Path.Combine(config, "agent-profiles.yaml"), yaml);
            Assert.Equal(Id, OmniHost.LoadAgentProfiles(paths).DefaultProfile!.Id);
            File.WriteAllText(Path.Combine(config, "agent-profiles.yaml"), yaml.Replace(
                "      reads: [\"**\"]", "", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => OmniHost.LoadAgentProfiles(paths));
        });
    }

    [Fact]
    public void New_runs_reuse_configuration_identity_and_bind_exact_fingerprint_content()
    {
        WithRoot(root =>
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var server = new OmniServer(store, codecs, new InMemoryAuditSink());
            var profile = Profile();
            server.ConfigureAgentProfiles(Selection(profile));
            var first = Start(server, root);
            var second = Start(server, root);
            Assert.NotEqual(first.Session, second.Session);
            foreach (var started in new[] { first, second })
            {
                var lane = Assert.Single(store.ReadFrom(started.Session, 1).Select(codecs.Decode).OfType<LaneCreated>());
                Assert.Equal(Id, lane.AgentProfile);
                Assert.Equal(1, lane.AgentProfileRevision);
                Assert.Equal(AgentProfileFingerprint.Hash(profile), lane.AgentProfileHash);
                Assert.Same(profile, server.ResolveLaneAgentProfile(started.Session, started.Run, started.Lane));
                var catalog = new FakeCatalog();
                var fingerprint = RuntimeFingerprintFactory.WithTurnConfiguration(
                    new ExecutionFingerprint("m", "h", "t", "c", "o", "b"), catalog, catalog.Definitions(),
                    "fixture", null, agentProfile: Id, resolvedAgentProfile: profile);
                Assert.Equal(lane.AgentProfileHash, Assert.Single(fingerprint.Components,
                    component => component.Name == "agent.profile").Hash);
            }
            Assert.Throws<InvalidOperationException>(() => server.ResolveLaneAgentProfile(first.Session, second.Run, first.Lane));
        });
    }

    [Theory]
    [InlineData("same")]
    [InlineData("revision")]
    [InlineData("content")]
    [InlineData("removed")]
    public void Reopened_lane_requires_its_original_configuration_not_a_permissive_fallback(string change)
    {
        WithRoot(root =>
        {
            var path = Path.Combine(root, "journal.db");
            (SessionId Session, RunId Run, LaneId Lane) started;
            var codecs = EventCodecs.Create();
            var store = new SqliteEventStore(path);
            try
            {
                var server = new OmniServer(store, codecs, new InMemoryAuditSink());
                server.ConfigureAgentProfiles(Selection(Profile()));
                started = Start(server, root);
            }
            finally { Close(store); }
            var reopened = new SqliteEventStore(path);
            try
            {
            var restored = new OmniServer(reopened, codecs, new InMemoryAuditSink());
            restored.ConfigureAgentProfiles(Selection(change switch
            {
                "revision" => Profile(2), "content" => Profile(name: "changed"), "removed" => null, _ => Profile(),
            }));
            var sequence = reopened.CurrentSequence(started.Session);
            if (change == "same")
                Assert.Equal(Id, restored.ResolveLaneAgentProfile(started.Session, started.Run, started.Lane)!.Id);
            else
                Assert.Throws<InvalidOperationException>(() => restored.ResolveLaneAgentProfile(started.Session, started.Run, started.Lane));
            Assert.Equal(sequence, reopened.CurrentSequence(started.Session));
            Assert.Empty(reopened.ReadFrom(started.Session, 1).Select(codecs.Decode).OfType<TurnStarted>());
            }
            finally { Close(reopened); }
        });
    }

    [Fact]
    public void Legacy_lane_does_not_acquire_a_new_User_default_retroactively()
    {
        WithRoot(root =>
        {
            var store = new InMemoryEventStore();
            var server = new OmniServer(store, EventCodecs.Create(), new InMemoryAuditSink());
            var started = Start(server, root);
            server.ConfigureAgentProfiles(Selection(Profile()));
            Assert.Null(server.ResolveLaneAgentProfile(started.Session, started.Run, started.Lane));
        });
    }

    private static (SessionId Session, RunId Run, LaneId Lane) Start(OmniServer server, string root)
    {
        var ack = server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"act\",\"objective\":\"inspect fixture\",\"workspace\":"
            + System.Text.Json.JsonSerializer.Serialize(root) + "}"), CancellationToken.None);
        Assert.Null(ack.Error);
        Assert.Equal("ok", ack.Status);
        return (server.LastSessionId()!, server.LastRunId()!, server.LastLaneId()!);
    }

    private static void WithRoot(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-profile-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { Directory.Delete(root, true); }
    }

    private static void Close(SqliteEventStore store)
    {
        store.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool((Microsoft.Data.Sqlite.SqliteConnection)store.Connection);
        store.Connection.Dispose();
    }

    private const string Yaml = """
        agentProfiles:
          explorer:
            id: 0199a000-0000-7000-8000-000000000001
            revision: 1
            permissions:
              reads: ["**"]
              writes: []
              process: []
              network: []
              secrets: []
              allowShell: false
            preferredTools: [filesystem.read]
        """;
}
