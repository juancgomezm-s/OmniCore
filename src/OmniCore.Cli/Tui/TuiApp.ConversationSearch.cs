using System.Collections.ObjectModel;
using System.Drawing;
using OmniCore.Client;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

public sealed partial class TuiApp
{
    private string SearchText(string key) => _localization.Resolve("search." + key);

    private void ShowConversationSearch()
    {
        if (_window is null || _overlay is not null || _activeInteraction is not null) return;
        var session = _cursorSession; var run = _sidebarProjection.Snapshot?.RunId;
        var blocks = DisplayedConversationBlocks().ToArray();
        var frame = OverlayFrame(SearchText("title"), _window.Frame.Height - 3);
        frame.Id = "conversation-search"; frame.Width = Dim.Percent(94); frame.Height = Dim.Fill(2);
        var search = new TextField { Id = "conversation-search-query", X = 2, Y = 3, Width = Dim.Fill(4), Height = 1 };
        var list = new ListView { Id = "conversation-search-results", X = 2, Y = 5, Width = Dim.Fill(4), Height = Dim.Fill(10),
            ShowMarks = false, ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar };
#pragma warning disable CS0618 // Read-only original snippet, matching existing TUI inspection surfaces.
        var details = new TextView { Id = "conversation-search-details", X = 2, Y = Pos.AnchorEnd(9), Width = Dim.Fill(4), Height = 5,
            ReadOnly = true, WordWrap = true, ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar };
#pragma warning restore CS0618
        var status = new Label { Id = "conversation-search-status", X = 2, Y = 4, Width = Dim.Fill(4), Height = 1 };
        var open = new Button { Id = "conversation-search-open", X = 2, Y = Pos.AnchorEnd(3), Text = SearchText("open") };
        var close = new Button { Id = "conversation-search-close", X = Pos.AnchorEnd(16), Y = Pos.AnchorEnd(3), Text = Pref("back") };
        var notice = new Label { Id = "conversation-search-notice", X = 2, Y = Pos.AnchorEnd(4), Width = Dim.Fill(4), Height = 1 };
        ConversationSearchResult result = new([]);
        var rebuilding = false;
        ConversationSearchHit? Selected() => list.SelectedItem is int at && at >= 0 && at < result.Hits.Count ? result.Hits[at] : null;
        void Select()
        {
            var hit = Selected(); open.Enabled = hit is not null;
            details.Text = hit is null ? "" : SearchText("origin") + ": " + SearchText("session") + " " + session
                + "\n" + SearchText("message") + " " + (hit.BlockIndex + 1) + " · " + SearchText("role." + hit.Role) + " · " + hit.BlockId
                + "\n" + (hit.SnippetOffset > 0 ? "…" : "") + hit.Snippet
                + (hit.SnippetOffset + hit.Snippet.Length < blocks[hit.BlockIndex].Text.Length ? "…" : "");
            notice.Text = hit?.Role == ConversationRole.Assistant ? SearchText("claim") : "";
        }
        void Filter()
        {
            result = ConversationSearch.Search(blocks, search.Text);
            rebuilding = true;
            list.SetSource(new ObservableCollection<string>(result.Hits.Select(hit =>
                "  " + (hit.BlockIndex + 1).ToString().PadLeft(3) + "  " + SearchText("role." + hit.Role) + " · "
                + string.Join(" ", hit.Snippet.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))));
            if (result.Hits.Count > 0) list.SelectedItem = 0;
            rebuilding = false;
            status.Text = result.QueryTooLong ? SearchText("query_limit") : result.Limited ? SearchText("limited")
                : result.Hits.Count == 1 ? SearchText("count_one")
                : result.Hits.Count > 0 ? _localization.Resolve("search.count", "count", result.Hits.Count.ToString())
                : string.IsNullOrWhiteSpace(search.Text) ? SearchText("prompt") : SearchText("empty");
            list.CanFocus = result.Hits.Count > 0; Select();
        }
        void Accept()
        {
            if (!ReferenceEquals(_overlay, frame) || Selected() is not { } hit) return;
            PollEvents();
            if (!ReferenceEquals(_overlay, frame)) return;
            var current = DisplayedConversationBlocks();
            if (_activeInteraction is not null || session != _cursorSession || run != _sidebarProjection.Snapshot?.RunId
                || hit.BlockIndex >= current.Count || current[hit.BlockIndex].Id != hit.BlockId
                || current[hit.BlockIndex].Text != blocks[hit.BlockIndex].Text)
            { notice.Text = SearchText("changed"); return; }
            CloseOverlay();
            if (_conversation is null) return;
            _followConversationEnd = false; PausePetForReading(); RenderConversation();
            var width = Math.Max(1, _conversation.Viewport.Width - 1);
            var row = hit.BlockIndex == 0 ? 0 : ConversationPresentation.RenderCards(current.Take(hit.BlockIndex).ToArray(),
                _locale, width, compactExamples: !_showMarkdownExamples).Count + 1;
            _conversation.SetFocus();
            _conversation.InsertionPoint = new Point(0, row);
            _conversation.ScrollTo(new Point(0, row));
        }
        void Navigate(Key key)
        {
            if (key.KeyCode == KeyCode.Enter) { key.Handled = true; Accept(); }
            else if (key.KeyCode is KeyCode.CursorUp or KeyCode.CursorDown && result.Hits.Count > 0)
            {
                key.Handled = true;
                list.SelectedItem = ((list.SelectedItem ?? 0) + (key.KeyCode == KeyCode.CursorUp ? -1 : 1) + result.Hits.Count) % result.Hits.Count;
                list.EnsureSelectedItemVisible();
            }
        }
        search.TextChanged += (_, _) => Filter();
        search.KeyDown += (_, key) => Navigate(key); list.KeyDown += (_, key) => Navigate(key);
        list.ValueChanged += (_, _) => { if (!rebuilding) Select(); };
        list.Accepted += (_, e) => { e.Handled = true; Accept(); };
        open.Accepted += (_, e) => { e.Handled = true; Accept(); };
        close.Accepted += (_, e) => { e.Handled = true; CloseOverlay(); };
        frame.Add(new Label { X = 2, Y = 1, Width = Dim.Fill(4), Height = 1, Text = SearchText("scope") },
            new Label { X = 2, Y = 2, Width = Dim.Fill(4), Height = 1, Text = Pref("search") },
            search, status, list, details, notice, open, close,
            new Label { X = 2, Y = Pos.AnchorEnd(1), Width = Dim.Fill(4), Height = 1, Text = SearchText("help") });
        ApplyTheme(frame); _overlay = frame; _window.Add(frame); Filter(); search.SetFocus();
    }
}
