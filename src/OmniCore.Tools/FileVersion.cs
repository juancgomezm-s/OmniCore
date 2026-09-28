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
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };
    private static readonly byte[] Utf16LeBom = { 0xFF, 0xFE };
    private static readonly byte[] Utf16BeBom = { 0xFE, 0xFF };
    private static readonly byte[] Utf32LeBom = { 0x00, 0x00, 0xFE, 0xFF };
    private static readonly byte[] Utf32BeBom = { 0xFF, 0xFE, 0x00, 0x00 };

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
    /// Detecta el encoding por BOM y decodifica. Lanza <see cref="UnsupportedEncodingException"/>
    /// si el BOM no es soportado (p. ej. UTF-32), sin tocar el archivo.
    /// </summary>
    public static DecodedFile Decode(byte[] contentBytes)
    {
        if (StartsWith(contentBytes, Utf8Bom))
        {
            return new DecodedFile(Encoding.UTF8.GetString(contentBytes, 3, contentBytes.Length - 3), FileEncoding.Utf8Bom);
        }

        if (StartsWith(contentBytes, Utf16LeBom))
        {
            return new DecodedFile(Encoding.Unicode.GetString(contentBytes, 2, contentBytes.Length - 2),
                FileEncoding.Utf16LeBom);
        }

        if (StartsWith(contentBytes, Utf16BeBom))
        {
            return new DecodedFile(Encoding.BigEndianUnicode.GetString(contentBytes, 2, contentBytes.Length - 2),
                FileEncoding.Utf16BeBom);
        }

        if (StartsWith(contentBytes, Utf32LeBom) || StartsWith(contentBytes, Utf32BeBom))
        {
            throw new UnsupportedEncodingException("encoding no soportado: UTF-32");
        }

        return new DecodedFile(Encoding.UTF8.GetString(contentBytes), FileEncoding.Utf8NoBom);
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
