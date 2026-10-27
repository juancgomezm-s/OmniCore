namespace OmniCore.Tests;

using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Security;
using OmniCore.Tools;

public sealed class ZeroCostModelSwitchRegressionTests
{
    [Theory]
    [InlineData("0", "0", true, false, false, 1)]
    [InlineData("0", "0", true, false, true, 1)]
    [InlineData("0", "0", true, true, false, 0)]
    [InlineData("0", "0", false, false, false, 0)]
    [InlineData("0.1", "0", true, false, false, 0)]
    [InlineData("0", "0.1", true, false, false, 0)]
    [InlineData(null, "0", true, false, false, 0)]
    [InlineData("0", null, true, false, false, 0)]
    public void Switching_from_unreported_local_usage_to_zero_cost_text_does_not_invent_spend_or_bypass_paid_or_token_limits(
        string? inputPrice, string? outputPrice, bool bounded, bool tokenLimit, bool zeroRunCap, int expectedCalls)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-zero-cost-switch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new InMemoryEventStore(); var codecs = EventCodecs.Create();
            var session = SessionId.New();
            var run = TestRun.Open(new EventStream(store, codecs, session), session,
                taskBudget: new TaskBudget(zeroRunCap ? 0m : 100m, tokenLimit ? 1L : null, null, null));
            var artifacts = new FileArtifactStore(root); var catalog = new FakeCatalog();
            var calls = 0;
            ExplorerTurn Create(BillingMode billing, ModelPricing? pricing, bool caps, bool finite) => new((_, _) =>
            {
                calls++;
                return new ModelResponse([new TextBlock("fixture response")], StopReason.EndTurn,
                    new TokenUsage(0, 0, 0, 0, 0), null, new ProviderMetadata("fixture", "fixture", null), TokenUsageFields.None);
            }, ScriptedToolExecutor.WithWorkspace(catalog,
                new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>()), root), catalog,
                new ContextMaterializer(new FakeTokenCounter(), []),
                new ExecutionFingerprint("fixture", "h", "t", "c", "o", "fixture-build"),
                new ModelSelection(new ModelIdValue("neutral-fixture-id"), 8192, ToolMode.Direct, null,
                    maxOutputTokens: finite ? 100 : null), store, codecs, artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
                pricing: pricing, enforceDefaultSpendCaps: caps, billingMode: billing, modelContextCapacity: 8192,
                maximumGenerationRequestAttempts: 1, spendReservations: new SqliteSpendReservationStore(Path.Combine(root, "reservations.db")));
            var first = Create(BillingMode.Local, null, false, true).Ask("first", "system", session,
                run.RunId, run.RootLane, "", CancellationToken.None);
            var before = store.ReadFrom(session, 1).ToArray();
            var pricing = new ModelPricing(Parse(inputPrice), Parse(outputPrice));
            var beforeCalls = calls;
            Create(BillingMode.MeteredCurrency, pricing, true, bounded).Ask("second", "system", session,
                run.RunId, run.RootLane, "", CancellationToken.None);
            Assert.Equal(expectedCalls, calls - beforeCalls);
            Assert.Equal(before.Select(evt => (evt.EventId, evt.PayloadJson)),
                store.ReadFrom(session, 1).Take(before.Length).Select(evt => (evt.EventId, evt.PayloadJson)));
            var events = store.ReadFrom(session, 1).Select(codecs.Decode).ToArray();
            Assert.All(events.OfType<ModelStepCompleted>(), step => Assert.Null(step.CostUsd));
            if (expectedCalls == 1)
            {
                Assert.Equal(StopReason.EndTurn, first.StopReason);
                Assert.DoesNotContain(events.OfType<InteractionRequested>(), request => request.Kind == InteractionKind.BudgetExceeded);
                // Paying later must still fail against the very same unknown historical usage.
                beforeCalls = calls;
                Create(BillingMode.MeteredCurrency, new ModelPricing(1m, 1m), true, true).Ask("paid", "system", session,
                    run.RunId, run.RootLane, "", CancellationToken.None);
                Assert.Equal(beforeCalls, calls);
                Assert.Contains(store.ReadFrom(session, 1).Select(codecs.Decode).OfType<InteractionRequested>(),
                    request => request.Kind == InteractionKind.BudgetExceeded);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static decimal? Parse(string? value) => value is null ? null
        : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
