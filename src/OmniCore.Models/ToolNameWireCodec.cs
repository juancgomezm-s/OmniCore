namespace OmniCore.Models;

/// <summary>
/// Codificación biyectiva (inyectiva y reversible) del nombre de tool en la frontera de transporte
/// hacia endpoints que imponen el charset de function-name <c>[A-Za-z0-9_-]+</c>. Lo aplican la
/// Chat Completions API (<c>function.name</c>, descrito en el OpenAPI oficial como
/// "Must be a-z, A-Z, 0-9, or contain underscores and dashes") y la Responses API
/// (<c>FunctionToolParam.name</c>, <c>pattern: ^[a-zA-Z0-9_-]+$</c>). Los ToolId canónicos de
/// OmniCore usan punto (<c>filesystem.read</c>, <c>plan.propose</c>, <c>user.ask</c>…), así que se
/// codifican al salir y se decodifican al entrar; la resolución de tool y su autorización siguen
/// operando sobre el ToolId canónico (INV-013/ADR-0024). La codificación no depende del nombre del
/// provider (INV-007).
/// </summary>
internal static class ToolNameWireCodec
{
    /// <summary>Codifica un nombre canónico a la forma de transporte válida en el wire.</summary>
    public static string Encode(string canonical)
    {
        if (canonical.Length == 0) return canonical;
        var builder = new System.Text.StringBuilder(canonical.Length + 4);
        foreach (var c in canonical)
        {
            switch (c)
            {
                case '.': builder.Append('-'); break;
                case '_': builder.Append("_u"); break;
                case '-': builder.Append("_m"); break;
                default: builder.Append(c); break;
            }
        }
        return builder.ToString();
    }

    /// <summary>Inversa exacta de <see cref="Encode"/>.</summary>
    public static string Decode(string wire)
    {
        if (wire.IndexOf('_') < 0 && wire.IndexOf('-') < 0) return wire;
        var builder = new System.Text.StringBuilder(wire.Length);
        for (var i = 0; i < wire.Length; i++)
        {
            var c = wire[i];
            if (c == '_' && i + 1 < wire.Length && (wire[i + 1] == 'u' || wire[i + 1] == 'm'))
            {
                builder.Append(wire[i + 1] == 'u' ? '_' : '-');
                i++;
            }
            else if (c == '-')
            {
                builder.Append('.');
            }
            else
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }
}
