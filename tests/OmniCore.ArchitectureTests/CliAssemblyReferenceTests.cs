using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace OmniCore.ArchitectureTests;

/// <summary>Verifica la frontera binaria de omni.dll definida en ADR-0009 §2.4 y ADR-0019.</summary>
public sealed class CliAssemblyReferenceTests
{
    private static readonly HashSet<string> AllowedCliReferences = new(StringComparer.Ordinal)
    {
        "OmniCore.Protocol",
        "OmniCore.Client",
        "OmniCore.Host",
    };

    // Excepciones heredadas existentes: el CLI actual referencia estos assemblies por el código
    // ya conectado a componentes de Engine/Host. No se reescribe aquí; una referencia nueva fuera
    // de esta lista falla y obliga a revisar explícitamente la frontera del CLI.
    private static readonly HashSet<string> KnownCurrentViolations = new(StringComparer.Ordinal)
    {
        "OmniCore.Abstractions",
        "OmniCore.Context",
        "OmniCore.Domain",
        "OmniCore.Engine",
        "OmniCore.Infrastructure",
        "OmniCore.Models",
        "OmniCore.Tools",
    };

    [Fact]
    public void Omni_assembly_references_only_allowed_projects_or_documented_legacy_exceptions()
    {
        var assemblyPath = CliAssemblyPath();
        using var file = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(file);
        var metadata = peReader.GetMetadataReader();
        var references = metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .Where(name => name.StartsWith("OmniCore.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var violations = references.Except(AllowedCliReferences, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(KnownCurrentViolations.SetEquals(violations),
            "Referencias IL de OmniCore.Cli cambiaron. Las permitidas son "
            + string.Join(", ", AllowedCliReferences.Order(StringComparer.Ordinal))
            + "; excepciones heredadas registradas: "
            + string.Join(", ", KnownCurrentViolations.Order(StringComparer.Ordinal))
            + "; actuales: " + string.Join(", ", violations.Order(StringComparer.Ordinal))
            + ". Un assembly nuevo es una violación y debe resolverse o añadirse mediante decisión arquitectónica explícita.");
    }

    private static string CliAssemblyPath()
    {
        var root = RepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var path = Path.Combine(root, "src", "OmniCore.Cli", "bin", configuration, "net10.0", "omni.dll");
        Assert.True(File.Exists(path), "No se encontró el assembly compilado de omni: " + path);
        return path;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OmniCore.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("No se encontró OmniCore.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
