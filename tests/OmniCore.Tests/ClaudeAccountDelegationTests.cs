namespace OmniCore.Tests;

using OmniCore.Abstractions;
using OmniCore.Host;

/// <summary>
/// Comportamiento real de la delegación puntual al CLI oficial de Claude Code
/// (<see cref="ClaudeAccountDelegationService"/>), reevaluación OAuth 2026-10-08 (ADR-0011
/// §10.7 rev. 4 + ADR-0012 §2 + página oficial «Run Claude Code programmatically»).
///
/// Sustituye a los antiguos tests de veto por literales: aquí lo que se prueba es el camino
/// implementado y elegido — cancelación, flujo request→result, errores, no filtración de
/// secretos y ausencia estructural de fallback a la vía de API pagada. Todo sobre un
/// <c>IProcessRuntime</c> falso: ningún test autentica, muta cuentas ni consulta la red.
/// (El refresh/expiry no aplica en OmniCore: el CLI oficial refresca sus propios tokens dentro
/// de su propio flujo y OmniCore nunca ve tokens — documentado en el handoff y la validación.)
/// </summary>
public sealed class ClaudeAccountDelegationTests
{
    // ---------------------------------------------------------------- fixtures

    private sealed class FakeRuntime : IProcessRuntime
    {
        public List<ProcessLaunch> Launched { get; } = [];
        public List<ProcessHandle> Cancelled { get; } = [];
        public bool ThrowOnLaunch { get; set; }
        public bool BlockWait { get; set; }
        public Func<ProcessLaunch, ProcessResult>? ResultProvider { get; set; }
        private int _nextPid = 500;

        public ProcessHandle Launch(ProcessLaunch launch, CancellationToken cancellationToken)
        {
            if (ThrowOnLaunch) throw new InvalidOperationException("spawn fallado con secreto C:\\claude-secreto");
            Launched.Add(launch);
            return new ProcessHandle(_nextPid++, this);
        }

        public void CancelTree(ProcessHandle handle) => Cancelled.Add(handle);

        public bool IsAlive(ProcessHandle handle) => true;

        public ProcessResult Wait(ProcessHandle handle, TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (BlockWait)
            {
                var deadline = DateTime.UtcNow + timeout;
                while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(10);
                }

                return new ProcessResult(-1, null, null, !cancellationToken.IsCancellationRequested);
            }

            return ResultProvider?.Invoke(Launched[^1]) ?? new ProcessResult(0, null, null, false);
        }
    }

    private static ClaudeDelegationRequest Request(string prompt = "Resume este repo en una frase",
        string? workingDirectory = null, string? model = null, int? maxTurns = null,
        double? maxBudgetUsd = null, string? jsonSchema = null) =>
        new(prompt, workingDirectory, model, maxTurns, maxBudgetUsd, jsonSchema);

    /// <summary>JSON <c>result</c> de éxito con el esquema documentado del CLI oficial
    /// (<c>packages/headless-sdk/src/coreSchemas.ts</c>, rev. de referencia 7c918f78).</summary>
    private static string SuccessJson(string resultText = "Respuesta delegada de prueba",
        string? structured = null)
    {
        var escaped = resultText.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var structuredPart = structured is null ? "" : ",\"structured_output\":" + structured;
        return "{\"type\":\"result\",\"subtype\":\"success\",\"duration_ms\":1234," +
            "\"duration_api_ms\":900,\"is_error\":false,\"num_turns\":1,\"result\":\"" + escaped +
            "\",\"stop_reason\":\"end_turn\",\"total_cost_usd\":0.0123,\"usage\":{\"input_tokens\":4321," +
            "\"output_tokens\":123,\"cache_read_input_tokens\":4000,\"cache_creation_input_tokens\":321}," +
            "\"permission_denials\":[],\"uuid\":\"u-1\",\"session_id\":\"s-abc\"" + structuredPart + "}";
    }

    private sealed record Fixture(FakeRuntime Runtime, string Root) : IDisposable
    {
        public void Dispose() => Directory.Delete(Root, recursive: true);

        /// <summary>Fixture con un binario oficial simulado en un directorio temporal: el locator
        /// lo encuentra sin tocar el PATH real ni el binario instalado del usuario.</summary>
        public static Fixture Create()
        {
            var root = TempDir("root");
            var name = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
            File.WriteAllText(Path.Combine(root, name), "placeholder binario oficial");
            return new Fixture(new FakeRuntime(), root);
        }

        public ClaudeAccountDelegationService Service(TimeSpan? defaultTimeout = null) =>
            new(Runtime, new ClaudeCodeRuntimeLocator(() => Root, () => Root),
                defaultTimeout: defaultTimeout ?? TimeSpan.FromSeconds(3));
    }

    private static (FakeRuntime Runtime, ClaudeAccountDelegationService Service) Service()
    {
        var runtime = new FakeRuntime();
        var service = new ClaudeAccountDelegationService(runtime,
            new ClaudeCodeRuntimeLocator(() => null, () => null),
            defaultTimeout: TimeSpan.FromSeconds(3));
        return (runtime, service);
    }

    private static string TempDir(string tag)
    {
        var directory = Path.Combine(Path.GetTempPath(),
            "omnicore-claude-deleg-" + tag + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    // ---------------------------------------------------------------- flujo request → result

    [Fact]
    public async Task RunAsync_SuccessResult_ReturnsTheRealResponseIntoOmniCore()
    {
        using var fixture = Fixture.Create();
        var runtime = fixture.Runtime;
        var service = fixture.Service();
        runtime.ResultProvider = _ => new ProcessResult(0, SuccessJson(), null, false);

        var outcome = await service.RunAsync(Request(), TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(ClaudeDelegationTerminalKind.Completed, outcome.Kind);
        Assert.True(outcome.Success);
        Assert.Equal("Respuesta delegada de prueba", outcome.Response);
        Assert.Equal("s-abc", outcome.SessionId);
        Assert.Equal(1, outcome.NumTurns);
        Assert.Equal(0.0123, outcome.TotalCostUsd);
        Assert.NotNull(outcome.Usage);
        Assert.Equal(4321, outcome.Usage.InputTokens);
        Assert.Equal(123, outcome.Usage.OutputTokens);
        Assert.Equal(4000, outcome.Usage.CacheReadInputTokens);
        Assert.Equal(321, outcome.Usage.CacheCreationInputTokens);
        Assert.Empty(outcome.Errors);
        Assert.Equal("exit 0", outcome.Detail);
        Assert.Empty(runtime.Cancelled);
    }

    [Fact]
    public async Task RunAsync_LaunchesTheDocumentedHeadlessInvocationWithTypedArgv()
    {
        var workdir = TempDir("argv");
        try
        {
            using var fixture = Fixture.Create();
        var runtime = fixture.Runtime;
        var service = fixture.Service();
            runtime.ResultProvider = _ => new ProcessResult(0, SuccessJson(), null, false);

            await service.RunAsync(Request(workingDirectory: workdir, model: "sonnet",
                maxTurns: 2, maxBudgetUsd: 0.5), TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            var launch = Assert.Single(runtime.Launched);
            Assert.EndsWith(OperatingSystem.IsWindows() ? "claude.exe" : "claude",
                launch.Executable, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(["-p", "Resume este repo en una frase", "--output-format", "json",
                "--permission-mode", "dontAsk", "--permission-prompts", "none",
                "--model", "sonnet", "--max-turns", "2", "--max-budget-usd", "0.5"],
                launch.Args);
            Assert.Equal(workdir, launch.WorkingDirectory);
            Assert.True(launch.CaptureOutput); // la respuesta entra a OmniCore por el stdout capturado
        }
        finally
        {
            Directory.Delete(workdir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_DefaultsUseTheBoundedOneShotShape()
    {
        using var fixture = Fixture.Create();
        var runtime = fixture.Runtime;
        var service = fixture.Service();
        runtime.ResultProvider = _ => new ProcessResult(0, SuccessJson(), null, false);

        await service.RunAsync(Request(maxTurns: 3), null, TestContext.Current.CancellationToken);

        var launch = Assert.Single(runtime.Launched);
        // Sin opciones: solo las bases documentadas + el tope de turnos explícito del llamador.
        Assert.Equal(["-p", "Resume este repo en una frase", "--output-format", "json",
            "--permission-mode", "dontAsk", "--permission-prompts", "none", "--max-turns", "3"],
            launch.Args);
        Assert.Equal(TimeSpan.FromMinutes(10),
            ClaudeAccountDelegationService.DefaultDelegationTimeout);
    }

    [Fact]
    public async Task RunAsync_StructuredOutput_ReturnsTheRawDocumentedField()
    {
        using var fixture = Fixture.Create();
        var runtime = fixture.Runtime;
        var service = fixture.Service();
        runtime.ResultProvider = _ =>
            new ProcessResult(0, SuccessJson(structured: "{\"names\":[\"a\",\"b\"]}"), null, false);

        var outcome = await service.RunAsync(Request(jsonSchema:
            "{\"type\":\"object\",\"properties\":{\"names\":{\"type\":\"array\"," +
            "\"items\":{\"type\":\"string\"}}},\"required\":[\"names\"]}"),
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(outcome.Success);
        Assert.Equal("{\"names\":[\"a\",\"b\"]}", outcome.StructuredOutputJson);
        var launch = Assert.Single(runtime.Launched);
        Assert.Contains("--json-schema", launch.Args);
    }

    // ---------------------------------------------------------------- errores del CLI

    [Fact]
    public async Task RunAsync_ErrorResult_IsReportedAsTypedOutcomeWithoutThrowing()
    {
        using var fixture = Fixture.Create();
        var runtime = fixture.Runtime;
        var service = fixture.Service();
        runtime.ResultProvider = _ => new ProcessResult(1,
            "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"duration_ms\":10," +
            "\"duration_api_ms\":0,\"is_error\":true,\"num_turns\":0,\"stop_reason\":null," +
            "\"total_cost_usd\":0,\"usage\":{},\"permission_denials\":[]," +
            "\"errors\":[\"authentication_failed\"],\"uuid\":\"u-2\",\"session_id\":\"s-err\"}",
            null, false);

        var outcome = await service.RunAsync(Request(), TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(ClaudeDelegationTerminalKind.Completed, outcome.Kind);
        Assert.False(outcome.Success);
        Assert.Null(outcome.Response);
        Assert.Equal("error_during_execution", outcome.Subtype);
        Assert.Equal(["authentication_failed"], outcome.Errors);
        Assert.Equal(1, outcome.ExitCode);
        // Sin fallback: un fallo del CLI no dispara ninguna segunda vía (un solo lanzamiento).
        Assert.Single(runtime.Launched);
    }

    [Fact]
    public async Task RunAsync_NoResultMessage_NeverEchoesChildOutputAndKeepsExitCode()
    {
        using var fixture = Fixture.Create();
        var runtime = fixture.Runtime;
        var service = fixture.Service();
        runtime.ResultProvider = _ => new ProcessResult(2,
            "Error: unknown option '--flag-que-no-existe'\ntoken interno sk-ant-fake-no-real",
            "stderr con credencial sk-ant-fake-no-real", false);

        var outcome = await service.RunAsync(Request(), TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(ClaudeDelegationTerminalKind.NoResultMessage, outcome.Kind);
        Assert.False(outcome.Success);
        Assert.Null(outcome.Response);
        Assert.Null(outcome.SessionId);
        Assert.Null(outcome.Usage);
        Assert.Equal(2, outcome.ExitCode);
        Assert.Equal("noResultMessage exit 2", outcome.Detail);
        // Ningún campo del outcome replica stdout/stderr del hijo (ni su texto ni su secreto).
        Assert.DoesNotContain("sk-ant-fake",
            string.Join("|", outcome.Errors) + "|" + outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown option", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MaxTurnsErrorSubtype_IsAnHonestCompletedFailure()
    {
        using var fixture = Fixture.Create();
        var runtime = fixture.Runtime;
        var service = fixture.Service();
        runtime.ResultProvider = _ => new ProcessResult(1,
            "{\"type\":\"result\",\"subtype\":\"error_max_turns\",\"duration_ms\":10," +
            "\"duration_api_ms\":0,\"is_error\":true,\"num_turns\":3,\"stop_reason\":null," +
            "\"total_cost_usd\":0.05,\"usage\":{},\"permission_denials\":[]," +
            "\"errors\":[\"Reached max turns\"],\"uuid\":\"u-3\",\"session_id\":\"s-mt\"}",
            null, false);

        var outcome = await service.RunAsync(Request(), TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Success);
        Assert.Equal("error_max_turns", outcome.Subtype);
        Assert.Equal(3, outcome.NumTurns);
        Assert.Equal(["Reached max turns"], outcome.Errors);
    }

    // ---------------------------------------------------------------- cancelación y timeout

    [Fact]
    public async Task RunAsync_TimeoutCutsTheProcessTreeAndReportsTimedOut()
    {
        using var fixture = Fixture.Create();
        var runtime = fixture.Runtime;
        var service = fixture.Service();
        runtime.BlockWait = true;

        var outcome = await service.RunAsync(Request(), TimeSpan.FromMilliseconds(250),
            TestContext.Current.CancellationToken);

        Assert.Equal(ClaudeDelegationTerminalKind.TimedOut, outcome.Kind);
        Assert.False(outcome.Success);
        Assert.Null(outcome.Response);
        Assert.Single(runtime.Cancelled); // el árbol se corta (ADR-0012 §5)
        Assert.Equal("timeout", outcome.Detail);
    }

    [Fact]
    public async Task RunAsync_CancellationMidRunCutsTheProcessTreeAndReportsCancelled()
    {
        using var fixture = Fixture.Create();
        var runtime = fixture.Runtime;
        var service = fixture.Service();
        runtime.BlockWait = true;
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

        var outcome = await service.RunAsync(Request(), TimeSpan.FromMinutes(5), cancellation.Token);

        Assert.Equal(ClaudeDelegationTerminalKind.Cancelled, outcome.Kind);
        Assert.False(outcome.Success);
        Assert.Null(outcome.Response);
        Assert.Single(runtime.Cancelled);
        Assert.Equal("cancelled", outcome.Detail);
    }

    [Fact]
    public async Task RunAsync_PreCancelled_ThrowsAndLaunchesNothing()
    {
        using var fixture = Fixture.Create();
        var runtime = fixture.Runtime;
        var service = fixture.Service();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.RunAsync(Request(), TimeSpan.FromSeconds(5), cancellation.Token));
        Assert.Empty(runtime.Launched);
        Assert.Empty(runtime.Cancelled);
    }

    // ---------------------------------------------------------------- errores del bloque

    [Fact]
    public async Task RunAsync_WithoutRuntime_ThrowsTypedRuntimeNotInstalled()
    {
        var runtime = new FakeRuntime();
        var service = new ClaudeAccountDelegationService(runtime,
            new ClaudeCodeRuntimeLocator(() => null, () => null));

        var exception = await Assert.ThrowsAsync<ClaudeAccountException>(() =>
            service.RunAsync(Request(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal("runtimeNotInstalled", exception.Kind);
        Assert.Equal("claude.auth.runtimeNotInstalled", exception.UserMessage.Key);
        Assert.Empty(runtime.Launched);
    }

    [Fact]
    public async Task RunAsync_LaunchFailure_ThrowsTypedWithoutEchoingOsMessageOrPaths()
    {
        using var fixture = Fixture.Create();
        var runtime = fixture.Runtime;
        var service = fixture.Service();
        runtime.ThrowOnLaunch = true;

        var exception = await Assert.ThrowsAsync<ClaudeAccountException>(() =>
            service.RunAsync(Request(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal("runtimeLaunchFailed", exception.Kind);
        Assert.DoesNotContain("secreto", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("spawn fallado", exception.Message, StringComparison.Ordinal);
        Assert.Empty(runtime.Launched);
    }

    [Fact]
    public async Task RunAsync_EmptyPrompt_ThrowsArgumentWithoutLaunching()
    {
        using var fixture = Fixture.Create();
        var runtime = fixture.Runtime;
        var service = fixture.Service();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RunAsync(Request(prompt: "   "), TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
        Assert.Empty(runtime.Launched);
    }

    // ---------------------------------------------------------------- no filtración de secretos

    [Fact]
    public async Task RunAsync_ChildEnvironmentDeltaIsAlwaysEmpty_NoClaudeOrAnthropicVariables()
    {
        using var fixture = Fixture.Create();
        var runtime = fixture.Runtime;
        var service = fixture.Service();
        runtime.ResultProvider = _ => new ProcessResult(0, SuccessJson(), null, false);

        await service.RunAsync(Request(), TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var launch = Assert.Single(runtime.Launched);
        // Delta vacío: ANTHROPIC_API_KEY/CLAUDE_CODE_OAUTH_TOKEN nunca se heredan de OmniCore
        // (la allowlist de plataforma ADR-0037 los excluye también en producción).
        Assert.Empty(launch.Environment);
    }

    [Fact]
    public async Task RunAsync_StderrWithSecret_IsNeverCopiedIntoTheOutcome()
    {
        using var fixture = Fixture.Create();
        var runtime = fixture.Runtime;
        var service = fixture.Service();
        runtime.ResultProvider = _ => new ProcessResult(0, SuccessJson(),
            "diag interno sk-ant-fake-no-real", false);

        var outcome = await service.RunAsync(Request(), TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Success);
        Assert.Equal("exit 0", outcome.Detail);
        Assert.DoesNotContain("sk-ant-fake", outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant-fake", outcome.Subtype ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void OutcomeRecord_NeverCarriesRawStdoutStderrOrEnvironmentFields()
    {
        var properties = typeof(ClaudeDelegationOutcome).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.Equal(new HashSet<string>
        {
            "Kind", "Success", "Response", "StructuredOutputJson", "SessionId", "NumTurns",
            "TotalCostUsd", "Usage", "Errors", "Subtype", "ExitCode", "Detail",
        }, properties);
    }

    // ---------------------------------------------------------------- sin fallback a la vía pagada

    [Fact]
    public void Service_HasNoCredentialOrSecretDependency_StructurallyNoPaidApiFallback()
    {
        // El constructor no acepta ICredentialStore/ISecretProvider ni factories HTTP: no existe
        // ninguna vía de código desde este backend hacia la credencial `anthropic` (bloque 1,
        // MeteredCurrency) ni hacia llamadas HTTP autenticadas.
        var constructors = typeof(ClaudeAccountDelegationService).GetConstructors();
        Assert.Single(constructors);
        var dependencies = constructors[0].GetParameters().Select(p => p.ParameterType.Name).ToArray();
        Assert.Equal(["IProcessRuntime", "ClaudeCodeRuntimeLocator", "Nullable`1"], dependencies);
        Assert.DoesNotContain(dependencies, name => name.Contains("Credential", StringComparison.Ordinal));
        Assert.DoesNotContain(dependencies, name => name.Contains("Secret", StringComparison.Ordinal));
        Assert.DoesNotContain(dependencies, name => name.Contains("Http", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsync_DelegationFailureRunsExactlyOneProcess_NoSecondaryPath()
    {
        using var fixture = Fixture.Create();
        var runtime = fixture.Runtime;
        var service = fixture.Service();
        runtime.ResultProvider = _ => new ProcessResult(1,
            "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true," +
            "\"errors\":[\"authentication_failed\"]}",
            null, false);

        var outcome = await service.RunAsync(Request(), TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.False(outcome.Success);
        // Exactamente un proceso durante todo el ciclo: el CLI oficial, nada más.
        Assert.Single(runtime.Launched);
        Assert.Empty(runtime.Cancelled);
    }

    // ---------------------------------------------------------------- shim npm en Windows

    [Fact]
    public async Task RunAsync_OnWindowsCmdShimDelegatesThroughCmdExeWithCapturedOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = TempDir("cmd");
        try
        {
            File.WriteAllText(Path.Combine(directory, "claude.cmd"), "placeholder");
            var runtime = new FakeRuntime { ResultProvider = _ => new ProcessResult(0, SuccessJson(), null, false) };
            var service = new ClaudeAccountDelegationService(runtime,
                new ClaudeCodeRuntimeLocator(() => directory, () => directory),
                defaultTimeout: TimeSpan.FromSeconds(3));

            var outcome = await service.RunAsync(Request(), TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.True(outcome.Success);

            var launch = Assert.Single(runtime.Launched);
            Assert.Equal("cmd.exe", launch.Executable);
            Assert.Equal(["/d", "/s", "/c"], launch.Args.Take(3).ToArray());
            Assert.EndsWith("claude.cmd", launch.Args[3], StringComparison.OrdinalIgnoreCase);
            Assert.Equal("-p", launch.Args[4]);
            Assert.True(launch.CaptureOutput);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------------------------------------------------------------- parser puro

    [Fact]
    public void ParseResultMessage_NullOrNonResultJson_ProducesNoResultMessageWithExitCode()
    {
        foreach (var stdout in new[] { null, "", "   ", "texto sin json", "{\"type\":\"system\"}" })
        {
            var outcome = ClaudeAccountDelegationService.ParseResultMessage(stdout, 7);
            Assert.Equal(ClaudeDelegationTerminalKind.NoResultMessage, outcome.Kind);
            Assert.Equal(7, outcome.ExitCode);
        }
    }

    [Fact]
    public void ParseResultMessage_ResultSurroundedByNoise_IsStillFoundFromTheLastLines()
    {
        var stdout = "aviso inicial\n" + SuccessJson() + "\n";
        var outcome = ClaudeAccountDelegationService.ParseResultMessage(stdout, 0);
        Assert.Equal(ClaudeDelegationTerminalKind.Completed, outcome.Kind);
        Assert.True(outcome.Success);
        Assert.Equal("Respuesta delegada de prueba", outcome.Response);
    }

    [Fact]
    public void ParseResultMessage_MissingUsageFields_StaysNullWithoutInventingZeros()
    {
        const string stdout = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false," +
            "\"num_turns\":1,\"result\":\"ok\",\"session_id\":\"s\",\"total_cost_usd\":0}";
        var outcome = ClaudeAccountDelegationService.ParseResultMessage(stdout, 0);
        Assert.True(outcome.Success);
        Assert.Null(outcome.Usage); // usage ausente → null, nunca ceros inventados
    }

    [Fact]
    public void ParseResultMessage_ZeroCostIsSurfacedAsZero()
    {
        var outcome = ClaudeAccountDelegationService.ParseResultMessage(
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"ok\"," +
            "\"total_cost_usd\":0}", 0);
        Assert.Equal(0.0, outcome.TotalCostUsd);
    }

    [Fact]
    public void OmniHostFactory_WiresTheRealDelegationService()
    {
        var service = OmniHost.CreateClaudeAccountDelegationService();
        Assert.IsType<ClaudeAccountDelegationService>(service);
    }
}
