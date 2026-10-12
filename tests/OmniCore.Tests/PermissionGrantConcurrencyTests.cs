using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Infrastructure;
using OmniCore.Security;
using Task = System.Threading.Tasks.Task;

namespace OmniCore.Tests;

public sealed class PermissionGrantConcurrencyTests
{
    private static PermissionGrantRecord Grant(WorkspaceId workspace) => new(GrantId.New(),
        "filesystem.read", new string('A', 64), GrantLifetime.Workspace, workspace, null, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Independent_stores_preserve_concurrent_additions_and_revocations()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-grant-concurrency-" + Guid.NewGuid().ToString("N"));
        var workspace = WorkspaceId.Parse("concurrent-workspace");
        var original = Enumerable.Range(0, 24).Select(_ => Grant(workspace)).ToArray();
        var replacement = Enumerable.Range(0, 24).Select(_ => Grant(workspace)).ToArray();
        var seed = new FilePermissionGrantStore(directory, new InMemoryAuditSink());
        foreach (var grant in original) seed.Add(grant, TestContext.Current.CancellationToken);
        await Task.WhenAll(original.Select((grant, index) => Task.Run(() =>
        {
            var store = new FilePermissionGrantStore(directory, new InMemoryAuditSink());
            store.Add(replacement[index], TestContext.Current.CancellationToken);
            Assert.True(store.Revoke(workspace, grant.Id, TestContext.Current.CancellationToken));
        }, TestContext.Current.CancellationToken)));
        Assert.Equal(replacement.Select(grant => grant.Id.ToString()).Order(),
            seed.List(workspace).Select(grant => grant.Id.ToString()).Order());
        Assert.Equal(72, File.ReadAllLines(Path.Combine(directory, "permission-grants.audit.tsv")).Length);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp*"));
    }

    [Fact]
    public async Task A_competing_os_lease_can_be_cancelled_without_changing_grants()
    {
        var directory = Path.Combine(Path.GetTempPath(), "omnicore-grant-lease-" + Guid.NewGuid().ToString("N"));
        var workspace = WorkspaceId.Parse("lease-workspace");
        var store = new FilePermissionGrantStore(directory, new InMemoryAuditSink());
        var existing = Grant(workspace);
        store.Add(existing, TestContext.Current.CancellationToken);
        var file = Path.Combine(directory, "permission-grants.tsv");
        var before = File.ReadAllBytes(file);
        using var competing = new FileStream(file + ".lease", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(
            () => new FilePermissionGrantStore(directory, new InMemoryAuditSink()).Revoke(workspace, existing.Id, cancel.Token),
            TestContext.Current.CancellationToken));
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(existing.Id, Assert.Single(store.List(workspace)).Id);
        Assert.Single(File.ReadAllLines(Path.Combine(directory, "permission-grants.audit.tsv")));
    }
}
