using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Protocol;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Fact]
    public void Companion_hide_button_restores_the_panel_and_detail_hide_returns_to_chat() => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: 120, initialRows: 40);
        if (!fx.Read(() => fx.App.SidebarOpen)) fx.InjectKey(new Key(KeyCode.F2));
        fx.Wait(() => fx.App.Sidebar!.Visible && fx.App.SidebarContent!.Frame.Height > 0, "normal panel laid out");
        var normalHeight = fx.Read(() => fx.App.SidebarContent!.Frame.Height);
        var composer = fx.Read(() => fx.App.Composer!.Frame);
        var conversation = fx.Read(() => fx.App.Conversation!.Text);
        var scroll = fx.Read(() => fx.App.Conversation!.Viewport.Location);
        fx.Invoke(() => CompanionPetPresentation.Execute(fx.Server, ""));
        fx.Wait(() => fx.App.CompanionPet?.Discovered == true && fx.App.SidebarContent!.Frame.Height < normalHeight,
            "companion reserves its compact panel section");
        var hide = fx.Read(() => Descendants(fx.App.Sidebar!).OfType<Button>().Single(view => view.Id == "pixel-widget-hide"));
        Assert.Equal("Ocultar", fx.Read(() => hide.Text));
        fx.Invoke(() => hide.SetFocus()); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.CompanionPet?.Preferences.Visible == false && fx.App.SidebarContent!.Frame.Height == normalHeight,
            "hide returns all reserved panel space");
        Assert.False(fx.Read(() => Descendants(fx.App.Sidebar!).Single(view => view.Id == "companion-pet").Visible));
        Assert.True(fx.Read(() => fx.App.Composer!.HasFocus));
        Assert.Equal(composer, fx.Read(() => fx.App.Composer!.Frame));
        Assert.Equal(conversation, fx.Read(() => fx.App.Conversation!.Text));
        Assert.Equal(scroll, fx.Read(() => fx.App.Conversation!.Viewport.Location));
        Assert.False(new CompanionPetService(Path.Combine(fx.Root, "host-data")).Status("terminal").Preferences.Visible);
        var stopped = fx.Read(() => fx.App.CompanionFrame);
        fx.Invoke(() => { for (var i = 0; i < 12; i++) fx.App.AnimateActivity(); });
        Assert.Equal(stopped, fx.Read(() => fx.App.CompanionFrame));
        ExportPreferenceFrame(fx, 120, 40, "companion-hidden", "SESIÓN");

        // The same clear control is available when opening details from preferences or /pet.
        fx.Invoke(() => CompanionPetPresentation.Execute(fx.Server, "show"));
        fx.Wait(() => fx.App.CompanionPet?.Preferences.Visible == true, "show restores companion");
        Type(fx, "/pet"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "companion-detail", "detail opened");
        Assert.Equal("Ocultar mascota", fx.Read(() => Descendants(fx.App.Overlay!).Single(view => view.Id == "companion-visible").Text));
        ClickId(fx, "companion-visible");
        fx.Wait(() => fx.App.Overlay is null && fx.App.CompanionPet?.Preferences.Visible == false
            && fx.App.SidebarContent!.Frame.Height == normalHeight, "detail hide closes the overlay and restores normal panel");
        Assert.Equal(conversation, fx.Read(() => fx.App.Conversation!.Text));
    }, noColor: false);

    [Theory]
    [InlineData("ox", 0)]
    [InlineData("fox", 1)]
    [InlineData("owl", 2)]
    [InlineData("beaver", 3)]
    [InlineData("cat", 4)]
    [InlineData("capybara", 5)]
    public void Companion_species_render_through_real_service_and_driver_with_keyboard_and_local_frames(string species, int index) => RunTuiTest(fx =>
    {
        var data = Path.Combine(fx.Root, "host-data");
        var service = new CompanionPetService(data, new(1), () => (index + .2) / 6,
            () => DateTimeOffset.UtcNow.AddSeconds(-2));
        service.Act(new("discover"), "discover");
        var stream = new EventStream(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!);
        stream.Append(new UserInputReceived(fx.Server.LastRunId()!, "[\"Revisa los archivos de este proyecto\"]", null));
        service.Synchronize(fx.Server.AcquireStore(), fx.Server.AcquireCodecs(), fx.Server.LastSessionId()!);
        Assert.Equal(species, service.Status().Species);
        fx.StartTui(initialColumns: 120, initialRows: 40);
        fx.Wait(() => fx.App.CompanionPet?.Species == species, "durable companion reaches production TUI");
        if (!fx.Read(() => fx.App.SidebarOpen)) fx.InjectKey(new Key(KeyCode.F2));
        fx.Wait(() => fx.App.Sidebar!.Visible, "existing sidebar opened");
        fx.Wait(() => Descendants(fx.App.Sidebar!).Any(v => v.Id == "companion-pet" && v.Frame.Height == 12), "full companion region laid out");
        var marker = CompanionPetPresentation.Word(species);
        ExportPreferenceFrame(fx, 120, 40, "companion-" + species + "-sidebar", marker);
        var conversation = fx.Read(() => fx.App.Conversation!.Text);
        Type(fx, "/pet"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "companion-detail", "pet detail opens without inference");
        fx.Wait(() => Descendants(fx.App.Overlay!).Any(v => v.Id == "companion-detail-renderer"), "native pixel renderer mounted");
        ExportPreferenceFrame(fx, 120, 40, "companion-" + species + "-idle", marker);
        var petButton = fx.Read(() => Descendants(fx.App.Overlay!).OfType<Button>().First(button => button.Text.ToString()!.Contains("Acariciar", StringComparison.Ordinal)));
        Assert.True(fx.Read(() => petButton.Viewport.Height >= 1), "Native button must have a drawable content row.");
        fx.Invoke(() => petButton.SetFocus()); fx.InjectKey(new Key(KeyCode.Enter));
        fx.Wait(() => fx.App.CompanionPet?.Reaction?.AnimationId == "pet", "Enter pets through Core");
        Assert.Equal(0, fx.App.CompanionPet!.Xp);
        ExportPreferenceFrame(fx, 120, 40, "companion-" + species + "-pet", marker);
        ClickId(fx, "companion-motion");
        fx.Wait(() => fx.App.CompanionPet?.Preferences.ReducedMotion == true, "reduced motion persisted for terminal only");
        var frame = fx.Read(() => fx.App.CompanionFrame);
        fx.Invoke(() => { for (var i = 0; i < 12; i++) fx.App.AnimateActivity(); });
        Assert.Equal(frame, fx.Read(() => fx.App.CompanionFrame));
        Assert.Equal(conversation, fx.Read(() => fx.App.Conversation!.Text));
        Assert.DoesNotContain(fx.Server.AcquireStore().ReadFrom(fx.Server.LastSessionId()!, 1)
            .Select(fx.Server.AcquireCodecs().Decode), e => e is ModelStepStarted);
        ClickId(fx, "companion-close");
        fx.Invoke(() => fx.Application.Driver!.SetScreenSize(80, 25));
        fx.Wait(() => fx.App.MainWindow!.Frame.Width == 80, "terminal resized");
        Type(fx, "/pet hide"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.CompanionPet?.Preferences.Visible == false, "hide works at compact width");
        var hiddenFrame = fx.Read(() => fx.App.CompanionFrame);
        fx.Invoke(() => { for (var i = 0; i < 12; i++) fx.App.AnimateActivity(); });
        Assert.Equal(hiddenFrame, fx.Read(() => fx.App.CompanionFrame));
    }, noColor: false);

    [Fact]
    public void Companion_is_hidden_before_discovery_and_typed_pet_unlocks_persisted_egg() => RunTuiTest(fx =>
    {
        fx.StartTui(initialColumns: 100, initialRows: 30);
        Assert.False(fx.App.CompanionPet?.Discovered ?? false);
        Assert.DoesNotContain(fx.App.SidebarComposition?.Navigation ?? [], widget => widget.Id == "core.companion");
        Type(fx, "/pet"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "companion-detail", "secret command discovers egg");
        Assert.True(fx.App.CompanionPet!.Discovered); Assert.Null(fx.App.CompanionPet.Species);
        Assert.Equal("egg", fx.App.CompanionPet.Stage);
        Assert.True(new CompanionPetService(Path.Combine(fx.Root, "host-data")).Status().Discovered);
        fx.Wait(() => fx.App.Overlay!.Frame.Width < 50 && fx.App.Overlay.Frame.Height <= 22,
            "pet card fits its contents instead of spanning the terminal");
        AssertPetControlsFit();
        ExportPreferenceFrame(fx, 100, 30, "companion-egg", "Huevo");
        fx.Invoke(() =>
        {
            var pixel = Descendants(fx.App.Overlay!).OfType<Label>()
                .First(view => view.Frame.Width == 1 && !string.IsNullOrWhiteSpace(view.Text));
            fx.Application.Mouse.RaiseMouseEvent(new Mouse { ScreenPosition = pixel.ViewportToScreen(System.Drawing.Point.Empty),
                Flags = MouseFlags.LeftButtonClicked });
        });
        fx.Wait(() => fx.App.CompanionPet?.Reaction?.AnimationId == "pet", "native mouse pets without a model");
        ExportPreferenceFrame(fx, 100, 30, "companion-egg-pet", "Caricia");
        fx.Invoke(() => fx.Application.Driver!.SetScreenSize(80, 20));
        fx.Wait(() => fx.App.MainWindow!.Frame.Height == 20 && fx.App.Overlay!.Frame.Height < 18,
            "open card adapts to a shorter terminal");
        AssertPetControlsFit();
        ExportPreferenceFrame(fx, 80, 20, "companion-compact", "Huevo");
        fx.Invoke(() => fx.Application.Driver!.SetScreenSize(100, 30));
        fx.Wait(() => fx.App.MainWindow!.Frame.Height == 30 && fx.App.Overlay!.Frame.Height == 22,
            "card restores its picture layout when the terminal grows");
        AssertPetControlsFit();
        ClickId(fx, "companion-close");
        KeyWithEffect(fx, KeyCode.F4, () => fx.App.Overlay is not null, "preferences opened");
        Assert.Contains(Descendants(fx.App.Overlay!), view => view.Id == "preferences-companion");
        fx.InjectKey(new Key(KeyCode.Esc)); fx.Wait(() => fx.App.Overlay is null, "close preferences");
        fx.Invoke(() => fx.Application.Driver!.SetScreenSize(80, 12));
        fx.Wait(() => fx.App.MainWindow!.Frame.Height == 12, "small terminal laid out");
        Type(fx, "/pet status"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay is not null, "text detail at very small height");
        Assert.Contains("Huevo", fx.Read(() => OverlayText(fx.App.Overlay!)));
        Assert.DoesNotContain(Descendants(fx.App.Overlay!), view => view.Id == "companion-name");

        void AssertPetControlsFit() => fx.Invoke(() =>
        {
            var panel = fx.App.Overlay!;
            Assert.All(panel.SubViews.Where(view => view.Visible), view =>
                Assert.True(view.Frame.X >= 0 && view.Frame.Y >= 0 && view.Frame.Right <= panel.Viewport.Width
                    && view.Frame.Bottom <= panel.Viewport.Height, view.Id + " fits in the pet card"));
            var buttons = panel.SubViews.OfType<Button>().ToArray();
            Assert.All(buttons, button => Assert.True(button.Viewport.Height >= 1, button.Id + " can be drawn and clicked"));
            for (var i = 0; i < buttons.Length; i++)
                for (var j = i + 1; j < buttons.Length; j++)
                    Assert.False(buttons[i].Frame.IntersectsWith(buttons[j].Frame), "pet controls do not overlap");
        });
    }, noColor: false);
}
