using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed partial class ExtensionPlatformTests
{
    [Fact]
    public void Trusted_non_Node_executable_negotiates_and_serves_a_real_view_without_the_SDK()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("The executable fixture uses a Linux Python shebang."); return; }
        if (!File.Exists("/usr/bin/python3")) { Assert.Skip("Python 3 is required for this interoperability fixture."); return; }
        using var fixture = new Fixture();
        var manifest = Declarative.Replace("source: workspace", "source: extension", StringComparison.Ordinal)
            + "\nexecutionMode: trusted\nruntime:\n  kind: process\n  command: entry.py\n";
        var source = fixture.Package(manifest);
        var entry = Path.Combine(source, "entry.py");
        File.WriteAllText(entry, """
            #!/usr/bin/python3
            import json, sys
            for line in sys.stdin:
                request = json.loads(line)
                if "id" not in request:
                    continue
                if request["method"] == "initialize":
                    result = request["params"]
                elif request["method"] == "view.read":
                    result = {"id":"test.files.files", "extensionId":"test.files", "kind":"tree",
                              "revision":1, "state":"ready", "items":[{"id":"python", "label":"Python real · español"}],
                              "actions":[]}
                else:
                    print(json.dumps({"jsonrpc":"2.0", "id":request["id"],
                                      "error":{"code":-32601, "message":"Unknown method"}}), flush=True)
                    continue
                print(json.dumps({"jsonrpc":"2.0", "id":request["id"], "result":result}), flush=True)
            """.Replace("\r\n", "\n", StringComparison.Ordinal));
        File.SetUnixFileMode(entry, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var installed = fixture.Packages.Install(source, CancellationToken.None);
        Assert.False(installed.Enabled);
        fixture.Packages.Change("test.files", "enable", true);
        var view = ExtensionProtocol.Decode(fixture.Server.Query(ViewQuery(), TestContext.Current.CancellationToken)!.Json);
        Assert.Equal("Python real · español", Assert.Single(view.Items).Label);
        Assert.NotNull(view.Scope?.SnapshotId);
    }
}
