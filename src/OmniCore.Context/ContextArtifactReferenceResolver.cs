namespace OmniCore.Context;

using OmniCore.Abstractions;
using OmniCore.Domain;

/// <summary>Resuelve refs CAS externalizadas como sha256:&lt;hex&gt; sin modificar el artifact.</summary>
public sealed class ContextArtifactReferenceResolver
{
    private readonly IArtifactStore _artifacts;
    public ContextArtifactReferenceResolver(IArtifactStore artifacts) => _artifacts = artifacts;

    public string? Read(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var hashText = reference.StartsWith("artifact=", StringComparison.Ordinal)
            ? reference[9..]
            : reference;
        var separator = hashText.IndexOf(':');
        if (separator <= 0 || !hashText[..separator].Equals("sha256", StringComparison.Ordinal)) return null;
        var value = hashText[(separator + 1)..];
        if (value.Length != 64 || value.Any(ch => !Uri.IsHexDigit(ch))) return null;
        return _artifacts.GetText(ContentHash.Sha256(value.ToLowerInvariant()));
    }
}
