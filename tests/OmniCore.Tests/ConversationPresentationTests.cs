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
        Assert.Contains(rows.SelectMany(r => r), s => s.Text == "│     public void Run() { }" && s.Style == ConversationStyle.Code);
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
        var spans = Render("````text\n```\n  tail").SelectMany(r => r).ToArray();
        Assert.Contains(spans, s => s.Text == "│ ```" && s.Style == ConversationStyle.Code);
        Assert.Contains(spans, s => s.Text == "│   tail" && s.Style == ConversationStyle.Code);
    }
}
