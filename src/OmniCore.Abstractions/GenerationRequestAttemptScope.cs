namespace OmniCore.Abstractions;

/// <summary>Per-invocation observation of generation sends, not credential requests,
/// token usage or a billing receipt. Mutable counter flows across async provider code.
/// A zero count is unobserved, not evidence that a failed request was free.</summary>
public sealed class GenerationRequestAttemptScope : IDisposable
{
    private static readonly AsyncLocal<GenerationRequestAttemptScope?> Current = new();
    private readonly GenerationRequestAttemptScope? _previous;
    private long _count;
    private bool _disposed;
    private GenerationRequestAttemptScope()
    {
        _previous = Current.Value;
        Current.Value = this;
    }
    public static GenerationRequestAttemptScope Enter() => new();
    public long ObservedSends => Interlocked.Read(ref _count);
    public static void RecordGenerationSend()
    {
        if (Current.Value is { } scope) Interlocked.Increment(ref scope._count);
    }
    public void Dispose()
    {
        if (_disposed) return;
        if (!ReferenceEquals(Current.Value, this)) throw new InvalidOperationException("Attempt scopes must close in nesting order.");
        Current.Value = _previous;
        _disposed = true;
    }
}
