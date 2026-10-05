// Adapted from OmniCoder's syntax regression cases for the terminal span contract.
using OmniCore.Cli;

namespace OmniCore.Tests;

/// <summary>
/// El resaltador de sintaxis.
/// </summary>
/// <remarks>
/// Lo que se comprueba no es que quede bonito —eso no se puede afirmar en un test— sino que el
/// texto <b>no se pierde ni se altera</b> y que las categorías caen donde deben. La primera
/// propiedad es la importante: un resaltador que se come un carácter convierte el código que
/// enseña en código que no compila.
/// </remarks>
public sealed class ConversationSyntaxHighlighterTests
{
    private static string Rebuild(IReadOnlyList<IReadOnlyList<ConversationSpan>> lines) =>
        string.Join('\n', lines.Select(l => string.Concat(l.Select(r => r.Text))));

    private static ConversationStyle? TokenOf(IReadOnlyList<IReadOnlyList<ConversationSpan>> lines, string text)
    {
        foreach (var run in lines.SelectMany(l => l))
        {
            if (run.Text == text)
            {
                return run.Style;
            }
        }

        return null;
    }

    [Theory]
    [InlineData("csharp")]
    [InlineData("javascript")]
    [InlineData("python")]
    [InlineData("sql")]
    [InlineData("abl")]
    [InlineData("java")]
    [InlineData("kotlin")]
    [InlineData("visualbasic")]
    [InlineData("dax")]
    [InlineData("powerquery")]
    [InlineData("xml")]
    [InlineData("yaml")]
    [InlineData("c")]
    [InlineData("cpp")]
    [InlineData("abap")]
    [InlineData("shell")]
    [InlineData("json")]
    public void The_text_survives_untouched(string language)
    {
        // Un texto con de todo: comillas, escapes, números pegados a letras, símbolos y acentos.
        const string code =
            "if (x1 == 42) { s = \"con \\\" escape\"; }\n"
            + "  /* comentario */ t = 'á';\n"
            + "\n"
            + "# almohadilla -- guiones\n"
            + "fin";

        var lines = SyntaxHighlighter.Highlight(code, language);

        Assert.Equal(code, Rebuild(lines));
    }

    [Fact]
    public void An_unknown_language_is_left_alone()
    {
        const string code = "esto no se toca\nsegunda linea";

        var lines = SyntaxHighlighter.Highlight(code, "brainfuck");

        Assert.Equal(code, Rebuild(lines));
        Assert.False(SyntaxHighlighter.IsSupported("brainfuck"));
    }

    [Fact]
    public void Recognises_the_categories_in_csharp()
    {
        var lines = SyntaxHighlighter.Highlight(
            "public int Sumar(int a) { return 42; } // nota", "csharp");

        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lines, "public"));
        Assert.Equal(ConversationStyle.SyntaxType, TokenOf(lines, "int"));
        Assert.Equal(ConversationStyle.SyntaxNumber, TokenOf(lines, "42"));

        // Una palabra seguida de paréntesis se lee como llamada.
        Assert.Equal(ConversationStyle.SyntaxFunction, TokenOf(lines, "Sumar"));

        Assert.Equal(ConversationStyle.SyntaxComment, TokenOf(lines, "// nota"));
    }

    [Fact]
    public void A_block_comment_spans_lines()
    {
        // Es la razón de resaltar el bloque entero y no línea a línea: analizada por separado, la
        // segunda línea no sabría que viene de un comentario abierto.
        var lines = SyntaxHighlighter.Highlight("/* uno\ndos */ int x;", "csharp");

        var second = lines[1];
        Assert.Equal(ConversationStyle.SyntaxComment, second[0].Style);
        Assert.StartsWith("dos", second[0].Text, StringComparison.Ordinal);

        // Y lo de después del cierre ya no es comentario.
        Assert.Equal(ConversationStyle.SyntaxType, TokenOf(lines, "int"));
    }

    [Fact]
    public void An_unterminated_string_does_not_bleed_into_the_next_line()
    {
        var lines = SyntaxHighlighter.Highlight("var s = \"sin cerrar\nint x = 1;", "csharp");

        // Si la cadena se tragara el salto, toda la segunda línea saldría del color de cadena.
        Assert.Equal(ConversationStyle.SyntaxType, TokenOf(lines, "int"));
    }

    [Fact]
    public void Abl_is_case_insensitive_and_accepts_hyphenated_words()
    {
        // En ABL DEFINE y define son la misma palabra, y `no-undo` lleva guion dentro.
        var upper = SyntaxHighlighter.Highlight("DEFINE VARIABLE i AS INTEGER NO-UNDO.", "abl");
        var lower = SyntaxHighlighter.Highlight("define variable i as integer no-undo.", "abl");

        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(upper, "DEFINE"));
        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lower, "define"));
        Assert.Equal(ConversationStyle.SyntaxType, TokenOf(upper, "INTEGER"));
        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(upper, "NO-UNDO"));
    }

    [Fact]
    public void Sql_keywords_ignore_case()
    {
        var lines = SyntaxHighlighter.Highlight("Select * From clientes Where id = 3", "sql");

        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lines, "Select"));
        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lines, "Where"));
        Assert.Equal(ConversationStyle.SyntaxNumber, TokenOf(lines, "3"));
    }

    [Fact]
    public void A_digit_inside_an_identifier_is_not_a_number()
    {
        var lines = SyntaxHighlighter.Highlight("var x1 = 2;", "csharp");

        Assert.NotEqual(ConversationStyle.SyntaxNumber, TokenOf(lines, "x1"));
        Assert.Equal(ConversationStyle.SyntaxNumber, TokenOf(lines, "2"));
    }

    [Fact]
    public void Recognises_the_categories_in_java()
    {
        var lines = SyntaxHighlighter.Highlight(
            "public int sumar(int a, int b) { return a + b; } // nota", "java");

        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lines, "public"));
        Assert.Equal(ConversationStyle.SyntaxType, TokenOf(lines, "int"));
        Assert.Equal(ConversationStyle.SyntaxFunction, TokenOf(lines, "sumar"));
        Assert.Equal(ConversationStyle.SyntaxComment, TokenOf(lines, "// nota"));
    }

    [Fact]
    public void Recognises_the_categories_in_javascript()
    {
        var lines = SyntaxHighlighter.Highlight(
            "export async function cargar(): Promise<string> { return 'ok'; } // nota", "javascript");

        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lines, "export"));
        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lines, "async"));
        Assert.Equal(ConversationStyle.SyntaxFunction, TokenOf(lines, "cargar"));
        Assert.Equal(ConversationStyle.SyntaxType, TokenOf(lines, "Promise"));
        Assert.Equal(ConversationStyle.SyntaxComment, TokenOf(lines, "// nota"));
    }

    [Fact]
    public void Recognises_the_categories_in_kotlin()
    {
        var lines = SyntaxHighlighter.Highlight(
            "data class Persona(val nombre: String) { fun saludar(): String = \"Hola\" } // nota", "kotlin");

        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lines, "data"));
        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lines, "class"));
        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lines, "fun"));
        Assert.Equal(ConversationStyle.SyntaxType, TokenOf(lines, "String"));
        Assert.Equal(ConversationStyle.SyntaxFunction, TokenOf(lines, "saludar"));
        Assert.Equal(ConversationStyle.SyntaxComment, TokenOf(lines, "// nota"));
    }

    [Fact]
    public void Recognises_visual_basic_case_insensitively()
    {
        var lines = SyntaxHighlighter.Highlight(
            "PUBLIC FUNCTION Sumar(valor AS Integer) AS String ' nota", "vbnet");

        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lines, "PUBLIC"));
        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lines, "FUNCTION"));
        Assert.Equal(ConversationStyle.SyntaxType, TokenOf(lines, "Integer"));
        Assert.Equal(ConversationStyle.SyntaxFunction, TokenOf(lines, "Sumar"));
        Assert.Equal(ConversationStyle.SyntaxComment, TokenOf(lines, "' nota"));
    }

    [Fact]
    public void Recognises_power_bi_dax()
    {
        var lines = SyntaxHighlighter.Highlight(
            "Total = VAR Importe = SUM(Ventas[Importe]) RETURN Importe // nota", "powerbi");

        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lines, "VAR"));
        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lines, "RETURN"));
        Assert.Equal(ConversationStyle.SyntaxFunction, TokenOf(lines, "SUM"));
        Assert.Equal(ConversationStyle.SyntaxComment, TokenOf(lines, "// nota"));
    }

    [Fact]
    public void Recognises_power_query_m()
    {
        var lines = SyntaxHighlighter.Highlight(
            "let Source = Table.FromRows({{1}}) in Source // nota", "powerquery");

        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lines, "let"));
        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lines, "in"));
        Assert.Equal(ConversationStyle.SyntaxFunction, TokenOf(lines, "FromRows"));
        Assert.Equal(ConversationStyle.SyntaxComment, TokenOf(lines, "// nota"));
    }

    [Fact]
    public void Recognises_xml_attributes_strings_and_comments()
    {
        var lines = SyntaxHighlighter.Highlight(
            "<Project Version=\"1.0\"><!-- nota --></Project>", "xml");

        Assert.Equal(ConversationStyle.SyntaxString, TokenOf(lines, "\"1.0\""));
        Assert.Equal(ConversationStyle.SyntaxComment, TokenOf(lines, "<!-- nota -->"));
    }

    [Fact]
    public void Recognises_yaml_values_numbers_strings_and_comments()
    {
        var lines = SyntaxHighlighter.Highlight(
            "enabled: true\nretries: 3\nname: \"OmniCoder\" # nota", "yaml");

        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lines, "true"));
        Assert.Equal(ConversationStyle.SyntaxNumber, TokenOf(lines, "3"));
        Assert.Equal(ConversationStyle.SyntaxString, TokenOf(lines, "\"OmniCoder\""));
        Assert.Equal(ConversationStyle.SyntaxComment, TokenOf(lines, "# nota"));
    }

    [Fact]
    public void Recognises_the_categories_in_c()
    {
        var lines = SyntaxHighlighter.Highlight(
            "static int sumar(int a, int b) { return a + b; } /* nota */", "c");

        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lines, "static"));
        Assert.Equal(ConversationStyle.SyntaxType, TokenOf(lines, "int"));
        Assert.Equal(ConversationStyle.SyntaxFunction, TokenOf(lines, "sumar"));

        var comment = lines.SelectMany(l => l).Single(r => r.Text.Contains("nota", StringComparison.Ordinal));
        Assert.Equal(ConversationStyle.SyntaxComment, comment.Style);
    }

    [Fact]
    public void Cpp_is_a_separate_language_from_c_with_its_own_keywords()
    {
        var lines = SyntaxHighlighter.Highlight(
            "class Foo { public: std::string nombre; };", "cpp");

        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lines, "class"));
        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lines, "public"));
        Assert.Equal(ConversationStyle.SyntaxType, TokenOf(lines, "string"));

        // "class" no es palabra reservada en C a secas: es justo lo que distingue a C++.
        Assert.NotEqual(ConversationStyle.SyntaxKeyword, TokenOf(SyntaxHighlighter.Highlight("class", "c"), "class"));
    }

    [Fact]
    public void Abap_is_case_insensitive_like_the_language_itself()
    {
        var upper = SyntaxHighlighter.Highlight("DATA: lv_total TYPE i.", "abap");
        var lower = SyntaxHighlighter.Highlight("data: lv_total type i.", "abap");

        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(upper, "DATA"));
        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(lower, "data"));
        Assert.Equal(ConversationStyle.SyntaxKeyword, TokenOf(upper, "TYPE"));
    }

    [Fact]
    public void Abap_multiplication_is_not_swallowed_as_a_comment()
    {
        // La trampa real: el comentario de línea completa de ABAP empieza con "*" al principio de
        // la línea, pero el resaltador no distingue "principio de línea" de "en cualquier
        // posición". Tratar "*" como marca de comentario sin esa distinción se comería cualquier
        // multiplicación. Por eso "*" NO está en LineComments para este lenguaje: se comprueba que
        // sobrevive como texto normal y no como comentario.
        const string code = "lv_total = lv_a * lv_b.";

        var lines = SyntaxHighlighter.Highlight(code, "abap");

        Assert.Equal(code, Rebuild(lines));
        Assert.NotEqual(ConversationStyle.SyntaxComment, TokenOf(lines, "lv_b"));
    }

    [Fact]
    public void Abap_double_quote_is_a_comment_and_single_quote_is_a_string()
    {
        var lines = SyntaxHighlighter.Highlight(
            """WRITE 'hola' . " esto es un comentario""", "abap");

        Assert.Equal(ConversationStyle.SyntaxString, TokenOf(lines, "'hola'"));

        var comment = lines.SelectMany(l => l)
            .First(r => r.Text.Contains("esto es un comentario", StringComparison.Ordinal));
        Assert.Equal(ConversationStyle.SyntaxComment, comment.Style);
    }

    [Fact]
    public void A_huge_block_is_not_highlighted()
    {
        // El resaltado es un lujo; que la interfaz responda no lo es.
        var code = new string('x', SyntaxHighlighter.MaxLength + 1);

        var lines = SyntaxHighlighter.Highlight(code, "csharp");

        Assert.Equal(code, Rebuild(lines));
        Assert.Single(Assert.Single(lines));
    }

    [Fact]
    public void Aliases_resolve_to_the_same_language()
    {
        Assert.True(SyntaxHighlighter.IsSupported("cs"));
        Assert.True(SyntaxHighlighter.IsSupported("C#"));
        Assert.True(SyntaxHighlighter.IsSupported("TypeScript"));
        Assert.True(SyntaxHighlighter.IsSupported("cjs"));
        Assert.True(SyntaxHighlighter.IsSupported("mts"));
        Assert.True(SyntaxHighlighter.IsSupported("kt"));
        Assert.True(SyntaxHighlighter.IsSupported("kts"));
        Assert.True(SyntaxHighlighter.IsSupported("VB.NET"));
        Assert.True(SyntaxHighlighter.IsSupported("VBA"));
        Assert.True(SyntaxHighlighter.IsSupported("PowerBI"));
        Assert.True(SyntaxHighlighter.IsSupported("PowerQuery-M"));
        Assert.True(SyntaxHighlighter.IsSupported("XAML"));
        Assert.True(SyntaxHighlighter.IsSupported("YML"));
        Assert.True(SyntaxHighlighter.IsSupported("openedge"));

        // La etiqueta del cercado puede traer más cosas detrás.
        Assert.NotNull(Languages.Find("js title=\"a.js\""));
    }

}
