using OmniCore.Client;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Views;

namespace OmniCore.Cli;

/// <summary>Generic declarative widget renderer. Native wrapping, selection and scrolling keep tall panels usable.</summary>
#pragma warning disable CS0618
internal sealed class SidebarView : TextView
{
    private string? _signature;
    private System.Drawing.Point _sourcePoint;
    internal bool ShowRoleGlyphs { get; set; } = true;
    private readonly Dictionary<int, WidgetRowModel> _actions = new();
    private readonly Dictionary<string, int> _headings = new();
    internal event Action<WidgetRowModel>? RowActivated;
    internal void SelectWidget(string? id)
    {
        if (id is null || !_headings.TryGetValue(id, out var row)) return;
        // Headings/actions use source rows; TextView.InsertionPoint uses wrapped screen rows.
        for (var wrapped = 0; wrapped < Lines; wrapped++)
        {
            InsertionPoint = new(0, wrapped);
            if ((WordWrap ? _sourcePoint.Y : wrapped) >= row) break;
        }
        SetFocus();
    }
    public SidebarView()
    {
        ReadOnly = true; WordWrap = true; ScrollBars = true;
        ConversationScrollBarStyle.Attach(VerticalScrollBar, () => false, "#202C3B");
        UnwrappedCursorPositionChanged += (_, point) => _sourcePoint = point;
        KeyDown += (_, key) =>
        {
            if (key.KeyCode != KeyCode.Enter || !_actions.TryGetValue(WordWrap ? _sourcePoint.Y : InsertionPoint.Y, out var action)) return;
            RowActivated?.Invoke(action); key.Handled = true;
        };
    }
    internal void Render(IReadOnlyList<(ISidebarWidget Widget, WidgetModel Model)> widgets)
    {
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR") is not null;
        var rows = new List<(string Text, ThemeRole Role, bool Heading)>();
        var actions = new Dictionary<int, WidgetRowModel>();
        _headings.Clear();
        foreach (var (source, widget) in widgets)
        {
            if (widget is not ListWidgetModel model) continue;
            if (rows.Count > 0) rows.Add(("", ThemeRole.Primary, false));
            _headings[source.Id] = rows.Count;
            rows.Add((model.Title, ThemeRole.Info, true));
            foreach (var row in model.Rows)
            {
                if (row.Action is not null) actions[rows.Count] = row;
                rows.Add(((!ShowRoleGlyphs || row.Role == ThemeRole.Primary ? "" : ThemeGlyphs.For(row.Role) + " ") + row.Text, row.Role, false));
            }
        }
        var signature = Viewport.Width + ":" + noColor + string.Join("\n", rows) + string.Join(";", actions.Select(a => a.Key + ":" + a.Value.Target));
        if (signature == _signature) return; // keep focus/selection/scroll on unchanged polls
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
                noColor ? Color.None : new Color("#202C3B"), row.Heading ? TextStyle.Bold : TextStyle.None);
            var line = new List<Cell>();
            var elements = System.Globalization.StringInfo.GetTextElementEnumerator(row.Text);
            while (elements.MoveNext()) line.Add(new Cell(attribute, false, elements.GetTextElement()));
            return line;
        }).ToList();
        InsertionPoint = System.Drawing.Point.Empty;
        Load(cells);
        InsertionPoint = new System.Drawing.Point(oldPoint.X, Math.Min(oldPoint.Y, GetAllLines().Count - 1));
        Viewport = new System.Drawing.Rectangle(oldViewport.Location, Viewport.Size);
        VerticalScrollBar.Value = oldScroll;
    }
    protected override void OnDrawReadOnlyColor(List<Cell> line, int idxCol, int idxRow)
    {
        if (idxCol >= 0 && idxCol < line.Count && line[idxCol].Attribute is { } attribute) SetAttribute(attribute);
        else base.OnDrawReadOnlyColor(line, idxCol, idxRow);
    }
}
#pragma warning restore CS0618
