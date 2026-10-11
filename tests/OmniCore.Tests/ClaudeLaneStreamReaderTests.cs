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
        var input = Init + "\n{\"type\":\"assistant\",\"session_id\":\"sid-123\",\"message\":{\"id\":\"msg-1\",\"usage\":{" + usage
            + "},\"content\":[{" + toolUses + "}]}}\n" + Result + "\n";
        using var stream = Bytes(input);
        var limits = usage.Contains("input_tokens\":10", StringComparison.Ordinal)
            ? new ClaudeLaneStreamLimits(MaximumTokens: 10)
            : new ClaudeLaneStreamLimits(MaximumToolCalls: 1);
        var exception = await Assert.ThrowsAsync<ClaudeLaneStreamException>(async () =>
            await ClaudeLaneStreamReader.ReadAsync(stream, static (_, _) => ValueTask.CompletedTask,
                limits, TestContext.Current.CancellationToken));
        Assert.Equal(ClaudeLaneStreamErrorCode.BudgetExceeded, exception.Code);
        Assert.NotNull(exception.BudgetEvidence);
        Assert.Equal(usage.Contains("input_tokens\":10", StringComparison.Ordinal) ? "tokens" : "tool-calls",
            exception.BudgetEvidence.Dimension);
        Assert.Equal(usage.Contains("input_tokens\":10", StringComparison.Ordinal) ? 14 : 2,
            exception.BudgetEvidence.ObservedCount);
    }

    [Fact]
    public async Task Cached_input_counts_toward_token_budget_and_activity_is_bounded_and_sanitized()
    {
        var assistant = "{\"type\":\"assistant\",\"session_id\":\"sid-123\",\"message\":{\"id\":\"msg-cache\","
            + "\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"cache_read_input_tokens\":9},"
            + "\"content\":[{\"type\":\"tool_use\",\"name\":\"Read\"}]}}";
        using var overBudget = Bytes(Init + "\n" + assistant + "\n" + Result + "\n");
        var budgetError = await Assert.ThrowsAsync<ClaudeLaneStreamException>(async () =>
            await ClaudeLaneStreamReader.ReadAsync(overBudget, static (_, _) => ValueTask.CompletedTask,
                new ClaudeLaneStreamLimits(MaximumTokens: 10), TestContext.Current.CancellationToken));
        Assert.Equal(ClaudeLaneStreamErrorCode.BudgetExceeded, budgetError.Code);
        Assert.Equal(new ClaudeLaneBudgetEvidence("tokens", 14, 10), budgetError.BudgetEvidence);

        var activity = "{\"type\":\"system\",\"subtype\":\"api_retry\"}\n"
            + "{\"type\":\"system\",\"subtype\":\"permission_denied\",\"tool_name\":\"Read\","
            + "\"reason\":\"C:/private/secret.txt\"}\n{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\"}}\n"
            + "{\"type\":\"api_retry\"}\n{\"type\":\"permission_denied\",\"tool_name\":\"Read\","
            + "\"reason\":\"C:/private/secret.txt\"}\n{\"type\":\"user\",\"message\":{\"content\":[{"
            + "\"type\":\"tool_result\",\"is_error\":false,\"content\":\"private output\"}]}}";
        using var complete = Bytes(Init + "\n" + activity + "\n" + Result + "\n");
        var outcome = await ClaudeLaneStreamReader.ReadAsync(complete, static (_, _) => ValueTask.CompletedTask,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, outcome.ApiRetryCount);
        Assert.Equal(2, outcome.PermissionDenialCount);
        Assert.Equal(1, outcome.ToolResultCount);
        Assert.DoesNotContain(outcome.PermissionDenials, denial => denial.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, outcome.StreamDeltaCount);
    }

    [Fact]
    public async Task Repeated_assistant_message_id_usage_is_counted_once_but_distinct_ids_accumulate()
    {
        var repeated = "{\"type\":\"assistant\",\"message\":{\"id\":\"message-1\","
            + "\"usage\":{\"input_tokens\":8,\"output_tokens\":2},\"content\":[{\"type\":\"text\",\"text\":\"a\"}]}}";
        var repeatedSecondBlock = "{\"type\":\"assistant\",\"message\":{\"id\":\"message-1\","
            + "\"usage\":{\"input_tokens\":8,\"output_tokens\":2},\"content\":[{\"type\":\"tool_use\",\"name\":\"Read\"}]}}";
        using var sameId = Bytes(Init + "\n" + repeated + "\n" + repeatedSecondBlock + "\n" + Result + "\n");
        var outcome = await ClaudeLaneStreamReader.ReadAsync(sameId, static (_, _) => ValueTask.CompletedTask,
            new ClaudeLaneStreamLimits(MaximumTokens: 15, MaximumToolCalls: 1), TestContext.Current.CancellationToken);
        Assert.Equal(12, outcome.TotalTokens);
        Assert.Equal(1, outcome.ToolUseCount);

        var distinct = repeated.Replace("message-1", "message-2", StringComparison.Ordinal);
        using var distinctIds = Bytes(Init + "\n" + repeated + "\n" + distinct + "\n" + Result + "\n");
        var budgetError = await Assert.ThrowsAsync<ClaudeLaneStreamException>(async () =>
            await ClaudeLaneStreamReader.ReadAsync(distinctIds, static (_, _) => ValueTask.CompletedTask,
                new ClaudeLaneStreamLimits(MaximumTokens: 15), TestContext.Current.CancellationToken));
        Assert.Equal(ClaudeLaneStreamErrorCode.BudgetExceeded, budgetError.Code);
        Assert.Equal(new ClaudeLaneBudgetEvidence("tokens", 16, 15), budgetError.BudgetEvidence);

        var inconsistent = repeatedSecondBlock.Replace("\"output_tokens\":2", "\"output_tokens\":3", StringComparison.Ordinal);
        using var changedUsage = Bytes(Init + "\n" + repeated + "\n" + inconsistent + "\n" + Result + "\n");
        var mismatch = await Assert.ThrowsAsync<ClaudeLaneStreamException>(async () =>
            await ClaudeLaneStreamReader.ReadAsync(changedUsage, static (_, _) => ValueTask.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(ClaudeLaneStreamErrorCode.RepeatedMessageUsageMismatch, mismatch.Code);
    }

    [Fact]
    public async Task Cost_and_missing_usage_fail_with_safe_budget_dimensions()
    {
        var overCost = Result.Replace("\"total_cost_usd\":0.01", "\"total_cost_usd\":0.02", StringComparison.Ordinal);
        using var costStream = Bytes(Init + "\n" + overCost + "\n");
        var costError = await Assert.ThrowsAsync<ClaudeLaneStreamException>(async () =>
            await ClaudeLaneStreamReader.ReadAsync(costStream, static (_, _) => ValueTask.CompletedTask,
                new ClaudeLaneStreamLimits(MaximumCostUsd: 0.01m), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(ClaudeLaneStreamErrorCode.BudgetExceeded, costError.Code);
        Assert.Equal(new ClaudeLaneBudgetEvidence("cost", ObservedCostUsd: 0.02m, LimitCostUsd: 0.01m),
            costError.BudgetEvidence);

        var noUsage = "{\"type\":\"result\",\"session_id\":\"sid-123\",\"is_error\":false,"
            + "\"structured_output\":{\"ok\":true},\"total_cost_usd\":0.01}";
        using var missingStream = Bytes(Init + "\n" + noUsage + "\n");
        var missing = await Assert.ThrowsAsync<ClaudeLaneStreamException>(async () =>
            await ClaudeLaneStreamReader.ReadAsync(missingStream, static (_, _) => ValueTask.CompletedTask,
                new ClaudeLaneStreamLimits(MaximumTokens: 10), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(ClaudeLaneStreamErrorCode.BudgetUsageUnavailable, missing.Code);
        Assert.Equal("tokens-usage-unavailable", missing.BudgetEvidence?.Dimension);
        Assert.Equal(10, missing.BudgetEvidence?.LimitCount);
    }

    [Fact]
    public async Task Resumed_result_cost_is_checked_against_total_task_ceiling_not_invocation_slice()
    {
        var cumulative = Result.Replace("\"total_cost_usd\":0.01", "\"total_cost_usd\":0.75", StringComparison.Ordinal);
        using var underTotal = Bytes(Init + "\n" + cumulative + "\n");
        var outcome = await ClaudeLaneStreamReader.ReadAsync(underTotal, static (_, _) => ValueTask.CompletedTask,
            new ClaudeLaneStreamLimits(MaximumTokens: 100, MaximumToolCalls: 10,
                MaximumCostUsd: 0.5m, MaximumReportedCostUsd: 1m), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0.75m, outcome.Usage.TotalCostUsd);

        var overTotal = cumulative.Replace("0.75", "1.01", StringComparison.Ordinal);
        using var overTotalStream = Bytes(Init + "\n" + overTotal + "\n");
        var error = await Assert.ThrowsAsync<ClaudeLaneStreamException>(async () =>
            await ClaudeLaneStreamReader.ReadAsync(overTotalStream, static (_, _) => ValueTask.CompletedTask,
                new ClaudeLaneStreamLimits(MaximumTokens: 100, MaximumToolCalls: 10,
                    MaximumCostUsd: 0.5m, MaximumReportedCostUsd: 1m), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(ClaudeLaneStreamErrorCode.BudgetExceeded, error.Code);
        Assert.Equal(new ClaudeLaneBudgetEvidence("cost", ObservedCostUsd: 1.01m, LimitCostUsd: 1m), error.BudgetEvidence);
    }

    [Fact]
    public async Task External_progress_is_coalesced_and_observed_before_stream_eof()
    {
        var progressEntered = new TaskCompletionSource<ClaudeLaneProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryEntered = new TaskCompletionSource<ClaudeLaneProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var denialEntered = new TaskCompletionSource<ClaudeLaneProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProgress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDenial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new System.Collections.Concurrent.ConcurrentQueue<ClaudeLaneProgress>();
        var deltas = string.Join('\n', Enumerable.Range(0, 32)
            .Select(_ => "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\"}}"));
        var retry = "{\"type\":\"system\",\"subtype\":\"api_retry\",\"cause\":\"rate_limit\"}";
        var denial = "{\"type\":\"system\",\"subtype\":\"permission_denied\",\"tool_name\":\"Read\",\"reason\":\"private path\"}";
        using var stream = Bytes(Init + "\n" + deltas + "\n" + retry + "\n" + denial + "\n" + Result + "\n");
        var read = ClaudeLaneStreamReader.ReadAsync(stream, static (_, _) => ValueTask.CompletedTask,
            cancellationToken: TestContext.Current.CancellationToken, onProgress: async (progress, token) =>
            {
                observed.Enqueue(progress);
                if (progress.Kind == ClaudeLaneProgressKind.StreamProgress)
                {
                    progressEntered.TrySetResult(progress);
                    await releaseProgress.Task.WaitAsync(token);
                }
                else if (progress.Kind == ClaudeLaneProgressKind.ApiRetry)
                {
                    retryEntered.TrySetResult(progress);
                    await releaseRetry.Task.WaitAsync(token);
                }
                else if (progress.Kind == ClaudeLaneProgressKind.PermissionDenied)
                {
                    denialEntered.TrySetResult(progress);
                    await releaseDenial.Task.WaitAsync(token);
                }
            }).AsTask();

        var progress = await progressEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(ClaudeLaneProgressKind.StreamProgress, progress.Kind);
        Assert.Equal(32, progress.ObservedCount);
        Assert.False(read.IsCompleted);
        releaseProgress.SetResult();
        var retryProgress = await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(ClaudeLaneProgressKind.ApiRetry, retryProgress.Kind);
        Assert.False(read.IsCompleted);
        releaseRetry.SetResult();
        var denialProgress = await denialEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(ClaudeLaneProgressKind.PermissionDenied, denialProgress.Kind);
        Assert.False(read.IsCompleted);
        releaseDenial.SetResult();
        var outcome = await read;
        Assert.Equal(32, outcome.StreamDeltaCount);
        Assert.Equal(new[] { ClaudeLaneProgressKind.StreamProgress, ClaudeLaneProgressKind.ApiRetry,
                ClaudeLaneProgressKind.PermissionDenied }, observed.Select(item => item.Kind));
        Assert.Equal(1, outcome.ApiRetryCount);
        Assert.Equal(1, outcome.PermissionDenialCount);
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
