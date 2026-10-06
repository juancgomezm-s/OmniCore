namespace OmniCore.Tests;

using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Abstractions;
using System.Net;

/// <summary>Router por alias desde models.yaml y escalación explícita (M5, spec §21/§73).</summary>
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class ModelRoutingHostTests
{
    [Fact]
    public void Yaml_aliases_resolve_physical_override_routes_without_using_hash_as_model_or_price_key()
    {
        var previous = Environment.GetEnvironmentVariable("OMNI_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", "http://127.0.0.1:9901/v1");
            var loaded = Load("routing:\n  exploration: [worker]\n  escalation: { mode: auto, chain: [worker, frontier] }\n");
            var decision = ModelRoutingHost.Route(loaded, RoutingTaskKind.Exploration, false, 0, _ => true)!;
            Assert.Equal("qwen-27b", decision.Chosen.ModelId);
            Assert.NotEqual(decision.Chosen.ModelId, decision.Chosen.RouteId.Value);
            Assert.Null(loaded.Registry.Model(decision.Chosen.RouteId.Value));
            Assert.Equal("qwen-27b", loaded.Registry.Model(decision.Chosen.ModelId)!.Id);
            Assert.Equal("http://127.0.0.1:9901/v1", decision.Chosen.Route.Endpoint);
            Assert.Equal(decision.Chosen.RouteId, ModelRoutingHost.Policy(loaded)!.Preferences[RoutingTaskKind.Exploration].Single());
            Assert.Equal(decision.Chosen.RouteId, decision.Chosen.Profile.RouteId);
            var next = ModelRoutingHost.NextEscalation(loaded, new RouteId(decision.Chosen.RouteId.Value), false, 40_000, _ => true)!;
            Assert.Equal("claude-x", next.ModelId);
            Assert.Equal(3m, next.PricePerMillionTokensUsd);
            Assert.Equal("cloud", next.Route.ProviderId);
            Assert.Null(ModelRoutingHost.NextEscalation(Load("routing:\n  escalation: { mode: ask, chain: [worker] }\n"),
                decision.Chosen.RouteId, false, 0, _ => true));
        }
        finally { Environment.SetEnvironmentVariable("OMNI_BASE_URL", previous); }
    }

    [Fact]
    public void Route_selection_captures_endpoint_once_even_when_write_policy_callback_changes_environment()
    {
        var previous = Environment.GetEnvironmentVariable("OMNI_BASE_URL");
        try
        {
            Environment.SetEnvironmentVariable("OMNI_BASE_URL", "http://127.0.0.1:9902/v1");
            var loaded = Load("routing:\n  exploration: [worker, frontier]\n");
            var decision = ModelRoutingHost.Route(loaded, RoutingTaskKind.Exploration, false, 0, _ =>
            {
                Environment.SetEnvironmentVariable("OMNI_BASE_URL", "http://127.0.0.1:9903/v1");
                return true;
            })!;
            Assert.Equal("http://127.0.0.1:9902/v1", decision.Chosen.Route.Endpoint);
            Assert.All(ModelRoutingHost.Candidates(loaded, _ => true), candidate =>
                Assert.Equal("http://127.0.0.1:9903/v1", candidate.Route.Endpoint));
        }
        finally { Environment.SetEnvironmentVariable("OMNI_BASE_URL", previous); }
    }

    [Fact]
    public async System.Threading.Tasks.Task Actual_open_provider_circuit_changes_candidates_routing_and_escalation_without_health_requests()
    {
        var loaded = Load("routing:\n  exploration: [worker, frontier]\n  escalation: { mode: ask, chain: [worker] }\n");
        var circuits = new ProviderResilienceCatalog();
        var now = DateTimeOffset.UnixEpoch;
        var handler = new UnavailableHandler();
        var provider = new OpenAiChatCompatibleProvider(loaded.Registry.Provider("local")!, new EmptySecrets(),
            () => new HttpClient(handler, disposeHandler: false), new OpenAiProviderOptions
            {
                CircuitCatalog = circuits, CircuitFailureThreshold = 1, MaxRetries = 0,
                CircuitCooldown = TimeSpan.FromSeconds(30), UtcNow = () => now,
            });
        Assert.Equal("qwen-27b", ModelRoutingHost.Route(loaded, RoutingTaskKind.Exploration, false, 0, _ => true, circuits)!.Chosen.ModelId);
        Assert.Null(circuits.Snapshot("cloud")); // Unknown is not an invented failed health check.
        var request = new ModelRequest(new ModelSelection(new ModelIdValue("qwen-27b"), 4096, ToolMode.Direct, null),
            [new ModelMessage(MessageRole.User, [new TextBlock("hello")])], null, [], ToolChoice.Auto(), null, null, null, null);
        await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var unused in provider.StreamAsync(request, TestContext.Current.CancellationToken)) { }
        });
        Assert.Equal(1, handler.Calls);
        var decision = ModelRoutingHost.Route(loaded, RoutingTaskKind.Exploration, false, 0, _ => true, circuits)!;
        Assert.Equal("claude-x", decision.Chosen.ModelId);
        Assert.Contains(decision.Rejected, rejected => rejected.ModelId == "qwen-27b" && rejected.Reason == RouteRejection.Unavailable);
        Assert.False(ModelRoutingHost.Candidates(loaded, _ => true, circuits).Single(candidate => candidate.ModelId == "qwen-27b").Available);
        Assert.Null(ModelRoutingHost.NextEscalation(loaded, "claude-x", false, 0, _ => true, circuits));
        Assert.Equal(1, handler.Calls); // Routing and snapshots are reads, not authenticated probes.
        now += TimeSpan.FromSeconds(30);
        Assert.True(circuits.Snapshot("local")!.CanAttempt);
        Assert.Equal("qwen-27b", ModelRoutingHost.Route(loaded, RoutingTaskKind.Exploration, false, 0, _ => true, circuits)!.Chosen.ModelId);
        Assert.Equal(1, handler.Calls);
    }

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                { Content = new StringContent("fixture unavailable") });
        }
    }

    private sealed class EmptySecrets : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Fixture expects no credential lookup.");
    }

    private const string Providers = """
providers:
  local: { baseUrl: http://127.0.0.1:8080, auth: none }
  cloud: { family: AnthropicMessages, baseUrl: https://api.example.test, authRef: cloud-key }
""";

    private static string Models(string routing) => """
models:
  qwen-27b: { provider: local, context: 32768, aliases: [worker] }
  claude-x: { provider: cloud, context: 200000, aliases: [frontier], inputPricePerMillionUsd: 3, outputPricePerMillionUsd: 15 }
""" + Environment.NewLine + routing;

    private static LoadedUserConfiguration Load(string routing) => new ConfigLoader().Load(Providers, Models(routing));

    [Fact]
    public void Routing_section_chooses_by_task_kind_through_aliases()
    {
        var loaded = Load("""
routing:
  exploration: [worker]
  implementation: [frontier, worker]
""");
        Assert.Equal("qwen-27b", ModelRoutingHost.Route(loaded, RoutingTaskKind.Exploration, false, 0, _ => true)!.Chosen.ModelId);
        Assert.Equal("claude-x", ModelRoutingHost.Route(loaded, RoutingTaskKind.Implementation, true, 0, _ => true)!.Chosen.ModelId);
    }

    [Fact]
    public void Switching_from_local_worker_to_frontier_only_changes_routing_config_not_the_task()
    {
        var request = (Kind: RoutingTaskKind.Implementation, Write: true);
        var local = Load("routing:\n  implementation: [worker]\n");
        var frontier = Load("routing:\n  implementation: [frontier]\n");

        Assert.Equal("qwen-27b", ModelRoutingHost.Route(local, request.Kind, request.Write, 0, _ => true)!.Chosen.ModelId);
        Assert.Equal("claude-x", ModelRoutingHost.Route(frontier, request.Kind, request.Write, 0, _ => true)!.Chosen.ModelId);
    }

    [Fact]
    public void Writing_task_skips_a_model_without_write_policy_and_local_is_detected_by_address()
    {
        var loaded = Load("routing:\n  preferLocal: true\n  implementation: [frontier, worker]\n");
        var decision = ModelRoutingHost.Route(loaded, RoutingTaskKind.Implementation, true, 0, m => m.Id != "qwen-27b")!;

        Assert.Equal("claude-x", decision.Chosen.ModelId);
        Assert.Contains(decision.Rejected, r => r.ModelId == "qwen-27b" && r.Reason == RouteRejection.NoWritePolicy);
        Assert.True(ModelRoutingHost.Candidates(loaded, _ => true).Single(c => c.ModelId == "qwen-27b").IsLocal);
        Assert.False(ModelRoutingHost.Candidates(loaded, _ => true).Single(c => c.ModelId == "claude-x").IsLocal);
    }

    [Fact]
    public void Without_routing_section_the_default_model_is_kept()
    {
        Assert.Null(ModelRoutingHost.Route(Load(""), RoutingTaskKind.Exploration, false, 0, _ => true));
    }

    [Fact]
    public void Unknown_alias_and_invalid_escalation_mode_are_typed_config_errors()
    {
        var unknown = Assert.Throws<ConfigValidationException>(() => Load("routing:\n  exploration: [nope]\n"));
        Assert.Contains(unknown.Diagnostics, d => d.KeyPath == "routing.exploration[0]" && d.Message.Key == "config.unknownModelAlias");
        var mode = Assert.Throws<ConfigValidationException>(() => Load("routing:\n  escalation: { mode: sometimes, chain: [frontier] }\n"));
        Assert.Contains(mode.Diagnostics, d => d.KeyPath == "routing.escalation.mode");
    }

    [Fact]
    public void Escalation_picks_the_next_chain_model_that_fits_and_deny_disables_it()
    {
        var auto = Load("routing:\n  escalation: { mode: auto, chain: [worker, frontier] }\n");
        var next = ModelRoutingHost.NextEscalation(auto, "qwen-27b", false, 40_000, _ => true);
        Assert.Equal("claude-x", next!.ModelId);
        Assert.Equal("auto", ModelRoutingHost.EscalationMode(auto));

        Assert.Null(ModelRoutingHost.NextEscalation(auto, "qwen-27b", false, 500_000, _ => true)); // nadie tiene tanto contexto
        var deny = Load("routing:\n  escalation: { mode: deny, chain: [frontier] }\n");
        Assert.Null(ModelRoutingHost.NextEscalation(deny, "qwen-27b", false, 40_000, _ => true));
        Assert.Equal("ask", ModelRoutingHost.EscalationMode(Load("routing:\n  escalation: { chain: [frontier] }\n")));
    }

    [Fact]
    public void Escalation_events_are_accepted_by_the_journal_and_replay_cleanly()
    {
        var store = new InMemoryEventStore();
        var session = SessionId.New();
        var opened = TestRun.Open(store, session);
        var stream = new EventStream(store, EventCodecs.Create(), session);

        stream.Append(new ModelEscalationRequested(opened.RunId, "qwen-27b", "claude-x", EscalationCause.ContextLimit));
        stream.Append(new ModelEscalationApproved(opened.RunId, "claude-x", "policy:auto"));
        stream.Append(new ModelEscalationCompleted(opened.RunId, "claude-x"));

        var decoded = store.ReadFrom(session, 1).Select(e => EventCodecs.Create().Decode(e)).ToArray();
        Assert.Contains(decoded, e => e is ModelEscalationRequested { Cause: EscalationCause.ContextLimit });
        CanonicalStateTracker.Replay(EventCodecs.Create(), store.ReadFrom(session, 1));
    }
}
