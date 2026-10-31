namespace OmniCore.Tests;

using OmniCore.Infrastructure;
using OmniCore.Host;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

public sealed partial class TuiWiringTests
{
    [Fact]
    public void Discovered_skill_is_visible_in_real_TUI_completion_and_command_catalog() => RunTuiTest(fx =>
    {
        var package = Path.Combine(fx.Workspace, ".omnicore", "skills", "fixture-invocation");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "skill.yaml"),
            "id: fixture-invocation\nversion: 1\nbudgetTokens: 128\nactivation:\n  explicit: true\n");
        File.WriteAllText(Path.Combine(package, "instructions.md"), "SKILL_PRIVATE_BODY_NOT_COMMAND_METADATA");
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "host-data"), Path.Combine(fx.Root, "host-config"));
        new WorkspaceTrustStore(paths).SetTrusted(fx.Workspace, true);
        fx.StartTui();
        Type(fx, "/skill-fixture");
        fx.Wait(() => fx.App.Completion!.Text?.ToString().Contains("/skill-fixture-invocation", StringComparison.Ordinal) == true,
            "real Host skill command reaches TUI autocomplete");
        Type(fx, "/comandos");
        KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is not null, "command catalog opens");
        var rows = Descendants(fx.App.Overlay!).OfType<ListView>().SelectMany(view =>
            view.Source?.ToList().Cast<object>().Select(item => item.ToString() ?? "") ?? []).ToArray();
        Assert.Contains(rows, row => row.Contains("fixture-invocation", StringComparison.Ordinal));
        Assert.DoesNotContain("SKILL_PRIVATE_BODY_NOT_COMMAND_METADATA", OverlayText(fx.App.Overlay!), StringComparison.Ordinal);
    });
}
