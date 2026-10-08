namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Host;

/// <summary>
/// Bloque de cuenta Claude (ADR-0011 §3.3 + ADR-0012): runtime locator, estado real y sesiones
/// interactivas del binario oficial, sobre un IProcessRuntime falso. Sin OAuth de Anthropic,
/// sin credenciales reales, sin llamadas de red y sin mutaciones de cuenta: solo proceso local,
/// elegibilidad documentada, errores tipados, cancelación y no filtración de secretos.
/// </summary>
public sealed class ClaudeAccountTests
{
    // ---------------------------------------------------------------- fixtures

    private sealed record RecordedLaunch(string Executable, IReadOnlyList<string> Args, string WorkingDirectory,
        IReadOnlyDictionary<string, string> Environment, bool CaptureOutput);

    /// <summary>Fake de IProcessRuntime que respeta timeout y cancelación como el real.</summary>
    private sealed class FakeRuntime : IProcessRuntime
    {
        private readonly Dictionary<int, RecordedLaunch> _byPid = [];
        public List<RecordedLaunch> Launched { get; } = [];
        public List<ProcessHandle> Cancelled { get; } = [];
        public List<TimeSpan> Waits { get; } = [];
        public bool BlockWait { get; set; }
        public bool ThrowOnLaunch { get; set; }
        public string? Stdout { get; set; }
        public string? Stderr { get; set; }
        public int ExitCode { get; set; }
        public Func<ProcessLaunch, ProcessResult>? ResultProvider { get; set; }
        private int _nextPid = 100;

        public ProcessHandle Launch(ProcessLaunch launch, CancellationToken cancellationToken)
        {
            if (ThrowOnLaunch)
            {
                throw new InvalidOperationException("Fallo al lanzar simulado con ruta secreta C:\\secreto\\claude");
            }

            var record = new RecordedLaunch(launch.Executable, launch.Args, launch.WorkingDirectory,
                launch.Environment, launch.CaptureOutput);
            Launched.Add(record);
            var pid = _nextPid++;
            _byPid[pid] = record;
            return new ProcessHandle(pid, this);
        }

        public void CancelTree(ProcessHandle handle) => Cancelled.Add(handle);

        public bool IsAlive(ProcessHandle handle) => true;

        public ProcessResult Wait(ProcessHandle handle, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Waits.Add(timeout);
            var record = _byPid[handle.Pid];
            if (ResultProvider is not null) return ResultProvider(ToLaunch(record));
            if (BlockWait)
            {
                var deadline = DateTime.UtcNow + timeout;
                while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(10);
                }

                return new ProcessResult(-1, null, null, !cancellationToken.IsCancellationRequested);
            }

            return new ProcessResult(ExitCode, Stdout, Stderr, false);
        }

        private static ProcessLaunch ToLaunch(RecordedLaunch record) =>
            new(record.Executable, record.Args, record.WorkingDirectory, record.Environment, record.CaptureOutput);
    }

    private sealed record Fixture(FakeRuntime Runtime, string Root) : IDisposable
    {
        public void Cleanup() => System.IO.Directory.Delete(Root, recursive: true);
        void IDisposable.Dispose() => Cleanup();

        public string? ExecutableName => OperatingSystem.IsWindows() ? "claude.exe" : "claude";

        public static Fixture WithRuntimeOnPath()
        {
            var directory = Path.Combine(Path.GetTempPath(), "omnicore-claude-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var runtime = new FakeRuntime();
            var name = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
            File.WriteAllText(Path.Combine(directory, name), "placeholder binario oficial");
            return new(runtime, directory);
        }

        public ClaudeAccountService Service(TimeSpan? probeTimeout = null) =>
            new(Runtime, new ClaudeCodeRuntimeLocator(
                pathProvider: () => Root,
                profileProvider: () => Root), versionProbeTimeout: probeTimeout ?? TimeSpan.FromSeconds(2));
    }

    // ---------------------------------------------------------------- elegibilidad

    [Fact]
    public void ForPlan_FreeIsNotEligibleWithoutPromises()
    {
        var eligibility = ClaudeAccountEligibility.ForPlan(ClaudePlan.Free);
        Assert.Equal(ClaudeAccountEntitlement.NotEligible, eligibility.Entitlement);
        Assert.Contains("code.claude.com/docs/en/authentication", eligibility.Source);
    }

    [Theory]
    [InlineData(ClaudePlan.Pro)]
    [InlineData(ClaudePlan.Max)]
    [InlineData(ClaudePlan.Team)]
    [InlineData(ClaudePlan.Enterprise)]
    public void ForPlan_SubscriptionPlansAreEligibleWithPrimarySource(ClaudePlan plan)
    {
        var eligibility = ClaudeAccountEligibility.ForPlan(plan);
        Assert.Equal(ClaudeAccountEntitlement.Eligible, eligibility.Entitlement);
        Assert.Equal(plan, eligibility.Plan);
        Assert.Equal(ClaudeAccountEligibility.PrimarySource + "; " +
            ClaudeAccountEligibility.SetupTokenPlanRequirement, eligibility.Source);
    }

    [Fact]
    public void ForPlan_UnknownPlanRequiresMeasurement()
    {
        var eligibility = ClaudeAccountEligibility.ForPlan(ClaudePlan.Unknown);
        Assert.Equal(ClaudeAccountEntitlement.RequiresMeasurement, eligibility.Entitlement);
        Assert.Contains("/status", eligibility.Source);
    }

    // ---------------------------------------------------------------- locator

    [Fact]
    public void Locator_FindsOfficialBinaryOnPath()
    {
        using var fixture = Fixture.WithRuntimeOnPath();
        var locator = new ClaudeCodeRuntimeLocator(() => fixture.Root, () => fixture.Root);
        var located = locator.Locate();
        Assert.NotNull(located);
        Assert.Contains(Path.Combine(fixture.Root, fixture.ExecutableName!), located, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Locator_FallsBackToNativeInstallDirectory()
    {
        var profile = Path.Combine(Path.GetTempPath(), "omnicore-claude-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(profile, ".local", "bin"));
        try
        {
            var name = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
            File.WriteAllText(Path.Combine(profile, ".local", "bin", name), "placeholder");
            var locator = new ClaudeCodeRuntimeLocator(() => null, () => profile);
            Assert.Contains(Path.Combine(profile, ".local", "bin"), locator.Locate(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
        }
    }

    [Fact]
    public void Locator_ReturnsNullWhenNothingInstalled()
    {
        var empty = Path.Combine(Path.GetTempPath(), "omnicore-claude-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var locator = new ClaudeCodeRuntimeLocator(() => empty, () => empty);
            Assert.Null(locator.Locate());
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    // ---------------------------------------------------------------- estado

    [Theory]
    [InlineData(1, false, "versionProbeExit 1")]
    [InlineData(0, true, "versionProbeTimedOut")]
    public async System.Threading.Tasks.Task Status_FailedProbeDoesNotPublishVersionFromPartialOutput(
        int exitCode, bool timedOut, string detail)
    {
        using var fixture = Fixture.WithRuntimeOnPath();
        fixture.Runtime.ResultProvider = _ => new ProcessResult(exitCode, "Claude Code 2.1.269", null, timedOut);
        var status = await fixture.Service().GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.True(status.RuntimePresent);
        Assert.Null(status.RuntimeVersion);
        Assert.Equal(detail, status.Detail);
    }

    [Fact]
    public async System.Threading.Tasks.Task Status_WithoutRuntime_ReportsAbsentWithoutLaunchingAnything()
    {
        var runtime = new FakeRuntime();
        var service = new ClaudeAccountService(runtime, new ClaudeCodeRuntimeLocator(() => null, () => null));
        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.False(status.RuntimePresent);
        Assert.Null(status.RuntimeVersion);
        Assert.Null(status.Detail);
        Assert.Empty(runtime.Launched);
    }

    [Fact]
    public async System.Threading.Tasks.Task Status_WithRuntime_MeasuresVersionThroughARealProcessLaunch()
    {
        using var fixture = Fixture.WithRuntimeOnPath();
        fixture.Runtime.ResultProvider = _ => new ProcessResult(0, "Claude Code 2.1.269 (Anthropic)", null, false);
        var status = await fixture.Service().GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.True(status.RuntimePresent);
        Assert.Equal("2.1.269", status.RuntimeVersion);
        Assert.Null(status.Detail);
        var launch = Assert.Single(fixture.Runtime.Launched);
        Assert.Equal(fixture.ExecutableName, Path.GetFileName(launch.Executable));
        Assert.Equal(["--version"], launch.Args);
        Assert.True(launch.CaptureOutput);
        Assert.Empty(launch.Environment); // delta de entorno vacío: sin CLAUDE_*/ANTHROPIC_* heredados
        Assert.Equal(TimeSpan.FromSeconds(2), fixture.Runtime.Waits.Single()); // probe acotado
    }

    [Fact]
    public async System.Threading.Tasks.Task Status_NeverEchoesChildOutput_OnlyTheExtractedVersion()
    {
        using var fixture = Fixture.WithRuntimeOnPath();
        fixture.Runtime.ResultProvider = _ => new ProcessResult(0,
            "token=sk-ant-fake-not-real-000000 version 2.1.269 build abc", null, false);
        var status = await fixture.Service().GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal("2.1.269", status.RuntimeVersion);
        Assert.DoesNotContain("sk-ant-fake", status.RuntimeVersion, StringComparison.Ordinal);
        if (status.Detail is not null)
        {
            Assert.DoesNotContain("sk-ant-fake", status.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Status_ProbeFails_ReportsPresentWithoutInventingVersion()
    {
        using var fixture = Fixture.WithRuntimeOnPath();
        fixture.Runtime.ResultProvider = _ => new ProcessResult(1, null, null, false);
        var status = await fixture.Service().GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.True(status.RuntimePresent);
        Assert.Null(status.RuntimeVersion);
        Assert.Equal("versionProbeExit 1", status.Detail);
    }

    [Fact]
    public async System.Threading.Tasks.Task Status_ProbeTimeout_ReportsPresentWithoutInventingVersion()
    {
        using var fixture = Fixture.WithRuntimeOnPath();
        fixture.Runtime.BlockWait = true;
        fixture.Runtime.ResultProvider = null;
        var status = await fixture.Service(TimeSpan.FromMilliseconds(200)).GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.True(status.RuntimePresent);
        Assert.Null(status.RuntimeVersion);
        Assert.Equal("versionProbeTimedOut", status.Detail);
        Assert.Single(fixture.Runtime.Cancelled); // el árbol del probe se corta
    }

    [Fact]
    public async System.Threading.Tasks.Task Status_CancelledBeforeLaunch_ThrowsAndLaunchesNothing()
    {
        using var fixture = Fixture.WithRuntimeOnPath();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = fixture.Service();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.GetStatusAsync(cancellation.Token));
        Assert.Empty(fixture.Runtime.Launched);
    }

    // ---------------------------------------------------------------- sesiones interactivas

    [Fact]
    public async System.Threading.Tasks.Task RunInteractive_AccountModesLaunchTheOfficialBinaryAttachedWithoutCapturingStdio()
    {
        using var fixture = Fixture.WithRuntimeOnPath();
        fixture.Runtime.ResultProvider = _ => new ProcessResult(0, null, null, false);
        var service = fixture.Service();
        foreach (var mode in new[] { ClaudeAccountSessionMode.Login, ClaudeAccountSessionMode.Logout,
                     ClaudeAccountSessionMode.OpenSession })
        {
            var outcome = await service.RunInteractiveAsync(mode, TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.False(outcome.TimedOut);
            Assert.False(outcome.Cancelled);
            Assert.Equal(0, outcome.ExitCode);
        }

        Assert.Equal(3, fixture.Runtime.Launched.Count);
        foreach (var launch in fixture.Runtime.Launched)
        {
            Assert.Equal(fixture.ExecutableName, Path.GetFileName(launch.Executable));
            Assert.Empty(launch.Args); // el usuario escribe /login y /logout; OmniCore no maneja el stdin
            Assert.False(launch.CaptureOutput);
            Assert.Empty(launch.Environment);
        }

        Assert.Empty(fixture.Runtime.Cancelled);
    }

    [Fact]
    public async System.Threading.Tasks.Task RunInteractive_SetupTokenRunsTheDocumentedCommandWithoutCapturingStdio()
    {
        using var fixture = Fixture.WithRuntimeOnPath();
        fixture.Runtime.ResultProvider = _ => new ProcessResult(0, null, null, false);
        var outcome = await fixture.Service().RunInteractiveAsync(ClaudeAccountSessionMode.SetupToken,
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, outcome.ExitCode);
        var launch = Assert.Single(fixture.Runtime.Launched);
        Assert.Equal(["setup-token"], launch.Args);
        Assert.False(launch.CaptureOutput);
        Assert.Equal("exit 0", outcome.Detail); // solo estado técnico, nunca salida del hijo
    }

    [Fact]
    public async System.Threading.Tasks.Task RunInteractive_WithoutRuntime_ThrowsTypedErrorAndNeverLaunches()
    {
        var runtime = new FakeRuntime();
        var service = new ClaudeAccountService(runtime, new ClaudeCodeRuntimeLocator(() => null, () => null));
        var exception = await Assert.ThrowsAsync<ClaudeAccountException>(() =>
            service.RunInteractiveAsync(ClaudeAccountSessionMode.Login, TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
        Assert.Equal("runtimeNotInstalled", exception.Kind);
        Assert.Equal("claude.auth.runtimeNotInstalled", exception.UserMessage.Key);
        Assert.Empty(runtime.Launched);
    }

    [Fact]
    public async System.Threading.Tasks.Task RunInteractive_TimeoutCutsTheProcessTree()
    {
        using var fixture = Fixture.WithRuntimeOnPath();
        fixture.Runtime.BlockWait = true;
        var outcome = await fixture.Service().RunInteractiveAsync(ClaudeAccountSessionMode.Login,
            TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        Assert.True(outcome.TimedOut);
        Assert.False(outcome.Cancelled);
        Assert.Null(outcome.ExitCode);
        Assert.Equal("timeout", outcome.Detail);
        Assert.Single(fixture.Runtime.Cancelled);
    }

    [Fact]
    public async System.Threading.Tasks.Task RunInteractive_CancelledCutsTheProcessTree()
    {
        using var fixture = Fixture.WithRuntimeOnPath();
        fixture.Runtime.BlockWait = true;
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(150));
        var outcome = await fixture.Service().RunInteractiveAsync(ClaudeAccountSessionMode.Login,
            TimeSpan.FromMinutes(1), cancellation.Token);
        Assert.True(outcome.Cancelled);
        Assert.False(outcome.TimedOut);
        Assert.Null(outcome.ExitCode);
        Assert.Equal("cancelled", outcome.Detail);
        Assert.Single(fixture.Runtime.Cancelled);
    }

    [Fact]
    public async System.Threading.Tasks.Task RunInteractive_CancelledBeforeLaunch_ThrowsAndLaunchesNothing()
    {
        using var fixture = Fixture.WithRuntimeOnPath();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Service().RunInteractiveAsync(ClaudeAccountSessionMode.Login, TimeSpan.FromSeconds(5),
                cancellation.Token));
        Assert.Empty(fixture.Runtime.Launched);
    }

    [Fact]
    public async System.Threading.Tasks.Task RunInteractive_LaunchFailureThrowsTypedErrorWithoutEchoingOsMessage()
    {
        using var fixture = Fixture.WithRuntimeOnPath();
        fixture.Runtime.ThrowOnLaunch = true;
        var exception = await Assert.ThrowsAsync<ClaudeAccountException>(() =>
            fixture.Service().RunInteractiveAsync(ClaudeAccountSessionMode.Login, TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
        Assert.Equal("runtimeLaunchFailed", exception.Kind);
        Assert.DoesNotContain("secreto", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Fallo al lanzar simulado", exception.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Runtime.Launched); // el lanzamiento falló antes de registrar el proceso
    }

    // Escenario Windows: shim de npm (.cmd) se lanza vía cmd.exe con argv tipado.
    [Fact]
    public async System.Threading.Tasks.Task RunInteractive_OnWindowsCmdShimDelegatesThroughCmdExe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "omnicore-claude-cmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "claude.cmd"), "placeholder");
            var runtime = new FakeRuntime { ResultProvider = _ => new ProcessResult(0, null, null, false) };
            var service = new ClaudeAccountService(runtime, new ClaudeCodeRuntimeLocator(
                () => directory, () => directory), versionProbeTimeout: TimeSpan.FromSeconds(2));
            await service.RunInteractiveAsync(ClaudeAccountSessionMode.Login, TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            var launch = Assert.Single(runtime.Launched);
            Assert.Equal("cmd.exe", launch.Executable);
            var command = Assert.Single(launch.Args.Skip(3));
            Assert.EndsWith("claude.cmd", command, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(["/d", "/s", "/c"], launch.Args.Take(3).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------------------------------------------------------------- versión

    [Theory]
    [InlineData("2.1.269 (Claude Code)", "2.1.269")]
    [InlineData("claude 1.2.3", "1.2.3")]
    [InlineData("v12.0.0", "12.0.0")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("sin versión", null)]
    [InlineData("1.2", null)]
    [InlineData("1.2.3.4", null)]
    [InlineData("no 12a.3.4", null)]
    public void ParseVersion_ExtractsOnlyTheFirstValidVersion(string? output, string? expected)
    {
        Assert.Equal(expected, ClaudeAccountService.ParseVersion(output));
    }

    [Fact]
    public void RunInteractive_SetupTokenOutcomeNeverContainsChildOutput()
    {
        // El contrato de ClaudeSessionOutcome no tiene campo de salida: se verifica por tipo.
        var properties = typeof(ClaudeSessionOutcome).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { "ExitCode", "TimedOut", "Cancelled", "Detail" }, properties);
        Assert.Equal(4, properties.Count);
    }
}
