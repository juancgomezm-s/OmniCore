namespace OmniCore.Infrastructure;

using OmniCore.Abstractions;

/// <summary>
/// SecretProvider simple para M2: lee de variables de entorno y de un mapa en memoria.
/// NO persiste secretos; el almacenamiento real (Windows Credential Manager / DPAPI) es M2+
/// (ADR-0011 §3.1, ADR-0018).
/// </summary>
public sealed class SimpleSecretProvider : ISecretProvider
{
    private readonly Dictionary<string, string> _secrets = new();

    private readonly string _envPrefix;

    public SimpleSecretProvider(string envPrefix)
    {
        _envPrefix = envPrefix;
    }

    public SimpleSecretProvider With(string secretRef, string value)
    {
        _secrets[secretRef] = value;
        return this;
    }

    public Secret GetSecret(string secretRef, CancellationToken cancellationToken)
    {
        var envName = _envPrefix + secretRef.Replace("-", "_").ToUpperInvariant();
        var fromEnv = System.Environment.GetEnvironmentVariable(envName);
        if (fromEnv is not null && fromEnv.Length > 0)
        {
            return Secret.Of(fromEnv);
        }

        if (_secrets.TryGetValue(secretRef, out var v))
        {
            return Secret.Of(v!);
        }

        throw new InvalidOperationException("Secreto no configurado: " + secretRef);
    }
}