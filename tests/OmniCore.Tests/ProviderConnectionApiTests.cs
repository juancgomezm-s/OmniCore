namespace OmniCore.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Client;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;

/// <summary>
/// Backend de conexión por API key de Anthropic (ADR-0011 §3, ADR-0018, ADR-0039 §4): store
/// protegido real con directorios temporales + handlers HTTP guionados. Sin credenciales de
/// cuenta, sin llamadas externas autenticadas y sin invocación de inferencia: la Models API se
/// usa solo como contrato de verificación (GET /v1/models).
/// </summary>
public sealed class ProviderConnectionApiTests
{
    private const string ApiKey = "sk-ant-test-0123456789abcdef";
    private const string OtherKey = "sk-or-test-9999zzzz";

    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- fixtures

    private sealed record RecordedRequest(string Method, string Url, string? ApiKeyHeader,
        string? ApiVersionHeader, string? UserAgentHeader)
    {
        public static RecordedRequest From(HttpRequestMessage request) => new(
            request.Method.Method,
            request.RequestUri!.ToString(),
            request.Headers.TryGetValues("x-api-key", out var key) ? string.Join(",", key) : null,
            request.Headers.TryGetValues("anthropic-version", out var version) ? string.Join(",", version) : null,
            request.Headers.TryGetValues("User-Agent", out var agent) ? string.Join(",", agent) : null);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
        private readonly ConcurrentQueue<Exception> _errors = new();

        public List<RecordedRequest> Requests { get; } = [];

        public ScriptedHandler Respond(Func<HttpRequestMessage, HttpResponseMessage> factory)
        {
            _responses.Enqueue(factory);
            return this;
        }

        public ScriptedHandler Fail(Exception error)
        {
            _errors.Enqueue(error);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(RecordedRequest.From(request));
            if (_errors.TryDequeue(out var error)) throw error;
            if (!_responses.TryDequeue(out var next))
                throw new InvalidOperationException("No scripted response remaining");
            return System.Threading.Tasks.Task.FromResult(next(request));
        }
    }

    private sealed class Harness
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "omnicore-pc-" + Guid.NewGuid().ToString("N"));
        public FileCredentialStore Store { get; }
        public ProviderConnectionService Service { get; }
        public ScriptedHandler Handler { get; } = new();
        public DefaultPlatformPaths Paths { get; }
        public string Config => Paths.ConfigDirectory;

        public Harness()
        {
            Paths = new DefaultPlatformPaths(Root, Path.Combine(Root, "config"));
            Store = new FileCredentialStore(Path.Combine(Root, "credentials.ini"));
            var handler = Handler;
            Service = new ProviderConnectionService(Store, Paths,
                httpFactory: () => new HttpClient(handler, disposeHandler: false),
                now: () => FixedNow);
        }

        public string ReadConfigFile(string name)
        {
            var path = Path.Combine(Config, name);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        public void WriteConfigFile(string name, string content)
        {
            Directory.CreateDirectory(Config);
            File.WriteAllText(Path.Combine(Config, name), content);
        }

        public ProviderConnectionStatus Row(string providerId, CancellationToken ct)
        {
            var row = Service.List(ct).SingleOrDefault(r => r.ProviderId == providerId);
            Assert.NotNull(row);
            return row;
        }
    }

    /// <summary>Página del contrato documentado de la Models API (GET /v1/models).</summary>
    private static HttpResponseMessage ModelsPage(string dataJson, bool hasMore = false,
        string? lastId = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var tail = hasMore
            ? "],\"first_id\":\"first\",\"last_id\":\"" + lastId + "\",\"has_more\":true}"
            : "],\"has_more\":false}";
        return new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"data\":[" + dataJson + tail, Encoding.UTF8, "application/json"),
        };
    }

    private static string ModelJson(string id, string displayName = "Model") =>
        "{\"id\":\"" + id + "\",\"type\":\"model\",\"display_name\":\"" + displayName +
        "\",\"created_at\":\"2025-09-29T00:00:00Z\"}";

    private static string MinimalSse() => """
event: message_start
data: {"type":"message_start","message":{"usage":{"input_tokens":3,"output_tokens":0}}}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}

event: message_stop
data: {"type":"message_stop"}

""";

    private static async System.Threading.Tasks.Task<List<ModelStreamEvent>> Collect(IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var result = new List<ModelStreamEvent>();
        await foreach (var item in stream) result.Add(item);
        return result;
    }

    // ---------------------------------------------------------------- status

    [Fact]
    public void Before_any_configuration_the_anthropic_row_is_not_configured_with_connect_only()
    {
        var harness = new Harness();
        var row = harness.Row(ProviderConnectionService.DefaultAnthropicProviderId,
            TestContext.Current.CancellationToken);
        Assert.Equal(ProviderConnectionState.NotConfigured, row.State);
        Assert.Equal(ProviderConnectionMethod.ApiKey, row.Method);
        Assert.True(row.CanConnect);
        Assert.False(row.CanTest);
        Assert.False(row.CanDisconnect);
        Assert.False(row.CanDiscoverModels);
    }

    [Fact]
    public async System.Threading.Tasks.Task Status_distinguishes_unverified_invalid_unreachable_and_connected_from_metadata()
    {
        var harness = new Harness();
        var ct = TestContext.Current.CancellationToken;

        // Sin verificar por red: Unknown, jamás Connected por tener la key presente.
        await harness.Service.ConnectAnthropicAsync(ApiKey, validate: false, ct);
        var unverified = harness.Row("anthropic", ct);
        Assert.Equal(ProviderConnectionState.Unknown, unverified.State);
        Assert.NotNull(unverified.Notice);

        harness.Handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var invalid = await harness.Service.TestAsync("anthropic", ct);
        Assert.Equal(ProviderConnectionState.Invalid, invalid.State);
        Assert.Equal(ProviderConnectionState.Invalid, harness.Row("anthropic", ct).State);

        harness.Handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var unreachable = await harness.Service.TestAsync("anthropic", ct);
        Assert.Equal(ProviderConnectionState.Unreachable, unreachable.State);
        Assert.Equal(ProviderConnectionState.Unreachable, harness.Row("anthropic", ct).State);

        harness.Handler.Respond(_ => ModelsPage(ModelJson("claude-x")));
        var valid = await harness.Service.TestAsync("anthropic", ct);
        var row = harness.Row("anthropic", ct);
        Assert.Equal(ProviderConnectionState.Connected, valid.State);
        Assert.Equal(ProviderConnectionState.Connected, row.State);
        Assert.Null(row.Notice);
        Assert.Equal("****cdef", row.Detail);
    }

    // ---------------------------------------------------------------- connect

    [Fact]
    public async System.Threading.Tasks.Task Connect_with_validation_2xx_saves_key_registers_provider_and_never_leaks_secret()
    {
        var harness = new Harness();
        var ct = TestContext.Current.CancellationToken;
        harness.Handler.Respond(_ => ModelsPage(ModelJson("claude-x")));
        var result = await harness.Service.ConnectAnthropicAsync(ApiKey, validate: true, ct);

        Assert.Equal(ProviderConnectionState.Connected, result.State);
        Assert.Equal(ApiKey, harness.Store.Load(ProviderConnectionService.AnthropicAuthRef, ct));
        var providers = harness.ReadConfigFile("providers.yaml");
        Assert.Contains("AnthropicMessages", providers, StringComparison.Ordinal);
        Assert.Contains("authRef: anthropic", providers, StringComparison.Ordinal);
        Assert.Contains("https://api.anthropic.com", providers, StringComparison.Ordinal);
        Assert.Equal("valid", ProviderConnectionMetadataStore.Read(harness.Root, "anthropic", ct).ValidationState);
        Assert.Equal("****cdef", result.Detail);

        // Ningún archivo en disco contiene la key en claro.
        foreach (var file in Directory.EnumerateFiles(harness.Root, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(ApiKey, File.ReadAllText(file), StringComparison.Ordinal);
        }

        // La petición lleva la credencial solo al endpoint declarado.
        var request = Assert.Single(harness.Handler.Requests);
        Assert.Equal("https://api.anthropic.com/v1/models?limit=1", request.Url);
        Assert.Equal(ApiKey, request.ApiKeyHeader);
        Assert.Equal("2023-06-01", request.ApiVersionHeader);
        Assert.StartsWith("omnicore/", request.UserAgentHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("claude-cli", request.UserAgentHeader, StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Connect_with_rejected_401_saves_nothing_and_masks_the_error()
    {
        var harness = new Harness();
        var ct = TestContext.Current.CancellationToken;
        harness.Handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var exception = await Assert.ThrowsAsync<ProviderConnectionException>(() =>
            harness.Service.ConnectAnthropicAsync(ApiKey, validate: true, ct));
        Assert.Equal("invalid", exception.Kind);
        Assert.DoesNotContain(ApiKey, exception.Message, StringComparison.Ordinal);
        Assert.Null(harness.Store.Load(ProviderConnectionService.AnthropicAuthRef, ct));
        Assert.Null(ProviderConnectionMetadataStore.Read(harness.Root, "anthropic", ct).ValidationState);
        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    public async System.Threading.Tasks.Task Empty_or_too_short_key_is_typed_and_never_saves()
    {
        var harness = new Harness();
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal("empty", (await Assert.ThrowsAsync<ProviderConnectionException>(() =>
            harness.Service.ConnectAnthropicAsync("   ", validate: false, ct))).Kind);
        Assert.Equal("tooShort", (await Assert.ThrowsAsync<ProviderConnectionException>(() =>
            harness.Service.ConnectAnthropicAsync("sk", validate: false, ct))).Kind);
        Assert.Null(harness.Store.Load(ProviderConnectionService.AnthropicAuthRef, ct));
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async System.Threading.Tasks.Task Replacing_the_key_updates_the_store_and_leaves_no_trace_of_the_old_one()
    {
        var harness = new Harness();
        var ct = TestContext.Current.CancellationToken;
        await harness.Service.ConnectAnthropicAsync(ApiKey, validate: false, ct);
        var replaced = await harness.Service.ConnectAnthropicAsync("sk-ant-new-9999abcdefgh", validate: false, ct);

        Assert.Equal("****efgh", replaced.Detail);
        Assert.Equal("sk-ant-new-9999abcdefgh", harness.Store.Load(ProviderConnectionService.AnthropicAuthRef, ct));
        foreach (var file in Directory.EnumerateFiles(harness.Root, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(ApiKey, File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Invalid_endpoint_is_rejected_before_any_request()
    {
        var harness = new Harness();
        var ct = TestContext.Current.CancellationToken;
        await harness.Service.ConnectAnthropicAsync(ApiKey, validate: false, ct);
        harness.WriteConfigFile("providers.yaml", """
providers:
  anthropic:
    family: AnthropicMessages
    baseUrl: https://user:secret@example.test
    authRef: anthropic
""");
        var exception = await Assert.ThrowsAsync<ProviderConnectionException>(() =>
            harness.Service.TestAsync("anthropic", ct));
        Assert.Equal("invalidEndpoint", exception.Kind);
        Assert.Empty(harness.Handler.Requests);
    }

    // ---------------------------------------------------------------- test connection

    [Fact]
    public async System.Threading.Tasks.Task Test_connection_with_stored_key_reports_429_and_network_without_claiming_valid()
    {
        var harness = new Harness();
        var ct = TestContext.Current.CancellationToken;
        await harness.Service.ConnectAnthropicAsync(ApiKey, validate: false, ct);

        harness.Handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var rateLimited = await harness.Service.TestAsync("anthropic", ct);
        Assert.Equal(ProviderConnectionState.Unknown, rateLimited.State);
        Assert.Null(ProviderConnectionMetadataStore.Read(harness.Root, "anthropic", ct).ValidationState);

        harness.Handler.Fail(new HttpRequestException("boom"));
        var unreachable = await harness.Service.TestAsync("anthropic", ct);
        Assert.Equal(ProviderConnectionState.Unreachable, unreachable.State);
        Assert.Equal("unreachable",
            ProviderConnectionMetadataStore.Read(harness.Root, "anthropic", ct).ValidationState);
        Assert.DoesNotContain(ApiKey, unreachable.Detail ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Test_connection_without_credential_is_typed_not_configured()
    {
        var harness = new Harness();
        var exception = await Assert.ThrowsAsync<ProviderConnectionException>(() =>
            harness.Service.TestAsync("anthropic", TestContext.Current.CancellationToken));
        Assert.Equal("notConfigured", exception.Kind);
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async System.Threading.Tasks.Task Cancellation_propagates_and_changes_nothing()
    {
        var harness = new Harness();
        var ct = TestContext.Current.CancellationToken;
        await harness.Service.ConnectAnthropicAsync(ApiKey, validate: false, ct);
        var before = ProviderConnectionMetadataStore.Read(harness.Root, "anthropic", ct);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Service.TestAsync("anthropic", cancelled.Token));

        var after = ProviderConnectionMetadataStore.Read(harness.Root, "anthropic", ct);
        Assert.Equal(before.ValidationState, after.ValidationState);
    }

    // ---------------------------------------------------------------- discovery

    [Fact]
    public async System.Threading.Tasks.Task Discovery_paginates_with_after_id_and_registers_only_reported_facts()
    {
        var harness = new Harness();
        var ct = TestContext.Current.CancellationToken;
        await harness.Service.ConnectAnthropicAsync(ApiKey, validate: false, ct);

        var first = ModelsPage(ModelJson("claude-a", "A") + "," + ModelJson("claude-b", "B"),
            hasMore: true, lastId: "claude-a");
        first.Headers.TryAddWithoutValidation("anthropic-ratelimit-requests-remaining", "99");
        harness.Handler.Respond(_ => first)
            .Respond(_ => ModelsPage(ModelJson("claude-c", "C"), hasMore: false));

        var result = await harness.Service.DiscoverAnthropicModelsAsync(null, ct);

        Assert.False(result.Truncated);
        Assert.Equal(3, result.RegisteredModels);
        Assert.Equal(2, harness.Handler.Requests.Count);
        Assert.Equal(["https://api.anthropic.com/v1/models?limit=1000",
            "https://api.anthropic.com/v1/models?limit=1000&after_id=claude-a"],
            harness.Handler.Requests.Select(r => r.Url).ToArray());
        Assert.All(harness.Handler.Requests, r => Assert.Equal(ApiKey, r.ApiKeyHeader));

        var quota = Assert.Single(result.Quota);
        Assert.Equal(99, quota.Remaining);
        Assert.Equal(RateLimitWindowKind.Requests, quota.Kind);

        var loaded = OmniHost.LoadUserConfiguration(harness.Paths);
        foreach (var modelId in (ReadOnlySpan<string>)["claude-a", "claude-b", "claude-c"])
        {
            var model = loaded.Registry.Model(modelId);
            Assert.NotNull(model);
            Assert.Equal("anthropic", model!.ProviderId);
        }

        var modelsYaml = harness.ReadConfigFile("models.yaml");
        Assert.Contains("claude-a:", modelsYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("context:", modelsYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("maxOutput:", modelsYaml, StringComparison.Ordinal);
        Assert.NotEqual(default, ProviderConnectionMetadataStore.Read(harness.Root, "anthropic", ct).DiscoveredAt);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discovery_preserves_unknown_limits_and_capabilities_as_unknown()
    {
        var harness = new Harness();
        var ct = TestContext.Current.CancellationToken;
        await harness.Service.ConnectAnthropicAsync(ApiKey, validate: false, ct);
        harness.Handler.Respond(_ => ModelsPage(ModelJson("claude-x")));

        var result = await harness.Service.DiscoverAnthropicModelsAsync(null, ct);

        Assert.Empty(result.Quota); // sin cabeceras de rate limit: nada inventado.
        var loaded = OmniHost.LoadUserConfiguration(harness.Paths);
        var definition = loaded.Registry.Model("claude-x");
        Assert.NotNull(definition);
        // Lo no informado queda desconocido (null), no false ni true.
        Assert.Null(definition!.ReasoningCapability.Supported);
        var modelsYaml = harness.ReadConfigFile("models.yaml");
        Assert.Contains("provider: anthropic", modelsYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning:", modelsYaml, StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discovery_caps_pages_and_marks_truncated()
    {
        var harness = new Harness();
        var ct = TestContext.Current.CancellationToken;
        await harness.Service.ConnectAnthropicAsync(ApiKey, validate: false, ct);
        for (var page = 0; page < AnthropicModelCatalog.MaxPages + 3; page++)
        {
            var cursor = "m" + page;
            harness.Handler.Respond(_ => ModelsPage(ModelJson("model-" + page), hasMore: true, lastId: cursor));
        }

        var result = await harness.Service.DiscoverAnthropicModelsAsync(null, ct);
        Assert.True(result.Truncated);
        Assert.Equal(AnthropicModelCatalog.MaxPages, harness.Handler.Requests.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task Discovery_rejects_out_of_contract_payloads_without_touching_config()
    {
        var harness = new Harness();
        var ct = TestContext.Current.CancellationToken;
        await harness.Service.ConnectAnthropicAsync(ApiKey, validate: false, ct);
        var before = harness.ReadConfigFile("models.yaml");
        harness.Handler.Respond(_ => ModelsPage(@"{""id"":""bad id!"",""type"":""model""}"));

        var exception = await Assert.ThrowsAsync<ProviderConnectionException>(() =>
            harness.Service.DiscoverAnthropicModelsAsync(null, ct));
        Assert.Equal("catalogInvalid", exception.Kind);
        Assert.Equal(before, harness.ReadConfigFile("models.yaml"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Discovery_without_credential_is_typed()
    {
        var harness = new Harness();
        var exception = await Assert.ThrowsAsync<ProviderConnectionException>(() =>
            harness.Service.DiscoverAnthropicModelsAsync(null, TestContext.Current.CancellationToken));
        Assert.Equal("notConfigured", exception.Kind);
        Assert.Empty(harness.Handler.Requests);
    }

    // ---------------------------------------------------------------- redirects

    [Fact]
    public async System.Threading.Tasks.Task Redirects_are_never_followed_so_the_credential_leaks_nowhere()
    {
        var harness = new Harness();
        var ct = TestContext.Current.CancellationToken;
        await harness.Service.ConnectAnthropicAsync(ApiKey, validate: false, ct);

        harness.Handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("http://other-host.example.test/steal") },
        });
        var result = await harness.Service.TestAsync("anthropic", ct);
        Assert.Equal(ProviderConnectionState.Unknown, result.State);
        Assert.Equal("providers.notice.redirectNotFollowed", result.Notice!.Render());
        Assert.Single(harness.Handler.Requests); // nunca se siguió la redirección.

        harness.Handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.Found));
        var exception = await Assert.ThrowsAsync<ProviderConnectionException>(() =>
            harness.Service.DiscoverAnthropicModelsAsync(null, ct));
        Assert.Equal("unreachable", exception.Kind);
        Assert.Equal(2, harness.Handler.Requests.Count);
    }

    // ---------------------------------------------------------------- disconnect

    [Fact]
    public async System.Threading.Tasks.Task Disconnect_removes_only_that_credential_and_preserves_the_rest()
    {
        var harness = new Harness();
        var ct = TestContext.Current.CancellationToken;
        harness.Store.Save("openrouter", OtherKey, ct);
        await harness.Service.ConnectAnthropicAsync(ApiKey, validate: false, ct);

        harness.Service.Disconnect("anthropic", ct);

        Assert.Null(harness.Store.Load(ProviderConnectionService.AnthropicAuthRef, ct));
        Assert.Equal(OtherKey, harness.Store.Load("openrouter", ct));
        Assert.Equal(ProviderConnectionState.NotConfigured, harness.Row("anthropic", ct).State);
        var file = Path.Combine(harness.Root, "provider-connections.json");
        Assert.False(File.Exists(file) && File.ReadAllText(file).Contains("\"anthropic\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Disconnect_of_unknown_provider_is_typed()
    {
        var harness = new Harness();
        var exception = Assert.Throws<ProviderConnectionException>(() =>
            harness.Service.Disconnect("nope", TestContext.Current.CancellationToken));
        Assert.Equal("unknownProvider", exception.Kind);
    }

    // ---------------------------------------------------------------- config preservation

    [Fact]
    public async System.Threading.Tasks.Task Registration_preserves_existing_user_providers_and_models()
    {
        var harness = new Harness();
        var ct = TestContext.Current.CancellationToken;
        harness.WriteConfigFile("providers.yaml", """
providers:
  local: { baseUrl: http://127.0.0.1:8080, auth: none }
  openrouter: { family: OpenAiChatCompatible, baseUrl: https://openrouter.example.test/api/v1, authRef: openrouter }
""");
        harness.WriteConfigFile("models.yaml", """
models:
  local-worker: { provider: local, context: 8192, maxOutput: 2048 }
  open-model: { provider: openrouter, context: 32768, maxOutput: 4096 }
""");

        await harness.Service.ConnectAnthropicAsync(ApiKey, validate: false, ct);
        harness.Handler.Respond(_ => ModelsPage(ModelJson("claude-x")));
        await harness.Service.DiscoverAnthropicModelsAsync(null, ct);

        var providers = harness.ReadConfigFile("providers.yaml");
        Assert.Contains("openrouter", providers, StringComparison.Ordinal);
        Assert.Contains("https://openrouter.example.test/api/v1", providers, StringComparison.Ordinal);
        var models = harness.ReadConfigFile("models.yaml");
        Assert.Contains("local-worker", models, StringComparison.Ordinal);
        Assert.Contains("open-model", models, StringComparison.Ordinal);
        Assert.Contains("context: 32768", models, StringComparison.Ordinal);

        var loaded = OmniHost.LoadUserConfiguration(harness.Paths);
        Assert.Equal(32768, loaded.Registry.Model("open-model")!.ContextWindow);
    }

    [Fact]
    public async System.Threading.Tasks.Task Repeated_discovery_skips_models_already_registered()
    {
        var harness = new Harness();
        var ct = TestContext.Current.CancellationToken;
        await harness.Service.ConnectAnthropicAsync(ApiKey, validate: false, ct);
        for (var i = 0; i < 2; i++)
        {
            harness.Handler.Respond(_ => ModelsPage(ModelJson("claude-x")));
        }

        var first = await harness.Service.DiscoverAnthropicModelsAsync(null, ct);
        var second = await harness.Service.DiscoverAnthropicModelsAsync(null, ct);
        Assert.Equal(1, first.RegisteredModels);
        Assert.Equal(0, second.RegisteredModels);
    }

    // ---------------------------------------------------------------- runtime consumability

    [Fact]
    public async System.Threading.Tasks.Task Stored_registry_drives_the_native_anthropic_adapter_against_a_fixture()
    {
        var harness = new Harness();
        var ct = TestContext.Current.CancellationToken;
        await harness.Service.ConnectAnthropicAsync(ApiKey, validate: false, ct);
        harness.Handler.Respond(_ => ModelsPage(ModelJson("claude-x")));
        await harness.Service.DiscoverAnthropicModelsAsync(null, ct);

        var loaded = OmniHost.LoadUserConfiguration(harness.Paths);
        var descriptor = loaded.Registry.Provider("anthropic");
        Assert.NotNull(descriptor);
        Assert.Equal(ProviderFamily.AnthropicMessages, descriptor!.Family);
        Assert.Equal(AuthKind.ApiKey, descriptor.Auth.Kind);
        Assert.Equal(ProviderConnectionService.AnthropicAuthRef, descriptor.Auth.SecretRef);

        var key = harness.Store.Load(ProviderConnectionService.AnthropicAuthRef, ct);
        Assert.NotNull(key);
        var sse = new ScriptedHandler().Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(MinimalSse(), Encoding.UTF8, "text/event-stream"),
        });
        var provider = new AnthropicMessagesProvider(descriptor,
            new SimpleSecretProvider("OMNI_").With(ProviderConnectionService.AnthropicAuthRef, key!),
            () => new HttpClient(sse, disposeHandler: false));
        var request = new ModelRequest(
            new ModelSelection(new ModelIdValue("claude-x"), 8192, ToolMode.Direct, null),
            [new ModelMessage(MessageRole.User, [new TextBlock("hola")])],
            "You are OmniCore.", [], ToolChoice.Auto(), null, null, null, null);
        var events = await Collect(provider.StreamAsync(request, ct));
        var completed = Assert.IsType<ResponseCompleted>(events[^1]);
        Assert.Equal("ok", Assert.Single(completed.Response.Content.OfType<TextBlock>()).Text);
        Assert.Equal(ApiKey, Assert.Single(sse.Requests).ApiKeyHeader);
        Assert.Equal("https://api.anthropic.com/v1/messages", Assert.Single(sse.Requests).Url);
    }

    // ---------------------------------------------------------------- localization

    [Fact]
    public void Connection_errors_are_localized_es_en()
    {
        var spanish = Localization.Spanish();
        var english = Localization.English();
        foreach (var kind in (ReadOnlySpan<string>)["empty", "tooShort", "invalid", "notConfigured",
                     "unreachable", "rateLimited", "unsupported", "unknownProvider", "catalogInvalid",
                     "catalogTooLarge", "invalidEndpoint", "modelConflict"])
        {
            var key = "providers.error." + kind;
            Assert.NotEqual(key, spanish.Resolve(key));
            Assert.NotEqual(key, english.Resolve(key));
        }

        Assert.Contains("redirecci\u00f3n", spanish.Resolve("providers.notice.redirectNotFollowed"), StringComparison.Ordinal);
        Assert.Contains("redirect", english.Resolve("providers.notice.redirectNotFollowed"), StringComparison.Ordinal);
    }
}
