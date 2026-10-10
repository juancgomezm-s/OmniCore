using System.Net;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;

namespace OmniCore.Tests;

/// <summary>
/// Prueba del cableado completo, no de cada pieza por separado. Es el test que faltaba cuando F1-F4
/// estaban verdes pero nadie invocaba el flujo: afirma que un providers.yaml declarado por cuenta
/// produce un provider real que manda Bearer, y que /doctor ve la sesion.
/// </summary>
public sealed class ClaudeOAuthWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-oauth-wiring-" + Guid.NewGuid().ToString("N"));

    public ClaudeOAuthWiringTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void A_provider_declaring_oauth_is_registered_as_AuthKind_OAuth()
    {
        var paths = WriteConfig(OAuthProviders);

        var loaded = OmniHost.LoadUserConfiguration(paths);
        var provider = loaded.Registry.Provider("anthropic");

        Assert.NotNull(provider);
        Assert.Equal(AuthKind.OAuth, provider!.Auth.Kind);
        // La secret ref del descriptor y la de la sesion tienen que ser la misma o el login
        // guardaria en un sitio y el provider leerian otro.
        Assert.Equal("anthropic-oauth", provider.Auth.SecretRef);
        Assert.Equal(provider.Auth.SecretRef, OmniHost.CreateClaudeOAuthSession(paths, "anthropic")?.SecretRef);
    }

    [Fact]
    public void An_oauth_provider_outranks_an_api_key_declaration_on_the_same_entry()
    {
        // Colision declarada: authRef + oauth. OAuth gana, y tiene que ser visible en el registro
        // para que el usuario no crea que esta gastando credits de API sin saberlo.
        // authRef + oauth en el mismo provider: gana oauth.
        var yaml = OAuthProviders.Replace(
            "            baseUrl: https://api.anthropic.com",
            "            baseUrl: https://api.anthropic.com\n            authRef: anthropic-key");
        var paths = WriteConfig(yaml);

        var provider = OmniHost.LoadUserConfiguration(paths).Registry.Provider("anthropic");

        Assert.Equal(AuthKind.OAuth, provider!.Auth.Kind);
    }

    [Fact]
    public void The_credential_source_reaches_the_connected_provider()
    {
        var paths = WriteConfig(OAuthProviders);
        var descriptor = OmniHost.LoadUserConfiguration(paths).Registry.Provider("anthropic")!;

        var source = OmniCliRuntime.ClaudeOAuthFor(descriptor, paths);

        Assert.NotNull(source);
    }

    [Fact]
    public void No_credential_source_for_a_plain_api_key_provider()
    {
        var paths = WriteConfig(ApiKeyProviders);
        var descriptor = OmniHost.LoadUserConfiguration(paths).Registry.Provider("anthropic")!;

        Assert.Null(OmniCliRuntime.ClaudeOAuthFor(descriptor, paths));
        Assert.Null(OmniCliRuntime.ClaudeOAuthIdentityFor(descriptor, paths));
    }

    [Fact]
    public async System.Threading.Tasks.Task The_connected_provider_sends_a_bearer_for_a_session_stored_by_the_login()
    {
        // End-to-end de verdad: se guarda un credential como lo dejaria el login, se conecta el
        // provider desde la config del usuario y se afirma lo que sale por la red.
        var paths = WriteConfig(OAuthProviders);
        var descriptor = OmniHost.LoadUserConfiguration(paths).Registry.Provider("anthropic")!;

        // El cliente de tokens no debe llamar a nadie: un token vigente se usa tal cual, asi que
        // cualquier peticion aqui es un fallo real y RejectingHandler la convierte en una.
        var session = OmniHost.CreateClaudeOAuthSession(paths, "anthropic",
            httpFactory: () => new HttpClient(new RejectingHandler(), disposeHandler: false))!;

        var now = DateTimeOffset.UtcNow;
        session.Credentials.Save(session.SecretRef, new ClaudeOAuthCredential(
            AccessToken: "at-wired",
            RefreshToken: "rt-wired",
            ExpiresAt: now.AddHours(1),
            Scopes: ["user:profile", "user:inference"],
            ClientId: "cid-wiring-0001")
        {
            AccountUuid = "acc-wired",
            AuthenticatedAt = now,
        }, CancellationToken.None);

        var handler = new RecordingHandler();
        var connected = (AnthropicMessagesProvider)OmniHost.ConnectProvider(
            descriptor, "https://api.anthropic.com", descriptor.Auth.SecretRef!, apiKey: "",
            claudeOAuth: session.Auth, oauthIdentity: session.Identity,
            anthropicHttpFactory: () => new HttpClient(handler, disposeHandler: false));

        await foreach (var _ in connected.StreamAsync(BuildRequest(), TestContext.Current.CancellationToken))
        {
        }

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("Bearer at-wired", sent["Authorization"]);
        Assert.Equal("oauth-2025-04-20", sent["anthropic-beta"]);
        Assert.Equal("true", sent["x-anthropic-additional-protection"]);
        Assert.Equal("acc-wired", sent["x-account-uuid"]);
        // El user-agent es el que declaro el usuario, con la version del producto rellenada.
        Assert.StartsWith("agent/", sent["User-Agent"], StringComparison.Ordinal);
        Assert.False(sent.ContainsKey("x-api-key"), "el camino de cuenta nunca manda API key");
    }

    [Fact]
    public async System.Threading.Tasks.Task An_expired_session_refreshes_through_the_wired_coordinator()
    {
        var paths = WriteConfig(OAuthProviders);
        var descriptor = OmniHost.LoadUserConfiguration(paths).Registry.Provider("anthropic")!;
        var session = OmniHost.CreateClaudeOAuthSession(paths, "anthropic",
            // Sin esta inyeccion el cliente apuntaria al endpoint real de Anthropic: ningun test
            // hace peticiones ni autentica (CLAUDE.md: priorizar tests deterministas).
            httpFactory: () => new HttpClient(new RejectingHandler(), disposeHandler: false))!;

        // Credential caducado: el proveedor debe refrescarlo antes de mandar el request.
        session.Credentials.Save(session.SecretRef, new ClaudeOAuthCredential(
            "at-stale", "rt-stale", DateTimeOffset.UtcNow.AddSeconds(-60),
            ["user:profile", "user:inference"], "cid-wiring-0001")
        {
            AuthenticatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        }, CancellationToken.None);

        // Sin servidor real el refresco falla de forma transitoria: el borde no puede devolver el
        // token caducado (produciria un 401 seguro) ni puede decir que la sesion murio.
        var ex = await Assert.ThrowsAsync<ClaudeOAuthNetworkException>(() =>
            session.Auth.GetAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.False(session.Refresh.IsDead("rt-stale"),
            "un fallo de red no mata un refresh token recuperable (plan §11.3)");
        _ = ex;
    }

    [Fact]
    public void Doctor_reports_the_configured_session_without_leaking_secrets()
    {
        // omni doctor toma las rutas del proceso: se le apuntan los temporales del test con las
        // mismas variables que usa el resto de la suite, para no tocar los datos del usuario.
        var paths = WriteConfig(OAuthProviders);
        var data = Environment.GetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable);
        var config = Environment.GetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable);
        var lines = new List<string>();
        try
        {
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, paths.DataDirectory);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, paths.ConfigDirectory);

            var localization = new OmniCore.Client.Localization("es");
            OmniCliRuntime.Doctor("es", lines.Add, localization.Resolve);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.DataDirVariable, data);
            Environment.SetEnvironmentVariable(DefaultPlatformPaths.ConfigDirVariable, config);
        }

        var output = string.Join(" ", lines);
        Assert.Contains("Claude", output, StringComparison.Ordinal);
        // Sin secretos y sin el client id del usuario: doctor informa la sesion, no la credencial.
        Assert.DoesNotContain("cid-wiring-0001", output, StringComparison.Ordinal);
        Assert.DoesNotContain("rt-wired", output, StringComparison.Ordinal);
        Assert.DoesNotContain("at-wired", output, StringComparison.Ordinal);
    }

    // ---- Config ---------------------------------------------------------------------------

    private const string OAuthProviders = """
        providers:
          anthropic:
            family: AnthropicMessages
            baseUrl: https://api.anthropic.com
            oauth:
              clientId: cid-wiring-0001
              userAgent: "agent/{version} (external, cli)"
              scopes: [user:profile, user:inference]
              authorizeUrl: https://claude.com/cai/oauth/authorize
              tokenUrl: https://platform.claude.com/v1/oauth/token
        """;

    private const string ApiKeyProviders = """
        providers:
          anthropic:
            family: AnthropicMessages
            baseUrl: https://api.anthropic.com
            authRef: anthropic-key
        """;

    private const string Models = """
        models:
          sonnet:
            provider: anthropic
            context: 200000
            recommendedUsableContext: 190000
            maxOutput: 8192
            parametersBillions: 100
        """;

    private TempPaths WriteConfig(string providersYaml)
    {
        var paths = new TempPaths(_root);
        Directory.CreateDirectory(paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "providers.yaml"), Dedent(providersYaml));
        File.WriteAllText(Path.Combine(paths.ConfigDirectory, "models.yaml"), Dedent(Models));
        return paths;
    }

    private static string Dedent(string yaml)
    {
        var normalized = yaml.Replace("\r\n", "\n").Trim('\n');
        var lines = normalized.Split('\n');
        var indent = lines.Where(l => !string.IsNullOrWhiteSpace(l))
            .Min(l => l.Length - l.TrimStart().Length);
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart())) + "\n";
    }

    private static ModelRequest BuildRequest() =>
        new(new ModelSelection(new ModelIdValue("claude-sonnet"), 4096, ToolMode.Direct, null, maxOutputTokens: 512),
            [new ModelMessage(MessageRole.User, [new TextBlock("hola")])], null, [],
            ToolChoice.Auto(), null, null, null, null);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Dictionary<string, string>> Requests { get; } = [];

        protected override async System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Headers
                .ToDictionary(h => h.Key, h => string.Join(" ", h.Value), StringComparer.OrdinalIgnoreCase));
            _ = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(MinimalStream, System.Text.Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed class QueueJsonHandler : HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new System.Net.Http.StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>
    /// Ningun test llama al endpoint real de Anthropic. Devuelve un fallo de red tipado en vez de
    /// una excepcion cruda: asi el coordinador lo clasifica como transitorio (no marca el refresh
    /// token como muerto) y el test puede afirmar que el camino es ese.
    /// </summary>
    private sealed class RejectingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return System.Threading.Tasks.Task.FromException<HttpResponseMessage>(
                new HttpRequestException("Un test no puede llamar al endpoint real de Anthropic."));
        }
    }

    private const string MinimalStream =
        "event: message_start\n"
        + "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"role\":\"assistant\",\"model\":\"m\","
        + "\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n"
        + "event: content_block_delta\n"
        + "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hola\"}}\n\n"
        + "event: message_delta\n"
        + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"usage\":{\"output_tokens\":1}}}\n\n"
        + "event: message_stop\n"
        + "data: {\"type\":\"message_stop\"}\n\n";

    private sealed class TempPaths(string root) : IPlatformPaths
    {
        public string DataDirectory => Path.Combine(root, "data");

        public string UserDatabasePath => Path.Combine(DataDirectory, "user.db");

        public string ConfigDirectory => Path.Combine(root, "config");

        public string WorkspaceDirectory(string workspaceId) => Path.Combine(DataDirectory, "workspaces", workspaceId);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
