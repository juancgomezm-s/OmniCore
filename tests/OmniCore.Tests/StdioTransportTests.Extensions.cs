using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorkspaceId = OmniCore.Domain.WorkspaceId;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;

namespace OmniCore.Tests;

public sealed partial class StdioTransportTests
{
    [Fact]
    public async Task Extension_forms_updates_and_rollback_cross_the_signed_stdio_boundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "omnicore-extension-wire-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace); Directory.CreateDirectory(data);
        var oldData = Environment.GetEnvironmentVariable("OMNICORE_DATA_DIR");
        var oldConfig = Environment.GetEnvironmentVariable("OMNICORE_CONFIG_DIR");
        var key = RandomNumberGenerator.GetBytes(32);
        Process? host = null;
        try
        {
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", data);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", Path.Combine(root, "config"));
            var canonical = ProjectIdentity.CanonicalWorkspacePath(workspace);
            File.WriteAllText(Path.Combine(data, "trust.yaml"), "workspaces:\n  " + WorkspaceId.Of(canonical)
                + ":\n    canonicalPath: " + canonical + "\n    trusted: true\n", new UTF8Encoding(false));
            host = StartRawHost(Path.Combine(FindRepositoryRoot(), "src", "OmniCore.Cli", "bin", "Debug", "net10.0", "omni.dll"), workspace, Convert.ToHexString(key));
            var hello = RawExchange(host, WireEnvelope.FromJson("""{"version":"1.0","messageType":"hello","messageId":"extension-hello","payload":{"supportedVersions":["1.0"]}}"""));
            using var helloJson = JsonDocument.Parse(hello.PayloadJson);
            var nonce = helloJson.RootElement.GetProperty("trustedActionNonce").GetString()!;
            CommandAck Send(string payload, bool signed = true)
            {
                var command = WireEnvelope.Command(Ids.NewV7(), payload);
                return Ack(RawExchange(host, signed ? TrustedActionEnvelope(command, key, nonce, workspace, null, null)
                    : WireEnvelope.Command(Ids.NewV7(), "{\"operation\":\"command\",\"command\":" + command.ToJson() + "}")));
            }
            string Query(string name)
            {
                var response = RawExchange(host, WireEnvelope.Command(Ids.NewV7(), "{\"operation\":\"query\"," + JsonObj.Field("name", name) + "}"));
                using var payload = JsonDocument.Parse(response.PayloadJson);
                return payload.RootElement.GetProperty("json").GetString()!;
            }
            var manager = new ExtensionPackageManager(OmniHost.CreatePlatformPaths(data));
            const string prefix = "{\"cmd\":\"extensions.package\",\"extensionId\":\"example.preferences\",\"action\":";
            var source = ExtensionPlatformTests.PreferenceExample(Path.Combine(root, "source"));
            Assert.Equal("ok", Send(prefix + "\"install\"," + JsonObj.Field("source", source) + "}").Status);
            var installed = manager.Inspect("example.preferences");
            string Identity(ExtensionPackageState package) => JsonObj.Field("expectedManifestSha256", package.ManifestSha256)
                + "," + JsonObj.Field("expectedPackageSha256", package.PackageSha256);
            Assert.Equal("ok", Send(prefix + "\"enable\",\"trustProcess\":true," + Identity(installed) + "}").Status);
            var view = ExtensionProtocol.Decode(Query("extensions.view:" + Uri.EscapeDataString(ExtensionProtocol.EncodeRequest(new("example.preferences", "preferences")))));
            using var values = JsonDocument.Parse("""{"title":"Título desde stdio","interval":4.5,"enabled":true,"detail":"wide","api":"1.0"}""");
            var submission = new ExtensionFormSubmission("example.preferences", "preferences", "save", view.Revision, view.Scope!, values.RootElement.Clone());
            string Form(ExtensionFormSubmission form) => prefix + "\"submit-view\"," + JsonObj.Field("submission", JsonSerializer.Serialize(form, ExtensionJson.Default.ExtensionFormSubmission)) + "}";
            Assert.Contains("trusted_user_action_required", Send(Form(submission), signed: false).Error);
            Assert.Contains("trusted_action_invalid", Send(Form(submission with { ExtensionId = "another.package" })).Error);
            Assert.Contains("trusted_action_invalid", Send(Form(submission with { Scope = submission.Scope with { SessionId = Guid.NewGuid().ToString() } })).Error);
            Assert.Equal("ok", Send(Form(submission)).Status);
            Assert.Equal("error", Send(Form(submission)).Status); // A fresh envelope cannot reuse the consumed snapshot.
            using var configured = JsonDocument.Parse(Query("extensions.configuration:" + Uri.EscapeDataString("{\"extensionId\":\"example.preferences\",\"key\":\"display\"}")));
            Assert.Equal("Título desde stdio", configured.RootElement.GetProperty("title").GetString());
            var updated = ExtensionPlatformTests.PreferenceExample(Path.Combine(root, "updated"));
            var manifest = Path.Combine(updated, "omnicore-extension.yaml");
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("version: 1.0.0", "version: 1.1.0", StringComparison.Ordinal));
            Assert.Equal("ok", Send(prefix + "\"update\"," + JsonObj.Field("source", updated) + "," + Identity(installed) + "}").Status);
            var newer = manager.Inspect("example.preferences");
            Assert.Equal("1.1.0", newer.Version); Assert.False(newer.Enabled);
            Assert.Equal("ok", Send(prefix + "\"rollback\"," + Identity(newer) + "}").Status);
            Assert.Equal("1.0.0", manager.Inspect("example.preferences").Version);
            host.StandardInput.Close();
            await host.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, host.ExitCode);
        }
        finally
        {
            if (host is not null)
            {
                try { if (!host.HasExited) host.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { await host.WaitForExitAsync(TestContext.Current.CancellationToken); } catch (InvalidOperationException) { }
                host.Dispose();
            }
            CryptographicOperations.ZeroMemory(key);
            Environment.SetEnvironmentVariable("OMNICORE_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("OMNICORE_CONFIG_DIR", oldConfig);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }
}
