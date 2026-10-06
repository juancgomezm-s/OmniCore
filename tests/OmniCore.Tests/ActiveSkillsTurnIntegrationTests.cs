using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

// Offline model; real context/journal/CAS pipeline, not authenticated provider usage.
public sealed class ActiveSkillsTurnIntegrationTests
{
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
