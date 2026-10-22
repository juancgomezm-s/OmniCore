using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Protocol;
using OmniCore.Sandbox;

namespace OmniCore.Tests;

public sealed class ExtensionIsolationTests
{
    [Fact]
    public async Task Cancelled_launch_never_executes_code_and_cleans_its_private_image()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-extension-cancel-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "package"); var storage = Path.Combine(root, "runtime");
        Directory.CreateDirectory(package);
        try
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IsolatedExtensionProcess.StartAsync("node",
                ["-e", "throw new Error('must-not-execute')"], package, storage, cancelled.Token));
            Assert.Empty(Directory.GetDirectories(storage));
            Assert.Empty(Directory.GetFiles(package));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Unsupported_backend_or_network_policy_has_no_unisolated_fallback()
    {
        var launch = new SandboxLaunchSpec(SandboxStrength.Strong, "node", ["-e", "throw new Error('must-not-run')"], null,
            new Dictionary<string, string>(), [], new(SandboxNetworkMode.AllowAll, [])) { Duplex = true };
        await Assert.ThrowsAnyAsync<NotSupportedException>(() => new LinuxBubblewrapProcessLauncher()
            .StartAsync(launch, TestContext.Current.CancellationToken).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_isolated_runtime_can_start_without_ambient_environment(bool fileEntry)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-extension-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var entry = Path.Combine(root, "entry.mjs"); File.WriteAllText(entry, "console.log('isolated-ready')");
            await using var process = await IsolatedExtensionProcess.StartAsync("node", fileEntry ? [entry] : ["-e", "console.log('isolated-ready')"], root,
                token: TestContext.Current.CancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var stdout = new StreamReader(process.StandardOutput, Encoding.UTF8, leaveOpen: true);
            using var stderr = new StreamReader(process.StandardError, Encoding.UTF8, leaveOpen: true);
            var output = stdout.ReadToEndAsync(deadline.Token); var errors = stderr.ReadToEndAsync(deadline.Token);
            var exit = await process.WaitForExitAsync(deadline.Token);
            Assert.True(exit == 0, "Exit: " + exit + "; " + await errors);
            Assert.Contains("isolated-ready", await output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Extension_cannot_write_Core_package_or_workspace_read_secrets_or_use_network_even_through_a_child()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-extension-escape-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "package"); Directory.CreateDirectory(package);
        var sentinel = Path.Combine(root, "core-credentials.fixture"); File.WriteAllText(sentinel, "private-fixture-value");
        if (OperatingSystem.IsWindows())
        {
            // Even an ambient ALL APPLICATION PACKAGES read ACE must not defeat LPAC.
            var info = new FileInfo(sentinel); var security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-2-1"),
                FileSystemRights.Read, AccessControlType.Allow)); info.SetAccessControl(security);
        }
        const string poison = "OMNICORE_EXTENSION_ISOLATION_TEST_SECRET";
        var previousPoison = Environment.GetEnvironmentVariable(poison);
        Environment.SetEnvironmentVariable(poison, "must-not-be-inherited");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var script = """
            import {readFileSync,writeFileSync} from 'node:fs';
            import {spawnSync} from 'node:child_process';
            import net from 'node:net'; import readline from 'node:readline';
            const blocked=fn=>{try{fn();return false}catch{return true}};
            for await (const line of readline.createInterface({input:process.stdin})) {
              const r=JSON.parse(line); if(!r.id)continue;
              if(r.method==='initialize') {
                console.log(JSON.stringify({jsonrpc:'2.0',id:r.id,result:r.params}));continue;
              }
              if(r.method==='shutdown') {console.log(JSON.stringify({jsonrpc:'2.0',id:r.id,result:{}}));break;}
              const p=r.params;
              console.error(JSON.stringify({level:'info',code:'probe.child-start'}));
              const child=spawnSync(process.execPath,['-e',`try{require('fs').writeFileSync(${JSON.stringify(p.sentinel)},'escaped');process.exit(0)}catch{process.exit(42)}`],{stdio:'ignore',timeout:2000});
              console.error(JSON.stringify({level:'info',code:'probe.child-done'}));
              const networkBlocked=await new Promise(resolve=>{const s=net.connect(p.port,'127.0.0.1');const done=v=>{s.destroy();resolve(v)};s.once('connect',()=>done(false));s.once('error',()=>done(true));s.setTimeout(1000,()=>done(true))});
              console.error(JSON.stringify({level:'info',code:'probe.network-done'}));
              writeFileSync(process.env.OMNICORE_EXTENSION_DATA_DIR+'/own.txt','own-private-data');
              const result={readBlocked:blocked(()=>readFileSync(p.sentinel)),coreWriteBlocked:blocked(()=>writeFileSync(p.sentinel,'modified')),
                packageWriteBlocked:blocked(()=>writeFileSync(new URL('./entry.mjs',import.meta.url),'modified')),
                workspaceWriteBlocked:blocked(()=>writeFileSync(p.sentinel+'.new','modified')),
                childBlocked:child.error!==undefined||child.status!==0,networkBlocked,
                ambientAbsent:process.env.OMNICORE_EXTENSION_ISOLATION_TEST_SECRET===undefined,
                privateData:readFileSync(process.env.OMNICORE_EXTENSION_DATA_DIR+'/own.txt','utf8')};
              console.log(JSON.stringify({jsonrpc:'2.0',id:r.id,result}));
            }
            """;
        var entry = Path.Combine(package, "entry.mjs"); File.WriteAllText(entry, script);
        try
        {
            await using var process = new SupervisedExtensionProcess("node", [entry], package,
                (_, _, _) => throw new UnauthorizedAccessException());
            var token = TestContext.Current.CancellationToken;
            await process.InitializeAsync(new("1.0", "test.isolation", [], null), token);
            var parameters = JsonSerializer.SerializeToElement(new { sentinel, port });
            JsonElement result;
            try { result = await process.InvokeAsync("probe", parameters, TimeSpan.FromSeconds(15), token); }
            catch (Exception error) { throw new IOException(string.Join(",", process.Diagnostics), error); }
            foreach (var name in new[] { "readBlocked", "coreWriteBlocked", "packageWriteBlocked", "workspaceWriteBlocked", "childBlocked", "networkBlocked", "ambientAbsent" })
                Assert.True(result.GetProperty(name).GetBoolean(), name);
            Assert.Equal("own-private-data", result.GetProperty("privateData").GetString());
            Assert.Equal("private-fixture-value", File.ReadAllText(sentinel)); Assert.Equal(script, File.ReadAllText(entry));
            Assert.False(File.Exists(sentinel + ".new")); Assert.False(listener.Pending());
        }
        finally { Environment.SetEnvironmentVariable(poison, previousPoison); Directory.Delete(root, recursive: true); }
    }
}
