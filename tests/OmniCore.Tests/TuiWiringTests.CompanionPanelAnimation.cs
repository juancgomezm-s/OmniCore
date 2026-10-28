using OmniCore.Cli;
using OmniCore.Client;
using Terminal.Gui.Input;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;

namespace OmniCore.Tests;

public sealed partial class TuiWiringTests
{
    [Fact]
    public void Companion_graphic_frames_reuse_encoded_images_keep_one_placement_and_clear_the_bounded_cache()
    {
        var retired = new List<View>();
        try
        {
            RunTuiTest(fx =>
            {
                var frames = new CompanionGraphicAssets().Load("fox").GetAwaiter().GetResult();
                fx.StartTui(initialColumns: 120, initialRows: 40);
                EmulatePetSixelDevice(fx);
                var renderer = new PixelWidgetView { X = 35, Y = 3, Width = 22, Height = 12 };
                fx.Invoke(() => { fx.App.MainWindow!.Add(renderer); fx.App.Composer!.SetFocus(); });
                var originals = new Dictionary<int, (CompanionImageView View, string Encoding)>();
                string? previous = null;
                foreach (var frame in new[] { 0, 13, 14, 0, 15, 16, 1, 0, 13, 14, 15, 16, 1, 0 })
                {
                    fx.Invoke(() => renderer.RenderRaster(frames[frame], 64, _ => Assert.Fail("Unexpected graphics failure"), retired.Add));
                    fx.Wait(() => fx.Read(() => fx.Application.Driver!.GetOutputBuffer().GetRasterImages() is { Count: 1 } images
                        && images[0].EncodedSixel is { Length: > 100 } encoded && encoded != previous), "prepared native animation frame");
                    fx.Read(() =>
                    {
                        var encoded = fx.Application.Driver!.GetOutputBuffer().GetRasterImages().Single().EncodedSixel!;
                        if (originals.TryGetValue(frame, out var original))
                        {
                            Assert.Same(original.View, renderer.Raster);
                            Assert.Same(original.Encoding, encoded); // The native encoder was not run again.
                        }
                        else originals.Add(frame, (renderer.Raster!, encoded));
                        previous = encoded;
                        Assert.InRange(renderer.CachedRasterFrameCount, 1, PixelWidgetView.MaxCachedRasterFrames);
                        Assert.Single(renderer.SubViews.OfType<CompanionImageView>(), view => view.Visible);
                        Assert.True(fx.App.Composer!.HasFocus);
                    });
                }
                Assert.Empty(retired);
                fx.Invoke(() => renderer.RenderRaster(frames[2], 64, _ => Assert.Fail("Unexpected graphics failure"), retired.Add));
                fx.Wait(() => fx.Read(() => fx.Application.Driver!.GetOutputBuffer().GetRasterImages() is { Count: 1 } images
                    && images[0].EncodedSixel is { Length: > 100 } encoded && encoded != previous), "cache eviction frame");
                Assert.Single(retired);
                fx.Read(() => Assert.Equal(PixelWidgetView.MaxCachedRasterFrames, renderer.CachedRasterFrameCount));
                fx.Invoke(renderer.RefreshRasterScale);
                fx.Wait(() => fx.Read(() => fx.Application.Driver!.GetOutputBuffer().GetRasterImages().Count == 1), "font-scale refresh");
                fx.Invoke(renderer.ClearRaster);
                fx.Wait(() => fx.Read(() => fx.Application.Driver!.GetOutputBuffer().GetRasterImages().Count == 0), "all placements removed");
                fx.Read(() =>
                {
                    Assert.Equal(0, renderer.CachedRasterFrameCount);
                    Assert.Empty(renderer.SubViews.OfType<CompanionImageView>());
                });
            }, noColor: false);
        }
        finally { foreach (var view in retired) view.Dispose(); }
    }

    [Fact]
    public void Companion_panel_clock_pauses_behind_menus_and_resumes_without_changing_the_draft() => RunTuiTest(fx =>
    {
        SeedPlaygroundPet(fx, "fox", 1);
        fx.StartTui(initialColumns: 120, initialRows: 40);
        if (!fx.App.SidebarOpen) KeyWithEffect(fx, KeyCode.F2, () => fx.App.SidebarOpen, "sidebar opened");
        Type(fx, "/pet home"); KeyWithEffect(fx, KeyCode.Enter, () => fx.App.Composer!.Text.Length == 0, "home");
        Type(fx, "Borrador intacto");
        fx.InjectKey(new Key(KeyCode.F4));
        fx.Wait(() => fx.App.Overlay is not null, "settings menu");
        fx.Invoke(() =>
        {
            var stopped = fx.App.CompanionFrame;
            for (var tick = 0; tick < 40; tick++) fx.App.AnimateActivity();
            Assert.Equal(stopped, fx.App.CompanionFrame);
        });
        fx.InjectKey(new Key(KeyCode.Esc));
        fx.Wait(() => fx.App.Overlay is null, "settings dismissed");
        fx.Invoke(() =>
        {
            var before = fx.App.CompanionFrame;
            for (var tick = 0; tick < 12; tick++) fx.App.AnimateActivity();
            Assert.True(fx.App.CompanionFrame > before);
            Assert.Equal("Borrador intacto", fx.App.Composer!.Text);
            Assert.True(fx.App.Composer.HasFocus);
        });
    });
}
