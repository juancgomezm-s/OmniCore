using System.Text.Json;
using Microsoft.Data.Sqlite;
using OmniCore.Client;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Extensions;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed partial class CompanionPetTests
{
    [Fact]
    public void Visual_preferences_persist_per_client_without_changing_identity_or_evolution()
    {
        using var fx = new Fixture(); fx.Hatch();
        var original = fx.Pet.Status();
        foreach (var mode in new[] { "graphics", "unicode", "text", "hidden", "auto" })
        {
            var updated = fx.Pet.Act(new("preferences", RenderMode: mode), "mode/" + mode);
            Assert.Equal(mode, updated.Preferences.RenderMode);
            Assert.Equal(original.Species, updated.Species); Assert.Equal(original.Xp, updated.Xp);
            Assert.Equal(original.Interactions, updated.Interactions); Assert.Equal(original.Stage, updated.Stage);
            Assert.Equal("auto", fx.Pet.Status("omnicoder").Preferences.RenderMode);
            Assert.Equal(mode, new CompanionPetService(fx.Root).Status().Preferences.RenderMode);
        }
        Assert.Throws<ArgumentException>(() => fx.Pet.Act(new("preferences", RenderMode: "invalid"), "bad"));
        var legacy = CompanionPetProtocol.Decode(CompanionPetProtocol.Encode(original).Replace(",\"renderMode\":\"auto\"", ""));
        Assert.Equal("auto", legacy!.Preferences.RenderMode);
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "omni-companion-" + Guid.NewGuid().ToString("N"));
        internal readonly InMemoryEventStore Journal = new();
        internal readonly OmniCore.Abstractions.IEventCodecRegistry Codecs = EventCodecs.Create();
        internal readonly SessionId Session = SessionId.New();
        internal readonly RunId Run = RunId.New();
        internal DateTimeOffset Now = DateTimeOffset.UtcNow.AddMinutes(-1);
        internal CompanionPetService Pet;
        internal Fixture(int hatchAfter = 20, int seed = 73, int cooldown = 0, int limit = 40)
        {
            Directory.CreateDirectory(Root); var random = new Random(seed);
            Pet = new(Root, new(hatchAfter, cooldown, limit), random.NextDouble, () => Now);
        }
        internal void Discover() => Pet.Act(new("discover"), "discover");
        internal void Add(DomainEventPayload payload, SessionId? session = null, CausationId? causation = null)
        {
            var target = session ?? Session;
            var evt = DomainEvent.Stored(EventId.New(), target, 0, payload.Type(), payload.SchemaVersion(),
                Now.AddSeconds(1), causation, Run, Run, null, null, null, null, null, [], Codecs.CodecFor(payload.Type()).Encode(payload));
            Journal.Append(target, evt, DurabilityClass.Barrier, CancellationToken.None);
        }
        internal void Message(string text, string? origin = null, SessionId? session = null) =>
            Add(new UserInputReceived(Run, JsonSerializer.Serialize(text), null, origin), session);
        internal void Sync(SessionId? session = null) => Pet.Synchronize(Journal, Codecs, session ?? Session);
        internal void Hatch()
        {
            Discover();
            for (var i = 0; i < 20 && Pet.Status().Species is null; i++) { Message("Explícame cómo funciona este sistema"); Sync(); }
            Assert.NotNull(Pet.Status().Species);
        }
        internal void Result(ActivityEvidence? evidence, bool failed = false, EffectOutcome outcome = EffectOutcome.Partial,
            ToolErrorCode? error = null, string tool = "process.exec", string source = "BuiltIn")
        {
            Now = Now.AddMinutes(1); var call = ToolCallId.New();
            Add(new ToolCallStarted(call, EffectClass.NonIdempotent, null)
                { Attribution = new(tool, source, null, "1", null) });
            Add(failed ? new ToolCallFailed(call, "result", outcome, error ?? ToolErrorCode.ProcessFailure) { ActivityEvidence = evidence }
                : new ToolCallSucceeded(call, "{}") { ActivityEvidence = evidence }); Sync();
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
    [Fact]
    public void Discovery_is_idempotent_and_profile_persistence_is_independent_of_clients()
    {
        using var fx = new Fixture(); Assert.False(fx.Pet.Status().Discovered);
        Assert.False(Directory.Exists(Path.Combine(fx.Root, "companion")));
        fx.Discover(); var first = fx.Pet.Status(); fx.Discover();
        Assert.Equal(first, fx.Pet.Status()); Assert.Equal("egg", first.Stage);
        var reopened = new CompanionPetService(fx.Root); Assert.Equal(first.Species, reopened.Status().Species);
        reopened.Act(new("hide", ClientId: "omnicoder"), "hide");
        Assert.False(fx.Pet.Status("omnicoder").Preferences.Visible);
        Assert.True(fx.Pet.Status("terminal").Preferences.Visible);
        reopened.Act(new("rename", "Luna"), "rename"); Assert.Equal("Luna", fx.Pet.Status().Name);
        Assert.Throws<ArgumentException>(() => reopened.Act(new("rename", "bad\u001b[2J"), "invalid"));
        using var other = new Fixture(); Assert.False(other.Pet.Status().Discovered);
    }
    [Theory]
    [InlineData("Ya te dije que lo hagas bien de una vez", "ox")]
    [InlineData("Busca una solución alternativa para este reto", "fox")]
    [InlineData("Explícame cómo funciona este sistema", "owl")]
    [InlineData("Continúa la construcción hasta completar este trabajo", "beaver")]
    [InlineData("Explora por tu cuenta usando tu criterio", "cat")]
    [InlineData("Trabajemos juntos con calma y sin prisa", "capybara")]
    public void Explicit_rules_normalize_interaction_style(string text, string species)
    {
        var result = new CompanionInteractionRules().Classify(text);
        Assert.True(result.Valid); Assert.Equal(species, result.Signal);
    }
    [Theory]
    [InlineData("````cs\nYa te dije que lo hagas bien\n````")]
    [InlineData("> Ya te dije que lo hagas bien")]
    [InlineData("\"Ya te dije que lo hagas bien\"")]
    [InlineData("`Ya te dije que lo hagas bien`")]
    [InlineData("[ERROR] Ya te dije que lo hagas bien")]
    [InlineData("    Ya te dije que lo hagas bien")]
    [InlineData("<tool_result>\nYa te dije que lo hagas bien\n</tool_result>")]
    [InlineData("hazlo")]
    public void Code_quotes_logs_and_short_orders_do_not_contribute(string text)
    {
        var result = new CompanionInteractionRules().Classify(text);
        Assert.False(result.Valid); Assert.Null(result.Signal);
    }
    [Fact]
    public void Ambiguity_does_not_add_signals_and_all_species_keep_a_floor()
    {
        Assert.Null(new CompanionInteractionRules().Classify("Por favor explica cómo funciona este sistema").Signal);
        var weights = CompanionInteractionRules.Weights(new Dictionary<string, int> { ["ox"] = 20 }, 20);
        Assert.Equal(.5, weights["ox"] / weights.Values.Sum());
        Assert.All(weights.Values, weight => Assert.True(weight / weights.Values.Sum() >= .1));
        Assert.Equal(6, Enumerable.Range(0, 1000).Select(i => CompanionInteractionRules.Draw(weights, i / 1000d)).Distinct().Count());
        var single = CompanionInteractionRules.Weights(new Dictionary<string, int> { ["ox"] = 1 }, 1);
        Assert.All(single.Values, weight => Assert.Equal(1, weight));
    }
    [Fact]
    public void Hatching_is_seeded_once_and_ignores_messages_before_discovery_and_tool_origins()
    {
        using var fx = new Fixture(2); fx.Message("Ya te dije que lo hagas bien"); fx.Now = fx.Now.AddSeconds(2);
        fx.Discover(); fx.Message("Ya te dije que lo hagas bien", "InteractionResponse(Questionnaire)"); fx.Sync();
        Assert.Equal(0, fx.Pet.Status().Interactions);
        fx.Message("Explícame cómo funciona este sistema"); fx.Sync();
        Assert.Null(fx.Pet.Status().Species); fx.Message("Explícame cómo funciona este sistema"); fx.Sync();
        var hatched = fx.Pet.Status(); Assert.Equal("baby", hatched.Stage); Assert.Equal(1, hatched.Level); Assert.Equal(0, hatched.Xp);
        using var sameSeed = new Fixture(2); sameSeed.Hatch();
        Assert.Equal(hatched.Species, sameSeed.Pet.Status().Species); Assert.Equal(hatched.Variant, sameSeed.Pet.Status().Variant);
        fx.Pet = new(fx.Root, random: () => throw new Exception("must never draw again"), clock: () => fx.Now);
        fx.Message("Ya te dije que lo hagas bien"); fx.Sync(); fx.Sync();
        Assert.Equal(hatched.Species, fx.Pet.Status().Species); Assert.Equal(hatched.Interactions, fx.Pet.Status().Interactions);
        var dbPath = Path.Combine(fx.Root, "companion", "pet.db");
        using var db = new SqliteConnection("Data Source=" + dbPath); db.Open(); using var read = db.CreateCommand();
        read.CommandText = "SELECT json FROM pet"; var json = (string)read.ExecuteScalar()!;
        Assert.DoesNotContain("Explícame", json); Assert.DoesNotContain("Ya te dije", json);
    }
    [Fact]
    public async System.Threading.Tasks.Task Concurrent_sessions_and_recovery_cannot_repeat_messages_or_rewards()
    {
        using var fx = new Fixture(100); fx.Discover();
        var sessions = Enumerable.Range(0, 4).Select(_ => SessionId.New()).ToArray();
        foreach (var session in sessions) for (var i = 0; i < 10; i++) fx.Message("Explícame cómo funciona este sistema", session: session);
        await System.Threading.Tasks.Task.WhenAll(sessions.Select(session => System.Threading.Tasks.Task.Run(() =>
        {
            var service = new CompanionPetService(fx.Root, new(100), clock: () => fx.Now);
            service.Synchronize(fx.Journal, fx.Codecs, session); service.Synchronize(fx.Journal, fx.Codecs, session);
        })));
        Assert.Equal(40, fx.Pet.Status().Interactions);
    }
    [Fact]
    public void Rewards_require_verified_outcomes_and_are_deduplicated_capped_and_separate_from_petting()
    {
        using var fx = new Fixture(1, limit: 2); fx.Hatch();
        fx.Result(null, failed: true, outcome: EffectOutcome.None); // launch failure
        fx.Result(null, failed: true, outcome: EffectOutcome.Unknown, error: ToolErrorCode.Cancellation);
        fx.Result(new("test", false, true, 1, 0, 2), true);
        fx.Result(new("test", true, false, 1, 0, 2), true);
        fx.Result(new("test", true, true, 0, 3), tool: "fake.test");
        fx.Result(new("test", true, true, 0, 3), source: "Extension");
        Assert.Equal(0, fx.Pet.Status().Xp);
        fx.Result(new("test", true, true, 0, 3)); Assert.Equal(10, fx.Pet.Status().Xp);
        fx.Sync(); Assert.Equal(10, fx.Pet.Status().Xp);
        fx.Result(new("test", true, true, 1, 0, 2), true); Assert.Equal(12, fx.Pet.Status().Xp);
        fx.Result(new("test", true, true, 0, 2)); fx.Result(new("test", true, true, 0, 2));
        Assert.Equal(22, fx.Pet.Status().Xp); // daily cap per kind
        var mood = fx.Pet.Status().Mood;
        fx.Pet.Act(new("pet"), "pet1"); var revision = fx.Pet.Status().Revision;
        fx.Pet.Act(new("pet"), "pet2"); Assert.Equal(revision, fx.Pet.Status().Revision);
        Assert.Equal(22, fx.Pet.Status().Xp); Assert.Equal(mood, fx.Pet.Status().Mood);
        fx.Now = fx.Now.AddDays(10); Assert.Equal(30, fx.Pet.Status().Mood);
    }
    [Fact]
    public void Fresh_machine_readable_test_reports_distinguish_assertions_from_startup_errors()
    {
        using var fx = new Fixture(); var path = Path.Combine(fx.Root, "result.xml");
        var args = new[] { "--junitxml=" + path };
        var probe = ProcessActivityProbe.Start("pytest", args, fx.Root, fx.Root);
        File.WriteAllText(path, "<testsuite><testcase/><testcase><failure/></testcase></testsuite>");
        Assert.Equal(new ActivityEvidence("test", true, true, 1, 1, 1), probe.Finish(1));
        var stale = ProcessActivityProbe.Start("pytest", args, fx.Root, fx.Root); Assert.Null(stale.Finish(0));
        var startup = ProcessActivityProbe.Start("pytest", args, fx.Root, fx.Root);
        File.WriteAllText(path, "<testsuite><testcase><error/></testcase></testsuite>"); Assert.Null(startup.Finish(1));
        Assert.Null(startup.Finish(2));
    }
    [Fact]
    public async System.Threading.Tasks.Task Core_Git_reader_verifies_real_commits_including_packed_history_and_linked_worktrees()
    {
        using var fx = new Fixture();
        void Git(params string[] args)
        {
            var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = fx.Root,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(10000), "temporary fixture Git completed"); Assert.True(process.ExitCode == 0, output + error);
        }
        Git("init"); Git("config", "user.name", "Companion Fixture"); Git("config", "user.email", "fixture@example.invalid");
        Git("config", "commit.gpgsign", "false"); Git("config", "core.hooksPath", Path.Combine(fx.Root, "no-hooks"));
        var reader = new GitWorktreeStore(SystemProcessRuntime.Instance());
        var probe = ProcessActivityProbe.Start("git", ["commit", "-m", "second"], fx.Root, fx.Root);
        Git("commit", "--allow-empty", "-m", "empty initial");
        Assert.Null(probe.Finish(0, null, await reader.ReadActivityAsync(fx.Root, CancellationToken.None)));
        File.WriteAllText(Path.Combine(fx.Root, "work.txt"), "one"); Git("add", "work.txt"); Git("commit", "-m", "first");
        Git("gc", "--prune=now");
        var before = await reader.ReadActivityAsync(fx.Root, CancellationToken.None); Assert.NotNull(before);
        File.WriteAllText(Path.Combine(fx.Root, "work.txt"), "two"); Git("add", "work.txt"); Git("commit", "-m", "second");
        var after = await reader.ReadActivityAsync(fx.Root, CancellationToken.None);
        Assert.Equal("commit", probe.Finish(0, before, after)?.Kind);
        Assert.Null(probe.Finish(0, after, after));
        Assert.Null(ProcessActivityProbe.Start("git", ["commit", "--dry-run"], fx.Root, fx.Root).Finish(0, before, after));
        Git("commit", "--allow-empty", "-m", "empty");
        var empty = await reader.ReadActivityAsync(fx.Root, CancellationToken.None); Assert.Null(probe.Finish(0, after, empty));
        var linked = Path.Combine(fx.Root, "linked"); Git("worktree", "add", "-b", "linked-fixture", linked);
        var linkedState = await reader.ReadActivityAsync(linked, CancellationToken.None); Assert.Equal(empty?.Revision, linkedState?.Revision);
    }
    [Fact]
    public void Queued_messages_are_counted_once_at_receipt_and_not_again_at_promotion()
    {
        using var fx = new Fixture(10); fx.Discover(); var id = FollowUpId.New(); var lane = LaneId.New();
        const string input = "\"Explícame cómo funciona este sistema\"";
        fx.Add(new FollowUpQueued(id, fx.Run, lane, null, input, null)); fx.Sync(); Assert.Equal(1, fx.Pet.Status().Interactions);
        fx.Add(new FollowUpPromoted(id, fx.Run, lane, TurnId.New())); fx.Add(new UserInputReceived(fx.Run, input, null));
        fx.Sync(); Assert.Equal(1, fx.Pet.Status().Interactions);
    }
    [Fact]
    public void Repeated_message_command_identity_is_deduplicated_even_with_distinct_event_ids()
    {
        using var fx = new Fixture(); fx.Discover(); var command = new CommandCausation(CommandId.New());
        var input = new UserInputReceived(fx.Run, "\"Explícame cómo funciona este sistema\"", null);
        fx.Add(input, causation: command); fx.Add(input, causation: command); fx.Sync();
        Assert.Equal(1, fx.Pet.Status().Interactions);
    }
    [Fact]
    public void Effective_filesystem_mutation_flows_from_the_real_executor_to_XP_once()
    {
        using var fx = new Fixture(1); fx.Hatch();
        var session = SessionId.New(); _ = TestRun.Open(fx.Journal, session);
        var artifacts = new FileArtifactStore(Path.Combine(fx.Root, "artifacts"));
        var tool = new FilesystemWriteTool(new PathBoundaryValidator());
        var executor = new ScriptedToolExecutor(new FakeCatalog().Add(tool),
            new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision> { ["filesystem.write"] = PermissionDecision.Allow }),
            fx.Root, null, null, null, false, artifacts: artifacts);
        var stream = new EventStream(fx.Journal, fx.Codecs, session);
        var result = executor.ExecuteTool(new(ToolCallId.New(), new("filesystem.write"), "edit",
            "{\"path\":\"created.txt\",\"content\":\"effective content\\n\"}"), false, CancellationToken.None, stream);
        stream.AppendBatch(result.Events, DurabilityClass.Standard);
        Assert.True(result.Succeeded, result.Summary); Assert.Equal("effective content\n", File.ReadAllText(Path.Combine(fx.Root, "created.txt")));
        fx.Sync(session); Assert.Equal(1, fx.Pet.Status().Xp); fx.Sync(session); Assert.Equal(1, fx.Pet.Status().Xp);
    }
    [Fact]
    public void Protocol_actions_revision_events_and_extension_view_are_real_Core_routes()
    {
        using var fx = new Fixture(); var paths = new DefaultPlatformPaths(fx.Root);
        var server = new OmniServer(fx.Journal, fx.Codecs, new InMemoryAuditSink(), platformPathsFactory: () => paths);
        var key = ModelPolicyKey.For("fixture", "fixture");
        var harness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Discovered, 8, GuidanceLevel.Full, 1, PlanControl.Assisted, 4);
        var policy = EffectiveModelPolicy.Resolve(key, new StoredModelPolicy(key, 1, ModelPolicyPresets.FullAgent(),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), harness);
        server.ConfigureToolDiagnostics(new FakeCatalog().Add(new CompanionPetTool(fx.Pet)),
            new OmniCore.Abstractions.ModelCapabilityBoundary(policy), RunMode.Act);
        Assert.DoesNotContain("companion.pet", server.Query("tools", CancellationToken.None)!.Json);
        Assert.DoesNotContain("core.companion", server.Query("extensions.views", CancellationToken.None)!.Json);
        var snapshot = CompanionPetPresentation.Execute(server, ""); Assert.True(snapshot.Discovered);
        Assert.Contains("companion.pet", server.Query("tools", CancellationToken.None)!.Json);
        var change = server.Query("companion.pet.events:0", CancellationToken.None)!;
        Assert.Contains("\"revision\":1", change.Json);
        Assert.Equal("null", server.Query("companion.pet.events:1", CancellationToken.None)!.Json);
        Assert.Contains("core.companion", server.Query("extensions.views", CancellationToken.None)!.Json);
        var request = "extensions.view:" + Uri.EscapeDataString(ExtensionProtocol.EncodeRequest(new("core.companion", "pet")));
        var view = ExtensionProtocol.Decode(server.Query(request, CancellationToken.None)!.Json);
        Assert.Equal("indicator", view.Kind); Assert.Equal(snapshot.Revision, view.Revision);
        Assert.Equal(0, fx.Journal.CurrentSequence(fx.Session));
    }
    [Fact]
    public void Full_tool_service_persistence_and_catalog_discovery_path_uses_no_model()
    {
        using var fx = new Fixture(); var session = SessionId.New(); var run = TestRun.Open(fx.Journal, session);
        var catalog = new FakeCatalog().Add(new CompanionPetTool(fx.Pet, clientId: "headless")); catalog.Add(new ToolSearchTool(catalog));
        var executor = ScriptedToolExecutor.WithWorkspace(catalog,
            new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
            { ["companion.pet"] = PermissionDecision.Allow, ["tool.search"] = PermissionDecision.Allow }), fx.Root);
        var stream = new EventStream(fx.Journal, fx.Codecs, session);
        var search = executor.ExecuteTool(new(ToolCallId.New(), new("tool.search"), "search", "{\"query\":\"mascota\"}"), false, CancellationToken.None, stream);
        stream.AppendBatch(search.Events, DurabilityClass.Standard); Assert.Contains("companion.pet", search.Preview!);
        var discover = executor.ExecuteTool(new(ToolCallId.New(), new("companion.pet"), "discover", "{\"action\":\"discover\"}"), false, CancellationToken.None, stream);
        stream.AppendBatch(discover.Events, DurabilityClass.Standard); Assert.True(discover.Succeeded, discover.Summary);
        Assert.True(CompanionPetProtocol.Decode(discover.Preview!)!.Discovered);
        Assert.True(new CompanionPetService(fx.Root).Status().Discovered);
        var hide = executor.ExecuteTool(new(ToolCallId.New(), new("companion.pet"), "hide", "{\"action\":\"hide\"}"),
            false, CancellationToken.None, stream);
        stream.AppendBatch(hide.Events, DurabilityClass.Standard); Assert.True(hide.Succeeded, hide.Summary);
        Assert.False(fx.Pet.Status("headless").Preferences.Visible); Assert.True(fx.Pet.Status("terminal").Preferences.Visible);
        Assert.Contains(fx.Journal.ReadFrom(session, 1).Select(fx.Codecs.Decode), e => e is ToolCallSucceeded);
        Assert.DoesNotContain(fx.Journal.ReadFrom(session, 1).Select(fx.Codecs.Decode), e => e is ModelStepStarted);
    }
    [Theory]
    [InlineData("Please read this repository", false)]
    [InlineData("Can I discover a mascota here", true)]
    public void Tool_description_is_only_disclosed_when_requested(string prompt, bool expected)
    {
        using var fx = new Fixture(); var run = TestRun.Open(fx.Journal, fx.Session);
        var artifacts = new FileArtifactStore(fx.Root); var calls = 0;
        var catalog = new FakeCatalog();
        foreach (var name in new[] { "filesystem.read", "filesystem.list", "filesystem.search", "reference.resolve", "artifact.read" })
            catalog.Add(FakeTool.Read(name));
        catalog.Add(new CompanionPetTool(fx.Pet)); catalog.Add(new ToolSearchTool(catalog));
        var executor = ScriptedToolExecutor.WithWorkspace(catalog, new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), fx.Root);
        var turn = new ExplorerTurn((request, _) =>
        {
            calls++; Assert.Equal(expected, request.Tools.Any(t => t.Name == "companion.pet"));
            return new ModelResponse([new TextBlock("done")], StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
                new ProviderMetadata("fixture", "fixture", null));
        }, executor, catalog, new ContextMaterializer(new FakeTokenCounter(), []),
            new ExecutionFingerprint("fixture", "h", "t", "c", "o", "build"),
            new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Discovered, null), fx.Journal, fx.Codecs,
            artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
            new HarnessPolicy(ToolCallFormat.Native, ToolMode.Discovered, 1, GuidanceLevel.Full, 1, PlanControl.Assisted, 4));
        turn.Ask(prompt, "system", fx.Session, run.RunId, run.RootLane, "", CancellationToken.None);
        Assert.Equal(1, calls);
    }
    [Fact]
    public void Portable_sprites_have_six_distinct_silhouettes_and_real_animation_frames()
    {
        using var fx = new Fixture(); fx.Hatch(); var pet = fx.Pet.Status();
        var shapes = new HashSet<string>();
        foreach (var species in CompanionPetProtocol.Species)
        {
            var state = pet with { Species = species };
            var frame = CompanionSpriteCatalog.Render(state, "idle", 1);
            Assert.Equal(8, frame.Count); Assert.All(frame, row => Assert.Equal(20, row.Count));
            shapes.Add(string.Join("", frame.SelectMany(r => r).Select(c => c.Glyph == " " ? "." : "#")));
            Assert.NotEqual(Flatten(frame), Flatten(CompanionSpriteCatalog.Render(state, "success", 1)));
            Assert.NotEqual(Flatten(frame), Flatten(CompanionSpriteCatalog.Render(state, "pet", 1)));
        }
        Assert.Equal(6, shapes.Count);
        Assert.Equal("working", CompanionPetPresentation.Animation(pet with { Reaction = new("pet", DateTimeOffset.UtcNow.AddSeconds(-1), 3) },
            true, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        static string Flatten(IReadOnlyList<IReadOnlyList<PixelCell>> cells) => string.Join("", cells.SelectMany(r => r).Select(c => c.ToString()));
    }
}
