using OmniCore.Abstractions;

namespace OmniCore.Tests;

/// <summary>
/// IBrowserLauncher falso: registra la URL que le dieron y dice si pudo abrirla. Ningun test
/// lanza un navegador (CLAUDE.md: priorizar tests deterministas).
/// </summary>
internal sealed class FakeBrowserLauncher : IBrowserLauncher
{
    private readonly BrowserLaunchResult _result;

    public FakeBrowserLauncher(bool launch = true, BrowserLaunchFailure failure = BrowserLaunchFailure.NoLauncher)
    {
        _result = launch ? BrowserLaunchResult.Ok() : BrowserLaunchResult.Failed(failure);
    }

    public List<Uri> Launched { get; } = [];

    public Task<BrowserLaunchResult> LaunchAsync(Uri url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Launched.Add(url);
        return Task.FromResult(_result);
    }
}
