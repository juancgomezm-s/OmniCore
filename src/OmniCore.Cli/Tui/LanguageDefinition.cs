// Adapted from OmniCoder.Core/Syntax/LanguageDefinition.cs.
namespace OmniCore.Cli;

/// <summary>
/// Lo que distingue a un lenguaje para el resaltador.
/// </summary>
/// <remarks>
/// No hay un analizador por lenguaje: hay <b>uno solo</b> parametrizado con esto. Resaltar código
/// en una tarjeta de chat no necesita entender el lenguaje, solo separar comentarios, cadenas,
/// números y palabras reservadas del resto. Un analizador de verdad por cada lenguaje sería mucho
/// código para una diferencia que no se ve.
/// </remarks>
/// <param name="Name">Nombre canónico, el que se enseña sobre el bloque.</param>
/// <param name="Aliases">Cómo lo escribe la gente en el cercado: <c>cs</c>, <c>c#</c>, <c>csharp</c>…</param>
/// <param name="Keywords">Palabras reservadas del lenguaje.</param>
/// <param name="Types">Tipos reconocidos por el resaltador.</param>
/// <param name="LineComments">Prefijos que comentan hasta el final de la línea.</param>
/// <param name="BlockComment">Delimitadores de comentario multilínea, si los hay.</param>
/// <param name="StringDelimiters">Comillas que abren y cierran una cadena.</param>
/// <param name="CaseSensitive">Si <c>IF</c> e <c>if</c> son la misma palabra reservada.</param>
internal sealed record LanguageDefinition(
    string Name,
    IReadOnlyList<string> Aliases,
    IReadOnlySet<string> Keywords,
    IReadOnlySet<string> Types,
    IReadOnlyList<string> LineComments,
    (string Open, string Close)? BlockComment,
    IReadOnlyList<char> StringDelimiters,
    bool CaseSensitive = true);

/// <summary>Los lenguajes que el resaltador conoce.</summary>
/// <remarks>
/// La lista es corta y deliberada: los que salen de verdad en una conversación con un agente de
/// código. Un lenguaje desconocido no es un fallo —se pinta en monoespaciado sin colores, que es
/// exactamente lo que había antes—, así que ampliar la lista es añadir una entrada aquí.
/// </remarks>
internal static class Languages
{
    private static IReadOnlySet<string> Set(bool caseSensitive, params string[] words) =>
        new HashSet<string>(words, caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    public static readonly LanguageDefinition CSharp = new(
        "csharp",
        ["cs", "c#", "csharp", "dotnet"],
        Set(true,
            "abstract", "as", "async", "await", "base", "break", "case", "catch", "checked", "class",
            "const", "continue", "default", "delegate", "do", "else", "enum", "event", "explicit",
            "extern", "finally", "fixed", "for", "foreach", "get", "goto", "if", "implicit", "in",
            "init", "interface", "internal", "is", "lock", "namespace", "new", "operator", "out",
            "override", "params", "partial", "private", "protected", "public", "readonly", "record",
            "ref", "return", "sealed", "set", "sizeof", "stackalloc", "static", "struct", "switch",
            "this", "throw", "try", "typeof", "unchecked", "unsafe", "using", "var", "virtual",
            "volatile", "when", "where", "while", "yield", "true", "false", "null", "nameof"),
        Set(true,
            "bool", "byte", "char", "decimal", "double", "float", "int", "long", "object", "sbyte",
            "short", "string", "uint", "ulong", "ushort", "void", "dynamic", "Task", "List",
            "Dictionary", "IEnumerable", "IReadOnlyList", "Span", "ReadOnlySpan"),
        ["//"],
        ("/*", "*/"),
        ['"', '\'']);

    public static readonly LanguageDefinition JavaScript = new(
        "javascript",
        ["js", "javascript", "ts", "typescript", "jsx", "tsx", "mjs", "cjs", "mts", "cts", "node"],
        Set(true,
            "as", "async", "await", "break", "case", "catch", "class", "const", "continue",
            "debugger", "default", "delete", "do", "else", "enum", "export", "extends", "finally",
            "for", "from", "function", "get", "if", "implements", "import", "in", "instanceof",
            "interface", "let", "new", "of", "private", "protected", "public", "readonly", "return",
            "set", "static", "super", "switch", "this", "throw", "try", "type", "typeof", "var",
            "void", "while", "yield", "true", "false", "null", "undefined"),
        Set(true, "string", "number", "boolean", "any", "unknown", "never", "object", "Promise",
            "Array", "Map", "Set", "Record"),
        ["//"],
        ("/*", "*/"),
        ['"', '\'', '`']);

    public static readonly LanguageDefinition Python = new(
        "python",
        ["py", "python", "python3"],
        Set(true,
            "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del",
            "elif", "else", "except", "finally", "for", "from", "global", "if", "import", "in",
            "is", "lambda", "match", "nonlocal", "not", "or", "pass", "raise", "return", "try",
            "while", "with", "yield", "True", "False", "None"),
        Set(true, "int", "float", "str", "bool", "bytes", "list", "dict", "set", "tuple", "self"),
        ["#"],
        BlockComment: null,
        ['"', '\'']);

    public static readonly LanguageDefinition Sql = new(
        "sql",
        ["sql", "mysql", "postgres", "postgresql", "tsql", "plsql"],
        Set(false,
            "select", "from", "where", "join", "inner", "left", "right", "outer", "full", "on",
            "group", "by", "order", "having", "insert", "into", "values", "update", "set", "delete",
            "create", "alter", "drop", "table", "view", "index", "and", "or", "not", "null", "as",
            "distinct", "union", "all", "case", "when", "then", "else", "end", "limit", "offset",
            "with", "exists", "in", "between", "like", "is", "asc", "desc", "primary", "key",
            "foreign", "references", "default", "constraint"),
        Set(false, "int", "integer", "bigint", "smallint", "varchar", "nvarchar", "char", "text",
            "date", "datetime", "timestamp", "boolean", "decimal", "numeric", "float", "real"),
        ["--"],
        ("/*", "*/"),
        ['\'', '"'],
        CaseSensitive: false);

    /// <summary>
    /// Progress ABL / OpenEdge.
    /// </summary>
    /// <remarks>
    /// Está porque es lo que usa quien va a mirar estos bloques. No distingue mayúsculas —en ABL
    /// <c>DEFINE</c> y <c>define</c> son la misma palabra— y sus comentarios son <c>/* */</c>,
    /// además anidables, aunque anidar no se contempla aquí.
    /// </remarks>
    public static readonly LanguageDefinition Abl = new(
        "abl",
        ["abl", "openedge", "progress", "4gl", "p"],
        Set(false,
            "define", "variable", "as", "no-undo", "for", "each", "end", "if", "then", "else",
            "do", "while", "repeat", "leave", "next", "return", "procedure", "function", "run",
            "assign", "find", "first", "last", "where", "no-lock", "exclusive-lock", "share-lock",
            "available", "create", "delete", "buffer", "temp-table", "field", "index", "like",
            "message", "view-as", "alert-box", "display", "with", "frame", "input", "output",
            "parameter", "output-to", "input-from", "close", "def", "and", "or", "not", "by",
            "break", "transaction", "no-error", "of", "to", "in", "using", "class", "method",
            "constructor", "destructor", "public", "private", "protected", "static", "override",
            "this-object", "super", "new", "catch", "finally", "throw", "undo", "true", "false"),
        Set(false,
            "character", "integer", "int64", "decimal", "logical", "date", "datetime", "datetime-tz",
            "handle", "longchar", "memptr", "rowid", "recid", "raw", "blob", "clob"),
        ["//"],
        ("/*", "*/"),
        ['"', '\''],
        CaseSensitive: false);

    public static readonly LanguageDefinition Java = new(
        "java",
        ["java"],
        Set(true,
            "abstract", "assert", "break", "case", "catch", "class", "continue", "default", "do",
            "else", "enum", "extends", "final", "finally", "for", "if", "implements", "import",
            "instanceof", "interface", "native", "new", "package", "private", "protected", "public",
            "record", "return", "sealed", "static", "strictfp", "super", "switch", "synchronized",
            "this", "throw", "throws", "transient", "try", "var", "while", "yield", "permits",
            "true", "false", "null"),
        Set(true,
            "boolean", "byte", "char", "double", "float", "int", "long", "short", "void",
            "String", "Integer", "Long", "Double", "Float", "Boolean", "Character", "Object",
            "List", "Map", "Set", "ArrayList", "HashMap", "Optional"),
        ["//"],
        ("/*", "*/"),
        ['"', '\'']);

    public static readonly LanguageDefinition Kotlin = new(
        "kotlin",
        ["kt", "kotlin", "kts"],
        Set(true,
            "as", "break", "by", "catch", "class", "companion", "constructor", "continue",
            "data", "delegate", "do", "else", "enum", "expect", "external", "false", "field",
            "file", "final", "finally", "for", "fun", "get", "if", "import", "in", "infix",
            "init", "inline", "inner", "interface", "internal", "is", "lateinit", "noinline",
            "null", "object", "open", "operator", "out", "override", "package", "private",
            "protected", "public", "receiver", "reified", "return", "sealed", "set", "setparam",
            "super", "suspend", "tailrec", "this", "throw", "true", "try", "typealias", "typeof",
            "val", "value", "var", "vararg", "when", "where", "while", "actual", "abstract",
            "annotation", "const", "crossinline", "dynamic"),
        Set(true,
            "Any", "Boolean", "Byte", "Char", "Double", "Float", "Int", "Long", "Nothing",
            "Number", "Short", "String", "Unit", "Array", "List", "MutableList", "Map",
            "MutableMap", "Set", "MutableSet", "Sequence", "Pair", "Triple", "Result"),
        ["//"],
        ("/*", "*/"),
        ['"', '\'']);

    public static readonly LanguageDefinition VisualBasic = new(
        "visualbasic",
        ["vb", "visualbasic", "visual-basic", "vbnet", "vb.net", "vba", "vbs"],
        Set(false,
            "addhandler", "addressof", "alias", "and", "andalso", "as", "async", "await", "boolean",
            "byref", "byte", "byval", "call", "case", "catch", "class", "const", "continue", "date",
            "decimal", "declare", "default", "delegate", "dim", "directcast", "do", "each", "else",
            "elseif", "end", "enum", "erase", "error", "event", "exit", "false", "finally", "for",
            "friend", "function", "get", "gettype", "getxmlnamespace", "global", "gosub", "goto",
            "handles", "if", "implements", "imports", "in", "inherits", "interface", "is", "isnot",
            "let", "lib", "like", "loop", "me", "mod", "module", "mustinherit", "mustoverride", "mybase",
            "myclass", "namespace", "narrowing", "new", "next", "not", "nothing", "notinheritable",
            "notoverridable", "object", "of", "on", "operator", "option", "optional", "or", "orelse",
            "overloads", "overridable", "overrides", "paramarray", "partial", "private", "property",
            "protected", "public", "raiseevent", "readonly", "redim", "removehandler", "resume", "return",
            "select", "set", "shadows", "shared", "static", "step", "stop", "structure", "sub", "synclock",
            "then", "throw", "to", "true", "try", "trycast", "typeof", "using", "variant", "wend", "when",
            "while", "widening", "with", "withevents", "writeonly", "xor"),
        Set(false,
            "Boolean", "Byte", "Char", "Date", "Decimal", "Double", "Integer", "Long", "Object", "SByte",
            "Short", "Single", "String", "UInteger", "ULong", "UShort", "Variant", "Collection", "Dictionary",
            "List", "Task"),
        ["'"],
        BlockComment: null,
        ['"'],
        CaseSensitive: false);

    public static readonly LanguageDefinition Dax = new(
        "dax",
        ["dax", "powerbi", "power-bi", "pbi"],
        Set(false,
            "define", "evaluate", "measure", "order", "by", "start", "at", "var", "return", "if", "switch",
            "true", "false", "blank", "and", "or", "not", "in", "asc", "desc"),
        Set(false, "boolean", "currency", "datetime", "decimal", "double", "integer", "string", "variant"),
        ["//", "--"],
        ("/*", "*/"),
        ['"', '\''],
        CaseSensitive: false);

    public static readonly LanguageDefinition PowerQuery = new(
        "powerquery",
        ["powerquery", "power-query", "powerquery-m", "mquery", "pq", "pqm"],
        Set(true,
            "and", "as", "each", "else", "error", "false", "if", "in", "is", "let", "meta", "not", "null",
            "or", "otherwise", "section", "shared", "then", "true", "try", "type"),
        Set(true,
            "any", "anynonnull", "binary", "date", "datetime", "datetimezone", "duration", "function", "list",
            "logical", "none", "null", "number", "record", "table", "text", "time", "type"),
        ["//"],
        ("/*", "*/"),
        ['"']);

    public static readonly LanguageDefinition Xml = new(
        "xml",
        ["xml", "xaml", "xsd", "xslt", "xsl", "svg", "resx", "csproj", "vbproj", "props", "targets"],
        Set(false, "xml", "version", "encoding", "standalone", "xmlns", "schema", "stylesheet", "template"),
        Set(false, "string", "boolean", "decimal", "integer", "date", "datetime"),
        LineComments: [],
        ("<!--", "-->"),
        ['"', '\''],
        CaseSensitive: false);

    public static readonly LanguageDefinition Yaml = new(
        "yaml",
        ["yaml", "yml"],
        Set(false,
            "true", "false", "yes", "no", "on", "off", "null", "include", "extends", "anchors", "aliases"),
        Set(false, "str", "string", "int", "integer", "float", "bool", "boolean", "timestamp", "binary", "map", "seq", "set"),
        ["#"],
        BlockComment: null,
        ['"', '\''],
        CaseSensitive: false);

    public static readonly LanguageDefinition C = new(
        "c",
        ["c", "h"],
        Set(true,
            "auto", "break", "case", "const", "continue", "default", "do", "else", "enum", "extern",
            "for", "goto", "if", "inline", "register", "restrict", "return", "sizeof", "static",
            "struct", "switch", "typedef", "union", "volatile", "while", "NULL"),
        Set(true,
            "int", "char", "float", "double", "long", "short", "unsigned", "signed", "void", "bool",
            "size_t", "ssize_t", "int8_t", "int16_t", "int32_t", "int64_t",
            "uint8_t", "uint16_t", "uint32_t", "uint64_t", "FILE"),
        ["//"],
        ("/*", "*/"),
        ['"', '\'']);

    /// <summary>
    /// C++. Distinto de <see cref="C"/> y no una variación de la misma entrada porque el resaltador
    /// no compone conjuntos de palabras entre lenguajes —son datos planos, no herencia—, y aquí la
    /// duplicación es más simple que inventar esa composición para dos casos.
    /// </summary>
    public static readonly LanguageDefinition Cpp = new(
        "cpp",
        ["cpp", "cc", "cxx", "hpp", "hh", "c++", "cplusplus"],
        Set(true,
            "auto", "break", "case", "catch", "class", "const", "constexpr", "continue", "decltype",
            "default", "delete", "do", "else", "enum", "explicit", "export", "extern", "final", "for",
            "friend", "goto", "if", "inline", "mutable", "namespace", "new", "noexcept", "operator",
            "override", "private", "protected", "public", "register", "restrict", "return", "sizeof",
            "static", "static_assert", "struct", "switch", "template", "this", "thread_local", "throw",
            "try", "typedef", "typename", "union", "using", "virtual", "volatile", "while",
            "nullptr", "true", "false"),
        Set(true,
            "int", "char", "float", "double", "long", "short", "unsigned", "signed", "void", "bool",
            "size_t", "int8_t", "int16_t", "int32_t", "int64_t", "uint8_t", "uint16_t", "uint32_t",
            "uint64_t", "string", "vector", "map", "set", "unordered_map", "shared_ptr", "unique_ptr",
            "auto"),
        ["//"],
        ("/*", "*/"),
        ['"', '\'']);

    /// <summary>
    /// SAP ABAP. No confundir con <see cref="Abl"/> (Progress OpenEdge): el nombre se parece, el
    /// lenguaje no tiene nada que ver.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sin distinguir mayúsculas, como el propio lenguaje. Las cadenas van solo con comilla simple
    /// —ABAP no usa comilla doble para cadenas—.
    /// </para>
    /// <para>
    /// <b>El comentario de línea completa (<c>*</c> al principio de la línea) no está soportado a
    /// propósito.</b> El resaltador no distingue «principio de línea» de «en cualquier posición», y
    /// tratar <c>*</c> como marca de comentario sin esa distinción convertiría cualquier
    /// multiplicación (<c>lv_total = lv_a * lv_b</c>) en un comentario que se traga el resto de la
    /// línea. Sí se soporta <c>"</c>, que en ABAP solo puede significar un comentario y nunca una
    /// cadena, así que ahí no hay ambigüedad que perder.
    /// </para>
    /// </remarks>
    public static readonly LanguageDefinition Abap = new(
        "abap",
        ["abap", "sap", "sapabap"],
        Set(false,
            "report", "data", "types", "class", "endclass", "class-methods", "class-data", "method",
            "endmethod", "form", "endform", "perform", "if", "endif", "elseif", "else", "loop",
            "endloop", "select", "single", "from", "where", "into", "table", "write", "move", "call",
            "function", "exporting", "importing", "changing", "exceptions", "returning", "raising",
            "try", "catch", "endtry", "do", "enddo", "while", "endwhile", "case", "when", "endcase",
            "is", "initial", "not", "and", "or", "eq", "ne", "lt", "gt", "le", "ge", "co", "cs", "cp",
            "module", "endmodule", "define", "refresh", "clear", "append", "delete", "modify",
            "insert", "update", "commit", "rollback", "work", "using", "tables", "value", "type",
            "like", "ref", "to", "public", "private", "protected", "section", "constants",
            "field-symbols", "assign", "unassign", "check", "exit", "continue", "stop", "return",
            "concatenate", "split", "condense", "translate", "upper", "lower", "begin", "of", "end",
            "occurs", "with", "header", "line", "sy", "sort", "ascending", "descending", "read",
            "key", "binary", "search", "abap_true", "abap_false", "true", "false"),
        Set(false, "string", "xstring"),
        ["\""],
        BlockComment: null,
        ['\''],
        CaseSensitive: false);

    public static readonly LanguageDefinition Shell = new(
        "shell",
        ["sh", "bash", "zsh", "shell", "console", "powershell", "ps1", "pwsh", "cmd", "bat"],
        Set(true,
            "if", "then", "else", "elif", "fi", "for", "while", "do", "done", "case", "esac",
            "function", "return", "export", "local", "in", "echo", "cd", "exit", "source",
            "param", "foreach", "try", "catch", "finally", "throw"),
        Set(true, "true", "false"),
        ["#"],
        BlockComment: null,
        ['"', '\'']);

    public static readonly LanguageDefinition Json = new(
        "json",
        ["json", "jsonc", "jsonl"],
        Set(true, "true", "false", "null"),
        Types: Set(true),
        LineComments: [],
        BlockComment: null,
        ['"']);

    public static readonly LanguageDefinition Html = new(
        "html", ["html", "htm", "html5"], Set(false), Set(false), [], ("<!--", "-->"), ['"', '\''], false);

    public static readonly LanguageDefinition Css = new(
        "css", ["css"],
        Set(false, "important", "inherit", "initial", "unset", "none", "auto", "block", "flex", "grid",
            "relative", "absolute", "fixed", "solid", "transparent", "red", "blue", "white", "black"),
        Set(false, "color", "background", "background-color", "display", "padding", "margin", "width",
            "height", "font-size", "font-family", "border", "position", "gap", "content", "opacity"),
        [], ("/*", "*/"), ['"', '\''], false);

    public static readonly LanguageDefinition Markdown = new(
        "markdown", ["markdown", "md", "mdown", "mkd"], Set(true), Set(true), [], null, []);

    public static readonly IReadOnlyList<LanguageDefinition> All =
        [CSharp, JavaScript, Python, Sql, Abl, Java, Kotlin, VisualBasic, Dax, PowerQuery, Xml, Yaml,
         C, Cpp, Abap, Shell, Json, Html, Css, Markdown];

    private static readonly Dictionary<string, LanguageDefinition> ByAlias = BuildIndex();

    private static Dictionary<string, LanguageDefinition> BuildIndex()
    {
        var index = new Dictionary<string, LanguageDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var language in All)
        {
            foreach (var alias in language.Aliases)
            {
                index[alias] = language;
            }
        }

        return index;
    }

    /// <summary>El lenguaje de una etiqueta de cercado, o <c>null</c> si no se conoce.</summary>
    /// <remarks>
    /// La etiqueta puede traer más cosas —<c>```js title="a.js"</c>—, así que solo se mira la
    /// primera palabra.
    /// </remarks>
    public static LanguageDefinition? Find(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var first = label.Trim().Split(' ', ':', ',')[0];

        return ByAlias.GetValueOrDefault(first);
    }
}
