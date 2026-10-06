namespace OmniCore.Domain;

/// <summary>Componente versionado, respaldado por hash y referencia CAS opcional (ADR-0017/0046).</summary>
public sealed record FingerprintComponent(string Name, string Version, ContentHash Hash, ArtifactRef? Content = null);
