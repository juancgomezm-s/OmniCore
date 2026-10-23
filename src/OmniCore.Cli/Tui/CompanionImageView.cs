using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

/// <summary>Framework-owned raster output, scaling, encoding, retained placements and cleanup.</summary>
internal sealed class CompanionImageView : ImageView
{
    internal Action<Exception>? Failed { get; init; }
    private bool _failed;
    internal CompanionImageView()
    {
        CanFocus = false; Enabled = false; UseRasterGraphics = true;
        UseBackgroundRendering = true; AllowSixelUpscaling = false; MaxSixelPaletteColors = 64;
        ViewportSettings = ViewportSettingsFlags.TransparentMouse;
    }
    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (_failed) return true;
        try { return base.OnDrawingContent(context); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException)
        {
            _failed = true;
            App?.Invoke(() => Failed?.Invoke(error));
            return true;
        }
    }
}
