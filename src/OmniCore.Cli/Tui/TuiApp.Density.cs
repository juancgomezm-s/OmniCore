using Terminal.Gui.ViewBase;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private readonly TuiDensityStore _densityStore;
    private bool _compactDensity;
    private bool? _densityBeforePreview;
    internal bool CompactDensity => _compactDensity;
    internal bool DensityPreviewActive => _densityBeforePreview.HasValue;

    private string DensityText(string key)
    {
        var language = _locale switch { "en" => 1, "fr" => 2, "it" => 3, _ => 0 };
        return key switch
        {
            "open" => new[] { "Probar panel lateral compacto", "Try compact sidebar", "Essayer le panneau compact", "Prova pannello compatto" }[language],
            "description" => new[] { "Panel lateral compacto; chat y mascota sin cambios", "Compact sidebar; chat and companion unchanged", "Panneau compact ; conversation et mascotte inchangées", "Pannello compatto; chat e mascotte invariati" }[language],
            "preview" => new[] { "Panel compacto · Esc volver · /compacto guardar", "Sidebar · Esc restore · /density save", "Panneau · Échap rétablir · /density save", "Pannello · Esc ripristina · /density save" }[language],
            "failed" => new[] { "No se pudo guardar. La vista sigue siendo reversible con Esc.", "Could not save. Esc still restores the preview.", "Enregistrement impossible. Échap rétablit l'aperçu.", "Salvataggio non riuscito. Esc ripristina l'anteprima." }[language],
            _ => "/compacto probar | guardar | cancelar | normal · /density preview | save | cancel | normal",
        };
    }

    private bool HandleDensitySlash(string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] is not ("/compacto" or "/density")) return false;
        if (_composer is not null) _composer.Text = "";
        if (parts.Length > 2) { ShowMessage(DensityText("usage")); return true; }
        switch (parts.Length == 1 ? "preview" : parts[1].ToLowerInvariant())
        {
            case "probar": case "preview":
                BeginDensityPreview(); break;
            case "guardar": case "save":
                SaveDensity(_compactDensity); break;
            case "cancelar": case "cancel":
                CancelDensityPreview(); break;
            case "normal":
                SaveDensity(false); break;
            default: ShowMessage(DensityText("usage")); break;
        }
        return true;
    }

    private void BeginDensityPreview()
    {
        _densityBeforePreview ??= _compactDensity;
        SetDensity(true);
    }

    private bool CancelDensityPreview()
    {
        if (_densityBeforePreview is not { } previous) return false;
        _densityBeforePreview = null;
        SetDensity(previous);
        return true;
    }

    private void SaveDensity(bool compact)
    {
        try { _densityStore.Save(compact); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ShowMessage(DensityText("failed")); return; }
        _densityBeforePreview = null;
        SetDensity(compact);
    }

    private void SetDensity(bool compact)
    {
        _compactDensity = compact;
        ApplyResponsiveLayout();
        _window?.SetNeedsLayout();
        _window?.SetNeedsDraw();
    }

    private void ApplyDensitySpacing()
    {
        var inset = _compactDensity ? 1 : 2;
        if (_sidebarSession is not null) { _sidebarSession.X = inset; _sidebarSession.Y = _compactDensity ? 2 : 3; }
        if (_sidebarContent is not null && _sidebarAttention is not null)
        { _sidebarContent.X = inset; _sidebarContent.Y = Pos.Bottom(_sidebarAttention) + (_compactDensity ? 0 : 1); }
    }

}
