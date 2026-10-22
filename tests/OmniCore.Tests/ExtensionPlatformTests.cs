using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Host;
using OmniCore.Protocol;
using OmniCore.Abstractions;
using OmniCore.Security;
using OmniCore.Tools;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed partial class ExtensionPlatformTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "omni-extension-platform-" + Guid.NewGuid().ToString("N"));
        public string Workspace => Path.Combine(Root, "workspace");
        public string Data => Path.Combine(Root, "data");
        public ExtensionPackageManager Packages { get; }
        public OmniServer Server { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Workspace); Directory.CreateDirectory(Data);
            var paths = OmniHost.CreatePlatformPaths(Data); Packages = new(paths);
            Server = OmniHost.CreateInMemoryServer(); Server.ConfigurePlatformPaths(() => paths); Server.ConfigureWorkspaceRoot(Workspace);
        }
        public string Package(string yaml, string? code = null)
        {
            var path = Path.Combine(Root, "source-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, ExtensionManifestLoader.ManifestFileName), yaml);
            if (code is not null) File.WriteAllText(Path.Combine(path, "entry.mjs"), code);
            return path;
        }
        public void Dispose()
        {
            Server.ClosePersistentStore(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
    private const string Declarative = """
        manifestVersion: 2
        id: test.files
        publisher: test
        version: 1.0.0
        displayName: Test
        omnicoreApi: '1.x'
        activationEvents: [onView]
        provides:
          views:
            - id: files
              kind: tree
              source: workspace
        requests:
          capabilities: [presentation.read, workspace.read]
          permissions:
            paths:
              read: ['**']
        """;
    private static string Executable => Declarative.Replace("source: workspace", "source: extension", StringComparison.Ordinal)
        + "\nexecutionMode: trusted\nruntime:\n  kind: node\n  command: entry.mjs\n";
    private static string RepoRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "OmniCore.slnx"))) return dir.FullName;
            throw new IOException("repo_not_found");
        }
    }
    private static string ViewQuery(string id = "test.files", string view = "files") => "extensions.view:"
        + Uri.EscapeDataString(ExtensionProtocol.EncodeRequest(new(id, view)));

    [Fact]
    public void Declarative_install_is_disabled_and_does_not_require_or_start_Node()
    {
        using var fixture = new Fixture(); var source = fixture.Package(Declarative, "throw new Error('must not execute')");
        var installed = fixture.Packages.Install(source, TestContext.Current.CancellationToken);
        Assert.Equal("installed", installed.State); Assert.False(installed.Enabled);
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Server.Query(ViewQuery(), CancellationToken.None));
        fixture.Packages.Change("test.files", "enable");
        Assert.Contains("test.files", fixture.Server.Query("extensions.views", CancellationToken.None)!.Json);
        fixture.Packages.Change("test.files", "disable");
        Assert.Equal("disabled", fixture.Packages.Inspect("test.files").State);
        Assert.Equal("[]", fixture.Server.Query("extensions.views", CancellationToken.None)!.Json);
        fixture.Packages.Uninstall("test.files");
        Assert.Empty(fixture.Packages.List()); Assert.Single(Directory.GetDirectories(Path.Combine(fixture.Data, "extension-archive")));
    }
    [Fact]
    public void Lifecycle_requires_human_authority_even_with_direct_wire_RPC()
    {
        using var fixture = new Fixture(); var source = fixture.Package(Declarative);
        var payload = "{\"cmd\":\"extensions.package\",\"action\":\"install\"," + JsonObj.Field("source", source) + "}";
        Assert.Equal("error", fixture.Server.Send(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None).Status);
        Assert.Empty(fixture.Packages.List());
        Assert.Equal("ok", ((ITrustedUserActionClient)fixture.Server).SendUserAction(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None).Status);
    }
    [Fact]
    public void Package_hash_change_revokes_access_and_restricted_mode_never_downgrades()
    {
        using var fixture = new Fixture();
        fixture.Packages.Install(fixture.Package(Executable, "// test"), CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => fixture.Packages.Change("test.files", "enable"));
        fixture.Packages.Change("test.files", "enable", true);
        File.AppendAllText(Path.Combine(fixture.Data, "extensions", "test.files", "entry.mjs"), "// changed");
        Assert.Equal("failed", fixture.Packages.Inspect("test.files").State);
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Server.Query(ViewQuery(), CancellationToken.None));
        fixture.Packages.Uninstall("test.files");
        fixture.Packages.Install(fixture.Package(Executable.Replace("executionMode: trusted", "executionMode: restricted", StringComparison.Ordinal), "// test"), CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => fixture.Packages.Change("test.files", "enable", true));
    }
    [Fact]
    public void Traversal_collisions_dependencies_and_versions_fail_without_partial_install()
    {
        using var fixture = new Fixture();
        var archive = Path.Combine(fixture.Root, "bad.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open()); writer.Write("no"); }
        Assert.Throws<InvalidDataException>(() => fixture.Packages.Install(archive, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(fixture.Data, "extension-staging", "escape.txt")));
        var nested = fixture.Package(Declarative);
        Directory.CreateDirectory(Path.Combine(nested, "nested"));
        File.WriteAllText(Path.Combine(nested, "nested", ExtensionManifestLoader.ManifestFileName), Declarative);
        Assert.Throws<InvalidDataException>(() => fixture.Packages.Install(nested, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => fixture.Packages.Install(fixture.Package(Declarative + "\ndependencies: [missing.extension@1.0.0]\n"), CancellationToken.None));
        Assert.Empty(fixture.Packages.List());
        var state = fixture.Packages.Install(fixture.Package(Declarative.Replace("'1.x'", "'2.x'", StringComparison.Ordinal)), CancellationToken.None);
        Assert.Equal("incompatible", state.State);
        Assert.Throws<InvalidOperationException>(() => fixture.Packages.Change("test.files", "enable"));
        Assert.Throws<InvalidOperationException>(() => fixture.Packages.Install(fixture.Package(Declarative), CancellationToken.None));
    }
    [Fact]
    public void Pilots_use_real_Core_queries_and_unknown_renderers_remain_headless()
    {
        using var fixture = new Fixture(); Git(fixture.Workspace, "init");
        File.WriteAllText(Path.Combine(fixture.Workspace, "hello.txt"), "hola");
        Directory.CreateDirectory(Path.Combine(fixture.Workspace, "new-folder"));
        File.WriteAllText(Path.Combine(fixture.Workspace, "new-folder", "file.txt"), "new");
        Directory.CreateDirectory(Path.Combine(fixture.Workspace, "ignored"));
        File.WriteAllText(Path.Combine(fixture.Workspace, "ignored", "file.txt"), "ignored");
        File.WriteAllText(Path.Combine(fixture.Workspace, ".gitignore"), "ignored/\n\n");
        foreach (var directory in Directory.GetDirectories(Path.Combine(RepoRoot, "extensions", "pilots")))
        { var state = fixture.Packages.Install(directory, CancellationToken.None); fixture.Packages.Change(state.Id, "enable"); }
        var files = ExtensionProtocol.Decode(fixture.Server.Query(ViewQuery("omnicore.filetree", "files"), CancellationToken.None)!.Json);
        Assert.Contains(files.Items, item => item.Id == "hello.txt");
        Assert.Contains(files.Items, item => item.Id == "new-folder" && item.HasChildren);
        Assert.DoesNotContain(files.Items, item => item.Id == "ignored");
        var agents = ExtensionProtocol.Decode(fixture.Server.Query(ViewQuery("omnicore.agent-flow", "agents"), CancellationToken.None)!.Json);
        Assert.Equal("table", agents.Kind);
        var history = ExtensionProtocol.Decode(fixture.Server.Query(ViewQuery("omnicore.time-machine", "history"), CancellationToken.None)!.Json);
        Assert.Equal("list", history.Kind);
        var query = "extensions.view:" + Uri.EscapeDataString(ExtensionProtocol.EncodeRequest(new("omnicore.filetree", "files", ClientKinds: ["table"])));
        Assert.Equal("unsupported", ExtensionProtocol.Decode(fixture.Server.Query(query, CancellationToken.None)!.Json).State);
        Assert.Equal("inactive", fixture.Packages.Inspect("omnicore.filetree").State);
    }
    [Fact]
    public async Task Real_compiled_Node_extension_uses_Core_and_is_closed_when_disabled()
    {
        using var fixture = new Fixture(); Git(fixture.Workspace, "init");
        var sdk = Path.Combine(RepoRoot, "sdk", "extensions", "dist", "src", "index.js");
        Assert.True(File.Exists(sdk), "Build SDK first: npm ci && npm run build in sdk/extensions");
        var module = new Uri(sdk).AbsoluteUri;
        var code = "import { Extension } from " + JsonSerializer.Serialize(module) + "; const extension = new Extension();"
            + "extension.view(async () => {let denied=false;try {await extension.service('execution.execute',{});}catch{denied=true;}"
            + "return {id:extension.id+'.files',extensionId:extension.id,kind:'tree',revision:1,state:'ready',reset:true,actions:[],items:["
            + "{id:'denied',label:String(denied),hasChildren:false},{id:'git',label:'Git',hasChildren:false,data:await extension.git.status()}]};});extension.listen();";
        fixture.Packages.Install(fixture.Package(Executable, code), CancellationToken.None); fixture.Packages.Change("test.files", "enable", true);
        var result = ExtensionProtocol.Decode(fixture.Server.Query(ViewQuery(), TestContext.Current.CancellationToken)!.Json);
        Assert.Equal("true", result.Items[0].Label); Assert.NotNull(result.Items[1].Data);
        var ack = ((ITrustedUserActionClient)fixture.Server).SendUserAction(WireEnvelope.Command(Ids.NewV7(),
            "{\"cmd\":\"extensions.package\",\"action\":\"disable\",\"extensionId\":\"test.files\"}"), CancellationToken.None);
        Assert.Equal("ok", ack.Status); Assert.Throws<UnauthorizedAccessException>(() => fixture.Server.Query(ViewQuery(), CancellationToken.None));
        await Task.CompletedTask;
    }
    [Theory]
    [InlineData("process.stdout.write('not-json\\n')")]
    [InlineData("process.exit(3)")]
    [InlineData("process.stdout.write('x'.repeat(262146))")]
    public async Task Malformed_or_crashed_process_fails_without_hanging_Host(string code)
    {
        using var fixture = new Fixture(); var source = fixture.Package(Executable, code);
        await using var process = new SupervisedExtensionProcess("node", [Path.Combine(source, "entry.mjs")], source,
            (_, _, _) => throw new UnauthorizedAccessException());
        await Assert.ThrowsAnyAsync<Exception>(() => process.InitializeAsync(new("1.0", "test.files", [], null), TestContext.Current.CancellationToken));
        Assert.Equal("failed", process.State);
    }
    [Fact]
    public async Task Hanging_process_is_cancelled_and_no_request_is_automatically_retried()
    {
        using var fixture = new Fixture(); var source = fixture.Package(Executable, "process.stdin.resume();setInterval(()=>{},1000)");
        await using var process = new SupervisedExtensionProcess("node", [Path.Combine(source, "entry.mjs")], source,
            (_, _, _) => throw new UnauthorizedAccessException());
        using var value = JsonDocument.Parse("{}");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.InvokeAsync("initialize", value.RootElement,
            TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken));
        Assert.Equal("failed", process.State);
        await Assert.ThrowsAsync<IOException>(() => process.InvokeAsync("initialize", value.RootElement, TimeSpan.FromSeconds(1), CancellationToken.None));
    }
    [Fact]
    public void Projection_contract_safe_integer_revisions_gap_recovery_and_four_languages()
    {
        var projection = new ExtensionViewProjection(); var view = new ExtensionView("test.files.files", "test.files", "tree", 5, "ready", [], []);
        Assert.True(projection.Apply(view)); Assert.False(projection.Apply(view with { Reset = false, Revision = 7 }));
        Assert.True(projection.Apply(view with { Revision = 1 }));
        Assert.Equal(view.Id, ExtensionProtocol.Decode(ExtensionProtocol.Encode(view)).Id);
        var form = view with { Kind = "form", Fields = [new("enabled", "Enabled", "boolean", Required: true)] };
        Assert.Equal("boolean", Assert.Single(ExtensionProtocol.Decode(ExtensionProtocol.Encode(form)).Fields!).Kind);
        Assert.Throws<JsonException>(() => ExtensionProtocol.Decode(ExtensionProtocol.Encode(form with { Fields = [null!] })));
        foreach (var locale in new[] { "es", "en", "fr", "it" })
            foreach (var key in ExtensionResources.Keys) Assert.False(new Localization(locale).ResolveDetailed(key).UsedFallback);
        Assert.Equal(["extensions", "install", "package-path"], HumanCommandAliases.NormalizeCli(["extensiones", "instalar", "package-path"]));
        Assert.Equal(["extensions", "enable", "test.files"], HumanCommandAliases.NormalizeCli(["extensions", "activer", "test.files"]));
        Assert.Equal(["extensions", "uninstall", "test.files"], HumanCommandAliases.NormalizeCli(["estensioni", "disinstalla", "test.files"]));
    }
    [Fact]
    public void V2_Node_tool_runs_through_existing_permission_and_executor_pipeline()
    {
        using var fixture = new Fixture();
        var sdk = new Uri(Path.Combine(RepoRoot, "sdk", "extensions", "dist", "src", "index.js")).AbsoluteUri;
        var manifest = Executable.Replace("activationEvents: [onView]", "activationEvents: [onView, onCommand]", StringComparison.Ordinal)
            .Replace("provides:", "provides:\n  tools:\n    - name: hello\n      schema: hello.json", StringComparison.Ordinal)
            .Replace("[presentation.read, workspace.read]", "[presentation.read, workspace.read, tool.register]", StringComparison.Ordinal)
            .Replace("permissions:", "permissions:\n    process:\n      - executable: '**/entry.mjs'", StringComparison.Ordinal);
        var source = fixture.Package(manifest, "import {Extension} from " + JsonSerializer.Serialize(sdk)
            + ";const ext=new Extension();ext.tool('hello',()=>({text:'hola desde Node'}));ext.listen();");
        File.WriteAllText(Path.Combine(source, "hello.json"), "{\"type\":\"object\"}");
        fixture.Packages.Install(source, CancellationToken.None); fixture.Packages.Change("test.files", "enable", true);
        var paths = OmniHost.CreatePlatformPaths(fixture.Data);
        var tool = Assert.IsType<ExtensionTool>(Assert.Single(ProjectExtensionTools.Load(fixture.Workspace, paths, false, fixture.Server.CreateExtensionInvoker())));
        var permission = ScriptedPermissionPolicy.WithTool(tool.Descriptor.Id.ToString(), PermissionDecision.Allow).WithModeDefaults(RunMode.Act);
        var executor = new ScriptedToolExecutor(new FakeCatalog().Add(tool), permission, fixture.Workspace,
            null, null, null, false, additionalBoundaries: [tool.Boundary]);
        var result = executor.ExecuteToolWithoutJournal(new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id, "v2-node", "{}"),
            userApprovesAsk: true, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Summary);
        Assert.Single(result.Events.OfType<ToolCallSucceeded>());
    }
    [Fact]
    public void Incremental_views_repair_gaps_and_observe_external_files()
    {
        using var fixture = new Fixture(); Git(fixture.Workspace, "init");
        fixture.Packages.Install(fixture.Package(Declarative), CancellationToken.None); fixture.Packages.Change("test.files", "enable");
        var initial = ExtensionProtocol.Decode(fixture.Server.Query(ViewQuery(), CancellationToken.None)!.Json);
        File.WriteAllText(Path.Combine(fixture.Workspace, "external.txt"), "external");
        var request = new ExtensionViewRequest("test.files", "files", AfterRevision: initial.Revision);
        var next = ExtensionProtocol.Decode(fixture.Server.Query("extensions.view:" + Uri.EscapeDataString(ExtensionProtocol.EncodeRequest(request)), CancellationToken.None)!.Json);
        Assert.False(next.Reset); Assert.Equal(initial.Revision + 1, next.Revision); Assert.Contains(next.Items, item => item.Id == "external.txt");
        var projection = new ExtensionViewProjection(); Assert.True(projection.Apply(initial)); Assert.True(projection.Apply(next));
        Assert.Contains(projection.Get(next.Id)!.Items, item => item.Id == "external.txt");
        var reset = ExtensionProtocol.Decode(fixture.Server.Query("extensions.view:" + Uri.EscapeDataString(ExtensionProtocol.EncodeRequest(request with { AfterRevision = 999 })), CancellationToken.None)!.Json);
        Assert.True(reset.Reset);
    }
    [Fact]
    public void Configuration_is_namespaced_by_extension_and_workspace_and_does_not_change_permissions()
    {
        using var fixture = new Fixture();
        fixture.Packages.Install(fixture.Package(Declarative + "\nconfiguration: [tone]\n"), CancellationToken.None);
        fixture.Packages.Install(fixture.Package(Declarative.Replace("test.files", "test.other", StringComparison.Ordinal) + "\nconfiguration: [tone]\n"), CancellationToken.None);
        var action = WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"extensions.package\",\"action\":\"configure\",\"extensionId\":\"test.files\",\"key\":\"tone\",\"value\":\"42\"}");
        Assert.Equal("ok", fixture.Server.SendUserAction(action, CancellationToken.None).Status);
        string QueryConfig(string id) => fixture.Server.Query("extensions.configuration:" + Uri.EscapeDataString("{" + JsonObj.Field("extensionId", id)
            + ",\"key\":\"tone\"}"), CancellationToken.None)!.Json;
        Assert.Equal("42", QueryConfig("test.files")); Assert.Equal("null", QueryConfig("test.other"));
        var second = Path.Combine(fixture.Root, "other-workspace"); Directory.CreateDirectory(second);
        fixture.Server.ConfigureWorkspaceRoot(second); Assert.Equal("null", QueryConfig("test.files"));
        Assert.False(fixture.Packages.Inspect("test.files").Enabled);
    }
    [Fact]
    public void Malformed_view_requests_and_responses_are_typed_failures_not_null_dereferences()
    {
        Assert.Throws<JsonException>(() => ExtensionProtocol.Decode("{}"));
        Assert.Throws<JsonException>(() => ExtensionProtocol.DecodeRequest("{}"));
        Assert.Throws<JsonException>(() => ExtensionProtocol.DecodeRequest("{\"extensionId\":\"test.files\",\"viewId\":\"files\",\"path\":null}"));
    }
    [Fact]
    public async Task Handshake_cannot_claim_another_identity_or_extra_capabilities()
    {
        using var fixture = new Fixture();
        var code = "process.stdin.once('data',()=>process.stdout.write(JSON.stringify({jsonrpc:'2.0',id:'1',result:{protocolVersion:'1.0',extensionId:'other.identity',capabilities:['secrets.read']}})+'\\n'));";
        var source = fixture.Package(Executable, code);
        await using var process = new SupervisedExtensionProcess("node", [Path.Combine(source, "entry.mjs")], source,
            (_, _, _) => throw new UnauthorizedAccessException());
        await Assert.ThrowsAsync<InvalidDataException>(() => process.InitializeAsync(new("1.0", "test.files", [], null), TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Revoking_a_grant_stops_an_idle_process_without_a_new_RPC()
    {
        using var fixture = new Fixture();
        var sdk = new Uri(Path.Combine(RepoRoot, "sdk", "extensions", "dist", "src", "index.js")).AbsoluteUri;
        var source = fixture.Package(Executable, "import {Extension} from " + JsonSerializer.Serialize(sdk) + ";new Extension().listen();");
        var granted = true;
        await using var process = new SupervisedExtensionProcess("node", [Path.Combine(source, "entry.mjs")], source,
            (_, _, _) => throw new UnauthorizedAccessException(), () => Volatile.Read(ref granted));
        await process.InitializeAsync(new("1.0", "test.files", [], null), TestContext.Current.CancellationToken);
        Volatile.Write(ref granted, false);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (process.State == "active" && DateTimeOffset.UtcNow < deadline) await Task.Delay(40, TestContext.Current.CancellationToken);
        Assert.Equal("failed", process.State); Assert.Contains("extension_authority_revoked", process.Diagnostics);
    }
    [Fact]
    public async Task Node_pre_hook_uses_existing_M8_dispatcher_and_can_only_restrict()
    {
        using var fixture = new Fixture();
        var sdk = new Uri(Path.Combine(RepoRoot, "sdk", "extensions", "dist", "src", "index.js")).AbsoluteUri;
        var manifest = Executable.Replace("activationEvents: [onView]", "activationEvents: [onView, onSession]", StringComparison.Ordinal)
            .Replace("provides:", "provides:\n  hooks:\n    - point: BeforeTool\n      capabilities: [Observe, Restrict]", StringComparison.Ordinal)
            .Replace("[presentation.read, workspace.read]", "[presentation.read, workspace.read, hook.register, hook.observe, hook.restrict]", StringComparison.Ordinal)
            .Replace("permissions:", "permissions:\n    process:\n      - executable: '**/entry.mjs'", StringComparison.Ordinal);
        var source = fixture.Package(manifest, "import {Extension} from " + JsonSerializer.Serialize(sdk)
            + ";const ext=new Extension();ext.before('BeforeTool',()=>({kind:'restrict',deniedToolIds:['filesystem.write']}));ext.listen();");
        fixture.Packages.Install(source, CancellationToken.None); fixture.Packages.Change("test.files", "enable", true);
        var hooks = ProjectExtensionHooks.Load(fixture.Workspace, OmniHost.CreatePlatformPaths(fixture.Data), false, fixture.Server.CreateExtensionInvoker());
        var result = await new LifecycleHookDispatcher().DispatchAsync(hooks, new(LifecyclePoint.BeforeTool, ToolId: "filesystem.write"), TestContext.Current.CancellationToken);
        Assert.True(result.Denied); Assert.Contains("filesystem.write", result.DeniedToolIds);
    }
    [Fact]
    public void Revoking_existing_M8_consent_also_revokes_platform_access()
    {
        using var fixture = new Fixture(); fixture.Packages.Install(fixture.Package(Declarative), CancellationToken.None);
        fixture.Packages.Change("test.files", "enable");
        new ExtensionConsentStore(OmniHost.CreatePlatformPaths(fixture.Data), ScopeLevel.User).Revoke("test.files");
        Assert.False(fixture.Packages.Inspect("test.files").Enabled);
        Assert.Equal("[]", fixture.Server.Query("extensions.views", CancellationToken.None)!.Json);
        fixture.Packages.Change("test.files", "enable"); Assert.True(fixture.Packages.Inspect("test.files").Enabled);
    }
    private static void Git(string root, params string[] arguments)
    {
        using var process = new Process { StartInfo = new("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start(); var error = process.StandardError.ReadToEnd(); process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(10000)); Assert.True(process.ExitCode == 0, error);
    }
}
