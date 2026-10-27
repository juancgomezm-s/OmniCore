namespace OmniCore.Execution;

using OmniCore.Abstractions;
using OmniCore.Sandbox;

/// <summary>
/// Selects the OS-enforced launcher for Strong requests, a Windows Job Object for persistent
/// Weak processes, and the regular process runtime for other Weak/None calls. Strong is never
/// silently downgraded: when unavailable the caller must obtain current WeakSandboxConsent and retry.
/// </summary>
public sealed class PlatformSandboxProcessLauncher : ISandboxProcessLauncher, IStrongSandboxPreflight
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
        _capabilities = capabilities ?? (OperatingSystem.IsWindows()
            ? new WindowsAppContainerCapabilitiesProbe()
            : new LinuxBubblewrapCapabilitiesProbe());
        _strongLauncher = strongLauncher ?? (OperatingSystem.IsWindows()
            ? new WindowsAppContainerProcessLauncher()
            : OperatingSystem.IsLinux() ? new LinuxBubblewrapProcessLauncher() : null);
    }

    public async ValueTask<ISandboxProcessControl> StartAsync(
        SandboxLaunchSpec launch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        cancellationToken.ThrowIfCancellationRequested();
        if (launch.RequestedStrength != SandboxStrength.Strong)
            return launch.Duplex
                ? OperatingSystem.IsWindows()
                    ? await new WindowsWeakPersistentProcessLauncher().StartAsync(launch, cancellationToken).ConfigureAwait(false)
                    : await new SystemProcessDuplexLauncher().StartAsync(launch, cancellationToken).ConfigureAwait(false)
                : await _basicLauncher.StartAsync(launch, cancellationToken).ConfigureAwait(false);

        var preflight = CheckStrongAvailability(launch);
        if (preflight == StrongSandboxPreflightResult.UnavailableBeforeLaunch)
        {
            var capabilities = _capabilities.Probe();
            var reason = capabilities.StrongUnavailableReason ?? "Strong process isolation is unavailable on this platform.";
            throw new StrongSandboxUnavailableBeforeLaunchException(reason);
        }
        if (preflight == StrongSandboxPreflightResult.Unknown)
            throw new StrongSandboxLaunchOutcomeUnknownException(
                "Strong process isolation availability could not be established before launch.",
                new InvalidOperationException("sandbox_preflight_unknown"));
        var strongLauncher = _strongLauncher;
        if (strongLauncher is null)
            throw new StrongSandboxUnavailableBeforeLaunchException(
                "Strong process isolation is unavailable on this platform.");

        try
        {
            return await strongLauncher.StartAsync(launch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not StrongSandboxUnavailableBeforeLaunchException
            and not StrongSandboxLaunchOutcomeUnknownException)
        {
            // Once the platform launcher has been called, even a security error can happen after
            // process creation. Do not turn that ambiguous boundary into a Weak fallback.
            throw new StrongSandboxLaunchOutcomeUnknownException(
                "Strong process launch was attempted but its outcome could not be established.", exception);
        }
    }

    public StrongSandboxPreflightResult CheckStrongAvailability(SandboxLaunchSpec launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (launch.RequestedStrength != SandboxStrength.Strong) return StrongSandboxPreflightResult.Available;
        try
        {
            var capabilities = _capabilities.Probe();
            if (!capabilities.StrongAvailable || _strongLauncher is null)
                return StrongSandboxPreflightResult.UnavailableBeforeLaunch;
            return _strongLauncher is IStrongSandboxPreflight pathPreflight
                ? pathPreflight.CheckStrongAvailability(launch)
                : StrongSandboxPreflightResult.Available;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return StrongSandboxPreflightResult.Unknown;
        }
    }
}
