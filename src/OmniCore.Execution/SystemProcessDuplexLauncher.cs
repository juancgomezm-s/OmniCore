namespace OmniCore.Execution;

using OmniCore.Sandbox;

/// <summary>
/// Rejects persistent redirected launches on platforms without a retained OS-level ownership
/// primitive. A late tree enumeration is not sufficient for a persistent duplex session.
/// </summary>
internal sealed class SystemProcessDuplexLauncher : ISandboxProcessLauncher
{
    public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new ProcessLaunchUnavailableBeforeStartException(
            "Persistent Weak duplex processes are unavailable because this launcher cannot retain OS-level process-tree ownership. Request Strong isolation or use a non-persistent process operation.");
    }
}
