namespace OmniCore.Abstractions;

/// <summary>Per-invocation observation of generation sends, not credential requests,
/// token usage or a billing receipt. Mutable counter flows across async provider code.
/// A zero count is unobserved, not evidence that a failed request was free.</summary>
public sealed class GenerationRequestAttemptScope : IDisposable
{
    private static readonly AsyncLocal<GenerationRequestAttemptScope?> Current = new();
    private readonly GenerationRequestAttemptScope? _previous;
    private long _count;
    private readonly Action<long>? _beforeSend;
    private readonly object _sendGate = new();
    private bool _disposed;
    private GenerationRequestAttemptScope(Action<long>? beforeSend)
    {
        _beforeSend = beforeSend;
        _previous = Current.Value;
        Current.Value = this;
    }
    public static GenerationRequestAttemptScope Enter(Action<long>? beforeSend = null) => new(beforeSend);
    public long ObservedSends => Interlocked.Read(ref _count);
    public static void RecordGenerationSend()
    {
        if (Current.Value is { } scope)
            lock (scope._sendGate)
            {
                scope._beforeSend?.Invoke(scope._count + 1);
                Interlocked.Increment(ref scope._count);
            }
    }
    public void Dispose()
    {
        if (_disposed) return;
        if (!ReferenceEquals(Current.Value, this)) throw new InvalidOperationException("Attempt scopes must close in nesting order.");
        Current.Value = _previous;
        _disposed = true;
    }
}
