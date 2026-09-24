using System.Xml.Linq;

namespace OmniCore.ArchitectureTests;

/// <summary>
/// Hace cumplir el grafo de dependencias de docs/adr/0009-grafo-de-dependencias.md (spec §80)
/// leyendo los ProjectReference declarados en cada .csproj de src/.
/// </summary>
public sealed class ProjectDependencyTests
{
    // Referencias permitidas por proyecto. Un proyecto ausente aquí hace fallar el test:
    // todo proyecto nuevo debe declarar explícitamente su frontera.
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["OmniCore.Domain"] = [],
        ["OmniCore.Abstractions"] = ["OmniCore.Domain"],
        ["OmniCore.Protocol"] = [],
        ["OmniCore.Sandbox"] = [],
        ["OmniCore.Engine"] = ["OmniCore.Abstractions", "OmniCore.Domain"],
        ["OmniCore.Context"] = ["OmniCore.Abstractions", "OmniCore.Domain"],
        ["OmniCore.Models"] = ["OmniCore.Abstractions", "OmniCore.Domain"],
        ["OmniCore.Tools"] = ["OmniCore.Abstractions", "OmniCore.Domain"],
        ["OmniCore.Security"] = ["OmniCore.Abstractions", "OmniCore.Domain"],
        ["OmniCore.Execution"] = ["OmniCore.Abstractions", "OmniCore.Domain", "OmniCore.Sandbox"],
        ["OmniCore.Infrastructure"] = ["OmniCore.Abstractions", "OmniCore.Domain"],
        ["OmniCore.Host"] =
        [
            "OmniCore.Abstractions", "OmniCore.Domain", "OmniCore.Engine", "OmniCore.Context", "OmniCore.Models",
            "OmniCore.Tools", "OmniCore.Security", "OmniCore.Execution", "OmniCore.Infrastructure", "OmniCore.Protocol",
        ],
        ["OmniCore.Cli"] = ["OmniCore.Host", "OmniCore.Protocol"],
    };

    public static TheoryData<string> Projects()
    {
        var data = new TheoryData<string>();
        foreach (var name in SourceProjects().Keys.Order(StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void Project_references_only_allowed_projects(string project)
    {
        Assert.True(Allowed.TryGetValue(project, out var allowed),
            $"{project} no tiene frontera declarada en {nameof(ProjectDependencyTests)}.{nameof(Allowed)}.");

        var actual = ProjectReferences(SourceProjects()[project]);
        var forbidden = actual.Except(allowed, StringComparer.Ordinal).ToArray();

        Assert.True(forbidden.Length == 0, $"{project} referencia proyectos no permitidos: {string.Join(", ", forbidden)}");
    }

    [Fact]
    public void Every_declared_boundary_has_a_project()
    {
        var missing = Allowed.Keys.Except(SourceProjects().Keys, StringComparer.Ordinal).ToArray();

        Assert.True(missing.Length == 0, $"Fronteras declaradas sin proyecto en src/: {string.Join(", ", missing)}");
    }

    [Theory]
    [InlineData("OmniCore.Domain")]
    [InlineData("OmniCore.Abstractions")]
    [InlineData("OmniCore.Protocol")]
    public void Pure_projects_have_no_package_references(string project)
    {
        var packages = XDocument.Load(SourceProjects()[project])
            .Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include"))
            .ToArray();

        Assert.True(packages.Length == 0, $"{project} debe ser puro y no tener paquetes: {string.Join(", ", packages)}");
    }

    private static Dictionary<string, string> SourceProjects() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetFileNameWithoutExtension(p), StringComparer.Ordinal);

    private static string[] ProjectReferences(string csprojPath) =>
        XDocument.Load(csprojPath)
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension((string)e.Attribute("Include")!.Value.Replace('\\', '/')))
            .ToArray();

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OmniCore.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No se encontró OmniCore.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
