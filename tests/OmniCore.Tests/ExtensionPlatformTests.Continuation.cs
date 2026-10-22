using System.Text.Json;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed partial class ExtensionPlatformTests
{
    [Fact]
    public void Delta_order_is_preserved_and_invalid_orders_do_not_corrupt_the_cache()
    {
        var projection = new ExtensionViewProjection();
        var initial = new ExtensionView("test.files.files", "test.files", "list", 1, "ready", [new("a", "A"), new("b", "B")], []);
        Assert.True(projection.Apply(initial));
        var update = initial with { Revision = 2, Reset = false, Items = [new("a", "changed")], ItemOrder = ["a", "b"] };
        Assert.True(projection.Apply(update)); Assert.Equal(["a", "b"], projection.Get(initial.Id)!.Items.Select(item => item.Id));
        Assert.False(projection.Apply(update with { Revision = 3, ItemOrder = ["missing", "b"] }));
        Assert.Equal(2, projection.Get(initial.Id)!.Revision);
        Assert.Throws<JsonException>(() => ExtensionProtocol.Decode(ExtensionProtocol.Encode(update with { ItemOrder = ["a", "a"] })));
    }

    [Fact]
    public void Update_and_rollback_preserve_data_and_always_require_new_consent()
    {
        using var fixture = new Fixture();
        fixture.Packages.Install(fixture.Package(Declarative + "\nconfiguration: [tone]\n"), CancellationToken.None);
        var previous = fixture.Packages.Change("test.files", "enable");
        var data = Path.Combine(fixture.Data, "extension-config", "test.files"); Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "tone.json"), "42");
        var source = fixture.Package((Declarative + "\nconfiguration: [tone]\n").Replace("version: 1.0.0", "version: 1.1.0", StringComparison.Ordinal));
        var next = fixture.Packages.Install(source, CancellationToken.None, previous.Id, previous.ManifestSha256, previous.PackageSha256);
        Assert.Equal("1.1.0", next.Version); Assert.False(next.Enabled); Assert.Empty(next.GrantedCapabilities);
        Assert.Equal("42", File.ReadAllText(Path.Combine(data, "tone.json")));
        Assert.Throws<InvalidOperationException>(() => fixture.Packages.Change("test.files", "enable", false, previous.ManifestSha256, previous.PackageSha256));
        var restored = fixture.Packages.Rollback(next.Id, next.ManifestSha256, next.PackageSha256, CancellationToken.None);
        Assert.Equal("1.0.0", restored.Version); Assert.False(restored.Enabled);
        Assert.Equal(previous.PackageSha256, restored.PackageSha256);
        Assert.Equal("42", File.ReadAllText(Path.Combine(data, "tone.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Data, "extension-state"), "*.update.json"));
    }

    [Fact]
    public void Update_rejects_identity_compatibility_dependencies_and_stale_confirmation_before_replacing()
    {
        using var fixture = new Fixture(); fixture.Packages.Install(fixture.Package(Declarative), CancellationToken.None);
        var previous = fixture.Packages.Change("test.files", "enable");
        void Reject(string yaml) => Assert.Throws<InvalidOperationException>(() => fixture.Packages.Install(fixture.Package(yaml), CancellationToken.None,
            previous.Id, previous.ManifestSha256, previous.PackageSha256));
        Reject(Declarative.Replace("test.files", "test.other", StringComparison.Ordinal));
        Reject(Declarative.Replace("'1.x'", "'9.x'", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => fixture.Packages.Install(fixture.Package(Declarative), CancellationToken.None,
            previous.Id, "stale", "stale"));
        fixture.Packages.Install(fixture.Package(Declarative.Replace("test.files", "test.dependent", StringComparison.Ordinal)
            + "\ndependencies: [test.files@1.0.0]\n"), CancellationToken.None);
        Reject(Declarative.Replace("version: 1.0.0", "version: 1.1.0", StringComparison.Ordinal));
        Assert.True(fixture.Packages.Inspect(previous.Id).Enabled);
        Assert.Equal(previous.PackageSha256, fixture.Packages.Inspect(previous.Id).PackageSha256);
    }

    [Fact]
    public void Interrupted_update_recovers_previous_files_without_reenabling_code()
    {
        using var fixture = new Fixture(); fixture.Packages.Install(fixture.Package(Declarative), CancellationToken.None);
        var previous = fixture.Packages.Change("test.files", "enable");
        fixture.Packages.BeforeUpdatePublish = () => throw new IOException("injected after archive");
        Assert.Throws<IOException>(() => fixture.Packages.Install(fixture.Package(Declarative.Replace("version: 1.0.0", "version: 1.1.0", StringComparison.Ordinal)),
            CancellationToken.None, previous.Id, previous.ManifestSha256, previous.PackageSha256));
        var recovered = fixture.Packages.Inspect(previous.Id);
        Assert.Equal(previous.PackageSha256, recovered.PackageSha256); Assert.False(recovered.Enabled);

        // Simulate a hard stop after archiving the old package, before publishing the new one.
        var archiveName = previous.Id + "-" + Guid.NewGuid().ToString("N");
        Directory.Move(Path.Combine(fixture.Data, "extensions", previous.Id), Path.Combine(fixture.Data, "extension-archive", archiveName));
        var intent = new ExtensionUpdateIntent(previous.Id, archiveName, Guid.NewGuid().ToString("N"), previous);
        File.WriteAllText(Path.Combine(fixture.Data, "extension-state", previous.Id + ".update.json"),
            JsonSerializer.Serialize(intent, ExtensionLifecycleJson.Default.ExtensionUpdateIntent));
        var restarted = new ExtensionPackageManager(OmniHost.CreatePlatformPaths(fixture.Data));
        Assert.Equal(previous.PackageSha256, restarted.Inspect(previous.Id).PackageSha256);
        Assert.False(restarted.Inspect(previous.Id).Enabled);
    }

    [Fact]
    public void Wire_update_rejects_model_authority_and_stale_hashes_and_publishes_only_disabled_code()
    {
        using var fixture = new Fixture(); fixture.Packages.Install(fixture.Package(Declarative), CancellationToken.None);
        var previous = fixture.Packages.Change("test.files", "enable");
        var source = fixture.Package(Declarative.Replace("version: 1.0.0", "version: 1.1.0", StringComparison.Ordinal));
        var payload = "{\"cmd\":\"extensions.package\",\"action\":\"update\"," + JsonObj.Field("extensionId", previous.Id)
            + "," + JsonObj.Field("source", source) + "," + JsonObj.Field("expectedManifestSha256", previous.ManifestSha256)
            + "," + JsonObj.Field("expectedPackageSha256", previous.PackageSha256) + "}";
        Assert.Equal("error", fixture.Server.Send(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None).Status);
        Assert.True(fixture.Packages.Inspect(previous.Id).Enabled);
        Assert.Equal("ok", fixture.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None).Status);
        Assert.Equal("1.1.0", fixture.Packages.Inspect(previous.Id).Version); Assert.False(fixture.Packages.Inspect(previous.Id).Enabled);
        Assert.Equal("error", fixture.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None).Status);
    }

    [Fact]
    public void Real_SDK_reads_scoped_session_and_activity_and_denies_foreign_session_and_mutation()
    {
        using var fixture = new Fixture();
        Assert.Equal("ok", fixture.Server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"session.input\",\"text\":\"primera instrucción\"}"), CancellationToken.None).Status);
        var sdk = new Uri(Path.Combine(RepoRoot, "sdk", "extensions", "dist", "src", "index.js")).AbsoluteUri;
        var script = "import {Extension} from " + JsonSerializer.Serialize(sdk) + ";const ext=new Extension();ext.view(async ()=>{"
            + "const session=await ext.sessions.current();const activity=await ext.activity.read(0,1);"
            + "let denied=0;for(const [method,params] of [['sessions.current',{sessionId:'foreign'}],['workspace.write',{path:'x',text:'bad'}]])"
            + "{try{await ext.service(method,params)}catch{denied++}}"
            + "return {id:ext.id+'.files',extensionId:ext.id,kind:'tree',revision:0,state:'ready',reset:true,items:[{id:'facts',label:'facts',hasChildren:false,data:{session,activity,denied}}],actions:[]}});ext.listen();";
        var source = fixture.Package(Executable.Replace("[presentation.read, workspace.read]", "[presentation.read, workspace.read, history.read, sessions.read]", StringComparison.Ordinal), script);
        fixture.Packages.Install(source, CancellationToken.None); fixture.Packages.Change("test.files", "enable", true);
        var view = ExtensionProtocol.Decode(fixture.Server.Query(ViewQuery(), CancellationToken.None)!.Json);
        var data = Assert.Single(view.Items).Data!.Value;
        Assert.Equal(fixture.Server.LastSessionId()!.ToString(), data.GetProperty("session").GetProperty("sessionId").GetString());
        Assert.Single(data.GetProperty("activity").GetProperty("items").EnumerateArray());
        Assert.Equal(2, data.GetProperty("denied").GetInt32());
        Assert.False(File.Exists(Path.Combine(fixture.Workspace, "x")));
    }

    [Fact]
    public void Activity_pages_report_truncation_and_complete_without_duplicates_or_silent_future_cursor()
    {
        using var fixture = new Fixture();
        fixture.Server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"session.input\",\"text\":\"seed\"}"), CancellationToken.None);
        var store = fixture.Server.AcquireStore(); var session = fixture.Server.LastSessionId()!;
        var stream = new EventStream(store, fixture.Server.AcquireCodecs(), session);
        for (var i = 0; i < 310; i++) stream.Append(new UserInputReceived(fixture.Server.LastRunId()!, "\"history " + i + "\"", null));
        var sdk = new Uri(Path.Combine(RepoRoot, "sdk", "extensions", "dist", "src", "index.js")).AbsoluteUri;
        var script = "import {Extension} from " + JsonSerializer.Serialize(sdk) + ";const ext=new Extension();ext.view(async()=>{"
            + "let cursor=0;const pages=[];do{const page=await ext.activity.read(cursor,64);pages.push(page);cursor=page.nextSequence;}while(cursor<pages.at(-1).highWater);"
            + "let denied=false;try{await ext.activity.read(cursor+1)}catch{denied=true}"
            + "return {id:ext.id+'.files',extensionId:ext.id,kind:'tree',revision:0,state:'ready',reset:true,items:[{id:'pages',label:'pages',hasChildren:false,data:{pages,denied}}],actions:[]}});ext.listen();";
        fixture.Packages.Install(fixture.Package(Executable.Replace("[presentation.read, workspace.read]", "[presentation.read, history.read]", StringComparison.Ordinal), script), CancellationToken.None);
        fixture.Packages.Change("test.files", "enable", true);
        var data = Assert.Single(ExtensionProtocol.Decode(fixture.Server.Query(ViewQuery(), CancellationToken.None)!.Json).Items).Data!.Value;
        var pages = data.GetProperty("pages").EnumerateArray().ToArray();
        Assert.True(pages[0].GetProperty("gap").GetBoolean()); Assert.True(data.GetProperty("denied").GetBoolean());
        var sequences = pages.SelectMany(page => page.GetProperty("items").EnumerateArray()).Select(item => item.GetProperty("sequence").GetInt64()).ToArray();
        Assert.Equal(300, sequences.Length); Assert.Equal(sequences.Length, sequences.Distinct().Count());
        Assert.Equal(store.CurrentSequence(session), pages[^1].GetProperty("nextSequence").GetInt64());
    }

    [Fact]
    public void Node_session_configuration_does_not_follow_the_extension_into_another_session()
    {
        using var fixture = new Fixture();
        fixture.Server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"session.input\",\"text\":\"first\"}"), CancellationToken.None);
        var sdk = new Uri(Path.Combine(RepoRoot, "sdk", "extensions", "dist", "src", "index.js")).AbsoluteUri;
        var script = "import {Extension} from " + JsonSerializer.Serialize(sdk) + ";const ext=new Extension();ext.view(async()=>{"
            + "const before=await ext.config.get('tone','session');await ext.config.set('tone','own-session','session');"
            + "return {id:ext.id+'.files',extensionId:ext.id,kind:'tree',revision:0,state:'ready',reset:true,items:[{id:'before',label:JSON.stringify(before),hasChildren:false}],actions:[]}});ext.listen();";
        var manifest = Executable.Replace("[presentation.read, workspace.read]", "[presentation.read, configuration.read, configuration.write]", StringComparison.Ordinal)
            + "\nconfiguration: [tone]\n";
        fixture.Packages.Install(fixture.Package(manifest, script), CancellationToken.None); fixture.Packages.Change("test.files", "enable", true);
        string Read() => Assert.Single(ExtensionProtocol.Decode(fixture.Server.Query(ViewQuery(), CancellationToken.None)!.Json).Items).Label;
        Assert.Equal("null", Read()); Assert.Equal("\"own-session\"", Read());
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal("ok", fixture.Server.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"conversation.new\"}"), CancellationToken.None).Status);
            Assert.Equal("null", Read());
        }
    }

    [Fact]
    public void Alternative_navigation_binds_a_new_runtime_and_rejects_unpublished_targets()
    {
        using var fixture = new Fixture(); Git(fixture.Workspace, "init");
        Git(fixture.Workspace, "config", "user.name", "Test"); Git(fixture.Workspace, "config", "user.email", "test@localhost");
        File.WriteAllText(Path.Combine(fixture.Workspace, "captured.txt"), "captured");
        Git(fixture.Workspace, "add", "."); Git(fixture.Workspace, "commit", "-m", "base");
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", Path.Combine(fixture.Root, "runtime-data"));
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(fixture.Root, "runtime-config"));
            using var runtime = OmniCliRuntime.Create(fixture.Workspace);
            var client = runtime.Connect(CancellationToken.None);
            Assert.Equal("ok", client.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"session.input\",\"text\":\"kept history\"}"), CancellationToken.None).Status);
            Assert.Equal("ok", client.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"session.input\",\"text\":\"cut here\"}"), CancellationToken.None).Status);
            static WorkspacePanelResult Panel(OmniCore.Protocol.IOmniClient source, string parameters) => JsonSerializer.Deserialize(source.Query("workspace.panel:" + Uri.EscapeDataString(parameters),
                CancellationToken.None)!.Json, WorkspacePanelJson.Default.WorkspacePanelResult)!;
            var history = Panel(client, "{\"kind\":\"timeline\"}");
            var checkpoint = history.Timeline!.Last(item => item.Kind == "instruction").Checkpoint!;
            File.WriteAllText(Path.Combine(fixture.Workspace, "captured.txt"), "original stays dirty");
            var command = Ids.NewV7();
            Assert.Equal("ok", ((ITrustedUserActionClient)client).SendUserAction(WireEnvelope.Command(command,
                "{\"cmd\":\"conversation.alternative\"," + JsonObj.Field("checkpointId", checkpoint.Id) + "}"), CancellationToken.None).Status);
            var target = Panel(client, "{\"kind\":\"alternative\"," + JsonObj.Field("commandId", command) + "}").Alternative!;
            Assert.Throws<InvalidOperationException>(() => runtime.OpenAlternative(target with { Workspace = Path.Combine(fixture.Root, "foreign") }, CancellationToken.None));
            using var next = runtime.OpenAlternative(target, CancellationToken.None);
            var derived = Panel(next.Connect(CancellationToken.None), "{\"kind\":\"timeline\"}");
            Assert.Equal(target.SessionId, derived.SessionId); Assert.Equal(history.SessionId, derived.Origin!.SourceSessionId);
            Assert.Contains(next.Connect(CancellationToken.None).SubscribeSince(1), envelope => envelope.PayloadJson.Contains("kept history", StringComparison.Ordinal));
            Assert.Equal("captured", File.ReadAllText(Path.Combine(target.Workspace, "captured.txt")));
            Assert.Equal("original stays dirty", File.ReadAllText(Path.Combine(fixture.Workspace, "captured.txt")));
            Assert.Equal(history.SessionId, Panel(client, "{\"kind\":\"timeline\"}").SessionId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
        }
    }
}
