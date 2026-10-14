using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Tools;
using OmniCore.Sandbox;

namespace OmniCore.Tests;

public sealed class InternalCompletionCommandTests
{
    private sealed class LifecycleTestHook(HookRegistration registration, string validatorId) : IHook
    {
        public HookRegistration Registration { get; } = registration;
        public ValueTask<HookResult> ExecuteAsync(HookContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<HookResult>(new HookRequireValidation(validatorId, "fixture requires validation"));
    }

    private sealed class LifecycleRestrictionTestHook(HookRegistration registration) : IHook
    {
        public HookRegistration Registration { get; } = registration;
        public ValueTask<HookResult> ExecuteAsync(HookContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<HookResult>(new HookRestrict(
                new HashSet<string>(["process.exec"], StringComparer.Ordinal), "fixture restricts process"));
    }

    [Fact]
    public void Skill_validator_pages_preserve_multiline_content_and_reject_mixed_versions()
    {
        const string firstHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string changedHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var joined = new System.Text.StringBuilder();
        string? version = null;
        Assert.True(ScriptedToolExecutor.TryAppendSkillValidationPage(
            "[lines:1-1/2;hasMore:true;contentTruncated:false]\nfirst line\n\n\n[version:" + firstHash + "]", joined,
            ref version, out var more));
        Assert.True(more);
        Assert.True(ScriptedToolExecutor.TryAppendSkillValidationPage(
            "[lines:2-2/2;hasMore:false;contentTruncated:false]\nsecond line\n\n\n[version:" + firstHash + "]",
            joined, ref version, out more));
        Assert.False(more);
        Assert.Contains("first line\nsecond line", joined.ToString(), StringComparison.Ordinal);

        var mixed = new System.Text.StringBuilder();
        string? mixedVersion = null;
        Assert.True(ScriptedToolExecutor.TryAppendSkillValidationPage(
            "[lines:1-1/2;hasMore:true;contentTruncated:false]\nfirst\n\n[version:" + firstHash + "]",
            mixed, ref mixedVersion, out more));
        Assert.False(ScriptedToolExecutor.TryAppendSkillValidationPage(
            "[lines:2-2/2;hasMore:false;contentTruncated:false]\nsecond\n\n[version:" + changedHash + "]",
            mixed, ref mixedVersion, out more));
    }

    [Fact]
    public void Delegated_skill_snapshot_does_not_reconcile_or_import_principal_session_skills()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-delegated-skill-snapshot-" + Guid.NewGuid().ToString("N"));
        var session = SessionId.New();
        const string manifestA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string manifestB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string contentA = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        const string contentB = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        try
        {
            var store = new SessionSkillActivationStore(root);
            store.Activate(session, ScopeLevel.User, "parent.user", manifestA, contentA, "User");
            store.ActivateAutomatic(session, ScopeLevel.Project, "parent.automatic", manifestB, contentB);
            var before = store.ReadDetailed(session);

            // This is the snapshot chosen by OmniCliRuntime for delegation != null. The accepted
            // context packet is added by ExplorerTurn separately; principal skills are not.
            var snapshot = SkillTurnComposition.CreateDelegatedSnapshot(new FakeTokenCounter());

            Assert.Empty(snapshot.Candidates);
            Assert.Empty(snapshot.LoadedSkills);
            Assert.Empty(snapshot.Fingerprints);
            Assert.Empty(snapshot.ToolIds);
            Assert.False(snapshot.Validators.HasValidators);
            Assert.Empty(snapshot.Materializer.Contributors());
            Assert.Equal(before, store.ReadDetailed(session));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Skill_validator_runs_at_the_real_act_completion_gate_and_blocks_completion_when_it_fails()
    {
        using var setup = new Setup();
        var manifest = new SkillManifest { Id = "fixture.validator", Version = "1", BudgetTokens = 64 };
        var skill = new DiscoveredSkill(setup.Root, manifest, ScopeLevel.Session, TrustLevel.Trusted,
            new string('a', 64), SkillLifecycleState.Loaded, null);
        const string rule = "{\"id\":\"output-check\",\"kind\":\"file-contains\",\"path\":\"output.txt\",\"text\":\"verified\"}";
        var resource = new LoadedSkillResource(SkillResourceKind.Validator, "validators/output.json", rule,
            new string('b', 64));
        var validator = new SkillCompletionValidator([new LoadedSkill(skill, "fixture instructions",
            new string('c', 64), [resource], new string('d', 64))]);
        var calls = 0;
        var catalog = new FakeCatalog();
        var turn = new ExplorerTurn((_, _) =>
        {
            calls++;
            return new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
        }, ScriptedToolExecutor.WithWorkspace(catalog,
            new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), setup.Root), catalog,
            new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
            new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null),
            setup.Store, setup.Codecs, setup.Artifacts, new InMemoryAuditSink(), new RedactionPolicy());

        var exit = OmniCliRuntime.Create(setup.Root).RunActLoop(turn, _ => { }, "objective", "instructions",
            setup.Session, setup.Run, setup.Lane, "", null, null, setup.Server, setup.Artifacts,
            new InMemoryAuditSink(), null, interactive: false, "en", TestContext.Current.CancellationToken,
            subsequentTurnFactory: _ => null, skillValidators: validator);

        Assert.Equal(1, exit);
        Assert.Equal(1, calls);
        var events = setup.Store.ReadFrom(setup.Session, 1);
        Assert.Contains(events, evt => evt.Type.ToString() == "run.validation_rejected");
        Assert.DoesNotContain(events, evt => evt.Type.ToString() == "run.completed");
    }

    [Fact]
    public void Unknown_required_validator_preflights_configured_build_before_any_gate_effect()
    {
        using var setup = new Setup();
        static IHook Requires(string id, string validatorId) => new LifecycleTestHook(new HookRegistration(
            id, new ComponentSource(SourceKind.Extension, ScopeLevel.Project, TrustLevel.Core, "fixture", "1"),
            LifecyclePoint.BeforeValidation, new HashSet<HookCapability>([HookCapability.RequireValidation]),
            new HashSet<HookCapability>([HookCapability.RequireValidation]), HookArgumentAccess.None,
            Enabled: true, WorkspaceTrusted: true), validatorId);
        var hooks = new[] { Requires("fixture.requires_unknown", "fixture.missing"),
            Requires("fixture.requires_build", "build") };
        var catalog = new FakeCatalog();
        var turn = new ExplorerTurn((_, _) => new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null)),
            ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), setup.Root), catalog,
            new ContextMaterializer(new FakeTokenCounter(), []),
            new ExecutionFingerprint("scripted", "h", "t", "c", "o", "test"),
            new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
            setup.Store, setup.Codecs, setup.Artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
            lifecycleHooks: hooks);

        _ = OmniCliRuntime.Create(setup.Root).RunActLoop(turn, _ => { }, "objective", "instructions",
            setup.Session, setup.Run, setup.Lane, "", new WorkspaceGatesYaml { Build = ["fixture-build"] },
            null, setup.Server, setup.Artifacts, new InMemoryAuditSink(), null, interactive: false, "en",
            TestContext.Current.CancellationToken);

        var events = setup.Store.ReadFrom(setup.Session, 1).Select(setup.Codecs.Decode).ToArray();
        Assert.Contains(events.OfType<HookValidationResolved>(), item => item.ValidatorId == "fixture.missing"
            && !item.Passed);
        Assert.Contains(events.OfType<HookValidationResolved>(), item => item.ValidatorId == "build" && !item.Passed);
        Assert.DoesNotContain(events, item => item is ToolCallRequested requested
            && requested.ToolName == "process.exec");
        Assert.DoesNotContain(events, item => item is ToolCallStarted);
    }

    [Fact]
    public void Before_validation_restriction_latches_through_after_validation_without_gate_or_rpc()
    {
        using var setup = new Setup();
        static HookRegistration Registration(string id, LifecyclePoint point, HookCapability capability) =>
            new(id, new ComponentSource(SourceKind.Extension, ScopeLevel.Project, TrustLevel.Core, "fixture", "1"),
                point, new HashSet<HookCapability>([capability]), new HashSet<HookCapability>([capability]),
                HookArgumentAccess.None, Enabled: true, WorkspaceTrusted: true);
        var validatorId = "extension.fixture.h_validator.verify-build";
        var hooks = new IHook[]
        {
            new LifecycleRestrictionTestHook(Registration("fixture.block_validation", LifecyclePoint.BeforeValidation,
                HookCapability.Restrict)),
            new LifecycleTestHook(Registration("fixture.requires_after_validation", LifecyclePoint.AfterValidation,
                HookCapability.RequireValidation), validatorId),
        };
        var manifest = new ExtensionManifest
        {
            ManifestVersion = 1,
            Id = "fixture.h_validator",
            Version = "1",
            DisplayName = "Fixture validator",
            Publisher = "tests",
            OmnicoreApi = "1.0",
            Runtime = new ExtensionRuntimeManifest { Kind = "process", Command = "runtime.exe", Args = [] },
            Provides = new ExtensionProvidesManifest { Validators = ["verify-build"] },
        };
        var extension = new DiscoveredExtension(setup.Root, manifest, ExtensionDiscoveryStatus.Ready,
            null, new string('a', 64), new string('b', 64), TrustLevel.Project);
        var launcher = new ExtensionFixtureLauncher();
        var validator = new SkillCompletionValidator([], [new ExtensionCompletionValidator(extension,
            ["verify-build"], setup.Root, setup.Session, setup.Run, new ExtensionRpcProcessInvoker(launcher))]);
        var catalog = new FakeCatalog();
        var turn = new ExplorerTurn((_, _) => new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("scripted", "test", null)),
            ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), setup.Root), catalog,
            new ContextMaterializer(new FakeTokenCounter(), []),
            new ExecutionFingerprint("scripted", "h", "t", "c", "o", "test"),
            new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
            setup.Store, setup.Codecs, setup.Artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
            lifecycleHooks: hooks);

        _ = OmniCliRuntime.Create(setup.Root).RunActLoop(turn, _ => { }, "objective", "instructions",
            setup.Session, setup.Run, setup.Lane, "", new WorkspaceGatesYaml { Build = ["fixture-build"] },
            null, setup.Server, setup.Artifacts, new InMemoryAuditSink(), null, interactive: false, "en",
            TestContext.Current.CancellationToken, skillValidators: validator);

        var events = setup.Store.ReadFrom(setup.Session, 1).Select(setup.Codecs.Decode).ToArray();
        Assert.Empty(launcher.Methods);
        Assert.DoesNotContain(events, item => item is ToolCallRequested requested
            && requested.ToolName == "process.exec");
        Assert.DoesNotContain(events, item => item is ToolCallStarted);
        Assert.Contains(events.OfType<HookApplied>(), item => item.Point == LifecyclePoint.AfterValidation.ToString()
            && item.Result == "validation_required" && item.DetailIds?.Contains(validatorId) == true);
        Assert.Contains(events.OfType<HookValidationResolved>(), item => item.ValidatorId == validatorId
            && !item.Passed);
        Assert.DoesNotContain(events, item => item is RunCompleted);
    }

    [Fact]
    public void Same_run_act_factory_refreshes_skill_context_tools_fingerprint_and_validator_after_skill_load()
    {
        using var setup = new Setup();
        var paths = OmniHost.CreatePlatformPaths(Path.Combine(setup.Root, "user-data"));
        var extensionPackage = Path.Combine(setup.Root, ".omnicore", "extensions", "fixture.package");
        Directory.CreateDirectory(extensionPackage);
        File.WriteAllText(Path.Combine(extensionPackage, "runtime.exe"), "fixture runtime");
        File.WriteAllText(Path.Combine(extensionPackage, "schema.json"), "{\"type\":\"object\"}");
        File.WriteAllText(Path.Combine(extensionPackage, ExtensionManifestLoader.ManifestFileName), """
            manifestVersion: 1
            id: fixture.package
            version: 1
            displayName: Fixture package
            publisher: tests
            omnicoreApi: '1.0'
            provides:
              tools:
                - name: compile
                  schema: schema.json
              contextContributors: [knowledge.note]
              validators: [verify-build]
            requests:
              capabilities: [tool.register, context.contribute, completion.validate]
              permissions:
                process:
                  - executable: runtime.exe
            runtime:
              kind: process
              command: runtime.exe
              args: []
            """);
        new WorkspaceTrustStore(paths).SetTrusted(setup.Root, true);
        var extensionCandidate = Assert.Single(new ExtensionManifestLoader().Discover(
            Path.Combine(setup.Root, ".omnicore", "extensions"), TrustLevel.Project, workspaceTrusted: true));
        new ExtensionConsentStore(paths, setup.Root).Grant(extensionCandidate,
            new HashSet<string>(["tool.register", "context.contribute", "completion.validate"], StringComparer.Ordinal));
        var extensionLauncher = new ExtensionFixtureLauncher();
        var extensionInvoker = new ExtensionRpcProcessInvoker(extensionLauncher);
        var package = Path.Combine(setup.Root, ".omnicore", "skills", "fixture.skill");
        Directory.CreateDirectory(Path.Combine(package, "validators"));
        File.WriteAllText(Path.Combine(package, "skill.yaml"), """
            id: fixture.skill
            version: 1
            modelInvocable: true
            budgetTokens: 256
            tools: [ext.fixture.package.compile]
            contributions:
              validators: [validators/output.json]
            """);
        const string instructions = "Apply the active compile guidance on the next Turn.";
        File.WriteAllText(Path.Combine(package, "instructions.md"), instructions);
        File.WriteAllText(Path.Combine(package, "validators", "output.json"),
            "{\"id\":\"output-exists\",\"kind\":\"file-exists\",\"path\":\"output.txt\"}");
        File.WriteAllText(Path.Combine(setup.Root, "output.txt"), "verified");

        var dataDirectory = Path.Combine(setup.Root, "user-data");
        var workspaceData = Path.Combine(setup.Root, "workspace-data");
        var activationStore = new SessionSkillActivationStore(dataDirectory);
        var sources = new SkillSourceCatalog();
        IReadOnlyList<DiscoveredSkill> Discover() => sources.Discover(setup.Root, dataDirectory,
            workspaceData, setup.Session, workspaceTrusted: true);
        var composition = new SkillTurnComposition(setup.Session, activationStore, Discover,
            new SkillSelectionLimits(4, 1024), new FakeTokenCounter(), workspaceData,
            guidanceEnabled: true, includeWorkingState: false, workingState: "",
            extensionValidatorFactory: () => ProjectExtensionValidators.Load(setup.Root, paths,
                workspaceTrusted: true, setup.Session, setup.Run, extensionInvoker),
            extensionContributorFactory: () => ProjectExtensionContributors.Load(setup.Root, paths,
                workspaceTrusted: true, extensionInvoker));
        var skillLoad = new SkillLoadTool(setup.Session, activationStore, new SkillManifestLoader(), Discover,
            new SkillSelectionLimits(4, 1024));
        var requests = new List<ModelRequest>();
        var snapshots = new List<SkillTurnCompositionSnapshot>();
        var modelCall = 0;
        ExplorerTurn BuildTurn()
        {
            var snapshot = composition.Refresh();
            snapshots.Add(snapshot);
            var catalog = new FakeCatalog().Add(new ReadFileTool(new PathBoundaryValidator()));
            var extensionTools = ProjectExtensionTools.Load(setup.Root, paths,
                workspaceTrusted: true, extensionInvoker);
            foreach (var extensionTool in extensionTools) catalog.Add(extensionTool);
            var extensionBoundaries = extensionTools.OfType<ExtensionTool>()
                .Select(tool => (OmniCore.Abstractions.IToolIntentBoundary)tool.Boundary).ToArray();
            catalog.Add(skillLoad);
            var permission = new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
            {
                ["skill.load"] = PermissionDecision.Allow,
                ["filesystem.read"] = PermissionDecision.Allow,
                ["ext.fixture.package.compile"] = PermissionDecision.Allow,
            }).WithModeDefaults(RunMode.Act);
            var executor = new ScriptedToolExecutor(catalog, permission, setup.Root, boundary: null,
                audit: null, interactionResponder: interaction =>
                    interaction.Kind == InteractionKind.Permission ? "allow_once" : null,
                isInteractive: true,
                additionalBoundaries: extensionBoundaries);
            return new ExplorerTurn((request, _) =>
            {
                requests.Add(request);
                var current = modelCall++;
                if (current == 0)
                    return new ModelResponse([new ToolCallBlock(ToolCallId.New(), "load-skill", "skill.load",
                            "{\"id\":\"fixture.skill\"}")], StopReason.ToolUse,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
                if (current == 2)
                    return new ModelResponse([new ToolCallBlock(ToolCallId.New(), "extension-compile",
                        "ext.fixture.package.compile", "{}")], StopReason.ToolUse,
                        new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
                return new ModelResponse([new TextBlock("ready")], StopReason.EndTurn,
                    new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null));
            }, executor, catalog,
                snapshot.Materializer, new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Discovered, null),
                setup.Store, setup.Codecs, setup.Artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                recordEffectiveFingerprint: true, activeSkills: snapshot.Fingerprints,
                activeSkillToolIds: snapshot.ToolIds);
        }

        var responderCall = 0;
        var diagnostics = new List<string>();
        var runtime = OmniCliRuntime.Create(setup.Root);
        var firstTurn = BuildTurn();
        var exit = runtime.RunActLoop(firstTurn, diagnostics.Add, "load skill", "instructions",
            setup.Session, setup.Run, setup.Lane, "", new WorkspaceGatesYaml { Acceptance = true }, null,
            setup.Server, setup.Artifacts, new InMemoryAuditSink(), null, interactive: true, "en",
            TestContext.Current.CancellationToken,
            acceptanceResponder: _ => responderCall++ == 0 ? "revise" : "accept",
            subsequentTurnFactory: _ => BuildTurn(), skillValidatorFactory: () =>
                snapshots.Count == 0 ? null : snapshots[^1].Validators);

        Assert.True(exit == 0, string.Join("\n", diagnostics));
        Assert.Equal(4, requests.Count);
        Assert.Contains(requests[0].Tools, tool => tool.Name == "skill.load");
        Assert.DoesNotContain(requests[1].Tools, tool => tool.Name == "ext.fixture.package.compile");
        Assert.Contains(requests[2].Tools, tool => tool.Name == "ext.fixture.package.compile");
        Assert.Contains(instructions, requests[2].Instructions ?? "", StringComparison.Ordinal);
        Assert.Contains("offline extension contribution", requests[2].Instructions ?? "", StringComparison.Ordinal);
        Assert.Contains(extensionLauncher.Methods, method => method == "context/contribute");
        Assert.Contains(extensionLauncher.Methods, method => method == "completion/validate");
        Assert.Equal(1, extensionLauncher.Methods.Count(method => method == "tools/call"));
        var extensionLaunch = extensionLauncher.LastLaunch!;
        Assert.Equal(SandboxStrength.Strong, extensionLaunch.RequestedStrength);
        Assert.Equal(SandboxNetworkMode.Deny, extensionLaunch.Network.Mode);
        Assert.Empty(snapshots[0].Fingerprints);
        Assert.Empty(snapshots[0].LoadedSkills);
        Assert.True(snapshots[0].Validators.HasValidators); // The separately consented extension validator is already active.
        Assert.Single(snapshots[1].Fingerprints);
        Assert.True(snapshots[1].Validators.HasValidators);
        var turns = setup.Store.ReadFrom(setup.Session, 1).Select(setup.Codecs.Decode).OfType<TurnStarted>().ToArray();
        Assert.Equal(2, turns.Length);
        Assert.NotEqual(turns[0].Fingerprint!.Components.Single(item => item.Name == "skills.active").Hash,
            turns[1].Fingerprint!.Components.Single(item => item.Name == "skills.active").Hash);
        Assert.Contains(setup.Store.ReadFrom(setup.Session, 1).Select(setup.Codecs.Decode),
            evt => evt is RunCompleted);
        Assert.True(activationStore.Release(setup.Session, ScopeLevel.Project, "fixture.skill"));
        var releasedSnapshot = composition.Refresh();
        Assert.Empty(releasedSnapshot.Fingerprints);
        Assert.Empty(releasedSnapshot.ToolIds);
        Assert.Empty(releasedSnapshot.LoadedSkills);
        Assert.True(releasedSnapshot.Validators.HasValidators); // Installed extension validator survives skill release.
        var releasedValidation = releasedSnapshot.Validators.Run(setup.Root, _ => null,
            TestContext.Current.CancellationToken);
        Assert.Equal("extension:fixture.package:verify-build", Assert.Single(releasedValidation).Key);
    }

    [Fact]
    public void Consent_bound_extension_validator_result_reaches_the_real_completion_gate()
    {
        using var setup = new Setup();
        var paths = OmniHost.CreatePlatformPaths(Path.Combine(setup.Root, "user-data"));
        var package = Path.Combine(setup.Root, ".omnicore", "extensions", "fixture.validator");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "runtime.exe"), "fixture runtime");
        File.WriteAllText(Path.Combine(package, ExtensionManifestLoader.ManifestFileName), """
            manifestVersion: 1
            id: fixture.validator
            version: 1
            displayName: Fixture validator
            publisher: tests
            omnicoreApi: '1.0'
            provides:
              validators: [verify-build]
            requests:
              capabilities: [completion.validate]
              permissions:
                process:
                  - executable: runtime.exe
            runtime:
              kind: process
              command: runtime.exe
              args: []
            """);
        new WorkspaceTrustStore(paths).SetTrusted(setup.Root, true);
        var candidate = Assert.Single(new ExtensionManifestLoader().Discover(
            Path.Combine(setup.Root, ".omnicore", "extensions"), TrustLevel.Project, workspaceTrusted: true));
        new ExtensionConsentStore(paths, setup.Root).Grant(candidate,
            new HashSet<string>(["completion.validate"], StringComparer.Ordinal));
        var launcher = new ExtensionFixtureLauncher(validatorPassed: false);
        var validators = ProjectExtensionValidators.Load(setup.Root, paths, workspaceTrusted: true,
            setup.Session, setup.Run, new ExtensionRpcProcessInvoker(launcher));
        var gate = new SkillCompletionValidator([], validators);
        var catalog = new FakeCatalog();
        var turn = new ExplorerTurn((_, _) => new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null)),
            ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), setup.Root), catalog,
            new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            new ExecutionFingerprint("fixture", "h", "t", "c", "o", "extension-validator"),
            new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null), setup.Store,
            setup.Codecs, setup.Artifacts, new InMemoryAuditSink(), new RedactionPolicy());

        var diagnostics = new List<string>();
        var exit = OmniCliRuntime.Create(setup.Root).RunActLoop(turn, diagnostics.Add, "objective", "instructions",
            setup.Session, setup.Run, setup.Lane, "", null, null, setup.Server, setup.Artifacts,
            new InMemoryAuditSink(), null, interactive: false, "en", TestContext.Current.CancellationToken,
            skillValidators: gate);

        Assert.Equal(1, exit);
        Assert.Contains(launcher.Methods, method => method == "completion/validate");
        Assert.Equal(SandboxStrength.Strong, launcher.LastLaunch!.RequestedStrength);
        var events = setup.Store.ReadFrom(setup.Session, 1).Select(setup.Codecs.Decode).ToArray();
        Assert.Contains(events, item => item is RunValidationRejected rejection
            && rejection.Missing.Any(value => value.Contains("extension:fixture.validator:verify-build", StringComparison.Ordinal)));
        Assert.DoesNotContain(events, item => item is RunCompleted);
    }

    [Fact]
    public void Consent_bound_extension_completion_gate_runs_through_strong_rpc_and_blocks_run_on_false()
    {
        using var setup = new Setup();
        var paths = OmniHost.CreatePlatformPaths(Path.Combine(setup.Root, "user-data-gate"));
        var package = Path.Combine(setup.Root, ".omnicore", "extensions", "fixture.gate");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "runtime.exe"), "fixture runtime");
        File.WriteAllText(Path.Combine(package, ExtensionManifestLoader.ManifestFileName), """
            manifestVersion: 1
            id: fixture.gate
            version: 1
            displayName: Fixture completion gate
            publisher: tests
            omnicoreApi: '1.0'
            provides:
              completionGates: [workspace-ready]
            requests:
              capabilities: [completion.gate]
              permissions:
                process:
                  - executable: runtime.exe
            runtime:
              kind: process
              command: runtime.exe
              args: []
            """);
        new WorkspaceTrustStore(paths).SetTrusted(setup.Root, true);
        var candidate = Assert.Single(new ExtensionManifestLoader().Discover(
            Path.Combine(setup.Root, ".omnicore", "extensions"), TrustLevel.Project, workspaceTrusted: true));
        new ExtensionConsentStore(paths, setup.Root).Grant(candidate,
            new HashSet<string>(["completion.gate"], StringComparer.Ordinal));
        var launcher = new ExtensionFixtureLauncher(validatorPassed: true, completionGatePassed: false);
        var validators = ProjectExtensionValidators.Load(setup.Root, paths, workspaceTrusted: true,
            setup.Session, setup.Run, new ExtensionRpcProcessInvoker(launcher));
        var gates = new SkillCompletionValidator([], validators);
        Assert.True(gates.HasValidators);
        var turn = new ExplorerTurn((_, _) => new ModelResponse([new TextBlock("done")], StopReason.EndTurn,
                new TokenUsage(1, 1, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null)),
            ScriptedToolExecutor.WithWorkspace(new FakeCatalog(),
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), setup.Root), new FakeCatalog(),
            new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            new ExecutionFingerprint("fixture", "h", "t", "c", "o", "completion-gate"),
            new ModelSelection(new ModelIdValue("fixture"), 8192, ToolMode.Direct, null), setup.Store,
            setup.Codecs, setup.Artifacts, new InMemoryAuditSink(), new RedactionPolicy());

        var diagnostics = new List<string>();
        var exit = OmniCliRuntime.Create(setup.Root).RunActLoop(turn, diagnostics.Add, "objective", "instructions",
            setup.Session, setup.Run, setup.Lane, "", null, null, setup.Server, setup.Artifacts,
            new InMemoryAuditSink(), null, interactive: false, "en", TestContext.Current.CancellationToken,
            skillValidators: gates);

        Assert.Equal(1, exit);
        Assert.Contains("completion/gate", launcher.Methods);
        Assert.Equal(SandboxStrength.Strong, launcher.LastLaunch!.RequestedStrength);
        Assert.Equal(SandboxNetworkMode.Deny, launcher.LastLaunch.Network.Mode);
        using (var request = System.Text.Json.JsonDocument.Parse(launcher.LastLaunch.InitialStandardInput!))
        {
            Assert.Equal("completion/gate", request.RootElement.GetProperty("method").GetString());
            Assert.True(request.RootElement.GetProperty("params").GetProperty("runContext")
                .GetProperty("evidence").GetArrayLength() > 0);
        }
        Assert.Contains(setup.Store.ReadFrom(setup.Session, 1).Select(setup.Codecs.Decode), evt =>
            evt is RunValidationRejected rejected
                && rejected.Missing.Any(missing => missing.Contains("completion.gate:fixture.gate:workspace-ready",
                    StringComparison.Ordinal)));
        Assert.DoesNotContain(setup.Store.ReadFrom(setup.Session, 1).Select(setup.Codecs.Decode),
            evt => evt is RunCompleted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cached_extension_validator_never_calls_a_method_after_its_exact_capability_is_revoked(
        bool retainCompletionGate)
    {
        using var setup = new Setup();
        var paths = OmniHost.CreatePlatformPaths(Path.Combine(setup.Root, "user-data-capability-revoke"));
        new WorkspaceTrustStore(paths).SetTrusted(setup.Root, true);
        var package = Path.Combine(setup.Root, ".omnicore", "extensions", "fixture.dual-gate");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "runtime.exe"), "fixture runtime");
        File.WriteAllText(Path.Combine(package, ExtensionManifestLoader.ManifestFileName), """
            manifestVersion: 1
            id: fixture.dual-gate
            version: 1
            displayName: Fixture dual gate
            publisher: tests
            omnicoreApi: '1.0'
            provides:
              validators: [verify-build]
              completionGates: [workspace-ready]
            requests:
              capabilities: [completion.validate, completion.gate]
              permissions:
                process:
                  - executable: runtime.exe
            runtime:
              kind: process
              command: runtime.exe
              args: []
            """);
        var candidate = Assert.Single(new ExtensionManifestLoader().Discover(
            Path.Combine(setup.Root, ".omnicore", "extensions"), TrustLevel.Project, workspaceTrusted: true));
        var consent = new ExtensionConsentStore(paths, setup.Root);
        consent.Grant(candidate, new HashSet<string>(["completion.validate", "completion.gate"], StringComparer.Ordinal));
        var launcher = new ExtensionFixtureLauncher();
        var cached = Assert.Single(ProjectExtensionValidators.Load(setup.Root, paths, workspaceTrusted: true,
            setup.Session, setup.Run, new ExtensionRpcProcessInvoker(launcher)));

        consent.Grant(candidate, new HashSet<string>([retainCompletionGate ? "completion.gate" : "completion.validate"],
            StringComparer.Ordinal));
        var outcomes = cached.Run(TestContext.Current.CancellationToken);

        Assert.Empty(launcher.Methods);
        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, outcome => Assert.False(outcome.Passed));
    }

    [Fact]
    public void Completion_true_and_validation_false_are_both_accepted_evaluations_with_exact_ranges()
    {
        using var setup = new Setup();
        var before = setup.Store.CurrentSequence(setup.Session);
        var complete = setup.Server.CheckRunCompletionAndGate(setup.Session, setup.Run,
            _ => Array.Empty<ExternalCompletionGateResult>(), null);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, complete.Ack.Outcome?.Kind);
        Assert.True(complete.Completed);
        var completedEvents = setup.Store.ReadFrom(setup.Session, before + 1)
            .Where(evt => evt.Causation is CommandCausation cause
                && cause.CommandId.Value.ToString() == complete.Ack.CommandId).ToArray();
        Assert.NotEmpty(completedEvents);
        Assert.Equal(completedEvents.Min(evt => evt.Sequence), complete.Ack.FirstSeq);
        Assert.Equal(completedEvents.Max(evt => evt.Sequence), complete.Ack.LastSeq);
        var runCompleted = Assert.Single(completedEvents, evt => evt.Type.ToString() == "run.completed");
        Assert.Equal(setup.Run, runCompleted.RunId);
        Assert.Equal(setup.Task, runCompleted.TaskId);
        Assert.Equal(setup.Lane, runCompleted.LaneId);

        using var rejectedSetup = new Setup();
        var rejectedEvaluation = rejectedSetup.Server.CheckRunCompletionAndGate(rejectedSetup.Session,
            rejectedSetup.Run, _ => new[] { new ExternalCompletionGateResult("build", false, "build failed") }, null);
        Assert.Equal(RuntimeCommandOutcomeKind.Accepted, rejectedEvaluation.Ack.Outcome?.Kind);
        Assert.False(rejectedEvaluation.Completed);
        var rejectedEvents = rejectedSetup.Store.ReadFrom(rejectedSetup.Session, 1).Where(evt =>
            evt.Causation is CommandCausation cause
            && cause.CommandId.Value.ToString() == rejectedEvaluation.Ack.CommandId).ToArray();
        Assert.NotEmpty(rejectedEvents);
        Assert.Equal(rejectedEvents.Min(evt => evt.Sequence), rejectedEvaluation.Ack.FirstSeq);
        Assert.Equal(rejectedEvents.Max(evt => evt.Sequence), rejectedEvaluation.Ack.LastSeq);
        Assert.Contains(rejectedEvents, evt => evt.Type.ToString() == "run.validation_rejected");
    }

    [Fact]
    public void Missing_session_or_run_is_rejected_without_invoking_gate_callback()
    {
        using var setup = new Setup();
        var calls = 0;
        var wrongSession = setup.Server.CheckRunCompletionAndGate(SessionId.New(), setup.Run, _ =>
        {
            calls++;
            return Array.Empty<ExternalCompletionGateResult>();
        }, null);
        Assert.Null(wrongSession.Completed);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, wrongSession.Ack.Outcome?.Kind);
        Assert.Null(wrongSession.Ack.FirstSeq);
        Assert.Null(wrongSession.Ack.LastSeq);

        var wrongRun = setup.Server.CheckRunCompletionAndGate(setup.Session, RunId.New(), _ =>
        {
            calls++;
            return Array.Empty<ExternalCompletionGateResult>();
        }, null);
        Assert.Null(wrongRun.Completed);
        Assert.Equal(RuntimeCommandOutcomeKind.Rejected, wrongRun.Ack.Outcome?.Kind);
        Assert.Null(wrongRun.Ack.FirstSeq);
        Assert.Null(wrongRun.Ack.LastSeq);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Evaluating_second_run_does_not_reconcile_or_close_the_cancelled_first_run()
    {
        var store = new InMemoryEventStore();
        var codecs = EventCodecs.Create();
        var server = new OmniServer(store, codecs, new InMemoryAuditSink());
        Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "session.input")
            + "," + JsonObj.Field("text", "first objective") + "}"), CancellationToken.None).Status);
        var session = Assert.IsType<SessionId>(server.LastSessionId());
        var firstRun = Assert.IsType<RunId>(server.LastRunId());
        var firstInitial = RunProjection.Replay(session, firstRun, codecs, store.ReadFrom(session, 1));
        var firstTask = Assert.IsType<TaskId>(firstInitial.RootTask);
        var firstLane = Assert.Single(LaneProjection.Replay(codecs, store.ReadFrom(session, 1))
            .ForTask(firstTask)).Id;
        Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "run.cancel")
            + "," + JsonObj.Field("runId", firstRun.Value.ToString()) + "}"), CancellationToken.None).Status);
        Assert.Equal(RunState.Cancelled,
            RunProjection.Replay(session, firstRun, codecs, store.ReadFrom(session, 1)).State);
        var firstTaskStateAfterCancel = TaskGraphProjection.Replay(codecs, store.ReadFrom(session, 1))
            .StateOf(firstTask);
        var firstLaneStateAfterCancel = LaneProjection.Replay(codecs, store.ReadFrom(session, 1))
            .ForTask(firstTask).Single().State;
        var firstRunEvents = store.ReadFrom(session, 1)
            .Where(evt => evt.CorrelationId?.Equals(firstRun) == true).ToArray();
        var firstPlanStateAfterCancel = Assert.Single(PlanProjection.Replay(codecs, firstRunEvents).Items()).State;

        Assert.Equal("ok", server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "session.input")
            + "," + JsonObj.Field("text", "second objective") + "}"), CancellationToken.None).Status);
        var secondRun = Assert.IsType<RunId>(server.LastRunId());
        Assert.False(firstRun.Value.Equals(secondRun.Value));
        var secondInitial = RunProjection.Replay(session, secondRun, codecs, store.ReadFrom(session, 1));
        var secondTask = Assert.IsType<TaskId>(secondInitial.RootTask);
        Assert.NotEqual(firstTask.Value, secondTask.Value);
        var before = store.CurrentSequence(session);
        var parentScope = new ExecutionScopeState(firstRun, firstTask, firstLane);
        bool? evaluationCompleted = null;
        using (ExecutionScope.Begin(parentScope))
        {
            var evaluation = server.CheckRunCompletionAndGate(session, secondRun,
                _ => Array.Empty<ExternalCompletionGateResult>(), null);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, evaluation.Ack.Outcome?.Kind);
            evaluationCompleted = evaluation.Completed;
            Assert.Equal(parentScope, ExecutionScope.Current);
            var after = store.ReadFrom(session, before + 1);
            Assert.All(after.Where(evt => evt.Causation is CommandCausation cause
                && cause.CommandId.Value.ToString() == evaluation.Ack.CommandId), evt =>
            {
                Assert.NotEqual(firstTask, evt.TaskId);
                Assert.NotEqual(firstLane, evt.LaneId);
            });
        }

        var tasksAfter = TaskGraphProjection.Replay(codecs, store.ReadFrom(session, 1));
        var lanesAfter = LaneProjection.Replay(codecs, store.ReadFrom(session, 1));
        Assert.Equal(firstTaskStateAfterCancel, tasksAfter.StateOf(firstTask));
        Assert.Equal(firstLaneStateAfterCancel, lanesAfter.ForTask(firstTask).Single().State);
        var firstPlanAfter = PlanProjection.Replay(codecs, store.ReadFrom(session, 1)
            .Where(evt => evt.CorrelationId?.Equals(firstRun) == true).ToArray());
        Assert.Equal(firstPlanStateAfterCancel, Assert.Single(firstPlanAfter.Items()).State);
        Assert.Equal(TaskState.Completed, tasksAfter.StateOf(secondTask));
        Assert.True(evaluationCompleted);
        Assert.Null(ExecutionScope.Current);
    }

    [Fact]
    public void Ambient_command_and_execution_scopes_are_preserved_and_not_inherited_by_events()
    {
        using var setup = new Setup();
        var command = new CommandCausation(new CommandId(Guid.NewGuid()));
        var parentExecution = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New(), TurnId.New());
        using (CausationScope.Begin(command))
        using (ExecutionScope.Begin(parentExecution))
        {
            var result = setup.Server.CheckRunCompletionAndGate(setup.Session, setup.Run,
                _ => Array.Empty<ExternalCompletionGateResult>(), null);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, result.Ack.Outcome?.Kind);
            Assert.Equal(command.CommandId.Value.ToString(), result.Ack.CommandId);
            Assert.Equal(command, CausationScope.Current);
            Assert.Equal(parentExecution, ExecutionScope.Current);

            var caused = setup.Store.ReadFrom(setup.Session, 1).Where(evt =>
                evt.Causation is CommandCausation cause && cause.CommandId == command.CommandId).ToArray();
            Assert.NotEmpty(caused);
            Assert.All(caused, evt =>
            {
                Assert.Equal(setup.Run, evt.RunId);
                Assert.NotEqual(parentExecution.RunId, evt.RunId);
            });
            var validationStarted = Assert.Single(caused, evt => evt.Type.ToString() == "run.validation_started");
            Assert.Equal(setup.Task, validationStarted.TaskId);
            Assert.Equal(setup.Lane, validationStarted.LaneId);
        }

        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
    }

    [Fact]
    public void Gate_exception_propagates_after_durable_prefix_and_restores_parent_scopes()
    {
        using var setup = new Setup();
        var before = setup.Store.CurrentSequence(setup.Session);
        var cause = new EventCausation(new EventId(Guid.NewGuid()));
        var parentExecution = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New());
        using (CausationScope.Begin(cause))
        using (ExecutionScope.Begin(parentExecution))
        {
            var original = new InvalidOperationException("controlled gate failure");
            var result = setup.Server.CheckRunCompletionAndGate(
                setup.Session, setup.Run, _ => throw original, null);
            Assert.Same(original, result.Failure);
            Assert.Null(result.Completed);
            Assert.Equal("error", result.Ack.Status);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, result.Ack.Outcome?.Kind);
            Assert.Equal(cause, CausationScope.Current);
            Assert.Equal(parentExecution, ExecutionScope.Current);
            var prefix = setup.Store.ReadFrom(setup.Session, before + 1);
            Assert.Contains(prefix, evt => evt.Type.ToString() == "run.validation_started"
                && evt.Causation is CommandCausation);
            Assert.All(prefix.Where(evt => evt.Causation is CommandCausation), evt =>
            {
                Assert.Equal(setup.Run, evt.RunId);
                Assert.NotEqual(parentExecution.RunId, evt.RunId);
            });
            Assert.DoesNotContain(prefix, evt => evt.Type.ToString() == "run.completed");
            var own = prefix.Where(evt => evt.Causation is CommandCausation command
                && command.CommandId.Value.ToString() == result.Ack.CommandId).ToArray();
            Assert.NotEmpty(own);
            Assert.Equal(own.Min(evt => evt.Sequence), result.Ack.FirstSeq);
            Assert.Equal(own.Max(evt => evt.Sequence), result.Ack.LastSeq);
        }

        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
    }

    [Fact]
    public void Failed_first_append_propagates_restores_scopes_and_retry_is_a_fresh_accepted_evaluation()
    {
        using var setup = new Setup();
        var before = setup.Store.CurrentSequence(setup.Session);
        var parentCause = new EventCausation(new EventId(Guid.NewGuid()));
        var parentExecution = new ExecutionScopeState(RunId.New(), TaskId.New(), LaneId.New());
        setup.Store.FailNextAppend = true;
        using (CausationScope.Begin(parentCause))
        using (ExecutionScope.Begin(parentExecution))
        {
            var failed = setup.Server.CheckRunCompletionAndGate(setup.Session, setup.Run,
                _ => Array.Empty<ExternalCompletionGateResult>(), null);
            Assert.IsType<IOException>(failed.Failure);
            Assert.Null(failed.Completed);
            Assert.Equal("error", failed.Ack.Status);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, failed.Ack.Outcome?.Kind);
            var durablePrefix = setup.Store.ReadFrom(setup.Session, before + 1);
            Assert.NotEmpty(durablePrefix);
            Assert.DoesNotContain(durablePrefix, evt => evt.Type.ToString() == "run.validation_started");
            Assert.DoesNotContain(durablePrefix, evt => evt.Type.ToString() == "run.completed");
            Assert.Equal(durablePrefix.Min(evt => evt.Sequence), failed.Ack.FirstSeq);
            Assert.Equal(durablePrefix.Max(evt => evt.Sequence), failed.Ack.LastSeq);
            Assert.Equal(parentCause, CausationScope.Current);
            Assert.Equal(parentExecution, ExecutionScope.Current);

            var retry = setup.Server.CheckRunCompletionAndGate(setup.Session, setup.Run,
                _ => Array.Empty<ExternalCompletionGateResult>(), null);
            Assert.Equal(RuntimeCommandOutcomeKind.Accepted, retry.Ack.Outcome?.Kind);
            Assert.NotEqual(failed.Ack.CommandId, retry.Ack.CommandId);
            Assert.True(retry.Completed);
            Assert.Equal(parentCause, CausationScope.Current);
            Assert.Equal(parentExecution, ExecutionScope.Current);
            var caused = setup.Store.ReadFrom(setup.Session, before + 1).Where(evt =>
                evt.Causation is CommandCausation cause
                && cause.CommandId.Value.ToString() == retry.Ack.CommandId).ToArray();
            Assert.NotEmpty(caused);
            Assert.Equal(caused.Min(evt => evt.Sequence), retry.Ack.FirstSeq);
            Assert.Equal(caused.Max(evt => evt.Sequence), retry.Ack.LastSeq);
        }

        Assert.Null(CausationScope.Current);
        Assert.Null(ExecutionScope.Current);
    }

    [Fact]
    public void Act_loop_acceptance_response_reenters_completion_boundary()
    {
        using var setup = new Setup();
        var catalog = new FakeCatalog();
        var executor = ScriptedToolExecutor.WithWorkspace(catalog,
            new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), setup.Root);
        var turn = new ExplorerTurn((_, _) => new ModelResponse(new ContentBlock[] { new TextBlock("done") },
                StopReason.EndTurn, new TokenUsage(1, 1, 0, 0, 0), null,
                new ProviderMetadata("scripted", "test", null)), executor, catalog,
            new ContextMaterializer(new FakeTokenCounter(), Array.Empty<IContextContributor>()),
            new ExecutionFingerprint("scripted", "h", "t", "c", "o", "test"),
            new ModelSelection(new ModelIdValue("scripted"), 8192, ToolMode.Direct, null),
            setup.Store, setup.Codecs, setup.Artifacts, new InMemoryAuditSink(), new RedactionPolicy());
        var runtime = OmniCliRuntime.Create(setup.Root);
        var exit = runtime.RunActLoop(turn, _ => { }, "completion objective", "instructions",
            setup.Session, setup.Run, setup.Lane, "", new WorkspaceGatesYaml { Acceptance = true }, null,
            setup.Server, setup.Artifacts, new InMemoryAuditSink(), null, true, "en",
            TestContext.Current.CancellationToken, acceptanceResponder: _ => "accept");

        Assert.Equal(0, exit);
        var events = setup.Store.ReadFrom(setup.Session, 1);
        Assert.Contains(events, evt => evt.Type.ToString() == "run.completed");
        var validations = events.Where(evt => evt.Type.ToString() == "run.validation_started").ToArray();
        Assert.Equal(2, validations.Length);
        var causes = validations.Select(evt => Assert.IsType<CommandCausation>(evt.Causation).CommandId.Value)
            .ToArray();
        Assert.NotEqual(causes[0], causes[1]);
    }

    private sealed class Setup : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "omnicore-completion-command-"
            + Guid.NewGuid().ToString("N"));
        public TestEventStore Store { get; } = new();
        public EventCodecs Codecs { get; } = EventCodecs.Create();
        public FileArtifactStore Artifacts { get; }
        public OmniServer Server { get; }
        public SessionId Session { get; }
        public RunId Run { get; }
        public LaneId Lane { get; }
        public TaskId Task { get; }

        public Setup()
        {
            Directory.CreateDirectory(Root);
            Artifacts = new FileArtifactStore(Path.Combine(Root, "artifacts"));
            Server = new OmniServer(Store, Codecs, new InMemoryAuditSink(), Artifacts);
            var ack = Server.Send(WireEnvelope.Command(Ids.NewV7(), "{" + JsonObj.Field("cmd", "act") + ","
                + JsonObj.Field("objective", "completion test") + ","
                + JsonObj.Field("workspace", Root) + "}"), CancellationToken.None);
            Assert.Equal("ok", ack.Status);
            Session = Assert.IsType<SessionId>(Server.LastSessionId());
            Run = Assert.IsType<RunId>(Server.LastRunId());
            Lane = Assert.IsType<LaneId>(Server.LastLaneId());
            Task = Assert.Single(Store.ReadFrom(Session, 1).Select(Codecs.Decode).OfType<RunCreated>()).RootTask;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class TestEventStore : IEventStore
    {
        private readonly InMemoryEventStore _inner = new();
        public bool FailNextAppend { get; set; }

        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken)
        {
            if (FailNextAppend)
            {
                FailNextAppend = false;
                throw new IOException("controlled failure before completion event persistence");
            }
            _inner.Append(sessionId, evt, durability, cancellationToken);
        }

        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken cancellationToken) =>
            _inner.AppendBatch(sessionId, events, durability, cancellationToken);

        public long CurrentSequence(SessionId sessionId) => _inner.CurrentSequence(sessionId);

        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) =>
            _inner.ReadFrom(sessionId, fromSequenceInclusive);
    }

    private sealed class ExtensionFixtureLauncher(bool validatorPassed = true,
        bool completionGatePassed = true) : ISandboxProcessLauncher
    {
        public List<string> Methods { get; } = [];
        public int Starts { get; private set; }
        public SandboxLaunchSpec? LastLaunch { get; private set; }

        public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch,
            CancellationToken cancellationToken)
        {
            Starts++;
            LastLaunch = launch;
            using var request = System.Text.Json.JsonDocument.Parse(launch.InitialStandardInput ?? "{}");
            var method = request.RootElement.GetProperty("method").GetString()!;
            Methods.Add(method);
            var response = method switch
            {
                "context/contribute" => "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"items\":[{\"id\":\"runtime-note\",\"kind\":\"knowledge\",\"content\":\"offline extension contribution\"}]}}",
                "completion/validate" => "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"results\":[{\"id\":\"verify-build\",\"passed\":"
                    + (validatorPassed ? "true" : "false") + ",\"summary\":\"offline validator passed\"}]}}",
                "completion/gate" => "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"results\":[{\"id\":\"workspace-ready\",\"passed\":"
                    + (completionGatePassed ? "true" : "false") + ",\"summary\":\"offline gate result\"}]}}",
                "tools/call" => "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"ok\":true}}",
                _ => throw new InvalidOperationException("Unexpected extension method " + method),
            };
            return ValueTask.FromResult<ISandboxProcessControl>(new ExtensionFixtureControl(
                response));
        }
    }

    private sealed class ExtensionFixtureControl(string response) : ISandboxProcessControl
    {
        public ValueTask<SandboxProcessOutput> WaitForExitAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SandboxProcessOutput(0,
                [new SandboxOutputChunk(SandboxOutputStream.StandardOutput, response)], TimedOut: false));
        public ValueTask TerminateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
