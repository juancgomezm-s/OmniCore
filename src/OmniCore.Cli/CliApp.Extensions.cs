using System.Text.Json;
using OmniCore.Client;
using OmniCore.Protocol;

namespace OmniCore.Cli;

public sealed partial class CliApp
{
    private static Task<int> RunExtensionPlatform(string[] args)
    {
        try
        {
            var client = Runtime.Connect(CancellationToken.None); var operation = args[1];
            if (operation == "config")
            {
                if (args.Length is < 4 or > 5) throw new ArgumentException("extensions config <extensionId> <key> [JSON-value]");
                if (args.Length == 4)
                    Console.WriteLine(client.Query("extensions.configuration:" + Uri.EscapeDataString("{" + JsonObj.Field("extensionId", args[2])
                        + "," + JsonObj.Field("key", args[3]) + "}"), CancellationToken.None)?.Json);
                else if (client is ITrustedUserActionClient configurationClient)
                {
                    var configurationAck = configurationClient.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"extensions.package\",\"action\":\"configure\","
                        + JsonObj.Field("extensionId", args[2]) + "," + JsonObj.Field("key", args[3]) + "," + JsonObj.Field("value", args[4]) + "}"), CancellationToken.None);
                    if (configurationAck.Status != "ok") throw new IOException(configurationAck.Error);
                    Console.WriteLine(Loc().Resolve("extension.platform.done"));
                }
                else throw new UnauthorizedAccessException("trusted_user_action_required");
                return Task.FromResult(0);
            }
            if (operation is "list" or "inspect" or "views" or "view")
            {
                var query = operation == "views" ? "extensions.views" : "extensions.packages";
                if (operation == "view")
                {
                    if (args.Length is < 4 or > 5) throw new ArgumentException("extensions view <extensionId> <viewId> [path]");
                    query = "extensions.view:" + Uri.EscapeDataString(ExtensionProtocol.EncodeRequest(new(args[2], args[3], args.Length == 5 ? args[4] : "")));
                }
                var result = client.Query(query, CancellationToken.None) ?? throw new IOException("extension_query_unavailable");
                if (operation == "inspect")
                {
                    if (args.Length != 3) throw new ArgumentException("extensions inspect <extensionId>");
                    using var parsed = JsonDocument.Parse(result.Json);
                    var package = parsed.RootElement.EnumerateArray().Single(item => item.GetProperty("id").GetString() == args[2]);
                    Console.WriteLine(package.GetRawText());
                }
                else Console.WriteLine(result.Json);
                return Task.FromResult(0);
            }
            if (operation is "update" or "rollback")
            {
                if (args.Length != (operation == "update" ? 4 : 3)) throw new ArgumentException(Loc().Resolve("extension.platform.update_usage"));
                if (client is not ITrustedUserActionClient lifecycle) throw new UnauthorizedAccessException("trusted_user_action_required");
                var current = JsonSerializer.Deserialize(client.Query("extensions.packages", CancellationToken.None)?.Json ?? "[]",
                    ExtensionJson.Default.ExtensionPackageStateArray)!.Single(package => package.Id == args[2]);
                var outcome = lifecycle.SendUserAction(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"extensions.package\","
                    + JsonObj.Field("action", operation) + "," + JsonObj.Field("extensionId", current.Id)
                    + "," + JsonObj.Field("expectedManifestSha256", current.ManifestSha256) + "," + JsonObj.Field("expectedPackageSha256", current.PackageSha256)
                    + (operation == "update" ? "," + JsonObj.Field("source", Path.GetFullPath(args[3])) : "") + "}"), CancellationToken.None);
                Console.WriteLine(outcome.Status == "ok" ? Loc().Resolve("extension.platform.updated_disabled")
                    : Loc().Resolve("extension.platform.failed") + ": " + outcome.Error);
                return Task.FromResult(outcome.Status == "ok" ? 0 : 1);
            }
            if (args.Length is < 3 or > 4 || args.Length == 4 && (operation != "enable" || args[3] != "--trust-process"))
                throw new ArgumentException("extensions install <directory|zip> | enable <id> [--trust-process] | disable|uninstall <id>");
            if (client is not ITrustedUserActionClient trusted) throw new UnauthorizedAccessException("trusted_user_action_required");
            ExtensionPackageState? expected = null;
            if (operation is "enable" or "uninstall")
                expected = JsonSerializer.Deserialize(client.Query("extensions.packages", CancellationToken.None)?.Json ?? "[]",
                    ExtensionJson.Default.ExtensionPackageStateArray)?.Single(item => item.Id == args[2]);
            var payload = "{" + JsonObj.Field("cmd", "extensions.package") + "," + JsonObj.Field("action", operation)
                + "," + JsonObj.Field(operation == "install" ? "source" : "extensionId", operation == "install" ? Path.GetFullPath(args[2]) : args[2])
                + (expected is null ? "" : "," + JsonObj.Field("expectedManifestSha256", expected.ManifestSha256) + "," + JsonObj.Field("expectedPackageSha256", expected.PackageSha256))
                + (args.Length == 4 ? ",\"trustProcess\":\"true\"" : "") + "}";
            var ack = trusted.SendUserAction(WireEnvelope.Command(Ids.NewV7(), payload), CancellationToken.None);
            Console.WriteLine(ack.Status == "ok" ? Loc().Resolve("extension.platform.done")
                : Loc().Resolve("extension.platform.failed") + ": " + ack.Error);
            return Task.FromResult(ack.Status == "ok" ? 0 : 1);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or UnauthorizedAccessException)
        { Console.Error.WriteLine(Loc().Resolve("extension.platform.failed") + ": " + error.Message); return Task.FromResult(1); }
    }
}
