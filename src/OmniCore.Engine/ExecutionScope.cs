namespace OmniCore.Engine;

using OmniCore.Domain;

/// <summary>Ambient entity attribution for events produced while executing one turn.</summary>
public sealed record ExecutionScopeState(
    RunId? RunId = null,
    TaskId? TaskId = null,
    LaneId? LaneId = null,
    TurnId? TurnId = null,
    ToolCallId? ToolCallId = null,
    ExecutionId? ExecutionId = null);

/// <summary>Async-flowing execution context. Nested scopes restore their parent on disposal.</summary>
public static class ExecutionScope
{
    private static readonly AsyncLocal<ExecutionScopeState?> Ambient = new();

    public static ExecutionScopeState? Current => Ambient.Value;

    public static IDisposable Begin(ExecutionScopeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var previous = Ambient.Value;
        Ambient.Value = state;
        return new Restore(previous);
    }

    private sealed class Restore(ExecutionScopeState? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Ambient.Value = previous;
        }
    }
}
