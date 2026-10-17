using OmniCore.Client;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

/// <summary>Generic declarative widget renderer. Native wrapping, selection and scrolling keep tall panels usable.</summary>
#pragma warning disable CS0618
internal sealed class SidebarView : TextView
{
    private string? _signature;
    private System.Drawing.Point _sourcePoint;
    private System.Drawing.Point? _mouseDown;
    private bool _dragged;
    internal bool ShowRoleGlyphs { get; set; } = true;
    internal TuiTheme Theme { get; set; } = TuiTheme.All[0];
    private readonly Dictionary<int, WidgetRowModel> _actions = new();
    private readonly Dictionary<string, int> _headings = new();
    internal event Action<WidgetRowModel>? RowActivated;
    internal WidgetRowModel? FocusedAction => _actions.GetValueOrDefault(WordWrap ? _sourcePoint.Y : InsertionPoint.Y);
    internal void SelectWidget(string? id)
    {
        if (id is null || !_headings.TryGetValue(id, out var row)) return;
        FocusSourceRow(row);
    }

    private void FocusSourceRow(int row)
    {
        // Headings/actions use source rows; TextView.InsertionPoint uses wrapped screen rows.
        SetFocus();
        // Setting the cursor to its existing position emits no cursor event. A
        // stale source row after Load must not terminate the first probe at row 0.
        _sourcePoint = System.Drawing.Point.Empty;
        for (var wrapped = 0; wrapped < Lines; wrapped++)
        {
            // TextView only refreshes its unwrapped cursor event during a pending
            // redraw. Ensure each probe observes this position, not the last draw.
            SetNeedsDraw();
            InsertionPoint = new(0, wrapped);
            if ((WordWrap ? _sourcePoint.Y : wrapped) >= row) break;
        }
        ScrollTo(new System.Drawing.Point(0, Math.Max(0, InsertionPoint.Y - 2)));
    }
    public SidebarView()
    {
        ReadOnly = true; WordWrap = true; ScrollBars = true;
        ConversationScrollBarStyle.Attach(VerticalScrollBar, () => false, "#202C3B", () => Theme);
        UnwrappedCursorPositionChanged += (_, point) => _sourcePoint = point;
        KeyDown += (_, key) =>
        {
            if ((key.KeyCode == KeyCode.Tab || key.KeyCode == (KeyCode.Tab | KeyCode.ShiftMask)) && _actions.Count > 0)
            {
                var at = WordWrap ? _sourcePoint.Y : InsertionPoint.Y;
                var previous = key.KeyCode.HasFlag(KeyCode.ShiftMask);
                var rows = _actions.Keys.Order().ToArray();
                var next = previous ? rows.LastOrDefault(row => row < at, -1) : rows.FirstOrDefault(row => row > at, -1);
                if (next < 0) return; // Tab leaves the panel at its final link.
                FocusSourceRow(next); key.Handled = true; return;
            }
            if (key.KeyCode != KeyCode.Enter || !_actions.TryGetValue(WordWrap ? _sourcePoint.Y : InsertionPoint.Y, out var action)) return;
            RowActivated?.Invoke(action); key.Handled = true;
        };
    }
    internal void Render(IReadOnlyList<(ISidebarWidget Widget, WidgetModel Model)> widgets)
    {
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR") is not null;
        var rows = new List<(string Text, ThemeRole Role, bool Heading, bool Link)>();
        var actions = new Dictionary<int, WidgetRowModel>();
        _headings.Clear();
        foreach (var (source, widget) in widgets)
        {
            if (widget is not ListWidgetModel model) continue;
            if (rows.Count > 0) rows.Add(("", ThemeRole.Primary, false, false));
            _headings[source.Id] = rows.Count;
            rows.Add((model.Title, ThemeRole.Info, true, false));
            foreach (var row in model.Rows)
            {
                if (row.Action is not null) actions[rows.Count] = row;
                rows.Add(((!ShowRoleGlyphs || row.Role == ThemeRole.Primary || row.Action is not null ? "" : ThemeGlyphs.For(row.Role) + " ") + row.Text, row.Role, false, row.Action is not null));
            }
        }
        var signature = Viewport.Width + ":" + noColor + string.Join("\n", rows) + string.Join(";", actions.Select(a => a.Key + ":" + a.Value.Target));
        if (signature == _signature) return; // keep focus/selection/scroll on unchanged polls
        var focusedAction = HasFocus ? FocusedAction : null;
        _signature = signature;
        _actions.Clear(); foreach (var action in actions) _actions[action.Key] = action.Value;
        var oldViewport = Viewport;
        var oldPoint = InsertionPoint;
        var oldScroll = VerticalScrollBar.Value;
        var cells = rows.Select(row =>
        {
            var color = row.Role switch
            {
                ThemeRole.Active => "#85E6DF", ThemeRole.Success => "#A6D99B", ThemeRole.Attention => "#F1D58A",
                ThemeRole.Error => "#FF9D9D", ThemeRole.Agent => "#D6B5FA", ThemeRole.Info => "#85D6F5",
                ThemeRole.Muted => "#B1C2D1", _ => "#F4F7FB",
            };
            var attribute = new Terminal.Gui.Drawing.Attribute(noColor ? Color.None : new Color(color),
                noColor ? Color.None : new Color("#202C3B"), row.Heading ? TextStyle.Bold : row.Link ? TextStyle.Underline : TextStyle.None);
            var line = new List<Cell>();
            var elements = System.Globalization.StringInfo.GetTextElementEnumerator(row.Text);
            while (elements.MoveNext()) line.Add(new Cell(attribute, false, elements.GetTextElement()));
            return line;
        }).ToList();
        InsertionPoint = System.Drawing.Point.Empty;
        Load(cells);
        // A settings/status refresh can change line wrapping between focus and
        // Enter. Keep the semantic link, not its obsolete wrapped coordinates.
        if (focusedAction is not null)
        {
            var destination = _actions.FirstOrDefault(pair => pair.Value.Action == focusedAction.Action
                && pair.Value.Target == focusedAction.Target && pair.Value.Text == focusedAction.Text);
            if (destination.Value is not null) { FocusSourceRow(destination.Key); return; }
        }
        InsertionPoint = new System.Drawing.Point(oldPoint.X, Math.Min(oldPoint.Y, GetAllLines().Count - 1));
        Viewport = new System.Drawing.Rectangle(oldViewport.Location, Viewport.Size);
        VerticalScrollBar.Value = oldScroll;
    }
    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.IsPressed)
        {
            if (_mouseDown is null) { _mouseDown = mouse.Position; _dragged = false; }
            else if (_mouseDown != mouse.Position) _dragged = true;
        }
        var handled = base.OnMouseEvent(mouse);
        if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked))
        {
            _mouseDown = null;
            if (!_dragged && _actions.TryGetValue(WordWrap ? _sourcePoint.Y : InsertionPoint.Y, out var action))
            { RowActivated?.Invoke(action); return true; }
            _dragged = false;
        }
        return handled;
    }
    protected override void OnDrawReadOnlyColor(List<Cell> line, int idxCol, int idxRow)
    {
        if (idxCol >= 0 && idxCol < line.Count && line[idxCol].Attribute is { } attribute) SetAttribute(Theme.Map(attribute));
        else base.OnDrawReadOnlyColor(line, idxCol, idxRow);
    }
}
#pragma warning restore CS0618
