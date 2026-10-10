using OmniCore.Abstractions;

namespace OmniCore.Tests;

/// <summary>
/// ICredentialStore en memoria para tests: mismo contrato que FileCredentialStore (registro en
/// SecretRedactorRegistry al leer) pero sin disco ni criptografía, para que los tests de F4
/// midan la lógica del store y no la de DPAPI.
/// </summary>
internal sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public IReadOnlyDictionary<string, string> Values => new Dictionary<string, string>(_values);

    public int SaveCount { get; private set; }

    public void Save(string key, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _values[key] = value;
            SaveCount++;
        }
    }

    public string? Load(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? stored;
        lock (_gate)
        {
            _values.TryGetValue(key, out stored);
        }

        if (stored is null)
        {
            return null;
        }

        // Contrato de ICredentialStore (ADR-0018): todo valor devuelto se registra antes de salir.
        SecretRedactorRegistry.Register(stored);
        return stored;
    }

    public void Delete(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _values.Remove(key);
        }
    }
}
