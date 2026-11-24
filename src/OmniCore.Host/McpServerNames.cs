namespace OmniCore.Host;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>Human names stay unchanged; tool namespaces use a stable bounded ASCII identifier.</summary>
public static class McpServerNames
{
    public static string CreateId(string name)
    {
        var value = Validate(name);
        var slug = new StringBuilder();
        var separator = false;
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (!char.IsAsciiLetterOrDigit(character)) { separator = slug.Length > 0; continue; }
            if (separator) { slug.Append('-'); separator = false; }
            slug.Append(char.ToLowerInvariant(character));
        }
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];
        return slug.Length == 0 ? "server-" + digest : slug.Length <= 64 ? slug.ToString() : slug.ToString(0, 51) + "-" + digest;
    }

    public static string Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128 || name.Any(char.IsControl))
            throw new ArgumentException("mcpPreferences.name_invalid");
        return name.Trim();
    }
}
