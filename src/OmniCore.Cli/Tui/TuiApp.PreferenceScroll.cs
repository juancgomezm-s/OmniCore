using System.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private static void ConfigurePreferenceScroll(View panel, int contentHeight)
    {
        panel.CanFocus = true;
        panel.VerticalScrollBar.VisibilityMode = ScrollBarVisibilityMode.Auto;
        void SizeContent()
        {
            var size = new Size(Math.Max(1, panel.Viewport.Width), contentHeight);
            if (panel.GetContentSize() != size) panel.SetContentSize(size);
        }
        panel.ViewportChanged += (_, _) => SizeContent();
        SizeContent();
        foreach (var child in panel.SubViews)
            child.HasFocusChanged += (_, _) =>
            {
                if (!child.HasFocus) return;
                var viewport = panel.Viewport;
                // Initial focus may be assigned before the overlay's first layout.
                // A zero-height viewport cannot reveal a field; scrolling now would
                // hide the opening summary after the real size becomes available.
                if (viewport.Height <= 0 || child.Frame.Height <= 0) return;
                var top = child.Frame.Top < viewport.Top ? child.Frame.Top
                    : child.Frame.Bottom > viewport.Bottom ? child.Frame.Bottom - viewport.Height : viewport.Top;
                if (top != viewport.Top)
                    panel.Viewport = new Rectangle(viewport.X, Math.Max(0, top), viewport.Width, viewport.Height);
            };
    }
}
