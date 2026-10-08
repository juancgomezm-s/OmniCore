using OmniCore.Cli;
using OmniCore.Client;

namespace OmniCore.Tests;

public sealed class ConversationMarkdownRegressionTests
{
    [Fact]
    public void Heading_renders_inline_formatting_instead_of_literal_markers()
    {
        var rows = MarkdownRenderer.Render("## 🚀 **Proyecto** y `C#`");
        Assert.Equal("🚀 Proyecto y C#", Plain(rows.Single()));
        Assert.Contains(rows.Single(), span => span.Text == "C#" && span.Style == ConversationStyle.InlineCode);
        Assert.Contains(rows.Single(), span => span.Text == "Proyecto" && span.Style == ConversationStyle.Heading);
    }

    [Theory]
    [InlineData("``a`b``", "a`b")]
    [InlineData("`` **literal** ``", "**literal**")]
    [InlineData("```a``b```", "a``b")]
    public void Inline_code_matches_the_entire_delimiter_run(string source, string expected)
    {
        var spans = MarkdownRenderer.Inline(source);
        var code = Assert.Single(spans);
        Assert.Equal(expected, code.Text);
        Assert.Equal(ConversationStyle.InlineCode, code.Style);
    }

    [Fact]
    public void Escaped_markers_stay_literal_without_disabling_later_formatting()
    {
        var spans = MarkdownRenderer.Inline(@"\*\*literal\*\* y **destacado**");
        Assert.Equal("**literal** y destacado", Plain(spans));
        Assert.Contains(spans, span => span.Text == "destacado" && span.Decoration.HasFlag(Terminal.Gui.Drawing.TextStyle.Bold));
        Assert.All(spans.Where(span => span.Text.Contains("literal")), span => Assert.Equal(ConversationStyle.Text, span.Style));
    }

    [Fact]
    public void Formatting_does_not_change_journal_text_or_literal_user_message()
    {
        const string source = "## **Proyecto**\n``a`b``";
        var user = new ConversationBlock("u", ConversationRole.User, source, null);
        var assistant = new ConversationBlock("a", ConversationRole.Assistant, source, null);
        var userRows = ConversationPresentation.Render(new[] { user }, "es");
        Assert.Equal(source, string.Join('\n', userRows.Skip(1).Select(Plain)));
        var assistantRows = ConversationPresentation.Render(new[] { assistant }, "es");
        Assert.Equal(new[] { "OMNICORE", "Proyecto", "a`b" }, assistantRows.Select(Plain));
        Assert.Equal(source, user.Text);
        Assert.Equal(source, assistant.Text);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(80)]
    public void Styled_presenter_receives_a_real_grid_and_inline_code_cells(int width)
    {
        const string source = "## **Ejemplo**\n| Código | Estado |\n| --- | --- |\n| ``a`b`` | ✅ |";
        var block = new ConversationBlock("a", ConversationRole.Assistant, source, null);
        var rows = ConversationPresentation.RenderCards(new[] { block }, "es", width);
        using var presenter = new StyledTextPresenter();
        presenter.LoadStyled(rows, width, noColor: false);
        var cells = presenter.GetAllLines();
        var rendered = string.Join('\n', cells.Select(row => string.Concat(row.Select(cell => cell.Grapheme))));
        Assert.Contains("Ejemplo", rendered);
        Assert.Contains("┌", rendered);
        Assert.Contains("└", rendered);
        Assert.Contains("a`b", rendered);
        Assert.Contains("✅", rendered);
        Assert.DoesNotContain("**", rendered);
        Assert.DoesNotContain("``", rendered);
        Assert.Contains(cells.SelectMany(row => row), cell => cell.Grapheme == "`"
            && cell.Attribute?.Foreground == new Terminal.Gui.Drawing.Color("#EF9A70"));
        Assert.Equal(source, block.Text);
    }

    [Theory]
    [InlineData("``incomplete", "``incomplete")]
    [InlineData("``a```", "``a```")]
    [InlineData(@"C:\folder\file.cs", @"C:\folder\file.cs")]
    public void Streaming_and_non_punctuation_backslashes_are_not_lost(string source, string expected) =>
        Assert.Equal(expected, Plain(MarkdownRenderer.Inline(source)));

    private static string Plain(IReadOnlyList<ConversationSpan> spans) => string.Concat(spans.Select(span => span.Text));
}
