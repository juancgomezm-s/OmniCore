using OmniCore.Domain;

namespace OmniCore.Abstractions;

/// <summary>Read-only Core Git evidence. Never accepts model-supplied revisions or commands.</summary>
public interface IGitActivityReader
{
    Task<GitActivitySnapshot?> ReadActivityAsync(string directory, CancellationToken cancellationToken);
}
