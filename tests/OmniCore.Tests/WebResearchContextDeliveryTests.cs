namespace OmniCore.Tests;

using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Security;
using OmniCore.Tools;

public sealed class WebResearchContextDeliveryTests
{
    private const string Marker = "WEB_EVIDENCE_ONE_DELIVERY_7812";

    [Fact]
    public void Real_explorer_delivers_each_body_once_then_suppresses_history_after_compaction_and_config_removal()
    {
        using var fixture = new Fixture();
        var step = 0;
        var requests = new List<ModelRequest>();
        var meta = new CaptureMeta();
        var turn = fixture.Turn(request =>
        {
            requests.Add(request);
            return ++step switch
            {
                1 => Tool("search.web", "{\"query\":\"release\",\"targetedGap\":\"q1\"}"),
                2 => Tool("web.read", fixture.ReadArguments()),
                3 => Tool("web.read", fixture.AssessArguments()),
                _ => Answer(),
            };
        }, meta);
        var outcome = fixture.Ask(turn);
        Assert.True(outcome.StopReason == StopReason.EndTurn, outcome.FailureDiagnostic + " | " + outcome.FinalText
            + " | " + string.Join(";", outcome.ToolCalls.Select(call => call.ToString())));
        Assert.Equal(1, requests.Count(request => Text(request).Contains(Marker, StringComparison.Ordinal)));
        Assert.Contains(Marker, Text(requests[2]), StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, Text(requests[3]), StringComparison.Ordinal);
        Assert.All(requests, request => Assert.DoesNotContain(Marker, request.Instructions ?? "", StringComparison.Ordinal));
        Assert.Equal(3, fixture.Controller.State.DeliveredToolCalls.Count);
        var before = fixture.Controller.State.EvidenceTokensUsed + fixture.Controller.State.CachedContextTokensUsed;

        // A fresh agent/runtime with no web config still loads the durable delivery guard.
        var restored = fixture.Turn(request => { requests.Add(request); return Answer(); }, meta,
            WebResearchHost.ContextGuard(fixture.Root, controller: null), compact: true);
        Assert.Equal(StopReason.EndTurn, fixture.Ask(restored).StopReason);
        Assert.Equal(StopReason.EndTurn, fixture.Ask(restored).StopReason);
        Assert.DoesNotContain(Marker, Text(requests[^1]), StringComparison.Ordinal);
        Assert.True(meta.Inputs.Count > 0);
        Assert.All(meta.Inputs, input => Assert.DoesNotContain(Marker, input, StringComparison.Ordinal));
        Assert.Equal(before, fixture.Controller.State.EvidenceTokensUsed + fixture.Controller.State.CachedContextTokensUsed);
        Assert.Equal(1, fixture.Provider.Calls);
        Assert.Equal(1, fixture.Reader.Calls);
        Assert.All(requests.SelectMany(request => request.Messages).SelectMany(message => message.Content)
            .OfType<ToolResultBlock>(), result => Assert.NotNull(result.Id));
    }

    [Fact]
    public void New_read_identity_can_explicitly_recover_cached_evidence_and_is_charged()
    {
        using var fixture = new Fixture();
        var step = 0;
        var exposed = 0;
        var turn = fixture.Turn(request =>
        {
            if (Text(request).Contains(Marker, StringComparison.Ordinal)) exposed++;
            return ++step switch
            {
                1 => Tool("search.web", "{\"query\":\"release\",\"targetedGap\":\"q1\"}"),
                2 or 3 => Tool("web.read", fixture.ReadArguments()),
                _ => Answer(),
            };
        });
        var outcome = fixture.Ask(turn);
        Assert.True(outcome.StopReason == StopReason.EndTurn, outcome.FailureDiagnostic + " | " + outcome.FinalText
            + " | " + string.Join(";", outcome.ToolCalls.Select(call => call.ToString())));
        Assert.Equal(2, exposed);
        Assert.Equal(1, fixture.Reader.Calls);
        Assert.True(fixture.Controller.State.CachedContextTokensUsed > 0);
        Assert.Equal(3, fixture.Controller.State.DeliveredToolCalls.Count);
    }

    [Fact]
    public void Repeated_terminal_calls_deliver_zero_web_text_to_the_real_model_path()
    {
        using var fixture = new Fixture();
        fixture.Research.Update(fixture.Run.RunId.ToString(), state =>
        {
            state.Status = WebResearchStatus.BudgetExhausted;
            state.TerminalReason = "shared_run_budget_exhausted";
            state.EvidenceTokensUsed = state.Limits.EvidenceTokens;
        });
        var step = 0;
        var requests = new List<ModelRequest>();
        var turn = fixture.Turn(request =>
        {
            requests.Add(request);
            return ++step <= 3 ? Tool("web.read", "{\"action\":\"status\"}") : Answer();
        });
        Assert.Equal(StopReason.EndTurn, fixture.Ask(turn).StopReason);
        var results = requests.SelectMany(request => request.Messages).SelectMany(message => message.Content).OfType<ToolResultBlock>().ToArray();
        Assert.NotEmpty(results);
        Assert.All(results, result => Assert.Equal("", Assert.IsType<TextBlock>(Assert.Single(result.Content)).Text));
        Assert.Empty(fixture.Controller.State.ContextDeliveries);
        Assert.Empty(fixture.Controller.State.Attempts);
        Assert.Equal(0, fixture.Provider.Calls);
        Assert.Equal(0, fixture.Reader.Calls);
        Assert.Equal(WebResearchStatus.BudgetExhausted, fixture.Controller.State.Status);
        Assert.Equal(fixture.Controller.State.Limits.EvidenceTokens, fixture.Controller.State.EvidenceTokensUsed);
    }

    [Fact]
    public void Two_real_child_lanes_share_the_controller_and_pay_for_explicit_cached_recovery()
    {
        using var fixture = new Fixture();
        var childA = fixture.Child();
        var childB = fixture.Child();
        var step = 0;
        var first = fixture.Turn(_ => ++step switch
        {
            1 => Tool("search.web", "{\"query\":\"release\",\"targetedGap\":\"q1\"}"),
            2 => Tool("web.read", fixture.ReadArguments()),
            _ => Answer(),
        });
        var resultA = fixture.Ask(first, childA);
        Assert.True(resultA.StopReason == StopReason.EndTurn, resultA.FailureDiagnostic + resultA.FinalText);
        var before = fixture.Controller.State.CachedContextTokensUsed;
        step = 0;
        var requests = new List<ModelRequest>();
        var second = fixture.Turn(request =>
        {
            requests.Add(request);
            return ++step == 1 ? Tool("web.read", fixture.ReadArguments()) : Answer();
        }, guard: WebResearchHost.ContextGuard(fixture.Root, null));
        var resultB = fixture.Ask(second, childB);
        Assert.True(resultB.StopReason == StopReason.EndTurn, resultB.FailureDiagnostic + resultB.FinalText);
        Assert.Equal(1, requests.Count(request => Text(request).Contains(Marker, StringComparison.Ordinal)));
        Assert.True(fixture.Controller.State.CachedContextTokensUsed > before);
        Assert.Equal(1, fixture.Controller.State.QueriesUsed);
        Assert.Equal(1, fixture.Controller.State.ReadsUsed);
        Assert.Equal(1, fixture.Provider.Calls);
        Assert.Equal(1, fixture.Reader.Calls);
        Assert.Equal(3, fixture.Controller.State.DeliveredToolCalls.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task Two_agent_projections_compete_for_one_durable_delivery_and_duplicates_are_empty()
    {
        using var fixture = new Fixture();
        var call = ToolCallId.New();
        var reply = await fixture.Controller.SearchAsync(call.ToString(), new("release", "q1", 1, [], false,
            new Dictionary<string, string>()), cancellationToken: TestContext.Current.CancellationToken);
        using (ExecutionScope.Begin(new(fixture.Run.RunId, fixture.Run.RootTask, fixture.Run.RootLane)))
            fixture.Stream.Append(new ToolCallRequested(call, "fixture", "search.web", "{}"));
        var body = JsonSerializer.Serialize(reply, WebResearchJson.Default.WebResearchReply);
        var result = new ToolResultBlock(call, [new TextBlock(body)], false);
        var request = Request([new(MessageRole.Tool, [result, result])]);
        var first = new WebResearchContextGuard(fixture.Research);
        var second = WebResearchHost.ContextGuard(fixture.Root, null);
        var projectionA = first.Project(fixture.Run.RunId.ToString(), fixture.Journal.ReadFrom(fixture.Session, 1), fixture.Codecs);
        var projectionB = second.Project(fixture.Run.RunId.ToString(), fixture.Journal.ReadFrom(fixture.Session, 1), fixture.Codecs);
        var outcomes = await System.Threading.Tasks.Task.WhenAll(
            System.Threading.Tasks.Task.Run(() => first.Admit(fixture.Run.RunId.ToString(), "child-a", request, projectionA), TestContext.Current.CancellationToken),
            System.Threading.Tasks.Task.Run(() => second.Admit(fixture.Run.RunId.ToString(), "child-b", request, projectionB), TestContext.Current.CancellationToken));
        Assert.Equal(1, outcomes.SelectMany(outcome => outcome.Request.Messages).SelectMany(message => message.Content)
            .OfType<ToolResultBlock>().Count(block => Assert.IsType<TextBlock>(Assert.Single(block.Content)).Text.Length != 0));
        Assert.Single(fixture.Controller.State.DeliveredToolCalls);
        Assert.Single(fixture.Controller.State.ContextDeliveries);
        Assert.Equal(1, fixture.Provider.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Physical_generation_retry_is_charged_or_withheld_before_http_dispatch(bool enoughContext)
    {
        using var fixture = new Fixture(enoughContext ? 100000 : 2048);
        using var handler = new RetryHandler();
        var options = new OpenAiProviderOptions { MaxRetries = 1, DelayAsync = static (_, _) => ValueTask.CompletedTask };
        var resilience = new ProviderResilience(options, "offline-model");
        var step = 0;
        var turn = fixture.Turn(_ =>
        {
            if (++step == 1) return Tool("search.web", "{\"query\":\"release\",\"targetedGap\":\"q1\",\"contextTokenCeiling\":2048}");
            var sent = resilience.SendWithRetryAsync(() => new HttpClient(handler, false),
                () => new HttpRequestMessage(HttpMethod.Post, "https://offline.invalid/generate"),
                (_, _) => new ModelProviderException("fixture", "fixture"), status => status == 429,
                TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            sent.Response.Dispose(); sent.Client.Dispose();
            return Answer();
        });
        var outcome = fixture.Ask(turn);
        Assert.True(outcome.StopReason == (enoughContext ? StopReason.EndTurn : StopReason.Error),
            outcome.FailureDiagnostic + " | " + outcome.FinalText + " | querycalls=" + fixture.Provider.Calls
            + " | " + string.Join(";", outcome.ToolCalls.Select(call => call.ToString())));
        Assert.Equal(enoughContext ? 2 : 1, handler.Calls);
        Assert.Equal(enoughContext ? 1 : 0, fixture.Controller.State.ContextDeliveries.Count(delivery => delivery.PhysicalRetry));
        if (enoughContext) Assert.True(fixture.Controller.State.CachedContextTokensUsed > 0);
        Assert.True(fixture.Controller.State.EvidenceTokensUsed + fixture.Controller.State.CachedContextTokensUsed
            <= fixture.Controller.State.Limits.EvidenceTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task First_physical_send_revalidates_deadline_and_concurrent_terminal(bool closeRun)
    {
        using var fixture = new Fixture();
        var request = await fixture.FreshRequest();
        var clock = new MutableClock();
        var guard = new WebResearchContextGuard(fixture.Research, clock);
        var invocation = guard.Admit(fixture.Run.RunId.ToString(), "initial-send", request,
            guard.Project(fixture.Run.RunId.ToString(), fixture.Journal.ReadFrom(fixture.Session, 1), fixture.Codecs));
        Assert.NotNull(invocation.BeforePhysicalSend);
        if (closeRun) fixture.Research.Update(fixture.Run.RunId.ToString(), state =>
        { state.Status = WebResearchStatus.SufficientEvidence; state.TerminalReason = "concurrent_close"; });
        else clock.Advance();
        using var handler = new RetryHandler();
        var resilience = new ProviderResilience(new() { MaxRetries = 0 }, "offline-model");
        using var sends = GenerationRequestAttemptScope.Enter(invocation.BeforePhysicalSend);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resilience.SendWithRetryAsync(
            () => new HttpClient(handler, false), () => new HttpRequestMessage(HttpMethod.Post, "https://offline.invalid/generate"),
            (_, _) => new ModelProviderException("fixture", "fixture"), _ => false, TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, sends.ObservedSends);
    }

    [Theory]
    [InlineData(WebResearchStatus.SufficientEvidence)]
    [InlineData(WebResearchStatus.NoNewEvidence)]
    [InlineData(WebResearchStatus.SourcesUnavailable)]
    [InlineData(WebResearchStatus.BudgetExhausted)]
    public async System.Threading.Tasks.Task Pending_original_receipt_does_not_bypass_any_terminal_state(WebResearchStatus terminal)
    {
        using var fixture = new Fixture();
        var request = await fixture.FreshRequest();
        fixture.Research.Update(fixture.Run.RunId.ToString(), state => { state.Status = terminal; state.TerminalReason = "closed"; });
        var guard = WebResearchHost.ContextGuard(fixture.Root, null);
        var invocation = guard.Admit(fixture.Run.RunId.ToString(), "after-close", request,
            guard.Project(fixture.Run.RunId.ToString(), fixture.Journal.ReadFrom(fixture.Session, 1), fixture.Codecs));
        Assert.All(invocation.Request.Messages.SelectMany(message => message.Content).OfType<ToolResultBlock>(),
            result => Assert.Equal("", Assert.IsType<TextBlock>(Assert.Single(result.Content)).Text));
        Assert.Empty(fixture.Controller.State.ContextDeliveries);
        Assert.Equal(terminal, fixture.Controller.State.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_context_and_session_checkpoints_cannot_reintroduce_web_evidence(bool manual)
    {
        using var fixture = new Fixture();
        var step = 0;
        var initial = fixture.Turn(_ => ++step == 1
            ? Tool("search.web", "{\"query\":\"release\",\"targetedGap\":\"q1\"}") : Answer());
        Assert.Equal(StopReason.EndTurn, fixture.Ask(initial).StopReason);
        fixture.UnsafeCheckpoint(manual);
        var meta = new CaptureMeta();
        var requests = new List<ModelRequest>();
        var recovered = fixture.Turn(request => { requests.Add(request); return Answer(); }, meta,
            WebResearchHost.ContextGuard(fixture.Root, null), compact: true);
        var result = fixture.Ask(recovered);
        Assert.True(result.StopReason == StopReason.EndTurn, result.FailureDiagnostic + result.FinalText);
        Assert.All(requests, request => Assert.DoesNotContain(Marker, Text(request), StringComparison.Ordinal));
        Assert.All(meta.Inputs, input => Assert.DoesNotContain(Marker, input, StringComparison.Ordinal));
    }

    private static ModelRequest Request(IReadOnlyList<ModelMessage> messages) => new(
        new(new("fixture"), 30000, ToolMode.Direct, null), messages, "system", [], ToolChoice.Auto(), null, null, null, null);
    private static ModelResponse Tool(string name, string arguments) => new([new ToolCallBlock(ToolCallId.New(), null, name, arguments)],
        StopReason.ToolUse, new(10, 2, 0, 0, 0), null, new("fixture", "fixture", null));
    private static ModelResponse Answer() => new([new TextBlock("done")], StopReason.EndTurn,
        new(10, 2, 0, 0, 0), null, new("fixture", "fixture", null));
    private static string Text(ModelRequest request) => (request.Instructions ?? "") + string.Join("\n", request.Messages.Select(ExplorerTurn.RenderMessage));

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "omni-web-context-" + Guid.NewGuid().ToString("N"));
        internal InMemoryEventStore Journal { get; } = new();
        internal EventCodecs Codecs { get; } = EventCodecs.Create();
        internal SessionId Session { get; } = SessionId.New();
        internal TestRun.Opened Run { get; }
        internal EventStream Stream { get; }
        internal SqliteWebResearchStore Research { get; }
        internal WebResearchController Controller { get; }
        internal Search Provider { get; } = new();
        internal Reader Reader { get; } = new();
        private readonly FileArtifactStore _artifacts;
        private readonly FakeCatalog _catalog;
        private readonly ScriptedToolExecutor _executor;
        internal Fixture(long tokens = 100000)
        {
            Directory.CreateDirectory(Root);
            _artifacts = new(Path.Combine(Root, "blobs"));
            Run = TestRun.Open(Journal, Session, mode: RunMode.Plan);
            Stream = new(Journal, Codecs, Session);
            Research = new(Path.Combine(Root, "web-research.db"));
            Controller = new(Research, Run.RunId.ToString(), [new("q1", "Release")],
                new(8, 12, 5, tokens, 300), Reader, ["https://source.test"], Provider);
            var tools = HostTools.Explorer(); tools.RegisterWebResearchTools(Controller);
            _catalog = tools.Catalog();
            _executor = ScriptedToolExecutor.WithWorkspace(_catalog, new ScriptedPermissionPolicy(new Dictionary<string, PermissionDecision>
                { ["search.web"] = PermissionDecision.Allow, ["web.read"] = PermissionDecision.Allow }), Root);
        }
        internal string ReadArguments()
        {
            var candidate = Assert.Single(Controller.State.Candidates);
            return "{\"targetedGap\":\"q1\",\"candidateId\":\"" + candidate.Id + "\",\"uri\":\"" + candidate.Url + "\"}";
        }
        internal string AssessArguments() => "{\"action\":\"assess\",\"targetedGap\":\"q1\",\"passageId\":\""
            + Assert.Single(Controller.State.Passages).Id + "\",\"quote\":\"" + Marker + "\",\"resolvesGap\":false}";
        internal async System.Threading.Tasks.Task<ModelRequest> FreshRequest()
        {
            var call = ToolCallId.New();
            var reply = await Controller.SearchAsync(call.ToString(), new("fresh", "q1", 1, [], false,
                new Dictionary<string, string>()), cancellationToken: TestContext.Current.CancellationToken);
            using (ExecutionScope.Begin(new(Run.RunId, Run.RootTask, Run.RootLane)))
                Stream.Append(new ToolCallRequested(call, "fixture", "search.web", "{}"));
            return Request([new(MessageRole.Tool, [new ToolResultBlock(call,
                [new TextBlock(JsonSerializer.Serialize(reply, WebResearchJson.Default.WebResearchReply))], false)])]);
        }
        internal void UnsafeCheckpoint(bool manual)
        {
            var through = Journal.CurrentSequence(Session);
            var json = JsonSerializer.Serialize(new { runId = Run.RunId.ToString(), laneId = Run.RootLane.ToString(),
                throughEventSequence = through, compactedThroughItemIndex = 0, summary = Marker });
            var artifact = _artifacts.PutText(json, "application/vnd.omnicore.context-checkpoint+json", ArtifactKind.ContextSnapshot, Sensitivity.Sensitive);
            using (ExecutionScope.Begin(new(Run.RunId, Run.RootTask, Run.RootLane)))
                Stream.Append(new ContextCheckpointRecorded("unsafe", Run.RunId, through, artifact,
                    manual ? SessionConversationCheckpoint.Fingerprint : "legacy"));
        }
        internal ExplorerTurn Turn(Func<ModelRequest, ModelResponse> complete, IModelProvider? meta = null,
            WebResearchContextGuard? guard = null, bool compact = false) => new((request, _) => complete(request), _executor, _catalog,
            new(new FakeTokenCounter(), Array.Empty<IContextContributor>()), new("model", "harness", "tools", "policy", "overrides", "test"),
            new(new("fixture"), 30000, ToolMode.Direct, null), Journal, Codecs, _artifacts, new InMemoryAuditSink(), new RedactionPolicy(),
            new HarnessPolicy(ToolCallFormat.Native, ToolMode.Direct, 32, GuidanceLevel.Off, 1, PlanControl.RuntimeDriven, 4,
                new(10000, 1200, 0, compact ? 1 : 24, 2000)), metaModelProvider: meta,
            webResearchContext: guard ?? new(Research));
        internal LaneId Child()
        {
            var task = TaskId.New(); var lane = LaneId.New();
            Stream.AppendBatch([
                new TaskCreated(task, Run.RunId, "child research", [], new(null, null, null, null), Run.RootTask),
                new TaskReady(task), new LaneCreated(lane, task, ProfileId.New()), new LaneStarted(lane), new TaskStarted(task, lane),
            ], DurabilityClass.Standard);
            return lane;
        }
        internal ExplorerTurn.TurnResult Ask(ExplorerTurn turn, LaneId? lane = null) => turn.Ask("Research the release", "Research safely.", Session,
            Run.RunId, lane ?? Run.RootLane, "working state", TestContext.Current.CancellationToken);
        public void Dispose() { Controller.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class Search : IWebSearchProvider
    {
        public string Id => "fixture";
        public Uri Endpoint { get; } = new("https://search.test/search");
        internal int Calls { get; private set; }
        public System.Threading.Tasks.Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken)
        { Calls++; return System.Threading.Tasks.Task.FromResult(new WebSearchResponse([new("https://source.test/doc", "Release", "candidate", Id)], [], [])); }
    }
    private sealed class Reader : IWebReadTransport
    {
        internal int Calls { get; private set; }
        public System.Threading.Tasks.Task<WebReadResponse> ReadAsync(WebReadRequest request, CancellationToken cancellationToken)
        { Calls++; return System.Threading.Tasks.Task.FromResult(new WebReadResponse(request.Url.AbsoluteUri, request.Url.AbsoluteUri,
            Marker, "fixture", DateTimeOffset.UtcNow, [], false)); }
    }
    private sealed class CaptureMeta : IModelProvider
    {
        public ProviderCapabilities Capabilities { get; } = ProviderCapabilities.Local();
        internal List<string> Inputs { get; } = [];
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Inputs.Add(Text(request));
            await System.Threading.Tasks.Task.CompletedTask;
            yield return new ResponseCompleted(Answer());
        }
    }
    private sealed class RetryHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(++Calls == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
                { Content = new StringContent("{}") });
    }
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance() => _now = _now.AddHours(1);
    }
}
