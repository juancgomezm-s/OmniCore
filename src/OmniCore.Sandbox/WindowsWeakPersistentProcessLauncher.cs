namespace OmniCore.Sandbox;

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

internal interface IWindowsWeakProcessHooks
{
    IReadOnlyList<uint> QueryJobProcessIds(SafeFileHandle job);
    bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    uint WaitForProcess(SafeProcessHandle process, uint milliseconds);
    ISandboxPersistentProcessControl CreateControl(SafeProcessHandle process, SafeFileHandle job,
        SafeFileHandle input, SafeFileHandle stdout, SafeFileHandle? stderr,
        SafePseudoConsoleHandle? pseudoConsole, bool terminal);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsWeakProcessHooks : IWindowsWeakProcessHooks
{
    internal static readonly WindowsWeakProcessHooks Instance = new();
    private WindowsWeakProcessHooks() { }
    public IReadOnlyList<uint> QueryJobProcessIds(SafeFileHandle job) => AppContainerNative.QueryJobProcessIds(job);
    public bool TerminateJobObject(SafeFileHandle job, uint exitCode) => AppContainerNative.TerminateJobObject(job, exitCode);
    public uint WaitForProcess(SafeProcessHandle process, uint milliseconds) => AppContainerNative.WaitForSingleObject(process, milliseconds);
    public ISandboxPersistentProcessControl CreateControl(SafeProcessHandle process, SafeFileHandle job,
        SafeFileHandle input, SafeFileHandle stdout, SafeFileHandle? stderr,
        SafePseudoConsoleHandle? pseudoConsole, bool terminal) => new WindowsWeakPersistentProcessControl(
            process, job, input, stdout, stderr, pseudoConsole, terminal, this);
}

internal static class WindowsWeakProcessCleanup
{
    internal static async Task TerminateAndWaitForJobMembersAsync(SafeFileHandle job, IWindowsWeakProcessHooks hooks)
    {
        if (hooks.QueryJobProcessIds(job).Count != 0 && !hooks.TerminateJobObject(job, 1)
            && Marshal.GetLastPInvokeError() != AppContainerNative.ErrorProcessNotFound)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to terminate remaining Weak process descendants.");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (hooks.QueryJobProcessIds(job).Count != 0)
        {
            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(10))
                throw new TimeoutException("The Windows Job Object did not become empty after process termination.");
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    internal static void DisposeCapture(IDisposable resource, ref List<Exception>? failures)
    {
        try { resource.Dispose(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { (failures ??= []).Add(exception); }
    }

    internal static void WaitForExitCapture(SafeProcessHandle process, uint milliseconds, IWindowsWeakProcessHooks hooks,
        ref List<Exception>? failures, string timeoutMessage)
    {
        try
        {
            if (hooks.WaitForProcess(process, milliseconds) != AppContainerNative.WaitObject0)
                (failures ??= []).Add(new TimeoutException(timeoutMessage));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { (failures ??= []).Add(exception); }
    }

    internal static Exception? Combine(List<Exception>? failures) => failures switch
    {
        null => null,
        { Count: 1 } => failures[0],
        _ => new AggregateException(failures)
    };
}

/// <summary>Persistent Weak process transport with Windows Job Object tree ownership.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWeakPersistentProcessLauncher : ISandboxProcessLauncher
{
    private readonly IWindowsWeakProcessHooks _hooks;

    public WindowsWeakPersistentProcessLauncher() : this(WindowsWeakProcessHooks.Instance) { }

    internal WindowsWeakPersistentProcessLauncher(IWindowsWeakProcessHooks hooks) =>
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));

    public async ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new ProcessLaunchUnavailableBeforeStartException("Weak persistent process ownership is unavailable on this platform.");
        if (launch.RequestedStrength != SandboxStrength.Weak || !launch.Duplex || launch.InitialStandardInput is not null)
            throw new NotSupportedException("The Windows Weak persistent launcher requires a Weak duplex process.");
        if (string.IsNullOrWhiteSpace(launch.Executable) || launch.Arguments is null || launch.Environment is null
            || launch.Arguments.Any(argument => argument is null)
            || launch.Environment.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Contains('=') || pair.Value is null))
            throw new ArgumentException("The process launch specification is invalid.", nameof(launch));
        if (launch.Terminal && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new ProcessLaunchUnavailableBeforeStartException("Windows ConPTY requires Windows 10 version 1809 or newer.");
        var executable = Path.GetFullPath(launch.Executable);
        var workingDirectory = Path.GetFullPath(launch.WorkingDirectory ?? Path.GetDirectoryName(executable)!);
        if (!File.Exists(executable) || !Directory.Exists(workingDirectory))
            throw new ProcessLaunchUnavailableBeforeStartException("The executable or working directory does not exist.");

        SafeFileHandle? input, output, error, job;
        SafeProcessHandle? process;
        SafePseudoConsoleHandle? pseudoConsole;
        var processResumed = false;
        try
        {
            AppContainerNative.CreateSuspendedWeakProcess(executable,
                WindowsAppContainerProcessLauncher.BuildCommandLine(executable, launch.Arguments, launch.WindowsShellCommandLine),
                workingDirectory, launch.Environment, launch.Terminal,
                out input, out output, out error, out job, out process, out pseudoConsole, out processResumed);
        }
        catch (AggregateException exception)
        {
            if (processResumed)
                throw new ProcessLaunchOutcomeUnknownException(
                    "Persistent Weak process may have run before native cleanup failed.", exception);
            // Cleanup failure before ResumeThread is still ambiguous because a suspended OS process may remain.
            throw;
        }
        catch (OperationCanceledException exception)
        {
            if (processResumed)
                throw new ProcessLaunchOutcomeUnknownException("Persistent Weak process may have run before launch cancellation.", exception);
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            if (processResumed)
                throw new ProcessLaunchOutcomeUnknownException(
                    "Persistent Weak process may have run before native launch setup failed.", exception);
            throw new ProcessLaunchUnavailableBeforeStartException(
                "Persistent Weak process launch failed before the suspended process was resumed.", exception);
        }
        try
        {
            return _hooks.CreateControl(process!, job!, input!, output!, error, pseudoConsole, launch.Terminal);
        }
        catch (Exception controlFailure) when (controlFailure is not OutOfMemoryException and not StackOverflowException)
        {
            var cleanupFailure = await CleanupAfterControlCreationFailureAsync(
                process!, job!, input!, output!, error, pseudoConsole, _hooks).ConfigureAwait(false);
            var cause = cleanupFailure is null ? controlFailure : new AggregateException(controlFailure, cleanupFailure);
            throw new ProcessLaunchOutcomeUnknownException(
                "Persistent Weak process was resumed but its control could not be created; the execution outcome is unknown.", cause);
        }
    }

    private static async Task<Exception?> CleanupAfterControlCreationFailureAsync(SafeProcessHandle process,
        SafeFileHandle job, SafeFileHandle input, SafeFileHandle stdout, SafeFileHandle? stderr,
        SafePseudoConsoleHandle? pseudoConsole, IWindowsWeakProcessHooks hooks)
    {
        List<Exception>? failures = null;
        try
        {
            await WindowsWeakProcessCleanup.TerminateAndWaitForJobMembersAsync(job, hooks).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            (failures ??= []).Add(exception);
        }
        // Closing the kill-on-close Job is the fallback fence; do it before HPCON disposal.
        WindowsWeakProcessCleanup.DisposeCapture(job, ref failures);
        WindowsWeakProcessCleanup.WaitForExitCapture(process, 5000, hooks, ref failures,
            "The resumed Weak root did not exit after its Job was closed.");
        if (pseudoConsole is not null) WindowsWeakProcessCleanup.DisposeCapture(pseudoConsole, ref failures);
        WindowsWeakProcessCleanup.DisposeCapture(input, ref failures);
        WindowsWeakProcessCleanup.DisposeCapture(stdout, ref failures);
        if (stderr is not null) WindowsWeakProcessCleanup.DisposeCapture(stderr, ref failures);
        WindowsWeakProcessCleanup.DisposeCapture(process, ref failures);
        return WindowsWeakProcessCleanup.Combine(failures);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsWeakPersistentProcessControl : ISandboxPersistentProcessControl
{
    private readonly IWindowsWeakProcessHooks _hooks;
    private readonly SafeProcessHandle _process;
    private readonly SafeFileHandle _job;
    private readonly SafeFileHandle _stdout;
    private readonly SafeFileHandle? _stderr;
    private readonly SafePseudoConsoleHandle? _pseudoConsole;
    private bool _finished;
    private bool _disposed;

    public Stream StandardInput { get; }
    public Stream StandardOutput { get; }
    public Stream StandardError { get; }
    public bool IsTerminal { get; }

    internal WindowsWeakPersistentProcessControl(SafeProcessHandle process, SafeFileHandle job,
        SafeFileHandle input, SafeFileHandle stdout, SafeFileHandle? stderr,
        SafePseudoConsoleHandle? pseudoConsole, bool terminal, IWindowsWeakProcessHooks hooks)
    {
        _hooks = hooks;
        _process = process;
        _job = job;
        _stdout = stdout;
        _stderr = stderr;
        _pseudoConsole = pseudoConsole;
        IsTerminal = terminal;
        StandardInput = new FileStream(input, FileAccess.Write, 4096, isAsync: false);
        StandardOutput = new FileStream(stdout, FileAccess.Read, 4096, isAsync: false);
        StandardError = stderr is null ? Stream.Null : new FileStream(stderr, FileAccess.Read, 4096, isAsync: false);
    }

    public ValueTask CloseInputAsync()
    {
        StandardInput.Dispose();
        return ValueTask.CompletedTask;
    }

    public void ResizeTerminal(short columns, short rows)
    {
        if (!IsTerminal || columns <= 0 || rows <= 0
            || !AppContainerNative.ResizePseudoConsole(_pseudoConsole!, columns, rows))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to resize the Weak process pseudo-console.");
    }

    public async ValueTask<int> WaitForExitCodeAsync(CancellationToken cancellationToken)
    {
        await Task.Run(() => WaitForRoot(cancellationToken), CancellationToken.None).ConfigureAwait(false);
        await WindowsWeakProcessCleanup.TerminateAndWaitForJobMembersAsync(_job, _hooks).ConfigureAwait(false);
        ClosePseudoConsole();
        if (!AppContainerNative.GetExitCodeProcess(_process, out var exitCode))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to read the Weak process exit code.");
        _finished = true;
        return unchecked((int)exitCode);
    }

    public async ValueTask<SandboxProcessOutput> WaitForExitAsync(CancellationToken cancellationToken)
    {
        var exitCode = await WaitForExitCodeAsync(cancellationToken).ConfigureAwait(false);
        return new SandboxProcessOutput(exitCode, []);
    }

    public async ValueTask TerminateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_finished)
        {
            await WindowsWeakProcessCleanup.TerminateAndWaitForJobMembersAsync(_job, _hooks).ConfigureAwait(false);
            ClosePseudoConsole();
            _finished = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        List<Exception>? failures = null;
        try
        {
            if (!_finished)
                await WindowsWeakProcessCleanup.TerminateAndWaitForJobMembersAsync(_job, _hooks).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { (failures ??= []).Add(exception); }
        finally
        {
            // KillOnJobClose is the fallback fence after any query, terminate or timeout error.
            // Close the Job before HPCON: pseudo-console teardown can wait on attached processes.
            WindowsWeakProcessCleanup.DisposeCapture(_job, ref failures);
            WindowsWeakProcessCleanup.WaitForExitCapture(_process, 5000, _hooks, ref failures,
                "The Weak root did not exit after its Job was closed.");
            ClosePseudoConsoleCapture(ref failures);
            WindowsWeakProcessCleanup.DisposeCapture(StandardInput, ref failures);
            WindowsWeakProcessCleanup.DisposeCapture(StandardOutput, ref failures);
            WindowsWeakProcessCleanup.DisposeCapture(StandardError, ref failures);
            WindowsWeakProcessCleanup.DisposeCapture(_stdout, ref failures);
            if (_stderr is not null) WindowsWeakProcessCleanup.DisposeCapture(_stderr, ref failures);
            WindowsWeakProcessCleanup.DisposeCapture(_process, ref failures);
            _disposed = true;
        }
        if (WindowsWeakProcessCleanup.Combine(failures) is { } cleanupFailure)
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    private void WaitForRoot(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = AppContainerNative.WaitForSingleObject(_process, 50);
            if (result == AppContainerNative.WaitObject0) return;
            if (result != AppContainerNative.WaitTimeout)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Waiting for the Weak process failed.");
        }
    }

    private void ClosePseudoConsole()
    {
        if (_pseudoConsole is { IsClosed: false, IsInvalid: false }) _pseudoConsole.Dispose();
    }

    private void ClosePseudoConsoleCapture(ref List<Exception>? failures)
    {
        if (_pseudoConsole is { IsClosed: false, IsInvalid: false })
            WindowsWeakProcessCleanup.DisposeCapture(_pseudoConsole, ref failures);
    }
}
