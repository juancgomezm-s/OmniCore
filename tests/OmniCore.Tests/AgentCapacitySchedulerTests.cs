using OmniCore.Domain;
using OmniCore.Host;

namespace OmniCore.Tests;

public sealed class AgentCapacitySchedulerTests
{
    [Fact]
    public async System.Threading.Tasks.Task Reader_precedes_later_waiting_writer_without_mutual_starvation()
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        var capacity = AgentCapacity.For(fx.Store);
        using var primary = capacity.TryAcquire(fx.Session, fx.Run, null, 3, 1000, false, CancellationToken.None)!;
        using var readerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var writerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var readerId = DelegationId.New();
        var readerTask = System.Threading.Tasks.Task.Run(() => capacity.Acquire(fx.Session, fx.Run, readerId,
            3, 10, true, readerCancellation.Token, 1), readerCancellation.Token);
        Assert.True(SpinWait.SpinUntil(() => capacity.ReadSnapshot(fx.Session, fx.Run).Waiting == 1, TimeSpan.FromSeconds(5)));
        var writerTask = System.Threading.Tasks.Task.Run(() => capacity.Acquire(fx.Session, fx.Run, null,
            3, 0, false, writerCancellation.Token, 2), writerCancellation.Token);
        Assert.True(SpinWait.SpinUntil(() => capacity.ReadSnapshot(fx.Session, fx.Run).Waiting == 2, TimeSpan.FromSeconds(5)));

        primary.Dispose();
        using var reader = await readerTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(writerTask.IsCompleted);
        reader.Dispose();
        using var writer = await writerTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(writer.ReadOnly);
    }

    [Fact]
    public void Active_root_lease_consumes_its_reserved_slot_only_once()
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        var capacity = AgentCapacity.For(fx.Store);
        using var root = capacity.TryAcquire(fx.Session, fx.Run, null, 3, 1000, true, CancellationToken.None);
        Assert.NotNull(root);
        using var first = capacity.TryAcquire(fx.Session, fx.Run, DelegationId.New(), 3, 0, true, CancellationToken.None);
        Assert.NotNull(first);
        using var second = capacity.TryAcquire(fx.Session, fx.Run, DelegationId.New(), 3, 0, true, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(3, capacity.ReadSnapshot(fx.Session, fx.Run).Active);
        Assert.Null(capacity.TryAcquire(fx.Session, fx.Run, DelegationId.New(), 3, 0, true, CancellationToken.None));
    }
}
