using System.Collections.ObjectModel;
using OmniCore.Client;
using OmniCore.Protocol;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private string PaletteText(string key) => _localization.Resolve("palette." + key);

    private IReadOnlyList<CommandPaletteEntry> PaletteEntries(KeyBindingContext context) =>
        CommandPalette.Build(_client.Query("commands", CancellationToken.None)?.Json, _localization, context, HasActiveModelWork);

    private void ShowCommandPalette()
    {
        // Editing overlays and server-provided approvals keep their focus and unsaved state.
        if (_window is null || _overlay is not null || _activeInteraction is not null) return;
        var originContext = KeyboardContext();
        var session = _cursorSession; var run = _sidebarProjection.Snapshot?.RunId;
        var entries = PaletteEntries(originContext);
        var bindings = entries.ToDictionary(entry => entry.Id,
            entry => CommandPalette.Bindings(entry, _keyBindings.Service, originContext));
        var frame = OverlayFrame(PaletteText("title"), _window.Frame.Height - 3);
        frame.Id = "global-command-palette"; frame.Width = Dim.Percent(94); frame.Height = Dim.Fill(2);
        var search = new TextField { Id = "palette-search", X = 2, Y = 2, Width = Dim.Fill(4), Height = 1 };
        var columns = new Label { Id = "palette-columns", X = 2, Y = 3, Width = Dim.Fill(4), Height = 1 };
        var list = new ListView { Id = "palette-list", X = 2, Y = 4, Width = Dim.Fill(4), Height = Dim.Fill(10),
            ShowMarks = false, ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar };
#pragma warning disable CS0618 // Pinned Terminal.Gui text inspection, matching other TUI screens.
        var details = new TextView { Id = "palette-details", X = 2, Y = Pos.AnchorEnd(9), Width = Dim.Fill(4), Height = 3,
            ReadOnly = true, WordWrap = true, ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar };
#pragma warning restore CS0618
        var argumentLabel = new Label { Id = "palette-arguments-label", X = 2, Y = Pos.AnchorEnd(5), Width = Dim.Fill(4), Height = 1 };
        var arguments = new TextField { Id = "palette-arguments", X = 2, Y = Pos.AnchorEnd(4), Width = Dim.Fill(4), Height = 1 };
        var execute = new Button { Id = "palette-execute", X = 2, Y = Pos.AnchorEnd(2), Text = PaletteText("execute") };
        var close = new Button { Id = "palette-close", X = Pos.AnchorEnd(16), Y = Pos.AnchorEnd(2), Text = PaletteText("close") };
        CommandPaletteEntry[] visible = [];
        var rebuilding = false;
        var renderedWidth = 0;
        CommandPaletteEntry? Selected() => list.SelectedItem is int at && at >= 0 && at < visible.Length ? visible[at] : null;
        string Shortcuts(CommandPaletteEntry entry) => string.Join(", ", bindings[entry.Id]
            .Select(binding => binding.Definition.Keys));
        string Source(CommandPaletteEntry entry) => entry.Source == "Core" ? _localization.Resolve("uiCompletion.command.coreSource") : entry.Source;
        static string Pad(string text, int width)
        {
            var fitted = FitText(text, width);
            return fitted + new string(' ', Math.Max(0, width - fitted.GetColumns(false)));
        }
        void UpdateSelection()
        {
            var entry = Selected(); execute.Enabled = entry?.Enabled == true;
            arguments.Visible = argumentLabel.Visible = entry?.Target.Kind == KeyBindingTargetKind.Command;
            arguments.Enabled = entry?.Target.Kind == KeyBindingTargetKind.Command && entry.Enabled;
            argumentLabel.Text = PaletteText("arguments") + ": " + string.Join(" ", entry?.ArgumentHints ?? []);
            details.Text = entry is null ? PaletteText("none") : string.Join("\n", entry.Description.Length > 0 ? entry.Description : entry.Title,
                entry.Target.Id + " · " + PaletteText("source") + ": " + Source(entry)
                    + (entry.ExternalDescription ? " · " + _localization.Resolve("catalog.external") : ""),
                PaletteText(entry.Enabled ? "ready" : "blocked") + (entry.DisabledReason is { Length: > 0 } reason ? " · " + reason : "")
                    + " · " + PaletteText("shortcut") + ": " + string.Join(", ", bindings[entry.Id]
                        .Select(binding => binding.Definition.Keys + " (" + binding.Definition.SourceId + ")")));
        }
        void RenderRows(string? keep = null)
        {
            // Titles, qualified identity, shortcut and source stay visible without executing search text.
            var width = Math.Max(24, list.Viewport.Width > 0 ? list.Viewport.Width - 1 : _window.Frame.Width - 12);
            var nameWidth = Math.Max(10, width - 32);
            columns.Text = Pad(PaletteText("item"), nameWidth) + "  " + Pad(PaletteText("shortcut"), 16) + "  " + PaletteText("source");
            rebuilding = true;
            list.SetSource(new ObservableCollection<string>(visible.Select(entry =>
                Pad((entry.Enabled ? "  " : "· ") + entry.Title, nameWidth)
                    + "  " + Pad(Shortcuts(entry), 16) + "  " + FitText(entry.Source, 10))));
            if (visible.Length > 0) list.SelectedItem = Math.Max(0, Array.FindIndex(visible, entry => entry.Id == keep));
            rebuilding = false;
            list.CanFocus = visible.Length > 0; UpdateSelection();
        }
        void Filter()
        {
            visible = CommandPalette.Search(entries, search.Text.ToString()).ToArray(); RenderRows();
        }
        list.SubViewsLaidOut += (_, _) =>
        {
            if (rebuilding || renderedWidth == list.Viewport.Width) return;
            renderedWidth = list.Viewport.Width; RenderRows(Selected()?.Id);
        };
        void Accept()
        {
            if (!ReferenceEquals(_overlay, frame) || Selected() is not { Enabled: true } selected) return;
            PollEvents();
            if (!ReferenceEquals(_overlay, frame)) return;
            if (_activeInteraction is not null || session != _cursorSession || run != _sidebarProjection.Snapshot?.RunId)
            { details.Text = PaletteText("changed"); return; }
            var fresh = PaletteEntries(KeyboardContext()).SingleOrDefault(entry => entry.Id == selected.Id);
            if (fresh is not { Enabled: true } || fresh.Source != selected.Source || fresh.SourceFingerprint != selected.SourceFingerprint
                || fresh.Description != selected.Description || !(fresh.ArgumentHints ?? []).SequenceEqual(selected.ArgumentHints ?? []))
            { details.Text = PaletteText("changed"); return; }
            CommandInvocation? command = null;
            if (selected.Target.Kind == KeyBindingTargetKind.Command)
            {
                if (!CommandLineParser.TryParse("/" + selected.Target.Id + " " + arguments.Text, out command) || command is null)
                { details.Text = PaletteText("invalid_arguments"); return; }
                command = command with { Name = selected.Target.Id, InvocationOrigin = "Palette" };
            }
            CloseOverlay();
            if (command is not null) { RunHostCommand(command); PollEvents(); }
            else new TuiActionHandler(this).ExecuteAsync(new(selected.Target.Id, selected.Target.Arguments, "Palette"),
                new(_client, _localization, CliApp.PermissionsListing), CancellationToken.None).GetAwaiter().GetResult();
        }
        void Move(int delta)
        {
            if (visible.Length == 0) return;
            list.SelectedItem = ((list.SelectedItem ?? 0) + delta + visible.Length) % visible.Length;
            list.EnsureSelectedItemVisible();
        }
        void Navigate(Key key)
        {
            if (key.KeyCode == KeyCode.Enter) { key.Handled = true; Accept(); }
            else if (key.KeyCode is KeyCode.CursorDown or KeyCode.CursorUp)
            { key.Handled = true; Move(key.KeyCode == KeyCode.CursorDown ? 1 : -1); }
        }
        search.TextChanged += (_, _) => { arguments.Text = ""; Filter(); };
        list.ValueChanged += (_, _) => { if (!rebuilding) { arguments.Text = ""; UpdateSelection(); } };
        search.KeyDown += (_, key) => Navigate(key); list.KeyDown += (_, key) => Navigate(key);
        arguments.KeyDown += (_, key) => { if (key.KeyCode == KeyCode.Enter) { key.Handled = true; Accept(); } };
        list.Accepted += (_, e) => { e.Handled = true; Accept(); };
        execute.Accepted += (_, e) => { e.Handled = true; Accept(); };
        close.Accepted += (_, e) => { e.Handled = true; CloseOverlay(); };
        frame.Add(new Label { Id = "palette-search-label", X = 2, Y = 1, Width = Dim.Fill(4), Height = 1, Text = PaletteText("search") },
            search, columns, list, details, argumentLabel, arguments, execute, close,
            new Label { Id = "palette-help", X = 2, Y = Pos.AnchorEnd(1), Width = Dim.Fill(4), Height = 1, Text = PaletteText("help") });
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); Filter(); search.SetFocus();
    }
}
