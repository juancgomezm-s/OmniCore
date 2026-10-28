using OmniCore.Client;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private bool HandleDirectControlsSlash(string input)
    {
        if (input is "/think on" or "/think off")
        {
            if (_activeInteraction is null && _overlay is null)
            { _composer!.Text = ""; SetReasoningVisibility(input.EndsWith(" on", StringComparison.Ordinal)); }
            return true;
        }
        Action? open = input switch
        {
            "/mode" => ShowModePreferences,
            "/permission" or "/permissions" => ShowExecutionPermissions,
            "/effort" or "/reasoning" => () => ShowReasoningPreferences(),
            "/think" => ShowReasoningVisibility,
            _ => null
        };
        if (open is null) return false;
        if (_activeInteraction is not null || _overlay is not null) return true;
        _composer!.Text = "";
        open();
        return true;
    }

    private bool TryHandleWorkspaceNavigation(string command)
    {
        if (!WorkspaceNavigationCommand.TryParse(command, out var destination)) return false;
        if (_turnBusy || _activeInteraction is not null || _overlay is not null || _pendingAttachments.Count > 0)
        { ShowMessage(WorkspaceNavigationResources.Text(_locale, "busy")); return true; }
        if (destination.Length == 0)
        { ShowMessage(Environment.CurrentDirectory); return true; }
        try
        {
            var path = WorkspaceNavigationCommand.Resolve(destination, Environment.CurrentDirectory);
            if (!Directory.Exists(path))
            { ShowMessage(WorkspaceNavigationResources.Text(_locale, "missing")); return true; }
            if (Path.TrimEndingDirectorySeparator(path).Equals(Path.TrimEndingDirectorySeparator(Environment.CurrentDirectory),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            { _composer!.Text = ""; ShowMessage(path); return true; }
            RequestedWorkspace = path;
            _composer!.Text = "";
            _application?.RequestStop();
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { ShowMessage(WorkspaceNavigationResources.Text(_locale, "missing")); }
        return true;
    }
}

internal static class WorkspaceNavigationCommand
{
    internal static bool TryParse(string command, out string destination)
    {
        destination = "";
        var boundary = command.IndexOfAny([' ', '\t']);
        var head = boundary < 0 ? command : command[..boundary];
        if (!head.Equals("cd", StringComparison.OrdinalIgnoreCase)
            && !head.Equals("chdir", StringComparison.OrdinalIgnoreCase)) return false;
        destination = boundary < 0 ? "" : command[(boundary + 1)..].Trim();
        if (OperatingSystem.IsWindows() && destination.StartsWith("/d ", StringComparison.OrdinalIgnoreCase))
            destination = destination[3..].TrimStart();
        if (destination.Length >= 2 && destination[0] == destination[^1] && destination[0] is '"' or '\'')
            destination = destination[1..^1];
        return true;
    }

    internal static string Resolve(string destination, string currentDirectory)
    {
        if (destination == "~") destination = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else if (destination.StartsWith("~/", StringComparison.Ordinal) || destination.StartsWith("~\\", StringComparison.Ordinal))
            destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), destination[2..]);
        return Path.GetFullPath(destination, currentDirectory);
    }
}
