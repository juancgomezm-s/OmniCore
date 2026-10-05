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
        SelectedCellsList.Select(row => string.Concat(row.Select(cell => cell.Grapheme))));

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
                    noColor ? Color.None : new Color(IsCode(span.Style) ? "#0A2330" : "#061822"));
                var elements = System.Globalization.StringInfo.GetTextElementEnumerator(span.Text);
                while (elements.MoveNext()) cells.Add(new Cell(attribute, false, elements.GetTextElement()));
            }
            // Like WPF Paragraph.Background, block shading covers the viewport,
            // whereas inline-code styling applies only to its own text.
            if (line.Count > 0 && line.All(span => IsCode(span.Style)))
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

    private static bool IsCode(ConversationStyle style) => style >= ConversationStyle.Code;

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
        _ => "#D8E5ED",
    };

    protected override void OnDrawReadOnlyColor(List<Cell> line, int idxCol, int idxRow)
    {
        if (idxCol >= 0 && idxCol < line.Count && line[idxCol].Attribute is { } attribute)
            SetAttribute(attribute);
        else base.OnDrawReadOnlyColor(line, idxCol, idxRow);
    }

    protected override void OnDrawNormalColor(List<Cell> line, int idxCol, int idxRow)
    {
        if (idxCol >= 0 && idxCol < line.Count && line[idxCol].Attribute is { } attribute)
            SetAttribute(attribute);
        else base.OnDrawNormalColor(line, idxCol, idxRow);
    }
}
#pragma warning restore CS0618
