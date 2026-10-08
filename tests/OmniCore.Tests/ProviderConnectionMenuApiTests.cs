namespace OmniCore.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Infrastructure;

/// <summary>
/// Menú de mantenimiento de conexiones (porción API de Anthropic, ADR-0011 §3): el TuiApp de
/// producción sobre un driver real de Terminal.Gui, cableado con la fachada real del Host
/// (<see cref="TuiProviderConnectionHost"/>) y un backend HTTP guionado. Sin login de cuenta
/// real, sin API externa autenticada y sin generación pagada: la Models API solo se usa como
/// contrato de verificación. La entrada de la API key es enmascarada y transitoria.
/// </summary>
public sealed class ProviderConnectionMenuApiTests
{
    private const string ApiKey = "sk-ant-test-0123456789abcdef";
    private const string OtherKey = "sk-or-test-9999zzzz";

    private static readonly DateTimeOffset FixedNow = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- fixtures

    private sealed record RecordedRequest(string Method, string Url, string? ApiKeyHeader)
    {
        public static RecordedRequest From(HttpRequestMessage request) => new(
            request.Method.Method,
            request.RequestUri!.ToString(),
            request.Headers.TryGetValues("x-api-key", out var key) ? string.Join(",", key) : null);
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

    /// <summary>Respuesta bloqueada hasta que el test la libera: ocupa y cancela de verdad.</summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        public TaskCompletionSource<HttpResponseMessage> Pending { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(RecordedRequest.From(request));
            return await Pending.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed record ConnectionFixture(
        TuiWiringTests.TuiFixture Fx,
        HttpMessageHandler Handler,
        FileCredentialStore Store,
        DefaultPlatformPaths Paths,
        TuiProviderConnectionHost Facade)
    {
        public ScriptedHandler Scripted => (ScriptedHandler)Handler;
        public GatedHandler Gated => (GatedHandler)Handler;
    }

    private static ConnectionFixture CreateFixture(Func<HttpMessageHandler>? handlerFactory = null)
    {
        var fx = TuiWiringTests.TuiFixture.Create();
        var paths = new DefaultPlatformPaths(Path.Combine(fx.Root, "connections"),
            Path.Combine(fx.Root, "connections-config"));
        var store = new FileCredentialStore(Path.Combine(fx.Root, "connections", "credentials.ini"));
        var handler = handlerFactory?.Invoke() ?? new ScriptedHandler();
        var service = new ProviderConnectionService(store, paths,
            httpFactory: () => new HttpClient(handler, disposeHandler: false), now: () => FixedNow);
        var facade = new TuiProviderConnectionHost(service, paths);
        return new(fx, handler, store, paths, facade);
    }

    private static void OpenConnectionsMenu(ConnectionFixture fixture)
    {
        var fx = fixture.Fx;
        TuiWiringTests.KeyWithEffect(fx, Terminal.Gui.Drivers.KeyCode.F4,
            () => fx.App.Overlay is not null, "F4 abre configuración");
        TuiWiringTests.Click(fx, "Conexiones de providers");
        fx.Wait(() => fx.App.Overlay?.SubViews.Any(view => view.Id == "omni-provider-list") == true,
            "menú de conexiones visible");
    }

    private static void ConnectKeyViaMenu(ConnectionFixture fixture)
    {
        var fx = fixture.Fx;
        TuiWiringTests.Click(fx, "Conectar");
        fx.Wait(() => fx.App.Overlay?.SubViews.OfType<Terminal.Gui.Views.TextField>()
            .Any(t => t.Id == "provider-api-key") == true, "formulario de conexión visible");
        TypeInto(fx, ApiKey);
    }

    /// <summary>Teclea la API key en el campo enmascarado con teclas inyectadas reales.</summary>
    private static void TypeInto(TuiWiringTests.TuiFixture fx, string text)
    {
        for (var tries = 0; ; tries++)
        {
            string actual = "";
            fx.Invoke(() => actual = Field(fx).Text?.ToString() ?? "");
            if (actual == text) return;
            foreach (var character in text[actual.Length..]) fx.InjectKey(new Terminal.Gui.Input.Key(character));
            if (TuiWiringTests.WaitUntil(() => (Field(fx).Text?.ToString() ?? "") == text, 3000)) return;
            if (tries >= 3) Assert.Fail("las teclas no llegaron al campo de la API key: '" + actual + "'");
        }
    }

    private static Terminal.Gui.Views.TextField Field(TuiWiringTests.TuiFixture fx) =>
        fx.App.Overlay!.SubViews.OfType<Terminal.Gui.Views.TextField>()
            .Single(t => t.Id == "provider-api-key");

    private static string ScreenText(TuiWiringTests.TuiFixture fx)
    {
        string text = "";
        fx.Invoke(() =>
        {
            var cells = fx.Application.Driver!.Contents!;
            text = string.Join("\n", Enumerable.Range(0, cells.GetLength(0)).Select(y =>
                string.Concat(Enumerable.Range(0, cells.GetLength(1)).Select(x => cells[y, x].Grapheme))));
        });
        return text;
    }

    private static string ListRows(TuiWiringTests.TuiFixture fx)
    {
        string rows = "";
        fx.Invoke(() => rows = string.Join("\n", fx.App.Overlay!.SubViews
            .OfType<Terminal.Gui.Views.ListView>().Single().Source!.ToList()
            .Cast<object>().Select(item => item.ToString() ?? "")));
        return rows;
    }

    // ---------------------------------------------------------------- presentación

    [Fact]
    public void Menu_presents_measured_status_without_paging_borders_or_secrets()
    {
        var fixture = CreateFixture();
        var fx = fixture.Fx;
        var ct = TestContext.Current.CancellationToken;
        fx.StartTui(connections: fixture.Facade);
        OpenConnectionsMenu(fixture);

        var overlay = (Terminal.Gui.Views.FrameView)fx.App.Overlay!;
        Assert.Equal(Terminal.Gui.Drawing.LineStyle.None, overlay.BorderStyle);
        Assert.Equal(Terminal.Gui.Drawing.LineStyle.None, fx.App.MainWindow!.BorderStyle);
        var list = overlay.SubViews.OfType<Terminal.Gui.Views.ListView>().Single();
        fx.Wait(() => list.Source!.Count == 1, "fila sintética de Anthropic declarada");
        var row = ListRows(fx);
        Assert.Contains("anthropic", row, StringComparison.Ordinal);
        Assert.Contains("api key", row, StringComparison.Ordinal);
        Assert.Contains("sin configurar", row, StringComparison.Ordinal);
        Assert.DoesNotContain("Anterior", row, StringComparison.Ordinal);
        Assert.DoesNotContain("Siguiente", row, StringComparison.Ordinal);
        Assert.DoesNotContain(overlay.SubViews.OfType<Terminal.Gui.Views.Button>(), b =>
            b.Text.ToString() is "Anterior" or "Siguiente");

        // Acentos: título violeta, selección cian, sin marcos.
        var title = overlay.SubViews.Single(view => view.Id == "omni-provider-title");
        Assert.Equal(new Terminal.Gui.Drawing.Color("#C8A0F5"), title.GetScheme().Normal.Foreground);
        Assert.Equal(new Terminal.Gui.Drawing.Color("#67D4D0"), list.GetScheme().Focus.Background);

        // Acciones diferenciadas según la fila: solo Conectar está disponible.
        var buttons = overlay.SubViews.OfType<Terminal.Gui.Views.Button>().ToDictionary(b => b.Id ?? "");
        Assert.True(buttons["provider-connect"].Enabled);
        Assert.False(buttons["provider-test"].Enabled);
        Assert.False(buttons["provider-discover"].Enabled);
        Assert.False(buttons["provider-disconnect"].Enabled);

        // Atajos visibles y sin secretos en pantalla.
        var screen = ScreenText(fx);
        Assert.Contains("Alt+T", screen, StringComparison.Ordinal);
        Assert.Contains("Alt+C", screen, StringComparison.Ordinal);
        Assert.Contains("Esc", screen, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, screen, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant", screen, StringComparison.Ordinal);
        _ = ct;
    }

    [Fact]
    public async System.Threading.Tasks.Task Menu_lists_declared_providers_and_marks_subscription_rows_without_api_actions()
    {
        var fixture = CreateFixture();
        var fx = fixture.Fx;
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(fixture.Paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(fixture.Paths.ConfigDirectory, "providers.yaml"), """
providers:
  chatgpt:
    family: OpenAIResponses
    profile: codex
    baseUrl: https://chatgpt.com/backend-api/codex
    billingMode: IncludedQuota
""");
        fx.StartTui(connections: fixture.Facade);
        OpenConnectionsMenu(fixture);

        var rows = ListRows(fx);
        Assert.Contains("chatgpt", rows, StringComparison.Ordinal);
        Assert.Contains("suscripción", rows, StringComparison.Ordinal);
        Assert.Contains("anthropic", rows, StringComparison.Ordinal);

        // Fila de suscripción seleccionada: ninguna acción de API key (sin sustituto manual,
        // sin fallback pagado, sin consentimiento implícito).
        fx.Invoke(() => fx.App.Overlay!.SubViews.OfType<Terminal.Gui.Views.ListView>()
            .Single().SelectedItem = 0);
        var buttons = fx.App.Overlay!.SubViews.OfType<Terminal.Gui.Views.Button>()
            .ToDictionary(b => b.Id ?? "");
        fx.Wait(() => !buttons["provider-connect"].Enabled, "sin Conectar en la fila de suscripción");
        Assert.False(buttons["provider-test"].Enabled);
        Assert.False(buttons["provider-discover"].Enabled);
        Assert.False(buttons["provider-disconnect"].Enabled);
        _ = ct;
    }

    // ---------------------------------------------------------------- conectar

    [Fact]
    public async System.Threading.Tasks.Task Connect_masks_entry_saves_encrypted_and_reports_measured_connected_state()
    {
        var fixture = CreateFixture();
        var fx = fixture.Fx;
        var ct = TestContext.Current.CancellationToken;
        fx.StartTui(connections: fixture.Facade);
        OpenConnectionsMenu(fixture);
        ConnectKeyViaMenu(fixture);

        var field = Field(fx);
        fx.Invoke(() => Assert.True(field.Secret, "la entrada de la API key debe estar enmascarada"));

        // Antes de guardar, nada persiste el borrador: ni el composer ni los archivos.
        fx.Invoke(() => Assert.DoesNotContain(ApiKey, fx.App.Composer!.Text?.ToString() ?? "", StringComparison.Ordinal));

        fixture.Scripted.Respond(_ => ModelsPage(ModelJson("claude-x")));
        TuiWiringTests.Click(fx, "Guardar");

        fx.Wait(() => fixture.Store.Load(ProviderConnectionService.AnthropicAuthRef, ct) == ApiKey,
            "credencial cifrada guardada en el almacén real");
        fx.Wait(() => fx.App.Overlay?.SubViews.Any(view => view.Id == "omni-provider-list") == true,
            "el menú se reabre con el resultado");
        Assert.Contains("conectada", ListRows(fx), StringComparison.Ordinal);
        Assert.Contains("****cdef", ListRows(fx), StringComparison.Ordinal);
        var screen = ScreenText(fx);
        Assert.Contains("2026-10-08 12:00 UTC", screen, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, screen, StringComparison.Ordinal);

        var metadata = ProviderConnectionMetadataStore.Read(fixture.Paths.DataDirectory, "anthropic", ct);
        Assert.Equal("valid", metadata.ValidationState);
        Assert.NotNull(metadata.ValidatedAt);
        Assert.Contains("AnthropicMessages",
            File.ReadAllText(Path.Combine(fixture.Paths.ConfigDirectory, "providers.yaml")), StringComparison.Ordinal);

        // Ningún archivo del disco del fixture contiene la key en claro (ni journal ni YAML ni logs).
        foreach (var file in Directory.EnumerateFiles(fx.Root, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(ApiKey, File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Connect_without_network_verification_reports_unmeasured_never_connected()
    {
        var fixture = CreateFixture();
        var fx = fixture.Fx;
        var ct = TestContext.Current.CancellationToken;
        fx.StartTui(connections: fixture.Facade);
        OpenConnectionsMenu(fixture);
        ConnectKeyViaMenu(fixture);
        // Sin respuesta guionada: guardar sin verificación no debe contactar al provider.
        TuiWiringTests.Click(fx, "Guardar");

        fx.Wait(() => fixture.Store.Load(ProviderConnectionService.AnthropicAuthRef, ct) == ApiKey,
            "credencial guardada");
        fx.Wait(() => fx.App.Overlay?.SubViews.Any(view => view.Id == "omni-provider-list") == true, "menú reabierto");
        var screen = ScreenText(fx);
        Assert.Contains("sin medir", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("conectada", screen, StringComparison.Ordinal);
        Assert.Empty(fixture.Scripted.Requests);
    }

    [Fact]
    public async System.Threading.Tasks.Task Connect_rejected_by_provider_shows_typed_error_and_saves_nothing()
    {
        var fixture = CreateFixture();
        var fx = fixture.Fx;
        var ct = TestContext.Current.CancellationToken;
        fx.StartTui(connections: fixture.Facade);
        OpenConnectionsMenu(fixture);
        ConnectKeyViaMenu(fixture);

        fixture.Scripted.Respond(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        TuiWiringTests.Click(fx, "Guardar");

        fx.Wait(() => ScreenText(fx).Contains("El provider rechazó", StringComparison.Ordinal),
            "error tipado localizado visible");
        Assert.NotNull(fx.App.Overlay); // el formulario sigue abierto: nada se fabrica como éxito
        Assert.Null(fixture.Store.Load(ProviderConnectionService.AnthropicAuthRef, ct));
        Assert.Single(fixture.Scripted.Requests);
    }

    // ---------------------------------------------------------------- probar

    [Fact]
    public async System.Threading.Tasks.Task Test_action_measures_the_stored_key_and_reports_invalid_with_notice()
    {
        var fixture = CreateFixture();
        var fx = fixture.Fx;
        var ct = TestContext.Current.CancellationToken;
        await fixture.Facade.ConnectAsync(ApiKey, validate: false, ct); // precondición de fixture, no de UI
        fixture.Scripted.Respond(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        fx.StartTui(connections: fixture.Facade);
        OpenConnectionsMenu(fixture);

        var buttons = fx.App.Overlay!.SubViews.OfType<Terminal.Gui.Views.Button>()
            .ToDictionary(b => b.Id ?? "");
        fx.Wait(() => buttons["provider-test"].Enabled, "Probar disponible con credencial");
        TuiWiringTests.Click(fx, "Probar");

        fx.Wait(() => ScreenText(fx).Contains("inválida", StringComparison.Ordinal),
            "estado medido inválido visible");
        Assert.Contains("HTTP 401", ScreenText(fx), StringComparison.Ordinal);
        Assert.Equal("invalid", ProviderConnectionMetadataStore.Read(fixture.Paths.DataDirectory, "anthropic", ct).ValidationState);
        Assert.DoesNotContain(ApiKey, ScreenText(fx), StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task Esc_cancels_a_pending_measurement_and_changes_nothing()
    {
        var fixture = CreateFixture(() => new GatedHandler());
        var fx = fixture.Fx;
        var ct = TestContext.Current.CancellationToken;
        await fixture.Facade.ConnectAsync(ApiKey, validate: false, ct);
        var before = ProviderConnectionMetadataStore.Read(fixture.Paths.DataDirectory, "anthropic", ct);
        fx.StartTui(connections: fixture.Facade);
        OpenConnectionsMenu(fixture);

        TuiWiringTests.Click(fx, "Probar");
        fx.Wait(() => ScreenText(fx).Contains("Probando la conexión", StringComparison.Ordinal),
            "ocupación visible");
        fx.Invoke(() => Assert.False(fx.App.Overlay!.SubViews.OfType<Terminal.Gui.Views.Button>()
            .Single(b => b.Id == "provider-test").Enabled, "acción deshabilitada mientras ocupa"));

        fx.InjectKey(new Terminal.Gui.Input.Key(Terminal.Gui.Drivers.KeyCode.Esc));
        fx.Wait(() => ScreenText(fx).Contains("Cancelado; nada cambió", StringComparison.Ordinal),
            "cancelación visible sin cerrar el menú");
        Assert.Single(fixture.Gated.Requests);
        Assert.NotNull(fx.App.Overlay);

        fixture.Gated.Pending.TrySetResult(ModelsPage(ModelJson("claude-x")));
        var after = ProviderConnectionMetadataStore.Read(fixture.Paths.DataDirectory, "anthropic", ct);
        Assert.Equal(before.ValidationState, after.ValidationState);
    }

    // ---------------------------------------------------------------- descubrir y desconectar

    [Fact]
    public async System.Threading.Tasks.Task Discover_registers_reported_models_in_the_user_config()
    {
        var fixture = CreateFixture();
        var fx = fixture.Fx;
        var ct = TestContext.Current.CancellationToken;
        await fixture.Facade.ConnectAsync(ApiKey, validate: false, ct);
        var page = ModelsPage(ModelJson("claude-a", "A") + "," + ModelJson("claude-b", "B"));
        page.Headers.TryAddWithoutValidation("anthropic-ratelimit-requests-remaining", "99");
        fixture.Scripted.Respond(_ => page);
        fx.StartTui(connections: fixture.Facade);
        OpenConnectionsMenu(fixture);

        TuiWiringTests.Click(fx, "Descubrir");
        fx.Wait(() => ScreenText(fx).Contains("2 modelos registrados", StringComparison.Ordinal),
            "resultado del descubrimiento visible");
        Assert.Contains("ventana de cuota", ScreenText(fx), StringComparison.Ordinal);
        var loaded = OmniHost.LoadUserConfiguration(fixture.Paths);
        Assert.NotNull(loaded.Registry.Model("claude-a"));
        Assert.Equal("anthropic", loaded.Registry.Model("claude-a")!.ProviderId);
        Assert.NotNull(loaded.Registry.Model("claude-b"));
        Assert.NotNull(ProviderConnectionMetadataStore.Read(fixture.Paths.DataDirectory, "anthropic", ct).DiscoveredAt);
    }

    [Fact]
    public async System.Threading.Tasks.Task Disconnect_requires_confirmation_and_removes_only_that_credential()
    {
        var fixture = CreateFixture();
        var fx = fixture.Fx;
        var ct = TestContext.Current.CancellationToken;
        fixture.Store.Save("openrouter", OtherKey, ct);
        await fixture.Facade.ConnectAsync(ApiKey, validate: false, ct);
        fx.StartTui(connections: fixture.Facade);
        OpenConnectionsMenu(fixture);

        var buttons = fx.App.Overlay!.SubViews.OfType<Terminal.Gui.Views.Button>()
            .ToDictionary(b => b.Id ?? "");
        fx.Wait(() => buttons["provider-disconnect"].Enabled, "Desconectar disponible");
        TuiWiringTests.Click(fx, "Desconectar…");
        fx.Wait(() => ((TuiWiringTests.TuiFixture)fx).App.Overlay is { } overlay
            && overlay.SubViews.OfType<Terminal.Gui.Views.Button>()
                .Any(b => b.Id == "provider-confirm-disconnect"), "confirmación visible");
        TuiWiringTests.Click(fx, "Sí, desconectar");

        fx.Wait(() => fixture.Store.Load(ProviderConnectionService.AnthropicAuthRef, ct) is null,
            "credencial borrada tras confirmar");
        Assert.Equal(OtherKey, fixture.Store.Load("openrouter", ct));
        fx.Wait(() => fx.App.Overlay is not null && ListRows(fx).Contains("sin configurar", StringComparison.Ordinal),
            "la fila vuelve a sin configurar");
    }

    // ---------------------------------------------------------------- navegación

    [Fact]
    public void Rows_navigate_vertically_with_wrap_and_footer_focus_wraps_too()
    {
        var fixture = CreateFixture();
        var fx = fixture.Fx;
        Directory.CreateDirectory(fixture.Paths.ConfigDirectory);
        File.WriteAllText(Path.Combine(fixture.Paths.ConfigDirectory, "providers.yaml"), """
providers:
  local: { baseUrl: http://127.0.0.1:8080, auth: none }
  anthropic:
    family: AnthropicMessages
    baseUrl: https://api.anthropic.com
    authRef: anthropic
    billingMode: MeteredCurrency
""");
        fx.StartTui(connections: fixture.Facade);
        OpenConnectionsMenu(fixture);

        bool Focused(string id) => fx.App.Overlay?.SubViews
            .OfType<Terminal.Gui.Views.Button>().Any(b => b.Id == id && b.HasFocus) == true;
        var list = fx.App.Overlay!.SubViews.OfType<Terminal.Gui.Views.ListView>().Single();
        fx.Wait(() => list.Source!.Count == 2 && list.HasFocus, "dos filas, lista enfocada");
        Assert.Equal(0, list.SelectedItem);
        TuiWiringTests.KeyWithEffect(fx, Terminal.Gui.Drivers.KeyCode.CursorDown, () => list.SelectedItem == 1,
            "abajo selecciona la segunda fila");
        TuiWiringTests.KeyWithEffect(fx, Terminal.Gui.Drivers.KeyCode.CursorDown, () => list.SelectedItem == 0,
            "abajo desde la última envuelve a la primera");
        TuiWiringTests.KeyWithEffect(fx, Terminal.Gui.Drivers.KeyCode.CursorUp, () => list.SelectedItem == 1,
            "arriba desde la primera envuelve a la última");
        TuiWiringTests.KeyWithEffect(fx, Terminal.Gui.Drivers.KeyCode.CursorRight, () => Focused("provider-connect"),
            "derecha pasa de la fila a las acciones");
        TuiWiringTests.KeyWithEffect(fx, Terminal.Gui.Drivers.KeyCode.CursorDown, () => Focused("provider-back"),
            "bajar salta las acciones deshabilitadas hasta Volver");
        TuiWiringTests.KeyWithEffect(fx, Terminal.Gui.Drivers.KeyCode.CursorDown, () => list.HasFocus,
            "desde Volver, abajo vuelve a la lista");
        TuiWiringTests.KeyWithEffect(fx, Terminal.Gui.Drivers.KeyCode.CursorUp, () => Focused("provider-back"),
            "arriba desde la lista envuelve a la última acción");
    }

    // ---------------------------------------------------------------- utilidades compartidas con el backend

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
}
