using System.Drawing;
using OmniCore.Cli;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Companion_playground_chases_typed_letters_without_editing_draft_caret_focus_or_journal(bool noColor) => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1);
        fx.StartTui(initialColumns: 120, initialRows: 40);
        fx.Wait(() => fx.App.CompanionPet?.Discovered == true, "pet connected");
        if (fx.Read(() => fx.App.SidebarOpen)) fx.InjectKey(new Key(KeyCode.F2));
        Type(fx, "/pet home"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.CompanionPlayFrame == 0, "home for baseline");
        var baseline = CapturePetOverlay(fx, false, () => fx.App.Conversation!.SetNeedsDraw());
        Type(fx, "/pet play"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.CompanionRunner?.Visible == true, "play");
        var sequence = fx.Server.AcquireStore().CurrentSequence(fx.Server.LastSessionId()!);
        var history = fx.Read(() => fx.App.Conversation!.Text);
        // Raw native keys: the helper must not repair focus between characters.
        foreach (var letter in "Persigue estas letras") fx.InjectKey(new Key(letter));
        fx.Wait(() => fx.App.CompanionTypingActive && fx.App.CompanionLetter?.Visible == true, "typing starts chase");
        var draft = fx.Read(() => fx.App.Composer!.Text);
        Assert.Equal("Persigue estas letras", draft);
        var caret = fx.Read(() => fx.App.Composer!.InsertionPoint);
        var firstX = fx.Read(() => fx.App.CompanionRunner!.Frame.X);
        fx.Invoke(() => { for (var i = 0; i < 16; i++) fx.App.AnimateActivity(); });
        fx.Wait(() => fx.App.CompanionRunner!.Frame.X != firstX, "pet pursues typing position");
        fx.Read(() =>
        {
            var composer = fx.App.Composer!;
            var area = new Rectangle(composer.ViewportToScreen(Point.Empty), composer.Viewport.Size);
            var runner = fx.App.CompanionRunner!;
            var letter = (CompanionLetterView)fx.App.CompanionLetter!;
            Assert.False(runner.CanFocus); Assert.False(runner.Enabled);
            Assert.False(letter.CanFocus); Assert.False(letter.Enabled); Assert.Equal("s", letter.Glyph);
            Assert.True(runner.ViewportToScreen(Point.Empty).Y + runner.Viewport.Height <= area.Top);
            Assert.True(letter.ViewportToScreen(Point.Empty).Y < area.Top);
            Assert.Equal(draft, composer.Text); Assert.Equal(caret, composer.InsertionPoint); Assert.True(composer.HasFocus);
            Assert.Equal(history, fx.App.Conversation!.Text);
        });
        if (!noColor) ExportPreferenceFrame(fx, 120, 40, "companion-fox-typing", "Persigue estas letras");
        fx.Invoke(() => { for (var i = 0; i < 48; i++) fx.App.AnimateActivity(); });
        fx.Wait(() => !fx.App.CompanionTypingActive && fx.App.CompanionLetter?.Visible == false, "chase expires without input");
        fx.InjectKey(new Key('!'));
        fx.Wait(() => fx.App.CompanionTypingActive, "new letter restarts chase");
        var restored = CapturePetOverlay(fx, false, () =>
        {
            var hide = fx.App.MainWindow!.SubViews.Single(view => view.Id == "companion-roam-hide");
            fx.Application.Mouse.RaiseMouseEvent(new Mouse { ScreenPosition = hide.ViewportToScreen(Point.Empty),
                Flags = MouseFlags.LeftButtonClicked });
        });
        AssertOverlayCells(baseline, restored);
        // Compare the border/gap too: the jumping letter must leave no stray glyphs behind.
        var composerTop = fx.Read(() => fx.App.Composer!.ViewportToScreen(Point.Empty).Y);
        for (var y = baseline.Chat.Bottom; y < composerTop; y++)
        for (var x = baseline.Chat.Left; x < baseline.Chat.Right; x++) Assert.Equal(baseline.Cells[y, x], restored.Cells[y, x]);
        fx.Wait(() => fx.App.CompanionPet?.Preferences.Visible == false && fx.App.CompanionLetter?.Visible == false, "hide clears all tricks");
        fx.Read(() =>
        {
            Assert.Equal(draft + "!", fx.App.Composer!.Text); Assert.True(fx.App.Composer.HasFocus);
            Assert.False(fx.App.CompanionTypingActive); Assert.Equal(history, fx.App.Conversation!.Text);
        });
        Assert.Equal(sequence, fx.Server.AcquireStore().CurrentSequence(fx.Server.LastSessionId()!));
    }, noColor);

    [Fact]
    public void Companion_playground_typing_handles_unicode_paste_wrap_resize_deletion_and_navigation() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1);
        fx.StartTui(initialColumns: 120, initialRows: 40);
        fx.Wait(() => fx.App.CompanionPet?.Discovered == true, "pet connected");
        if (fx.Read(() => fx.App.SidebarOpen)) fx.InjectKey(new Key(KeyCode.F2));
        Type(fx, "/pet play"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.CompanionRunner?.Visible == true, "play");
        var clipboard = new FakeClipboard(false, false);
        var pasted = new string('x', 125) + "\nCafé é 界🐾";
        var draft = pasted.Replace("\n", Environment.NewLine); // TextView exposes native line endings.
        fx.Invoke(() => { clipboard.SetClipboardData(pasted); fx.Application.Driver!.Clipboard = clipboard; });
        fx.InjectKey(new Key(KeyCode.V | KeyCode.CtrlMask));
        fx.Wait(() => fx.App.Composer!.Text.Contains("🐾"), "paste reaches native editor");
        Assert.Equal(draft, fx.Read(() => fx.App.Composer!.Text));
        fx.Invoke(() => { for (var i = 0; i < 4; i++) fx.App.AnimateActivity(); });
        Assert.True(fx.Read(() => fx.App.CompanionTypingActive));
        Assert.Equal("🐾", fx.Read(() => ((CompanionLetterView)fx.App.CompanionLetter!).Glyph));
        fx.Invoke(() => fx.Application.Driver!.SetScreenSize(80, 25));
        fx.Wait(() => fx.App.MainWindow!.Frame.Width == 80 && fx.App.CompanionLetter?.Visible == true, "resize while chasing");
        fx.Read(() =>
        {
            var runner = fx.App.CompanionRunner!;
            Assert.True(runner.ViewportToScreen(Point.Empty).X + runner.Viewport.Width < 80);
            Assert.True(runner.ViewportToScreen(Point.Empty).Y + runner.Viewport.Height <= fx.App.Composer!.ViewportToScreen(Point.Empty).Y);
            Assert.Equal(draft, fx.App.Composer.Text); Assert.True(fx.App.Composer.HasFocus);
        });
        fx.Invoke(() => { for (var i = 0; i < 48; i++) fx.App.AnimateActivity(); });
        fx.InjectKey(new Key(KeyCode.CursorLeft));
        var caret = fx.Read(() => fx.App.Composer!.InsertionPoint);
        fx.Invoke(() => { for (var i = 0; i < 4; i++) fx.App.AnimateActivity(); });
        Assert.False(fx.Read(() => fx.App.CompanionTypingActive));
        Assert.Equal(caret, fx.Read(() => fx.App.Composer!.InsertionPoint));
        fx.InjectKey(new Key('z'));
        fx.Wait(() => fx.App.CompanionTypingActive, "insertion inside draft starts chase");
        fx.InjectKey(new Key(KeyCode.Backspace));
        fx.Wait(() => fx.App.Composer!.Text == draft && !fx.App.CompanionTypingActive, "deletion does not fabricate a letter");
        Assert.Equal(pasted, clipboard.GetClipboardData());
        fx.Invoke(() => fx.App.Composer!.Text = "");
        Type(fx, "/pet home"); KeyWithEffect(fx, KeyCode.Enter, () => !fx.App.CompanionRunner!.Visible, "return home");
        fx.InjectKey(new Key('a'));
        fx.Invoke(() => { for (var i = 0; i < 8; i++) fx.App.AnimateActivity(); });
        Assert.False(fx.Read(() => fx.App.CompanionTypingActive));
        Assert.Equal("a", fx.Read(() => fx.App.Composer!.Text));
    }, noColor: false);
}
