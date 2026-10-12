using System.Globalization;

namespace OmniCore.Client;

/// <summary>Human form values only; configuration and protocol serialization stay invariant.</summary>
public static class PreferenceInput
{
    public static CultureInfo Culture(string locale) => CultureInfo.GetCultureInfo(locale switch
    { "en" => "en-US", "fr" => "fr-FR", "it" => "it-IT", _ => "es-MX" });

    public static bool TryMoney(string text, string locale, out decimal? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        // No grouping: a comma in French must never silently multiply a limit by ten.
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint
            | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, Culture(locale), out var parsed)
            || parsed < 0) return false;
        value = parsed; return true;
    }

    public static string CanonicalValue(string category, string text, string locale, params string[] allowed) =>
        allowed.FirstOrDefault(value => string.Equals(value, text.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(UiCompletionResources.EnumLabel(category, value, locale), text.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? text.Trim();
}
