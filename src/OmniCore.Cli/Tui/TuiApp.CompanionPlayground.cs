using System.Drawing;
using OmniCore.Client;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private readonly CompanionPetPlayground _petPlayground = new();
    private CompanionRunnerView? _petRunner;
    private Button? _petRunnerHide;
    private readonly CompanionTypingChase _petTyping = new();
    private CompanionLetterView? _petLetter;
    private int _petDraftLength;
    private bool _petTypingPending;
    private Point? _petRunnerPlacement, _petLetterPlacement;
    private void PausePetForReading()
    {
        _petQuietUntil = DateTimeOffset.UtcNow.AddMilliseconds(300);
        // A quick trip from the editor to the history and back can happen between ticks.
        // Cancel the letter chase at the reading gesture rather than waiting for a timer.
        _petTypingPending = false; _petTyping.Clear();
    }
    internal bool CompanionTypingActive => _petTyping.Active;
    internal View? CompanionLetter => _petLetter;
    internal View? CompanionRunner => _petRunner;
    internal long CompanionPlayFrame => _petPlayground.Frame;
    private bool PetPlaygroundEnabled() => _pet?.Discovered == true && _pet.Preferences.Visible && _petConnected
        && CompanionVisual.Effective is "unicode" or "graphics"
        && !_petPlayground.AtHome && !_pet.Preferences.ReducedMotion && _animationPreferences.Enabled
        && !_animationPreferences.ReducedMotion && !_animationPreferences.Ascii
        && (_window?.Frame.Height ?? 0) >= 24 && (_window?.Frame.Width ?? 0) - _mainColumnInset >= 48;
    private bool PetCanPlay() => PetPlaygroundEnabled() && _overlay is null && _activeInteraction is null && _commandHelper is null
        && !(_pet?.Reaction is { AnimationId: "pet" } reaction && reaction.Until > DateTimeOffset.UtcNow);
    private int PetPlayWidth => Math.Max(0, (_conversation?.Viewport.Width ?? 0) - 1);
    private int PetPlayHeight => _conversation is null ? 0 : Math.Min(_conversation.Viewport.Height,
        Math.Max(CompanionPetPlayground.SpriteHeight, _conversation.Lines - _conversation.Viewport.Y));
    private int PetHeaderEndPadding => _petRunnerHide?.Visible == true ? 24 : 2;
    private int PetTypingTargetX => Math.Clamp((_composer?.Cursor.Position?.X ?? PetPlayOrigin.X) - PetPlayOrigin.X - 1,
        0, Math.Max(0, PetPlayWidth - 2));
    // The chase may cross the editor's top border, but never paints any draft/caret cells.
    private int PetChaseHeight => Math.Max(CompanionPetPlayground.SpriteHeight,
        (_composer?.ViewportToScreen(Point.Empty).Y ?? 0) - _conversation!.ViewportToScreen(Point.Empty).Y);
    private int PetOverlayHeight => _petTyping.Active ? PetChaseHeight : PetPlayHeight;
    private CompanionPlayPose PetPlayPose => _petTyping.Active
        ? _petTyping.Pose(PetTypingTargetX, PetPlayWidth, PetChaseHeight, _petPlayground.Frame)
        : _petPlayground.Pose(PetPlayWidth, PetPlayHeight);
    private void ObservePetTyping(bool userEdit)
    {
        var length = _composer?.Text?.Length ?? 0;
        _petTypingPending = userEdit && length > _petDraftLength && _composer!.HasFocus && PetCanPlay();
        _petDraftLength = length;
        if (!_petTypingPending) _petTyping.Clear();
    }
    private void UpdatePetTyping()
    {
        if (!PetCanPlay() || _composer?.HasFocus != true)
        {
            _petTypingPending = false; _petTyping.Clear();
            if (_petLetter?.Visible == true) _petLetter.Visible = false;
            return;
        }
        if (!_petTypingPending) return;
        _petTypingPending = false;
        var point = _composer.InsertionPoint;
        var line = _composer.GetLine(point.Y);
        if (point.X <= 0 || point.X > line.Count) return;
        var glyph = line[point.X - 1].Grapheme;
        var columns = Terminal.Gui.Text.StringExtensions.GetColumns(glyph, false);
        if (string.IsNullOrWhiteSpace(glyph) || glyph.Length > 16 || columns is < 1 or > 2
            || glyph.Any(char.IsControl)) return;
        _petTyping.Follow(glyph, PetPlayPose.X);
        RenderPetPlayground();
    }
    private Point PetPlayOrigin
    {
        get
        {
            var chat = _conversation!.ViewportToScreen(Point.Empty);
            var window = _window!.ViewportToScreen(Point.Empty);
            return new(chat.X - window.X, chat.Y - window.Y);
        }
    }
    private IReadOnlyList<Rectangle> PetWideGlyphs(Rectangle region)
    {
        var result = new List<Rectangle>();
        if (_conversation is not { } chat) return result;
        var origin = chat.ViewportToScreen(Point.Empty);
        var viewport = new Rectangle(origin, chat.Viewport.Size);
        region.Intersect(viewport);
        for (var y = region.Top; y < region.Bottom; y++)
        {
            var row = y - origin.Y + chat.Viewport.Y;
            if (row < 0 || row >= chat.Lines) continue;
            var x = origin.X - chat.Viewport.X;
            foreach (var cell in chat.GetLine(row))
            {
                var width = cell.Grapheme == "\t" ? Math.Max(1, chat.TabWidth - (x - origin.X + chat.Viewport.X) % Math.Max(1, chat.TabWidth))
                    : Math.Max(1, Terminal.Gui.Text.StringExtensions.GetColumns(cell.Grapheme, false));
                if (width == 2 && x < region.Right && x + width > region.Left) result.Add(new(x, y, width, 1));
                x += width;
                if (x >= region.Right) break;
            }
        }
        return result;
    }
    private void RenderPetPlayground()
    {
        if (_window is null || _conversationFrame is null) return;
        var enabled = PetPlaygroundEnabled();
        if (!PetCanPlay() || _composer?.HasFocus != true) _petTyping.Clear();
        if (enabled && _petRunner is null)
        {
            _petRunner = new() { Id = "companion-runner", Visible = false,
                X = Pos.Func(_ => PetPlayOrigin.X + PetPlayPose.X),
                Y = Pos.Func(_ => PetPlayOrigin.Y + PetPlayPose.Y),
                ClipScreen = () => new Rectangle(_conversation!.ViewportToScreen(Point.Empty), new Size(PetPlayWidth, PetOverlayHeight)),
                UnderlyingWideGlyphs = PetWideGlyphs };
            _petLetter = new() { Id = "companion-letter", Visible = false,
                X = Pos.Func(_ => PetPlayOrigin.X + PetTypingTargetX),
                Y = Pos.Func(_ => PetPlayOrigin.Y + PetChaseHeight - 1 - (int)(_petPlayground.Frame % 2)) };
            _petRunnerHide = new() { Id = "companion-roam-hide", Y = 0, Height = 2, Width = 20,
                X = Pos.Func(_ => Math.Max(2, _window.Frame.Width - _mainColumnInset - 21)),
                Text = _locale == "es" ? "Ocultar mascota" : "Hide companion", Visible = false };
            _petRunnerHide.Accepted += (_, args) => { args.Handled = true; RunPetAction("hide", false); };
            _window.Add(_petRunner, _petLetter, _petRunnerHide); ApplyTheme(_petRunnerHide);
        }
        if (_petRunner is null) return;
        var pose = PetPlayPose;
        var outside = PetCanPlay() && pose.Outside;
        _petRunner.Visible = outside;
        _petLetter!.Visible = outside && _petTyping.Active;
        if (_petLetter.Visible)
        {
            var noColor = PetNoColor;
            _petLetter.Glyph = _petTyping.Glyph!;
            _petLetter.Width = Terminal.Gui.Text.StringExtensions.GetColumns(_petLetter.Glyph, false);
            _petLetter.Ink = new(noColor ? Terminal.Gui.Drawing.Color.None : _theme.Map(new Terminal.Gui.Drawing.Color(_theme.Accent)),
                noColor ? Terminal.Gui.Drawing.Color.None : _theme.Map(new Terminal.Gui.Drawing.Color(_theme.Background)));
            var placement = new Point(PetPlayOrigin.X + PetTypingTargetX,
                PetPlayOrigin.Y + PetChaseHeight - 1 - (int)(_petPlayground.Frame % 2));
            if (_petLetterPlacement != placement)
            { _petLetterPlacement = placement; _petLetter.SetNeedsLayout(); }
            _petLetter.SetNeedsDraw();
        }
        else _petLetter.Glyph = "";
        var hideVisible = enabled && _overlay is null;
        if (_petRunnerHide!.Visible != hideVisible)
        {
            _petRunnerHide.Visible = hideVisible;
            if (_header is not null) _header.Width = Dim.Fill(_mainColumnInset + PetHeaderEndPadding);
        }
        if (outside)
        {
            _petRunner.Render(_pet!, pose, _theme, PetNoColor);
            var placement = new Point(PetPlayOrigin.X + pose.X, PetPlayOrigin.Y + pose.Y);
            if (_petRunnerPlacement != placement)
            { _petRunnerPlacement = placement; _petRunner.SetNeedsLayout(); }
        }
    }
}
