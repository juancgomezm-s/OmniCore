namespace OmniCore.Tests;

using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;

/// <summary>Router por alias desde models.yaml y escalación explícita (M5, spec §21/§73).</summary>
public sealed class ModelRoutingHostTests
{
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
        Assert.Equal("qwen-27b", ModelRoutingHost.Route(loaded, RoutingTaskKind.Exploration, false, 0, _ => true)!.Chosen.Alias);
        Assert.Equal("claude-x", ModelRoutingHost.Route(loaded, RoutingTaskKind.Implementation, true, 0, _ => true)!.Chosen.Alias);
    }

    [Fact]
    public void Switching_from_local_worker_to_frontier_only_changes_routing_config_not_the_task()
    {
        var request = (Kind: RoutingTaskKind.Implementation, Write: true);
        var local = Load("routing:\n  implementation: [worker]\n");
        var frontier = Load("routing:\n  implementation: [frontier]\n");

        Assert.Equal("qwen-27b", ModelRoutingHost.Route(local, request.Kind, request.Write, 0, _ => true)!.Chosen.Alias);
        Assert.Equal("claude-x", ModelRoutingHost.Route(frontier, request.Kind, request.Write, 0, _ => true)!.Chosen.Alias);
    }

    [Fact]
    public void Writing_task_skips_a_model_without_write_policy_and_local_is_detected_by_address()
    {
        var loaded = Load("routing:\n  preferLocal: true\n  implementation: [frontier, worker]\n");
        var decision = ModelRoutingHost.Route(loaded, RoutingTaskKind.Implementation, true, 0, m => m.Id != "qwen-27b")!;

        Assert.Equal("claude-x", decision.Chosen.Alias);
        Assert.Contains(decision.Rejected, r => r.Alias == "qwen-27b" && r.Reason == RouteRejection.NoWritePolicy);
        Assert.True(ModelRoutingHost.Candidates(loaded, _ => true).Single(c => c.Alias == "qwen-27b").IsLocal);
        Assert.False(ModelRoutingHost.Candidates(loaded, _ => true).Single(c => c.Alias == "claude-x").IsLocal);
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
        Assert.Equal("claude-x", next!.Alias);
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
