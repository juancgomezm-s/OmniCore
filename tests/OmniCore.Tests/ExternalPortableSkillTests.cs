namespace OmniCore.Tests;

using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Context;
using OmniCore.Host;
using System.Text;

public sealed class ExternalPortableSkillTests
{
    [Fact]
    public void PortableSkillLoadsBoundedBodyAndHonorsClaudeInvocationPolicy()
    {
        using var directory = new TemporaryDirectory();
        var skill = Path.Combine(directory.Path, "example");
        Directory.CreateDirectory(skill);
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: example\ndescription: Example workflow\ndisable-model-invocation: true\nuser-invocable: true\n---\nUse this as data.", new UTF8Encoding(false));

        var loader = new SkillManifestLoader();
        var discovered = Assert.Single(loader.DiscoverPortable(directory.Path, ScopeLevel.User,
            TrustLevel.Trusted, "claude"));
        Assert.Equal("example", discovered.Manifest.Id);
        Assert.Equal("Example workflow", discovered.Description);
        Assert.False(discovered.InvocationRestrictions!.AllowModelInvocation);
        Assert.True(discovered.InvocationRestrictions.AllowUserInvocation);
        var loaded = loader.Activate(discovered);
        Assert.Contains("Use this as data.", loaded.Instructions, StringComparison.Ordinal);

        var selector = new SkillSelector();
        var eligible = discovered with { State = SkillLifecycleState.Eligible };
        Assert.Empty(selector.Select([eligible], SkillSelectionRequestKind.ModelExplicit,
            new SkillSelectionLimits(1, 2000), "example").Selected);
        Assert.Single(selector.Select([eligible], SkillSelectionRequestKind.UserExplicit,
            new SkillSelectionLimits(1, 2000), "example").Selected);
    }

    [Fact]
    public void CodexImplicitInvocationRestrictionAppliesBeforeModelAndAutomaticSelection()
    {
        using var directory = new TemporaryDirectory();
        var skill = Path.Combine(directory.Path, "example");
        Directory.CreateDirectory(Path.Combine(skill, "agents"));
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: example\ndescription: Example workflow\n---\nBody", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(skill, "agents", "openai.yaml"), "policy:\n  allow_implicit_invocation: false\n", new UTF8Encoding(false));

        var loader = new SkillManifestLoader();
        var discovered = Assert.Single(loader.DiscoverPortable(directory.Path, ScopeLevel.User,
            TrustLevel.Trusted, "codex"));
        var eligible = discovered with { State = SkillLifecycleState.Eligible };
        var selector = new SkillSelector();
        Assert.Empty(selector.Select([eligible], SkillSelectionRequestKind.ModelExplicit,
            new SkillSelectionLimits(1, 2000), "example").Selected);
        Assert.Empty(selector.Select([eligible], SkillSelectionRequestKind.Automatic,
            new SkillSelectionLimits(1, 2000)).Selected);
    }

    [Fact]
    public void CodexPolicyAndPhysicalPackageIdentityInvalidateOldActivation()
    {
        using var directory = new TemporaryDirectory();
        var skill = Path.Combine(directory.Path, "example");
        var policyPath = Path.Combine(skill, "agents", "openai.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(policyPath)!);
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: example\ndescription: Example workflow\n---\nBody", new UTF8Encoding(false));
        File.WriteAllText(policyPath, "policy:\n  allow_implicit_invocation: true\n", new UTF8Encoding(false));

        var loader = new SkillManifestLoader();
        var before = Assert.Single(loader.DiscoverPortable(directory.Path, ScopeLevel.User,
            TrustLevel.Trusted, "codex"));
        var bodyHash = before.PortableContentSha256;
        var firstActivation = before.ManifestSha256;
        File.WriteAllText(policyPath, "policy:\n  allow_implicit_invocation: false\n", new UTF8Encoding(false));
        var afterPolicyChange = Assert.Single(loader.DiscoverPortable(directory.Path, ScopeLevel.User,
            TrustLevel.Trusted, "codex"));
        Assert.Equal(bodyHash, afterPolicyChange.PortableContentSha256);
        Assert.NotEqual(firstActivation, afterPolicyChange.ManifestSha256);
        Assert.False(afterPolicyChange.InvocationRestrictions!.AllowModelInvocation);
        Assert.Throws<InvalidDataException>(() => loader.Activate(before));

        var movedDirectory = Path.Combine(directory.Path, "moved");
        Directory.Move(skill, movedDirectory);
        var movedRoot = Path.Combine(directory.Path, "moved-root");
        Directory.CreateDirectory(movedRoot);
        Directory.Move(movedDirectory, Path.Combine(movedRoot, "example"));
        var moved = Assert.Single(loader.DiscoverPortable(movedRoot, ScopeLevel.User,
            TrustLevel.Trusted, "codex"));
        Assert.Equal(bodyHash, moved.PortableContentSha256);
        Assert.NotEqual(afterPolicyChange.ManifestSha256, moved.ManifestSha256);
    }

    [Fact]
    public void UnsupportedClaudeExecutionFeaturesAreVisibleButCannotActivate()
    {
        using var directory = new TemporaryDirectory();
        var skill = Path.Combine(directory.Path, "example");
        Directory.CreateDirectory(skill);
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: example\ndescription: Example workflow\ncontext: fork\n---\nBody", new UTF8Encoding(false));

        var discovered = Assert.Single(new SkillManifestLoader().DiscoverPortable(directory.Path,
            ScopeLevel.User, TrustLevel.Trusted, "claude"));
        Assert.Equal(SkillLifecycleState.Conflict, discovered.State);
        Assert.Contains("never executed", discovered.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task SkillIndexHonorsEffectiveContextBudgetWithManyLongDescriptions()
    {
        var session = SessionId.New();
        var candidates = Enumerable.Range(0, 32).Select(index => new DiscoveredSkill(
            "fixture/skill-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new SkillManifest { Id = "skill-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Version = "1", ModelInvocable = true, BudgetTokens = 64 },
            ScopeLevel.User, TrustLevel.Trusted, "manifest-" + index, SkillLifecycleState.Eligible, null,
            InvocationRestrictions: new SkillInvocationRestrictions(true, true, true, "codex"),
            Description: new string('d', 512))).ToArray();
        var contributor = new SkillIndexContributor(session, candidates, enabled: true, contextTokenBudget: 256);
        var request = new MaterializeRequest(session, RunId.New(), null, null, null, 0,
            new ExecutionFingerprint("model", "harness", "tools", "policy", "overrides", "test"));

        var items = await contributor.GetContextAsync(request, TestContext.Current.CancellationToken);
        var item = Assert.Single(items);
        Assert.InRange(item.EstimatedTokens, 1, 256);
        Assert.Contains("skill.load", item.Content, StringComparison.Ordinal);
        Assert.Contains("skill-0", item.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("skill-31", item.Content, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "omnicore-portable-skill-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
