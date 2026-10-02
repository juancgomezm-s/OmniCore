namespace OmniCore.Tests;

using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;
using Task = System.Threading.Tasks.Task;

/// <summary>
/// Bug 7 del gap pre-M6 (§4): el modo <c>routing.escalation.mode: auto</c> solo aprueba y ejecuta
/// un candidato cuando su provider está autorizado (auth none declarada o credencial existente)
/// y su precio es completo (entrada y salida). Los casos no elegibles degradan a sugerencia sin
/// emitir <c>ModelEscalationApproved</c>/<c>ModelEscalationCompleted</c> ni invocar al modelo escalado.
/// <c>ask</c> y <c>deny</c> conservan su contrato y en <c>act</c> se mantiene el filtro de política
/// de escritura. Sin llamadas de reales: el provider de la nube es un servidor scripted en loopback.
/// </summary>
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class EscalationAutoApprovalTests
{
    // ---------------------------------------------------------------
    // Guard puro de ModelRoutingHost (configuración + delegates, sin red ni journal)
    // ---------------------------------------------------------------

    private const string GuardProviders = """
providers:
  local: { baseUrl: http://127.0.0.1:8080, auth: none }
  cloud: { family: AnthropicMessages, baseUrl: https://api.example.test, authRef: cloud-key }
""";

    private static LoadedUserConfiguration LoadGuardConfig(string routing) => new ConfigLoader().Load(GuardProviders,
        """
models:
  qwen-27b: { provider: local, context: 32768, aliases: [worker], inputPricePerMillionUsd: 0, outputPricePerMillionUsd: 0 }
  claude-x: { provider: cloud, context: 200000, aliases: [frontier], inputPricePerMillionUsd: 3, outputPricePerMillionUsd: 15 }
  partial-x: { provider: cloud, context: 200000, aliases: [partial], inputPricePerMillionUsd: 3 }
  unpriced-x: { provider: local, context: 200000, aliases: [unpriced] }
"""
        + Environment.NewLine + routing);

    private static RouteCandidate GuardCandidate(LoadedUserConfiguration loaded, string alias) =>
        ModelRoutingHost.Candidates(loaded, _ => true).Single(candidate => candidate.Alias == alias);

    [Fact]
    public void Auto_approval_requires_credential_and_complete_price_of_the_candidate()
    {
        var loaded = LoadGuardConfig("routing:\n  escalation: { mode: auto, chain: [frontier] }\n");
        var frontier = GuardCandidate(loaded, "claude-x");

        Assert.True(ModelRoutingHost.CanAutoApprove(loaded, frontier, _ => true, out var eligible));
        Assert.Null(eligible);
        Assert.False(ModelRoutingHost.CanAutoApprove(loaded, frontier, _ => false, out var noCredential));
        Assert.Equal(ModelRoutingHost.AutoEscalationRejection.UnauthorizedProvider, noCredential);
    }

    [Fact]
    public void Partial_or_unknown_pricing_blocks_auto_approval_even_when_authorized()
    {
        var loaded = LoadGuardConfig("routing:\n  escalation: { mode: auto, chain: [partial, unpriced] }\n");

        Assert.False(ModelRoutingHost.CanAutoApprove(loaded, GuardCandidate(loaded, "partial-x"), _ => true,
            out var partial));
        Assert.Equal(ModelRoutingHost.AutoEscalationRejection.UnknownPricing, partial);
        Assert.False(ModelRoutingHost.CanAutoApprove(loaded, GuardCandidate(loaded, "unpriced-x"), _ => true,
            out var unknown));
        Assert.Equal(ModelRoutingHost.AutoEscalationRejection.UnknownPricing, unknown);
    }

    [Fact]
    public void Declared_auth_none_is_authorized_without_credential_but_price_must_still_be_complete()
    {
        var loaded = LoadGuardConfig("routing:\n  escalation: { mode: auto, chain: [worker, unpriced] }\n");

        Assert.True(ModelRoutingHost.CanAutoApprove(loaded, GuardCandidate(loaded, "qwen-27b"), _ => false,
            out var worker));
        Assert.Null(worker);
        Assert.False(ModelRoutingHost.CanAutoApprove(loaded, GuardCandidate(loaded, "unpriced-x"), _ => false,
            out var unpriced));
        Assert.Equal(ModelRoutingHost.AutoEscalationRejection.UnknownPricing, unpriced);
    }

    [Fact]
    public void Unverifiable_auth_modes_and_missing_descriptors_are_never_authorized()
    {
        var registry = new ModelRegistry()
            .Add(new ProviderDescriptor("bearer", ProviderFamily.OpenAiChatCompatible, "https://api.example.test",
                new AuthConfig(AuthKind.Bearer, "bearer-key"), true, true, true))
            .Add(new ProviderDescriptor("keyless", ProviderFamily.OpenAiChatCompatible, "https://api.example.test",
                new AuthConfig(AuthKind.ApiKey, null), true, true, true))
            .AddModel(new ModelDefinition("bearer-x", "bearer", 200000, 200000, 2048))
            .AddModel(new ModelDefinition("keyless-x", "keyless", 200000, 200000, 2048));
        var loaded = new LoadedUserConfiguration(registry, null, null);
        var candidates = ModelRoutingHost.Candidates(loaded, _ => true);
        var ghost = new RouteCandidate("ghost", candidates[0].Profile, false, true, false, 1m);

        Assert.False(ModelRoutingHost.CanAutoApprove(loaded, candidates.Single(c => c.Alias == "bearer-x"),
            _ => true, out var bearer));
        Assert.Equal(ModelRoutingHost.AutoEscalationRejection.UnauthorizedProvider, bearer);
        Assert.False(ModelRoutingHost.CanAutoApprove(loaded, candidates.Single(c => c.Alias == "keyless-x"),
            _ => true, out var keyless));
        Assert.Equal(ModelRoutingHost.AutoEscalationRejection.UnauthorizedProvider, keyless);
        Assert.False(ModelRoutingHost.CanAutoApprove(loaded, ghost, _ => true, out var noDescriptor));
        Assert.Equal(ModelRoutingHost.AutoEscalationRejection.UnauthorizedProvider, noDescriptor);
    }

    [Fact]
    public void Ask_and_deny_keep_their_contract_and_act_keeps_the_write_policy_filter()
    {
        var ask = LoadGuardConfig("routing:\n  escalation: { chain: [frontier] }\n");
        var deny = LoadGuardConfig("routing:\n  escalation: { mode: deny, chain: [frontier] }\n");
        var act = LoadGuardConfig("routing:\n  escalation: { mode: auto, chain: [frontier] }\n");

        Assert.Equal("ask", ModelRoutingHost.EscalationMode(ask));
        Assert.Equal("deny", ModelRoutingHost.EscalationMode(deny));
        // deny no ofrece candidato; ask sigue sugiriendo el siguiente de la cadena aunque el
        // guard de auto no lo apruebe después.
        Assert.Null(ModelRoutingHost.NextEscalation(deny, "qwen-27b", false, 40_000, _ => true));
        Assert.Equal("claude-x", ModelRoutingHost.NextEscalation(ask, "qwen-27b", false, 40_000, _ => true)!.Alias);
        // act (requiresWrite): sin política de escritura el candidato no se selecciona.
        Assert.Null(ModelRoutingHost.NextEscalation(act, "qwen-27b", true, 40_000, _ => false));
        Assert.Equal("claude-x", ModelRoutingHost.NextEscalation(act, "qwen-27b", true, 40_000, _ => true)!.Alias);
    }

    // ---------------------------------------------------------------
    // Flujo real del runtime: TryEscalateAsync ante ContextOverflow
    // ---------------------------------------------------------------

    private sealed class FlowResult(int code, string output, IReadOnlyList<DomainEventPayload> events)
    {
        public int Code { get; } = code;

        public string Output { get; } = output;

        public IReadOnlyList<DomainEventPayload> Events { get; } = events;
    }

    private static async Task<FlowResult> RunTurnWithEscalationAsync(string providersYaml, string modelsYaml,
        bool act, Action<string>? seed = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-escalation-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var data = Path.Combine(root, "data");
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "providers.yaml"), providersYaml);
        File.WriteAllText(Path.Combine(config, "models.yaml"), modelsYaml);

        var previousDirectory = Environment.CurrentDirectory;
        var previousData = Environment.GetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable);
        var previousConfig = Environment.GetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable);
        var previousLocale = Environment.GetEnvironmentVariable("OMNI_LOCALE");
        var previousModel = Environment.GetEnvironmentVariable("OMNI_MODEL");
        var previousBaseUrl = Environment.GetEnvironmentVariable("OMNI_BASE_URL");
        var previousKey = Environment.GetEnvironmentVariable("OMNI_QWEN_KEY");
        try
        {
            Environment.CurrentDirectory = workspace;
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, data);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, config);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", "es");
            Environment.SetEnvironmentVariable("OMNI_MODEL", null);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", null);
            Environment.SetEnvironmentVariable("OMNI_QWEN_KEY", null);
            seed?.Invoke(data);

            var runtime = OmniCliRuntime.Create(workspace);
            runtime.Localize = (key, args) => Localization.Spanish().Resolve(key, args);
            var lines = new List<string>();
            var code = act
                ? await runtime.ActAsync("trabaja en el objetivo", lines.Add, CancellationToken.None)
                : await runtime.AskAsync("pregunta larga sobre el estado del workspace", lines.Add,
                    CancellationToken.None);
            return new FlowResult(code, string.Join(Environment.NewLine, lines), ReadAllEvents(workspace));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, previousData);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, previousConfig);
            Environment.SetEnvironmentVariable("OMNI_LOCALE", previousLocale);
            Environment.SetEnvironmentVariable("OMNI_MODEL", previousModel);
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", previousBaseUrl);
            Environment.SetEnvironmentVariable("OMNI_QWEN_KEY", previousKey);
            Environment.CurrentDirectory = previousDirectory;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static IReadOnlyList<DomainEventPayload> ReadAllEvents(string workspace)
    {
        var workspaceData = OmniHost.WorkspaceDataDirectory(OmniHost.CreatePlatformPaths(), workspace);
        var journal = Path.Combine(workspaceData, "journal.db");
        if (!File.Exists(journal)) return [];
        var sessions = new List<SessionId>();
        using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = journal }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT session_id FROM events";
            using var reader = command.ExecuteReader();
            while (reader.Read()) sessions.Add(SessionId.Parse(reader.GetString(0)));
        }
        var store = new SqliteEventStore(journal);
        try
        {
            return sessions.SelectMany(session => store.ReadFrom(session, 1))
                .Select(EventCodecs.Create().Decode).ToArray();
        }
        finally { store.Close(); }
    }

    private static string Providers(string cloudBaseUrl, string cloudAuth) => "providers:\n"
        + "  local: { baseUrl: http://127.0.0.1:8080, auth: none }\n"
        + "  cloud: { baseUrl: " + cloudBaseUrl + ", " + cloudAuth + " }\n";

    private static string Models(string escalation, string cloudPrices) => "models:\n"
        + "  small: { provider: local, context: 16 }\n"
        + "  big: { provider: cloud, context: 200000" + cloudPrices + " }\n"
        + "routing:\n"
        + "  escalation: { " + escalation + ", chain: [big] }\n";

    private static void AssertNoAutoEscalation(FlowResult result, ScriptedHttpProvider provider, string because)
    {
        Assert.True(provider.RequestCount == 0, because + ": el modelo escalado no debe invocarse, se invocó "
            + provider.RequestCount + " vez/veces");
        Assert.DoesNotContain(result.Events, evt => evt is ModelEscalationApproved);
        Assert.DoesNotContain(result.Events, evt => evt is ModelEscalationCompleted);
    }

    [Fact]
    public async Task Auto_escalation_approves_and_runs_an_authorized_candidate_with_complete_price()
    {
        await using var provider = new ScriptedHttpProvider();
        provider.RespondWith((_, _) => TextResponse("respuesta-big"));
        var result = await RunTurnWithEscalationAsync(
            Providers(provider.BaseUrl, "authRef: cloud-key"),
            Models("mode: auto", ", inputPricePerMillionUsd: 3, outputPricePerMillionUsd: 15"),
            act: false,
            seed: _ => OmniHost.CreateUserCredentialStore(OmniHost.CreatePlatformPaths())
                .Save("cloud-key", "test-cloud-key-123", CancellationToken.None));

        Assert.Equal(0, result.Code);
        Assert.Contains("escalando a big", result.Output);
        Assert.Contains("respuesta-big", result.Output);
        Assert.True(provider.RequestCount >= 1, "el candidato autorizado y con precio completo debe ejecutarse");
        Assert.Contains(result.Events, evt => evt is ModelEscalationRequested { Cause: EscalationCause.ContextLimit });
        Assert.Contains(result.Events, evt => evt is ModelEscalationApproved { ToModel: "big", ApprovedBy: "policy:auto" });
        Assert.Contains(result.Events, evt => evt is ModelEscalationCompleted { ToModel: "big" });
    }

    [Fact]
    public async Task Auto_escalation_without_credential_degrades_to_suggestion_and_never_invokes_the_candidate()
    {
        await using var provider = new ScriptedHttpProvider();
        var result = await RunTurnWithEscalationAsync(
            Providers(provider.BaseUrl, "authRef: cloud-key"),
            Models("mode: auto", ", inputPricePerMillionUsd: 3, outputPricePerMillionUsd: 15"),
            act: false);

        Assert.Equal(0, result.Code);
        Assert.Contains("no fue aprobada", result.Output);
        Assert.Contains("el provider no está autorizado", result.Output);
        Assert.DoesNotContain("escalando a big", result.Output);
        Assert.Contains(result.Events, evt => evt is ModelEscalationRequested);
        AssertNoAutoEscalation(result, provider, "sin credencial del provider");
    }

    [Fact]
    public async Task Auto_escalation_with_incomplete_price_degrades_to_suggestion_and_never_invokes_the_candidate()
    {
        await using var provider = new ScriptedHttpProvider();
        var result = await RunTurnWithEscalationAsync(
            Providers(provider.BaseUrl, "auth: none"),
            Models("mode: auto", ", inputPricePerMillionUsd: 3"),
            act: false);

        Assert.Equal(0, result.Code);
        Assert.Contains("no fue aprobada", result.Output);
        Assert.Contains("el precio del modelo no es completo", result.Output);
        Assert.DoesNotContain("escalando a big", result.Output);
        Assert.Contains(result.Events, evt => evt is ModelEscalationRequested);
        AssertNoAutoEscalation(result, provider, "con precio incompleto");
    }

    [Fact]
    public async Task Auto_escalation_with_declared_auth_none_and_complete_price_runs_the_candidate()
    {
        await using var provider = new ScriptedHttpProvider();
        provider.RespondWith((_, _) => TextResponse("respuesta-big"));
        var result = await RunTurnWithEscalationAsync(
            Providers(provider.BaseUrl, "auth: none"),
            Models("mode: auto", ", inputPricePerMillionUsd: 3, outputPricePerMillionUsd: 15"),
            act: false);

        Assert.Equal(0, result.Code);
        Assert.Contains("escalando a big", result.Output);
        Assert.Contains("respuesta-big", result.Output);
        Assert.True(provider.RequestCount >= 1, "auth none declarada con precio completo es elegible");
        Assert.Contains(result.Events, evt => evt is ModelEscalationApproved { ToModel: "big" });
        Assert.Contains(result.Events, evt => evt is ModelEscalationCompleted { ToModel: "big" });
    }

    [Fact]
    public async Task Ask_mode_still_suggests_without_approving_or_invoking_the_candidate()
    {
        await using var provider = new ScriptedHttpProvider();
        provider.RespondWith((_, _) => TextResponse("respuesta-big"));
        var result = await RunTurnWithEscalationAsync(
            Providers(provider.BaseUrl, "auth: none"),
            Models("mode: ask", ", inputPricePerMillionUsd: 3, outputPricePerMillionUsd: 15"),
            act: false);

        Assert.Equal(0, result.Code);
        Assert.Contains("Escalación propuesta a big", result.Output);
        Assert.DoesNotContain("escalando a big", result.Output);
        Assert.Contains(result.Events, evt => evt is ModelEscalationRequested);
        AssertNoAutoEscalation(result, provider, "en modo ask la aprobación es del usuario");
    }

    [Fact]
    public async Task Deny_mode_neither_suggests_nor_journals_any_escalation()
    {
        await using var provider = new ScriptedHttpProvider();
        provider.RespondWith((_, _) => TextResponse("respuesta-big"));
        var result = await RunTurnWithEscalationAsync(
            Providers(provider.BaseUrl, "auth: none"),
            Models("mode: deny", ", inputPricePerMillionUsd: 3, outputPricePerMillionUsd: 15"),
            act: false);

        Assert.Equal(0, result.Code);
        Assert.DoesNotContain("Escalación propuesta", result.Output);
        Assert.DoesNotContain("escalando a big", result.Output);
        Assert.DoesNotContain(result.Events, evt => evt is ModelEscalationRequested);
        AssertNoAutoEscalation(result, provider, "en modo deny");
    }

    [Fact]
    public async Task Act_without_write_policy_does_not_execute_the_escalated_candidate()
    {
        await using var provider = new ScriptedHttpProvider();
        provider.RespondWith((_, _) => TextResponse("respuesta-big"));
        var result = await RunTurnWithEscalationAsync(
            Providers(provider.BaseUrl, "auth: none"),
            Models("mode: auto", ", inputPricePerMillionUsd: 3, outputPricePerMillionUsd: 15"),
            act: true);

        Assert.Equal(1, result.Code);
        Assert.DoesNotContain("escalando a big", result.Output);
        Assert.DoesNotContain(result.Events, evt => evt is ModelEscalationRequested);
        AssertNoAutoEscalation(result, provider, "en act sin política de escritura del candidato");
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

    /// <summary>Servidor HTTP real en loopback con respuestas OpenAI-compatible deterministas.</summary>
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
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(header.AsSpan("Content-Length:".Length).Trim(), out contentLength);
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
                var eventText = _response?.Invoke(requestNumber, request) ?? TextResponse("respuesta-big");
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
