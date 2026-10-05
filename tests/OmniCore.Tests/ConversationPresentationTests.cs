using OmniCore.Cli;
using OmniCore.Client;

namespace OmniCore.Tests;

public sealed class ConversationPresentationTests
{
    private static IReadOnlyList<IReadOnlyList<ConversationSpan>> Render(string text, ConversationRole role = ConversationRole.Assistant) =>
        ConversationPresentation.Render(new[] { new ConversationBlock("test", role, text, null) }, "es");

    [Fact]
    public void Reference_hierarchy_code_and_inline_text_have_distinct_semantic_styles()
    {
        var rows = Render("## Key Methods\n- `AskAsync(ct)` — **Run Explorer**\n```c#\n    public void Run() { }\n```\nFinal.");
        Assert.Contains(rows.SelectMany(r => r), s => s.Text == "Key Methods" && s.Style == ConversationStyle.Heading);
        Assert.Contains(rows.SelectMany(r => r), s => s.Text == "AskAsync(ct)" && s.Style == ConversationStyle.InlineCode);
        Assert.Contains(rows.SelectMany(r => r), s => s.Text == "Run Explorer" && s.Style == ConversationStyle.Heading);
        Assert.Contains(rows, row => string.Concat(row.Select(s => s.Text)) == "      public void Run() { }");
        Assert.Contains(rows.SelectMany(r => r), s => s.Text == "public" && s.Style == ConversationStyle.SyntaxKeyword);
        Assert.Equal("Final.", rows.Last().Single().Text);
    }

    [Fact]
    public void User_input_stays_literal_and_unclosed_inline_markup_is_not_lost()
    {
        const string input = "## title\n```c#\n`unclosed ** x";
        Assert.Equal(input, string.Join("\n", Render(input, ConversationRole.User).Skip(1).Select(r => string.Concat(r.Select(s => s.Text)))));
        Assert.Equal("`unclosed ** x", Render("`unclosed ** x").Last().Single().Text);
    }

    [Fact]
    public void Longer_fence_and_unclosed_code_keep_indentation_and_inner_fence()
    {
        var rows = Render("````text\n```\n  tail");
        Assert.Contains(rows, row => string.Concat(row.Select(s => s.Text)) == "  ```"
            && row.All(s => s.Style == ConversationStyle.Code));
        Assert.Contains(rows, row => string.Concat(row.Select(s => s.Text)) == "    tail"
            && row.All(s => s.Style == ConversationStyle.Code));
    }

    [Fact]
    public void Whole_fenced_block_keeps_multiline_comments_and_blank_lines()
    {
        var rows = Render("```C#\n/* first\nsecond */\n\nint count = 42;\n```");
        var comment = rows.Single(row => row.Any(s => s.Text.Contains("second", StringComparison.Ordinal)));
        Assert.Contains(comment, s => s.Text == "second */" && s.Style == ConversationStyle.SyntaxComment);
        Assert.Contains(rows, row => string.Concat(row.Select(s => s.Text)) == "  ");
        Assert.Contains(rows.SelectMany(row => row), s => s.Text == "42" && s.Style == ConversationStyle.SyntaxNumber);
    }

    [Fact]
    public void Different_marker_and_shorter_fence_do_not_close_or_eat_code()
    {
        var rows = Render("````csharp\n~~~\n```\n  int n = 7;\n````\nAfter");
        Assert.Contains(rows, row => string.Concat(row.Select(s => s.Text)) == "  ~~~");
        Assert.Contains(rows, row => string.Concat(row.Select(s => s.Text)) == "  ```");
        Assert.Equal("After", rows.Last().Single().Text);
    }

    [Fact]
    public void Huge_markdown_keeps_source_literal_without_expensive_formatting()
    {
        var text = "```csharp\n" + new string('x', MarkdownRenderer.MaxLength);
        Assert.Equal(text, string.Join("\n", MarkdownRenderer.Render(text).Select(row => string.Concat(row.Select(s => s.Text)))));
    }

    [Theory]
    [InlineData("html", "<script>const n = 42;</script>", "const", "SyntaxKeyword")]
    [InlineData("md", "# Title\n**strong** `inline`", "# Title", "SyntaxType")]
    public void Markup_languages_reach_the_conversation_as_styled_code(
        string language, string source, string token, string style)
    {
        var rows = Render("~~~~" + language + "\n" + source + "\n~~~~");
        Assert.Contains(rows.SelectMany(row => row), span => span.Text == token && span.Style == Enum.Parse<ConversationStyle>(style));
        var body = rows.Skip(2).Take(rows.Count - 3).ToArray();
        Assert.Equal(source, string.Join("\n", body.Select(row => string.Concat(row.Select(span => span.Text))[2..])));
        Assert.All(body.SelectMany(row => row), span => Assert.True(span.Style >= ConversationStyle.Code));
    }
}
