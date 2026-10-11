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

    [Fact]
    public void Destination_leases_allow_independent_writers_but_exclude_same_root_readers_and_writers()
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        var capacity = AgentCapacity.For(fx.Store);
        var root = Path.Combine(Path.GetTempPath(), "m7-capacity-" + Guid.NewGuid().ToString("N"));
        var firstPath = Path.Combine(root, "lane-a");
        var secondPath = Path.Combine(root, "lane-b");
        using var first = capacity.TryAcquire(fx.Session, fx.Run, DelegationId.New(), 4, 10,
            readOnly: false, CancellationToken.None, destinationRoot: firstPath);
        Assert.NotNull(first);
        using var second = capacity.TryAcquire(fx.Session, fx.Run, DelegationId.New(), 4, 0,
            readOnly: false, CancellationToken.None, destinationRoot: secondPath);
        Assert.NotNull(second);
        Assert.Null(capacity.TryAcquire(fx.Session, fx.Run, DelegationId.New(), 4, -1,
            readOnly: true, CancellationToken.None, destinationRoot: firstPath));
        Assert.Null(capacity.TryAcquire(fx.Session, fx.Run, DelegationId.New(), 4, -1,
            readOnly: false, CancellationToken.None, destinationRoot: firstPath));
        Assert.Equal(2, capacity.ReadSnapshot(fx.Session, fx.Run).Active);
    }

    [Fact]
    public void Destination_identity_uses_physical_path_canonicalization_and_case_fold_on_windows()
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        var capacity = AgentCapacity.For(fx.Store);
        var root = Path.Combine(Path.GetTempPath(), "m7-capacity-" + Guid.NewGuid().ToString("N"));
        var original = Path.Combine(root, "lane");
        using var writer = capacity.TryAcquire(fx.Session, fx.Run, DelegationId.New(), 3, 1,
            false, CancellationToken.None, destinationRoot: original);
        Assert.NotNull(writer);
        var equivalent = OperatingSystem.IsWindows() ? original.ToUpperInvariant() : Path.Combine(root, ".", "lane");
        Assert.Null(capacity.TryAcquire(fx.Session, fx.Run, DelegationId.New(), 3, 0,
            true, CancellationToken.None, destinationRoot: equivalent));
    }

    [Fact]
    public void Physical_destination_exclusion_is_shared_across_sessions_and_runs()
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        var capacity = AgentCapacity.For(fx.Store);
        var otherSession = SessionId.New();
        var otherRun = RunId.New();
        var root = Path.Combine(Path.GetTempPath(), "m7-capacity-cross-run-" + Guid.NewGuid().ToString("N"));
        using var first = capacity.TryAcquire(fx.Session, fx.Run, DelegationId.New(), 3, 10,
            readOnly: false, CancellationToken.None, destinationRoot: root);
        Assert.NotNull(first);
        Assert.Null(capacity.TryAcquire(otherSession, otherRun, DelegationId.New(), 3, 10,
            readOnly: true, CancellationToken.None, destinationRoot: root));
        Assert.Null(capacity.TryAcquire(otherSession, otherRun, DelegationId.New(), 3, 10,
            readOnly: false, CancellationToken.None, destinationRoot: root));
        using var independent = capacity.TryAcquire(otherSession, otherRun, DelegationId.New(), 3, 10,
            readOnly: false, CancellationToken.None, destinationRoot: Path.Combine(root, "owned-lane"));
        Assert.NotNull(independent);
    }

    [Fact]
    public void Unknown_destination_is_a_wildcard_against_known_destinations()
    {
        using var fx = new DelegationAdmissionTests.Fixture();
        var capacity = AgentCapacity.For(fx.Store);
        var knownPath = Path.Combine(Path.GetTempPath(), "m7-capacity-" + Guid.NewGuid().ToString("N"));
        using (var unknownWriter = capacity.TryAcquire(fx.Session, fx.Run, DelegationId.New(), 4, 10,
                   false, CancellationToken.None))
        {
            Assert.NotNull(unknownWriter);
            Assert.Null(capacity.TryAcquire(fx.Session, fx.Run, DelegationId.New(), 4, 0,
                true, CancellationToken.None, destinationRoot: knownPath));
            Assert.Null(capacity.TryAcquire(fx.Session, fx.Run, DelegationId.New(), 4, 0,
                false, CancellationToken.None, destinationRoot: knownPath));
        }

        using var unknownReader = capacity.TryAcquire(fx.Session, fx.Run, DelegationId.New(), 4, 10,
            true, CancellationToken.None);
        Assert.NotNull(unknownReader);
        Assert.Null(capacity.TryAcquire(fx.Session, fx.Run, DelegationId.New(), 4, 0,
            false, CancellationToken.None, destinationRoot: knownPath));
        using var knownReader = capacity.TryAcquire(fx.Session, fx.Run, DelegationId.New(), 4, 0,
            true, CancellationToken.None, destinationRoot: knownPath);
        Assert.NotNull(knownReader);
    }
}
