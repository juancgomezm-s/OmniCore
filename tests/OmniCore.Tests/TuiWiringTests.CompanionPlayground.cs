using System.Text.Json;
using System.Drawing;
using OmniCore.Cli;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Terminal.Gui.ViewBase;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Theory]
    [InlineData("fox", 1, false)]
    [InlineData("egg", 0, false)]
    [InlineData("cat", 4, true)]
    public void Companion_playground_overlays_messages_preserves_chat_and_hides_without_sidebar(string species, int index, bool noColor) => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, species, index);
        fx.StartTui(initialColumns: 120, initialRows: 40);
        fx.Wait(() => fx.App.CompanionPet?.Discovered == true, "companion connected");
        if (fx.Read(() => fx.App.SidebarOpen)) fx.InjectKey(new Key(KeyCode.F2));
        fx.Wait(() => !fx.App.Sidebar!.Visible, "sidebar closed");
        var chatFrame = fx.Read(() => fx.App.Conversation!.SuperView!.Frame);
        fx.Invoke(() => { for (var i = 0; i < 84; i++) fx.App.AnimateActivity(); });
        fx.Wait(() => fx.App.CompanionRunner?.Visible == true, "automatic outing works without sidebar");
        Type(fx, "Un borrador que debe permanecer intacto");
        fx.Invoke(() =>
        {
            fx.App.Conversation!.SetFocus();
            fx.App.Conversation.MoveHome();
        });
        fx.InjectKey(new Key(KeyCode.PageUp));
        fx.Invoke(() => { fx.App.Conversation!.SelectAll(); fx.App.Composer!.SetFocus(); });
        var selection = fx.Read(() => fx.App.Conversation!.SelectedText);
        var text = fx.Read(() => fx.App.Conversation!.Text);
        var scroll = fx.Read(() => fx.App.Conversation!.Viewport.Location);
        var composer = fx.Read(() => fx.App.Composer!.Frame);
        var caret = fx.Read(() => fx.App.Composer!.InsertionPoint);
        var sequence = fx.Server.AcquireStore().CurrentSequence(fx.Server.LastSessionId()!);
        var firstX = fx.Read(() => fx.App.CompanionRunner!.Frame.X);
        fx.Invoke(() => { for (var i = 0; i < 48; i++) fx.App.AnimateActivity(); });
        fx.Wait(() => fx.App.CompanionRunner!.Frame.X != firstX, "runner moves over the existing conversation");
        fx.Read(() =>
        {
            var runner = fx.App.CompanionRunner!;
            Assert.False(runner.CanFocus); Assert.False(runner.Enabled);
            Assert.True(runner.ViewportSettings.HasFlag(ViewportSettingsFlags.Transparent));
            Assert.True(runner.ViewportSettings.HasFlag(ViewportSettingsFlags.TransparentMouse));
            var chat = fx.App.Conversation!;
            var chatBounds = new Rectangle(chat.ViewportToScreen(Point.Empty), chat.Viewport.Size);
            Assert.True(chatBounds.Contains(new Rectangle(runner.ViewportToScreen(Point.Empty), runner.Viewport.Size)));
            Assert.Equal(chatFrame, chat.SuperView!.Frame);
            Assert.Equal(text, chat.Text); Assert.Equal(selection, chat.SelectedText);
            Assert.Equal(scroll, chat.Viewport.Location);
            Assert.True(fx.App.Composer!.HasFocus); Assert.Equal(composer, fx.App.Composer.Frame);
            Assert.Equal(caret, fx.App.Composer.InsertionPoint);
        });
        fx.InjectKey(new Key('!'));
        fx.Wait(() => fx.App.Composer!.Text == "Un borrador que debe permanecer intacto!", "raw typing still reaches composer");
        var clipboard = new FakeClipboard(false, false);
        fx.Invoke(() => { fx.Application.Driver!.Clipboard = clipboard; fx.App.Conversation!.InvokeCommand(Command.Copy); });
        Assert.Equal(selection, clipboard.GetClipboardData());
        Assert.Equal(sequence, fx.Server.AcquireStore().CurrentSequence(fx.Server.LastSessionId()!));
        if (!noColor) ExportPreferenceFrame(fx, 120, 40, "companion-" + species + "-roaming", "Ocultar mascota");
        fx.Invoke(() =>
        {
            var hide = fx.App.MainWindow!.SubViews.OfType<Button>().Single(v => v.Id == "companion-roam-hide");
            fx.Application.Mouse.RaiseMouseEvent(new Mouse { ScreenPosition = hide.ViewportToScreen(System.Drawing.Point.Empty),
                Flags = MouseFlags.LeftButtonClicked });
        });
        fx.Wait(() => fx.App.CompanionPet?.Preferences.Visible == false && fx.App.CompanionRunner?.Visible == false,
            "visible hide button works with sidebar closed");
        fx.Read(() =>
        {
            Assert.Equal(2, fx.App.Conversation!.SuperView!.Frame.Y);
            Assert.Equal("Un borrador que debe permanecer intacto!", fx.App.Composer!.Text);
            Assert.True(fx.App.Composer.HasFocus); Assert.Equal(selection, fx.App.Conversation.SelectedText);
        });
        var stopped = fx.Read(() => fx.App.CompanionPlayFrame);
        fx.Invoke(() => { for (var i = 0; i < 40; i++) fx.App.AnimateActivity(); });
        Assert.Equal(stopped, fx.Read(() => fx.App.CompanionPlayFrame));
        Assert.False(new CompanionPetService(Path.Combine(fx.Root, "host-data")).Status("terminal").Preferences.Visible);
        Assert.DoesNotContain(fx.Server.AcquireStore().ReadFrom(fx.Server.LastSessionId()!, 1).Select(fx.Server.AcquireCodecs().Decode),
            e => e is ModelStepStarted);
    }, noColor);

    [Fact]
    public void Companion_playground_buttons_pause_for_menus_motion_and_small_terminals() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1);
        fx.StartTui(initialColumns: 120, initialRows: 40);
        fx.Wait(() => fx.App.CompanionPet?.Discovered == true, "companion connected");
        if (fx.Read(() => fx.App.SidebarOpen)) fx.InjectKey(new Key(KeyCode.F2));
        Type(fx, "/pet"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "companion-detail", "detail opens");
        Assert.Equal("Salir a jugar", fx.Read(() => fx.App.Overlay!.SubViews.Single(v => v.Id == "companion-play").Text));
        ClickId(fx, "companion-play");
        fx.Wait(() => fx.App.Overlay is null && fx.App.CompanionRunner?.Visible == true, "play button sends companion out");
        Type(fx, "/pet"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "companion-detail", "detail pauses outing");
        fx.Wait(() => fx.App.CompanionRunner?.Visible == false, "runner stays behind menus");
        var paused = fx.Read(() => fx.App.CompanionPlayFrame);
        fx.Invoke(() => { for (var i = 0; i < 40; i++) fx.App.AnimateActivity(); });
        Assert.Equal(paused, fx.Read(() => fx.App.CompanionPlayFrame));
        Assert.Equal("Volver al panel", fx.Read(() => fx.App.Overlay!.SubViews.Single(v => v.Id == "companion-play").Text));
        ClickId(fx, "companion-play");
        fx.Wait(() => fx.App.Overlay is null && fx.App.Conversation!.SuperView!.Frame.Y == 2, "home restores normal chat geometry");
        fx.Invoke(() => { for (var i = 0; i < 140; i++) fx.App.AnimateActivity(); });
        Assert.False(fx.Read(() => fx.App.CompanionRunner!.Visible));
        Type(fx, "/pet play"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.CompanionRunner?.Visible == true, "local play command");
        fx.Invoke(() => CompanionPetPresentation.Execute(fx.Server, "reduced"));
        fx.Wait(() => fx.App.CompanionPet?.Preferences.ReducedMotion == true && fx.App.CompanionRunner?.Visible == false,
            "reduced motion stops outings");
        paused = fx.Read(() => fx.App.CompanionPlayFrame);
        fx.Invoke(() => { for (var i = 0; i < 40; i++) fx.App.AnimateActivity(); });
        Assert.Equal(paused, fx.Read(() => fx.App.CompanionPlayFrame));
        fx.Invoke(() => CompanionPetPresentation.Execute(fx.Server, "motion"));
        fx.Wait(() => fx.App.CompanionRunner?.Visible == true, "motion resumes");
        fx.Invoke(() => fx.Application.Driver!.SetScreenSize(80, 12));
        fx.Wait(() => fx.App.MainWindow!.Frame.Height == 12 && fx.App.CompanionRunner?.Visible == false,
            "small terminal keeps text-only companion");
        Assert.Equal(2, fx.Read(() => fx.App.Conversation!.SuperView!.Frame.Y));
        fx.Invoke(() => fx.Application.Driver!.SetScreenSize(80, 25));
        fx.Wait(() => fx.App.CompanionRunner?.Visible == true, "runner stays within resized conversation width");
        Assert.True(fx.Read(() => fx.App.CompanionRunner!.Frame.Right < 80));
        Type(fx, "/pet home"); KeyWithEffect(fx, KeyCode.Enter, () => !fx.App.CompanionRunner!.Visible, "local home command");
    }, noColor: false);

    [Fact]
    public void Companion_playground_opaque_sprite_restores_all_text_without_resizing_chat() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1);
        fx.StartTui(initialColumns: 120, initialRows: 40);
        fx.Wait(() => fx.App.CompanionPet?.Discovered == true, "companion connected");
        if (fx.Read(() => fx.App.SidebarOpen)) fx.InjectKey(new Key(KeyCode.F2));
        Type(fx, "/pet home"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.CompanionPlayFrame == 0, "home for baseline");
        var baseline = CapturePetOverlay(fx, false, () => fx.App.Conversation!.SetNeedsDraw());
        Type(fx, "/pet play"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.CompanionRunner?.Visible == true, "start overlay");
        var overText = CapturePetOverlay(fx, true, () => { for (var i = 0; i < 48; i++) fx.App.AnimateActivity(); });
        AssertOverlayCells(baseline, overText);
        Assert.Contains(overText.Painted, point => !string.IsNullOrWhiteSpace(baseline.Cells[point.Y, point.X].Glyph));
        Assert.Contains(overText.Painted, point => !string.IsNullOrWhiteSpace(baseline.Cells[point.Y, point.X].Glyph)
            && baseline.Cells[point.Y, point.X].Glyph != overText.Cells[point.Y, point.X].Glyph);
        var moved = CapturePetOverlay(fx, true, () => { for (var i = 0; i < 12; i++) fx.App.AnimateActivity(); });
        AssertOverlayCells(baseline, moved);
        Assert.NotEqual(overText.Pet, moved.Pet);
        var restored = CapturePetOverlay(fx, false, () =>
        {
            var hide = fx.App.MainWindow!.SubViews.Single(view => view.Id == "companion-roam-hide");
            fx.Application.Mouse.RaiseMouseEvent(new Mouse { ScreenPosition = hide.ViewportToScreen(Point.Empty),
                Flags = MouseFlags.LeftButtonClicked });
        });
        Assert.Equal(baseline.Chat, restored.Chat);
        AssertOverlayCells(baseline, restored);
    }, noColor: false);

    [Fact]
    public void Companion_playground_passes_clicks_and_scroll_to_the_link_under_a_painted_pixel() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1);
        fx.StartTui(initialColumns: 120, initialRows: 40);
        fx.Wait(() => fx.App.CompanionPet?.Discovered == true, "companion connected");
        if (fx.Read(() => fx.App.SidebarOpen)) fx.InjectKey(new Key(KeyCode.F2));
        Type(fx, "/pet play"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.CompanionRunner?.Visible == true, "start overlay");
        var links = new RecordingWebLinkHost();
        fx.Invoke(() =>
        {
            fx.App.LinkHost = links;
            fx.App.Conversation!.WordWrap = false;
            fx.App.Conversation.LoadStyled(Enumerable.Range(0, 60).Select(_ => (IReadOnlyList<ConversationSpan>)
                new[] { new ConversationSpan(new string('L', 140), ConversationStyle.Heading, LinkTarget: "https://example.com/under-pet") })
                .ToArray(), 140, false);
            fx.App.Conversation.MoveHome(); fx.App.Composer!.SetFocus();
        });
        var frame = CapturePetOverlay(fx, true, () => { for (var i = 0; i < 4; i++) fx.App.AnimateActivity(); });
        var point = frame.Painted.First();
        fx.Invoke(() => fx.Application.Mouse.RaiseMouseEvent(new Mouse { ScreenPosition = point,
            Flags = MouseFlags.LeftButtonClicked }));
        fx.Wait(() => links.Opened.Count == 1, "painted pet pixel forwards click to the real link");
        Assert.Equal("https://example.com/under-pet", Assert.Single(links.Opened));
        var scroll = fx.Read(() => fx.App.Conversation!.Viewport.Y);
        fx.Invoke(() => fx.Application.Mouse.RaiseMouseEvent(new Mouse { ScreenPosition = point,
            Flags = MouseFlags.WheeledDown }));
        fx.Wait(() => fx.App.Conversation!.Viewport.Y > scroll, "wheel over the pet scrolls conversation");
    }, noColor: false);

    [Fact]
    public void Companion_playground_more_mischief_runs_between_lines_and_restores_every_occupied_cell() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1);
        fx.StartTui(initialColumns: 120, initialRows: 40);
        fx.Wait(() => fx.App.CompanionPet?.Discovered == true, "companion connected");
        if (fx.Read(() => fx.App.SidebarOpen)) fx.InjectKey(new Key(KeyCode.F2));
        Type(fx, "/pet home"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.CompanionPlayFrame == 0, "home for baseline");
        var baseline = CapturePetOverlay(fx, false, () => fx.App.Conversation!.SetNeedsDraw());
        Type(fx, "/pet play"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.CompanionRunner?.Visible == true, "start mischief");
        var positions = new List<Rectangle>();
        foreach (var (phase, animation) in new[] { (3, "run"), (18, "peek"), (28, "weave"), (37, "hop"),
            (46, "roll"), (54, "pounce"), (60, "dance"), (67, "dash"), (76, "hide") })
        {
            var frame = CapturePetOverlay(fx, true, () =>
            {
                while (fx.App.CompanionPlayFrame < 20 + phase)
                    for (var tick = 0; tick < 4; tick++) fx.App.AnimateActivity();
                fx.App.CompanionRunner!.SetNeedsDraw();
            });
            AssertOverlayCells(baseline, frame); positions.Add(frame.Pet);
            if (animation is "weave" or "roll" or "dance" or "hide")
                ExportPreferenceFrame(fx, 120, 40, "companion-fox-" + animation, "Ocultar mascota");
        }
        Assert.True(positions.Select(position => position.Y).Distinct().Count() >= 4, "plays on multiple text rows");
        Assert.True(positions.Select(position => position.X).Distinct().Count() >= 5, "short dashes and reversals across the text");
        var restored = CapturePetOverlay(fx, false, () =>
        {
            var hide = fx.App.MainWindow!.SubViews.Single(view => view.Id == "companion-roam-hide");
            fx.Application.Mouse.RaiseMouseEvent(new Mouse { ScreenPosition = hide.ViewportToScreen(Point.Empty),
                Flags = MouseFlags.LeftButtonClicked });
        });
        AssertOverlayCells(baseline, restored);
        Assert.True(fx.Read(() => fx.App.Composer!.HasFocus));
        Assert.Equal(baseline.Chat, restored.Chat);
    }, noColor: false);

    [Fact]
    public void Companion_playground_stays_near_short_text_instead_of_wandering_through_empty_rows() => RunTuiTest(fx =>
    {
        new CompanionPetService(Path.Combine(fx.Root, "host-data")).Act(new("discover"), "discover");
        fx.StartTui(initialColumns: 120, initialRows: 40);
        fx.Wait(() => fx.App.CompanionPet?.Discovered == true, "egg connected to short conversation");
        if (fx.Read(() => fx.App.SidebarOpen)) fx.InjectKey(new Key(KeyCode.F2));
        Type(fx, "/pet play"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.CompanionRunner?.Visible == true, "play near short text");
        foreach (var phase in new[] { 3, 28, 54, 60, 76 })
        {
            var frame = CapturePetOverlay(fx, true, () =>
            {
                while (fx.App.CompanionPlayFrame < 20 + phase)
                    for (var tick = 0; tick < 4; tick++) fx.App.AnimateActivity();
                fx.App.CompanionRunner!.SetNeedsDraw();
            });
            var textRows = fx.Read(() => Math.Max(CompanionPetPlayground.SpriteHeight,
                fx.App.Conversation!.Lines - fx.App.Conversation.Viewport.Y));
            Assert.True(textRows < frame.Chat.Height, "fixture has empty rows below the short conversation");
            Assert.InRange(frame.Pet.Top, frame.Chat.Top, frame.Chat.Top + textRows - CompanionPetPlayground.SpriteHeight);
            Assert.True(frame.Pet.Bottom <= frame.Chat.Top + textRows, "mischief remains among text rows");
        }
    }, noColor: false);

    private sealed record PetOverlayFrame(
        (string Glyph, Terminal.Gui.Drawing.Attribute? Attribute)[,] Cells, Rectangle Chat, Rectangle Pet, HashSet<Point> Painted);

    private static PetOverlayFrame CapturePetOverlay(TuiFixture fx, bool outside, Action mutate)
    {
        using var captured = new ManualResetEventSlim();
        PetOverlayFrame? result = null;
        var armed = false;
        EventHandler<EventArgs> capture = (_, _) =>
        {
            if (!armed || captured.IsSet || (fx.App.CompanionRunner?.Visible == true) != outside) return;
            var contents = fx.Application.Driver!.Contents!;
            var copy = new (string, Terminal.Gui.Drawing.Attribute?)[contents.GetLength(0), contents.GetLength(1)];
            for (var y = 0; y < copy.GetLength(0); y++)
            for (var x = 0; x < copy.GetLength(1); x++) copy[y, x] = (contents[y, x].Grapheme, contents[y, x].Attribute);
            var chat = fx.App.Conversation!;
            var rect = new Rectangle(chat.ViewportToScreen(Point.Empty), new Size(chat.Viewport.Width - 1, chat.Viewport.Height));
            var runner = (CompanionRunnerView?)fx.App.CompanionRunner;
            var pet = runner is null ? Rectangle.Empty : new Rectangle(runner.ViewportToScreen(Point.Empty), runner.Viewport.Size);
            var painted = outside ? runner!.PaintedOffsets.Select(point => new Point(pet.X + point.X, pet.Y + point.Y)).ToHashSet() : [];
            result = new(copy, rect, pet, painted); captured.Set();
        };
        fx.Application.LayoutAndDrawComplete += capture;
        try { fx.Invoke(() => { mutate(); armed = true; }); Assert.True(captured.Wait(TimeSpan.FromSeconds(10)), "fresh overlay frame drawn"); }
        finally { fx.Application.LayoutAndDrawComplete -= capture; }
        return result!;
    }

    private static void AssertOverlayCells(PetOverlayFrame baseline, PetOverlayFrame frame)
    {
        Assert.Equal(baseline.Chat, frame.Chat);
        for (var y = frame.Chat.Top; y < frame.Chat.Bottom; y++)
        for (var x = frame.Chat.Left; x < frame.Chat.Right; x++)
            if (!frame.Painted.Contains(new Point(x, y)))
                Assert.True(baseline.Cells[y, x] == frame.Cells[y, x], $"underlying cell {x},{y} survives overlay/movement/hide");
        Assert.All(frame.Painted, point => Assert.True(frame.Chat.Contains(point)));
    }

    private static void SeedPlaygroundPet(TuiFixture fx, string species, int index)
    {
        var service = new CompanionPetService(Path.Combine(fx.Root, "host-data"), new(species == "egg" ? 20 : 1),
            () => (index + .2) / 6, () => DateTimeOffset.UtcNow.AddSeconds(-2));
        service.Act(new("discover"), "discover");
        var stream = new EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!);
        stream.Append(new UserInputReceived(fx.Server.LastRunId()!, JsonSerializer.Serialize(string.Join("\n",
            Enumerable.Range(1, 70).Select(i => $"Mensaje de historial para leer y seleccionar, línea {i}."))), null));
        service.Synchronize(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!);
        Assert.Equal(species == "egg" ? null : species, service.Status().Species);
    }
}
