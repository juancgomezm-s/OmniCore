namespace OmniCore.Tests;

using OmniCore.Client;
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

public sealed class ExtensionManifestTests
{
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
        var server = OmniHost.CreateInMemoryServer();
        server.ConfigurePlatformPaths(() => paths);
        server.ConfigureWorkspaceRoot(workspace);
        try
        {
            var untrusted = server.Query("extensions", CancellationToken.None)!.Json;
            using (var snapshot = System.Text.Json.JsonDocument.Parse(untrusted))
                Assert.Equal("WorkspaceTrustRequired", snapshot.RootElement.GetProperty("extensions")[0].GetProperty("status").GetString());

            new WorkspaceTrustStore(paths).SetTrusted(workspace, true);
            var trustedWorkspace = server.Query("extensions", CancellationToken.None)!.Json;
            using var trustedSnapshot = System.Text.Json.JsonDocument.Parse(trustedWorkspace);
            Assert.Equal("ConsentRequired", trustedSnapshot.RootElement.GetProperty("extensions")[0].GetProperty("status").GetString());

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
        public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch, CancellationToken cancellationToken)
        {
            Starts++;
            var (stdout, timedOut) = fixture switch
            {
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
