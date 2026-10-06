using Terminal.Gui.Drawing;
using Terminal.Gui.Views;
using Terminal.Gui.Input;

namespace OmniCore.Cli;

/// <summary>
/// Terminal counterpart of OmniCoder's read-only RichTextBox presenter. Styles
/// affect cells, not source text; selection/copy and scrolling stay with TextView.
/// Polling readiness/caching is owned by TuiApp, so identical text is not reloaded.
/// </summary>
#pragma warning disable CS0618
internal class StyledTextPresenter : TextView
{
    internal bool Dimmed { get; set; }
    // Internal cell metadata; normalize the one-step background marker before drawing.
    private static readonly Color AccentMarker = new("#061823");
    private static bool IsDecoration(Cell cell) => cell.Attribute?.Background == AccentMarker;
    public StyledTextPresenter()
    {
        ReadOnly = true;
        WordWrap = true;
        ScrollBars = true;
        AddCommand(Command.Copy, () => CopySelection());
    }

    // Keep clipboard output independent of style serialization: extract only
    // graphemes, never attributes or terminal escape sequences.
    public new string SelectedText => string.Join(Environment.NewLine,
        SelectedCellsList.Select(row => string.Concat(row.Where(cell => !IsDecoration(cell)).Select(cell => cell.Grapheme))));

    public new void Copy() => CopySelection();

    private bool CopySelection()
    {
        var text = SelectedText;
        return text.Length > 0 && App?.Clipboard?.TrySetClipboardData(text) == true;
    }

    internal void LoadStyled(IReadOnlyList<IReadOnlyList<ConversationSpan>> lines, int width, bool noColor)
    {
        var rows = new List<List<Cell>>();
        foreach (var line in lines)
        {
            var cells = new List<Cell>();
            foreach (var span in line)
            {
                var attribute = new Terminal.Gui.Drawing.Attribute(
                    noColor ? Color.None : new Color(Foreground(span.Style)),
                    IsAccent(span.Style) ? AccentMarker : noColor ? Color.None : new Color(IsCode(span.Style) ? "#0A2330" : "#061822"));
                var elements = System.Globalization.StringInfo.GetTextElementEnumerator(span.Text);
                while (elements.MoveNext())
                {
                    var cell = new Cell(attribute, false, elements.GetTextElement());
                    cells.Add(cell);
                }
            }
            // Like WPF Paragraph.Background, block shading covers the viewport,
            // whereas inline-code styling applies only to its own text.
            if (line.Any(span => IsCode(span.Style)) && line.Where(span => !IsAccent(span.Style)).All(span => IsCode(span.Style)))
            {
                var columns = line.Sum(span => Terminal.Gui.Text.StringExtensions.GetColumns(span.Text, false));
                var padding = new Terminal.Gui.Drawing.Attribute(noColor ? Color.None : new Color("#B5DEF3"),
                    noColor ? Color.None : new Color("#0A2330"));
                for (var column = columns; column < width; column++) cells.Add(new Cell(padding, false, " "));
            }
            rows.Add(cells);
        }
        Load(rows);
    }

    private static bool IsCode(ConversationStyle style) => style is >= ConversationStyle.Code and <= ConversationStyle.SyntaxPunctuation;
    private static bool IsAccent(ConversationStyle style) => style >= ConversationStyle.UserAccent;

    private static string Foreground(ConversationStyle style) => style switch
    {
        ConversationStyle.Heading => "#53B8F5",
        ConversationStyle.InlineCode => "#EF9A70",
        ConversationStyle.Code => "#B5DEF3",
        ConversationStyle.CodeHeader => "#67D4D0",
        ConversationStyle.Muted => "#8B9DAC",
        ConversationStyle.SyntaxKeyword => "#C8A0F5",
        ConversationStyle.SyntaxType => "#67D4D0",
        ConversationStyle.SyntaxFunction => "#F1D58A",
        ConversationStyle.SyntaxString => "#A6D99B",
        ConversationStyle.SyntaxNumber => "#EF9A70",
        ConversationStyle.SyntaxComment => "#91A8B8",
        ConversationStyle.SyntaxPunctuation => "#ADC4D4",
        ConversationStyle.SyntaxVariable => "#D8E5ED",
        ConversationStyle.UserAccent => "#B47CE7",
        ConversationStyle.AssistantAccent => "#4DBAC9",
        ConversationStyle.ToolAccent => "#D99955",
        ConversationStyle.NoticeAccent => "#526A7C",
        _ => "#D8E5ED",
    };

    protected override void OnDrawReadOnlyColor(List<Cell> line, int idxCol, int idxRow)
    {
        if (idxCol >= 0 && idxCol < line.Count && line[idxCol].Attribute is { } attribute)
            PaintAttribute(attribute);
        else base.OnDrawReadOnlyColor(line, idxCol, idxRow);
    }

    protected override void OnDrawNormalColor(List<Cell> line, int idxCol, int idxRow)
    {
        if (idxCol >= 0 && idxCol < line.Count && line[idxCol].Attribute is { } attribute)
            PaintAttribute(attribute);
        else base.OnDrawNormalColor(line, idxCol, idxRow);
    }
    private void PaintAttribute(Terminal.Gui.Drawing.Attribute attribute)
    {
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR") is not null;
        SetAttribute(new Terminal.Gui.Drawing.Attribute(noColor ? Color.None : Dimmed ? new Color("#405666") : attribute.Foreground,
            noColor ? Color.None : attribute.Background == AccentMarker ? new Color("#061822") : attribute.Background));
    }
}
#pragma warning restore CS0618
