using OmniCore.Abstractions;
using OmniCore.Infrastructure;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class TelemetryConcurrencyTests
{
    [Fact]
    public void Eviction_preserves_FIFO_and_previously_obtained_snapshot()
    {
        var sink = new InMemoryTelemetrySink(3);
        var records = Enumerable.Range(0, 6).Select(i => Sample(i)).ToArray();
        foreach (var record in records.Take(5)) sink.Record(record);
        var before = sink.Snapshot();
        Assert.Equal(new long[] { 2, 3, 4 }, before.Select(r => r.Value));
        for (var i = 0; i < 3; i++) Assert.Same(records[i + 2], before[i]);

        sink.Record(records[5]);
        Assert.Equal(new long[] { 2, 3, 4 }, before.Select(r => r.Value));
        Assert.Equal(new long[] { 3, 4, 5 }, sink.Snapshot().Select(r => r.Value));
    }

    [Fact]
    public async Task Concurrent_writers_preserve_all_samples_and_snapshots_are_independent()
    {
        const int writers = 4;
        const int perWriter = 64;
        const int capacity = writers * perWriter;
        var sink = new InMemoryTelemetrySink(capacity);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = TestContext.Current.CancellationToken;
        var tasks = Enumerable.Range(0, writers).Select(writer => Task.Run(async () =>
        {
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            for (var index = 0; index < perWriter; index++)
            {
                sink.Record(Sample(writer * perWriter + index));
                await Task.Yield();
            }
        }, token)).ToArray();
        var reader = Task.Run(async () =>
        {
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            // The scheduling interleave is deliberately unspecified; every observed snapshot
            // must be internally consistent whether writers are active or already complete.
            for (var index = 0; index < perWriter; index++)
            {
                var snapshot = sink.Snapshot();
                Assert.InRange(snapshot.Count, 0, capacity);
                Assert.Equal(snapshot.Count, snapshot.Select(r => r.Value).Distinct().Count());
                Assert.All(snapshot, r =>
                {
                    Assert.InRange(r.Value, 0, capacity - 1);
                    Assert.Equal(TelemetryKind.Sample, r.Kind);
                    Assert.Equal(TelemetrySignal.InputTokenCount, r.Signal);
                    Assert.Equal(TimeSpan.Zero, r.TimestampUtc.Offset);
                });
                await Task.Yield();
            }
        }, token);
        gate.SetResult();
        await Task.WhenAll(tasks.Append(reader)).WaitAsync(TimeSpan.FromSeconds(10), token);

        var completed = sink.Snapshot();
        Assert.Equal(capacity, completed.Count);
        Assert.Equal(Enumerable.Range(0, capacity).Select(i => (long)i),
            completed.Select(r => r.Value).OrderBy(value => value));
        sink.Record(Sample(999));
        var after = sink.Snapshot();
        Assert.Equal(capacity, after.Count);
        Assert.Equal(capacity, completed.Count);
        Assert.DoesNotContain(completed, r => r.Value == 999);
        Assert.Equal(completed.Skip(1).Select(r => r.Value).Append(999), after.Select(r => r.Value));
    }

    private static TelemetryRecord Sample(long value) => new(TelemetryKind.Sample,
        TelemetrySignal.InputTokenCount, value, DateTimeOffset.UtcNow);
}
