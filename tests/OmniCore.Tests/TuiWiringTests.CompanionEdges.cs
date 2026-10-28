using System.Drawing;
using OmniCore.Cli;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData(120, 40, false, false)]
    [InlineData(120, 40, true, false)]
    [InlineData(80, 25, false, false)]
    [InlineData(80, 25, false, true)]
    public void Companion_edges_restore_text_and_leave_borders_untouched(int columns, int rows, bool sidebar, bool wrap) => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1);
        fx.StartTui(initialColumns: columns, initialRows: rows);
        fx.Wait(() => fx.App.CompanionPet?.Discovered == true, "pet connected");
        if (fx.Read(() => fx.App.SidebarOpen) != sidebar)
            KeyWithEffect(fx, KeyCode.F2, () => fx.App.SidebarOpen == sidebar, "sidebar layout");
        Type(fx, "/pet home"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.CompanionPlayFrame == 0, "baseline");
        fx.Invoke(() =>
        {
            fx.App.Conversation!.WordWrap = wrap;
            fx.App.Conversation.LoadStyled(Enumerable.Range(0, 70).Select(_ => (IReadOnlyList<ConversationSpan>)
                new[] { new ConversationSpan(string.Concat(Enumerable.Repeat("界🐾ab", 30)), ConversationStyle.Heading) }).ToArray(), 180, false);
            fx.App.Conversation.MoveHome(); fx.App.Composer!.SetFocus();
        });
        var baseline = CapturePetOverlay(fx, false, () => fx.App.Conversation!.SetNeedsDraw());
        Type(fx, "/pet play"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.CompanionRunner?.Visible == true, "outing");
        var origin = fx.Read(() => fx.App.MainWindow!.ViewportToScreen(Point.Empty));
        var width = fx.Read(() => fx.App.CompanionRunner!.Viewport.Width);
        var height = fx.Read(() => fx.App.CompanionRunner!.Viewport.Height);
        foreach (var position in new[]
        {
            new Point(baseline.Chat.Right - width, baseline.Chat.Top),
            new Point(baseline.Chat.Left, baseline.Chat.Top),
            new Point(baseline.Chat.Left, baseline.Chat.Bottom - height),
            new Point(baseline.Chat.Right - width, baseline.Chat.Bottom - height),
            new Point(baseline.Chat.Right - width + 2, baseline.Chat.Bottom - height + 1),
            new Point(baseline.Chat.Left - 2, baseline.Chat.Top - 1),
            new Point(baseline.Chat.Left + 8, baseline.Chat.Top + 2),
        })
        {
            var frame = CapturePetOverlay(fx, true, () =>
            {
                var runner = fx.App.CompanionRunner!;
                runner.X = position.X - origin.X; runner.Y = position.Y - origin.Y;
                runner.SetNeedsLayout(); runner.SetNeedsDraw();
            });
            frame = frame with { Painted = frame.Painted.Where(frame.Chat.Contains).ToHashSet() };
            var changes = (from y in Enumerable.Range(frame.Chat.Top, frame.Chat.Height)
                           from x in Enumerable.Range(frame.Chat.Left, frame.Chat.Width)
                           where !frame.Painted.Contains(new Point(x, y)) && baseline.Cells[y, x] != frame.Cells[y, x]
                           select $"{x},{y}: '{baseline.Cells[y, x].Glyph}' -> '{frame.Cells[y, x].Glyph}'").Take(12).ToArray();
            Assert.True(changes.Length == 0, $"position={position}; pet={frame.Pet}; " + string.Join("; ", changes));
            AssertOverlayCells(baseline, frame);
            // The reserved scrollbar column and the row above the composer are outside the drawing area.
            for (var y = baseline.Chat.Top; y < baseline.Chat.Bottom; y++)
                Assert.Equal(baseline.Cells[y, baseline.Chat.Right], frame.Cells[y, baseline.Chat.Right]);
            for (var x = baseline.Chat.Left; x <= baseline.Chat.Right; x++)
                Assert.Equal(baseline.Cells[baseline.Chat.Bottom, x], frame.Cells[baseline.Chat.Bottom, x]);
        }
        var restored = CapturePetOverlay(fx, false, () =>
        {
            fx.App.CompanionRunner!.Visible = false;
        });
        AssertOverlayCells(baseline, restored);
    }, noColor: false);

    [Fact]
    public void Companion_petting_interrupts_roaming_animates_and_then_resumes() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1);
        fx.StartTui(initialColumns: 120, initialRows: 40);
        if (!fx.Read(() => fx.App.SidebarOpen)) KeyWithEffect(fx, KeyCode.F2, () => fx.App.SidebarOpen, "sidebar");
        Type(fx, "/pet play"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.CompanionRunner?.Visible == true, "outing");
        var xp = fx.Read(() => fx.App.CompanionPet!.Xp);
        EmulatePetSixelDevice(fx);
        fx.Invoke(() =>
        {
            var action = fx.App.Sidebar!.SubViews.Single(view => view.Id == "companion-pet").SubViews.OfType<Button>()
                .Single(view => view.Id == "pixel-widget-action");
            fx.Application.Mouse.RaiseMouseEvent(new Terminal.Gui.Input.Mouse { ScreenPosition = action.ViewportToScreen(Point.Empty),
                Flags = Terminal.Gui.Input.MouseFlags.LeftButtonClicked });
        });
        fx.Wait(() => fx.App.CompanionPet?.Reaction?.AnimationId == "pet", "pet action accepted");
        fx.Wait(() => fx.App.CompanionRunner?.Visible == false, "petting takes precedence over roaming");
        fx.Read(() => Assert.Contains("Caricia", fx.App.Sidebar!.SubViews.Single(view => view.Id == "companion-pet").SubViews.First().Text));
        var firstEncoding = WaitForPetExpressionFrame(fx, "pet", null);
        var before = fx.Read(() => fx.App.CompanionFrame);
        fx.Invoke(() => { for (var tick = 0; tick < 6; tick++) fx.App.AnimateActivity(); });
        Assert.True(fx.Read(() => fx.App.CompanionFrame > before));
        Assert.NotEqual(firstEncoding, WaitForPetExpressionFrame(fx, "pet", firstEncoding));
        fx.Wait(() => fx.App.CompanionRunner?.Visible == true, "outing resumes after petting");
        Assert.Equal(xp, fx.Read(() => fx.App.CompanionPet!.Xp));
    }, noColor: false);
}
