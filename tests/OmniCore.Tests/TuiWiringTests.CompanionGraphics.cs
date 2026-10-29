using System.Drawing;
using System.Text.Json;
using OmniCore.Cli;
using OmniCore.Client;
using OmniCore.Protocol;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    private sealed class CompanionExpressionClient(IOmniClient inner) : IOmniClient
    {
        internal volatile string Expression = "idle";
        public CommandAck Send(WireEnvelope command, CancellationToken token) => inner.Send(command, token);
        public IReadOnlyList<WireEnvelope> SubscribeSince(long sequence) => inner.SubscribeSince(sequence);
        public SessionQueryResult? Query(string name, CancellationToken token)
        {
            var result = inner.Query(name, token);
            if (name != "companion.pet:terminal" || result is null) return result;
            var pet = CompanionPetProtocol.Decode(result.Json)!;
            return new(name, CompanionPetProtocol.Encode(pet with { Reaction = new(Expression, DateTimeOffset.UtcNow.AddSeconds(30), 1) }));
        }
    }
    // Controls the session lottery, then exercises the real Host query and renderer.
    [Theory]
    [InlineData("claude-crab")]
    [InlineData("codex-robot")]
    public void Companion_design_preview_renders_six_native_sixel_states(string design) => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "ox", 0);
        fx.Server.CompanionService.GuestSelector = (_, _, _) => design;
        var client = new CompanionExpressionClient(fx.Server);
        fx.StartTui(turnHost: new CompanionHubTurn(fx), initialColumns: 120, initialRows: 40, client: client);
        if (!fx.Read(() => fx.App.SidebarOpen)) fx.InjectKey(new Key(KeyCode.F2));
        fx.Wait(() => fx.App.CompanionPet?.GuestId == design && fx.App.CompanionPet.Species == "ox", "guest connected; ordinary species preserved");
        Type(fx, "/pet home"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.CompanionPlayFrame == 0, "home");
        // These captures compare expressions; moving idle frames have their own cache/animation test.
        fx.Invoke(() => CompanionPetPresentation.Execute(fx.Server, "reduced"));
        fx.Wait(() => fx.App.CompanionPet?.Preferences.ReducedMotion == true, "stable expression captures");
        EmulatePetSixelDevice(fx);
        string? previous = null;
        foreach (var expression in new[] { "idle", "working", "waiting", "success", "failure", "pet" })
        {
            client.Expression = expression;
            previous = WaitForPetExpressionFrame(fx, expression, previous);
            ExportPetRasterFrame(fx, design + "-" + expression);
        }
        Assert.Equal("ox", new OmniCore.Host.CompanionPetService(Path.Combine(fx.Root, "host-data")).Status().Species);
        Assert.True(fx.Read(() => fx.App.Composer!.HasFocus));
        fx.Invoke(() => CompanionPetPresentation.Execute(fx.Server, "mode unicode"));
        fx.Wait(() => fx.App.CompanionVisual.Effective == "unicode" && fx.Application.Driver!.GetOutputBuffer().GetRasterImages().Count == 0,
            "guest Unicode fallback clears graphics");
        ExportPreferenceFrame(fx, 120, 40, "companion-" + design + "-unicode", "De visita");
        fx.Invoke(() => CompanionPetPresentation.Execute(fx.Server, "mode text"));
        fx.Wait(() => fx.App.CompanionVisual.Effective == "text", "guest textual fallback");
        Assert.Contains("De visita", CompanionPetPresentation.TextSummary(fx.Read(() => fx.App.CompanionPet!)));
        fx.Invoke(() => CompanionPetPresentation.Execute(fx.Server, "hide"));
        fx.Wait(() => fx.App.CompanionVisual.Effective == "hidden", "guest respects ordinary visibility control");
    }, noColor: false);
    [Fact]
    public void Companion_graphics_ox_native_sixel_six_states_cleanup_resize_overlays_and_failure() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "ox", 0);
        var client = new CompanionExpressionClient(fx.Server);
        fx.StartTui(turnHost: new CompanionHubTurn(fx), initialColumns: 120, initialRows: 40, client: client);
        if (!fx.Read(() => fx.App.SidebarOpen)) fx.InjectKey(new Key(KeyCode.F2));
        fx.Wait(() => fx.App.CompanionPet?.Discovered == true, "ox connected");
        Type(fx, "/pet home"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.CompanionPlayFrame == 0, "home for detailed renderer");
        fx.Invoke(() => CompanionPetPresentation.Execute(fx.Server, "reduced"));
        fx.Wait(() => fx.App.CompanionPet?.Preferences.ReducedMotion == true, "stable expression captures");
        // Controlled device evidence, not a claim about the terminal hosting the test runner.
        EmulatePetSixelDevice(fx);
        fx.Wait(() => fx.App.CompanionVisual.Effective == "graphics", "verified emulated Sixel capability selected");
        Type(fx, "Borrador con foco intacto");
        var draft = fx.Read(() => fx.App.Composer!.Text);
        var caret = fx.Read(() => fx.App.Composer!.InsertionPoint);
        fx.Invoke(() => { fx.App.Conversation!.MoveHome(); fx.App.Conversation.SelectAll(); fx.App.Composer!.SetFocus(); });
        var scroll = fx.Read(() => fx.App.Conversation!.Viewport.Location);
        var selection = fx.Read(() => fx.App.Conversation!.SelectedText);
        string? previous = null;
        foreach (var expression in new[] { "idle", "working", "waiting", "success", "failure", "pet" })
        {
            client.Expression = expression;
            previous = WaitForPetExpressionFrame(fx, expression, previous);
            Assert.StartsWith("\u001bP", previous);
            ExportPetRasterFrame(fx, "ox-" + expression);
        }
        fx.Read(() =>
        {
            Assert.Equal(draft, fx.App.Composer!.Text); Assert.Equal(caret, fx.App.Composer.InsertionPoint); Assert.True(fx.App.Composer.HasFocus);
            Assert.Equal(selection, fx.App.Conversation!.SelectedText); Assert.Equal(scroll, fx.App.Conversation.Viewport.Location);
            var image = Descendants(fx.App.MainWindow!).OfType<CompanionImageView>().Single(view => view.Visible);
            Assert.False(image.CanFocus); Assert.False(image.Enabled); Assert.True(image.UseBackgroundRendering);
        });
        fx.InjectKey(new Key(KeyCode.F2));
        fx.Wait(() => !fx.App.Sidebar!.Visible && fx.Application.Driver!.GetOutputBuffer().GetRasterImages().Count == 0, "closing panel cleans native image");
        fx.InjectKey(new Key(KeyCode.F2));
        fx.Wait(() => fx.App.Sidebar!.Visible && fx.Application.Driver!.GetOutputBuffer().GetRasterImages().Count == 1, "reopening panel restores image");
        fx.Invoke(() => fx.Application.Driver!.SetScreenSize(100, 36));
        fx.Wait(() => fx.App.MainWindow!.Frame.Width == 100 && fx.Application.Driver!.GetOutputBuffer().GetRasterImages().Count == 1, "image fits resize");
        fx.Invoke(() =>
        {
            // Emulates a new font's reported cell dimensions via the framework's own support object.
            fx.Application.Driver!.SetSixelSupport(new() { IsSupported = true, Resolution = new(12, 24), MaxPaletteColors = 64 });
            foreach (var view in Descendants(fx.App.MainWindow!).OfType<PixelWidgetView>()) view.RefreshRasterScale();
        });
        fx.Wait(() => fx.Application.Driver!.GetOutputBuffer().GetRasterImages().All(image => image.DestinationCells.Right < 100), "font scaling stays within terminal");
        fx.InjectKey(new Key(KeyCode.F4));
        fx.Wait(() => fx.App.Overlay is not null && fx.Application.Driver!.GetOutputBuffer().GetRasterImages().Count == 0, "menu removes raster rather than painting over persistent graphics");
        fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => fx.App.Overlay is null && fx.Application.Driver!.GetOutputBuffer().GetRasterImages().Count == 1, "closing menu restores image");
        fx.Invoke(() => CompanionPetPresentation.Execute(fx.Server, "reduced"));
        fx.Wait(() => fx.App.CompanionPet?.Preferences.ReducedMotion == true, "reduced motion persisted");
        var stopped = fx.Read(() => fx.App.CompanionFrame);
        fx.Invoke(() => { for (var tick = 0; tick < 40; tick++) fx.App.AnimateActivity(); });
        Assert.Equal(stopped, fx.Read(() => fx.App.CompanionFrame));
        fx.Invoke(() => fx.App.HandlePetGraphicsError(new IOException("Injected raster backend failure")));
        fx.Wait(() => fx.App.CompanionVisual.Effective == "unicode" && fx.Application.Driver!.GetOutputBuffer().GetRasterImages().Count == 0, "backend failure cleans and falls back");
        fx.Invoke(() => { for (var tick = 0; tick < 80; tick++) fx.App.AnimateActivity(); });
        Assert.Equal("unicode", fx.Read(() => fx.App.CompanionVisual.Effective));
        Assert.Equal(draft, fx.Read(() => fx.App.Composer!.Text)); Assert.True(fx.Read(() => fx.App.Composer!.HasFocus));
        fx.Invoke(() => fx.App.Composer!.Text = "");
        Type(fx, "/pet detect"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "companion-settings", "explicit redetection");
        fx.Wait(() => fx.App.CompanionVisual.Effective == "graphics", "only explicit redetection clears failure latch");
        fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => fx.App.Overlay is null && fx.Application.Driver!.GetOutputBuffer().GetRasterImages().Count == 1, "image resumes after explicit recovery");
        fx.Invoke(() => CompanionPetPresentation.Execute(fx.Server, "mode texto"));
        fx.Wait(() => fx.App.CompanionVisual.Effective == "text", "explicit textual mode");
        ExportPreferenceFrame(fx, 100, 36, "companion-ox-text", "Buey");
        fx.Invoke(() => CompanionPetPresentation.Execute(fx.Server, "hide"));
        fx.Wait(() => fx.App.CompanionVisual.Effective == "hidden", "hide removes all presentations");
        Assert.Equal(0, fx.Read(() => fx.Application.Driver!.GetOutputBuffer().GetRasterImages().Count));
    }, noColor: false);

    [Theory]
    [InlineData("fox", 1)] [InlineData("owl", 2)] [InlineData("beaver", 3)] [InlineData("cat", 4)] [InlineData("capybara", 5)]
    public void Companion_graphics_other_species_reuse_native_renderer(string species, int index) => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, species, index); fx.StartTui(turnHost: new CompanionHubTurn(fx), initialColumns: 120, initialRows: 40);
        if (!fx.Read(() => fx.App.SidebarOpen)) fx.InjectKey(new Key(KeyCode.F2));
        fx.Wait(() => fx.App.CompanionPet?.Discovered == true, "species connected");
        Type(fx, "/pet home"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.CompanionPlayFrame == 0, "home");
        EmulatePetSixelDevice(fx);
        fx.Wait(() => fx.App.CompanionVisual.Effective == "graphics" && fx.Application.Driver!.GetOutputBuffer().GetRasterImages()
            .Any(image => image.EncodedSixel is { Length: > 100 }), "native species graphic frame");
        ExportPetRasterFrame(fx, species + "-idle");
        fx.Invoke(() => CompanionPetPresentation.Execute(fx.Server, "mode unicode"));
        fx.Wait(() => fx.App.CompanionVisual.Effective == "unicode" && fx.Application.Driver!.GetOutputBuffer().GetRasterImages().Count == 0,
            "mode switch clears native raster placements");
    }, noColor: false);

    [Fact]
    public void Companion_graphics_unverified_names_redirected_output_and_user_settings_choose_safe_fallback() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "ox", 0); fx.StartTui(turnHost: new CompanionHubTurn(fx), initialColumns: 120, initialRows: 40);
        fx.Wait(() => fx.App.CompanionPet?.Discovered == true, "ox connected");
        Type(fx, "/pet home"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.CompanionPlayFrame == 0, "home");
        fx.Invoke(() => fx.Application.Driver!.SetKittyGraphicsSupport(new() { IsSupported = true }));
        fx.Wait(() => fx.Application.Driver!.KittyGraphicsSupport?.IsSupported == false, "environment-only Kitty hint is disarmed");
        Assert.Equal("unicode", fx.Read(() => fx.App.CompanionVisual.Effective));
        Type(fx, "/pet mode gráfico"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "companion-settings", "display settings");
        fx.Wait(() => OverlayText(fx.App.Overlay!).Contains("Gráficos no disponibles", StringComparison.Ordinal), "requested graphics reports compatible fallback");
        ExportPreferenceFrame(fx, 120, 40, "companion-mode-settings", "Mascota");
        ClickId(fx, "companion-settings-visual"); ClickId(fx, "companion-settings-visual");
        fx.Wait(() => fx.App.CompanionVisual.Effective == "text", "text mode selected from native button");
        Assert.Equal("text", new OmniCore.Host.CompanionPetService(Path.Combine(fx.Root, "host-data")).Status().Preferences.RenderMode);
        fx.InjectKey(new Key(KeyCode.Esc));
        Type(fx, "/pet"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Overlay?.Id == "companion-detail", "text detail opens");
        Assert.True(fx.Read(() => fx.App.Overlay!.Frame.Height < 18), "text-only detail fits its contents");
        Assert.Empty(fx.Read(() => Descendants(fx.App.Overlay!).OfType<CompanionImageView>().ToArray()));
        fx.InjectKey(new Key(KeyCode.Esc));
        fx.Invoke(() => { fx.App.CompanionInteractiveOverride = false; fx.Application.Driver!.SetSixelSupport(new() { IsSupported = true }); });
        fx.Invoke(() => CompanionPetPresentation.Execute(fx.Server, "mode auto"));
        fx.Wait(() => fx.App.CompanionVisual.Effective == "text", "redirected output cannot choose graphics or animation");
        Assert.Empty(fx.Read(() => fx.Application.Driver!.GetOutputBuffer().GetRasterImages()));
    }, noColor: false);

    private static string WaitForPetExpressionFrame(TuiFixture fx, string expression, string? previous)
    {
        string? captured = null;
        // Observe and capture on the UI thread in the same turn: a background render
        // or animation tick may replace a placement between a wait and a later read.
        fx.Wait(() => fx.Read(() =>
        {
            if (fx.App.CompanionPet?.Reaction?.AnimationId != expression) return false;
            var images = fx.Application.Driver!.GetOutputBuffer().GetRasterImages();
            if (images.Count != 1 || images[0].EncodedSixel is not { Length: > 100 } code || code == previous) return false;
            captured = code;
            return true;
        }), "native expression frame " + expression);
        return captured!;
    }

    private static void ExportPetRasterFrame(TuiFixture fx, string scene)
    {
        var directory = Environment.GetEnvironmentVariable("OMNICORE_PET_GRAPHICS_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        fx.Read(() =>
        {
            var raster = fx.Application.Driver!.GetOutputBuffer().GetRasterImages().Single();
            var pixels = raster.Pixels!;
            var evidence = new { Width = pixels.GetLength(0), Height = pixels.GetLength(1),
                CellWidth = fx.Application.Driver.SixelSupport!.Resolution.Width, CellHeight = fx.Application.Driver.SixelSupport.Resolution.Height,
                raster.DestinationCells.X, raster.DestinationCells.Y, Columns = fx.App.MainWindow!.Frame.Width, Rows = fx.App.MainWindow.Frame.Height };
            File.WriteAllText(Path.Combine(directory, "companion-" + scene + ".raster.json"), JsonSerializer.Serialize(evidence));
            File.WriteAllText(Path.Combine(directory, "companion-" + scene + ".sixel"), raster.EncodedSixel);
        });
        if (scene is "ox-idle" or "ox-working") ExportPreferenceFrame(fx, 120, 40, "companion-raster-" + scene, "Mascota");
    }
    private static void EmulatePetSixelDevice(TuiFixture fx) => fx.Invoke(() =>
    {
        var driver = fx.Application.Driver!;
        // The host runner cannot answer startup DA queries. Keep the controlled device's
        // result stable when an abandoned startup probe subsequently reports unsupported.
        driver.SixelSupportChanged += (_, _) =>
        {
            if (driver.SixelSupport?.IsSupported != true)
                driver.SetSixelSupport(new() { IsSupported = true, Resolution = new(8, 16), MaxPaletteColors = 64 });
        };
        driver.SetSixelSupport(new() { IsSupported = true, Resolution = new(8, 16), MaxPaletteColors = 64 });
    });
}
