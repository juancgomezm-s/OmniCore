using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;

namespace OmniCore.Host;

/// <summary>Transient wake signal only; every delivery and lifecycle transition remains journal-backed.</summary>
internal sealed class MailboxWaitRegistry
{
    private static readonly ConditionalWeakTable<IEventStore, MailboxWaitRegistry> Registries = new();
    private readonly object _gate = new();
    private readonly Dictionary<(SessionId Session, RunId Run, ExecutionId Execution), Waiter> _waiters = new();

    internal sealed record Waiter(ToolCallId ToolCall, TaskCompletionSource<string> Signal,
        ModelRoute Route, BillingMode Billing);

    internal static MailboxWaitRegistry For(IEventStore store) => Registries.GetValue(store, _ => new());

    internal Waiter Register(SessionId session, RunId run, ExecutionId execution, ToolCallId toolCall,
        ModelRoute route, BillingMode billing)
    {
        lock (_gate)
        {
            var key = (session, run, execution);
            if (_waiters.ContainsKey(key)) throw new InvalidOperationException("This execution already has an active mailbox receive.");
            var waiter = new Waiter(toolCall, new(TaskCreationOptions.RunContinuationsAsynchronously), route, billing);
            _waiters.Add(key, waiter);
            return waiter;
        }
    }

    internal bool TryGet(SessionId session, RunId run, ExecutionId execution, out Waiter waiter)
    {
        lock (_gate) return _waiters.TryGetValue((session, run, execution), out waiter!);
    }

    internal bool Signal(SessionId session, RunId run, ExecutionId execution, ToolCallId toolCall, string delivery)
    {
        lock (_gate)
        {
            if (!_waiters.TryGetValue((session, run, execution), out var waiter) || waiter.ToolCall != toolCall) return false;
            return waiter.Signal.TrySetResult(delivery);
        }
    }

    internal void Remove(SessionId session, RunId run, ExecutionId execution, Waiter waiter)
    {
        lock (_gate)
        {
            var key = (session, run, execution);
            if (_waiters.TryGetValue(key, out var current) && ReferenceEquals(current, waiter)) _waiters.Remove(key);
        }
    }
}
