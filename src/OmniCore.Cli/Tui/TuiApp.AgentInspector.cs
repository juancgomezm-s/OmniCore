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
    private string AgentInspectorText(string key) => _localization.Resolve("agentInspector." + key);

    private void ShowAgentInspector()
    {
        if (_window is null || _overlay is not null || _activeInteraction is not null) return;
        var session = _cursorSession; var run = _sidebarProjection.Snapshot?.RunId;
        var projection = new AgentInspectorProjection(session, run);
        var frame = OverlayFrame(AgentInspectorText("title"), _window.Frame.Height - 3);
        frame.Id = "agent-inspector"; frame.Width = Dim.Percent(94); frame.Height = Dim.Fill(2);
        var capture = new Label { Id = "agent-inspector-capture", X = 2, Y = 1, Width = Dim.Fill(4), Height = 1 };
        var search = new TextField { Id = "agent-inspector-search", X = 2, Y = 3, Width = Dim.Fill(4), Height = 1 };
        var columns = new Label { Id = "agent-inspector-columns", X = 2, Y = 4, Width = Dim.Fill(4), Height = 1 };
        var list = new ListView { Id = "agent-inspector-list", X = 2, Y = 5, Width = Dim.Fill(4), Height = Dim.Percent(25),
            ShowMarks = false, ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar };
#pragma warning disable CS0618 // Read-only diagnostic details, matching existing TUI inspection surfaces.
        var details = new TextView { Id = "agent-inspector-details", X = 2, Y = Pos.Bottom(list) + 1, Width = Dim.Fill(4), Height = Dim.Fill(5),
            ReadOnly = true, WordWrap = true, ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar };
#pragma warning restore CS0618
        var notice = new Label { Id = "agent-inspector-notice", X = 2, Y = Pos.AnchorEnd(4), Width = Dim.Fill(4), Height = 1 };
        var refresh = new Button { Id = "agent-inspector-refresh", X = 2, Y = Pos.AnchorEnd(3), Text = AgentInspectorText("refresh") };
        var close = new Button { Id = "agent-inspector-close", X = Pos.AnchorEnd(16), Y = Pos.AnchorEnd(3), Text = Pref("back") };
        IReadOnlyList<AgentLaneSnapshot> visible = [];
        var rebuilding = false;
        var renderedWidth = 0;
        string? SelectedId() => list.SelectedItem is int at && at >= 0 && at < visible.Count ? visible[at].LaneId : null;
        void Select()
        {
            var selected = SelectedId();
            details.Text = selected is null || projection.Snapshot is not { } snapshot ? ""
                : AgentInspectorPresentation.Describe(snapshot, visible.Single(lane => lane.LaneId == selected), _localization);
        }
        void Filter(string? keep = null)
        {
            visible = projection.Search(search.Text);
            var width = Math.Max(24, list.Viewport.Width > 0 ? list.Viewport.Width - 1 : _window.Frame.Width - 12);
            var stateWidth = Math.Min(22, width / 3);
            string Pad(string value) => FitText(value, stateWidth) + new string(' ', Math.Max(0, stateWidth - FitText(value, stateWidth).GetColumns(false)));
            columns.Text = "  " + Pad(AgentInspectorText("state")) + "  " + AgentInspectorText("objective");
            rebuilding = true;
            list.SetSource(new ObservableCollection<string>(visible.Select(lane => "  "
                + Pad(AgentInspectorPresentation.State(lane.LaneState, _localization)) + "  "
                + FitText(string.Join(" ", lane.Objective.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), Math.Max(1, width - stateWidth - 4)))));
            if (visible.Count > 0) list.SelectedItem = Math.Max(0, visible.ToList().FindIndex(lane => lane.LaneId == keep));
            rebuilding = false; list.CanFocus = visible.Count > 0;
            capture.Text = projection.Snapshot is { } snapshot
                ? _localization.Resolve("agentInspector.capture", "sequence", snapshot.BasedOnJournalSequence.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : AgentInspectorText("status." + projection.Status);
            notice.Text = search.Text.Length > 256 ? AgentInspectorText("query_limit")
                : projection.Status != AgentInspectorStatus.Ready ? AgentInspectorText("status." + projection.Status)
                : projection.Snapshot!.Lanes.Count > AgentInspectorProjection.MaximumLanes ? AgentInspectorText("lane_limit")
                : visible.Count == 0 ? AgentInspectorText("empty") : "";
            Select();
        }
        void Refresh()
        {
            if (!ReferenceEquals(_overlay, frame)) return;
            var selected = SelectedId();
            try
            {
                PollEvents();
                if (!ReferenceEquals(_overlay, frame)) return;
                if (_activeInteraction is not null || session != _cursorSession || run != _sidebarProjection.Snapshot?.RunId)
                {
                    projection.Invalidate(AgentInspectorStatus.ScopeChanged); Filter(); refresh.Enabled = false; return;
                }
                projection.Apply(AgentsJson.Decode(_client.Query("agents", CancellationToken.None)?.Json ?? "null"), _lastSequence);
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
            { projection.Invalidate(); }
            Filter(selected);
        }
        void Navigate(Key key)
        {
            if (key.KeyCode == KeyCode.Enter) { key.Handled = true; if (visible.Count > 0) details.SetFocus(); }
            else if (key.KeyCode is KeyCode.CursorUp or KeyCode.CursorDown && visible.Count > 0)
            {
                key.Handled = true;
                list.SelectedItem = ((list.SelectedItem ?? 0) + (key.KeyCode == KeyCode.CursorUp ? -1 : 1) + visible.Count) % visible.Count;
                list.EnsureSelectedItemVisible();
            }
        }
        search.TextChanged += (_, _) => Filter();
        search.KeyDown += (_, key) => Navigate(key); list.KeyDown += (_, key) => Navigate(key);
        list.ValueChanged += (_, _) => { if (!rebuilding) Select(); };
        list.SubViewsLaidOut += (_, _) =>
        {
            if (rebuilding || renderedWidth == list.Viewport.Width) return;
            renderedWidth = list.Viewport.Width; Filter(SelectedId());
        };
        list.Accepted += (_, e) => { e.Handled = true; if (visible.Count > 0) details.SetFocus(); };
        refresh.Accepted += (_, e) => { e.Handled = true; Refresh(); };
        close.Accepted += (_, e) => { e.Handled = true; CloseOverlay(); };
        frame.Add(capture, new Label { X = 2, Y = 2, Width = Dim.Fill(4), Height = 1, Text = Pref("search") },
            search, columns, list, details, notice, refresh, close,
            new Label { X = 2, Y = Pos.AnchorEnd(1), Width = Dim.Fill(4), Height = 1, Text = AgentInspectorText("help") });
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); Refresh(); search.SetFocus();
    }
}
