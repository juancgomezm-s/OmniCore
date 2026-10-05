using OmniCore.Cli;

namespace OmniCore.Tests;

public sealed class MarkupSyntaxHighlighterTests
{
    private static string Plain(IReadOnlyList<IReadOnlyList<ConversationSpan>> rows) =>
        string.Join("\n", rows.Select(row => string.Concat(row.Select(span => span.Text))));

    [Theory]
    [InlineData("html")]
    [InlineData("HTM")]
    [InlineData("html5")]
    [InlineData("css")]
    [InlineData("md")]
    [InlineData("markdown")]
    [InlineData("mdown")]
    [InlineData("mkd")]
    public void New_aliases_preserve_source_exactly(string language)
    {
        const string source = "# título\n<div title=\"1 > 0\">á 😀 &amp;</div>\n\n**hola** `code`\n";
        Assert.True(SyntaxHighlighter.IsSupported(language));
        Assert.Equal(source, Plain(SyntaxHighlighter.Highlight(source, language)));
    }

    [Fact]
    public void Html_styles_tags_attributes_strings_and_multiline_comments()
    {
        const string source = "<!-- first\nsecond -->\n<div title=\"a > b\">Text &amp; more</div>";
        var rows = SyntaxHighlighter.Highlight(source, "html");
        Assert.Equal(source, Plain(rows));
        Assert.Contains(rows.SelectMany(r => r), s => s.Text == "div" && s.Style == ConversationStyle.SyntaxType);
        Assert.Contains(rows.SelectMany(r => r), s => s.Text == "title" && s.Style == ConversationStyle.SyntaxVariable);
        Assert.Contains(rows.SelectMany(r => r), s => s.Text == "a > b" && s.Style == ConversationStyle.SyntaxString);
        Assert.Contains(rows[1], s => s.Text == "second -->" && s.Style == ConversationStyle.SyntaxComment);
    }

    [Fact]
    public void Html_switches_between_css_javascript_and_markup_on_the_same_line()
    {
        const string source = "<STYLE>.card { color: red; padding: 12px; }</STYLE><script>const n = 42; /* a\nb */ alert('ok');</script><p>hello</p>";
        var rows = SyntaxHighlighter.Highlight(source, "html");
        var spans = rows.SelectMany(r => r).ToArray();
        Assert.Equal(source, Plain(rows));
        Assert.Contains(spans, s => s.Text == "color" && s.Style == ConversationStyle.SyntaxType);
        Assert.Contains(spans, s => s.Text == "12px" && s.Style == ConversationStyle.SyntaxNumber);
        Assert.Contains(spans, s => s.Text == "const" && s.Style == ConversationStyle.SyntaxKeyword);
        Assert.Contains(spans, s => s.Text == "alert" && s.Style == ConversationStyle.SyntaxFunction);
        Assert.Contains(spans, s => s.Text == "p" && s.Style == ConversationStyle.SyntaxType);
        Assert.Contains(rows[1], s => s.Text.StartsWith("b */", StringComparison.Ordinal) && s.Style == ConversationStyle.SyntaxComment);
    }

    [Fact]
    public void Inline_styles_and_handlers_are_highlighted_without_decoding_entities()
    {
        const string source = "<button style='color: red' onclick=\"return run(7);\" title='&quot;'>Go</button>";
        var rows = SyntaxHighlighter.Highlight(source, "html");
        Assert.Equal(source, Plain(rows));
        Assert.Contains(rows.SelectMany(r => r), s => s.Text == "color" && s.Style == ConversationStyle.SyntaxType);
        Assert.Contains(rows.SelectMany(r => r), s => s.Text == "return" && s.Style == ConversationStyle.SyntaxKeyword);
        Assert.Contains(rows.SelectMany(r => r), s => s.Text == "&quot;" && s.Style == ConversationStyle.SyntaxString);
    }

    [Theory]
    [InlineData("<div title=\"unterminated\nvalue")]
    [InlineData("<!-- unterminated\ncomment")]
    [InlineData("<script>const n = 42;\n// pending")]
    [InlineData("<style>/* pending\ncolor: red")]
    [InlineData("text<")]
    public void Incomplete_html_stays_visible_and_lossless(string source) =>
        Assert.Equal(source, Plain(SyntaxHighlighter.Highlight(source, "html")));

    [Fact]
    public void Non_javascript_script_types_are_not_treated_as_javascript()
    {
        var template = SyntaxHighlighter.Highlight("<script type='text/template'>const</script>", "html");
        Assert.Contains(template.SelectMany(r => r), s => s.Text == "const" && s.Style == ConversationStyle.Code);
        var json = SyntaxHighlighter.Highlight("<script type='application/ld+json'>{\"n\":42}</script>", "html");
        Assert.Contains(json.SelectMany(r => r), s => s.Text == "42" && s.Style == ConversationStyle.SyntaxNumber);
    }

    [Fact]
    public void Markdown_source_retains_markers_and_highlights_fenced_code()
    {
        const string source = "# Title\n**strong** and `inline` [link](https://example.test)\n> quote\n````js\nconst n = 42;\n```\n````\n";
        var rows = SyntaxHighlighter.Highlight(source, "markdown");
        Assert.Equal(source, Plain(rows));
        Assert.Contains(rows[0], s => s.Text == "# Title" && s.Style == ConversationStyle.SyntaxType);
        Assert.Contains(rows.SelectMany(r => r), s => s.Text == "**strong**" && s.Style == ConversationStyle.SyntaxKeyword);
        Assert.Contains(rows.SelectMany(r => r), s => s.Text == "const" && s.Style == ConversationStyle.SyntaxKeyword);
    }

    [Theory]
    [InlineData("~~~python\nreturn 42\n")]
    [InlineData("```md\n# nested\n```\n")]
    [InlineData("````html\n<div>hello</div>\n````")]
    public void Markdown_incomplete_nested_and_html_fences_are_bounded_and_lossless(string source) =>
        Assert.Equal(source, Plain(SyntaxHighlighter.Highlight(source, "md")));

    [Theory]
    [InlineData("html")]
    [InlineData("markdown")]
    [InlineData("css")]
    public void Huge_markup_respects_the_existing_plain_text_limit(string language)
    {
        var source = new string('x', SyntaxHighlighter.MaxLength + 1);
        var rows = SyntaxHighlighter.Highlight(source, language);
        Assert.Equal(source, Plain(rows));
        Assert.Equal(ConversationStyle.Code, Assert.Single(Assert.Single(rows)).Style);
    }
}
