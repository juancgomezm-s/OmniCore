namespace OmniCore.Tools;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Token de versión de archivo (ADR-0044 §5): SHA-256 hex de los BYTES REALES del archivo,
/// no del string decodificado. Así el token es estable ante BOM/encoding y coincide entre
/// filesystem.read (que lo expone al modelo) y filesystem.patch (que lo verifica antes de
/// mutar). Soporta UTF-8 (con/sin BOM) y UTF-16 (LE/BE con BOM); cualquier otro BOM se
/// rechaza explícitamente sin modificar el archivo.
/// </summary>
public static class FileVersion
{
    /// <summary>Código estable del error tipado de escritura sobre contenido obsoleto (spec §71).
    /// Única fuente de verdad: la constante tipada del código en Domain.</summary>
    public const string StaleWriteCode = OmniCore.Domain.ToolErrorCode.StaleWriteCode;

    /// <summary>Mensaje de error tipado: prefijo de código estable + token vigente [version:…] para releer.</summary>
    public static string StaleWriteMessage(string currentVersion) =>
        StaleWriteCode + ": el contenido del archivo cambió desde la lectura; no se escribió nada. "
        + "Versión actual [version:" + currentVersion + "]. Reléelo e intenta de nuevo.";

    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };
    private static readonly byte[] Utf16LeBom = { 0xFF, 0xFE };
    private static readonly byte[] Utf16BeBom = { 0xFE, 0xFF };
    private static readonly byte[] Utf32LeBom = { 0xFF, 0xFE, 0x00, 0x00 };
    private static readonly byte[] Utf32BeBom = { 0x00, 0x00, 0xFE, 0xFF };

    // Decoders estrictos: los bytes inválidos lanzan DecoderFallbackException en vez de
    // producir caracteres de reemplazo (un patch nunca debe corromper bytes, ADR-0044 §5).
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16Le = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16Be = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>SHA-256 hex de los bytes reales del contenido. Determinista y comparable.</summary>
    public static string VersionToken(byte[] contentBytes)
    {
        var hash = SHA256.HashData(contentBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Token de versión de un archivo: lee los bytes reales y aplica SHA-256.</summary>
    public static string VersionTokenForFile(string fullPath)
    {
        return VersionToken(File.ReadAllBytes(fullPath));
    }

    /// <summary>Modo de encoding detectado desde el BOM (o UTF-8 por defecto sin BOM).</summary>
    public enum FileEncoding
    {
        Utf8NoBom,
        Utf8Bom,
        Utf16LeBom,
        Utf16BeBom,
    }

    /// <summary>
    /// Número de líneas de un texto (ADR-0044 §5): segmentos separados por '\n'; el segmento
    /// final sin salto de línea cuenta como línea; "" tiene 0 líneas.
    /// </summary>
    public static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var lines = 0;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        if (text[text.Length - 1] != '\n')
        {
            lines++;
        }

        return lines;
    }

    /// <summary>
    /// Diferencia de líneas entre el contenido original y el resultante de una mutación
    /// (ADR-0044 §5): <paramref name="deleted"/> = líneas del original que NO sobreviven,
    /// <paramref name="inserted"/> = líneas nuevas que no estaban en el original. El matching
    /// es por multiconjunto (contando repeticiones): una línea MOVIDA sobrevive con otra
    /// grafía y no cuenta como cambio (no se reescribió contenido, solo se reubicó); una línea
    /// EDITADA cuenta como borrada + insertada. Determinista y O(n).
    ///
    /// El PRESUPUESTO por Turn suma ambas (deleted + inserted); el RATIO de reescritura usa
    /// solo las borradas sobre las líneas originales: mide cuánto del contenido PREVIO se
    /// reescribió, no cuánto creció el archivo (una creación o un append puro no reescriben
    /// nada existente).
    /// </summary>
    public static void ChangedLines(string originalText, string newText, out int deleted, out int inserted)
    {
        var oldLines = SplitLines(originalText);
        var newLines = SplitLines(newText);

        var surviving = new Dictionary<string, int>(oldLines.Count);
        foreach (var line in oldLines)
        {
            surviving[line] = surviving.TryGetValue(line, out var count) ? count + 1 : 1;
        }

        var matched = 0;
        foreach (var line in newLines)
        {
            if (surviving.TryGetValue(line, out var count) && count > 0)
            {
                surviving[line] = count - 1;
                matched++;
            }
        }

        deleted = oldLines.Count - matched;
        inserted = newLines.Count - matched;
    }

    /// <summary>Segmentos de texto entre saltos de línea; el tramo final sin salto cuenta como línea.</summary>
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return lines;
        }

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(text.Substring(start, i - start));
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text.Substring(start));
        }

        return lines;
    }

    /// <summary>Contenido decodido junto con el modo de encoding usado.</summary>
    public readonly struct DecodedFile
    {
        public string Text { get; }
        public FileEncoding Encoding { get; }

        public DecodedFile(string text, FileEncoding encoding)
        {
            Text = text;
            Encoding = encoding;
        }
    }

    /// <summary>
    /// Detecta el encoding por BOM y decodifica estrictamente. Lanza
    /// <see cref="UnsupportedEncodingException"/> si el BOM no es soportado (p. ej. UTF-32) o si
    /// los bytes no son UTF-8/UTF-16 válidos, sin tocar el archivo.
    /// </summary>
    public static DecodedFile Decode(byte[] contentBytes)
    {
        // UTF-32 se rechaza ANTES que UTF-16: el BOM UTF-32 LE (FF FE 00 00) empieza con el
        // prefijo del BOM UTF-16 LE (FF FE) y, si no, se clasificaría mal como UTF-16 LE.
        if (StartsWith(contentBytes, Utf32LeBom) || StartsWith(contentBytes, Utf32BeBom))
        {
            throw new UnsupportedEncodingException("encoding no soportado: UTF-32");
        }

        if (StartsWith(contentBytes, Utf8Bom))
        {
            return new DecodedFile(DecodeStrict(StrictUtf8, contentBytes, 3, contentBytes.Length - 3), FileEncoding.Utf8Bom);
        }

        if (StartsWith(contentBytes, Utf16LeBom))
        {
            return new DecodedFile(DecodeStrict(StrictUtf16Le, contentBytes, 2, contentBytes.Length - 2),
                FileEncoding.Utf16LeBom);
        }

        if (StartsWith(contentBytes, Utf16BeBom))
        {
            return new DecodedFile(DecodeStrict(StrictUtf16Be, contentBytes, 2, contentBytes.Length - 2),
                FileEncoding.Utf16BeBom);
        }

        return new DecodedFile(DecodeStrict(StrictUtf8, contentBytes, 0, contentBytes.Length), FileEncoding.Utf8NoBom);
    }

    /// <summary>Decodifica estrictamente: bytes inválidos → UnsupportedEncodingException (sin mutar).</summary>
    private static string DecodeStrict(Encoding encoding, byte[] bytes, int index, int count)
    {
        try
        {
            return encoding.GetString(bytes, index, count);
        }
        catch (DecoderFallbackException ex)
        {
            throw new UnsupportedEncodingException("encoding inválido o secuencia de bytes corrupta en la posición " + ex.Index);
        }
    }

    /// <summary>Re-codifica el texto al mismo modo de encoding (conservando el BOM) para reescribir.</summary>
    public static byte[] Encode(string text, FileEncoding encoding)
    {
        switch (encoding)
        {
            case FileEncoding.Utf8Bom:
            {
                var body = Encoding.UTF8.GetBytes(text);
                var result = new byte[3 + body.Length];
                Array.Copy(Utf8Bom, result, 3);
                Array.Copy(body, 0, result, 3, body.Length);
                return result;
            }
            case FileEncoding.Utf16LeBom:
            {
                var body = Encoding.Unicode.GetBytes(text);
                var result = new byte[2 + body.Length];
                Array.Copy(Utf16LeBom, result, 2);
                Array.Copy(body, 0, result, 2, body.Length);
                return result;
            }
            case FileEncoding.Utf16BeBom:
            {
                var body = Encoding.BigEndianUnicode.GetBytes(text);
                var result = new byte[2 + body.Length];
                Array.Copy(Utf16BeBom, result, 2);
                Array.Copy(body, 0, result, 2, body.Length);
                return result;
            }
            default:
                return Encoding.UTF8.GetBytes(text);
        }
    }

    private static bool StartsWith(byte[] bytes, byte[] prefix)
    {
        if (bytes.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (bytes[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Encoding no soportado (p. ej. UTF-32): el archivo se rechaza sin modificar.</summary>
public sealed class UnsupportedEncodingException : Exception
{
    public UnsupportedEncodingException(string message) : base(message)
    {
    }
}
