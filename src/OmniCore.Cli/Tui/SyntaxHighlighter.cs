// Adapted from OmniCoder.Core/Syntax/SyntaxHighlighter.cs; no WPF dependency.
using System.Text;

namespace OmniCore.Cli;

/// <summary>
/// Colorea un bloque de código.
/// </summary>
/// <remarks>
/// <para>
/// Un recorrido de un solo paso sobre el texto, parametrizado por <see cref="LanguageDefinition"/>.
/// No entiende el lenguaje ni lo pretende: separa comentarios, cadenas, números, palabras
/// reservadas y tipos del resto. Para leer un fragmento en una tarjeta de chat es todo lo que hace
/// falta, y evita arrastrar un analizador por lenguaje.
/// </para>
/// <para>
/// Devuelve <b>tramos por línea</b> y no líneas enteras porque quien llama —el renderizador de
/// Markdown— ya sabe qué fondo y qué monoespaciado ponerle al bloque; aquí solo se decide el color
/// del texto.
/// </para>
/// <para>
/// Es reentrante y sin estado entre llamadas: el estado del comentario de bloque vive en la pila
/// del recorrido, así que una misma instancia sirve para toda la conversación.
/// </para>
/// </remarks>
internal static class SyntaxHighlighter
{
    /// <summary>Por encima de esto se devuelve el texto sin colorear.</summary>
    /// <remarks>
    /// El resaltado es un lujo; que la interfaz responda no lo es. Un bloque de medio megabyte
    /// pegado en el chat no puede costar un recorrido carácter a carácter en el hilo de UI.
    /// </remarks>
    public const int MaxLength = 200_000;

    public static bool IsSupported(string? language) => Languages.Find(language) is not null;

    /// <summary>
    /// Trocea el código en tramos con color, una lista por línea.
    /// </summary>
    /// <remarks>
    /// Con un lenguaje desconocido o un bloque enorme devuelve una lista por línea con un solo
    /// tramo sin estilo: quien llama no tiene que distinguir el caso.
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<ConversationSpan>> Highlight(string? code, string? language)
    {
        var text = code ?? string.Empty;
        var definition = Languages.Find(language);

        if (definition is null || text.Length > MaxLength)
        {
            return [.. SplitLines(text).Select(l =>
                (IReadOnlyList<ConversationSpan>)(l.Length == 0 ? [] : [new ConversationSpan(l, ConversationStyle.Code)]))];
        }

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return definition.Name switch
        {
            "html" => MarkupSyntaxHighlighter.Html(text),
            "markdown" => MarkupSyntaxHighlighter.Markdown(text),
            _ => new Scanner(text, definition).Run(),
        };
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');


    /// <summary>El recorrido. Vive solo el tiempo de una llamada.</summary>
    private sealed class Scanner(string text, LanguageDefinition language)
    {
        private readonly List<IReadOnlyList<ConversationSpan>> _lines = [];
        private readonly List<ConversationSpan> _current = [];
        private readonly StringBuilder _pending = new();

        private ConversationStyle _pendingToken = ConversationStyle.Code;
        private int _index;

        public IReadOnlyList<IReadOnlyList<ConversationSpan>> Run()
        {
            while (_index < text.Length)
            {
                var c = text[_index];

                if (c == '\r')
                {
                    _index++;
                    continue;
                }

                if (c == '\n')
                {
                    FlushLine();
                    _index++;
                    continue;
                }

                if (TryComment() || TryString() || TryNumber() || TryWord())
                {
                    continue;
                }

                // Signos y espacios: lo que queda. Los espacios van sin token propio para no
                // trocear la línea en un tramo por hueco.
                Append(char.IsWhiteSpace(c) ? ConversationStyle.Code : ConversationStyle.SyntaxPunctuation, c);
                _index++;
            }

            FlushLine();
            return _lines;
        }

        private bool TryComment()
        {
            foreach (var prefix in language.LineComments)
            {
                if (Matches(prefix))
                {
                    // Hasta el salto de línea: el salto lo procesa el bucle principal.
                    while (_index < text.Length && text[_index] is not ('\n' or '\r'))
                    {
                        Append(ConversationStyle.SyntaxComment, text[_index++]);
                    }

                    return true;
                }
            }

            if (language.BlockComment is not { } block || !Matches(block.Open))
            {
                return false;
            }

            // Un comentario de bloque cruza líneas: el salto se emite dentro del bucle para que
            // cada línea reciba su propio tramo, pero sin salir del estado de comentario.
            Append(ConversationStyle.SyntaxComment, block.Open);
            _index += block.Open.Length;

            while (_index < text.Length)
            {
                if (Matches(block.Close))
                {
                    Append(ConversationStyle.SyntaxComment, block.Close);
                    _index += block.Close.Length;
                    return true;
                }

                if (text[_index] == '\n')
                {
                    FlushLine();
                    _index++;
                    continue;
                }

                Append(ConversationStyle.SyntaxComment, text[_index++]);
            }

            // Sin cerrar: el bloque de código se acabó dentro del comentario. No es un error.
            return true;
        }

        private bool TryString()
        {
            var quote = text[_index];

            if (!language.StringDelimiters.Contains(quote))
            {
                return false;
            }

            Append(ConversationStyle.SyntaxString, quote);
            _index++;

            while (_index < text.Length)
            {
                var c = text[_index];

                // Una cadena sin cerrar no debe teñir el resto del bloque: se corta en la línea.
                if (c == '\n')
                {
                    return true;
                }

                // La barra escapa la comilla siguiente, que por tanto no cierra.
                if (c == '\\' && _index + 1 < text.Length && text[_index + 1] != '\n')
                {
                    Append(ConversationStyle.SyntaxString, c);
                    Append(ConversationStyle.SyntaxString, text[_index + 1]);
                    _index += 2;
                    continue;
                }

                Append(ConversationStyle.SyntaxString, c);
                _index++;

                if (c == quote)
                {
                    return true;
                }
            }

            return true;
        }

        private bool TryNumber()
        {
            var c = text[_index];

            if (!char.IsAsciiDigit(c))
            {
                return false;
            }

            // No es un número si viene pegado detrás de una palabra: `x1` es un identificador.
            if (_index > 0 && (char.IsLetter(text[_index - 1]) || text[_index - 1] == '_'))
            {
                return false;
            }

            while (_index < text.Length &&
                   (char.IsAsciiLetterOrDigit(text[_index]) || text[_index] == '.' || text[_index] == '_'))
            {
                Append(ConversationStyle.SyntaxNumber, text[_index++]);
            }

            return true;
        }

        private bool TryWord()
        {
            var c = text[_index];

            if (!char.IsLetter(c) && c != '_')
            {
                return false;
            }

            var start = _index;

            // El guion forma parte de la palabra en ABL (`no-undo`, `temp-table`); en los demás
            // lenguajes no aparece dentro de un identificador, así que no estorba.
            while (_index < text.Length &&
                   (char.IsLetterOrDigit(text[_index]) || text[_index] is '_' or '-'))
            {
                _index++;
            }

            var word = text[start.._index].TrimEnd('-');
            _index = start + word.Length;

            var token = language.Keywords.Contains(word) ? ConversationStyle.SyntaxKeyword
                : language.Types.Contains(word) ? ConversationStyle.SyntaxType
                : LooksLikeCall() ? ConversationStyle.SyntaxFunction
                : ConversationStyle.SyntaxVariable;

            Append(token, word);
            return true;
        }

        /// <summary>Una palabra seguida de paréntesis se pinta como llamada.</summary>
        private bool LooksLikeCall()
        {
            var ahead = _index;

            while (ahead < text.Length && text[ahead] == ' ')
            {
                ahead++;
            }

            return ahead < text.Length && text[ahead] == '(';
        }

        private bool Matches(string value) =>
            string.CompareOrdinal(text, _index, value, 0, value.Length) == 0 &&
            _index + value.Length <= text.Length;

        private void Append(ConversationStyle token, char c)
        {
            if (token != _pendingToken)
            {
                FlushRun();
                _pendingToken = token;
            }

            _pending.Append(c);
        }

        private void Append(ConversationStyle token, string value)
        {
            foreach (var c in value)
            {
                Append(token, c);
            }
        }

        private void FlushRun()
        {
            if (_pending.Length == 0)
            {
                return;
            }

            _current.Add(new ConversationSpan(_pending.ToString(), _pendingToken));
            _pending.Clear();
        }

        private void FlushLine()
        {
            var token = _pendingToken;

            FlushRun();

            // Una línea vacía se devuelve sin tramos. Meterle un espacio para que la vista no la
            // colapse sería alterar el texto, y quien pide un resaltado espera de vuelta lo mismo
            // que entregó. Ese apaño es cosa de quien pinta, y allí está.
            _lines.Add(_current.Count > 0 ? [.. _current] : []);
            _current.Clear();

            // El estado del comentario de bloque sobrevive al salto de línea.
            _pendingToken = token;
        }
    }
}
