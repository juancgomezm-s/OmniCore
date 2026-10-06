using OmniCore.Cli;
using OmniCore.Client;
using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class MarkdownTableRendererTests
{
    [Fact]
    public void Small_table_has_an_exact_stable_grid_snapshot()
    {
        var rows = MarkdownRenderer.Render("| A | B |\n| --- | --- |\n| `x` | y |", 80);
        Assert.Equal(new[] { "┌──────┬──────┐", "│ A    │ B    │", "├──────┼──────┤", "│ x    │ y    │", "└──────┴──────┘" },
            rows.Select(row => string.Concat(row.Select(span => span.Text))));
        Assert.Equal(ConversationStyle.Heading, rows[1].Single(span => span.Text == "A").Style);
        Assert.Equal(ConversationStyle.InlineCode, rows[3].Single(span => span.Text == "x").Style);
    }

    [Fact]
    public void Every_usable_width_preserves_all_table_cells_and_aligned_grid_edges()
    {
        const string source = "| A | B | C |\n| --- | :---: | ---: |\n| a0123456789 | 中文😊 | c0123456789 |\n| a9876543210 | é✅ | c9876543210 |";
        for (var width = 20; width <= 120; width++)
        {
            var rows = ConversationPresentation.RenderCards(new[] { new ConversationBlock("qa", ConversationRole.Assistant, source, null) }, "es", width);
            var lines = rows.Skip(1).Select(row => string.Concat(row.Skip(1).Select(span => span.Text))).ToArray();
            Assert.All(rows, row => Assert.True(Terminal.Gui.Text.StringExtensions.GetColumns(string.Concat(row.Select(span => span.Text)), false) <= width, "desbordamiento a " + width));
            if (lines[0].StartsWith('┌'))
            {
                var borderWidth = Terminal.Gui.Text.StringExtensions.GetColumns(lines[0], false);
                Assert.All(lines, line => Assert.Equal(borderWidth, Terminal.Gui.Text.StringExtensions.GetColumns(line, false)));
                var separator = Array.FindIndex(lines, line => line.StartsWith('├'));
                var records = new List<List<string>> { new() };
                foreach (var line in lines.Skip(separator + 1))
                {
                    if (line.StartsWith('└')) break;
                    if (line.StartsWith('├')) records.Add(new());
                    else records[^1].Add(line);
                }
                Assert.Equal(2, records.Count);
                var expected = new[] { new[] { "a0123456789", "中文😊", "c0123456789" }, new[] { "a9876543210", "é✅", "c9876543210" } };
                for (var record = 0; record < expected.Length; record++)
                for (var col = 0; col < 3; col++)
                    Assert.Equal(expected[record][col], string.Concat(records[record].Select(line => line.Split('│')[col + 1].Trim())));
            }
            else
            {
                var body = string.Concat(lines);
                foreach (var value in new[] { "a0123456789", "中文😊", "c0123456789", "a9876543210", "é✅", "c9876543210" })
                    Assert.Contains(value, body);
            }
        }
    }

    [Theory]
    [InlineData(36)]
    [InlineData(78)]
    [InlineData(120)]
    public void Grid_preserves_content_styles_and_terminal_column_alignment(int width)
    {
        const string text = "| Archivo | Tecnología | Estado |\n| :--- | :---: | ---: |\n| `index.html` | HTML | ✅ Activo |\n| **código** | C# | 中文 |";
        var rows = MarkdownRenderer.Render(text, width);
        var rendered = rows.Select(row => string.Concat(row.Select(span => span.Text))).ToArray();
        Assert.StartsWith("┌", rendered[0]);
        Assert.StartsWith("└", rendered[^1]);
        Assert.DoesNotContain(rendered, line => line.Contains("---"));
        var columns = rendered.Select(line => Terminal.Gui.Text.StringExtensions.GetColumns(line, false)).ToArray();
        Assert.All(columns, size => Assert.Equal(columns[0], size));
        Assert.True(columns[0] <= width);
        Assert.Contains(rows.SelectMany(row => row), span => span.Style == ConversationStyle.InlineCode);
        Assert.Contains(rows.SelectMany(row => row), span => span.Style == ConversationStyle.Heading);
        var dataLines = rendered.SkipWhile(line => !line.StartsWith('├')).Skip(1).TakeWhile(line => !line.StartsWith('├')).ToArray();
        string Column(int col) => string.Concat(dataLines.Select(line => line.Split('│')[col + 1].Trim()));
        Assert.Equal("index.html", Column(0));
        Assert.Equal("HTML", Column(1));
        Assert.Equal("✅Activo", Column(2).Replace(" ", ""));
        Assert.Contains("中文", string.Concat(rendered));
    }

    [Fact]
    public void Escaped_pipes_alignment_and_incomplete_last_row_are_supported()
    {
        var rows = MarkdownRenderer.Render("Name | Value\n:--- | ---:\nleft\\|right | 42\nlast |", 80);
        var lines = rows.Select(row => string.Concat(row.Select(span => span.Text))).ToArray();
        Assert.Contains(lines, line => line.Contains("left|right"));
        Assert.Contains(lines, line => line.EndsWith("42 │", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("last"));
    }

    [Fact]
    public void Fenced_tables_and_invalid_delimiters_remain_literal()
    {
        const string table = "| A | B |\n| --- | --- |\n| x | y |";
        var fenced = MarkdownRenderer.Render("```text\n" + table + "\n```", 80);
        Assert.DoesNotContain(fenced.SelectMany(row => row), span => span.Text.Contains('┌'));
        Assert.Contains(fenced.SelectMany(row => row), span => span.Text.Contains("| A | B |"));
        var invalid = MarkdownRenderer.Render(table.Replace("---", "--"), 80);
        Assert.Equal("| A | B |", string.Concat(invalid[0].Select(span => span.Text)));
    }

    [Fact]
    public void Small_cards_keep_all_values_without_widening_or_modifying_source()
    {
        const string source = "| Archivo | Contenido |\n| --- | --- |\n| index.html | Una explicación extensa con símbolos 😊 y 中文 |";
        var block = new ConversationBlock("table", ConversationRole.Assistant, source, null);
        var rows = ConversationPresentation.RenderCards(new[] { block }, "es", 16);
        Assert.All(rows, row => Assert.True(Terminal.Gui.Text.StringExtensions.GetColumns(string.Concat(row.Select(span => span.Text)), false) <= 16));
        var text = string.Concat(rows.Skip(1).SelectMany(row => row.Skip(1)).Select(span => span.Text));
        Assert.Contains("Archivo: index.html", text);
        Assert.Contains("Una explicación extensa con símbolos 😊 y 中文", text);
        Assert.Equal(source, block.Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Runtime_requests_inline_answers_in_both_modes_without_changing_approval_rules(bool act)
    {
        var instruction = OmniCliRuntime.TurnInstruction(act);
        Assert.Contains("Do not create or modify workspace files unless the user explicitly requests file changes", instruction);
        Assert.Contains("include the requested code, Markdown tables", instruction);
        Assert.Contains("all approval and completion requirements", instruction);
    }
}
