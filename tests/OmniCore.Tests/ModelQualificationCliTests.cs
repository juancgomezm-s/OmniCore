using System.Net;
using System.Net.Sockets;
using System.Text;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Models;
using OmniCore.Qualification;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

/// <summary>
/// Tests del comando `omni model qualify` (M5, ADR-0007 §6–§7): consentimiento explícito
/// (--yes o aviso interactivo; sin TTY y sin --yes se rechaza), y el recorrido completo contra
/// un provider HTTP scripteado — el MISMO camino de provider del runtime, sin atajo — imprimiendo
/// estados, traits y la recomendación, que nunca toca la política operativa (ADR-0044 §9).
/// Muta variables de entorno: corre en la colección serializada de proceso.
/// </summary>
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class ModelQualificationCliTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omnicore-m5-qual-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<int> Run(string[] args, string input, TextWriter output, string dataDir,
        bool interactive = false)
    {
        // Real CLI configuration path, with an explicit Local descriptor for the private HTTP fixture.
        // A registry override with no billing descriptor must not imply a free provider.
        var paths = OmniHost.CreatePlatformPaths(dataDir);
        Directory.CreateDirectory(paths.ConfigDirectory);
        var baseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL") ?? "http://127.0.0.1:1/v1";
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), $$"""
            providers:
              local:
                family: OpenAiChatCompatible
                baseUrl: {{baseUrl}}
                auth: none
                billingMode: Local
            """);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "models.yaml"), """
            models:
              qwen-test:
                provider: local
                context: 8192
                recommendedUsableContext: 8192
                maxOutput: 2048
            """);
        return await OmniCore.Cli.ModelPolicyCommands.Run(args, new StringReader(input), output, interactive,
            dataDir);
    }

    private static ModelQualificationKey QualificationKey(string dataDir)
    {
        var configuration = OmniHost.LoadUserConfiguration(OmniHost.CreatePlatformPaths(dataDir));
        var model = configuration.Registry.Model("qwen-test")!;
        return ModelQualificationHost.QualificationKeyFor(model, configuration.Registry.Provider(model.ProviderId));
    }

    [Fact]
    public async Task Qualify_refuses_without_yes_and_without_tty()
    {
        var output = new StringWriter();

        var code = await Run(new[] { "model", "qualify", "qwen-test" }, "", output, TempDir(), interactive: false);

        Assert.Equal(1, code);
        Assert.Contains("requiere consentimiento explícito", output.ToString());
        Assert.Contains("--yes", output.ToString());
    }

    [Fact]
    public async Task Qualify_interactive_prompt_declined_runs_nothing()
    {
        var output = new StringWriter();

        var code = await Run(new[] { "model", "qualify", "qwen-test" }, "n\n", output, TempDir(),
            interactive: true);

        Assert.Equal(1, code);
        Assert.Contains("Cancelado", output.ToString());
    }

    [Fact]
    public async Task Qualify_rejects_invalid_cost_argument()
    {
        var output = new StringWriter();

        var code = await Run(
            new[] { "model", "qualify", "qwen-test", "--yes", "--max-cost", "abc" }, "", output, TempDir());

        Assert.Equal(1, code);
        Assert.Contains("--max-cost", output.ToString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3601")]
    [InlineData("abc")]
    public async Task Qualify_rejects_invalid_probe_timeout_argument(string value)
    {
        var output = new StringWriter();

        var code = await Run(
            new[] { "model", "qualify", "qwen-test", "--yes", "--probe-timeout", value }, "", output, TempDir());

        Assert.Equal(1, code);
        Assert.Contains("--probe-timeout", output.ToString());
    }

    [Theory]
    [InlineData("1", 2)]
    [InlineData("3", 0)]
    public async Task Qualify_probe_timeout_changes_whether_a_slow_provider_completes(string seconds, int expectedCode)
    {
        var dir = TempDir();
        await using var provider = new ScriptedHttpProvider();
        provider.RespondWith((_, body) =>
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(1200));
            return QuickFixtureResponse(body);
        });

        var priorBaseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", provider.BaseUrl);
            var output = new StringWriter();

            var code = await Run(
                new[] { "model", "qualify", "qwen-test", "--yes", "--probe-timeout", seconds }, "", output, dir);

            Assert.Equal(expectedCode, code);
            if (expectedCode == 0) Assert.Contains("Qualified", output.ToString());
            else Assert.Contains("timeout", output.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", priorBaseUrl);
        }
    }

    [Fact]
    public async Task Qualify_unknown_model_fails_with_localized_error()
    {
        var output = new StringWriter();

        var code = await Run(new[] { "model", "qualify", "missing" }, "", output, TempDir());

        Assert.Equal(2, code);
        Assert.Contains("missing", output.ToString());
    }

    [Fact]
    public async Task Qualify_without_provider_credential_fails_before_any_probe_and_persists_nothing()
    {
        var dir = TempDir();
        await using var provider = new ScriptedHttpProvider();
        provider.RespondWith((_, body) => QuickFixtureResponse(body));
        var paths = OmniHost.CreatePlatformPaths(dir);
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), $$"""
            providers:
              local:
                family: OpenAiChatCompatible
                baseUrl: {{provider.BaseUrl}}
                authRef: local-key
                billingMode: Local
            """);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "models.yaml"), """
            models:
              qwen-test:
                provider: local
                context: 8192
                recommendedUsableContext: 8192
                maxOutput: 2048
            """);

        var priorKey = Environment.GetEnvironmentVariable("OMNI_QWEN_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OMNI_QWEN_KEY", null);
            var output = new StringWriter();

            var code = await OmniCore.Cli.ModelPolicyCommands.Run(new[] { "model", "qualify", "qwen-test", "--yes" },
                new StringReader(""), output, false, dir, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, code);
            var text = output.ToString();
            Assert.Contains("falta la credencial del provider", text);
            Assert.Contains("OMNI_QWEN_KEY", text);
            Assert.DoesNotContain("must contain at least", text);
            Assert.Equal(0, provider.RequestCount);
            using var store = OmniHost.CreateModelQualificationStore(dir);
            Assert.Null(store.Get(QualificationKey(dir), CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNI_QWEN_KEY", priorKey);
        }
    }

    [Fact]
    public async Task Qualify_runs_the_quick_suite_over_the_runtime_provider_and_persists()
    {
        var dir = TempDir();
        await using var provider = new ScriptedHttpProvider();
        provider.RespondWith((_, body) => QuickFixtureResponse(body));

        var priorBaseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL");
        try
        {
            // Mismo mecanismo que omni ask|act: la suite no tiene un provider especial (M5).
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", provider.BaseUrl);
            var output = new StringWriter();

            var code = await Run(new[] { "model", "qualify", "qwen-test", "--yes" }, "", output, dir);

            Assert.Equal(0, code);
            var text = output.ToString();

            // Transición de estado (ADR-0007 §4) y probes.
            Assert.Contains("Declared", text);
            Assert.Contains("Qualified", text);
            Assert.Contains("reading-basic", text);
            Assert.Contains("Passed", text);

            // Traits empíricos guardados.
            Assert.Contains("InstructionFollowing", text);
            Assert.Contains("StructuredOutputReliability", text);

            // Recomendación visible con el hint del flujo explícito (ADR-0044 §9).
            Assert.Contains("PatchOnly", text);
            Assert.Contains("omni model policy set qwen-test --category PatchOnly", text);
            Assert.Contains("no se cambió ninguna política", text);

            // La suite corrió de verdad contra el provider scripteado.
            Assert.Equal(10, provider.RequestCount);
            Assert.All(QuickProbeSuite.Probes(), probe => Assert.Contains(probe.Id.ToString(), text));

            // Persistido en el store de cualificación bajo la clave exacta.
            var key = QualificationKey(dir);
            using var store = OmniHost.CreateModelQualificationStore(dir);
            var stored = store.Get(key, CancellationToken.None);
            Assert.NotNull(stored);
            Assert.Equal(ModelQualificationState.Qualified, stored!.State);
            var traits = store.Traits(key, stored.ProfileRevision, CancellationToken.None);
            Assert.Contains(traits, t => t.Trait == "InstructionFollowing" && t.Value == 1.0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", priorBaseUrl);
        }
    }

    [Theory]
    [InlineData("es", "coste desconocido")]
    [InlineData("en", "cost unavailable")]
    public async Task Qualify_displays_unknown_cost_for_http_response_without_usage(string locale,
        string expectedCostLabel)
    {
        var dir = TempDir();
        await using var provider = new ScriptedHttpProvider();
        provider.RespondWith((_, body) => QuickFixtureResponse(body));

        var priorBaseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL");
        var priorLocale = Environment.GetEnvironmentVariable("OMNI_LOCALE");
        try
        {
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", provider.BaseUrl);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", locale);
            var output = new StringWriter();

            var code = await Run(new[] { "model", "qualify", "qwen-test", "--yes" }, "", output, dir);

            Assert.Equal(0, code);
            var text = output.ToString();
            Assert.Contains("Qualified", text);
            Assert.Contains(expectedCostLabel, text);
            Assert.DoesNotContain("0.0000 USD", text);
            Assert.Equal(10, provider.RequestCount);
            Assert.All(QuickProbeSuite.Probes(), probe => Assert.Contains(probe.Id.ToString(), text));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", priorBaseUrl);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", priorLocale);
        }
    }

    [Fact]
    public async Task Qualify_prints_a_recommendation_but_keeps_the_operational_policy_untouched()
    {
        var dir = TempDir();
        // Política operativa del usuario: ObserveOnly (la más restrictiva).
        var policyOutput = new StringWriter();
        Assert.Equal(0, await Run(
            new[] { "model", "policy", "set", "qwen-test", "--category", "ObserveOnly" }, "", policyOutput, dir));

        await using var provider = new ScriptedHttpProvider();
        provider.RespondWith((_, body) => QuickFixtureResponse(body));

        var priorBaseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", provider.BaseUrl);
            var output = new StringWriter();

            // Cualificar recomienda PatchOnly…
            Assert.Equal(0, await Run(new[] { "model", "qualify", "qwen-test", "--yes" }, "", output, dir));
            Assert.Contains("PatchOnly", output.ToString());
            Assert.Equal(10, provider.RequestCount);

            // …pero la política operativa guardada sigue siendo ObserveOnly rev=1.
            var policyService = OmniHost.CreateModelPolicyService(dir);
            var stored = policyService.Get(ModelPolicyKey.For("local", "qwen-test"), CancellationToken.None);
            Assert.NotNull(stored);
            Assert.Equal(ModelPolicyCategory.ObserveOnly, stored!.Policy.Category);
            Assert.Equal(1, stored.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", priorBaseUrl);
        }
    }

    // ---- Provider HTTP scripteado (mismo patrón que CliEndToEndTests) ----

    private static string QuickFixtureResponse(string body)
    {
        // Parse the request, including escaped punctuation; reject any unexpected fixture prompt.
        using var document = System.Text.Json.JsonDocument.Parse(body);
        var prompt = document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        var probe = Assert.Single(QuickProbeSuite.Probes(), candidate => candidate.Prompt == prompt);
        return TextResponse(probe.Expected);
    }

    private static string TextResponse(string text)
    {
        var eventBody = System.Text.Json.JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content = text }, finish_reason = (string?)null } },
        });
        return string.Join("\n", "data: " + eventBody, "",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}", "",
            "data: [DONE]", "");
    }

    /// <summary>Un servidor HTTP real que responde OpenAI-compatible SSE determinista.</summary>
    private sealed class ScriptedHttpProvider : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        private readonly CancellationTokenSource _stop = new();

        private readonly Task _serve;

        private Func<int, string, string>? _response;

        private int _requestCount;

        public ScriptedHttpProvider()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _serve = ServeAsync();
        }

        public string BaseUrl { get; }

        public int RequestCount => Volatile.Read(ref _requestCount);

        public void RespondWith(Func<int, string, string> response) => _response = response;

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                _ = HandleAsync(client, _stop.Token);
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                var contentLength = 0;
                while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } header)
                {
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(header.AsSpan("Content-Length:".Length).Trim(), out contentLength);
                    }
                }

                var body = new char[contentLength];
                var read = 0;
                while (read < body.Length)
                {
                    var count = await reader.ReadAsync(body.AsMemory(read), cancellationToken);
                    if (count == 0) break;
                    read += count;
                }

                var requestNumber = Interlocked.Increment(ref _requestCount) - 1;
                var request = new string(body, 0, read);
                var eventText = _response?.Invoke(requestNumber, request) ?? TextResponse("respuesta-scripted");
                var bytes = Encoding.UTF8.GetBytes(eventText);
                var response = Encoding.ASCII.GetBytes(string.Join("\r\n", "HTTP/1.1 200 OK",
                    "Content-Type: text/event-stream", "Cache-Control: no-cache", "Connection: close",
                    "Content-Length: " + bytes.Length) + "\r\n\r\n");
                await stream.WriteAsync(response, cancellationToken);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _serve; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }
}
