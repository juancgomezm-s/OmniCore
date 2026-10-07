namespace OmniCore.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;
using Task = System.Threading.Tasks.Task;

public sealed class GenerationRequestAttemptScopeTests
{
    [Fact]
    public async Task Scope_is_async_local_nested_and_isolated_between_concurrent_invocations()
    {
        GenerationRequestAttemptScope.RecordGenerationSend();
        using var outer = GenerationRequestAttemptScope.Enter();
        GenerationRequestAttemptScope.RecordGenerationSend();
        Assert.Equal(1L, outer.ObservedSends);

        using (var nested = GenerationRequestAttemptScope.Enter())
        {
            GenerationRequestAttemptScope.RecordGenerationSend();
            Assert.Equal(1L, nested.ObservedSends);
            Assert.Equal(1L, outer.ObservedSends);
        }

        GenerationRequestAttemptScope.RecordGenerationSend();
        Assert.Equal(2L, outer.ObservedSends);

        var parallelCounts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            using var invocation = GenerationRequestAttemptScope.Enter();
            await Task.Yield();
            GenerationRequestAttemptScope.RecordGenerationSend();
            return invocation.ObservedSends;
        }, TestContext.Current.CancellationToken)));

        Assert.All(parallelCounts, count => Assert.Equal(1L, count));
        Assert.Equal(2L, outer.ObservedSends);
    }

    [Fact]
    public void Recording_without_a_scope_is_discarded_not_carried_into_a_later_scope()
    {
        GenerationRequestAttemptScope.RecordGenerationSend();

        using var later = GenerationRequestAttemptScope.Enter();

        Assert.Equal(0L, later.ObservedSends);
        GenerationRequestAttemptScope.RecordGenerationSend();
        Assert.Equal(1L, later.ObservedSends);
    }

    [Fact]
    public async Task Native_adapter_counts_one_send_when_retries_are_disabled()
    {
        var handler = new QueueHandler(_ => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"));
        var provider = CreateProvider(handler, new OpenAiProviderOptions
        {
            MaxRetries = 0,
            DelayAsync = (_, _) => ValueTask.CompletedTask,
        });
        using var scope = GenerationRequestAttemptScope.Enter();

        var events = await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Contains(events, item => item is ResponseCompleted);
        Assert.Equal(1, handler.Count);
        Assert.Equal(1L, scope.ObservedSends);
    }

    [Fact]
    public async Task Native_adapter_counts_each_generation_send_across_a_503_retry()
    {
        var handler = new QueueHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") },
            _ => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"));
        var provider = CreateProvider(handler, new OpenAiProviderOptions
        {
            MaxRetries = 1,
            DelayAsync = (_, _) => ValueTask.CompletedTask,
            Jitter = () => 0,
        });
        using var scope = GenerationRequestAttemptScope.Enter();

        var events = await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Contains(events, item => item is ResponseCompleted);
        Assert.Equal(2, handler.Count);
        Assert.Equal(2L, scope.ObservedSends);
    }

    [Fact]
    public async Task Native_adapter_counts_terminal_http_failure_instead_of_reporting_zero_sends()
    {
        var handler = new QueueHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid request"),
        });
        var provider = CreateProvider(handler, new OpenAiProviderOptions { MaxRetries = 2 });
        using var scope = GenerationRequestAttemptScope.Enter();

        await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));
        });

        Assert.Equal(1, handler.Count);
        Assert.Equal(1L, scope.ObservedSends);
    }

    [Fact]
    public async Task Native_adapter_counts_send_cancelled_inside_http_handler()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CancellationHandler(entered);
        var provider = CreateProvider(handler, new OpenAiProviderOptions { MaxRetries = 2 });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var scope = GenerationRequestAttemptScope.Enter();

        var consuming = Collect(provider.StreamAsync(Request(), deadline.Token));
        await entered.Task.WaitAsync(deadline.Token);
        deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await consuming.WaitAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.Count);
        Assert.Equal(1L, scope.ObservedSends);
    }

    private static OpenAiChatCompatibleProvider CreateProvider(HttpMessageHandler handler,
        OpenAiProviderOptions options) =>
        new(new ProviderDescriptor("attempt-scope-provider", ProviderFamily.OpenAiChatCompatible,
                "https://example.test/v1", AuthConfig.None(), false, false, true), new EmptySecrets(),
            () => new HttpClient(handler, disposeHandler: false), options);

    private static ModelRequest Request()
    {
        var selection = new ModelSelection(new ModelIdValue("attempt-scope-model"), 4096,
            ToolMode.Direct, null);
        return new ModelRequest(selection,
            [new ModelMessage(MessageRole.User, [new TextBlock("hello")])], null, [],
            ToolChoice.Auto(), null, null, null, null);
    }

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    private static async Task<List<ModelStreamEvent>> Collect(IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var events = new List<ModelStreamEvent>();
        await foreach (var item in stream) events.Add(item);
        return events;
    }

    private sealed class EmptySecrets : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Auth.None must not request a secret.");
    }

    private sealed class QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new(responses);
        private int _count;
        public int Count => Volatile.Read(ref _count);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            var response = _responses.TryDequeue(out var next)
                ? next(request)
                : throw new InvalidOperationException("No scripted response remains.");
            return Task.FromResult(response);
        }
    }

    private sealed class CancellationHandler(TaskCompletionSource entered) : HttpMessageHandler
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Canceled send unexpectedly completed.");
        }
    }
}
