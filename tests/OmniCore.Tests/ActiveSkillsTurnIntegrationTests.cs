using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Extensions;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

// Offline model; real context/journal/CAS pipeline, not authenticated provider usage.
public sealed class ActiveSkillsTurnIntegrationTests
{
    [Fact]
    public void Offline_model_can_load_an_eligible_skill_and_the_next_turn_gets_its_materialized_instructions()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-model-skill-load-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, ".omnicore", "skills", "compile.safe");
        Directory.CreateDirectory(package);
        const string instructions = "Use the fixture compiler after reading its project configuration.";
        File.WriteAllText(Path.Combine(package, "skill.yaml"), """
            id: compile.safe
            version: 1
            modelInvocable: true
            budgetTokens: 512
            tools: [fixture.compile]
            """);
        File.WriteAllText(Path.Combine(package, "instructions.md"), instructions);
        try
        {
            var session = SessionId.New();
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var run = TestRun.Open(store, session);
            var loader = new SkillManifestLoader();
            var skill = Assert.Single(loader.Discover(Path.Combine(root, ".omnicore", "skills"),
                ScopeLevel.Project, TrustLevel.Project, workspaceTrusted: true));
            var activations = new SessionSkillActivationStore(Path.Combine(root, "runtime"));
            var tool = new SkillLoadTool(session, activations, loader, [skill],
                new SkillSelectionLimits(2, 2048));
            var catalog = new FakeCatalog().Add(tool);
            var policyKey = ModelPolicyKey.For("fixture", "fixture");
            var effectivePolicy = EffectiveModelPolicy.Resolve(policyKey,
                new StoredModelPolicy(policyKey, 1, ModelPolicyPresets.ObserveOnly(), DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch),
                new HarnessPolicy(ToolCallFormat.Native, ToolMode.Discovered, 8, GuidanceLevel.Full, 3,
                    PlanControl.Assisted, 4));
            var boundary = OmniCliRuntime.CreateBoundary(effectivePolicy, root, catalog);
            var firstRequests = new List<ModelRequest>();
            var responseIndex = 0;
            var firstTurn = new ExplorerTurn((request, _) =>
            {
                firstRequests.Add(request);
                return responseIndex++ == 0
                    ? new ModelResponse([new ToolCallBlock(ToolCallId.New(), "load-skill", "skill.load",
                        "{\"id\":\"compile.safe\"}")], StopReason.ToolUse,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null))
                    : new ModelResponse([new TextBlock("loaded")], StopReason.EndTurn,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
            }, ScriptedToolExecutor.WithWorkspace(catalog,
                    new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()).WithModeDefaults(RunMode.Plan), root, boundary), catalog,
                new ContextMaterializer(new FakeTokenCounter(), [new SkillIndexContributor(session, [skill], true)]),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
                store, codecs, new FileArtifactStore(root), new InMemoryAuditSink(), new RedactionPolicy(),
                boundary: boundary);
            Assert.Equal(StopReason.EndTurn,
                firstTurn.Ask("Use compile.safe", "system", session, run.RunId, run.RootLane, "", CancellationToken.None).StopReason);
            Assert.Contains(firstRequests[0].Tools, definition => definition.Name == "skill.load");
            Assert.DoesNotContain("compile.safe", firstRequests[0].Instructions ?? "", StringComparison.Ordinal);
            Assert.Contains(firstRequests[0].Messages.SelectMany(message => message.Content).OfType<TextBlock>(), block => block.Text.Contains("compile.safe", StringComparison.Ordinal));
            Assert.Equal(skill.ManifestSha256, Assert.Single(activations.Read(session)).Value);
            Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<SkillActivationChanged>(),
                item => item.Activated && item.Origin == "Model" && item.Scope == ScopeLevel.Project);

            var loaded = Assert.Single(new SkillActivationResolver().LoadCandidates([skill], session, activations));
            var secondRequests = new List<ModelRequest>();
            var secondCatalog = new FakeCatalog().Add(FakeTool.Read("fixture.compile"));
            var secondTurn = new ExplorerTurn((request, _) =>
            {
                secondRequests.Add(request);
                return new ModelResponse([new TextBlock("ready")], StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
            }, ScriptedToolExecutor.WithWorkspace(secondCatalog,
                    new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root), secondCatalog,
                new ContextMaterializer(new FakeTokenCounter(), [new SkillContextContributor(session, loaded)]),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Discovered, null),
                store, codecs, new FileArtifactStore(root), new InMemoryAuditSink(), new RedactionPolicy(),
                recordEffectiveFingerprint: true,
                activeSkills: [new ActiveSkillFingerprint("compile.safe", "1", ContentHash.Sha256(loaded.PackageContentSha256!))],
                activeSkillToolIds: loaded.Skill.Manifest.Tools);
            Assert.Equal(StopReason.EndTurn,
                secondTurn.Ask("continue", "system", session, run.RunId, run.RootLane, "", CancellationToken.None).StopReason);
            Assert.DoesNotContain(instructions, Assert.Single(secondRequests).Instructions ?? "", StringComparison.Ordinal);
            Assert.Contains(secondRequests[0].Messages.SelectMany(message => message.Content).OfType<TextBlock>(), block => block.Text.Contains(instructions, StringComparison.Ordinal));
            Assert.Contains(Assert.Single(secondRequests).Tools, definition => definition.Name == "fixture.compile");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Forced_extension_command_tool_is_selected_for_the_initial_model_request()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-extension-command-turn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var catalog = new FakeCatalog().Add(FakeTool.Read("ext.acme.progress.compile"));
            var requests = new List<ModelRequest>();
            var providerCalls = 0;
            var turn = new ExplorerTurn((request, _) =>
            {
                requests.Add(request);
                if (providerCalls++ == 0)
                    return new ModelResponse([new ToolCallBlock(ToolCallId.New(), "fixture-call",
                        "ext.acme.progress.compile", "{}")], StopReason.ToolUse,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
                return new ModelResponse([new TextBlock("fixture response")], StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
            }, ScriptedToolExecutor.WithWorkspace(catalog,
                ScriptedPermissionPolicy.WithTool("ext.acme.progress.compile", PermissionDecision.Allow), root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
                store, codecs, new FileArtifactStore(root), new InMemoryAuditSink(), new RedactionPolicy(),
                forcedInitialToolName: "ext.acme.progress.compile");
            var result = turn.Ask("compile", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(StopReason.EndTurn, result.StopReason);
            Assert.Equal(2, requests.Count);
            Assert.Equal("exact", requests[0].ToolChoice.Mode);
            Assert.Equal("ext.acme.progress.compile", requests[0].ToolChoice.ToolName);
            Assert.Equal("auto", requests[1].ToolChoice.Mode);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Empty_declaration_cannot_hide_skill_context(bool knownEmpty, bool contributesSkill)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-skills-turn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new InMemoryEventStore();
            var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(store, session);
            var artifacts = new FileArtifactStore(root);
            var catalog = new FakeCatalog();
            var calls = 0;
            var turn = new ExplorerTurn((_, _) =>
            {
                calls++;
                return new ModelResponse([new TextBlock("fixture response")], StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
            }, ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), contributesSkill ? [new SkillContributor()] : []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
                store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                recordEffectiveFingerprint: true, activeSkills: knownEmpty ? [] : null);
            var result = turn.Ask("hello", "system", session, run.RunId, run.RootLane, "", CancellationToken.None);
            var contradicts = knownEmpty && contributesSkill;
            Assert.Equal(contradicts ? StopReason.Error : StopReason.EndTurn, result.StopReason);
            Assert.Equal(contradicts ? 0 : 1, calls);
            if (contradicts) return;
            var start = Assert.Single(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnStarted>());
            var component = Assert.Single(start.Fingerprint!.Components, part => part.Name == "skills.active");
            Assert.NotNull(component.Content);
            Assert.True(artifacts.Verify(component.Content.Hash, component.Content.Size));
            using var json = JsonDocument.Parse(artifacts.GetText(component.Content.Hash)!);
            Assert.Equal(knownEmpty ? "provided" : "unavailable", json.RootElement.GetProperty("source").GetString());
            Assert.Equal(knownEmpty ? JsonValueKind.Array : JsonValueKind.Null,
                json.RootElement.GetProperty("skills").ValueKind);
            if (knownEmpty) Assert.Empty(json.RootElement.GetProperty("skills").EnumerateArray());
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class SkillContributor : IContextContributor
    {
        public Task<IReadOnlyList<ContextItem>> GetContextAsync(MaterializeRequest request, CancellationToken cancellationToken)
            => System.Threading.Tasks.Task.FromResult<IReadOnlyList<ContextItem>>([new ContextItem("fixture-skill", ContextItemKind.Skill,
                "fixture instructions", 1, ContextPriority.High, RetentionPolicy.KeepForever,
                new ContextProvenance("fixture", ContributionCategory.Skills, "fixture", ScopeLevel.Session, false))]);
    }
}
