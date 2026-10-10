namespace OmniCore.Host;

using OmniCore.Abstractions;

/// <summary>Host-wide exclusion keyed by the resolved physical workspace root. Shared across
/// sessions and executors so filesystem/process/worktree writes cannot race within this Host.</summary>
internal sealed class HostWorkspaceWriteLeases : IWorkspaceWriteLeaseProvider
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static HostWorkspaceWriteLeases Shared { get; } = new();

    private readonly object _sync = new();
    private readonly Dictionary<string, GateEntry> _gates = new(PathComparer);

    private HostWorkspaceWriteLeases() { }

    public IDisposable Acquire(string workspaceRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var physical = ProjectIdentity.ResolvePhysicalWorkspaceRoot(workspaceRoot);
        var canonical = ProjectIdentity.CanonicalWorkspacePath(physical);
        GateEntry entry;
        lock (_sync)
        {
            if (!_gates.TryGetValue(canonical, out entry!))
                _gates[canonical] = entry = new GateEntry();
            entry.References++;
        }
        try
        {
            entry.Gate.Wait(cancellationToken);
            return new Lease(this, canonical, entry);
        }
        catch
        {
            ReleaseReference(canonical, entry);
            throw;
        }
    }

    private void ReleaseReference(string key, GateEntry entry)
    {
        lock (_sync)
        {
            entry.References--;
            if (entry.References == 0 && _gates.TryGetValue(key, out var current)
                && ReferenceEquals(current, entry))
            {
                _gates.Remove(key);
                entry.Gate.Dispose();
            }
        }
    }

    private sealed class GateEntry { public SemaphoreSlim Gate { get; } = new(1, 1); public int References; }

    private sealed class Lease(HostWorkspaceWriteLeases owner, string key, GateEntry entry) : IDisposable
    {
        private HostWorkspaceWriteLeases? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            entry.Gate.Release();
            current.ReleaseReference(key, entry);
        }
    }
}
