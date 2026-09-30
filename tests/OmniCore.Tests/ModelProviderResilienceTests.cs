namespace OmniCore.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;

public sealed class ModelProviderResilienceTests
{
    [Fact]
    public async System.Threading.Tasks.Task Sse_streams_ordered_text_and_reassembles_split_tool_arguments()
    {
        var handler = new QueueHandler(_ => Sse("""
data: {"choices":[{"delta":{"content":"hi"},"finish_reason":null}]}

data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call-x","function":{"name":"search","arguments":"{\"q\":\""}}]},"finish_reason":null}]}

data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"x\"}"}}]},"finish_reason":null}]}

data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":7,"completion_tokens":3}}

data: [DONE]

"""));
        var provider = CreateProvider(handler);

        var events = await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        Assert.IsType<ResponseStarted>(events[0]);
        Assert.Contains(events, e => e is TextDelta { Text: "hi" });
        Assert.Contains(events, e => e is ToolArgumentsDelta { PartialJson: "{\"q\":\"" });
        var completed = Assert.IsType<ResponseCompleted>(events[^1]).Response;
        var call = Assert.Single(completed.Content.OfType<ToolCallBlock>());
        Assert.Equal("call-x", call.ProviderCallId);
        Assert.Equal("search", call.ToolName);
        Assert.Equal("{\"q\":\"x\"}", call.ArgumentsJson);
        Assert.Equal(7, completed.Usage.Input);
        Assert.Equal(StopReason.ToolUse, completed.StopReason);
    }

    [Fact]
    public async System.Threading.Tasks.Task Cancellation_during_stream_stops_enumeration()
    {
        var handler = new QueueHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"first\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"second\"}}]}\n\n", Encoding.UTF8, "text/event-stream"),
        });
        var provider = CreateProvider(handler);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var iterator = provider.StreamAsync(Request(), cts.Token).GetAsyncEnumerator(cts.Token);
        var sawText = false;
        while (await iterator.MoveNextAsync())
        {
            if (iterator.Current is TextDelta)
            {
                sawText = true;
                cts.Cancel();
                break;
            }
        }
        Assert.True(sawText);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await iterator.MoveNextAsync());
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task Opaque_response_fields_round_trip_into_the_next_same_model_request()
    {
        var parsed = OpenAiChatCompatibleProvider.ParseChatCompletion(
            "{\"model\":\"model-a\",\"choices\":[{\"message\":{\"content\":\"ok\",\"reasoning_content\":\"think\",\"vendor_trace\":{\"x\":4}},\"finish_reason\":\"stop\"}]} ");
        Assert.NotNull(parsed.State);
        var handler = new QueueHandler(req =>
        {
            req.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            return Sse("data: {\"choices\":[{\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}]}\n\n" + "data: [DONE]\n\n");
        });
        var provider = CreateProvider(handler);
        var request = Request(continuation: parsed.State);

        await Collect(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        var payload = await handler.LastBody!;
        Assert.Contains("\"reasoning_content\":\"think\"", payload);
        Assert.Contains("\"vendor_trace\":{\"x\":4}", payload);
    }

    [Fact]
    public async System.Threading.Tasks.Task Retries_503_once_then_streams_success()
    {
        var handler = new QueueHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") },
            _ => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n" + "data: [DONE]\n\n"));
        var delays = new List<TimeSpan>();
        var provider = CreateProvider(handler, new OpenAiProviderOptions
        {
            MaxRetries = 1,
            DelayAsync = (delay, _) => { delays.Add(delay); return ValueTask.CompletedTask; },
            Jitter = () => 0,
        });

        var events = await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(2, handler.Count);
        Assert.Single(delays);
        Assert.Contains(events, item => item is TextDelta { Text: "ok" });
    }

    [Fact]
    public async System.Threading.Tasks.Task Retry_After_429_honours_retry_after_without_sleeping()
    {
        var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow down") };
        rateLimited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(4));
        var handler = new QueueHandler(_ => rateLimited,
            _ => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n" + "data: [DONE]\n\n"));
        var delays = new List<TimeSpan>();
        var provider = CreateProvider(handler, new OpenAiProviderOptions
        {
            MaxRetries = 1,
            DelayAsync = (delay, _) => { delays.Add(delay); return ValueTask.CompletedTask; },
        });

        await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(TimeSpan.FromSeconds(4), Assert.Single(delays));
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task No_retry_after_first_streamed_token()
    {
        var handler = new QueueHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ThrowAfterFirstReadContent(Encoding.UTF8.GetBytes(
                "data: {\"choices\":[{\"delta\":{\"content\":\"visible\"}}]}\n\n")),
        }, _ => Sse("data: [DONE]\n\n"));
        var provider = CreateProvider(handler, new OpenAiProviderOptions
        {
            MaxRetries = 5,
            DelayAsync = (_, _) => ValueTask.CompletedTask,
        });
        var sawToken = false;

        await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var item in provider.StreamAsync(Request(), TestContext.Current.CancellationToken))
                if (item is TextDelta) sawToken = true;
        });

        Assert.True(sawToken);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task Breaker_opens_at_configured_threshold_and_half_open_probe_after_cooldown()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var handler = new QueueHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"recovered\"},\"finish_reason\":\"stop\"}]}\n\n" + "data: [DONE]\n\n"));
        var provider = CreateProvider(handler, new OpenAiProviderOptions
        {
            MaxRetries = 0,
            CircuitFailureThreshold = 2,
            CircuitCooldown = TimeSpan.FromMinutes(1),
            UtcNow = () => now,
            DelayAsync = (_, _) => ValueTask.CompletedTask,
        });

        await Assert.ThrowsAsync<ModelProviderException>(() => Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));
        Assert.Equal(1, handler.Count); // Threshold=2: one failure must not open circuit.
        await Assert.ThrowsAsync<ModelProviderException>(() => Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));
        Assert.Equal(2, handler.Count);
        await Assert.ThrowsAsync<ModelProviderException>(() => Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));
        Assert.Equal(2, handler.Count); // Fast rejection while circuit remains open.
        now += TimeSpan.FromMinutes(1);
        var recovered = await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Contains(recovered, item => item is TextDelta { Text: "recovered" });
        Assert.Equal(3, handler.Count);
    }

    [Fact]
    public async System.Threading.Tasks.Task Retry_count_configuration_changes_number_of_attempts()
    {
        foreach (var (retryLimit, expectedCalls) in new[] { (0, 1), (2, 3) })
        {
            var handler = new QueueHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var provider = CreateProvider(handler, new OpenAiProviderOptions
            {
                MaxRetries = retryLimit,
                CircuitFailureThreshold = 10,
                DelayAsync = (_, _) => ValueTask.CompletedTask,
            });
            await Assert.ThrowsAsync<ModelProviderException>(() => Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));
            Assert.Equal(expectedCalls, handler.Count);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Circuit_threshold_and_cooldown_configuration_change_behavior()
    {
        var now = DateTimeOffset.UnixEpoch;
        var handler = new QueueHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => Sse("data: {\"choices\":[{\"delta\":{\"content\":\"recovered\"},\"finish_reason\":\"stop\"}]}\n\n" + "data: [DONE]\n\n"));
        var provider = CreateProvider(handler, new OpenAiProviderOptions
        {
            MaxRetries = 0,
            CircuitFailureThreshold = 1,
            CircuitCooldown = TimeSpan.FromSeconds(15),
            UtcNow = () => now,
            DelayAsync = (_, _) => ValueTask.CompletedTask,
        });
        await Assert.ThrowsAsync<ModelProviderException>(() => Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));
        await Assert.ThrowsAsync<ModelProviderException>(() => Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));
        Assert.Equal(1, handler.Count);
        now += TimeSpan.FromSeconds(14);
        await Assert.ThrowsAsync<ModelProviderException>(() => Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken)));
        Assert.Equal(1, handler.Count);
        now += TimeSpan.FromSeconds(1);
        await Collect(provider.StreamAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public void Managed_server_api_key_is_environment_only_never_argv()
    {
        const string secret = "sensitive-managed-key";
        var spec = new ManagedServerSpec("server", ["--port", "{port}", "--api-key", "{apiKey}", "--name={apiKey}"], "", 1234);

        var launch = spec.ToLaunch(1234, secret);

        Assert.DoesNotContain(launch.Args, arg => arg.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(launch.Args, arg => arg.Contains("{apiKey}", StringComparison.Ordinal));
        Assert.Equal(secret, launch.Environment["OMNI_SERVER_API_KEY"]);
        Assert.Equal(secret, launch.Environment["LLAMA_API_KEY"]);
    }

    private static OpenAiChatCompatibleProvider CreateProvider(QueueHandler handler, OpenAiProviderOptions? options = null) =>
        new(new ProviderDescriptor("test-provider", ProviderFamily.OpenAiChatCompatible, "https://example.test/v1",
            AuthConfig.None(), false, false, true), new EmptySecrets(),
            () => new HttpClient(handler, disposeHandler: false), options);

    private static ModelRequest Request(ProviderState? continuation = null)
    {
        var profile = new EffectiveModelProfile("model-a", 4096, 4096, 1024, ["text"], [ToolCallFormat.Native], true,
            new Dictionary<string, double>());
        var selection = new ModelSelection(new ModelIdValue("model-a"), 4096, ToolMode.Direct, null);
        var messages = continuation is null
            ? new ModelMessage[] { new(MessageRole.User, [new TextBlock("hello")]) }
            : new ModelMessage[] { new(MessageRole.User, [new TextBlock("hello")]), new(MessageRole.Assistant, [new TextBlock("ok")]) };
        return new ModelRequest(selection, messages, null, [], ToolChoice.Auto(), null, null, null, continuation);
    }

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    private static async System.Threading.Tasks.Task<List<ModelStreamEvent>> Collect(IAsyncEnumerable<ModelStreamEvent> stream)
    {
        var result = new List<ModelStreamEvent>();
        await foreach (var item in stream) result.Add(item);
        return result;
    }

    private sealed class EmptySecrets : ISecretProvider
    {
        public Secret GetSecret(string secretRef, CancellationToken cancellationToken) => throw new InvalidOperationException("No API key expected");
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;
        public int Count { get; private set; }
        public System.Threading.Tasks.Task<string>? LastBody { get; private set; }
        public QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) => _responses = new(responses);
        protected override async System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            LastBody = request.Content!.ReadAsStringAsync(cancellationToken);
            var next = _responses.TryDequeue(out var response) ? response : throw new InvalidOperationException("No scripted response remaining");
            return next(request);
        }
    }

    private sealed class ThrowAfterFirstReadContent(byte[] initialBytes) : HttpContent
    {
        protected override System.Threading.Tasks.Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length) { length = -1; return false; }
        protected override System.Threading.Tasks.Task<Stream> CreateContentReadStreamAsync() => System.Threading.Tasks.Task.FromResult<Stream>(new ThrowAfterFirstReadStream(initialBytes));
    }

    private sealed class ThrowAfterFirstReadStream(byte[] initialBytes) : Stream
    {
        private int _position;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => initialBytes.Length; public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position == 0)
            {
                var count = Math.Min(buffer.Length, initialBytes.Length);
                initialBytes.AsMemory(0, count).CopyTo(buffer);
                _position = count;
                return ValueTask.FromResult(count);
            }
            throw new IOException("scripted mid-stream disconnect");
        }
        public override System.Threading.Tasks.Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
