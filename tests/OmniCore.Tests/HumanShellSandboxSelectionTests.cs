namespace OmniCore.Tests;

using System.Text;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Execution;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Sandbox;
using OmniCore.Security;
using OmniCore.Tools;
using Task = System.Threading.Tasks.Task;

public sealed class HumanShellSandboxSelectionTests
{
    [Theory]
    [InlineData(StrongSandboxPreflightResult.Available, SandboxStrength.Strong, true)]
    [InlineData(StrongSandboxPreflightResult.UnavailableBeforeLaunch, SandboxStrength.Weak, true)]
    [InlineData(StrongSandboxPreflightResult.Unknown, SandboxStrength.Strong, false)]
    public async Task Automatic_permission_prefers_Strong_and_only_falls_back_after_proven_unavailability(
        StrongSandboxPreflightResult availability, SandboxStrength expected, bool succeeds)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-human-shell-sandbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var manager = new PersistentProcessSessionManager(SessionId.New(), new InMemoryEventStore(),
            EventCodecs.Create(), Guid.NewGuid().ToString("N"), root, null, static () => true);
        var launcher = new RecordingLauncher(availability);
        var tool = new HumanShellProcessTool(manager, launcher, SandboxStrength.Strong,
            new PathBoundaryValidator(), writeOutput: null);
        var policy = new ScriptedPermissionPolicy(new()).WithModeDefaults(RunMode.Act)
            .WithProfile(PermissionProfile.Autonomous);
        var events = new List<DomainEventPayload>();
        var runtime = new ToolRuntime(new FakeCatalog().Add(tool), policy,
            item => { events.Add(item); return VoidBox.Instance; }, null, new FixedResolver());
        var context = new ToolExecutionContext(root, null, emitEvent: events.Add,
            mutationRunId: RunId.New().ToString(), mutationLaneId: LaneId.New().ToString(),
            mutationTurnId: TurnId.New().ToString(), isNativeUserAction: true);
        try
        {
            var call = new ValidatedToolCall(ToolCallId.New(), new("shell.exec"), "human-shell",
                JsonSerializer.Serialize(new { command = "echo marker", cwd = root, timeoutSeconds = 5 }));
            var result = runtime.Run(call, new(root, DateTimeOffset.UtcNow, isNativeUserAction: true),
                context, false, TestContext.Current.CancellationToken);
            Assert.True(succeeds == result.Succeeded, result.Summary);
            Assert.Equal(expected, Assert.Single(launcher.Attempts).RequestedStrength);
            Assert.DoesNotContain(events, item => item is InteractionRequested);
            Assert.False(context.WeakSandboxConsentForThisCall);
            Assert.False(context.AutomaticSandboxConsentForThisCall);
            // Display output is subject to the process-wide secret redactor. Validate
            // the sandbox's terminal receipt and effect, independent of display text.
            if (succeeds)
            {
                using var preview = JsonDocument.Parse(result.Preview!);
                Assert.Equal("exited", preview.RootElement.GetProperty("state").GetString());
                Assert.Equal(0, preview.RootElement.GetProperty("exitCode").GetInt32());
                Assert.Equal(EffectOutcome.Applied, result.Effect);
            }
            else Assert.Equal(EffectOutcome.Unknown, result.Effect);
        }
        finally
        {
            await manager.StopAllAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Automatic_context_copy_preserves_an_explicit_Weak_continuation()
    {
        var original = new ToolExecutionContext(Path.GetTempPath(), null, weakSandboxConsentForThisCall: true);
        var copy = original.WithAutomaticSandboxConsentForThisCall();
        Assert.True(copy.WeakSandboxConsentForThisCall);
        Assert.False(copy.AutomaticSandboxConsentForThisCall);
        Assert.False(original.AutomaticSandboxConsentForThisCall);
    }

    // Commands are never executed by this fixture; retain the prepared absolute shell path.
    private sealed class FixedResolver : IExecutableResolver
    {
        public ExecutableResolution Resolve(string executable, string workspaceRoot) => new(executable, executable);
    }

    private sealed class RecordingLauncher(StrongSandboxPreflightResult availability)
        : ISandboxProcessLauncher, IStrongSandboxPreflight
    {
        internal List<SandboxLaunchSpec> Attempts { get; } = [];
        public StrongSandboxPreflightResult CheckStrongAvailability(SandboxLaunchSpec launch) => availability;
        public ValueTask<ISandboxProcessControl> StartAsync(SandboxLaunchSpec launch, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Attempts.Add(launch);
            if (availability == StrongSandboxPreflightResult.Unknown)
                throw new StrongSandboxLaunchOutcomeUnknownException("fixture_unknown", new IOException());
            return ValueTask.FromResult<ISandboxProcessControl>(new CompletedControl());
        }
    }

    private sealed class CompletedControl : ISandboxPersistentProcessControl
    {
        public Stream StandardInput { get; } = new MemoryStream();
        public Stream StandardOutput { get; } = new MemoryStream(Encoding.UTF8.GetBytes("human-shell-marker"));
        public Stream StandardError { get; } = new MemoryStream();
        public bool IsTerminal => false;
        public ValueTask<int> WaitForExitCodeAsync(CancellationToken token) => ValueTask.FromResult(0);
        public ValueTask<SandboxProcessOutput> WaitForExitAsync(CancellationToken token) =>
            ValueTask.FromResult(new SandboxProcessOutput(0, []));
        public ValueTask TerminateAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask CloseInputAsync() => ValueTask.CompletedTask;
        public void ResizeTerminal(short columns, short rows) => throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            StandardInput.Dispose(); StandardOutput.Dispose(); StandardError.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
