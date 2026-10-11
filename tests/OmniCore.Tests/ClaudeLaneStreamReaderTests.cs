namespace OmniCore.Tests;

using System.Text;
using OmniCore.Host;

public sealed class ClaudeLaneStreamReaderTests
{
    private const string Init = "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sid-123\",\"model\":\"claude-haiku-5-5\",\"tools\":[\"Read\",\"Edit\"]}";
    private const string Result = "{\"type\":\"result\",\"subtype\":\"success\",\"session_id\":\"sid-123\",\"is_error\":false,\"result\":\"done\",\"structured_output\":{\"ok\":true},\"usage\":{\"input_tokens\":12,\"output_tokens\":4,\"cache_read_input_tokens\":2},\"total_cost_usd\":0.01}";

    [Fact]
    public async Task Valid_stream_waits_for_durable_init_callback_and_returns_terminal_usage()
    {
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stream = Bytes(Init + "\n{\"type\":\"assistant\",\"session_id\":\"sid-123\"}\n" + Result + "\n");
        var reading = ClaudeLaneStreamReader.ReadAsync(stream, async (session, cancellationToken) =>
        {
            Assert.Equal("sid-123", session.SessionId);
            Assert.Equal("claude-haiku-5-5", session.Model);
            Assert.Equal(new[] { "Read", "Edit" }, session.Tools);
            callbackEntered.SetResult();
            await releaseCallback.Task.WaitAsync(cancellationToken);
        }, cancellationToken: TestContext.Current.CancellationToken).AsTask();

        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(reading.IsCompleted);
        releaseCallback.SetResult();
        var outcome = await reading;

        Assert.False(outcome.IsError);
        Assert.Equal("done", outcome.ResultText);
        Assert.Equal("{\"ok\":true}", outcome.StructuredOutputJson);
        Assert.Equal(new ClaudeLaneUsage(12, 4, 2, null, 0.01m), outcome.Usage);
        Assert.Equal(3, outcome.EventCount);
        Assert.True(outcome.StreamBytes > 0);
    }

    [Fact]
    public async Task Capability_name_array_is_preserved_as_enabled_capabilities()
    {
        var init = "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sid-123\",\"model\":\"claude-haiku-5-5\",\"tools\":[\"Read\"],\"capabilities\":[\"structured_output\",\"fast_mode\"]}";
        using var stream = Bytes(init + "\n" + Result + "\n");
        var outcome = await ClaudeLaneStreamReader.ReadAsync(stream, static (_, _) => ValueTask.CompletedTask,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["structured_output"] = true,
            ["fast_mode"] = true,
        }, outcome.Capabilities);
    }

    [Theory]
    [InlineData("{\"type\":\"result\",\"session_id\":\"sid-123\",\"is_error\":false}", "InitRequired")]
    [InlineData("{\"type\":\"system\",\"subtype\":\"init\",\"subtype\":\"init\",\"session_id\":\"sid-123\",\"model\":\"m\",\"tools\":[]}\n" + Result, "DuplicateJsonMember")]
    [InlineData("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sid-123\",\"model\":\"m\",\"tools\":[]}\n{\"type\":\"result\",\"session_id\":\"other\",\"is_error\":false}", "SessionMismatch")]
    [InlineData(Init, "MissingTerminal")]
    [InlineData(Init + "\n" + Result + "\n" + Result, "DuplicateTerminal")]
    [InlineData("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sid-123\",\"model\":\"m\",\"tools\":[\"Read\",\"Read\"]}", "InvalidInitTool")]
    [InlineData("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sid-123\",\"model\":\"m\",\"tools\":[],\"capabilities\":{\"x\":\"yes\"}}", "InvalidInitCapabilityValue")]
    [InlineData("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sid-123\",\"model\":\"m\",\"tools\":[],\"capabilities\":[{}]}", "InvalidInitCapabilityValue")]
    public async Task Invalid_streams_fail_with_typed_protocol_codes(string input, string expected)
    {
        using var stream = Bytes(input + "\n");
        var exception = await Assert.ThrowsAsync<ClaudeLaneStreamException>(async () =>
            await ClaudeLaneStreamReader.ReadAsync(stream, static (_, _) => ValueTask.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(Enum.Parse<ClaudeLaneStreamErrorCode>(expected), exception.Code);
    }

    [Fact]
    public async Task Line_and_event_limits_fail_without_truncating_or_accepting_terminal()
    {
        var longResult = Result.Replace("\"done\"", "\"" + new string('x', 300) + "\"", StringComparison.Ordinal);
        using var lineStream = Bytes(Init + "\n" + longResult + "\n");
        var lineError = await Assert.ThrowsAsync<ClaudeLaneStreamException>(async () =>
            await ClaudeLaneStreamReader.ReadAsync(lineStream, static (_, _) => ValueTask.CompletedTask,
                new ClaudeLaneStreamLimits(MaximumLineBytes: 256, MaximumTotalBytes: 2048), TestContext.Current.CancellationToken));
        Assert.Equal(ClaudeLaneStreamErrorCode.LimitExceeded, lineError.Code);

        using var eventStream = Bytes(Init + "\n" + Result + "\n");
        var eventError = await Assert.ThrowsAsync<ClaudeLaneStreamException>(async () =>
            await ClaudeLaneStreamReader.ReadAsync(eventStream, static (_, _) => ValueTask.CompletedTask,
                new ClaudeLaneStreamLimits(MaximumEvents: 1), TestContext.Current.CancellationToken));
        Assert.Equal(ClaudeLaneStreamErrorCode.LimitExceeded, eventError.Code);
    }

    [Fact]
    public async Task Invalid_utf8_and_out_of_range_usage_are_rejected()
    {
        using var utf8 = new MemoryStream([0xFF, 0x0A]);
        var utf8Error = await Assert.ThrowsAsync<ClaudeLaneStreamException>(async () =>
            await ClaudeLaneStreamReader.ReadAsync(utf8, static (_, _) => ValueTask.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(ClaudeLaneStreamErrorCode.InvalidUtf8, utf8Error.Code);

        using var usage = Bytes(Init + "\n{\"type\":\"result\",\"session_id\":\"sid-123\",\"is_error\":false,\"usage\":{\"input_tokens\":-1}}\n");
        var usageError = await Assert.ThrowsAsync<ClaudeLaneStreamException>(async () =>
            await ClaudeLaneStreamReader.ReadAsync(usage, static (_, _) => ValueTask.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(ClaudeLaneStreamErrorCode.InvalidResult, usageError.Code);
    }

    [Theory]
    [InlineData("\"input_tokens\":10,\"output_tokens\":1", "\"type\":\"tool_use\",\"name\":\"Read\"")]
    [InlineData("\"input_tokens\":1,\"output_tokens\":1", "\"type\":\"tool_use\",\"name\":\"Read\"},{\"type\":\"tool_use\",\"name\":\"Edit\"")]
    public async Task Assistant_usage_and_tool_calls_are_enforced_before_result(string usage, string toolUses)
    {
        var input = Init + "\n{\"type\":\"assistant\",\"session_id\":\"sid-123\",\"message\":{\"usage\":{" + usage
            + "},\"content\":[{" + toolUses + "}]}}\n" + Result + "\n";
        using var stream = Bytes(input);
        var limits = usage.Contains("input_tokens\":10", StringComparison.Ordinal)
            ? new ClaudeLaneStreamLimits(MaximumTokens: 10)
            : new ClaudeLaneStreamLimits(MaximumToolCalls: 1);
        var exception = await Assert.ThrowsAsync<ClaudeLaneStreamException>(async () =>
            await ClaudeLaneStreamReader.ReadAsync(stream, static (_, _) => ValueTask.CompletedTask,
                limits, TestContext.Current.CancellationToken));
        Assert.Equal(ClaudeLaneStreamErrorCode.BudgetExceeded, exception.Code);
    }

    [Fact]
    public async Task Cancellation_interrupts_stream_read()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new BlockingStream();
        var reading = ClaudeLaneStreamReader.ReadAsync(stream, static (_, _) => ValueTask.CompletedTask,
            cancellationToken: cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reading);
    }

    private static MemoryStream Bytes(string value) => new(Encoding.UTF8.GetBytes(value));

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
