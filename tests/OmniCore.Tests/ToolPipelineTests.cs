using OmniCore.Abstractions;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

namespace OmniCore.Tests;

public sealed class ToolPipelineTests
{
    private sealed class EventSink
    {
        public readonly List<OmniCore.Domain.DomainEventPayload> Events = new();

        public VoidBox Emit(OmniCore.Domain.DomainEventPayload payload)
        {
            Events.Add(payload);
            return VoidBox.Instance;
        }

        public int Count(string eventName)
        {
            var n = 0;
            foreach (var e in Events)
            {
                if (e.Type().ToString() == eventName)
                {
                    n += 1;
                }
            }

            return n;
        }
    }

    [Fact]
    public async Task Allow_authorizes_and_executes()
    {
        var sink = new EventSink();
        var policy = ScriptedPermissionPolicy.WithTool("fake.write", OmniCore.Domain.PermissionDecision.Allow);
        var runtime = ToolRuntime.For(FakeCatalog.Default(), policy, sink.Emit);

        var outcome = runtime.Run(
            new ValidatedToolCall(OmniCore.Domain.ToolCallId.New(), new ToolId("fake.write"), "pc-1", "{}"),
            new ToolPreparationContext("sim", DateTimeOffset.Now),
            new ToolExecutionContext("sim"), true, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.Equal(OmniCore.Domain.ToolCallState.Succeeded, outcome.FinalState);
        Assert.Equal(OmniCore.Domain.EffectOutcome.Applied, outcome.Effect);
        Assert.True(sink.Count("toolcall.permission_evaluated") == 1);
        Assert.True(sink.Count("toolcall.authorized") == 1, "Allow → autorizado");
        Assert.True(sink.Count("toolcall.started") == 1, "Efecto → started");
        Assert.True(sink.Count("toolcall.succeeded") == 1, "Ejecutó y terminó ok");
    }

    [Fact]
    public async Task Deny_never_starts_execution()
    {
        var sink = new EventSink();
        var policy = ScriptedPermissionPolicy.WithTool("fake.write", OmniCore.Domain.PermissionDecision.Deny);
        var runtime = ToolRuntime.For(FakeCatalog.Default(), policy, sink.Emit);

        var outcome = runtime.Run(
            new ValidatedToolCall(OmniCore.Domain.ToolCallId.New(), new ToolId("fake.write"), "pc-2", "{}"),
            new ToolPreparationContext("sim", DateTimeOffset.Now),
            new ToolExecutionContext("sim"), false, TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Equal(OmniCore.Domain.ToolCallState.Rejected, outcome.FinalState);
        Assert.True(sink.Count("toolcall.permission_evaluated") == 1);
        Assert.True(sink.Count("toolcall.permission_denied") == 1);
        Assert.True(sink.Count("toolcall.authorized") == 0, "Nunca se autoriza en Deny");
        Assert.True(sink.Count("toolcall.started") == 0, "Nunca arranca en Deny");
    }

    [Fact]
    public async Task Ask_without_user_approval_denies()
    {
        var sink = new EventSink();
        var policy = ScriptedPermissionPolicy.WithTool("fake.write", OmniCore.Domain.PermissionDecision.Ask);
        var runtime = ToolRuntime.For(FakeCatalog.Default(), policy, sink.Emit);

        var outcome = runtime.Run(
            new ValidatedToolCall(OmniCore.Domain.ToolCallId.New(), new ToolId("fake.write"), "pc-3", "{}"),
            new ToolPreparationContext("sim", DateTimeOffset.Now),
            new ToolExecutionContext("sim"), false, TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.True(sink.Count("toolcall.permission_requested") == 1, "Ask abre interacción");
        Assert.True(sink.Count("toolcall.permission_denied") == 1, "Sin cliente → Deny (ADR-0003)");
    }

    [Fact]
    public async Task Prepare_is_pure_and_deterministic()
    {
        // ADR-0014 §3: Prepare síncrono y puro; dos llamadas producen el mismo intent.
        var tool = FakeTool.Write("fake.write");
        var ctx = new ToolPreparationContext("sim", DateTimeOffset.Now);
        var call = new ValidatedToolCall(OmniCore.Domain.ToolCallId.New(), new ToolId("fake.write"), "pc-1",
            "{\"path\":\"a.txt\"}");

        var first = tool.Prepare(call, ctx);
        var second = tool.Prepare(call, ctx);

        Assert.True(first is Prepared, "Prepare debe aceptar la llamada válida");
        var a = ((Prepared) first).Intent;
        var b = ((Prepared) second).Intent;
        Assert.Equal(a.Effect, b.Effect);
        Assert.Equal(a.NormalizedArgumentsJson, b.NormalizedArgumentsJson);
        Assert.Equal(OmniCore.Domain.EffectClass.Reconcilable, a.Effect);
        Assert.NotNull(a.Reconciliation);
    }

    [Fact]
    public async Task Unknown_tool_is_rejected()
    {
        var sink = new EventSink();
        var policy = ScriptedPermissionPolicy.WithTool("nope", OmniCore.Domain.PermissionDecision.Allow);
        var runtime = ToolRuntime.For(FakeCatalog.Default(), policy, sink.Emit);

        var outcome = runtime.Run(
            new ValidatedToolCall(OmniCore.Domain.ToolCallId.New(), new ToolId("nope"), "pc-4", "{}"),
            new ToolPreparationContext("sim", DateTimeOffset.Now),
            new ToolExecutionContext("sim"), true, TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Equal(OmniCore.Domain.ToolCallState.Rejected, outcome.FinalState);
        Assert.True(sink.Count("toolcall.rejected") == 1);
    }

    [Fact]
    public async Task Reconciliation_marks_effect_unknown_then_not_applied_when_post_hash_absent()
    {
        // ADR-0004 §4: una ToolCall Started sin outcome se reconcilia; sin post-hash, NotApplied.
        var sink = new EventSink();
        var runtime = ToolRuntime.For(FakeCatalog.Default(),
            ScriptedPermissionPolicy.WithTool("fake.write", OmniCore.Domain.PermissionDecision.Allow), sink.Emit);
        var intent = new ToolIntent(OmniCore.Domain.ToolCallId.New(), new ToolId("fake.write"), "{}",
            OmniCore.Domain.EffectClass.Reconcilable, new OmniCore.Domain.ResourceClaims(new string[0], new string[0],
            new OmniCore.Domain.NetworkGrant[0], null, new string[0]), OmniCore.Abstractions.ToolRisk.Low,
            new ReconciliationSpec("pre", "post", "key"));

        var state = runtime.Reconcile(intent.ToolCallId, intent, post => null, TestContext.Current.CancellationToken);

        Assert.Equal(OmniCore.Domain.ToolCallState.Reconciled, state);
        Assert.True(sink.Count("toolcall.effect_unknown") == 1);
        Assert.True(sink.Count("toolcall.reconciled") == 1);
        Assert.True(ContainsReconciliation(sink, OmniCore.Domain.ReconciliationOutcome.NotApplied));
    }

    [Fact]
    public async Task Reconciliation_marks_applied_when_post_hash_matches()
    {
        var sink = new EventSink();
        var runtime = ToolRuntime.For(FakeCatalog.Default(),
            ScriptedPermissionPolicy.WithTool("fake.write", OmniCore.Domain.PermissionDecision.Allow), sink.Emit);
        var intent = new ToolIntent(OmniCore.Domain.ToolCallId.New(), new ToolId("fake.write"), "{}",
            OmniCore.Domain.EffectClass.Reconcilable, new OmniCore.Domain.ResourceClaims(new string[0], new string[0],
            new OmniCore.Domain.NetworkGrant[0], null, new string[0]), OmniCore.Abstractions.ToolRisk.Low,
            new ReconciliationSpec("pre", "post", "key"));

        var state = runtime.Reconcile(intent.ToolCallId, intent, post => "post", TestContext.Current.CancellationToken);

        Assert.Equal(OmniCore.Domain.ToolCallState.Reconciled, state);
        Assert.True(ContainsReconciliation(sink, OmniCore.Domain.ReconciliationOutcome.Applied));
    }

    private static bool ContainsReconciliation(EventSink sink, OmniCore.Domain.ReconciliationOutcome wanted)
    {
        foreach (var e in sink.Events)
        {
            var reconciled = e is OmniCore.Domain.ToolCallReconciled r ? r : null;
            if (reconciled is not null && reconciled.Outcome == wanted)
            {
                return true;
            }
        }

        return false;
    }
}