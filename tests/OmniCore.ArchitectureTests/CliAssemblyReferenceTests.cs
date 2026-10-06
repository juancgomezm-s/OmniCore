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

    private static readonly HashSet<string> KnownCurrentViolations = new(StringComparer.Ordinal);

    [Fact]
    public void Omni_assembly_references_only_allowed_projects()
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
        Assert.Empty(KnownCurrentViolations);
        Assert.True(violations.Count == 0,
            "Referencias IL no permitidas de OmniCore.Cli: " + string.Join(", ", violations.Order(StringComparer.Ordinal))
            + ". Las únicas permitidas son "
            + string.Join(", ", AllowedCliReferences.Order(StringComparer.Ordinal)) + ".");
    }

    private static string CliAssemblyPath()
    {
        // Inspect the CLI copied from this build's ProjectReference, including isolated
        // outputs used while a user is running the normal bin/Debug executable.
        var adjacent = Path.Combine(AppContext.BaseDirectory, "omni.dll");
        if (File.Exists(adjacent)) return adjacent;
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
