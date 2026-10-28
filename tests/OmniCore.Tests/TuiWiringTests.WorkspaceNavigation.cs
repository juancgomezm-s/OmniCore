namespace OmniCore.Tests;

using Terminal.Gui.Input;
using Terminal.Gui.Drivers;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Directory_navigation_requests_a_real_workspace_without_model_or_shell(bool quoted) => RunTuiTest(fx =>
    {
        fx.StartTui();
        var target = Path.Combine(fx.Root, "another workspace");
        Directory.CreateDirectory(target);
        var command = "!cd " + (quoted ? "\"" + target + "\"" : target);
        Type(fx, command);
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.RequestedWorkspace == target, "workspace navigation request");
        Assert.Equal(target, fx.App.RequestedWorkspace);
    });

    [Fact]
    public void Missing_directory_preserves_the_navigation_draft() => RunTuiTest(fx =>
    {
        fx.StartTui();
        var command = "!cd " + Path.Combine(fx.Root, "missing");
        Type(fx, command);
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay is not null, "missing directory feedback");
        Assert.Null(fx.App.RequestedWorkspace);
        Assert.Equal(command, fx.App.Composer!.Text.ToString());
    });

    [Theory]
    [InlineData("/modo", "mode-preferences")]
    [InlineData("/permiso", "security-preferences")]
    [InlineData("/esfuerzo", "reasoning-preferences")]
    [InlineData("/think", "reasoning-visibility")]
    public void Direct_controls_open_from_the_real_composer(string command, string overlay) => RunTuiTest(fx =>
    {
        fx.StartTui();
        Type(fx, command);
        fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.Overlay?.Id == overlay, "direct control " + command);
    });

    [Theory]
    [InlineData(KeyCode.F1, "mode-preferences")]
    [InlineData(KeyCode.F1 | KeyCode.CtrlMask, "security-preferences")]
    public void Direct_control_function_keys_preserve_the_draft(KeyCode key, string overlay) => RunTuiTest(fx =>
    {
        fx.StartTui();
        Type(fx, "keep my draft");
        KeyWithEffect(fx, key, () => fx.App.Overlay?.Id == overlay, "direct function key");
        fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => fx.App.Overlay is null, "direct control escape");
        Assert.Equal("keep my draft", fx.App.Composer!.Text.ToString());
    });
}
