namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Host;
using OmniCore.Context;
using OmniCore.Engine;
using OmniCore.Infrastructure;
using OmniCore.Tools;
using OmniCore.Security;

public sealed class SkillHumanInvocationTests
{
    private static DiscoveredSkill Candidate(string id, string? displayName = null, int budget = 64,
        TrustLevel trust = TrustLevel.Trusted, SkillLifecycleState state = SkillLifecycleState.Discovered) =>
        new("unused", new SkillManifest { Id = id, Version = "1", DisplayName = displayName,
            BudgetTokens = budget, Activation = new SkillActivationManifest { Explicit = true } },
            ScopeLevel.User, trust, new string('a', 64), state, null);

    [Theory]
    [InlineData("Usa visual-explainer para este cambio")]
    [InlineData("/visual-explainer explica el proyecto")]
    [InlineData("Usa $visual-explainer.")]
    [InlineData("Usa Visual Explainer.")]
    [InlineData("Use skill User:visual-explainer.\nExplain")]
    public void Human_names_and_slash_commands_resolve_once(string text)
    {
        var skill = Candidate("visual-explainer", "Visual Explainer");
        Assert.Equal("visual-explainer", Assert.Single(SkillHumanInvocationResolver.Resolve(
            text, [skill]).Skills).Manifest.Id);
    }

    [Theory]
    [InlineData("visual-explainer-extra")]
    [InlineData("prefixvisual-explainer")]
    [InlineData("visual-explainer.cs")]
    [InlineData("src/visual-explainer")]
    [InlineData("another:visual-explainer")]
    public void Accidental_substrings_and_paths_do_not_resolve(string text) =>
        Assert.Empty(SkillHumanInvocationResolver.Resolve(text, [Candidate("visual-explainer")]).Skills);

    [Theory]
    [InlineData("visual-explainer@example.com")]
    [InlineData("person@visual-explainer.example")]
    [InlineData("mailto:visual-explainer@example.com")]
    [InlineData("https://example.invalid/?skill=visual-explainer")]
    [InlineData("https://example.invalid/#visual-explainer")]
    [InlineData("https://example.invalid/?skill=$visual-explainer")]
    [InlineData("www.example.invalid/?skill=visual-explainer")]
    public void Email_addresses_and_URL_data_do_not_activate_named_skills(string text)
    {
        var result = SkillHumanInvocationResolver.Resolve(text, [Candidate("visual-explainer")]);
        Assert.Empty(result.Skills);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void A_separate_skill_name_after_a_URL_remains_a_human_invocation()
    {
        var result = SkillHumanInvocationResolver.Resolve(
            "Revisa https://example.invalid/?skill=visual-explainer y usa visual-explainer después.",
            [Candidate("visual-explainer")]);
        Assert.Equal("visual-explainer", Assert.Single(result.Skills).Manifest.Id);
        Assert.Empty(result.Diagnostics);
    }
    [Fact]
    public void Ambiguous_display_names_require_a_qualified_identity()
    {
        var skills = new[] { Candidate("review-a", "Review code"), Candidate("review-b", "Review code") };
        var ambiguous = SkillHumanInvocationResolver.Resolve("Review code", skills);
        Assert.Empty(ambiguous.Skills);
        Assert.Single(ambiguous.Diagnostics);
        Assert.Equal("review-b", Assert.Single(SkillHumanInvocationResolver.Resolve("User:review-b", skills).Skills).Manifest.Id);
    }

    [Fact]
    public void Complete_multiword_names_win_over_contained_short_names_without_losing_separate_mentions()
    {
        var skills = new[] { Candidate("explain"), Candidate("architecture", "Explain architecture") };
        Assert.Equal("architecture", Assert.Single(SkillHumanInvocationResolver.Resolve(
            "Usa Explain architecture.", skills).Skills).Manifest.Id);
        Assert.Equal(2, SkillHumanInvocationResolver.Resolve(
            "Usa Explain architecture y después explain.", skills).Skills.Count);
        var ambiguousShort = skills.Append(Candidate("another", "explain")).ToArray();
        Assert.Empty(SkillHumanInvocationResolver.Resolve("Explain architecture", ambiguousShort).Diagnostics);
        Assert.Single(SkillHumanInvocationResolver.Resolve("Explain architecture y explain", ambiguousShort).Diagnostics);
    }
    [Fact]
    public void Reserved_and_external_command_names_require_explicit_skill_spelling()
    {
        var skills = new[] { Candidate("help"), Candidate("explain") };
        Assert.Empty(SkillHumanInvocationResolver.Resolve("help and explain", skills, ["explain"]).Skills);
        Assert.Equal(2, SkillHumanInvocationResolver.Resolve("$help /skill-explain", skills, ["explain"]).Skills.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task Command_catalog_contains_aliases_without_instruction_bodies_and_keeps_existing_commands()
    {
        var registry = new CommandService().Registry;
        var skills = new[] { Candidate("explain"), Candidate("visual-explainer"), Candidate("help") };
        var initial = registry.Descriptors().Count;
        SkillInvocationCommands.Register(registry, skills);
        SkillInvocationCommands.Register(registry, skills);
        Assert.Equal(initial + 3, registry.Descriptors().Count);
        Assert.Equal("core:explain", registry.Resolve("explain").Descriptor.Id);
        Assert.Equal("skill-visual-explainer", registry.Resolve("visual-explainer").Descriptor.Name);
        Assert.Equal("skill-help", registry.Resolve("skill-help").Descriptor.Name);
        var handler = registry.Resolve("visual-explainer");
        var outcome = Assert.IsType<PromptCommandRequested>(await handler.HandleAsync(["Explain"],
            new CommandContext(null, null, null, null), CancellationToken.None));
        Assert.Equal("Use skill User:visual-explainer.\nExplain", outcome.Text);
        Assert.DoesNotContain("unused", outcome.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Command_normalization_collisions_get_distinct_commands_and_no_arbitrary_alias()
    {
        var registry = new CommandRegistry();
        SkillInvocationCommands.Register(registry, [Candidate("fixture.a"), Candidate("fixture-a")]);
        Assert.Equal(2, registry.Descriptors().Count);
        Assert.Equal(2, registry.Descriptors().Select(command => command.Name).Distinct().Count());
        Assert.Single(SkillHumanInvocationResolver.Resolve("skill-fixture-a",
            [Candidate("fixture.a"), Candidate("fixture-a")]).Diagnostics);
    }

    [Fact]
    public void Activation_is_bounded_idempotent_and_materializes_only_the_requested_session()
    {
        using var fixture = new Fixture();
        var session = SessionId.New();
        var events = new List<SkillActivationChanged>();
        var candidates = fixture.Discover();
        Assert.Single(SkillHumanInvocationActivation.Activate("/fixture-a fixture-a", candidates,
            session, fixture.Store, new SkillSelectionLimits(2, 128), true, events.Add));
        Assert.Empty(SkillHumanInvocationActivation.Activate("fixture-a", candidates,
            session, fixture.Store, new SkillSelectionLimits(2, 128), true, events.Add));
        Assert.Single(events);
        Assert.Equal("User", events[0].Origin);
        var loaded = Assert.Single(new SkillActivationResolver().LoadCandidates(candidates, session, fixture.Store));
        Assert.Contains("Fixture A instructions", loaded.Instructions, StringComparison.Ordinal);
        Assert.Empty(new SkillActivationResolver().LoadCandidates(candidates, SessionId.New(), fixture.Store));
    }

    [Fact]
    public void Over_budget_ambiguous_and_untrusted_requests_do_not_mutate_activation_state()
    {
        using var fixture = new Fixture();
        var session = SessionId.New();
        var candidates = fixture.Discover();
        Assert.Throws<InvalidOperationException>(() => SkillHumanInvocationActivation.Activate(
            "fixture-a fixture-b", candidates, session, fixture.Store, new SkillSelectionLimits(1, 64),
            true, _ => throw new InvalidOperationException("Should not emit")));
        Assert.Empty(fixture.Store.Read(session));
        Assert.Throws<InvalidOperationException>(() => SkillHumanInvocationActivation.Activate(
            "fixture-a", candidates, session, fixture.Store, new SkillSelectionLimits(2, 128),
            false, _ => throw new InvalidOperationException("Should not emit")));
        Assert.Empty(fixture.Store.Read(session));
        var restricted = candidates.Select(skill => skill with
        { InvocationRestrictions = new(false, false, false, "fixture") }).ToArray();
        Assert.Throws<InvalidOperationException>(() => SkillHumanInvocationActivation.Activate(
            "fixture-a", restricted, session, fixture.Store, new SkillSelectionLimits(2, 128), true, _ => { }));
        Assert.Empty(fixture.Store.Read(session));
    }

    [Fact]
    public void A_new_human_mention_revalidates_and_replaces_only_its_changed_package()
    {
        using var fixture = new Fixture();
        var session = SessionId.New();
        var events = new List<SkillActivationChanged>();
        var limits = new SkillSelectionLimits(2, 128);
        SkillHumanInvocationActivation.Activate("fixture-a fixture-b", fixture.Discover(),
            session, fixture.Store, limits, true, events.Add);
        var before = fixture.Store.ReadDetailed(session);
        File.WriteAllText(Path.Combine(fixture.Root, "skills", "fixture-a", "instructions.md"),
            "Changed fixture A instructions");
        Assert.Single(SkillHumanInvocationActivation.Activate("fixture-a", fixture.Discover(),
            session, fixture.Store, limits, true, events.Add));
        var after = fixture.Store.ReadDetailed(session);
        Assert.NotEqual(before[new(ScopeLevel.Project, "fixture-a")].PackageContentSha256,
            after[new(ScopeLevel.Project, "fixture-a")].PackageContentSha256);
        Assert.Equal(before[new(ScopeLevel.Project, "fixture-b")], after[new(ScopeLevel.Project, "fixture-b")]);
        Assert.Equal(3, events.Count);
        var active = new SkillActivationResolver().LoadCandidates(fixture.Discover(), session, fixture.Store);
        Assert.Contains("Changed fixture A instructions", active.Single(skill =>
            skill.Skill.Manifest.Id == "fixture-a").Instructions, StringComparison.Ordinal);
    }
    [Fact]
    public void Journal_failure_rolls_back_the_activation()
    {
        using var fixture = new Fixture();
        var session = SessionId.New();
        Assert.Throws<IOException>(() => SkillHumanInvocationActivation.Activate("fixture-a", fixture.Discover(),
            session, fixture.Store, new SkillSelectionLimits(2, 128), true, _ => throw new IOException("fixture")));
        Assert.Empty(fixture.Store.Read(session));
    }

    [Fact]
    public void Human_activation_reaches_the_first_real_turn_request_once_and_keeps_tool_permissions()
    {
        using var fixture = new Fixture();
        var session = SessionId.New();
        var journal = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var run = TestRun.Open(journal, session);
        var candidates = fixture.Discover();
        void Emit(SkillActivationChanged payload) => new EventStream(journal, codecs, session)
            .Append(payload, DurabilityClass.Barrier);
        SkillHumanInvocationActivation.Activate("Use fixture-a fixture-a", candidates,
            session, fixture.Store, new SkillSelectionLimits(2, 128), true, Emit);
        var composition = new SkillTurnComposition(session, fixture.Store, () => candidates,
            new SkillSelectionLimits(2, 128), new FakeTokenCounter(), fixture.Data,
            guidanceEnabled: true, includeWorkingState: false, workingState: "", emitLifecycle: Emit).Refresh();
        var requests = new List<ModelRequest>();
        var catalog = new FakeCatalog().Add(FakeTool.Read("fixture.forbidden"));
        var turn = new ExplorerTurn((request, _) =>
        {
            requests.Add(request);
            return new ModelResponse([new TextBlock("ready")], StopReason.EndTurn,
                new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
        }, ScriptedToolExecutor.WithWorkspace(catalog,
            ScriptedPermissionPolicy.WithTool("fixture.forbidden", PermissionDecision.Deny), fixture.Root), catalog,
            composition.Materializer,
            new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
            new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Discovered, null),
            journal, codecs, new FileArtifactStore(fixture.Root), new InMemoryAuditSink(), new RedactionPolicy(),
            recordEffectiveFingerprint: true, activeSkills: composition.Fingerprints,
            activeSkillToolIds: composition.ToolIds);
        Assert.Equal(StopReason.EndTurn,
            turn.Ask("Use fixture-a", "system", session, run.RunId, run.RootLane, "", CancellationToken.None).StopReason);
        var body = string.Join("\n", Assert.Single(requests).Messages.SelectMany(message => message.Content)
            .OfType<TextBlock>().Select(block => block.Text));
        Assert.Equal(1, body.Split("Fixture A instructions", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Fixture B instructions", body, StringComparison.Ordinal);
        Assert.Single(journal.ReadFrom(session, 1).Select(codecs.Decode).OfType<SkillActivationChanged>());
        // The user mention records context activation only, never executes a skill's declared tools.
        Assert.DoesNotContain(journal.ReadFrom(session, 1).Select(codecs.Decode), payload => payload is ToolCallStarted);
    }

    [Fact]
    public void Refresh_removes_obsolete_commands_and_handles_a_single_combined_source_snapshot()
    {
        using var fixture = new Fixture();
        var registry = new CommandRegistry();
        var session = SessionId.New();
        SessionSkillWorkflowCommands.Refresh(registry, session, fixture.Root, fixture.Data,
            fixture.Data, true, allCandidates: [Candidate("old")]);
        Assert.Single(registry.Descriptors());
        SessionSkillWorkflowCommands.Refresh(registry, session, fixture.Root, fixture.Data,
            fixture.Data, true, allCandidates: [Candidate("new")]);
        Assert.Equal("skill-new", Assert.Single(registry.Descriptors()).Name);
        Assert.Throws<InvalidOperationException>(() => registry.Resolve("old"));
        SessionSkillWorkflowCommands.Refresh(registry, null, fixture.Root, fixture.Data, fixture.Data, true, allCandidates: []);
        Assert.Empty(registry.Descriptors());
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-human-skills-" + Guid.NewGuid().ToString("N"));
        public string Root => _root;
        public string Data => Path.Combine(_root, "data");
        public SessionSkillActivationStore Store { get; }
        public Fixture()
        {
            foreach (var (id, instruction) in new[] { ("fixture-a", "Fixture A instructions"), ("fixture-b", "Fixture B instructions") })
            {
                var package = Path.Combine(_root, "skills", id);
                Directory.CreateDirectory(package);
                File.WriteAllText(Path.Combine(package, "skill.yaml"), "id: " + id
                    + "\nversion: 1\nbudgetTokens: 64\nactivation:\n  explicit: true\n");
                File.WriteAllText(Path.Combine(package, "instructions.md"), instruction);
            }
            Store = new SessionSkillActivationStore(Path.Combine(_root, "data"));
        }
        public IReadOnlyList<DiscoveredSkill> Discover() => new SkillManifestLoader().Discover(
            Path.Combine(_root, "skills"), ScopeLevel.Project, TrustLevel.Project, workspaceTrusted: true);
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
