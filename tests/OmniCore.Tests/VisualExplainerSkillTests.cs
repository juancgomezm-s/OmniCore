using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class VisualExplainerSkillTests
{
    [Fact]
    public async System.Threading.Tasks.Task Bundled_visual_skill_is_discoverable_activates_real_resources_and_stays_session_scoped()
    {
        var session = SessionId.New();
        var sources = new SkillSourceCatalog().Discover(null, Path.Combine(Path.GetTempPath(), "no-user-skills-" + Guid.NewGuid()),
            null, session, workspaceTrusted: false);
        var skill = Assert.Single(sources, candidate => candidate.Manifest.Id == "visual-explainer");
        Assert.Equal(ScopeLevel.BuiltIn, skill.Scope);
        Assert.Equal(TrustLevel.Core, skill.Trust);
        Assert.Equal(SkillLifecycleState.Discovered, skill.State);
        Assert.True(skill.Manifest.Activation!.Explicit);
        var loaded = new SkillManifestLoader().Activate(skill);
        Assert.Equal(3, loaded.Resources!.Count);
        Assert.All(loaded.Resources, resource => Assert.NotEmpty(resource.Sha256));
        Assert.True(File.Exists(Path.Combine(skill.Directory, "scripts", "render_report.py")));
        Assert.True(File.Exists(Path.Combine(skill.Directory, "LICENSE")));
        var request = new MaterializeRequest(session, RunId.New(), null, null, null, 0,
            new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture"));
        var context = await new SkillContextContributor(session, loaded).GetContextAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(4, context.Count);
        Assert.Contains(context, item => item.Content.Contains(Path.GetFullPath(skill.Directory), StringComparison.Ordinal));
        Assert.Contains(context, item => item.Content.Contains("diagramas de texto verticales", StringComparison.Ordinal));
        Assert.True(context.Sum(item => item.EstimatedTokens) <= skill.Manifest.BudgetTokens);
        Assert.Empty(await new SkillContextContributor(SessionId.New(), loaded).GetContextAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Portable_entrypoint_loads_in_the_supported_Codex_adapter()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "skills");
        var skill = Assert.Single(new SkillManifestLoader().DiscoverPortable(root, ScopeLevel.User,
            TrustLevel.Trusted, "codex"), item => item.Manifest.Id == "visual-explainer");
        Assert.Contains("Visual explainer para OmniCore", new SkillManifestLoader().Activate(skill).Instructions);
    }
}
