namespace OmniCore.Infrastructure;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>
/// Token counter determinista para pruebas y para M1 (ADR-0042 §1): cuenta palabras × 1.
/// Nunca depende de un servidor; es la base del "deterministic budget" de la spec §82.
/// </summary>
public sealed class FakeTokenCounter : ITokenCounter
{
    public static readonly TokenizerId FakeId = TokenizerId.Parse("fake:words/1");

    public TokenizerId Id => FakeId;

    public TokenCountAccuracy Accuracy => TokenCountAccuracy.Estimated;

    public Task<int> CountAsync(ContextItem item, CancellationToken cancellationToken)
    {
        var text = item.Content;
        if (text is null || text.Length == 0)
        {
            return System.Threading.Tasks.Task.FromResult(0);
        }

        var n = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ' || text[i] == '\n' || text[i] == '\t')
            {
                n += 1;
            }
        }

        return System.Threading.Tasks.Task.FromResult(n);
    }
}