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
    public WebLinkHost() : this(new SystemBrowserLauncher(allowHttp: true)) { }
    public WebLinkHost(IBrowserLauncher browser) =>
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));

    public async Task<bool> OpenAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(target) || target.Length > 8192 || target.Any(char.IsControl)
            || !Uri.TryCreate(target, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.Host.Length == 0 || uri.UserInfo.Length != 0) return false;
        return (await _browser.LaunchAsync(uri, cancellationToken).ConfigureAwait(false)).Launched;
    }
}
