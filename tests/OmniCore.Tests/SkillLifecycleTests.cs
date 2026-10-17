namespace OmniCore.Tests;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;

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
    public void Skill_source_catalog_keeps_workspace_runtime_scope_available_without_trusting_project_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-skill-source-roots-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "repo");
        var user = Path.Combine(root, "user-data");
        var workspaceData = Path.Combine(root, "workspace-data");
        var session = SessionId.New();
        try
        {
            static void Add(string path, string id)
            {
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, SkillManifestLoader.ManifestFileName),
                    $"id: {id}\nversion: 1\n");
                File.WriteAllText(Path.Combine(path, SkillManifestLoader.InstructionsFileName), "fixture");
            }
            Add(Path.Combine(project, ".omnicore", "skills", "project.skill"), "project.skill");
            Add(Path.Combine(user, "skills", "user.skill"), "user.skill");
            Add(Path.Combine(workspaceData, "skills", "workspace.skill"), "workspace.skill");
            Add(Path.Combine(workspaceData, "sessions", session.ToString(), "skills", "session.skill"), "session.skill");

            var catalog = new SkillSourceCatalog();
            var untrusted = catalog.Discover(project, user, workspaceData, session, workspaceTrusted: false);
            Assert.Equal(new[] { "session.skill", "user.skill", "workspace.skill" },
                untrusted.Where(skill => skill.Scope != ScopeLevel.BuiltIn)
                    .Select(skill => skill.Manifest.Id).Order(StringComparer.Ordinal));
            Assert.All(untrusted.Where(skill => skill.Scope == ScopeLevel.BuiltIn),
                skill => Assert.Equal(TrustLevel.Core, skill.Trust));
            var trusted = catalog.Discover(project, user, workspaceData, session, workspaceTrusted: true);
            Assert.Contains(trusted, skill => skill.Manifest.Id == "project.skill");
            Assert.Contains(trusted, skill => skill.Scope == ScopeLevel.Workspace
                && skill.Manifest.Id == "workspace.skill");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
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
    public void Discovery_supports_declared_skill_scopes_and_keeps_trust_boundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-skill-scopes-" + Guid.NewGuid().ToString("N"));
        try
        {
            var loader = new SkillManifestLoader();
            foreach (var (scope, trust, workspaceTrusted) in new[]
            {
                (ScopeLevel.BuiltIn, TrustLevel.Core, false),
                (ScopeLevel.User, TrustLevel.Trusted, false),
                (ScopeLevel.Project, TrustLevel.Project, true),
                (ScopeLevel.Workspace, TrustLevel.Project, true),
                (ScopeLevel.Session, TrustLevel.Trusted, false),
            })
            {
                var scopeRoot = Path.Combine(root, scope.ToString());
                var package = Path.Combine(scopeRoot, "compile");
                Directory.CreateDirectory(package);
                File.WriteAllText(Path.Combine(package, SkillManifestLoader.ManifestFileName), Manifest);
                var discoveredSkills = loader.Discover(scopeRoot, scope, trust, workspaceTrusted);
                Assert.True(discoveredSkills.Count > 0, $"No skills discovered for {scope} with {trust}.");
                var discovered = Assert.Single(discoveredSkills);
                Assert.Equal(scope, discovered.Scope);
                Assert.Equal(trust, discovered.Trust);
            }

            var untrustedRoot = Path.Combine(root, "untrusted");
            Directory.CreateDirectory(Path.Combine(untrustedRoot, "compile"));
            File.WriteAllText(Path.Combine(untrustedRoot, "compile", SkillManifestLoader.ManifestFileName), Manifest);
            Assert.Empty(loader.Discover(untrustedRoot, ScopeLevel.User, TrustLevel.Untrusted));
            Assert.Empty(loader.Discover(untrustedRoot, ScopeLevel.Project, TrustLevel.Project, workspaceTrusted: false));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Skill_selector_enforces_eligibility_model_invocation_and_harness_context_budgets()
    {
        var manifest = new SkillManifest { Id = "build.compile", Version = "1", BudgetTokens = 100 };
        DiscoveredSkill Candidate(string id, SkillLifecycleState state, int priority = 0, bool modelInvocable = false) =>
            new("fixture", manifest with { Id = id, Priority = priority, ModelInvocable = modelInvocable },
                ScopeLevel.Project, TrustLevel.Project, "hash", state, null);

        var selector = new SkillSelector();
        var smallHarness = new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 6,
            GuidanceLevel.Full, 1, PlanControl.RuntimeDriven, 1);
        var limits = SkillSelectionLimits.FromHarness(smallHarness, contextTokenBudget: 180);
        var automatic = selector.Select([
            Candidate("build.compile", SkillLifecycleState.Eligible, priority: 2),
            Candidate("lint.project", SkillLifecycleState.Eligible, priority: 1),
            Candidate("hidden.skill", SkillLifecycleState.Discovered),
        ], SkillSelectionRequestKind.Automatic, limits);
        Assert.Equal(["build.compile"], automatic.Selected.Select(item => item.Manifest.Id));
        Assert.Equal(100, automatic.ReservedContextTokens);
        Assert.Contains(automatic.Decisions, item => item.Skill.Manifest.Id == "lint.project"
            && item.Disposition == SkillSelectionDisposition.ContextBudgetExceeded);
        Assert.Contains(automatic.Decisions, item => item.Skill.Manifest.Id == "hidden.skill"
            && item.Disposition == SkillSelectionDisposition.NotEligible);

        var modelRequest = selector.Select([Candidate("model.allowed", SkillLifecycleState.Discovered, modelInvocable: true)],
            SkillSelectionRequestKind.ModelExplicit, limits, "model.allowed");
        Assert.Equal("model.allowed", Assert.Single(modelRequest.Selected).Manifest.Id);
        var deniedModelRequest = selector.Select([Candidate("user.only", SkillLifecycleState.Discovered)],
            SkillSelectionRequestKind.ModelExplicit, limits, "user.only");
        Assert.Equal(SkillSelectionDisposition.ModelInvocationDisabled,
            Assert.Single(deniedModelRequest.Decisions).Disposition);

        var noBudget = selector.Select([Candidate("build.compile", SkillLifecycleState.Eligible)],
            SkillSelectionRequestKind.Automatic, limits with { ContextTokenBudget = 99 });
        Assert.Empty(noBudget.Selected);
        Assert.Equal(SkillSelectionDisposition.ContextBudgetExceeded, Assert.Single(noBudget.Decisions).Disposition);
    }

    [Fact]
    public void Skill_instruction_load_enforces_manifest_token_budget()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-skill-budget-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "compile");
        Directory.CreateDirectory(package);
        var manifest = Manifest.Replace("priority: 5", "priority: 5\nbudgetTokens: 2", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(package, SkillManifestLoader.ManifestFileName), manifest);
        File.WriteAllText(Path.Combine(package, SkillManifestLoader.InstructionsFileName), "instructions exceed two tokens");
        try
        {
            var loader = new SkillManifestLoader();
            var skill = Assert.Single(loader.Discover(root, workspaceTrusted: true));
            Assert.Throws<InvalidDataException>(() => loader.Activate(skill));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Session_skill_store_keeps_identical_ids_isolated_by_scope_and_reopens_them()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-skill-scope-store-" + Guid.NewGuid().ToString("N"));
        var session = SessionId.New();
        const string userHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string sessionHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        try
        {
            var store = new SessionSkillActivationStore(root);
            store.Activate(session, ScopeLevel.User, "shared.skill", userHash);
            store.Activate(session, ScopeLevel.Session, "shared.skill", sessionHash);
            var reopened = new SessionSkillActivationStore(root);
            var loaded = reopened.ReadAll(session);
            Assert.Equal(2, loaded.Count);
            Assert.Equal(userHash, loaded[new SkillActivationIdentity(ScopeLevel.User, "shared.skill")]);
            Assert.Equal(sessionHash, loaded[new SkillActivationIdentity(ScopeLevel.Session, "shared.skill")]);
            Assert.Empty(reopened.Read(session)); // legacy view remains Project-only
            Assert.True(reopened.Release(session, ScopeLevel.User, "shared.skill"));
            Assert.Single(reopened.ReadAll(session));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Automatic_skills_do_not_spend_a_slot_twice_and_release_suppresses_same_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-skill-auto-" + Guid.NewGuid().ToString("N"));
        var session = SessionId.New();
        var workspaceData = Path.Combine(root, "data");
        Directory.CreateDirectory(root);
        try
        {
            DiscoveredSkill Add(string id, int priority)
            {
                var directory = Path.Combine(root, id);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "skill.yaml"), $"id: {id}\nversion: 1\npriority: {priority}\nbudgetTokens: 20\n");
                File.WriteAllText(Path.Combine(directory, "instructions.md"), "fixture skill instructions");
                return Assert.Single(new SkillManifestLoader().Discover(root, workspaceTrusted: true),
                    skill => skill.Manifest.Id == id) with { State = SkillLifecycleState.Eligible };
            }
            var persisted = Add("persisted.skill", 100);
            var automatic = Add("automatic.skill", 1);
            var store = new SessionSkillActivationStore(workspaceData);
            var loadedPersisted = new SkillManifestLoader().Activate(persisted);
            store.Activate(session, ScopeLevel.Project, "persisted.skill", persisted.ManifestSha256,
                loadedPersisted.PackageContentSha256!);
            var lifecycle = new List<SkillActivationChanged>();
            var composition = new SkillTurnComposition(session, store, () => [persisted, automatic],
                new SkillSelectionLimits(2, 128), new FakeTokenCounter(), workspaceData,
                guidanceEnabled: true, includeWorkingState: false, workingState: "",
                emitLifecycle: lifecycle.Add);

            var selected = composition.Refresh();
            Assert.Equal(new[] { "automatic.skill", "persisted.skill" },
                selected.LoadedSkills.Select(skill => skill.Skill.Manifest.Id).Order(StringComparer.Ordinal));
            var autoIdentity = new SkillActivationIdentity(ScopeLevel.Project, "automatic.skill");
            var autoReceipt = store.ReadDetailed(session)[autoIdentity];
            Assert.Equal("Automatic", autoReceipt.Origin);
            Assert.Equal("Automatic", Assert.Single(lifecycle).Origin);

            Assert.True(store.Release(session, ScopeLevel.Project, "automatic.skill"));
            store.SuppressAutomatic(session, ScopeLevel.Project, "automatic.skill", autoReceipt.ManifestSha256,
                autoReceipt.PackageContentSha256);
            var afterRelease = composition.Refresh();
            Assert.Equal(new[] { "persisted.skill" }, afterRelease.LoadedSkills
                .Select(skill => skill.Skill.Manifest.Id).ToArray());
            Assert.True(store.HasAutomaticSuppression(session, ScopeLevel.Project, "automatic.skill",
                automatic.ManifestSha256));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Consented_extension_declared_skills_use_parent_package_identity_and_scope()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-extension-skill-source-" + Guid.NewGuid().ToString("N"));
        var userData = Path.Combine(root, "user-data");
        var extension = Path.Combine(userData, "extensions", "fixture.skills");
        var skill = Path.Combine(extension, "skills", "compile");
        Directory.CreateDirectory(skill);
        var paths = OmniHost.CreatePlatformPaths(userData);
        try
        {
            File.WriteAllText(Path.Combine(extension, "runtime.exe"), "offline fixture");
            File.WriteAllText(Path.Combine(extension, "omnicore-extension.yaml"), """
                manifestVersion: 1
                id: fixture.skills
                version: 1
                displayName: Fixture skills
                publisher: tests
                omnicoreApi: '1.0'
                provides:
                  skills: [skills/compile]
                requests:
                  capabilities: [skill.register]
                runtime:
                  kind: process
                  command: runtime.exe
                  args: []
                """);
            File.WriteAllText(Path.Combine(skill, "skill.yaml"), "id: extension.compile\nversion: 1\n");
            File.WriteAllText(Path.Combine(skill, "instructions.md"), "fixture from extension");
            var workspace = Path.Combine(root, "repo");
            Directory.CreateDirectory(workspace);
            var catalog = new SkillSourceCatalog();
            var ready = ProjectExtensionPackageCatalog.Ready(workspace, paths, workspaceTrusted: false);
            Assert.DoesNotContain(catalog.AddConsentedExtensionSkills([], ready), item =>
                item.Manifest.Id == "extension.compile");
            var package = Assert.Single(new ExtensionManifestLoader().Discover(
                Path.Combine(userData, "extensions"), TrustLevel.Trusted, workspaceTrusted: true));
            new ExtensionConsentStore(paths, ScopeLevel.User).Grant(package,
                new HashSet<string>(["skill.register"], StringComparer.Ordinal));
            ready = ProjectExtensionPackageCatalog.Ready(workspace, paths, workspaceTrusted: false);
            var discovered = Assert.Single(catalog.AddConsentedExtensionSkills([], ready));
            Assert.Equal(ScopeLevel.User, discovered.Scope);
            Assert.Equal(TrustLevel.Trusted, discovered.Trust);
            Assert.Equal("fixture.skills", discovered.ParentPackageId);
            var before = new SkillManifestLoader().Activate(discovered).PackageContentSha256;
            File.WriteAllText(Path.Combine(extension, "runtime.exe"), "package content changed");
            var changedManifest = Assert.Single(new ExtensionManifestLoader().Discover(
                Path.Combine(userData, "extensions"), TrustLevel.Trusted, workspaceTrusted: true));
            new ExtensionConsentStore(paths, ScopeLevel.User).Grant(changedManifest,
                new HashSet<string>(["skill.register"], StringComparer.Ordinal));
            var changedPackage = ProjectExtensionPackageCatalog.Ready(workspace, paths, workspaceTrusted: false);
            var changedSkill = Assert.Single(catalog.AddConsentedExtensionSkills([], changedPackage));
            Assert.NotEqual(before, new SkillManifestLoader().Activate(changedSkill).PackageContentSha256);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task Skill_file_contributions_are_bounded_loaded_and_package_identity_tracks_them()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-skill-contributions-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "contributed");
        Directory.CreateDirectory(Path.Combine(package, "workflows"));
        Directory.CreateDirectory(Path.Combine(package, "validators"));
        Directory.CreateDirectory(Path.Combine(package, "contributors"));
        Directory.CreateDirectory(Path.Combine(package, "references"));
        File.WriteAllText(Path.Combine(package, SkillManifestLoader.ManifestFileName), """
            id: docs.contribute
            version: 1
            budgetTokens: 512
            contributions:
              tools: ["ext.docs.lookup"]
              workflows: ["workflows/review.yaml"]
              validators: ["validators/check.json"]
              contributors: ["contributors/summary.md"]
              knowledge: ["references/protocol.md"]
            """);
        File.WriteAllText(Path.Combine(package, SkillManifestLoader.InstructionsFileName), "Use the declared docs workflow.");
        File.WriteAllText(Path.Combine(package, "workflows", "review.yaml"), "steps: [inspect, summarize]");
        File.WriteAllText(Path.Combine(package, "validators", "check.json"), "{\"id\":\"output-check\"}");
        File.WriteAllText(Path.Combine(package, "contributors", "summary.md"), "Add a short summary.");
        File.WriteAllText(Path.Combine(package, "references", "protocol.md"), "The protocol is versioned.");
        var session = SessionId.New();
        try
        {
            var loader = new SkillManifestLoader();
            var discovered = Assert.Single(loader.Discover(root, ScopeLevel.User, TrustLevel.Trusted));
            Assert.Contains("ext.docs.lookup", discovered.Manifest.Tools!);
            var loaded = loader.Activate(discovered);
            Assert.Equal(4, loaded.Resources!.Count);
            Assert.NotNull(loaded.PackageContentSha256);
            var materialize = new MaterializeRequest(session, RunId.New(), null, null, null, 0,
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"));
            var contextItems = await new SkillContextContributor(session, loaded)
                .GetContextAsync(materialize, CancellationToken.None);
            Assert.Contains(contextItems, item => item.Kind == ContextItemKind.Knowledge
                && item.Provenance.Category == ContributionCategory.Knowledge);
            Assert.Contains(contextItems, item => item.Kind == ContextItemKind.Skill
                && item.Content.Contains("Workflow template", StringComparison.Ordinal));
            Assert.DoesNotContain(contextItems, item => item.Kind == ContextItemKind.Memory);

            var store = new SessionSkillActivationStore(Path.Combine(root, "runtime"));
            store.Activate(session, ScopeLevel.User, discovered.Manifest.Id!, discovered.ManifestSha256,
                loaded.PackageContentSha256!);
            File.WriteAllText(Path.Combine(package, "references", "protocol.md"), "The protocol changed.");
            var sameManifest = Assert.Single(loader.Discover(root, ScopeLevel.User, TrustLevel.Trusted));
            Assert.Throws<InvalidDataException>(() => new SkillActivationResolver().LoadCandidates(
                [sameManifest], session, store));

            Assert.Throws<InvalidDataException>(() => loader.Load("""
                id: unsafe.ref
                version: 1
                contributions:
                  knowledge: ["../private.md"]
                """));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task Model_skill_load_uses_tool_pipeline_persists_session_selection_and_loads_next_context()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-skill-model-load-" + Guid.NewGuid().ToString("N"));
        var skillsRoot = Path.Combine(root, ".omnicore", "skills");
        var package = Path.Combine(skillsRoot, "progress.compile");
        var runtime = Path.Combine(root, "runtime");
        Directory.CreateDirectory(package);
        var manifest = Manifest.Replace("modelInvocable: false", "modelInvocable: true", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(package, SkillManifestLoader.ManifestFileName), manifest);
        const string instructions = "Build only through the approved compiler flow.";
        File.WriteAllText(Path.Combine(package, SkillManifestLoader.InstructionsFileName), instructions);
        var session = SessionId.New();
        try
        {
            var loader = new SkillManifestLoader();
            var candidate = Assert.Single(loader.Discover(skillsRoot, ScopeLevel.Project,
                TrustLevel.Project, workspaceTrusted: true));
            var index = new SkillIndexContributor(session, [candidate], enabled: true);
            var fingerprint = new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build");
            var materialize = new MaterializeRequest(session, RunId.New(), null, null, null, 0, fingerprint);
            var indexItem = Assert.Single(await index.GetContextAsync(materialize, CancellationToken.None));
            Assert.Contains("progress.compile", indexItem.Content);
            Assert.DoesNotContain(instructions, indexItem.Content);

            var store = new SessionSkillActivationStore(runtime);
            var tool = new SkillLoadTool(session, store, loader, [candidate]);
            var executor = new ScriptedToolExecutor(new FakeCatalog().Add(tool),
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()).WithModeDefaults(RunMode.Plan), root);
            var outcome = executor.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(),
                new ToolId("skill.load"), "model-load", "{\"id\":\"progress.compile\"}"),
                userApprovesAsk: true, CancellationToken.None);
            Assert.True(outcome.Succeeded, outcome.Summary);
            Assert.Equal(candidate.ManifestSha256, Assert.Single(store.Read(session)).Value);

            var nextTurnSkill = Assert.Single(new SkillActivationResolver().Load(skillsRoot, session,
                new SessionSkillActivationStore(runtime), workspaceTrusted: true));
            var context = new SkillContextContributor(session, nextTurnSkill);
            var loadedItem = Assert.Single(await context.GetContextAsync(materialize, CancellationToken.None));
            Assert.Equal(ContextItemKind.Skill, loadedItem.Kind);
            Assert.Contains(instructions, loadedItem.Content);
        }
        finally { Directory.Delete(root, recursive: true); }
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
                var visible = untrusted.RootElement.GetProperty("skills").EnumerateArray().ToArray();
                Assert.DoesNotContain(visible, skill => skill.GetProperty("scope").GetString() == "Project");
                Assert.Contains(visible, skill => skill.GetProperty("id").GetString() == "visual-explainer"
                    && skill.GetProperty("scope").GetString() == "BuiltIn");
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
            Assert.Contains("detectada", result.Text);
            Assert.DoesNotContain("PRIVATE_SKILL_INSTRUCTIONS", result.Text);

            var activate = await new ClientActionHandler().ExecuteAsync(
                new ClientActionInvocation(ClientActionIds.SkillsInspect, ["load", "progress.compile"], "slash"),
                new ClientContext(server, Localization.Spanish()), CancellationToken.None);
            Assert.Contains("activada", activate.Text);
            var release = await new ClientActionHandler().ExecuteAsync(
                new ClientActionInvocation(ClientActionIds.SkillsInspect, ["release", "progress.compile"], "slash"),
                new ClientContext(server, Localization.Spanish()), CancellationToken.None);
            Assert.Contains("detectada", release.Text);
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
