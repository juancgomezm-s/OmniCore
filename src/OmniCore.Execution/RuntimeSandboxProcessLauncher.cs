namespace OmniCore.Execution;

using OmniCore.Abstractions;
using OmniCore.Sandbox;

/// <summary>Adapta el contrato compartido del sandbox al control de árbol de IProcessRuntime.</summary>
public sealed class RuntimeSandboxProcessLauncher : ISandboxProcessLauncher
{
    private readonly IProcessRuntime _runtime;

    public RuntimeSandboxProcessLauncher(IProcessRuntime runtime) =>
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (launch.RequestedStrength != SandboxStrength.Weak)
            throw new NotSupportedException("The runtime process launcher only provides Weak process execution.");
        var process = _runtime.Launch(new ProcessLaunch(launch.Executable, launch.Arguments,
            launch.WorkingDirectory ?? Environment.CurrentDirectory, launch.Environment, captureOutput: true),
            cancellationToken);
        return ValueTask.FromResult<ISandboxProcessControl>(new Control(_runtime, process, launch.Timeout));
    }

    private sealed class Control : ISandboxProcessControl
    {
        private readonly IProcessRuntime _runtime;
        private readonly ProcessHandle _handle;
        private readonly TimeSpan? _timeout;
        private bool _finished;

        public Control(IProcessRuntime runtime, ProcessHandle handle, TimeSpan? timeout)
        {
            _runtime = runtime;
            _handle = handle;
            _timeout = timeout;
        }

        public async ValueTask<SandboxProcessOutput> WaitForExitAsync(CancellationToken cancellationToken)
        {
            var result = await Task.Run(() => _runtime.Wait(_handle, _timeout ?? TimeSpan.FromDays(1), cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
            _finished = true;
            var chunks = new List<SandboxOutputChunk>();
            if (!string.IsNullOrEmpty(result.Stdout))
                chunks.Add(new SandboxOutputChunk(SandboxOutputStream.StandardOutput, result.Stdout));
            if (!string.IsNullOrEmpty(result.Stderr))
                chunks.Add(new SandboxOutputChunk(SandboxOutputStream.StandardError, result.Stderr));
            return new SandboxProcessOutput(result.ExitCode, chunks, result.TimedOut);
        }

        public ValueTask TerminateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_finished) _runtime.CancelTree(_handle);
            _finished = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_finished) _runtime.CancelTree(_handle);
            _finished = true;
            return ValueTask.CompletedTask;
        }
    }
}
