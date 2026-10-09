using System.Runtime.CompilerServices;
using OmniCore.Abstractions;
using OmniCore.Domain;

namespace OmniCore.Host;

/// <summary>One actual in-process execution slot per store. Root and children share it.
/// No PID/heartbeat is mistaken for capacity. Unknown ownership after restart is not adopted.</summary>
internal sealed class AgentCapacity
{
    private static readonly ConditionalWeakTable<IEventStore, AgentCapacity> Stores = new();
    private readonly object _gate = new();
    private Lease? _active;
    internal static AgentCapacity For(IEventStore store) => Stores.GetValue(store, _ => new());
    internal Lease? TryAcquire(SessionId session, RunId run, DelegationId? delegation,
        CancellationToken token)
    {
        lock (_gate)
        {
            if (_active is not null) return null;
            return _active = new(this, session, run, delegation, token);
        }
    }
    internal bool Cancel(SessionId session, RunId run, DelegationId delegation, Action? persistIntent = null)
    {
        lock (_gate)
        {
            if (_active?.Session != session || _active.Run != run || _active.Delegation != delegation) return false;
            persistIntent?.Invoke();
            // Do not run arbitrary provider cancellation callbacks while holding the journal gate.
            _ = _active.Cancellation.CancelAsync();
            return true;
        }
    }
    internal sealed class Lease : IDisposable
    {
        private readonly AgentCapacity _owner;
        internal SessionId Session { get; }
        internal RunId Run { get; }
        internal DelegationId? Delegation { get; }
        internal CancellationTokenSource Cancellation { get; }
        internal Lease(AgentCapacity owner, SessionId session, RunId run, DelegationId? delegation, CancellationToken token)
        { _owner = owner; Session = session; Run = run; Delegation = delegation; Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); }
        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_owner._active != this) return;
                _owner._active = null;
                Cancellation.Dispose();
            }
        }
    }
}
