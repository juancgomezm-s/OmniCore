namespace OmniCore.Domain;

/// <summary>SelectedProjection only: explicit factual item selection, never an authority or visibility grant.</summary>
public sealed class ContextInheritancePolicy
{
    public static ContextInheritancePolicy Default { get; } = new(Array.Empty<string>());
    public IReadOnlyList<string> SelectedItemIds { get; }

    public ContextInheritancePolicy(IReadOnlyList<string> selectedItemIds)
    {
        ArgumentNullException.ThrowIfNull(selectedItemIds);
        var copy = selectedItemIds.ToArray();
        if (copy.Any(string.IsNullOrWhiteSpace) || copy.Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("Inherited items must have explicit unique identities.", nameof(selectedItemIds));
        SelectedItemIds = Array.AsReadOnly(copy);
    }
}
