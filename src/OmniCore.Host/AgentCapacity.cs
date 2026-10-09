using System.Runtime.CompilerServices;
using OmniCore.Abstractions;
using OmniCore.Domain;

namespace OmniCore.Host;

/// <summary>In-process Run scheduler. It admits bounded read lanes concurrently, serializes any
/// writer lane behind all readers, and orders waiters by descending priority then durable submit
/// order. Leases are deliberately process-local; replay never adopts uncertain work.</summary>
internal sealed class AgentCapacity
{
    internal sealed record Snapshot(int Active, int Waiting, bool WriterActive,
        IReadOnlySet<DelegationId> WaitingDelegations);

    private static readonly ConditionalWeakTable<IEventStore, AgentCapacity> Stores = new();
    private readonly object _gate = new();
    private readonly List<Lease> _active = [];
    private readonly List<Waiter> _waiting = [];
    private long _ticket;

    internal static AgentCapacity For(IEventStore store) => Stores.GetValue(store, _ => new());

    internal Lease Acquire(SessionId session, RunId run, DelegationId? delegation, int maxAgents,
        int priority, bool readOnly, CancellationToken token, long durableSequence = long.MaxValue)
    {
        if (maxAgents < 1) throw new ArgumentOutOfRangeException(nameof(maxAgents));
        if (priority is < -1000 or > 1000) throw new ArgumentOutOfRangeException(nameof(priority));
        var waiter = new Waiter(session, run, delegation, maxAgents, priority, durableSequence, readOnly,
            Interlocked.Increment(ref _ticket), CancellationTokenSource.CreateLinkedTokenSource(token));
        lock (_gate)
        {
            _waiting.Add(waiter);
            Monitor.PulseAll(_gate);
            try
            {
                while (true)
                {
                    waiter.Cancellation.Token.ThrowIfCancellationRequested();
                    var first = _waiting.Where(candidate => candidate.Session == session && candidate.Run == run)
                        .OrderByDescending(candidate => candidate.Priority).ThenBy(candidate => candidate.DurableSequence)
                        .ThenBy(candidate => candidate.Ticket)
                        .FirstOrDefault();
                    if (ReferenceEquals(first, waiter) && CanGrant(waiter))
                    {
                        _waiting.Remove(waiter);
                        waiter.Cancellation.Dispose();
                        var lease = new Lease(this, session, run, delegation, readOnly, token);
                        _active.Add(lease);
                        Monitor.PulseAll(_gate);
                        return lease;
                    }
                    Monitor.Wait(_gate, TimeSpan.FromMilliseconds(100));
                }
            }
            catch
            {
                _waiting.Remove(waiter);
                waiter.Cancellation.Dispose();
                Monitor.PulseAll(_gate);
                throw;
            }
        }
    }

    internal Lease? TryAcquire(SessionId session, RunId run, DelegationId? delegation, int maxAgents,
        int priority, bool readOnly, CancellationToken token, long durableSequence = long.MaxValue)
    {
        if (maxAgents < 1) throw new ArgumentOutOfRangeException(nameof(maxAgents));
        if (priority is < -1000 or > 1000) throw new ArgumentOutOfRangeException(nameof(priority));
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var prior = _waiting.Where(candidate => candidate.Session == session && candidate.Run == run)
                .OrderByDescending(candidate => candidate.Priority).ThenBy(candidate => candidate.DurableSequence)
                .ThenBy(candidate => candidate.Ticket).FirstOrDefault();
            if (prior is not null && (prior.Priority > priority
                || prior.Priority == priority && prior.DurableSequence <= durableSequence)) return null;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var probe = new Waiter(session, run, delegation, maxAgents, priority, durableSequence, readOnly,
                long.MaxValue, cancellation);
            if (!CanGrant(probe)) return null;
            var lease = new Lease(this, session, run, delegation, readOnly, token);
            _active.Add(lease);
            Monitor.PulseAll(_gate);
            return lease;
        }
    }

    internal Snapshot ReadSnapshot(SessionId session, RunId run)
    {
        lock (_gate)
        {
            var active = _active.Where(lease => lease.Session == session && lease.Run == run).ToArray();
            var waiting = _waiting.Where(item => item.Session == session && item.Run == run).ToArray();
            return new(active.Length, waiting.Length, active.Any(lease => !lease.ReadOnly),
                waiting.Where(item => item.Delegation is not null).Select(item => item.Delegation!).ToHashSet());
        }
    }

    internal bool IsActive(SessionId session, RunId run, DelegationId delegation)
    {
        lock (_gate) return _active.Any(item => item.Session == session && item.Run == run
            && item.Delegation == delegation);
    }

    internal bool Cancel(SessionId session, RunId run, DelegationId delegation, Action? persistIntent = null)
    {
        lock (_gate)
        {
            var lease = _active.FirstOrDefault(item => item.Session == session && item.Run == run
                && item.Delegation == delegation);
            var waiter = _waiting.FirstOrDefault(item => item.Session == session && item.Run == run
                && item.Delegation == delegation);
            if (lease is null && waiter is null) return false;
            persistIntent?.Invoke();
            // Do not run cancellation callbacks while holding the scheduler gate.
            if (lease is not null) _ = lease.Cancellation.CancelAsync();
            else _ = waiter!.Cancellation.CancelAsync();
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    internal bool CancelWaiting(SessionId session, RunId run, DelegationId delegation)
    {
        lock (_gate)
        {
            var waiter = _waiting.FirstOrDefault(item => item.Session == session && item.Run == run
                && item.Delegation == delegation);
            if (waiter is null) return false;
            _ = waiter.Cancellation.CancelAsync();
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    private bool CanGrant(Waiter waiter)
    {
        if (waiter.Delegation is { } delegation
            && _active.Any(item => item.Session == waiter.Session && item.Run == waiter.Run
                && item.Delegation == delegation)) return false;
        var active = _active.Where(item => item.Session == waiter.Session && item.Run == waiter.Run).ToArray();
        // The root agent owns one logical slot even between turns. Delegated readers may use
        // only the remaining MaxAgents-1 slots; an active root turn consumes its same slot.
        var rootLeaseActive = active.Any(item => item.Delegation is null);
        var occupied = active.Length + (waiter.Delegation is not null && !rootLeaseActive ? 1 : 0);
        if (occupied >= waiter.MaxAgents) return false;
        if (!waiter.ReadOnly) return _active.Count == 0;
        return _active.All(item => item.ReadOnly);
    }

    private void Release(Lease lease)
    {
        lock (_gate)
        {
            if (!_active.Remove(lease)) return;
            lease.Cancellation.Dispose();
            Monitor.PulseAll(_gate);
        }
    }

    private sealed record Waiter(SessionId Session, RunId Run, DelegationId? Delegation,
        int MaxAgents, int Priority, long DurableSequence, bool ReadOnly, long Ticket, CancellationTokenSource Cancellation);

    internal sealed class Lease : IDisposable
    {
        private readonly AgentCapacity _owner;
        private int _disposed;
        internal SessionId Session { get; }
        internal RunId Run { get; }
        internal DelegationId? Delegation { get; }
        internal bool ReadOnly { get; }
        internal CancellationTokenSource Cancellation { get; }

        internal Lease(AgentCapacity owner, SessionId session, RunId run, DelegationId? delegation,
            bool readOnly, CancellationToken token)
        {
            _owner = owner;
            Session = session;
            Run = run;
            Delegation = delegation;
            ReadOnly = readOnly;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.Release(this);
        }
    }
}
