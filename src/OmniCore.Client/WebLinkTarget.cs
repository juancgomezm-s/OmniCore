namespace OmniCore.Client;

/// <summary>Browser links are data until the user explicitly activates them.</summary>
public static class WebLinkTarget
{
    public static bool TryParse(string? target, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(target) || target.Length > 8192 || target.Any(char.IsControl)
            || !Uri.TryCreate(target, UriKind.Absolute, out var parsed) || parsed.UserInfo.Length != 0) return false;
        if (parsed.IsFile)
        {
            // Recognition only. The Host additionally checks workspace scope, existence and links.
            if (parsed.IsUnc || parsed.Host.Length != 0 || parsed.Query.Length != 0 || parsed.LocalPath.Any(char.IsControl)
                || !string.Equals(Path.GetExtension(parsed.LocalPath), ".html", StringComparison.OrdinalIgnoreCase)) return false;
        }
        else if ((parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || parsed.Host.Length == 0) return false;
        uri = parsed;
        return true;
    }
}
