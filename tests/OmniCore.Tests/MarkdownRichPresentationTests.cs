using OmniCore.Cli;
using OmniCore.Client;
using Terminal.Gui.Drawing;

namespace OmniCore.Tests;

public sealed class MarkdownRichPresentationTests
{
    private static string Plain(string markdown) => string.Join('\n', MarkdownRenderer.Render(markdown).Select(row => string.Concat(row.Select(s => s.Text))));

    [Fact]
    public void Emphasis_is_real_typography_and_survives_card_wrapping()
    {
        var rows = ConversationPresentation.RenderCards(new[] { new ConversationBlock("a", ConversationRole.Assistant,
            "**Negrita** *Cursiva* ***Ambas*** ~~Tachado~~", null) }, "es", 20);
        var body = rows.Skip(1).SelectMany(row => row.Skip(1)).ToArray();
        Assert.Equal("Negrita Cursiva Ambas Tachado", string.Concat(body.Select(s => s.Text)));
        Assert.Contains(body, s => s.Text == "N" && s.Decoration == TextStyle.Bold);
        Assert.Contains(body, s => s.Text == "C" && s.Decoration == TextStyle.Italic);
        Assert.Contains(body, s => s.Text == "A" && s.Decoration == (TextStyle.Bold | TextStyle.Italic));
        Assert.Contains(body, s => s.Text == "T" && s.Decoration == TextStyle.Strikethrough);
    }

    [Fact]
    public void Html_is_presented_but_fenced_html_remains_literal_code()
    {
        const string html = "<kbd>Ctrl</kbd> + <kbd>C</kbd> <mark>atención</mark> <abbr title=\"Lenguaje &amp; formato\">HTML</abbr>";
        var spans = MarkdownRenderer.Inline(html);
        Assert.Equal("Ctrl + C atención HTML (Lenguaje & formato)", string.Concat(spans.Select(s => s.Text)));
        Assert.Contains(spans, s => s.Text == "Ctrl" && s.Style == ConversationStyle.InlineCode);
        Assert.Contains(spans, s => s.Text == "atención" && s.Decoration.HasFlag(TextStyle.Reverse));
        var fenced = Plain("```html\n" + html + "\n```");
        Assert.Contains(html, fenced);
        Assert.Contains("<unknown>visible</unknown>", Plain("<unknown>visible</unknown>"));
    }

    [Fact]
    public void Spanish_entities_footnotes_and_combining_accents_survive()
    {
        Assert.Equal("á é í ó ú ü ñ ¿¡ é", Plain("&aacute; &eacute; &#237; ó ú &uuml; &ntilde; ¿¡ é"));
        Assert.Equal("Texto[1].\n\n[1] Explicación: pingüino, niño y acción.",
            Plain("Texto[^1].\n\n[^1]: Explicación: pingüino, niño y acción."));
        Assert.Contains("[^1]", Plain("`[^1]`"));
        Assert.Equal("[^1]", Plain(@"\[^1]"));
        Assert.Equal("primera\nsegunda", Plain("primera<br>segunda"));
    }

    [Fact]
    public void Lists_keep_nesting_tasks_and_numbering()
    {
        Assert.Equal("• Uno\n  • Niño\n    • Más\n3. Tres\n   1. Dentro\n☑ Listo\n☐ Falta",
            Plain("- Uno\n  - Niño\n    + Más\n3. Tres\n   1. Dentro\n- [x] Listo\n- [ ] Falta"));
        var wrapped = ConversationPresentation.RenderCards(new[] { new ConversationBlock("a", ConversationRole.Assistant,
            "  - Un elemento anidado que ocupa varias líneas", null) }, "es", 24);
        Assert.True(wrapped.Count > 2);
        Assert.All(wrapped.Skip(2), row => Assert.StartsWith("    ", string.Concat(row.Skip(1).Select(span => span.Text))));
    }

    [Fact]
    public void Links_quotes_rules_and_literal_examples_are_not_duplicated()
    {
        Assert.Equal("Sitio (https://example.com)", Plain("[Sitio](https://example.com)"));
        Assert.Equal("Imagen: Diagrama (diagram.png)", Plain("![Diagrama](diagram.png)"));
        Assert.Equal("│ Cita", Plain("> Cita"));
        Assert.All(Plain("---").ToCharArray(), character => Assert.Equal('─', character));
        Assert.Equal(1, Plain("Texto único").Split("Texto único").Length - 1);
        var example = Plain("```markdown\n**ejemplo**\n```\n**ejemplo**");
        Assert.Contains("**ejemplo**", example);
        Assert.EndsWith("\nejemplo", example);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(65)]
    [InlineData(80)]
    [InlineData(120)]
    public void Footer_and_heading_fit_in_a_single_line(int width)
    {
        var footer = TuiApp.CompactHelp("act", "es", width);
        Assert.StartsWith("act · Enter enviar", footer);
        Assert.True(Terminal.Gui.Text.StringExtensions.GetColumns(footer, false) <= width);
        var header = TuiApp.SessionHeading("OmniCore", "Revisión española del proyecto", "qwen/modelo-largo", width);
        Assert.Contains("│", header);
        Assert.True(Terminal.Gui.Text.StringExtensions.GetColumns(header, false) <= width);
    }
}
