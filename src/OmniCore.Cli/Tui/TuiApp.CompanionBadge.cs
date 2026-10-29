using Terminal.Gui.ViewBase;
using Terminal.Gui.Drawing;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private bool CompanionNeedsAttention => !_companionQuiet && _companionPendingCount > 0 && DateTimeOffset.UtcNow < _companionAttentionUntil
        && _pet?.Preferences.ReducedMotion != true && _animationPreferences.Enabled && !_animationPreferences.ReducedMotion;
    private void RenderCompanionBadge()
    {
        if (_window is null) return;
        if (_companionBadge is null)
        {
            _companionBadge = new() { Id = "companion-notice-bubble", Height = 1, Width = 5, CanFocus = false,
                NoDecorations = true, NoPadding = true, ShadowStyle = ShadowStyles.None, Visible = false };
            _companionBadge.Accepted += (_, args) => { args.Handled = true; OpenCompanionHub("chat"); };
            _window.Add(_companionBadge); ApplyTheme(_companionBadge);
            _companionBadge.Margin.Thickness = new(0); _companionBadge.Border.Thickness = new(0); _companionBadge.Padding.Thickness = new(0);
        }
        var anchor = _petRunner?.Visible == true ? (View)_petRunner : _petView;
        var visible = _companionPendingCount > 0 && !_companionQuiet && _pet?.Preferences.Visible == true && _overlay is null
            && _activeInteraction is null && _commandHelper is null && anchor?.Visible == true
            && (anchor == _petRunner || _sidebar?.Visible == true);
        if (_companionBadge.Visible != visible) _companionBadge.Visible = visible;
        if (_petView is not null) _petView.HeadingInset = visible && anchor == _petView ? 6 : 0;
        if (!visible || anchor is null) return;
        var point = anchor.ViewportToScreen(System.Drawing.Point.Empty);
        var origin = _window.ViewportToScreen(System.Drawing.Point.Empty);
        var x = Math.Clamp(point.X - origin.X, 0, Math.Max(0, _window.Viewport.Width - 5));
        var y = Math.Clamp(point.Y - origin.Y, 0, Math.Max(0, _window.Viewport.Height - 1));
        if (_companionBadge.Frame.X != x || _companionBadge.Frame.Y != y) { _companionBadge.X = x; _companionBadge.Y = y; }
        var label = "(" + (_companionPendingCount > 99 ? "99+" : _companionPendingCount.ToString()) + ")";
        if (_companionBadge.Text != label) _companionBadge.Text = label;
    }
}
