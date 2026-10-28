using System.Text.Json;
using OmniCore.Protocol;

namespace OmniCore.Cli;

public sealed partial class CliApp
{
    private static async Task<int> RunCompanionHubCliAsync(string[] args)
    {
        try
        {
            var hub = Runtime.CompanionHub;
            if (args[1] is "model" or "modelo" or "settings")
            {
                var current = hub.ChatSettings();
                var model = args.Skip(2).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
                if (model is not null || args.Contains("--local-only") || args.Contains("--any-model"))
                {
                    current = new(model ?? current.ModelId, args.Contains("--local-only") || !args.Contains("--any-model") && current.LocalOnly);
                    hub.SaveChatSettings(current, Runtime.CompanionModels());
                }
                Console.WriteLine(args.Contains("--text") ? (current.ModelId ?? "sin elegir") + " | Solo local: " + current.LocalOnly
                    : JsonSerializer.Serialize(current, CompanionCoordinatorJson.Default.CompanionChatSettings));
            }
            else if (args[1] is "usage" or "consumo")
            {
                var totals = hub.ChatUsage();
                Console.WriteLine(args.Contains("--text") ? TuiApp.FormatCompanionUsage(totals)
                    : JsonSerializer.Serialize(totals, CompanionCoordinatorJson.Default.CompanionChatUsageArray));
            }
            else if (args[1] == "chat")
            {
                var text = string.Join(" ", args.Skip(2).Where(a => a is not ("--json" or "--text")));
                var key = "cli/" + Environment.CurrentDirectory;
                var history = hub.Chat(key).Append(new CompanionChatMessage("user", text)).TakeLast(24).ToArray();
                var reply = await Runtime.CompanionChatAsync(history, CancellationToken.None).ConfigureAwait(false);
                hub.SaveChat(key, history.Append(new("assistant", reply)));
                Console.WriteLine(args.Contains("--text") ? reply : JsonSerializer.Serialize(new CompanionChatMessage("assistant", reply),
                    CompanionCoordinatorJson.Default.CompanionChatMessage));
            }
            else
            {
                var snapshot = hub.Read();
                if (args.Contains("--text"))
                    Console.WriteLine(args[1] is "memory" or "memoria" ? snapshot.Memory.Path + "\n" + snapshot.Memory.Text
                        : args[1] is "alerts" or "avisos" ? string.Join("\n", snapshot.Notices.Select(n => n.Session + " | " + n.Title))
                        : string.Join("\n", snapshot.Instances.Select(i => i.Id + " | " + i.Workspace + " | " + i.Session + " | " + i.State)));
                else Console.WriteLine(JsonSerializer.Serialize(snapshot, CompanionCoordinatorJson.Default.CompanionHubSnapshot));
            }
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Console.WriteLine("{\"error\":\"" + JsonObj.Escape(OmniCore.Host.OmniCliRuntime.RedactSensitive(error.Message)) + "\"}"); return 2; }
    }
}
