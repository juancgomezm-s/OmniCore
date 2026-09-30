namespace OmniCore.Infrastructure;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Token counter heurístico real para el runtime (ADR-0042 §1): ~0.25 tokens/char
/// (4 chars por token, estándar en modelos BPE grandes). Determinista y sin servidor;
/// sustituye a FakeTokenCounter en el Host (el fake queda para los tests del materializer).
/// </summary>
public sealed class HeuristicTokenCounter : ITokenCounter
{
    /// <summary>Margen de seguridad por defecto del ADR-0042 §1 (10 %), redondeado hacia arriba.</summary>
    public const double DefaultSafetyMargin = 0.10;

    private readonly double _safetyMargin;
    private readonly TokenEstimateCalibrator? _calibrator;
    private readonly string? _calibrationKey;

    public HeuristicTokenCounter() : this(DefaultSafetyMargin) { }

    /// <summary>
    /// Con <paramref name="calibrator"/>, la razón caracteres/token sale de los conteos exactos del
    /// tokenizer <paramref name="calibrationKey"/> (M5); sin muestras suficientes sigue siendo 4.
    /// </summary>
    public HeuristicTokenCounter(double safetyMargin, TokenEstimateCalibrator? calibrator, string? calibrationKey)
        : this(safetyMargin)
    {
        _calibrator = calibrator;
        _calibrationKey = calibrationKey;
    }

    public HeuristicTokenCounter(double safetyMargin)
    {
        if (safetyMargin < 0 || double.IsNaN(safetyMargin))
            throw new ArgumentOutOfRangeException(nameof(safetyMargin));
        _safetyMargin = safetyMargin;
    }

    public static readonly TokenizerId HeuristicId = TokenizerId.Parse("heuristic:chars4/1");

    public TokenizerId Id => HeuristicId;

    public TokenCountAccuracy Accuracy => TokenCountAccuracy.Estimated;

    public Task<int> CountAsync(ContextItem item, CancellationToken cancellationToken)
    {
        var text = item.Content;
        if (text is null || text.Length == 0)
        {
            return System.Threading.Tasks.Task.FromResult(0);
        }

        var chars = text.Length;
        var charsPerToken = _calibrator is not null && _calibrationKey is not null
            ? _calibrator.CharsPerToken(_calibrationKey) : 4.0;
        var tokens = (int)Math.Ceiling(Math.Round(chars * (1 + _safetyMargin) / charsPerToken, 6));
        return System.Threading.Tasks.Task.FromResult(tokens);
    }
}