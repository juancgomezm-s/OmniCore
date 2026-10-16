using OmniCore.Cli;

namespace OmniCore.Tests;

public sealed class MarkdownExampleCompactionTests
{
    [Theory]
    [InlineData("- [x] Completada\n- [ ] Pendiente")]
    [InlineData("> Primera línea\n> Segunda línea")]
    [InlineData("El comando `ls -la` muestra archivos.")]
    [InlineData("## Encabezado")]
    public void Repeated_demo_is_displayed_once(string example)
    {
        var source = "```markdown\n" + example + "\n```\n\n" + example;
        Assert.Equal(example, MarkdownExamples.Compact(source));
    }

    [Fact]
    public void Link_preview_keeps_real_url_and_demo_can_be_restored_from_original()
    {
        const string source = "```md\n[Texto del enlace](https://ejemplo.com)\n```\n[Texto del enlace](https://es.wikipedia.org/wiki/Markdown)";
        Assert.Equal("[Texto del enlace](https://es.wikipedia.org/wiki/Markdown)", MarkdownExamples.Compact(source));
        Assert.Contains("https://ejemplo.com", source);
    }

    [Theory]
    [InlineData("```markdown\n- aún escribiendo")]
    [InlineData("```html\n<mark>ejemplo</mark>\n```\n<mark>ejemplo</mark>")]
    [InlineData("````text\n```markdown\n- fuente\n```\n- fuente\n````")]
    public void Unfinished_and_non_demo_code_remain_literal(string source)
        => Assert.Equal(source, MarkdownExamples.Compact(source));

    [Theory]
    [InlineData("markdown")]
    [InlineData("MD")]
    public void Complete_markdown_example_gets_a_preview_without_requiring_a_repeated_copy(string language)
    {
        const string example = "## Encabezado 2\n\n**Texto en negrita** y _cursiva_\n\n| Dato | Estado |\n| --- | --- |\n| Uno | Listo |";
        var source = "```" + language + "\n" + example + "\n```\nExplicación diferente";
        Assert.Equal(example + "\nExplicación diferente", MarkdownExamples.Compact(source));
        var block = new OmniCore.Client.ConversationBlock("a", OmniCore.Client.ConversationRole.Assistant, source, null);
        var preview = ConversationPresentation.Render([block], "es", compactExamples: true);
        Assert.DoesNotContain(preview.SelectMany(row => row), span => span.Style == ConversationStyle.CodeHeader);
        Assert.Contains(preview.SelectMany(row => row), span => span.Text == "Texto en negrita"
            && span.Decoration.HasFlag(Terminal.Gui.Drawing.TextStyle.Bold));
        var original = ConversationPresentation.Render([block], "es", compactExamples: false);
        Assert.Contains(original.SelectMany(row => row), span => span.Text.Contains("**Texto en negrita**", StringComparison.Ordinal));
        Assert.Equal(source, block.Text);
    }

    [Fact]
    public void Shared_first_line_does_not_hide_different_content()
    {
        const string source = "```markdown\n## Resultado\nPrimer dato\n```\n## Resultado\nOtro dato";
        var preview = MarkdownExamples.Compact(source);
        Assert.Contains("Primer dato", preview);
        Assert.Contains("Otro dato", preview);
    }

    [Fact]
    public void Human_markdown_source_stays_literal_in_preview_mode()
    {
        const string source = "```markdown\n## Encabezado\n**literal**\n```";
        var block = new OmniCore.Client.ConversationBlock("u", OmniCore.Client.ConversationRole.User, source, null);
        var rows = ConversationPresentation.Render([block], "es", compactExamples: true);
        Assert.Equal(source, string.Join('\n', rows.Skip(1).Select(row => string.Concat(row.Select(span => span.Text)))));
    }

    [Fact]
    public void Nested_code_example_preserves_one_actual_code_block()
    {
        const string code = "```python\nprint('Hola')\n```";
        Assert.Equal(code, MarkdownExamples.Compact("````markdown\n" + code + "\n````\n" + code));
    }

    [Fact]
    public void Header_caps_title_even_on_wide_terminal_and_keeps_full_model()
    {
        var header = TuiApp.SessionHeading("OmniCore", new string('X', 150), "Llama.cpp/qwen38-27b-abl-v9-best-q4kxl", 200);
        Assert.Contains(new string('X', 31) + "…", header);
        Assert.DoesNotContain(new string('X', 32), header);
        Assert.Contains("Modelo: Llama.cpp/qwen38-27b-abl-v9-best-q4kxl", header);
        Assert.True(header.Length < 120);
    }
}
