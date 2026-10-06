using System.Runtime.CompilerServices;
using System.IO;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Protocol;
using OmniCore.Security;
using OmniCoder.OmniCore;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "omni-observability-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new SqliteEventStore(Path.Combine(root, "journal.db"));
        var artifacts = OmniHost.CreateArtifactStore(root);
        var codecs = EventCodecs.Create();
        var server = new OmniServer(store, codecs, new FileAuditSink(root), Path.Combine(root, "lastsession.txt"), artifacts);
        var created = server.Send(WireEnvelope.Command(Ids.NewV7(), "{\"cmd\":\"act\",\"objective\":\"observability integration probe\"}"), CancellationToken.None);
        Require(created.Status == "ok", "persistent session created");
        var session = server.LastSessionId()!;
        var vm = new SessionMetricsViewModel(server);
        vm.ActivateSession(session.ToString());
        var panel = new SessionMetricsPanel { DataContext = vm };
        if (args.Contains("--accounts"))
        {
            Console.WriteLine("AUTHENTICATED-READ-ONLY: official CLI quota queries; no model inference or token-consumption validation.");
            var service = new SubscriptionQuotaService();
            foreach (var providerId in new[] { "chatgpt", "claude" })
            {
                var quota = service.QueryAsync(providerId).GetAwaiter().GetResult();
                server.Observability.SetQuota(session, quota);
                vm.Refresh();
                Require(vm.Snapshot!.Quotas.Any(q => q.ProviderId == providerId && q.AsOf == quota.AsOf), "account query result reaches real OmniCoder view model: " + providerId);
                Require(vm.Details.Any(d => d.StartsWith(providerId + " ·", StringComparison.Ordinal)), "quota remains visible without a context measurement: " + providerId);
            }
            Console.WriteLine(ObservabilityJson.Encode(vm.Snapshot!));
            Console.WriteLine("Evidence directory: " + root);
            return 0;
        }
        Console.WriteLine("FIXTURE-INTEGRATION: production Host + SQLite + ExplorerTurn + Protocol JSON + net8 OmniCoder WPF assembly; provider stream is scripted, NOT real consumption.");
        var lane = store.ReadFrom(session, 1).Select(codecs.Decode).OfType<LaneCreated>().Last().LaneId;
        var tools = OmniHost.CreateExplorerTools();
        var executor = OmniHost.CreateExplorerExecutor(tools.Catalog(), root, null, null, server.LastRunId()!);
        var selection = new ModelSelection(new ModelIdValue("fixture-sol"), 8192, ToolMode.Direct, null);
        var provider = new StreamFixture(() =>
        {
            vm.Refresh();
            Require(vm.Reasoning && !vm.AnswerText, "reasoning does not activate answer-text indicator");
            Require(vm.Snapshot!.Activities.Last().FirstAnswerTextReceived == false, "reasoning is not first answer text");
        }, () => { vm.Refresh(); Require(vm.AnswerText, "answer-text generation reaches OmniCoder"); });
        var turn = new ExplorerTurn((request, ct) => server.Observability.Complete(session, provider, request, 16000, ct),
            executor, tools.Catalog(), new ContextMaterializer(OmniHost.CreateTokenCounter(null, null, null), []),
            new ExecutionFingerprint("fixture/sol", "test", "test", "test", "test", "probe"), selection,
            store, codecs, artifacts, new FileAuditSink(root), new RedactionPolicy(), pricing: new ModelPricing(1m, 2m), modelContextCapacity: 16000);
        var result = turn.Ask("show inline answer", "Read only", session, server.LastRunId()!, lane, "", CancellationToken.None);
        if (result.StopReason != StopReason.EndTurn)
            Console.WriteLine("Fixture lifecycle failure: " + string.Join("; ", store.ReadFrom(session, 1).Select(codecs.Decode).OfType<TurnAbandoned>().Select(e => e.Reason)));
        Require(result.StopReason == StopReason.EndTurn, "real Explorer lifecycle completed");
        vm.Refresh();
        Require(!vm.Reasoning && !vm.AnswerText && !vm.WaitingForFirstAnswer, "terminal event removes all indicators");
        Require(vm.Snapshot!.Context!.Tokens.Value == 100 && vm.Snapshot.Consumption.Total.Value == 120, "current context and cumulative spending are separate");
        var first = vm.Snapshot.Consumption; vm.Refresh();
        Require(vm.Snapshot!.Consumption == first, "snapshot polling is idempotent");
        var window = new System.Windows.Window { Content = panel, Width = 380, Height = 420 };
        window.Measure(new System.Windows.Size(380, 420)); window.Arrange(new System.Windows.Rect(0, 0, 380, 420)); window.UpdateLayout();
        var dispatcherFrame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => dispatcherFrame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(dispatcherFrame);
        Require(panel.DataContext == vm && panel.FindName("PART_Essential") is System.Windows.Controls.TextBlock label && label.Text.Contains("Contexto:"), "actual WPF panel binding rendered essential summary");
        vm.ActivateSession("different-session"); vm.Refresh();
        Require(vm.Snapshot is null && !vm.AnswerText && !vm.Reasoning, "session switch rejects previous session metrics and indicators");
        Console.WriteLine("PASS: Host → IOmniClient query → Protocol JSON → OmniCoder net8 ViewModel → WPF binding, no Git mutation.");
        Console.WriteLine("Evidence directory: " + root);
        return 0;
    }
    private static void Require(bool condition, string name)
    { if (!condition) throw new InvalidOperationException("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    private sealed class StreamFixture(Action reasoning, Action text) : IModelProvider
    {
        public ProviderCapabilities Capabilities => new(true, false, false);
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new ResponseStarted(0);
            yield return new ReasoningDelta(0, "fixture reasoning"); reasoning();
            await System.Threading.Tasks.Task.CompletedTask; ct.ThrowIfCancellationRequested();
            yield return new TextDelta(1, "fixture answer"); text();
            yield return new ResponseCompleted(new ModelResponse([new TextBlock("fixture answer")], StopReason.EndTurn,
                new TokenUsage(100, 20, 30, 0, 10), null, new ProviderMetadata("fixture-request", "fixture-sol", null)));
        }
    }
}
