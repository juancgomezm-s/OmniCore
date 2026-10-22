using OmniCore.Client;
using OmniCore.Protocol;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private CompanionPetSnapshot? _pet;
    private PixelWidgetView? _petView;
    private PixelWidgetView? _petDetail;
    private bool _petConnected;
    private long _petFrame;
    private int _petTicks;
    private DateTimeOffset _petLastActive = DateTimeOffset.UtcNow;
    internal CompanionPetSnapshot? CompanionPet => _pet;
    internal long CompanionFrame => _petFrame;
    private void PollCompanion()
    {
        try
        {
            var wasVisible = _pet?.Preferences.Visible == true;
            _pet = CompanionPetProtocol.Decode(_client.Query("companion.pet:terminal", CancellationToken.None)?.Json ?? "null");
            _petConnected = _pet is not null;
            if (wasVisible && _pet?.Preferences.Visible == false && _petDetail is not null) CloseOverlay();
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or IOException)
        { _petConnected = false; }
        RenderCompanion();
    }
    private string PetAnimation()
    {
        var working = HasActiveModelWork;
        var waiting = _state.Overlays.Count > 0;
        if (working || waiting) _petLastActive = DateTimeOffset.UtcNow;
        return _pet is null ? "idle" : CompanionPetPresentation.Animation(_pet, working, waiting, _petLastActive, DateTimeOffset.UtcNow);
    }
    private void AnimateCompanion()
    {
        if (_pet is null || !_petConnected || _pet.Preferences.ReducedMotion || !_animationPreferences.Enabled
            || _animationPreferences.ReducedMotion || !(_petView?.Visible == true && _sidebar?.Visible == true || _petDetail?.Visible == true)) return;
        if (++_petTicks < 4) return;
        _petTicks = 0; _petFrame++; RenderCompanion();
    }
    private void RenderCompanion()
    {
        if (_sidebar is null || _sidebarContent is null) return;
        if (_petView is null)
        {
            _petView = new() { Id = "companion-pet", X = 2, Width = Dim.Fill(1), Visible = false };
            _petView.ActionInvoked += () => RunPetAction("pet", false); _sidebar.Add(_petView);
            _petView.DismissInvoked += () => RunPetAction("hide", false);
            ApplyTheme(_petView, "#202C3B");
        }
        _petView.DismissText = _locale == "es" ? "Ocultar" : "Hide";
        var visible = _pet?.Discovered == true && _pet.Preferences.Visible;
        var full = (_window?.Frame.Height ?? 0) >= 36 && _sidebar.Viewport.Width >= 23;
        var height = full ? 12 : 4;
        if (_petView.Visible != visible || _petView.Frame.Height != height)
        {
            _petView.Visible = visible; _petView.Height = height; _petView.Y = Pos.AnchorEnd(height + 1);
            _sidebarContent.Height = Dim.Fill(visible ? height + 2 : 1);
        }
        if (_pet is null) return;
        var moving = _petConnected && !_pet.Preferences.ReducedMotion && _animationPreferences.Enabled && !_animationPreferences.ReducedMotion;
        var widget = new CompanionPetSidebarWidget(_pet, PetAnimation(), moving ? _petFrame : 1,
            _animationPreferences.Ascii, _locale);
        if (visible) _petView.Render(widget.Build(_state, full ? WidgetSize.Normal : WidgetSize.Compact),
            Environment.GetEnvironmentVariable("NO_COLOR") is not null);
        if (_petDetail?.Visible == true) _petDetail.Render(widget.Build(_state,
            (_window?.Frame.Height ?? 0) >= 24 ? WidgetSize.Expanded : WidgetSize.Compact),
            Environment.GetEnvironmentVariable("NO_COLOR") is not null);
    }
    private void RunPetAction(string argument, bool detail)
    {
        try
        {
            var petHadFocus = _petView?.HasFocus == true;
            _pet = CompanionPetPresentation.Execute(_client, argument); _petConnected = true;
            _petLastActive = DateTimeOffset.UtcNow; RenderCompanion();
            if (argument.Trim() == "hide")
            {
                _petTicks = 0;
                if (_petDetail is not null) CloseOverlay();
                else if (petHadFocus) _composer?.SetFocus();
            }
            else if (detail) ShowPetDetail();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Text.Json.JsonException or IOException)
        { ShowMessage(error.Message); }
    }
    private void ShowPetDetail()
    {
        if (_window is null || _pet is null) return;
        CloseOverlay();
        if (_window.Frame.Height < 18)
        {
            ShowMessage(CompanionPetPresentation.Summary(_pet, _locale)
                + "\n/pet pet · /pet show · /pet hide\n/pet rename <name> · /pet reduced");
            return;
        }
        var full = _window.Frame.Height >= 24;
        var frame = OverlayFrame(CompanionPetPresentation.Word("pet", _locale), full ? 21 : Math.Max(8, _window.Frame.Height - 2));
        frame.Id = "companion-detail";
        _petDetail = new PixelWidgetView { Id = "companion-detail-renderer", X = 2, Y = 1,
            Width = Dim.Fill(2), Height = full ? 12 : 4 };
        _petDetail.ActionInvoked += () => RunPetAction("pet", false); frame.Add(_petDetail);
        var y = full ? 14 : 6;
        var name = new TextField { Id = "companion-name", X = 2, Y = y, Width = 24, Text = _pet.Name ?? "Omni" };
        var rename = new Button { Id = "companion-rename", X = 28, Y = y, Text = _locale == "es" ? "Renombrar" : "Rename" };
        rename.Accepted += (_, _) => RunPetAction("rename " + name.Text, false);
        var visibility = new Button { Id = "companion-visible", X = 2, Y = y + 2,
            Text = PetVisibilityLabel() };
        visibility.Accepted += (_, _) => { RunPetAction(_pet?.Preferences.Visible == true ? "hide" : "show", false);
            visibility.Text = PetVisibilityLabel(); };
        var motion = new Button { Id = "companion-motion", X = 20, Y = y + 2,
            Text = _pet.Preferences.ReducedMotion ? "/pet motion" : "/pet reduced" };
        motion.Accepted += (_, _) => { RunPetAction(_pet?.Preferences.ReducedMotion == true ? "motion" : "reduced", false);
            motion.Text = _pet?.Preferences.ReducedMotion == true ? "/pet motion" : "/pet reduced"; };
        frame.Add(name, rename, visibility, motion);
        if (full) frame.Add(new Label { X = 2, Y = y + 4, Width = Dim.Fill(2), Height = 1,
            Text = $"{CompanionPetPresentation.Word(_pet.Stage, _locale)} · {_pet.Xp} XP · {_pet.Mood}/100" });
        var close = new Button { Id = "companion-close", X = 2, Y = Pos.AnchorEnd(1), Text = _locale == "es" ? "Cerrar" : "Close" };
        close.Accepted += (_, _) => { _petDetail = null; CloseOverlay(); }; frame.Add(close);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); frame.SetFocus(); RenderCompanion();
    }
    private string PetVisibilityLabel() => _pet?.Preferences.Visible == true
        ? _locale == "es" ? "Ocultar mascota" : "Hide companion"
        : _locale == "es" ? "Mostrar mascota" : "Show companion";
}
