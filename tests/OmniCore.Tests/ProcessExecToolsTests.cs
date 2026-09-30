using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Execution;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Sandbox;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class ProcessExecToolsTests
{
    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "omnicore-process-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Cleanup(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private static string ProcessArgs(string executable, IReadOnlyList<string> argv, string cwd,
        int timeout = 5, string sandbox = "Weak") => JsonSerializer.Serialize(new
        {
            executable, argv, cwd, timeoutSeconds = timeout, sandboxLevel = sandbox,
        });

    private static ToolRuntime Runtime(ProcessExecTool tool, IExecutableResolver resolver,
        IPermissionPolicy? policy = null) => new(
            new FakeCatalog().Add(tool),
            policy ?? ScriptedPermissionPolicy.WithTool("process.exec", PermissionDecision.Allow),
            _ => VoidBox.Instance, null, resolver);

    private sealed class RecordingRuntime : IProcessRuntime
    {
        private readonly Action? _onLaunch;
        public RecordingRuntime(Action? onLaunch = null) => _onLaunch = onLaunch;
        public ProcessLaunch? LastLaunch { get; private set; }
        public string? Output { get; set; }
        public bool TimedOut { get; set; }
        public bool CancelledTree { get; private set; }
        public ProcessHandle Launch(ProcessLaunch launch, CancellationToken cancellationToken)
        {
            LastLaunch = launch;
            _onLaunch?.Invoke();
            return new ProcessHandle(42, this);
        }
        public void CancelTree(ProcessHandle handle) => CancelledTree = true;
        public ProcessResult Wait(ProcessHandle handle, TimeSpan timeout, CancellationToken cancellationToken) =>
            new(0, Output ?? "ok", null, TimedOut);
        public bool IsAlive(ProcessHandle handle) => false;
    }

    [Fact]
    public void Resolver_skips_fake_workspace_executable_and_returns_typed_not_found()
    {
        var workspace = TempDir();
        try
        {
            var fake = Path.Combine(workspace, OperatingSystem.IsWindows() ? "dotnet.cmd" : "dotnet");
            File.WriteAllText(fake, "fake executable");
            var systemPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            var resolver = new SystemExecutableResolver(new[] { workspace }.Concat(systemPath), workspace);
            var resolved = resolver.Resolve("dotnet", workspace);
            Assert.False(Path.GetFullPath(resolved.ResolvedPath).StartsWith(Path.GetFullPath(workspace),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            Assert.Throws<ExecutableNotFoundException>(() => resolver.Resolve("this-executable-does-not-exist-omnicore", workspace));
            Assert.Throws<ExecutableNotFoundException>(() => resolver.Resolve("tools/fake", workspace));
        }
        finally { Cleanup(workspace); }
    }

    [Fact]
    public void Resolver_never_returns_a_batch_script_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Solo Windows ejecuta .bat/.cmd a través de cmd.exe.");
        }

        var workspace = TempDir();
        var bin = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(bin, "sometool.cmd"), "@echo off");
            File.WriteAllText(Path.Combine(bin, "other.bat"), "@echo off");
            var resolver = new SystemExecutableResolver(new[] { bin }, workspace);

            // Por PATHEXT no se ofrece el .cmd; pedido explícitamente, falla tipado.
            Assert.Throws<ExecutableNotFoundException>(() => resolver.Resolve("sometool", workspace));
            Assert.Throws<ExecutableRequiresShellException>(() => resolver.Resolve("other.bat", workspace));
            Assert.Throws<ExecutableRequiresShellException>(() =>
                resolver.Resolve(Path.Combine(bin, "sometool.cmd"), workspace));
        }
        finally
        {
            Cleanup(workspace);
            Cleanup(bin);
        }
    }

    [Fact]
    public void Process_exec_passes_argv_verbatim_without_a_shell()
    {
        var workspace = TempDir();
        try
        {
            var runtime = new RecordingRuntime();
            var tool = new ProcessExecTool(runtime, new PathBoundaryValidator());
            var quoted = "an argument with spaces and \"quotes\"";
            var executable = OperatingSystem.IsWindows() ? "dotnet" : "/bin/echo";
            var argv = OperatingSystem.IsWindows() ? new[] { quoted, "--version" } : new[] { quoted };
            var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("process.exec"), "p1",
                ProcessArgs(executable, argv, "."));
            var result = Runtime(tool, new SystemExecutableResolver()).Run(call,
                new ToolPreparationContext(workspace, DateTimeOffset.UtcNow), new ToolExecutionContext(workspace),
                false, CancellationToken.None);
            Assert.True(result.Succeeded, result.Summary);
            Assert.Equal(argv, runtime.LastLaunch!.Args);
            Assert.Equal(new SystemExecutableResolver().Resolve(executable, workspace).ResolvedPath,
                runtime.LastLaunch.Executable);
        }
        finally { Cleanup(workspace); }
    }

    [Fact]
    public void Small_real_process_runs_on_the_ci_platform()
    {
        var workspace = TempDir();
        try
        {
            var runtime = new SystemProcessRuntime();
            var tool = new ProcessExecTool(runtime, new PathBoundaryValidator());
            var executable = OperatingSystem.IsWindows() ? "dotnet" : "/bin/echo";
            var argv = OperatingSystem.IsWindows() ? new[] { "--version" } : new[] { "process exec ok" };
            var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("process.exec"), "real-process",
                ProcessArgs(executable, argv, "."));
            var outcome = Runtime(tool, new SystemExecutableResolver()).Run(call,
                new ToolPreparationContext(workspace, DateTimeOffset.UtcNow), new ToolExecutionContext(workspace),
                false, TestContext.Current.CancellationToken);
            Assert.True(outcome.Succeeded, outcome.Summary);
            if (!OperatingSystem.IsWindows()) Assert.Contains("process exec ok", outcome.Preview);
            else Assert.NotEmpty(outcome.Preview ?? string.Empty);
        }
        finally { Cleanup(workspace); }
    }

    [Fact]
    public void Cwd_outside_workspace_is_rejected_before_authorization_or_launch()
    {
        var workspace = TempDir();
        var outside = Path.GetTempPath();
        try
        {
            var runtime = new RecordingRuntime();
            var tool = new ProcessExecTool(runtime, new PathBoundaryValidator());
            var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("process.exec"), "p1",
                ProcessArgs(OperatingSystem.IsWindows() ? "dotnet" : "/bin/echo", Array.Empty<string>(), outside));
            var outcome = Runtime(tool, new SystemExecutableResolver()).Run(call,
                new ToolPreparationContext(workspace, DateTimeOffset.UtcNow), new ToolExecutionContext(workspace),
                false, CancellationToken.None);
            Assert.False(outcome.Succeeded);
            Assert.Null(runtime.LastLaunch);
        }
        finally { Cleanup(workspace); }
    }

    private sealed class BarrierCheckingStore : IEventStore
    {
        private readonly List<DomainEvent> _events = new();
        public readonly List<(string Type, DurabilityClass Durability)> Writes = new();
        public void Append(SessionId sessionId, DomainEvent evt, DurabilityClass durability,
            CancellationToken cancellationToken) => AppendBatch(sessionId, new[] { evt }, durability, cancellationToken);
        public void AppendBatch(SessionId sessionId, IReadOnlyList<DomainEvent> events,
            DurabilityClass durability, CancellationToken cancellationToken)
        {
            foreach (var evt in events)
            {
                Writes.Add((evt.Type.ToString(), durability));
                _events.Add(DomainEvent.Stored(evt.EventId, sessionId, _events.Count + 1L, evt.Type,
                    evt.SchemaVersion, evt.Timestamp, evt.Causation, evt.CorrelationId, evt.RunId, evt.TaskId,
                    evt.LaneId, evt.TurnId, evt.PlanItemId, evt.ToolCallId, evt.ArtifactRefs, evt.PayloadJson));
            }
        }
        public long CurrentSequence(SessionId sessionId) => _events.Count;
        public IReadOnlyList<DomainEvent> ReadFrom(SessionId sessionId, long fromSequenceInclusive) =>
            _events.Skip((int)Math.Max(0, fromSequenceInclusive - 1)).ToArray();
    }

    [Fact]
    public void Tool_call_started_barrier_is_persisted_before_process_launch()
    {
        var workspace = TempDir();
        try
        {
            var store = new BarrierCheckingStore();
            var sawBarrierAtLaunch = false;
            var runtime = new RecordingRuntime(() =>
                sawBarrierAtLaunch = store.Writes.Any(write => write.Type == "toolcall.started"
                    && write.Durability == DurabilityClass.Barrier));
            var tool = new ProcessExecTool(runtime, new PathBoundaryValidator());
            var catalog = new FakeCatalog().Add(tool);
            var executor = new OmniCore.Host.ScriptedToolExecutor(catalog,
                ScriptedPermissionPolicy.WithTool("process.exec", PermissionDecision.Allow), workspace, null);
            var stream = new EventStream(store, EventCodecs.Create(), SessionId.New());
            var executable = OperatingSystem.IsWindows() ? "dotnet" : "/bin/echo";
            var argv = OperatingSystem.IsWindows() ? new[] { "--version" } : new[] { "barrier" };
            var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("process.exec"), "p1",
                ProcessArgs(executable, argv, "."));
            var outcome = executor.ExecuteTool(call, false, CancellationToken.None, stream);
            Assert.True(outcome.Succeeded, outcome.Summary);
            Assert.True(sawBarrierAtLaunch);
        }
        finally { Cleanup(workspace); }
    }

    [Fact]
    public void Output_is_capped_and_known_secrets_are_redacted()
    {
        var workspace = TempDir();
        try
        {
            SecretRedactorRegistry.Install(new SecretRedactor());
            SecretRedactorRegistry.Register("known-secret-value-123");
            var runtime = new RecordingRuntime { Output = "known-secret-value-123" + new string('x', 20_000) };
            var tool = new ProcessExecTool(runtime, new PathBoundaryValidator());
            var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("process.exec"), "p1",
                ProcessArgs(OperatingSystem.IsWindows() ? "dotnet" : "/bin/echo", Array.Empty<string>(), "."));
            var outcome = Runtime(tool, new SystemExecutableResolver()).Run(call,
                new ToolPreparationContext(workspace, DateTimeOffset.UtcNow), new ToolExecutionContext(workspace),
                false, CancellationToken.None);
            Assert.True(outcome.Succeeded);
            Assert.DoesNotContain("known-secret-value-123", outcome.Preview);
            Assert.Contains("output capped", outcome.Preview);
            Assert.True(outcome.Preview!.Length < 16_500);
        }
        finally { Cleanup(workspace); }
    }

    [Fact]
    public void Timeout_kills_the_entire_process_tree()
    {
        var workspace = TempDir();
        try
        {
            var runtime = new SystemProcessRuntime();
            var tool = new ProcessExecTool(runtime, new PathBoundaryValidator());
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe")
                : "/bin/sleep";
            var argv = OperatingSystem.IsWindows() ? new[] { "127.0.0.1", "-n", "10" } : new[] { "10" };
            var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("process.exec"), "p1",
                ProcessArgs(executable, argv, ".", timeout: 1));
            var start = DateTime.UtcNow;
            var outcome = Runtime(tool, new SystemExecutableResolver()).Run(call,
                new ToolPreparationContext(workspace, DateTimeOffset.UtcNow), new ToolExecutionContext(workspace),
                false, CancellationToken.None);
            Assert.False(outcome.Succeeded);
            Assert.Contains("timeout", outcome.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(8));
        }
        finally { Cleanup(workspace); }
    }

    [Fact]
    public void Shell_permissions_are_deny_in_plan_and_ask_in_act_and_keep_raw_claim()
    {
        var workspace = TempDir();
        try
        {
            var tool = new ShellExecTool(new RecordingRuntime(), new PathBoundaryValidator());
            const string raw = "echo 'one; two' && touch x";
            var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("shell.exec"), "p1",
                JsonSerializer.Serialize(new { command = raw, cwd = ".", timeoutSeconds = 5 }));
            var intent = Assert.IsType<Prepared>(tool.Prepare(call, new ToolPreparationContext(workspace, DateTimeOffset.UtcNow))).Intent;
            Assert.Contains(raw, intent.Claims.Process!.Args);
            Assert.Equal(PermissionDecision.Deny,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()).WithModeDefaults(RunMode.Plan).Evaluate(intent).Final);
            Assert.Equal(PermissionDecision.Ask,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()).WithModeDefaults(RunMode.Act).Evaluate(intent).Final);
        }
        finally { Cleanup(workspace); }
    }

    [Fact]
    public void Strong_fallback_refuses_without_client_and_requests_audited_consent_with_interaction()
    {
        var workspace = TempDir();
        try
        {
            var tool = new ProcessExecTool(new RecordingRuntime(), new PathBoundaryValidator());
            var args = ProcessArgs(OperatingSystem.IsWindows() ? "dotnet" : "/bin/echo", Array.Empty<string>(), ".", sandbox: "Strong");
            var call = new ValidatedToolCall(ToolCallId.New(), new ToolId("process.exec"), "p1", args);

            var noClientEvents = new List<DomainEventPayload>();
            var noClient = Runtime(tool, new SystemExecutableResolver()).Run(call,
                new ToolPreparationContext(workspace, DateTimeOffset.UtcNow),
                new ToolExecutionContext(workspace, null, e => noClientEvents.Add(e), isInteractive: false),
                false, CancellationToken.None);
            Assert.False(noClient.Succeeded);
            Assert.Equal(WeakSandboxConsentRequiredException.Code, noClient.Summary);
            Assert.Contains(noClientEvents, e => e is InteractionRequested { Kind: InteractionKind.WeakSandboxConsent });
            Assert.Contains(noClientEvents, e => e is InteractionResolved { Cause: InteractionCause.NoClient });

            var events = new List<DomainEventPayload>();
            var audit = new InMemoryAuditSink();
            var context = new ToolExecutionContext(workspace, null, e => events.Add(e),
                _ => "consent_once", audit, isInteractive: true, new WeakSandboxConsentState());
            var granted = Runtime(tool, new SystemExecutableResolver()).Run(call,
                new ToolPreparationContext(workspace, DateTimeOffset.UtcNow), context, false, CancellationToken.None);
            Assert.True(granted.Succeeded, granted.Summary);
            Assert.Contains(events, e => e is InteractionResolved { OptionId: "consent_once" });
            Assert.Contains(audit.Records(), r => r.EventName == "WeakSandboxConsentGranted");
        }
        finally { Cleanup(workspace); }
    }

    private sealed class FakeSandboxLauncher : ISandboxProcessLauncher
    {
        public int Starts { get; private set; }
        public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Starts++;
            return ValueTask.FromResult<ISandboxProcessControl>(new FakeSandboxProcess());
        }
    }

    private sealed class FakeSandboxProcess : ISandboxProcessControl
    {
        public ValueTask<SandboxProcessOutput> WaitForExitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SandboxProcessOutput(0, Array.Empty<SandboxOutputChunk>()));
        }
        public ValueTask TerminateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async System.Threading.Tasks.Task No_isolation_runner_executes_weak_and_none_but_never_downgrades_strong()
    {
        var launcher = new FakeSandboxLauncher();
        var runner = new NoIsolationSandboxRunner(launcher);
        var baseSpec = new SandboxLaunchSpec(SandboxStrength.Weak, "/bin/echo", new[] { "hello" }, ".",
            new Dictionary<string, string>(), Array.Empty<SandboxAllowedPath>(),
            new SandboxNetworkPolicy(SandboxNetworkMode.Deny, Array.Empty<string>()));
        Assert.True((await runner.RunAsync(baseSpec, TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await runner.RunAsync(baseSpec with { RequestedStrength = SandboxStrength.None }, TestContext.Current.CancellationToken)).IsSuccess);
        var strong = await runner.RunAsync(baseSpec with { RequestedStrength = SandboxStrength.Strong }, TestContext.Current.CancellationToken);
        Assert.Equal(SandboxResultStatus.Unsupported, strong.Status);
        Assert.Equal(2, launcher.Starts);
        Assert.False(new NoIsolationSandboxCapabilitiesProbe().Probe().StrongAvailable);
    }

    [Fact]
    public void Act_catalog_has_process_and_shell_but_explorer_does_not()
    {
        Assert.True(OmniCore.Host.OmniHost.CreateActTools().Catalog().Contains(new ToolId("process.exec")));
        Assert.True(OmniCore.Host.OmniHost.CreateActTools().Catalog().Contains(new ToolId("shell.exec")));
        Assert.False(OmniCore.Host.OmniHost.CreateExplorerTools().Catalog().Contains(new ToolId("process.exec")));
        Assert.False(OmniCore.Host.OmniHost.CreateExplorerTools().Catalog().Contains(new ToolId("shell.exec")));
    }
}
