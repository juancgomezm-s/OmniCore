namespace OmniCore.Host;

/// <summary>
/// Composition root de OmniCore. Registra runtimes, stores y providers, y entrega el control a un cliente
/// (CLI hoy; stdio/named pipes cuando exista el Omni Protocol).
/// </summary>
public sealed class OmniHost
{
    private readonly string[] _args;

    private OmniHost(string[] args) => _args = args;

    public static OmniHost Create(string[] args) => new(args);

    public Task<int> RunAsync(Func<string[], CancellationToken, Task<int>> client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client(_args, cancellationToken);
    }
}
