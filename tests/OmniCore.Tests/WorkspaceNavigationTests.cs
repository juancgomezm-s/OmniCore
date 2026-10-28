namespace OmniCore.Tests;

using OmniCore.Cli;
using OmniCore.Client;

using OmniCore.Protocol;

public sealed class WorkspaceNavigationTests
{
    [Theory]
    [InlineData("cd C:\\Users\\juanc\\source\\repos\\OmniCore", "C:\\Users\\juanc\\source\\repos\\OmniCore")]
    [InlineData("cd \"C:\\My projects\\OmniCore\"", "C:\\My projects\\OmniCore")]
    [InlineData("chdir 'a b'", "a b")]
    [InlineData("cd", "")]
    public void Human_directory_navigation_preserves_literal_paths(string command, string expected)
    {
        Assert.True(WorkspaceNavigationCommand.TryParse(command, out var path));
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("echo cd")]
    [InlineData("cdrom test")]
    public void Other_commands_keep_the_native_shell_path(string command) =>
        Assert.False(WorkspaceNavigationCommand.TryParse(command, out _));

    [Theory]
    [InlineData("modo", "mode")]
    [InlineData("permiso", "permission")]
    [InlineData("esfuerzo", "effort")]
    [InlineData("think", "think")]
    public void Requested_commands_have_reserved_human_aliases(string human, string canonical)
    {
        Assert.Equal(canonical, HumanCommandAliases.CanonicalName(human));
        Assert.True(HumanCommandAliases.IsReserved(human));
    }

    [Theory]
    [InlineData("F1", "mode.open")]
    [InlineData("Ctrl+F1", "permissions.open")]
    public void Mode_and_permission_shortcuts_resolve_without_colliding(string keys, string target)
    {
        Assert.True(KeyChord.TryParseSequence(keys, out var sequence));
        var service = new KeyBindingService();
        var result = service.Resolve(sequence, new KeyBindingContext(new Dictionary<string, bool>()));
        Assert.Equal(KeyBindingResolutionStatus.Matched, result.Status);
        Assert.Equal(target, result.Binding!.Definition.Target.Id);
        var approval = service.Resolve(sequence, new KeyBindingContext(new Dictionary<string, bool> { ["awaitingPermission"] = true }));
        Assert.Equal(KeyBindingResolutionStatus.None, approval.Status);
    }
}
