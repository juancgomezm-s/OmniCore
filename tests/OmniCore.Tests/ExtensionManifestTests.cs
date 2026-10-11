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
            await Assert.ThrowsAsync<InvalidDataException>(() => invoker.InvokeAsync(extension, "tools/call",
                "{}", CancellationToken.None));
            Assert.Equal(0, launcher.Starts);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Extension_process_runs_only_after_permission_pipeline_authorizes_the_tool_intent()
    {
        var extension = new DiscoveredExtension("C:\\fixture", new ExtensionManifest
        {
            ManifestVersion = 1, Id = "fixture.rpc", Version = "1.0", DisplayName = "fixture", Publisher = "tests",
            OmnicoreApi = "1.0", Runtime = new ExtensionRuntimeManifest
            { Kind = "process", Command = Environment.ProcessPath!, Args = [] },
        }, ExtensionDiscoveryStatus.Ready, null, new string('b', 64), new string('c', 64), TrustLevel.Trusted);
        var rpc = new CountingRpcClient();
        var tool = new ExtensionTool(extension, new ExtensionToolManifest { Name = "compile", Schema = "schema.json" },
            "{\"type\":\"object\"}", rpc);
        var denied = ScriptedToolExecutor.WithWorkspace(new FakeCatalog().Add(tool),
            ScriptedPermissionPolicy.WithTool("ext.fixture.rpc.compile", PermissionDecision.Deny), "C:\\workspace")
            .ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id, "call-1", "{}"),
                userApprovesAsk: false, CancellationToken.None);
        Assert.False(denied.Succeeded);
        Assert.Equal(0, rpc.Invocations);

        var permitted = ScriptedToolExecutor.WithWorkspace(new FakeCatalog().Add(tool),
            ScriptedPermissionPolicy.WithTool("ext.fixture.rpc.compile", PermissionDecision.Allow), "C:\\workspace")
            .ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id, "call-2", "{}"),
                userApprovesAsk: false, CancellationToken.None);
        Assert.True(permitted.Succeeded, permitted.Summary);
        Assert.Equal(1, rpc.Invocations);
    }

    private sealed class NeverInvokedRpcClient : IExtensionRpcInvoker
    {
        public System.Threading.Tasks.Task<string> InvokeAsync(DiscoveredExtension extension, string method,
            string parametersJson, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Prepare/discovery must not launch extension processes.");
    }

    private sealed class CountingRpcClient : IExtensionRpcInvoker
    {
        public int Invocations { get; private set; }
        public System.Threading.Tasks.Task<string> InvokeAsync(DiscoveredExtension extension, string method,
            string parametersJson, CancellationToken cancellationToken)
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
              - executable: prowin.exe
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
