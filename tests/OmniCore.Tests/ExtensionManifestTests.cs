namespace OmniCore.Tests;

using OmniCore.Client;
using OmniCore.Context;
using OmniCore.Extensions;
using OmniCore.Abstractions;
using OmniCore.Host;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCore.Sandbox;
using OmniCore.Tools;
using TrustLevel = OmniCore.Domain.TrustLevel;
using PermissionDecision = OmniCore.Domain.PermissionDecision;
using ToolCallId = OmniCore.Domain.ToolCallId;
using SessionId = OmniCore.Domain.SessionId;
using RunId = OmniCore.Domain.RunId;
using ExecutionFingerprint = OmniCore.Domain.ExecutionFingerprint;
using ContextItemKind = OmniCore.Domain.ContextItemKind;
using ContributionCategory = OmniCore.Domain.ContributionCategory;
using ScopeLevel = OmniCore.Domain.ScopeLevel;

public sealed class ExtensionManifestTests
{
    [Fact]
    public void Extension_tool_tags_can_match_an_equivalent_control_without_lowering_its_risk_or_effect()
    {
        var manifest = new ExtensionManifestLoader().Load(Manifest.Replace(
            "schema: schemas/compile.json", "schema: schemas/compile.json\n      tags: [process, execution]", StringComparison.Ordinal));
        var declaration = Assert.Single(manifest.Provides!.Tools!);
        Assert.Equal(["process", "execution"], declaration.Tags);
        var extension = new DiscoveredExtension("C:\\fixture", manifest, ExtensionDiscoveryStatus.Ready,
            null, new string('b', 64), new string('c', 64), TrustLevel.Project);
        var tool = new ExtensionTool(extension, declaration, "{\"type\":\"object\"}",
            new CountingRpcClient(), "C:\\workspace");

        Assert.Equal(["process", "execution"], tool.Descriptor.Tags);
        Assert.Equal(ToolRisk.High, tool.Descriptor.Risk);
        Assert.Equal(OmniCore.Domain.EffectClass.NonIdempotent, tool.Descriptor.EffectClass);
        Assert.False(tool.Descriptor.ReadOnly);
        Assert.Throws<InvalidDataException>(() => new ExtensionManifestLoader().Load(Manifest.Replace(
            "schema: schemas/compile.json", "schema: schemas/compile.json\n      tags: [not valid]", StringComparison.Ordinal)));
    }

    [Fact]
    public void Optional_ui_resources_are_literal_bounded_and_do_not_change_execution_identity()
    {
        var loader = new ExtensionManifestLoader();
        var original = loader.Load(Manifest);
        Assert.Null(original.UiResources);
        var translated = loader.Load(Manifest + "\nuiResources:\n  fr:\n    extension.displayName: Outils de compilation\n    tool.compile.description: Compile les fichiers du projet.\n");
        Assert.Equal(original.Id, translated.Id);
        Assert.Equal(original.Runtime!.Command, translated.Runtime!.Command);
        Assert.Equal(original.Requests!.Capabilities, translated.Requests!.Capabilities);
        Assert.Equal("Outils de compilation", translated.UiResources!["fr"]["extension.displayName"]);
        Assert.Throws<InvalidDataException>(() => loader.Load(Manifest + "\nuiResources:\n  de:\n    extension.displayName: Text\n"));
        Assert.Throws<InvalidDataException>(() => loader.Load(Manifest + "\nuiResources:\n  fr:\n    command.undeclared.description: Text\n"));
        Assert.Throws<InvalidDataException>(() => loader.Load(Manifest + "\nuiResources:\n  fr:\n    extension.description: " + new string('a', 2049) + "\n"));
        Assert.Throws<InvalidDataException>(() => loader.Load(Manifest + "\nuiResources:\n  fr:\n    extension.description: { prompt: ignored }\n"));
    }

    [Fact]
    public void External_ui_copy_roundtrips_without_translating_identity_or_unknown_fields()
    {
        const string json = """
            {"workspaceTrusted":false,"session":null,"extensions":[{
              "id":"acme.progress","displayName":"Original","version":"1","status":"ConsentRequired","trust":"Project",
              "uiResources":{"fr":{"extension.displayName":"Outils","extension.description":"Description française","secret":"NEVER SHOW"},
                 "xx":{"extension.displayName":"Unsupported"}}}]}
            """;
        var extension = Assert.Single(SessionPreferencesPresentation.Decode("extensions", json).Extensions);
        Assert.Equal("acme.progress", extension.Id);
        Assert.Equal("Original", extension.DisplayName);
        Assert.Equal("Outils", extension.TextFor("fr-FR")!.DisplayName);
        Assert.Equal("Description française", extension.TextFor("fr")!.Description);
        Assert.Null(extension.TextFor("it"));
        Assert.DoesNotContain("NEVER SHOW", extension.TextFor("fr")!.ToString());
        var tools = ToolsInspectionJson.Decode("""
            {"mode":"Act","tools":[{"visibleName":"ext.acme.compile","toolId":"ext.acme.compile","source":"Extension:Project:Project:acme@1",
              "effectClass":"NonIdempotent","decision":"permission policy","visible":true,"description":"Original tool",
              "uiDescriptions":{"fr":"Compile le projet"}}]}
            """)!;
        var tool = Assert.Single(tools.Tools);
        Assert.Equal("Compile le projet", ToolInspectionPresentation.Description(tool, new("fr")));
        Assert.Contains("Original tool", ToolInspectionPresentation.Description(tool, new("it")));
        Assert.Contains(new Localization("it").Resolve("client.tools.description.original"), ToolInspectionPresentation.Description(tool, new("it")));
    }

    [Fact]
    public void Discovery_is_pure_and_requires_project_trust_and_capability_consent()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-extension-" + Guid.NewGuid().ToString("N"));
        var extensionPath = Path.Combine(root, "extensions", "acme.progress");
        Directory.CreateDirectory(extensionPath);
        File.WriteAllText(Path.Combine(extensionPath, ExtensionManifestLoader.ManifestFileName), Manifest);
        try
        {
            var loader = new ExtensionManifestLoader();
            var project = Assert.Single(loader.Discover(Path.Combine(root, "extensions"), TrustLevel.Project,
                workspaceTrusted: false));
            Assert.Equal(ExtensionDiscoveryStatus.WorkspaceTrustRequired, project.Status);
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(extensionPath),
                path => Path.GetFileName(path) != ExtensionManifestLoader.ManifestFileName);

            var consent = Assert.Single(loader.Discover(Path.Combine(root, "extensions"), TrustLevel.Project,
                workspaceTrusted: true));
            Assert.Equal(ExtensionDiscoveryStatus.ConsentRequired, consent.Status);

            var ready = Assert.Single(loader.Discover(Path.Combine(root, "extensions"), TrustLevel.Project,
                workspaceTrusted: true, new HashSet<string>(["tool.register"])));
            Assert.Equal(ExtensionDiscoveryStatus.Ready, ready.Status);
            Assert.NotNull(ready.Manifest);
            Assert.Equal("acme.progress", ready.Manifest!.Id);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Manifest_validation_rejects_unknown_fields_and_incompatible_api()
    {
        var loader = new ExtensionManifestLoader();
        Assert.Throws<InvalidDataException>(() => loader.Load(Manifest.Replace("displayName:", "unknown: x\ndisplayName:", StringComparison.Ordinal)));

        var root = Path.Combine(Path.GetTempPath(), "OmniCore-extension-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ExtensionManifestLoader.ManifestFileName), Manifest.Replace(">=1.0 <2.0", ">=2.0 <3.0", StringComparison.Ordinal));
        try
        {
            var incompatible = Assert.Single(loader.Discover(root, TrustLevel.Trusted, workspaceTrusted: true,
                new HashSet<string>(["tool.register"])));
            Assert.Equal(ExtensionDiscoveryStatus.IncompatibleApi, incompatible.Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Extension_skill_declarations_reject_duplicate_and_escaping_package_paths()
    {
        var loader = new ExtensionManifestLoader();
        var requested = Manifest.Replace("capabilities: [tool.register]",
            "capabilities: [tool.register, skill.register]", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => loader.Load(requested.Replace("  tools:",
            "  skills: [../outside]\n  tools:", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => loader.Load(requested.Replace("  tools:",
            "  skills: [skills/one, skills/one]\n  tools:", StringComparison.Ordinal)));
    }

    [Fact]
    public void Manifest_ast_mapping_rejects_wrong_scalar_and_nested_types()
    {
        var loader = new ExtensionManifestLoader();
        Assert.Throws<InvalidDataException>(() => loader.Load(Manifest.Replace("manifestVersion: 1", "manifestVersion: nope", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => loader.Load(Manifest.Replace("schema: schemas/compile.json", "schema: { nested: value }", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => loader.Load(Manifest.Replace("read: ['**/*.p']", "read: { nested: value }", StringComparison.Ordinal)));
    }

    [Fact]
    public void Extension_commands_must_bind_a_declared_tool_and_manifest_loads_binding()
    {
        var commandManifest = Manifest.Replace("requests:", "  commands:\n    - name: prowin-compile\n      kind: Extension\n      tool: compile\nrequests:", StringComparison.Ordinal)
            .Replace("capabilities: [tool.register]", "capabilities: [tool.register, command.register]", StringComparison.Ordinal);
        var command = Assert.Single(new ExtensionManifestLoader().Load(commandManifest).Provides!.Commands!);
        Assert.Equal("compile", command.Tool);
        Assert.Throws<InvalidDataException>(() => new ExtensionManifestLoader().Load(commandManifest.Replace("tool: compile", "tool: missing", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Extension_command_returns_a_forced_tool_turn_intent_without_starting_a_process()
    {
        var manifest = new ExtensionManifest { Id = "acme.progress", Version = "1", DisplayName = "Progress" };
        var extension = new DiscoveredExtension("unused", manifest, ExtensionDiscoveryStatus.Ready, null,
            new string('a', 64), new string('b', 64), TrustLevel.Project);
        var handler = new ProjectExtensionCommandHandler(extension,
            new ExtensionCommandManifest { Name = "prowin-compile", Kind = "Extension", Tool = "compile" });
        var context = new CommandContext(OmniCore.Domain.SessionId.New(), OmniCore.Domain.RunId.New(), OmniCore.Domain.RunMode.Act, Path.GetTempPath());
        var outcome = Assert.IsType<PromptCommandRequested>(await handler.HandleAsync(
            ["{\"file\":\"src/program.p\"}"], context, CancellationToken.None));
        Assert.Equal("ext.acme.progress.compile", outcome.ForcedToolName);
        Assert.Contains("{\"file\":\"src/program.p\"}", outcome.Text);
    }

    [Fact]
    public void Host_command_catalog_only_registers_trusted_and_consented_extension_commands()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-extension-command-catalog-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var package = Path.Combine(workspace, ".omnicore", "extensions", "acme.progress");
        Directory.CreateDirectory(package);
        var manifest = Manifest.Replace("requests:", "  commands:\n    - name: prowin-compile\n      kind: Extension\n      tool: compile\nrequests:", StringComparison.Ordinal)
            .Replace("capabilities: [tool.register]", "capabilities: [tool.register, command.register]", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(package, ExtensionManifestLoader.ManifestFileName), manifest);
        var paths = OmniHost.CreatePlatformPaths(Path.Combine(root, "data"));
        var server = new OmniServer(new OmniCore.Infrastructure.InMemoryEventStore(),
            OmniCore.Infrastructure.EventCodecs.Create(), new OmniCore.Infrastructure.InMemoryAuditSink());
        server.ConfigurePlatformPaths(() => paths);
        server.ConfigureWorkspaceRoot(workspace);
        try
        {
            Assert.DoesNotContain("acme.progress:prowin-compile", server.Query("commands", CancellationToken.None)!.Json);
            new WorkspaceTrustStore(paths).SetTrusted(workspace, true);
            Assert.DoesNotContain("acme.progress:prowin-compile", server.Query("commands", CancellationToken.None)!.Json);
            var loader = new ExtensionManifestLoader();
            var extension = Assert.Single(loader.Discover(Path.Combine(workspace, ".omnicore", "extensions"),
                TrustLevel.Project, workspaceTrusted: true));
            new ExtensionConsentStore(paths, workspace).Grant(extension,
                new HashSet<string>(["tool.register", "command.register"], StringComparer.Ordinal));
            var catalog = server.Query("commands", CancellationToken.None)!.Json;
            Assert.Contains("acme.progress:prowin-compile", catalog);
            Assert.Contains("ext.acme.progress.compile", catalog);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Extension_boundary_is_a_ceiling_for_path_process_and_network_claims()
    {
        var permissions = new ExtensionPermissionManifest
        {
            Process = [new ExtensionProcessPermissionManifest { Executable = "prowin.exe" }],
            Network = ["api.example.com"],
            Paths = new ExtensionPathPermissionManifest { Read = ["**/*.p"], Write = ["output/*.log"] },
        };
        var boundary = new ExtensionBoundary(permissions);
        Assert.True(boundary.AllowsProcess("prowin.exe"));
        Assert.False(boundary.AllowsProcess("powershell.exe"));
        Assert.True(boundary.AllowsNetwork("api.example.com"));
        Assert.False(boundary.AllowsNetwork("attacker.example.com"));
        Assert.True(boundary.AllowsPath("src/main.p", write: false));
        Assert.False(boundary.AllowsPath("src/main.p", write: true));
        Assert.True(boundary.AllowsPath("output/build.log", write: true));
        Assert.False(boundary.AllowsPath("private/credentials.txt", write: false));
        Assert.False(boundary.AllowsPath("../outside/credentials.txt", write: false));
        Assert.False(boundary.AllowsPath("C:/outside/credentials.txt", write: true));
    }

    [Fact]
    public async Task Host_extensions_query_reports_project_trust_and_consent_state_without_launching()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-extension-host-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "user-data");
        var workspace = Path.Combine(root, "workspace");
        var extensionPath = Path.Combine(workspace, ".omnicore", "extensions", "acme.progress");
        Directory.CreateDirectory(extensionPath);
        File.WriteAllText(Path.Combine(extensionPath, ExtensionManifestLoader.ManifestFileName), Manifest);
        var paths = OmniHost.CreatePlatformPaths(data);
        var userExtensionPath = Path.Combine(data, "extensions", "fixture.user-query");
        Directory.CreateDirectory(userExtensionPath);
        File.WriteAllText(Path.Combine(userExtensionPath, ExtensionManifestLoader.ManifestFileName),
            Manifest.Replace("acme.progress", "fixture.user-query", StringComparison.Ordinal));
        var server = OmniHost.CreateInMemoryServer();
        server.ConfigurePlatformPaths(() => paths);
        server.ConfigureWorkspaceRoot(workspace);
        try
        {
            var untrusted = server.Query("extensions", CancellationToken.None)!.Json;
            using (var snapshot = System.Text.Json.JsonDocument.Parse(untrusted))
            {
                var extensions = snapshot.RootElement.GetProperty("extensions");
                Assert.Equal("WorkspaceTrustRequired", Assert.Single(extensions.EnumerateArray(), item =>
                    item.GetProperty("scope").GetString() == "Project").GetProperty("status").GetString());
                Assert.Equal("ConsentRequired", Assert.Single(extensions.EnumerateArray(), item =>
                    item.GetProperty("scope").GetString() == "User").GetProperty("status").GetString());
            }

            new WorkspaceTrustStore(paths).SetTrusted(workspace, true);
            var trustedWorkspace = server.Query("extensions", CancellationToken.None)!.Json;
            using var trustedSnapshot = System.Text.Json.JsonDocument.Parse(trustedWorkspace);
            Assert.All(trustedSnapshot.RootElement.GetProperty("extensions").EnumerateArray(), item =>
                Assert.Equal("ConsentRequired", item.GetProperty("status").GetString()));

            var clientResult = await new ClientActionHandler().ExecuteAsync(
                new ClientActionInvocation(ClientActionIds.ExtensionsInspect, [], "slash"),
                new ClientContext(server, new Localization("es")), CancellationToken.None);
            Assert.Contains("ConsentRequired", clientResult.Text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Exact_manifest_consent_loads_namespaced_tools_and_is_invalidated_by_manifest_change()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-extension-consent-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        var extensionPath = Path.Combine(workspace, ".omnicore", "extensions", "acme.progress");
        Directory.CreateDirectory(Path.Combine(extensionPath, "schemas"));
        var manifestPath = Path.Combine(extensionPath, ExtensionManifestLoader.ManifestFileName);
        File.WriteAllText(manifestPath, Manifest);
        File.WriteAllText(Path.Combine(extensionPath, "schemas", "compile.json"), "{\"type\":\"object\"}");
        var paths = OmniHost.CreatePlatformPaths(data);
        new WorkspaceTrustStore(paths).SetTrusted(workspace, true);
        var store = new ExtensionConsentStore(paths, workspace);
        try
        {
            var loader = new ExtensionManifestLoader();
            var candidate = Assert.Single(loader.Discover(Path.Combine(workspace, ".omnicore", "extensions"),
                TrustLevel.Project, workspaceTrusted: true));
            Assert.Equal(ExtensionDiscoveryStatus.ConsentRequired, candidate.Status);
            store.Grant(candidate, new HashSet<string>(candidate.Manifest!.Requests!.Capabilities!, StringComparer.Ordinal));
            var grants = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                [candidate.Manifest.Id!] = store.ConsentedCapabilities(candidate),
            };
            var ready = Assert.Single(loader.DiscoverWithConsents(Path.Combine(workspace, ".omnicore", "extensions"),
                TrustLevel.Project, workspaceTrusted: true, grants));
            Assert.Equal(ExtensionDiscoveryStatus.Ready, ready.Status);

            var tools = ProjectExtensionTools.Load(workspace, paths, workspaceTrusted: true,
                invoker: new NeverInvokedRpcClient());
            var extensionTool = Assert.Single(tools);
            Assert.Equal("ext.acme.progress.compile", extensionTool.Descriptor.Id.ToString());
            Assert.Equal(TrustLevel.Project, extensionTool.Descriptor.Source.Trust);

            File.WriteAllText(Path.Combine(extensionPath, "schemas", "compile.json"), "{\"type\":\"string\"}");
            var packageChanged = Assert.Single(loader.Discover(Path.Combine(workspace, ".omnicore", "extensions"),
                TrustLevel.Project, workspaceTrusted: true));
            Assert.Equal(ExtensionDiscoveryStatus.ConsentRequired, packageChanged.Status);
            Assert.Empty(ProjectExtensionTools.Load(workspace, paths, workspaceTrusted: true,
                invoker: new NeverInvokedRpcClient()));

            File.WriteAllText(Path.Combine(extensionPath, "schemas", "compile.json"), "{\"type\":\"object\"}");
            candidate = Assert.Single(loader.Discover(Path.Combine(workspace, ".omnicore", "extensions"),
                TrustLevel.Project, workspaceTrusted: true));
            store.Grant(candidate, new HashSet<string>(candidate.Manifest!.Requests!.Capabilities!, StringComparer.Ordinal));
            File.WriteAllText(manifestPath, Manifest.Replace("1.4.0", "1.4.1", StringComparison.Ordinal));
            var changed = Assert.Single(loader.Discover(Path.Combine(workspace, ".omnicore", "extensions"),
                TrustLevel.Project, workspaceTrusted: true));
            Assert.Equal(ExtensionDiscoveryStatus.ConsentRequired, changed.Status);
            Assert.Empty(ProjectExtensionTools.Load(workspace, paths, workspaceTrusted: true,
                invoker: new NeverInvokedRpcClient()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Consented_extension_context_contributor_runs_through_strong_stdio_with_metadata_only_input()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-extension-context-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var paths = OmniHost.CreatePlatformPaths(Path.Combine(root, "data"));
        var package = Path.Combine(workspace, ".omnicore", "extensions", "fixture.context");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "runtime.exe"), "fixture runtime");
        File.WriteAllText(Path.Combine(package, ExtensionManifestLoader.ManifestFileName), """
            manifestVersion: 1
            id: fixture.context
            version: 1.0.0
            displayName: Fixture context
            publisher: tests
            omnicoreApi: '1.0'
            provides:
              contextContributors: [knowledge.note]
              validators: [verify-build]
            requests:
              capabilities: [context.contribute, completion.validate]
              permissions:
                process:
                  - executable: runtime.exe
            runtime:
              kind: process
              command: runtime.exe
              args: []
            """);
        try
        {
            var loader = new ExtensionManifestLoader();
            var candidate = Assert.Single(loader.Discover(Path.Combine(workspace, ".omnicore", "extensions"),
                TrustLevel.Project, workspaceTrusted: true));
            Assert.Equal(ExtensionDiscoveryStatus.ConsentRequired, candidate.Status);
            var invoker = new ExtensionRpcProcessInvoker(new ProtocolFixtureLauncher("contributor"));
            Assert.Empty(ProjectExtensionContributors.Load(workspace, paths, workspaceTrusted: true, invoker));
            new ExtensionConsentStore(paths, workspace).Grant(candidate,
                new HashSet<string>(["context.contribute", "completion.validate"], StringComparer.Ordinal));
            var contributor = Assert.Single(ProjectExtensionContributors.Load(workspace, paths,
                workspaceTrusted: true, invoker));
            var fingerprint = new ExecutionFingerprint("fixture", "h", "t", "c", "o", "build");
            var request = new MaterializeRequest(SessionId.New(), RunId.New(), null, null, null, 0, fingerprint);
            var items = await contributor.GetContextAsync(request, CancellationToken.None);
            var item = Assert.Single(items);
            Assert.Equal(ContextItemKind.Knowledge, item.Kind);
            Assert.Equal("offline contributed knowledge", item.Content);
            Assert.Equal(ContributionCategory.Knowledge, item.Provenance.Category);
            Assert.StartsWith("Extension:fixture.context@1.0.0:", item.Provenance.ComponentSource,
                StringComparison.Ordinal);
            Assert.Equal(OmniCore.Domain.ScopeLevel.Project, item.Provenance.Scope);
            var validatorLauncher = new ProtocolFixtureLauncher("validator");
            var validators = ProjectExtensionValidators.Load(workspace, paths, workspaceTrusted: true,
                request.SessionId, request.RunId, new ExtensionRpcProcessInvoker(validatorLauncher));
            var result = Assert.Single(Assert.Single(validators).Run(TestContext.Current.CancellationToken));
            Assert.True(result.Passed);
            Assert.Equal("extension:fixture.context:verify-build", result.Key);
            var validatorLaunch = validatorLauncher.LastLaunch!;
            Assert.Equal(SandboxStrength.Strong, validatorLaunch.RequestedStrength);
            Assert.Equal(SandboxNetworkMode.Deny, validatorLaunch.Network.Mode);
            Assert.DoesNotContain(validatorLaunch.AllowedPaths, path => !path.Path.StartsWith(package,
                StringComparison.OrdinalIgnoreCase));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task User_extension_tools_contributors_and_validators_are_hash_consented_and_run_strong()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-extension-user-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var paths = OmniHost.CreatePlatformPaths(Path.Combine(root, "data"));
        var package = Path.Combine(paths.DataDirectory, "extensions", "fixture.user");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "runtime.exe"), "fixture runtime");
        File.WriteAllText(Path.Combine(package, "schema.json"), "{\"type\":\"object\"}");
        File.WriteAllText(Path.Combine(package, ExtensionManifestLoader.ManifestFileName), """
            manifestVersion: 1
            id: fixture.user
            version: 1.0.0
            displayName: User fixture
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
        try
        {
            var loader = new ExtensionManifestLoader();
            var candidate = Assert.Single(loader.Discover(Path.Combine(paths.DataDirectory, "extensions"),
                TrustLevel.Trusted, workspaceTrusted: false));
            Assert.Equal(ExtensionDiscoveryStatus.ConsentRequired, candidate.Status);
            var capabilities = new HashSet<string>(["tool.register", "context.contribute", "completion.validate"],
                StringComparer.Ordinal);
            new ExtensionConsentStore(paths, ScopeLevel.User).Grant(candidate, capabilities);

            var rpcToolLauncher = new ProtocolFixtureLauncher("tool");
            var tools = ProjectExtensionTools.Load(workspace, paths, workspaceTrusted: false,
                new ExtensionRpcProcessInvoker(rpcToolLauncher));
            var tool = Assert.Single(tools);
            Assert.Equal(OmniCore.Domain.ScopeLevel.User, tool.Descriptor.Source.Scope);
            Assert.Equal(TrustLevel.Trusted, tool.Descriptor.Source.Trust);
            var permission = ScriptedPermissionPolicy.WithTool("ext.fixture.user.compile", PermissionDecision.Allow)
                .WithModeDefaults(OmniCore.Domain.RunMode.Act);
            var execution = new ScriptedToolExecutor(new FakeCatalog().Add(tool), permission, workspace,
                null, null, null, false, additionalBoundaries: [((ExtensionTool)tool).Boundary]);
            var toolResult = execution.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id,
                "user-extension", "{}"), userApprovesAsk: true, CancellationToken.None);
            Assert.True(toolResult.Succeeded, toolResult.Summary + " | "
                + string.Join(" | ", toolResult.Events.Select(evt => evt.GetType().Name + ":" + evt))
                + " | launches=" + rpcToolLauncher.Starts.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(1, rpcToolLauncher.Starts);
            var toolLaunch = rpcToolLauncher.LastLaunch!;
            Assert.Equal(SandboxStrength.Strong, toolLaunch.RequestedStrength);
            Assert.Equal(SandboxNetworkMode.Deny, toolLaunch.Network.Mode);
            var contributor = Assert.Single(ProjectExtensionContributors.Load(workspace, paths,
                workspaceTrusted: false, new ExtensionRpcProcessInvoker(new ProtocolFixtureLauncher("contributor"))));
            var request = new MaterializeRequest(SessionId.New(), RunId.New(), null, null, null, 0,
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "build"));
            var item = Assert.Single(await contributor.GetContextAsync(request, CancellationToken.None));
            Assert.Equal(OmniCore.Domain.ScopeLevel.User, item.Provenance.Scope);

            var validators = ProjectExtensionValidators.Load(workspace, paths, workspaceTrusted: false,
                request.SessionId, request.RunId, new ExtensionRpcProcessInvoker(new ProtocolFixtureLauncher("validator")));
            Assert.True(Assert.Single(Assert.Single(validators).Run(TestContext.Current.CancellationToken)).Passed);
            new ExtensionConsentStore(paths, ScopeLevel.User).Revoke("fixture.user");
            var afterRevoke = execution.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(),
                tool.Descriptor.Id, "revoked-user-extension", "{}"), userApprovesAsk: true,
                TestContext.Current.CancellationToken);
            Assert.False(afterRevoke.Succeeded);
            Assert.Equal(1, rpcToolLauncher.Starts);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Extension_process_refuses_package_changes_before_starting_child()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-extension-tamper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "schemas"));
        var manifestPath = Path.Combine(root, ExtensionManifestLoader.ManifestFileName);
        File.WriteAllText(manifestPath, Manifest.Replace("acme-progress-ext.exe", "runtime.exe", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(root, "runtime.exe"), "fixture runtime");
        File.WriteAllText(Path.Combine(root, "schemas", "compile.json"), "{}");
        try
        {
            var extension = Assert.Single(new ExtensionManifestLoader().Discover(root, TrustLevel.Trusted,
                workspaceTrusted: true, new HashSet<string>(["tool.register"], StringComparer.Ordinal)));
            File.WriteAllText(Path.Combine(root, "schemas", "compile.json"), "{\"changed\":true}");
            var launcher = new CountingSandboxLauncher();
            var invoker = new ExtensionRpcProcessInvoker(launcher);
            var process = new OmniCore.Domain.ProcessClaim(Path.Combine(root, "runtime.exe"), ["--omnicore-extension"],
                "ExtensionTool", WorkingDirectory: root);
            await Assert.ThrowsAsync<InvalidDataException>(() => invoker.InvokeAsync(extension, "tools/call",
                "{}", new OmniCore.Domain.ResourceClaims([], [], [], process, []), CancellationToken.None));
            Assert.Equal(0, launcher.Starts);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Package_change_during_permission_ask_stops_before_Strong_process_start()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-extension-ask-tamper-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var package = Path.Combine(workspace, ".omnicore", "extensions", "fixture.rpc");
        Directory.CreateDirectory(package);
        var schemaPath = Path.Combine(package, "schema.json");
        File.WriteAllText(schemaPath, "{\"type\":\"object\"}");
        var executable = Environment.ProcessPath!;
        File.WriteAllText(Path.Combine(package, ExtensionManifestLoader.ManifestFileName), $$"""
            manifestVersion: 1
            id: fixture.rpc
            version: 1.0.0
            displayName: Fixture RPC
            publisher: tests
            omnicoreApi: '1.0'
            provides:
              tools:
                - name: mutate
                  schema: schema.json
            requests:
              capabilities: [tool.register]
              permissions:
                process:
                  - executable: {{System.Text.Json.JsonSerializer.Serialize(Path.GetFileName(executable))}}
            runtime:
              kind: process
              command: {{System.Text.Json.JsonSerializer.Serialize(executable)}}
              args: []
            """);
        try
        {
            var extension = Assert.Single(new ExtensionManifestLoader().Discover(Path.Combine(workspace, ".omnicore", "extensions"),
                TrustLevel.Project, workspaceTrusted: true, new HashSet<string>(["tool.register"], StringComparer.Ordinal)));
            var launcher = new CountingSandboxLauncher();
            var tool = new ExtensionTool(extension, extension.Manifest!.Provides!.Tools!.Single(),
                File.ReadAllText(schemaPath), new ExtensionRpcProcessInvoker(launcher), workspace);
            var policy = new PackageMutatingPermissionPolicy(
                ScriptedPermissionPolicy.WithTool("ext.fixture.rpc.mutate", PermissionDecision.Allow)
                    .WithModeDefaults(OmniCore.Domain.RunMode.Act), schemaPath);
            var executor = new ScriptedToolExecutor(new FakeCatalog().Add(tool), policy, workspace,
                null, null, null, false, additionalBoundaries: [tool.Boundary]);
            var result = executor.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id,
                "tamper-after-prepare", "{}"), userApprovesAsk: true, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.Contains(result.Events, item => item is OmniCore.Domain.PermissionRequested);
            Assert.Contains(result.Events, item => item is OmniCore.Domain.PermissionGranted);
            Assert.Equal(0, launcher.Starts);
            Assert.DoesNotContain("changed", result.Summary, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("version")]
    [InlineData("id")]
    [InlineData("error")]
    [InlineData("timeout")]
    public void JSON_RPC_protocol_failures_are_closed_before_returning_to_the_model(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-extension-protocol-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var package = Path.Combine(workspace, ".omnicore", "extensions", "fixture.rpc");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "runtime.exe"), "test fixture");
        File.WriteAllText(Path.Combine(package, "schema.json"), "{\"type\":\"object\"}");
        File.WriteAllText(Path.Combine(package, ExtensionManifestLoader.ManifestFileName), """
            manifestVersion: 1
            id: fixture.rpc
            version: 1.0.0
            displayName: Fixture RPC
            publisher: tests
            omnicoreApi: '1.0'
            provides:
              tools:
                - name: call
                  schema: schema.json
            requests:
              capabilities: [tool.register]
              permissions:
                process:
                  - executable: runtime.exe
            runtime:
              kind: process
              command: runtime.exe
              args: []
            """);
        try
        {
            var extension = Assert.Single(new ExtensionManifestLoader().Discover(Path.Combine(workspace, ".omnicore", "extensions"),
                TrustLevel.Project, workspaceTrusted: true, new HashSet<string>(["tool.register"], StringComparer.Ordinal)));
            var launcher = new ProtocolFixtureLauncher(fixture);
            var rpc = new ExtensionRpcProcessInvoker(launcher);
            var tool = new ExtensionTool(extension, extension.Manifest!.Provides!.Tools!.Single(),
                File.ReadAllText(Path.Combine(package, "schema.json")), rpc, workspace);
            var executor = new ScriptedToolExecutor(new FakeCatalog().Add(tool),
                ScriptedPermissionPolicy.WithTool("ext.fixture.rpc.call", PermissionDecision.Allow), workspace,
                null, null, null, false, additionalBoundaries: [tool.Boundary]);
            var result = executor.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id,
                "protocol-" + fixture, "{}"), userApprovesAsk: false, CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.DoesNotContain("REMOTE_SECRET", result.Summary, StringComparison.Ordinal);
            Assert.Contains("failed safely", result.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, launcher.Starts);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async System.Threading.Tasks.Task Project_hook_uses_the_existing_strong_stdio_extension_boundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-extension-hook-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var package = Path.Combine(workspace, ".omnicore", "extensions", "fixture.hooks");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "runtime.exe"), "fixture runtime");
        File.WriteAllText(Path.Combine(package, ExtensionManifestLoader.ManifestFileName), """
            manifestVersion: 1
            id: fixture.hooks
            version: 1.0.0
            displayName: Fixture hooks
            publisher: tests
            omnicoreApi: '1.0'
            provides:
              hooks:
                - point: BeforeModel
                  capabilities: [Observe]
            requests:
              capabilities: [hook.register, hook.observe]
              permissions:
                process:
                  - executable: runtime.exe
            runtime:
              kind: process
              command: runtime.exe
              args: []
            """);
        try
        {
            var consented = new HashSet<string>(["hook.register", "hook.observe"], StringComparer.Ordinal);
            var extension = Assert.Single(new ExtensionManifestLoader().Discover(
                Path.Combine(workspace, ".omnicore", "extensions"), TrustLevel.Project,
                workspaceTrusted: true, consented));
            var launcher = new ProtocolFixtureLauncher("hook");
            var hook = new ExtensionLifecycleHook(extension, 0, workspace,
                new ExtensionRpcProcessInvoker(launcher), consented, explicitlyActivated: true);
            var result = await hook.ExecuteAsync(new OmniCore.Abstractions.HookContext(
                OmniCore.Abstractions.LifecyclePoint.BeforeModel, EventType: "BeforeModel"),
                TestContext.Current.CancellationToken);
            Assert.IsType<OmniCore.Abstractions.HookContinue>(result);
            Assert.Equal(OmniCore.Sandbox.SandboxStrength.Strong, launcher.LastLaunch!.RequestedStrength);
            Assert.Contains("hooks/execute", launcher.LastLaunch.InitialStandardInput, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", launcher.LastLaunch.InitialStandardInput, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Project_hooks_are_not_registered_until_capability_consent_is_hash_bound()
    {
        var root = Path.Combine(Path.GetTempPath(), "OmniCore-extension-hook-consent-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var package = Path.Combine(workspace, ".omnicore", "extensions", "fixture.hooks");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "runtime.exe"), "fixture runtime");
        File.WriteAllText(Path.Combine(package, ExtensionManifestLoader.ManifestFileName), """
            manifestVersion: 1
            id: fixture.hooks
            version: 1.0.0
            displayName: Fixture hooks
            publisher: tests
            omnicoreApi: '1.0'
            provides:
              hooks:
                - point: BeforeModel
                  capabilities: [Observe]
            requests:
              capabilities: [hook.register, hook.observe]
              permissions:
                process:
                  - executable: runtime.exe
            runtime:
              kind: process
              command: runtime.exe
              args: []
            """);
        try
        {
            var paths = OmniHost.CreatePlatformPaths(Path.Combine(root, "data"));
            Assert.Empty(ProjectExtensionHooks.Load(workspace, paths, workspaceTrusted: true, new CountingRpcClient()));
            var candidate = Assert.Single(new ExtensionManifestLoader().Discover(
                Path.Combine(workspace, ".omnicore", "extensions"), TrustLevel.Project, workspaceTrusted: true));
            new ExtensionConsentStore(paths, workspace).Grant(candidate,
                new HashSet<string>(["hook.register", "hook.observe"], StringComparer.Ordinal));
            var registered = Assert.Single(ProjectExtensionHooks.Load(workspace, paths, workspaceTrusted: true,
                new CountingRpcClient()));
            Assert.True(registered.Registration.Enabled);
            Assert.Equal(TrustLevel.Project, registered.Registration.Source.Trust);
            Assert.True(registered.Registration.IsEligible(OmniCore.Abstractions.HookCapability.Observe));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Extension_process_runs_only_after_permission_pipeline_authorizes_the_tool_intent()
    {
        var manifest = new ExtensionManifest
        {
            ManifestVersion = 1, Id = "fixture.rpc", Version = "1.0", DisplayName = "fixture", Publisher = "tests",
            OmnicoreApi = "1.0", Requests = new ExtensionRequestsManifest
            {
                Capabilities = ["tool.register"], Permissions = new ExtensionPermissionManifest
                { Process = [new ExtensionProcessPermissionManifest { Executable = Environment.ProcessPath! }] },
            }, Runtime = new ExtensionRuntimeManifest
            { Kind = "process", Command = Environment.ProcessPath!, Args = [] },
        };
        var extension = new DiscoveredExtension("C:\\fixture", manifest, ExtensionDiscoveryStatus.Ready, null,
            new string('b', 64), new string('c', 64), TrustLevel.Project);
        var rpc = new CountingRpcClient();
        var tool = new ExtensionTool(extension, new ExtensionToolManifest { Name = "compile", Schema = "schema.json" },
            "{\"type\":\"object\"}", rpc, "C:\\workspace");
        var deniedExecutor = new ScriptedToolExecutor(new FakeCatalog().Add(tool),
            ScriptedPermissionPolicy.WithTool("ext.fixture.rpc.compile", PermissionDecision.Deny), "C:\\workspace",
            null, null, null, false, additionalBoundaries: [tool.Boundary]);
        var denied = deniedExecutor
            .ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id, "call-1", "{}"),
                userApprovesAsk: false, CancellationToken.None);
        Assert.False(denied.Succeeded);
        Assert.Equal(0, rpc.Invocations);

        var askExecutor = new ScriptedToolExecutor(new FakeCatalog().Add(tool),
            ScriptedPermissionPolicy.WithTool("ext.fixture.rpc.compile", PermissionDecision.Allow)
                .WithModeDefaults(OmniCore.Domain.RunMode.Act), "C:\\workspace",
            null, null, null, false, additionalBoundaries: [tool.Boundary]);
        var pending = askExecutor.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id,
            "call-2", "{}"), userApprovesAsk: false, CancellationToken.None);
        Assert.False(pending.Succeeded);
        Assert.Contains(pending.Events, item => item is OmniCore.Domain.PermissionRequested);
        Assert.Equal(0, rpc.Invocations);
        var permitted = askExecutor
            .ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id, "call-2", "{}"),
                userApprovesAsk: true, CancellationToken.None);
        Assert.True(permitted.Succeeded, permitted.Summary);
        Assert.Equal(1, rpc.Invocations);

        var restricted = new ScriptedToolExecutor(new FakeCatalog().Add(tool),
            ScriptedPermissionPolicy.WithTool("ext.fixture.rpc.compile", PermissionDecision.Allow)
                .WithUserPolicyTool("ext.fixture.rpc.compile", PermissionDecision.Deny)
                .WithModeDefaults(OmniCore.Domain.RunMode.Act), "C:\\workspace",
            null, null, null, false, additionalBoundaries: [tool.Boundary]);
        var deniedByParent = restricted.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id,
            "call-3", "{}"), userApprovesAsk: true, CancellationToken.None);
        Assert.False(deniedByParent.Succeeded);
        Assert.Equal(1, rpc.Invocations);

        var narrowerBoundary = new ExtensionBoundary(new ExtensionPermissionManifest(), "fixture.rpc",
            "C:\\workspace", tool.Descriptor.Source);
        var ceilingRuntime = new ScriptedToolExecutor(new FakeCatalog().Add(tool),
            ScriptedPermissionPolicy.WithTool("ext.fixture.rpc.compile", PermissionDecision.Allow), "C:\\workspace",
            null, null, null, false, additionalBoundaries: [narrowerBoundary]);
        var outsideCeiling = ceilingRuntime.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(),
            tool.Descriptor.Id, "call-outside-ceiling", "{}"), userApprovesAsk: true, CancellationToken.None);
        Assert.False(outsideCeiling.Succeeded);
        Assert.DoesNotContain(outsideCeiling.Events, item => item is OmniCore.Domain.PermissionEvaluated);
        Assert.Equal(1, rpc.Invocations);
    }

    private sealed class NeverInvokedRpcClient : IExtensionRpcInvoker
    {
        public System.Threading.Tasks.Task<string> InvokeAsync(DiscoveredExtension extension, string method,
            string parametersJson, OmniCore.Domain.ResourceClaims authorizedClaims, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Prepare/discovery must not launch extension processes.");
    }

    private sealed class CountingRpcClient : IExtensionRpcInvoker
    {
        public int Invocations { get; private set; }
        public System.Threading.Tasks.Task<string> InvokeAsync(DiscoveredExtension extension, string method,
            string parametersJson, OmniCore.Domain.ResourceClaims authorizedClaims, CancellationToken cancellationToken)
        {
            Invocations++;
            return System.Threading.Tasks.Task.FromResult("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"output\":\"ok\"}}");
        }
    }

    private sealed class CountingSandboxLauncher : ISandboxProcessLauncher
    {
        public int Starts { get; private set; }
        public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch, CancellationToken cancellationToken)
        {
            Starts++;
            throw new InvalidOperationException("Tampered package must be rejected before process start.");
        }
    }

    private sealed class ProtocolFixtureLauncher(string fixture) : ISandboxProcessLauncher
    {
        public int Starts { get; private set; }
        public SandboxLaunchSpec? LastLaunch { get; private set; }
        public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch, CancellationToken cancellationToken)
        {
            Starts++;
            LastLaunch = launch;
            var (stdout, timedOut) = fixture switch
            {
                "hook" => ("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"kind\":\"continue\"}}", false),
                "contributor" => ("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"items\":[{\"id\":\"fixture-note\",\"kind\":\"knowledge\",\"content\":\"offline contributed knowledge\"}]}}", false),
                "tool" => ("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"ok\":true}}", false),
                "validator" => ("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{\"results\":[{\"id\":\"verify-build\",\"passed\":true,\"summary\":\"offline check passed\"}]}}", false),
                "malformed" => ("{broken", false),
                "version" => ("{\"jsonrpc\":2,\"id\":\"1\",\"result\":{}}", false),
                "id" => ("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}", false),
                "error" => ("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"error\":{\"message\":\"REMOTE_SECRET\"}}", false),
                "timeout" => ("", true),
                _ => throw new ArgumentOutOfRangeException(nameof(fixture)),
            };
            return ValueTask.FromResult<ISandboxProcessControl>(new ProtocolFixtureControl(stdout, timedOut));
        }
    }

    private sealed class ProtocolFixtureControl(string stdout, bool timedOut) : ISandboxProcessControl
    {
        public ValueTask<SandboxProcessOutput> WaitForExitAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SandboxProcessOutput(0,
                [new SandboxOutputChunk(SandboxOutputStream.StandardOutput, stdout)], timedOut));
        public ValueTask TerminateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PackageMutatingPermissionPolicy(IPermissionPolicy inner, string packageFile) : IPermissionPolicy
    {
        public OmniCore.Domain.PermissionDecisionRecord Evaluate(ToolIntent intent)
        {
            var decision = inner.Evaluate(intent);
            File.WriteAllText(packageFile, "{\"changed\":true}");
            return decision;
        }
        public AuthorizedToolIntent Authorize(ToolIntent intent) => inner.Authorize(intent);
        public AuthorizedToolIntent AuthorizeApproved(ToolIntent intent, OmniCore.Domain.GrantId? approvedGrant) =>
            inner.AuthorizeApproved(intent, approvedGrant);
    }

    private const string Manifest = """
        manifestVersion: 1
        id: acme.progress
        version: 1.4.0
        displayName: Progress OpenEdge tools
        publisher: acme
        omnicoreApi: '>=1.0 <2.0'
        provides:
          tools:
            - name: compile
              schema: schemas/compile.json
        requests:
          capabilities: [tool.register]
          permissions:
            process:
              - executable: acme-progress-ext.exe
            network: [api.example.com]
            paths:
              read: ['**/*.p']
              write: [output/*.log]
        runtime:
          kind: process
          command: acme-progress-ext.exe
          args: [--omnicore-extension]
        source:
          kind: local
          location: .
          sha256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
        """;
}
