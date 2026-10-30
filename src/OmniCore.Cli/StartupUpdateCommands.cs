namespace OmniCore.Cli;

using OmniCore.Host;

/// <summary>Private launcher protocol. It executes before CliApp initializes a conversation.
/// All output is bounded machine data; it never reads stdin or asks for confirmation.</summary>
internal static class StartupUpdateCommands
{
    internal static bool Matches(string[] args) => args.Length >= 3 && args[0] == "update" && args[1] == "startup";

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            using var service = ReleaseStartupUpdates.CreateDefault();
            if (args.Length == 3 && args[2] == "snapshot")
            { Console.WriteLine(service.SnapshotJson()); return 0; }
            if (args.Length == 3 && args[2] == "prepare")
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                await service.PrepareAsync(deadline.Token).ConfigureAwait(false);
                Console.WriteLine(service.SnapshotJson()); return 0;
            }
            if (args.Length is 4 or 5 && args[2] == "launch"
                && (args.Length == 4 || args[4] == "restart"))
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                Console.WriteLine(ReleaseStartupUpdates.Encode(await service.LaunchAsync(args[3], args.Length == 5,
                    deadline.Token).ConfigureAwait(false)));
                return 0;
            }
            return 2;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            // The regular runtime still checks the install barrier before opening a Session.
            Console.WriteLine(ReleaseStartupUpdates.UnavailableLaunchJson());
            return 0;
        }
    }
}
