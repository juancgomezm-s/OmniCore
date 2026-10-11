namespace OmniCore.Tests;

using System.Text.Json;
using OmniCore.Client;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Host;
using OmniCore.Protocol;
using OmniCore.Security;

public sealed class SkillLifecycleTests
{
    private const string Manifest = """
        id: progress.compile
        version: 1
        displayName: Progress compile
        sealed: false
        priority: 5
        modelInvocable: false
        activation:
          paths: ["**/*.p"]
          extensions: [".w"]
        execution:
          context: fork
          preferredModelAlias: local-worker
        tools: [filesystem.read]
        """;

    [Fact]
    public void Discovery_is_project_trust_gated_and_does_not_read_instructions_until_activation()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-skills-" + Guid.NewGuid().ToString("N"));
        var skillDirectory = Path.Combine(root, ".omnicore", "skills", "progress.compile");
        Directory.CreateDirectory(skillDirectory);
        var manifestPath = Path.Combine(skillDirectory, SkillManifestLoader.ManifestFileName);
        File.WriteAllText(manifestPath, Manifest);
        try
        {
            var loader = new SkillManifestLoader();
            Assert.Empty(loader.Discover(Path.Combine(root, ".omnicore", "skills"),
                workspaceTrusted: false));
            var discovered = Assert.Single(loader.Discover(Path.Combine(root, ".omnicore", "skills"),
                workspaceTrusted: true));
            Assert.Equal(SkillLifecycleState.Discovered, discovered.State);
            Assert.Equal(0, new DirectoryInfo(skillDirectory).GetFiles().Count(file =>
                file.Name == SkillManifestLoader.InstructionsFileName));

            Assert.Equal(SkillLifecycleState.Eligible,
                loader.EvaluateEligibility(discovered, relativePath: "src/main.p").State);
            Assert.Equal(SkillLifecycleState.Discovered,
                loader.EvaluateEligibility(discovered, relativePath: "src/main.cs").State);

            var instructions = "Use the project's compiler workflow.";
            File.WriteAllText(Path.Combine(skillDirectory, SkillManifestLoader.InstructionsFileName), instructions);
            var loaded = loader.Activate(discovered);
            Assert.Equal(SkillLifecycleState.Loaded, loaded.Skill.State);
            Assert.Equal(instructions, loaded.Instructions);
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(instructions))), loaded.ContentSha256);

            File.WriteAllText(manifestPath, Manifest.Replace("version: 1", "version: 2", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => loader.Activate(discovered));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Manifest_rejects_unknown_fields_and_activation_paths_that_escape_scope()
    {
        var loader = new SkillManifestLoader();
        Assert.Throws<InvalidDataException>(() => loader.Load(Manifest + "unknownSetting: true\n"));
        Assert.Throws<InvalidDataException>(() => loader.Load(Manifest.Replace("**/*.p", "../secret", StringComparison.Ordinal)));
    }

    [Fact]
    public void Skill_manifest_ast_mapping_rejects_wrong_scalar_and_nested_types()
    {
        var loader = new SkillManifestLoader();
        Assert.Throws<InvalidDataException>(() => loader.Load(Manifest.Replace("priority: 5", "priority: high", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => loader.Load(Manifest.Replace("paths: [\"**/*.p\"]", "paths: true", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => loader.Load(Manifest.Replace("extensions: [\".w\"]", "explicit: certainly", StringComparison.Ordinal)));
    }

    [Fact]
    public void Skill_precedence_honors_sealed_and_does_not_allow_lower_trust_to_hide()
    {
        var manifest = new SkillManifest { Id = "core.skill", Version = "1", Sealed = true };
        var builtIn = new DiscoveredSkill("builtin", manifest, ScopeLevel.BuiltIn, TrustLevel.Core,
            "a", SkillLifecycleState.Discovered, null);
        var project = new DiscoveredSkill("project", manifest with { Sealed = false }, ScopeLevel.Project,
            TrustLevel.Project, "b", SkillLifecycleState.Discovered, null);

        var sealedResult = SkillManifestLoader.ResolvePrecedence([builtIn, project]);
        Assert.Equal(SkillLifecycleState.Discovered, Assert.Single(sealedResult, item => item.Scope == ScopeLevel.BuiltIn).State);
        Assert.Equal(SkillLifecycleState.Conflict, Assert.Single(sealedResult, item => item.Scope == ScopeLevel.Project).State);

        var trustedUser = new DiscoveredSkill("user", manifest with { Sealed = false }, ScopeLevel.User,
            TrustLevel.Trusted, "c", SkillLifecycleState.Discovered, null);
        var lessTrustedProject = project with { Scope = ScopeLevel.Project };
        Assert.All(SkillManifestLoader.ResolvePrecedence([trustedUser, lessTrustedProject]), item =>
            Assert.Equal(SkillLifecycleState.Conflict, item.State));
    }

    [Fact]
    public async System.Threading.Tasks.Task Host_skills_query_hides_untrusted_project_and_client_action_lists_only_metadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-skills-host-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var workspace = Path.Combine(root, "workspace");
        var skillDirectory = Path.Combine(workspace, ".omnicore", "skills", "progress.compile");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(skillDirectory, SkillManifestLoader.ManifestFileName), Manifest);
        File.WriteAllText(Path.Combine(skillDirectory, SkillManifestLoader.InstructionsFileName), "PRIVATE_SKILL_INSTRUCTIONS");
        var paths = OmniHost.CreatePlatformPaths(data);
        var server = OmniHost.CreateInMemoryServer();
        server.ConfigurePlatformPaths(() => paths);
        server.ConfigureWorkspaceRoot(workspace);
        try
        {
            using (var untrusted = JsonDocument.Parse(server.Query("skills", CancellationToken.None)!.Json))
            {
                Assert.False(untrusted.RootElement.GetProperty("workspaceTrusted").GetBoolean());
                Assert.Empty(untrusted.RootElement.GetProperty("skills").EnumerateArray());
            }

            var start = server.Send(WireEnvelope.Command(Ids.NewV7(),
                "{" + JsonObj.Field("cmd", "session.input") + "," + JsonObj.Field("text", "start fixture session") + "}"),
                CancellationToken.None);
            Assert.Equal("ok", start.Status);
            var deniedActivation = await new ClientActionHandler().ExecuteAsync(
                new ClientActionInvocation(ClientActionIds.SkillsInspect, ["load", "progress.compile"], "slash"),
                new ClientContext(server, Localization.Spanish()), CancellationToken.None);
            Assert.Contains("Trust the workspace", deniedActivation.Text);

            new WorkspaceTrustStore(paths).SetTrusted(workspace, true);
            var result = await new ClientActionHandler().ExecuteAsync(
                new ClientActionInvocation(ClientActionIds.SkillsInspect, [], "slash"),
                new ClientContext(server, Localization.Spanish()), CancellationToken.None);
            Assert.Contains("progress.compile", result.Text);
            Assert.Contains("Discovered", result.Text);
            Assert.DoesNotContain("PRIVATE_SKILL_INSTRUCTIONS", result.Text);

            var activate = await new ClientActionHandler().ExecuteAsync(
                new ClientActionInvocation(ClientActionIds.SkillsInspect, ["load", "progress.compile"], "slash"),
                new ClientContext(server, Localization.Spanish()), CancellationToken.None);
            Assert.Contains("Activated", activate.Text);
            var release = await new ClientActionHandler().ExecuteAsync(
                new ClientActionInvocation(ClientActionIds.SkillsInspect, ["release", "progress.compile"], "slash"),
                new ClientContext(server, Localization.Spanish()), CancellationToken.None);
            Assert.Contains("Discovered", release.Text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task Session_activation_persists_by_session_and_context_is_materialized_only_for_that_session()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-session-skills-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, ".omnicore", "skills", "progress.compile");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, SkillManifestLoader.ManifestFileName), Manifest);
        File.WriteAllText(Path.Combine(directory, SkillManifestLoader.InstructionsFileName), "Build progress sources safely.");
        var data = Path.Combine(root, "runtime-data");
        var session = SessionId.New();
        try
        {
            var loader = new SkillManifestLoader();
            var skill = Assert.Single(loader.Discover(Path.Combine(root, ".omnicore", "skills"), workspaceTrusted: true));
            var loaded = loader.Activate(skill);
            var store = new SessionSkillActivationStore(data);
            store.Activate(session, skill.Manifest.Id!, skill.ManifestSha256);
            var reopened = new SessionSkillActivationStore(data);
            Assert.Equal(skill.ManifestSha256, reopened.Read(session)[skill.Manifest.Id!]);
            Assert.Empty(reopened.Read(SessionId.New()));

            var fingerprint = new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build");
            var request = new MaterializeRequest(session, RunId.New(), null, null, null, 0, fingerprint);
            var items = await new SkillContextContributor(session, loaded).GetContextAsync(request, CancellationToken.None);
            var item = Assert.Single(items);
            Assert.Equal(ContextItemKind.Skill, item.Kind);
            Assert.Contains("Build progress sources safely", item.Content);
            Assert.Equal(ContributionCategory.Skills, item.Provenance.Category);

            var otherSession = new MaterializeRequest(SessionId.New(), RunId.New(), null, null, null, 0, fingerprint);
            Assert.Empty(await new SkillContextContributor(session, loaded).GetContextAsync(otherSession, CancellationToken.None));
            Assert.True(reopened.Release(session, skill.Manifest.Id!));
            Assert.Empty(reopened.Read(session));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
