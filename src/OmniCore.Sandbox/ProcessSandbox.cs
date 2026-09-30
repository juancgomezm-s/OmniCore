namespace OmniCore.Sandbox;

/// <summary>The isolation level requested for a process.</summary>
public enum SandboxStrength
{
    Strong = 0,
    Weak = 1,
    None = 2
}

/// <summary>Access requested for a path when a platform sandbox is available.</summary>
[Flags]
public enum SandboxPathAccess
{
    None = 0,
    Read = 1,
    Write = 2
}

/// <summary>One path and its requested access.</summary>
public sealed record SandboxAllowedPath(string Path, SandboxPathAccess Access);

/// <summary>Network access requested for a process.</summary>
public enum SandboxNetworkMode
{
    Deny = 0,
    AllowList = 1,
    AllowAll = 2
}

/// <summary>
/// Describes requested network access. Hosts are meaningful when <see cref="Mode"/> is
/// <see cref="SandboxNetworkMode.AllowList"/>.
/// </summary>
public sealed record SandboxNetworkPolicy(
    SandboxNetworkMode Mode,
    IReadOnlyList<string> AllowedHosts);

/// <summary>
/// Describes a process invocation and the filesystem/network access it requests.
/// Access declarations are intent only; enforcement is the responsibility of a real sandbox.
/// </summary>
public sealed record SandboxLaunchSpec(
    SandboxStrength RequestedStrength,
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<SandboxAllowedPath> AllowedPaths,
    SandboxNetworkPolicy Network,
    TimeSpan? Timeout = null);

/// <summary>Reports which strengths the current Sandbox implementation can provide.</summary>
public sealed record SandboxCapabilities(
    bool StrongAvailable,
    bool WeakAvailable,
    bool NoneAvailable,
    string? StrongUnavailableReason)
{
    public bool IsAvailable(SandboxStrength strength) => strength switch
    {
        SandboxStrength.Strong => StrongAvailable,
        SandboxStrength.Weak => WeakAvailable,
        SandboxStrength.None => NoneAvailable,
        _ => false
    };
}

/// <summary>Probes the capabilities of the current Sandbox implementation.</summary>
public interface ISandboxCapabilitiesProbe
{
    SandboxCapabilities Probe();
}

/// <summary>
/// Capability report for the initial no-isolation implementation. It cannot provide Strong;
/// Weak and None both run without OS-enforced filesystem or network isolation.
/// </summary>
public sealed class NoIsolationSandboxCapabilitiesProbe : ISandboxCapabilitiesProbe
{
    public SandboxCapabilities Probe() => new(
        StrongAvailable: false,
        WeakAvailable: true,
        NoneAvailable: true,
        StrongUnavailableReason: "Strong process isolation is not implemented.");
}

/// <summary>Identifies a process output stream.</summary>
public enum SandboxOutputStream
{
    StandardOutput = 0,
    StandardError = 1
}

/// <summary>A chunk of process output, in the order supplied by the process control.</summary>
public sealed record SandboxOutputChunk(SandboxOutputStream Stream, string Text);

/// <summary>Captured process output and its exit code.</summary>
public sealed record SandboxProcessOutput(
    int ExitCode,
    IReadOnlyList<SandboxOutputChunk> Chunks,
    bool TimedOut = false);

/// <summary>Outcome of a sandbox runner operation.</summary>
public enum SandboxResultStatus
{
    Succeeded = 0,
    Unsupported = 1,
    Failed = 2,
    Cancelled = 3
}

/// <summary>Typed failure details returned by a sandbox operation.</summary>
public sealed record SandboxFailure(SandboxFailureCode Code, string Message);

public enum SandboxFailureCode
{
    UnsupportedStrength = 0,
    InvalidRequest = 1,
    LaunchFailed = 2,
    ExecutionFailed = 3
}

/// <summary>A result that carries a value only when the operation succeeds.</summary>
public sealed record SandboxResult<T>(
    SandboxResultStatus Status,
    T? Value,
    SandboxFailure? Failure)
{
    public bool IsSuccess => Status == SandboxResultStatus.Succeeded;

    public static SandboxResult<T> Success(T value) =>
        new(SandboxResultStatus.Succeeded, value, null);

    public static SandboxResult<T> Unsupported(SandboxFailure failure) =>
        new(SandboxResultStatus.Unsupported, default, failure);

    public static SandboxResult<T> Failed(SandboxFailure failure) =>
        new(SandboxResultStatus.Failed, default, failure);

    public static SandboxResult<T> Cancelled() =>
        new(SandboxResultStatus.Cancelled, default, null);
}

/// <summary>
/// Abstract process launcher. Implementations own the platform-specific mechanics of starting
/// and controlling a process; this interface does not imply any sandbox enforcement.
/// </summary>
public interface ISandboxProcessLauncher
{
    ValueTask<ISandboxProcessControl> StartAsync(
        SandboxLaunchSpec launch,
        CancellationToken cancellationToken);
}

/// <summary>Control surface for a launched process, including its captured output.</summary>
public interface ISandboxProcessControl : IAsyncDisposable
{
    /// <summary>Waits for process exit and returns captured output.</summary>
    ValueTask<SandboxProcessOutput> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Requests termination of the process and its descendants.</summary>
    ValueTask TerminateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Runs Weak and None requests by delegating to a process launcher without adding isolation.
/// Strong requests are explicitly unsupported and are never silently downgraded.
/// </summary>
public sealed class NoIsolationSandboxRunner
{
    private readonly ISandboxProcessLauncher _processLauncher;
    private readonly ISandboxCapabilitiesProbe _capabilitiesProbe;

    public NoIsolationSandboxRunner(
        ISandboxProcessLauncher processLauncher,
        ISandboxCapabilitiesProbe? capabilitiesProbe = null)
    {
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _capabilitiesProbe = capabilitiesProbe ?? new NoIsolationSandboxCapabilitiesProbe();
    }

    public async ValueTask<SandboxResult<SandboxProcessOutput>> RunAsync(
        SandboxLaunchSpec launch,
        CancellationToken cancellationToken = default)
    {
        if (launch is null)
        {
            return SandboxResult<SandboxProcessOutput>.Failed(new SandboxFailure(
                SandboxFailureCode.InvalidRequest,
                "A process launch specification is required."));
        }

        if (!IsValid(launch))
        {
            return SandboxResult<SandboxProcessOutput>.Failed(new SandboxFailure(
                SandboxFailureCode.InvalidRequest,
                "The process launch specification contains an invalid value."));
        }

        var capabilities = _capabilitiesProbe.Probe();
        // This runner never enforces isolation, so a custom probe cannot make Strong safe here.
        if (launch.RequestedStrength == SandboxStrength.Strong
            || !capabilities.IsAvailable(launch.RequestedStrength))
        {
            return SandboxResult<SandboxProcessOutput>.Unsupported(new SandboxFailure(
                SandboxFailureCode.UnsupportedStrength,
                launch.RequestedStrength == SandboxStrength.Strong
                    ? capabilities.StrongUnavailableReason ?? "Strong process isolation is unavailable."
                    : "The requested sandbox strength is unavailable."));
        }

        ISandboxProcessControl? process = null;
        try
        {
            process = await _processLauncher.StartAsync(launch, cancellationToken).ConfigureAwait(false);
            var output = await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return SandboxResult<SandboxProcessOutput>.Success(output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (process is not null)
            {
                try
                {
                    await process.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Preserve the cancellation outcome; the launcher owns termination recovery.
                }
            }

            return SandboxResult<SandboxProcessOutput>.Cancelled();
        }
        catch (Exception)
        {
            var code = process is null
                ? SandboxFailureCode.LaunchFailed
                : SandboxFailureCode.ExecutionFailed;
            var message = process is null ? "Process launch failed." : "Process execution failed.";
            return SandboxResult<SandboxProcessOutput>.Failed(new SandboxFailure(code, message));
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    await process.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Cleanup failures must not escape the typed result contract.
                }
            }
        }
    }

    private static bool IsValid(SandboxLaunchSpec launch)
    {
        if (string.IsNullOrWhiteSpace(launch.Executable)
            || launch.Arguments is null
            || launch.Environment is null
            || launch.AllowedPaths is null
            || launch.Network is null
            || launch.Timeout is { } timeout && timeout <= TimeSpan.Zero
            || launch.Network.AllowedHosts is null
            || !Enum.IsDefined(launch.RequestedStrength)
            || !Enum.IsDefined(launch.Network.Mode))
        {
            return false;
        }

        foreach (var argument in launch.Arguments)
        {
            if (argument is null)
            {
                return false;
            }
        }

        foreach (var path in launch.AllowedPaths)
        {
            if (path is null
                || string.IsNullOrWhiteSpace(path.Path)
                || path.Access == SandboxPathAccess.None
                || (path.Access & ~(SandboxPathAccess.Read | SandboxPathAccess.Write)) != 0)
            {
                return false;
            }
        }

        foreach (var variable in launch.Environment)
        {
            if (string.IsNullOrWhiteSpace(variable.Key)
                || variable.Key.Contains('=')
                || variable.Value is null)
            {
                return false;
            }
        }

        foreach (var host in launch.Network.AllowedHosts)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }
        }

        return launch.Network.Mode != SandboxNetworkMode.AllowList
            || launch.Network.AllowedHosts.Count > 0;
    }
}
