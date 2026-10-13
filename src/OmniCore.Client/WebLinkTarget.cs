namespace OmniCore.Client;

/// <summary>Browser links are data until the user explicitly activates them.</summary>
public static class WebLinkTarget
{
    public static bool TryParse(string? target, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(target) || target.Length > 8192 || target.Any(char.IsControl)
            || !Uri.TryCreate(target, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || parsed.Host.Length == 0 || parsed.UserInfo.Length != 0) return false;
        uri = parsed;
        return true;
    }
}
