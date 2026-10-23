namespace OmniCore.Execution;

using OmniCore.Sandbox;

/// <summary>
/// Rejects persistent redirected launches that have no OS-level process-tree ownership.
/// Persistent duplex sessions must use a Strong launcher with a job/group that can be fenced.
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
