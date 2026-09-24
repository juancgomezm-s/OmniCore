using System.Reflection;

namespace OmniCore.Cli;

internal static class CliClient
{
    public static Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var version = typeof(CliClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        Console.WriteLine($"omni {version}");
        Console.WriteLine("Esqueleto de OmniCore: el runtime (M1) aún no está implementado.");
        return Task.FromResult(0);
    }
}
