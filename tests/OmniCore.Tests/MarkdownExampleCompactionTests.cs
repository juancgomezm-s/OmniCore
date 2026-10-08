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
    [InlineData("```markdown\n- único\n```\nExplicación diferente")]
    [InlineData("```markdown\n- único\n```")]
    [InlineData("```markdown\n- aún escribiendo")]
    [InlineData("```html\n<mark>ejemplo</mark>\n```\n<mark>ejemplo</mark>")]
    [InlineData("````text\n```markdown\n- fuente\n```\n- fuente\n````")]
    public void Unique_unfinished_and_non_demo_code_remain_literal(string source)
        => Assert.Equal(source, MarkdownExamples.Compact(source));

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
