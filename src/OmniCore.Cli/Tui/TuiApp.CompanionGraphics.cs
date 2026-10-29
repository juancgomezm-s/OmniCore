using System.Text;
using System.Text.RegularExpressions;
using OmniCore.Client;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private readonly CompanionVisualPolicy _petVisual = new();
    private readonly CompanionGraphicAssets _petAssets = new();
    private bool _petProbeStarted, _petProbeComplete;
    private int _petProbeGeneration;
    private DateTimeOffset _petProbeDeadline;
    private string? _petVisualNotice;
    private System.Drawing.Size _petObservedSize;
    private DateTimeOffset? _petCellRefreshAt;
    internal bool? CompanionInteractiveOverride { get; set; }
    internal bool? CompanionUnicodeOverride { get; set; }
    internal bool? CompanionColorOverride { get; set; }
    private bool PetNoColor => Environment.GetEnvironmentVariable("NO_COLOR") is not null
        || !(CompanionColorOverride ?? (_application?.Driver?.ColorCapabilities?.Capability != ColorCapabilityLevel.NoColor));
    internal CompanionVisualSelection CompanionVisual => _petVisual.Select(_pet?.Preferences.RenderMode ?? "auto",
        _pet?.Preferences.Visible == true, PetCapabilities());
    private CompanionVisualCapabilities PetCapabilities()
    {
        var driver = _application?.Driver;
        var interactive = CompanionInteractiveOverride ?? (!Console.IsInputRedirected && !Console.IsOutputRedirected);
        var unicode = CompanionUnicodeOverride ?? (!_animationPreferences.Ascii && Console.OutputEncoding.CodePage is 65001 or 1200 or 1201
            && Environment.GetEnvironmentVariable("TERM") != "dumb"
            && Terminal.Gui.Text.StringExtensions.GetColumns("▀▄█", false) == 3);
        return new(interactive, unicode && !_animationPreferences.Ascii, driver?.SixelSupport?.IsSupported == true,
            false, !PetNoColor,
            _petProbeComplete ? "framework DA / cell-size query" : "unverified graphics");
    }
    private void ProbePetGraphics(bool explicitRetry = false)
    {
        if (explicitRetry)
        { _petVisual.Redetect(); _petAssets.Clear(); _petVisualNotice = null; _petProbeGeneration++; }
        if (_application?.Driver is not { } driver) return;
        if (!PetCapabilities().Interactive || driver.IsLegacyConsole)
        { _petProbeStarted = true; _petProbeComplete = true; return; }
        if (!explicitRetry && (_pet?.Preferences.Visible != true || _pet.Preferences.RenderMode is "text" or "hidden")) return;
        if (_petProbeStarted && !explicitRetry) return;
        _petProbeStarted = true; _petProbeComplete = false; _petProbeDeadline = DateTimeOffset.UtcNow.AddSeconds(3);
        var generation = ++_petProbeGeneration;
        // This version's Kitty detector trusts environment hints, and its reader cannot safely
        // parse an APC response. Never activate that backend from a terminal name or inherited env.
        driver.SetKittyGraphicsSupport(new() { IsSupported = false });
        void Complete(SixelSupportResult result) => _application.Invoke(() =>
        {
            if (generation != _petProbeGeneration || DateTimeOffset.UtcNow > _petProbeDeadline) return;
            driver.SetSixelSupport(result); _petProbeComplete = true;
            if (result.IsSupported) RefreshPetCellSize(generation);
            RenderCompanion();
        });
        if (!explicitRetry && driver.SixelSupport is { } detected) Complete(detected);
        else new SixelSupportDetector(driver).Detect(Complete);
    }
    private void RefreshPetCellSize(int generation)
    {
        _application!.Driver!.QueueAnsiRequest(new AnsiEscapeSequenceRequest
        {
            Request = "\u001b[16t", Terminator = "t", Value = "6",
            ResponseReceived = response => _application.Invoke(() =>
            {
                if (generation != _petProbeGeneration || _application.Driver?.SixelSupport is not { } support) return;
                var match = Regex.Match(response ?? "", @"\[6;(\d+);(\d+)t$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                if (match.Success && int.TryParse(match.Groups[1].Value, out var height) && int.TryParse(match.Groups[2].Value, out var width)
                    && height is > 0 and <= 256 && width is > 0 and <= 256)
                {
                    support.Resolution = new(width, height);
                    _application.Driver.SetSixelSupport(support); _petView?.RefreshRasterScale(); _petDetail?.RefreshRasterScale();
                }
            }),
            Abandoned = () => { }, // Framework scheduler times out; no retry loop or raw input reader.
        });
    }
    private void UpdatePetGraphics()
    {
        if (_pet?.Discovered != true) return;
        ProbePetGraphics();
        if (_petProbeStarted && !_petProbeComplete && DateTimeOffset.UtcNow > _petProbeDeadline)
        { _petProbeComplete = true; _petProbeGeneration++; }
        // Disarm late environment-only Kitty results from the framework startup detector.
        if (_application?.Driver?.KittyGraphicsSupport?.IsSupported == true)
            _application.Driver.SetKittyGraphicsSupport(new() { IsSupported = false });
        var selection = CompanionVisual;
        if (selection.Effective == "graphics") _petVisualNotice = null;
        if (selection.Requested == "graphics" && selection.Effective is not ("graphics" or "hidden")
            && _petProbeComplete && _petVisualNotice is null)
            _petVisualNotice = _locale == "es" ? "Gráficos no disponibles; usando " + selection.Effective + "." : "Graphics unavailable; using " + selection.Effective + ".";
        if (_window is { } window && _petObservedSize != window.Frame.Size)
        { _petObservedSize = window.Frame.Size; _petCellRefreshAt = DateTimeOffset.UtcNow.AddMilliseconds(500); }
        if (_petCellRefreshAt is { } refresh && DateTimeOffset.UtcNow >= refresh && selection.Effective == "graphics")
        { _petCellRefreshAt = null; RefreshPetCellSize(_petProbeGeneration); }
        if (_overlay?.Id == "companion-settings")
        {
            var mode = _overlay.SubViews.FirstOrDefault(view => view.Id == "companion-settings-visual");
            var label = "Visual: " + selection.Requested + " → " + selection.Effective;
            if (mode is not null && mode.Text != label) mode.Text = label;
            if (_petVisualNotice is not null && _overlay.SubViews.FirstOrDefault(view => view.Id == "companion-settings-status") is { } status
                && string.IsNullOrEmpty(status.Text)) status.Text = _petVisualNotice;
        }
    }
    private void RenderPetImage(PixelWidgetView view, string animation, bool enabled)
    {
        if (!enabled || CompanionVisual.Effective != "graphics") { view.ClearRaster(); return; }
        var id = CompanionPetPresentation.VisualId(_pet!);
        var guest = id != (_pet!.Species ?? "egg");
        var load = _petAssets.Load(id, guest ? "warm.spots-0" : _pet.Variant, !guest && _pet.Stage == "legendary");
        if (!load.IsCompleted) return; // Never decode, wait or read a file in the draw loop.
        if (!load.IsCompletedSuccessfully) { HandlePetGraphicsError(load.Exception?.GetBaseException() ?? new IOException("Sprite load failed.")); return; }
        var still = _pet.Preferences.ReducedMotion || _animationPreferences.ReducedMotion || !_animationPreferences.Enabled || !_petConnected;
        var index = CompanionVisualPolicy.SpriteFrame(animation, _petFrame, still);
        view.RenderRaster(load.Result[index], _application?.Driver?.Force16Colors == true ? 16 : 64,
            HandlePetGraphicsError, image => _retiredOverlays.Add(image));
    }
    internal void HandlePetGraphicsError(Exception error)
    {
        _petVisual.FailGraphics(); _petAssets.Clear();
        _petView?.ClearRaster(); _petDetail?.ClearRaster();
        _petVisualNotice = _locale == "es" ? "La imagen falló; se activó el respaldo compatible." : "Image failed; compatible fallback enabled.";
        RenderCompanion();
    }
    private bool HandlePetDrawError(Exception error)
    {
        // Terminal.Gui reports asynchronous encoder/scaler failures through its Run handler.
        if (error.Message != "Background ImageView rendering failed." || CompanionVisual.Effective != "graphics") return false;
        HandlePetGraphicsError(error); return true;
    }
    private string PetVisualDescription => (_locale == "es" ? "Solicitado: " : "Requested: ")
        + (_pet?.Preferences.RenderMode ?? "auto") + (_locale == "es" ? " | Efectivo: " : " | Effective: ") + CompanionVisual.Effective
        + (CompanionVisual.Backend is { } backend ? " (" + backend + ")" : "")
        + (_petVisualNotice is { } notice ? "\n" + notice : "");
    private void ShowPetVisualSettings() => ShowCompanionSettings();
}
