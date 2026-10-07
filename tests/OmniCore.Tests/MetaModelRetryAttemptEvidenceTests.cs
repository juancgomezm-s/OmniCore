using Microsoft.Data.Sqlite;
using OmniCore.Abstractions;
using OmniCore.Context;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using OmniCore.Models;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class MetaModelRetryAttemptEvidenceTests
{
    [Fact]
    public async Task Retried_success_persists_final_usage_and_uncertain_attempt_evidence_across_reopen()
    {
        using var fixture = new Fixture(RetryableFailure, CompletedResponse);
        var provider = fixture.CreateProvider(maxRetries: 2);
        var service = fixture.CreateService(provider);
        using var scope = ExecutionScope.Begin(new ExecutionScopeState(fixture.Run.RunId,
            fixture.Run.RootTask, fixture.Run.RootLane));

        Assert.Equal("older facts preserved", await service.SummarizeAsync(fixture.Run.RunId,
            "CompressContext", "fixture context", 1000, CancellationToken.None));

        Assert.Equal(2, fixture.Handler.RequestCount);
        Assert.Equal(3L, provider.MaximumGenerationRequestAttempts);
        var completed = Assert.Single(fixture.Payloads().OfType<MetaModelInvocationCompleted>());
        Assert.Equal(new TokenUsage(17, 4, 0, 0, 0), completed.Usage);
        Assert.Equal(TokenUsageFields.Input | TokenUsageFields.Output, completed.ReportedUsageFields);
        Assert.Equal(2L, completed.GenerationAttempts!.ObservedGenerationSends);
        Assert.Equal(3L, completed.GenerationAttempts.MaximumGenerationRequestAttempts);
        Assert.False(completed.GenerationAttempts.HasCompleteUsageCoverage);
        Assert.Empty(fixture.Payloads().OfType<MetaModelInvocationNotDispatched>());

        fixture.CloseJournal();
        fixture.ReopenJournal();
        var reopened = Assert.Single(fixture.Payloads().OfType<MetaModelInvocationCompleted>());
        Assert.Equal(completed.InvocationId, reopened.InvocationId);
        Assert.Equal(completed.Usage, reopened.Usage);
        Assert.Equal(completed.ReportedUsageFields, reopened.ReportedUsageFields);
        Assert.Equal(completed.GenerationAttempts, reopened.GenerationAttempts);
        Assert.Equal(2, fixture.Handler.RequestCount); // reopening never repeats the provider call
        var budget = RunTokenBudgetReader.Read(fixture.EventsInSession(), fixture.Codecs,
            fixture.Run.RunId, limit: 100);
        Assert.Null(budget.Remaining);
        Assert.NotNull(budget.Limitation);
    }

    [Fact]
    public async Task Exhausted_http_retries_persist_unknown_usage_and_observed_sends_in_failed_terminal()
    {
        using var fixture = new Fixture(RetryableFailure, RetryableFailure, RetryableFailure);
        var provider = fixture.CreateProvider(maxRetries: 2);
        var service = fixture.CreateService(provider);
        using var scope = ExecutionScope.Begin(new ExecutionScopeState(fixture.Run.RunId,
            fixture.Run.RootTask, fixture.Run.RootLane));

        await Assert.ThrowsAsync<ModelProviderException>(() => service.SummarizeAsync(fixture.Run.RunId,
            "CompressContext", "fixture context", 1000, CancellationToken.None));

        Assert.Equal(3, fixture.Handler.RequestCount);
        var failed = Assert.Single(fixture.Payloads().OfType<MetaModelInvocationFailed>());
        Assert.Null(failed.Usage);
        Assert.Equal(TokenUsageFields.None, failed.ReportedUsageFields);
        Assert.Null(failed.CostUsd);
        Assert.Equal(3L, failed.GenerationAttempts!.ObservedGenerationSends);
        Assert.Equal(3L, failed.GenerationAttempts.MaximumGenerationRequestAttempts);
        Assert.False(failed.GenerationAttempts.HasCompleteUsageCoverage);
        Assert.Empty(fixture.Payloads().OfType<MetaModelInvocationNotDispatched>());

        fixture.CloseJournal();
        fixture.ReopenJournal();
        var reopened = Assert.Single(fixture.Payloads().OfType<MetaModelInvocationFailed>());
        Assert.Equal(failed.InvocationId, reopened.InvocationId);
        Assert.Null(reopened.Usage);
        Assert.Equal(TokenUsageFields.None, reopened.ReportedUsageFields);
        Assert.Equal(failed.GenerationAttempts, reopened.GenerationAttempts);
        Assert.Equal(3, fixture.Handler.RequestCount);
        var budget = RunTokenBudgetReader.Read(fixture.EventsInSession(), fixture.Codecs,
            fixture.Run.RunId, limit: 100);
        Assert.Null(budget.Remaining);
        Assert.NotNull(budget.Limitation);
    }

    [Fact]
    public async Task Single_observed_send_has_complete_terminal_usage_without_claiming_billing()
    {
        using var fixture = new Fixture(CompletedResponse);
        var provider = fixture.CreateProvider(maxRetries: 2);
        var service = fixture.CreateService(provider);
        using var scope = ExecutionScope.Begin(new ExecutionScopeState(fixture.Run.RunId,
            fixture.Run.RootTask, fixture.Run.RootLane));

        await service.SummarizeAsync(fixture.Run.RunId, "CompressContext", "fixture context", 1000,
            CancellationToken.None);

        Assert.Equal(1, fixture.Handler.RequestCount);
        var completed = Assert.Single(fixture.Payloads().OfType<MetaModelInvocationCompleted>());
        Assert.Equal(1L, completed.GenerationAttempts!.ObservedGenerationSends);
        Assert.Equal(3L, completed.GenerationAttempts.MaximumGenerationRequestAttempts);
        Assert.True(completed.GenerationAttempts.HasCompleteUsageCoverage);
        Assert.Equal(new TokenUsage(17, 4, 0, 0, 0), completed.Usage);
        Assert.Null(completed.CostUsd); // no charge or price is asserted by this fixture
        var budget = RunTokenBudgetReader.Read(fixture.EventsInSession(), fixture.Codecs,
            fixture.Run.RunId, limit: 100);
        Assert.Equal(79L, budget.Remaining);
        Assert.Null(budget.Limitation);
    }

    [Fact]
    public async Task Bound_of_one_can_cover_reported_usage_without_rewriting_zero_observed_sends()
    {
        using var fixture = new Fixture();
        var provider = new BoundOneUninstrumentedProvider();
        var service = fixture.CreateService(provider);
        using var scope = ExecutionScope.Begin(new ExecutionScopeState(fixture.Run.RunId,
            fixture.Run.RootTask, fixture.Run.RootLane));

        await service.SummarizeAsync(fixture.Run.RunId, "CompressContext", "fixture context", 1000,
            CancellationToken.None);

        var completed = Assert.Single(fixture.Payloads().OfType<MetaModelInvocationCompleted>());
        Assert.Equal(0L, completed.GenerationAttempts!.ObservedGenerationSends);
        Assert.Equal(1L, completed.GenerationAttempts.MaximumGenerationRequestAttempts);
        Assert.True(completed.GenerationAttempts.HasCompleteUsageCoverage);
        Assert.Equal(new TokenUsage(17, 4, 0, 0, 0), completed.Usage);
        var budget = RunTokenBudgetReader.Read(fixture.EventsInSession(), fixture.Codecs,
            fixture.Run.RunId, limit: 100);
        Assert.Equal(79L, budget.Remaining);
        Assert.Null(budget.Limitation);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "omni-meta-attempts-" + Guid.NewGuid().ToString("N"));
        private readonly string _journal;
        private SqliteEventStore? _store;
        private readonly EventCodecs _codecs = EventCodecs.Create();

        public Fixture(params Func<HttpResponseMessage>[] responses)
        {
            Directory.CreateDirectory(_root);
            _journal = Path.Combine(_root, "journal.db");
            _store = new SqliteEventStore(_journal);
            Artifacts = new FileArtifactStore(_root);
            Handler = new ScriptedHandler(responses);
            var session = SessionId.New();
            Run = TestRun.Open(_store, session, mode: RunMode.Plan);
            Events = new EventStream(_store, _codecs, session);
        }

        public FileArtifactStore Artifacts { get; }
        public ScriptedHandler Handler { get; }
        public EventStream Events { get; }
        public TestRun.Opened Run { get; }
        public EventCodecs Codecs => _codecs;

        public OpenAIResponsesProvider CreateProvider(int maxRetries) => new(
            new ProviderDescriptor("openai", ProviderFamily.OpenAIResponses,
                "https://api.example.test/v1", AuthConfig.None(), false, false, true),
            new FixtureSecrets(),
            () => new HttpClient(Handler, disposeHandler: false),
            new OpenAIResponsesOptions
            {
                Resilience = new OpenAiProviderOptions
                {
                    MaxRetries = maxRetries,
                    DelayAsync = static (_, _) => ValueTask.CompletedTask,
                },
            });

        public MetaModelService CreateService(IModelProvider provider) => new(provider, Artifacts, new JournalSink(Events),
            new ModelSelection(new ModelIdValue("gpt-attempt-fixture"), 4096, ToolMode.Direct, null));

        public DomainEventPayload[] Payloads() => _store!.ReadFrom(Run.SessionId, 1)
            .Select(_codecs.Decode).ToArray();

        public IReadOnlyList<DomainEvent> EventsInSession() => _store!.ReadFrom(Run.SessionId, 1);

        public void CloseJournal()
        {
            var store = _store;
            if (store is null) return;
            _store = null;
            var connection = (SqliteConnection)store.Connection;
            store.Close();
            SqliteConnection.ClearPool(connection);
            connection.Dispose();
        }

        public void ReopenJournal() => _store = new SqliteEventStore(_journal);

        public void Dispose()
        {
            CloseJournal();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class JournalSink(EventStream stream) : IContextEventSink
    {
        public ValueTask AppendAsync(DomainEventPayload payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Append(payload, DurabilityClass.Barrier);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixtureSecrets : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) => Secret.Of("fixture-only");
    }

    private sealed class BoundOneUninstrumentedProvider : IModelProvider, IModelRequestAttemptBound
    {
        public ProviderCapabilities Capabilities { get; } = ProviderCapabilities.Local();
        public long? MaximumGenerationRequestAttempts => 1;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ResponseCompleted(new ModelResponse(
                [new TextBlock("older facts preserved")], StopReason.EndTurn, new TokenUsage(17, 4, 0, 0, 0),
                null, new ProviderMetadata("scripted-meta", "fixture", null),
                TokenUsageFields.Input | TokenUsageFields.Output));
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;
        public int RequestCount { get; private set; }

        public ScriptedHandler(params Func<HttpResponseMessage>[] responses) => _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            if (!_responses.TryDequeue(out var response))
                throw new InvalidOperationException("The fixture received an unexpected extra HTTP request.");
            return Task.FromResult(response());
        }
    }

    private static HttpResponseMessage RetryableFailure() => new(System.Net.HttpStatusCode.ServiceUnavailable)
    {
        Content = new StringContent("{\"error\":{\"message\":\"scripted retry\"}}"),
    };

    private static HttpResponseMessage CompletedResponse() => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent("""
event: response.output_item.added
data: {"type":"response.output_item.added","output_index":0,"item":{"type":"message","role":"assistant"}}

event: response.output_text.delta
data: {"type":"response.output_text.delta","output_index":0,"delta":"older facts preserved"}

event: response.output_item.done
data: {"type":"response.output_item.done","output_index":0,"item":{"type":"message","content":[{"type":"output_text","text":"older facts preserved"}]}}

event: response.completed
data: {"type":"response.completed","response":{"id":"resp-fixture","status":"completed","usage":{"input_tokens":17,"output_tokens":4}}}

""", System.Text.Encoding.UTF8, "text/event-stream"),
    };
}
