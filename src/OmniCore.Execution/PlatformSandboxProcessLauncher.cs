namespace OmniCore.Execution;

using OmniCore.Abstractions;
using OmniCore.Sandbox;

/// <summary>
/// Selects the OS-enforced launcher for Strong requests and the regular process runtime for
/// Weak/None. Strong is never silently downgraded: when unavailable the caller must obtain the
/// session's WeakSandboxConsent and explicitly retry with Weak.
/// </summary>
public sealed class PlatformSandboxProcessLauncher : ISandboxProcessLauncher
{
    private readonly ISandboxProcessLauncher _basicLauncher;
    private readonly ISandboxProcessLauncher? _strongLauncher;
    private readonly ISandboxCapabilitiesProbe _capabilities;

    public PlatformSandboxProcessLauncher(
        IProcessRuntime runtime,
        ISandboxCapabilitiesProbe? capabilities = null,
        ISandboxProcessLauncher? strongLauncher = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _basicLauncher = new RuntimeSandboxProcessLauncher(runtime);
        _capabilities = capabilities ?? new WindowsAppContainerCapabilitiesProbe();
        _strongLauncher = strongLauncher ?? (OperatingSystem.IsWindows()
            ? new WindowsAppContainerProcessLauncher()
            : null);
    }

    public ValueTask<ISandboxProcessControl> StartAsync(
        SandboxLaunchSpec launch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        cancellationToken.ThrowIfCancellationRequested();
        if (launch.RequestedStrength != SandboxStrength.Strong)
            return _basicLauncher.StartAsync(launch, cancellationToken);

        var capabilities = _capabilities.Probe();
        if (!capabilities.StrongAvailable || _strongLauncher is null)
        {
            var reason = capabilities.StrongUnavailableReason ?? "Strong process isolation is unavailable on this platform.";
            throw new NotSupportedException(reason);
        }

        return _strongLauncher.StartAsync(launch, cancellationToken);
    }
}
