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
    private DateTimeOffset _petPolledAt;
    private long _petPolledSequence = -1;
    private string? _petPolledSession;
    private readonly record struct PetWidgetKey(CompanionPetSnapshot Pet, string Animation, int Frame, bool Textual, string Locale);
    private PetWidgetKey? _petWidgetKey;
    private readonly Dictionary<WidgetSize, WidgetModel> _petWidgetModels = new();
    private DateTimeOffset _petQuietUntil;
    private DateTimeOffset _petLastActive = DateTimeOffset.UtcNow;
    internal CompanionPetSnapshot? CompanionPet => _pet;
    internal long CompanionFrame => _petFrame;
    private void PollCompanion()
    {
        var now = DateTimeOffset.UtcNow;
        var interval = _pet?.Preferences.Visible == true ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(3);
        if (_petPolledSession == _cursorSession && _petPolledSequence == _lastSequence && now - _petPolledAt < interval) return;
        _petPolledAt = now; _petPolledSequence = _lastSequence; _petPolledSession = _cursorSession;
        try
        {
            var wasVisible = _pet?.Preferences.Visible == true;
            _pet = CompanionPetProtocol.Decode(_client.Query("companion.pet:terminal", CancellationToken.None)?.Json ?? "null");
            _petConnected = _pet is not null;
            if (wasVisible && _pet?.Preferences.Visible == false && _petDetail is not null) CloseOverlay();
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or IOException)
        { _petConnected = false; }
        // RenderSidebar presents the result once, after the remaining projections are refreshed.
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
        if (_pet is null || !_petConnected || !_pet.Preferences.Visible || _pet.Preferences.ReducedMotion || !_animationPreferences.Enabled
            || _animationPreferences.ReducedMotion || CompanionVisual.Effective is "text" or "hidden")
        {
            if (_petRunner?.Visible == true || _petLetter?.Visible == true) RenderPetPlayground();
            return;
        }
        UpdatePetTyping();
        // Give scrolling a short uninterrupted drawing window; typing still triggers its chase.
        if (DateTimeOffset.UtcNow < _petQuietUntil) return;
        var playing = PetCanPlay();
        if (!playing && _petRunner?.Visible == true) RenderPetPlayground();
        if (!playing && !(_petView?.Visible == true && _sidebar?.Visible == true || _petDetail?.Visible == true)) return;
        // Roaming/chasing keeps its existing cadence. A panel sprite needs fewer updates.
        if (++_petTicks < (playing ? 4 : 6)) return;
        _petTicks = 0; _petFrame++;
        if (playing)
        {
            _petPlayground.Advance();
            _petTyping.Advance(PetTypingTargetX, PetPlayWidth);
        }
        RenderCompanion();
    }
    private void RenderCompanion()
    {
        UpdatePetGraphics();
        RenderPetPlayground();
        if (_sidebar is null || _sidebarContent is null) return;
        if (_petView is null)
        {
            _petView = new() { Id = "companion-pet", X = 2, Width = Dim.Fill(1), Visible = false };
            _petView.ActionInvoked += () => RunPetAction("pet", false); _sidebar.Add(_petView);
            _petView.DismissInvoked += () => RunPetAction("hide", false);
            ApplyTheme(_petView, "#202C3B");
        }
        _petView.DismissText = _locale == "es" ? "Ocultar" : "Hide";
        var visual = CompanionVisual;
        if (visual.Effective != "graphics") _petAssets.Clear();
        var visible = _pet?.Discovered == true && visual.Effective != "hidden";
        var outside = _petRunner?.Visible == true;
        var full = visual.Effective != "text" && !outside && (_window?.Frame.Height ?? 0) >= 36 && _sidebar.Viewport.Width >= 23;
        var height = full ? 12 : visual.Effective == "text" ? 3 : 4;
        if (_petView.Visible != visible || _petView.Frame.Height != height)
        {
            _petView.Visible = visible; _petView.Height = height; _petView.Y = Pos.AnchorEnd(height + 1);
            _sidebarContent.Height = Dim.Fill(visible ? height + 2 : 1);
        }
        if (_pet is null) return;
        var moving = _petConnected && !_pet.Preferences.ReducedMotion && _animationPreferences.Enabled && !_animationPreferences.ReducedMotion;
        var animation = outside ? "play" : PetAnimation();
        var poseFrame = CompanionVisualPolicy.SpriteFrame(animation, _petFrame, !moving);
        var tick = visual.Effective == "graphics" ? poseFrame : poseFrame % 2;
        var textual = visual.Effective is "text" or "hidden";
        var key = new PetWidgetKey(_pet, animation, tick, textual, _locale);
        if (_petWidgetKey != key) { _petWidgetKey = key; _petWidgetModels.Clear(); }
        WidgetModel Model(WidgetSize size)
        {
            if (_petWidgetModels.TryGetValue(size, out var cached)) return cached;
            return _petWidgetModels[size] = textual ? new ListWidgetModel("",
                [new(CompanionPetPresentation.TextSummary(_pet, animation, _locale), ThemeRole.Primary),
                 new(CompanionPetPresentation.Word("pet_action", _locale), ThemeRole.Info)])
                : new CompanionPetSidebarWidget(_pet, animation, tick, textual, _locale).Build(_state, size);
        }
        _petView.LineOnly = textual;
        if (_petDetail is not null) _petDetail.LineOnly = textual;
        if (visible) _petView.Render(Model(full ? WidgetSize.Normal : WidgetSize.Compact),
            PetNoColor);
        if (_petDetail?.Visible == true) _petDetail.Render(Model(
            (_window?.Frame.Height ?? 0) >= 26 ? WidgetSize.Expanded : WidgetSize.Compact),
            PetNoColor);
        RenderPetImage(_petView, animation, visible && full && _sidebar.Visible && _overlay is null && _commandHelper is null && _activeInteraction is null);
        if (_petDetail is not null) RenderPetImage(_petDetail, animation, visible && _petDetail.Visible && _overlay?.Id == "companion-detail"
            && (_window?.Frame.Height ?? 0) >= 26);
    }
    private void RunPetAction(string argument, bool detail)
    {
        try
        {
            var localAction = argument.Trim().ToLowerInvariant();
            if (localAction is "detect" or "detectar")
            { ProbePetGraphics(true); RenderCompanion(); ShowPetVisualSettings(); return; }
            if (localAction is "mode" or "modo") { ShowPetVisualSettings(); return; }
            if (localAction is "play" or "home")
            {
                if (localAction == "play")
                {
                    _pet = CompanionPetPresentation.Execute(_client, _pet?.Discovered == true ? "show" : "discover");
                    _petConnected = true; _petPlayground.Play();
                }
                else _petPlayground.Home();
                CloseOverlay(); RenderCompanion(); _composer?.SetFocus(); return;
            }
            var petHadFocus = _petView?.HasFocus == true || _petRunnerHide?.HasFocus == true;
            _pet = CompanionPetPresentation.Execute(_client, argument); _petConnected = true;
            _petLastActive = DateTimeOffset.UtcNow; RenderCompanion();
            if (detail && (localAction.StartsWith("mode ", StringComparison.Ordinal) || localAction.StartsWith("modo ", StringComparison.Ordinal)))
            { ShowPetVisualSettings(); return; }
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
        int CardWidth() => Math.Min(Math.Max(42,
            Terminal.Gui.Text.StringExtensions.GetColumns(CompanionPetPresentation.Summary(_pet!, _locale), false) + 4),
            Math.Max(20, _window.Frame.Width - 4));
        CloseOverlay();
        if (_window.Frame.Height < 18)
        {
            ShowMessage(CompanionPetPresentation.Summary(_pet, _locale)
                + "\n/pet pet · /pet show · /pet hide\n/pet rename <name> · /pet reduced");
            if (_overlay is not null) _overlay.Width = Dim.Func(_ => CardWidth());
            return;
        }
        bool Full() => _window.Frame.Height >= 26 && CompanionVisual.Effective is "graphics" or "unicode";
        int ControlsY() => Full() ? 13 : 5;
        var frame = OverlayFrame(CompanionPetPresentation.Word("pet", _locale), 18);
        frame.Id = "companion-detail";
        // This small card follows its contents instead of taking 80% of the terminal.
        frame.Width = Dim.Func(_ => CardWidth());
        frame.Height = Dim.Func(_ => Full() ? 22 : 14);
        _petDetail = new PixelWidgetView { Id = "companion-detail-renderer", X = 2, Y = 1,
            Width = Dim.Fill(2), Height = Dim.Func(_ => Full() ? 12 : 4) };
        _petDetail.ActionInvoked += () => RunPetAction("pet", false); frame.Add(_petDetail);
        var name = new TextField { Id = "companion-name", X = 2, Y = Pos.Func(_ => ControlsY()),
            Width = Dim.Func(_ => Math.Min(24, Math.Max(4, frame.Viewport.Width - 18))), Text = _pet.Name ?? "Omni" };
        var rename = new Button { Id = "companion-rename", X = Pos.Right(name) + 2, Y = Pos.Func(_ => ControlsY()), Height = 2,
            Text = _locale == "es" ? "Renombrar" : "Rename" };
        rename.Accepted += (_, _) => RunPetAction("rename " + name.Text, false);
        var visibility = new Button { Id = "companion-visible", X = 2, Y = Pos.Func(_ => ControlsY() + 2), Height = 2,
            Text = PetVisibilityLabel() };
        visibility.Accepted += (_, _) => { RunPetAction(_pet?.Preferences.Visible == true ? "hide" : "show", false);
            visibility.Text = PetVisibilityLabel(); };
        var motion = new Button { Id = "companion-motion", X = Pos.Right(visibility) + 2,
            Y = Pos.Func(_ => ControlsY() + 2), Height = 2,
            Text = _pet.Preferences.ReducedMotion ? "/pet motion" : "/pet reduced" };
        motion.Accepted += (_, _) => { RunPetAction(_pet?.Preferences.ReducedMotion == true ? "motion" : "reduced", false);
            motion.Text = _pet?.Preferences.ReducedMotion == true ? "/pet motion" : "/pet reduced"; };
        frame.Add(name, rename, visibility, motion);
        var mode = new Button { Id = "companion-mode", X = 2, Y = Pos.Func(_ => ControlsY() + 4), Height = 2,
            Text = (_locale == "es" ? "Vista: " : "View: ") + _pet.Preferences.RenderMode + "/" + CompanionVisual.Effective };
        mode.Accepted += (_, _) => ShowPetVisualSettings(); frame.Add(mode);
        var close = new Button { Id = "companion-close", X = 2, Y = Pos.AnchorEnd(2), Height = 2,
            Text = _locale == "es" ? "Cerrar" : "Close" };
        close.Accepted += (_, _) => { _petDetail = null; CloseOverlay(); }; frame.Add(close);
        var play = new Button { Id = "companion-play", X = Pos.Right(close) + 2, Y = Pos.AnchorEnd(2), Height = 2,
            Text = _petPlayground.Pose(PetPlayWidth).Outside
                ? _locale == "es" ? "Volver al panel" : "Back to panel"
                : _locale == "es" ? "Salir a jugar" : "Go play" };
        play.Accepted += (_, args) => { args.Handled = true;
            RunPetAction(_petPlayground.Pose(PetPlayWidth).Outside ? "home" : "play", false); };
        frame.Add(play);
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); frame.SetFocus(); RenderCompanion();
    }
    private string PetVisibilityLabel() => _pet?.Preferences.Visible == true
        ? _locale == "es" ? "Ocultar mascota" : "Hide companion"
        : _locale == "es" ? "Mostrar mascota" : "Show companion";
}
