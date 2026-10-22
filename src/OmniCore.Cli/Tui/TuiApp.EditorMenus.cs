using System.Runtime.CompilerServices;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private readonly ConditionalWeakTable<View, object> _editorMenuSubscriptions = new();

#pragma warning disable CS0618 // TextView is the pinned editor used throughout the TUI.
    private void ConfigureEditorContextMenu(View view)
    {
        if (view is not (TextView or TextField)) return;
        if (_editorMenuSubscriptions.TryAdd(view, new object()))
        {
            view.Initialized += (_, _) => RefreshEditorContextMenu(view);
            // TextView creates its menu after raising Initialized in base.EndInit.
            view.SubViewsLaidOut += (_, _) => RefreshEditorContextMenu(view);
            // TextField recreates its context menu whenever it gains focus.
            view.HasFocusChanged += (_, _) => RefreshEditorContextMenu(view);
        }
        RefreshEditorContextMenu(view);
    }

    private void RefreshEditorContextMenu(View view)
    {
        var menu = view switch
        {
            TextView text => text.ContextMenu,
            TextField field => field.ContextMenu,
            _ => null,
        };
        if (menu?.Root is null) return;
        foreach (var item in menu.Root.SubViews.OfType<MenuItem>())
        {
            var (resource, preferred) = item.Command switch
            {
                Command.SelectAll => ("select_all", Key.A.WithCtrl),
                Command.DeleteAll => ("delete_all", Key.Delete.WithCtrl.WithShift),
                Command.Copy => ("copy", Key.C.WithCtrl),
                Command.Cut => ("cut", Key.X.WithCtrl),
                Command.Paste => ("paste", Key.V.WithCtrl),
                Command.Undo => ("undo", Key.Z.WithCtrl),
                Command.Redo => ("redo", Key.Y.WithCtrl),
                _ => (null, Key.Empty),
            };
            if (resource is null) continue;
            var title = _localization.Resolve("shell.editor." + resource);
            if (item.Title != title) item.Title = title;
            if (item.HelpText.Length > 0) item.HelpText = "";
            // Show only a shortcut actually bound on this editor, including platform overrides.
            var key = view.KeyBindings.GetCommands(preferred).Contains(item.Command)
                ? preferred : view.KeyBindings.GetFirstFromCommands(item.Command) ?? Key.Empty;
            if (item.Key != key) item.Key = key;
        }
    }
#pragma warning restore CS0618
}
