namespace OmniCore.Tests;

using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

public sealed partial class TuiWiringTests
{
    [Fact]
    public void Simulated_composer_cursor_preserves_the_character_under_the_caret() => RunTuiTest(fx =>
    {
        fx.StartTui();
        Type(fx, "abcd");
        fx.InjectKey(new Key(KeyCode.CursorLeft));
        fx.Wait(() => fx.App.Composer!.CurrentColumn == 3, "caret before d");
        fx.Wait(() => fx.Read(() =>
        {
            var at = fx.App.Composer!.Cursor.Position!.Value;
            return fx.Application.Driver!.Contents![at.Y, at.X].Grapheme == "d";
        }), "character remains visible in the block cursor");
        Assert.Equal("abcd", fx.App.Composer!.Text.ToString());
        Assert.Equal(CursorStyle.Hidden, fx.App.Composer.Cursor.Style);
    }, noColor: false);
}
