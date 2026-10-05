using System.Runtime.CompilerServices;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Engine;
using OmniCore.Host;
using OmniCore.Infrastructure;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class TelemetryExecutionAttributionTests
{
    [Fact]
    public async Task Nested_executions_are_attributed_without_leaking_into_parent_or_unscoped_streams()
    {
        var provider = new SingleDeltaProvider();
        var sink = new InMemoryTelemetrySink();
        var wrapped = new TelemetryObservingModelProvider(provider, sink);
        var parent = ExecutionId.New();
        var child = ExecutionId.New();
        var run = RunId.New();
        var parentScope = new ExecutionScopeState(RunId: run, ExecutionId: parent);

        using (ExecutionScope.Begin(parentScope))
        {
            await Consume(wrapped);
            using (ExecutionScope.Begin(new ExecutionScopeState(RunId: run, ExecutionId: child)))
                await Consume(wrapped);
            Assert.Same(parentScope, ExecutionScope.Current);
            await Consume(wrapped);
        }
        Assert.Null(ExecutionScope.Current);
        await Consume(wrapped);

        var records = sink.Snapshot();
        Assert.Equal(new ExecutionId?[] { parent, child, parent, null },
            records.Select(record => record.ExecutionId).ToArray());
        Assert.Equal(new RunId?[] { run, run, run, null },
            records.Select(record => record.RunId).ToArray());
        Assert.All(records, record => Assert.Equal(6, record.Value));
        Assert.Equal(4, provider.Calls);
    }

    [Fact]
    public async Task Concurrent_async_flows_keep_their_execution_ids_in_the_shared_sink()
    {
        var sink = new InMemoryTelemetrySink();
        var first = ExecutionId.New();
        var second = ExecutionId.New();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task Observe(ExecutionId execution)
        {
            using var scope = ExecutionScope.Begin(new ExecutionScopeState(ExecutionId: execution));
            if (Interlocked.Increment(ref arrivals) == 2) ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await Consume(new TelemetryObservingModelProvider(new SingleDeltaProvider(), sink));
        }
        await Task.WhenAll(Task.Run(() => Observe(first), TestContext.Current.CancellationToken),
            Task.Run(() => Observe(second), TestContext.Current.CancellationToken));
        Assert.Null(ExecutionScope.Current);
        var records = sink.Snapshot();
        Assert.Equal(2, records.Count);
        Assert.Single(records, record => record.ExecutionId == first);
        Assert.Single(records, record => record.ExecutionId == second);
        Assert.All(records, record => Assert.Null(record.RunId));

        // Existing numeric-only callers need not provide a newly available execution identity.
        var legacy = new TelemetryRecord(TelemetryKind.Progress, TelemetrySignal.ResponseStartedCount,
            1, DateTimeOffset.UtcNow);
        Assert.Null(legacy.ExecutionId);
    }

    private static async Task Consume(IModelProvider provider)
    {
        var request = new ModelRequest(
            new ModelSelection(new ModelIdValue("scripted"), 1024, ToolMode.Direct, null),
            Array.Empty<ModelMessage>(), null, Array.Empty<ToolDefinition>(), ToolChoice.Auto(),
            null, null, null, null);
        await foreach (var item in provider.StreamAsync(request, TestContext.Current.CancellationToken))
            Assert.IsType<TextDelta>(item);
    }

    private sealed class SingleDeltaProvider : IModelProvider
    {
        public ProviderCapabilities Capabilities => ProviderCapabilities.Local();
        public int Calls { get; private set; }
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TextDelta(0, "opaque");
        }
    }
}
