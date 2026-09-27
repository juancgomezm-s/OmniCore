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
        var tokens = chars / 4 + (chars % 4 == 0 ? 0 : 1);
        return System.Threading.Tasks.Task.FromResult(tokens);
    }
}