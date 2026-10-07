using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Security;
using OmniCore.Sandbox;
using OmniCore.Tools;

namespace OmniCore.Tests;

/// <summary>
/// Proposed runtime regressions for ADR-0046 ToolCallStarted.TargetRef. Process execution is
/// observed through the real Prepare/ToolRuntime path; the launcher is a fixture and never
/// starts a process. Paths here are workspace-relative attribution only, not authorization,
/// reversibility, or a universal URI/reference scheme.
/// </summary>
public sealed class TargetRefRelativeProcessTests
{
    [Theory]
    [InlineData(".", ".")]
    [InlineData("subdir", "subdir")]
    [InlineData("subdir/nested", "subdir/nested")]
    public void Process_exec_started_target_is_relative_to_workspace(string requestedCwd, string expectedTarget)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-targetref-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (requestedCwd != ".") Directory.CreateDirectory(Path.Combine(root, requestedCwd));

        try
        {
            var launcher = new RecordingLauncher();
            var tool = new ProcessExecTool(launcher, new PathBoundaryValidator(), SandboxStrength.Weak);
            var events = new List<DomainEventPayload>();
            var runtime = new ToolRuntime(new FakeCatalog().Add(tool),
                ScriptedPermissionPolicy.WithTool("process.exec", PermissionDecision.Allow),
                payload => { events.Add(payload); return VoidBox.Instance; }, null,
                new FixtureExecutableResolver());
            var call = new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id, "fixture-call",
                "{\"executable\":\"fixture-command\",\"argv\":[],\"cwd\":\""
                    + requestedCwd + "\",\"timeoutSeconds\":1}");

            var result = runtime.Run(call, new ToolPreparationContext(root, DateTimeOffset.UtcNow),
                new ToolExecutionContext(root), false, CancellationToken.None);

            Assert.True(result.Succeeded);
            var started = Assert.Single(events.OfType<ToolCallStarted>());
            Assert.Equal(expectedTarget, started.TargetRef);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, requestedCwd)), launcher.Launch!.WorkingDirectory);
            Assert.Equal(Reversibility.Unknown, started.Reversibility);
            Assert.Null(started.BeforeStateRef);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Filesystem_write_keeps_its_existing_relative_claim_at_started()
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-targetref-filesystem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = "src/output.txt";
        try
        {
            var tool = new FilesystemWriteTool(new PathBoundaryValidator());
            var events = new List<DomainEventPayload>();
            var runtime = new ToolRuntime(new FakeCatalog().Add(tool),
                ScriptedPermissionPolicy.WithTool("filesystem.write", PermissionDecision.Allow),
                payload => { events.Add(payload); return VoidBox.Instance; });
            var call = new ValidatedToolCall(ToolCallId.New(), tool.Descriptor.Id, "fixture-call",
                "{\"path\":\"src/output.txt\",\"content\":\"fixture\"}");

            var result = runtime.Run(call, new ToolPreparationContext(root, DateTimeOffset.UtcNow),
                new ToolExecutionContext(root), false, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal("fixture", File.ReadAllText(Path.Combine(root, "src", "output.txt")));
            var started = Assert.Single(events.OfType<ToolCallStarted>());
            Assert.Equal(target, started.TargetRef);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingLauncher : ISandboxProcessLauncher
    {
        public SandboxLaunchSpec? Launch { get; private set; }

        public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Launch = launch;
            return ValueTask.FromResult<ISandboxProcessControl>(new CompletedProcess());
        }
    }

    private sealed class CompletedProcess : ISandboxProcessControl
    {
        public ValueTask<SandboxProcessOutput> WaitForExitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SandboxProcessOutput(0, Array.Empty<SandboxOutputChunk>()));
        }

        public ValueTask TerminateAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixtureExecutableResolver : IExecutableResolver
    {
        public ExecutableResolution Resolve(string executable, string workspaceRoot) =>
            new(executable, Path.Combine(workspaceRoot, "fixture-only-executable"));
    }
}
