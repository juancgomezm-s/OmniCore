using OmniCore.Abstractions;
using OmniCore.Infrastructure;

namespace OmniCore.Host;

/// <summary>Explicit client browser action; never runs a model tool or writes the journal.</summary>
public interface IWebLinkHost
{
    Task<bool> OpenAsync(string target, CancellationToken cancellationToken);
}

public sealed class WebLinkHost : IWebLinkHost
{
    private readonly IBrowserLauncher _browser;
    private readonly string _workspaceRoot;
    public WebLinkHost() : this(new SystemBrowserLauncher(allowHttp: true, allowLocalHtml: true)) { }
    public WebLinkHost(IBrowserLauncher browser, string? workspaceRoot = null)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _workspaceRoot = Path.GetFullPath(workspaceRoot ?? Environment.CurrentDirectory);
    }

    public async Task<bool> OpenAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(target) || target.Length > 8192 || target.Any(char.IsControl)
            || !Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0) return false;
        if (uri.IsFile)
        {
            if (uri.IsUnc || uri.Host.Length != 0 || uri.Query.Length != 0 || uri.LocalPath.Any(char.IsControl)
                || !string.Equals(Path.GetExtension(uri.LocalPath), ".html", StringComparison.OrdinalIgnoreCase)
                || !IsWorkspaceReport(uri.LocalPath)) return false;
        }
        else if ((uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.Host.Length == 0) return false;
        return (await _browser.LaunchAsync(uri, cancellationToken).ConfigureAwait(false)).Launched;
    }

    private bool IsWorkspaceReport(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(_workspaceRoot, full);
            if (Path.IsPathRooted(relative) || relative is "." or ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(full)) return false;
            for (var current = full; current is not null; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                if (string.Equals(current, _workspaceRoot, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return true;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return false;
    }
}
