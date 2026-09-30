namespace OmniCore.Tests;

using OmniCore.Abstractions;
using Xunit;

public class FileReadRegistryPathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "omni-frr-root");

    [Fact]
    public void RelativeDotPrefixedAndAbsoluteSpellingsAreTheSameFile()
    {
        var reg = new FileReadRegistry(Root);
        reg.RecordRead("src/a.cs", "v1");

        Assert.True(reg.Matches("./src/a.cs", "v1"));
        Assert.True(reg.Matches("src/x/../a.cs", "v1"));
        Assert.True(reg.Matches(Path.Combine(Root, "src", "a.cs"), "v1"));
        Assert.Equal(1, reg.Size());
    }

    [Fact]
    public void RelativeSpellingsUnifyWithoutRoot()
    {
        var reg = new FileReadRegistry();
        reg.RecordRead("./src//a.cs", "v1");
        Assert.True(reg.HasRead("src/a.cs"));
        Assert.True(reg.HasRead("src\\b\\..\\a.cs"));
    }

    [Fact]
    public void DifferentFileIsNotRead()
    {
        var reg = new FileReadRegistry(Root);
        reg.RecordRead("src/a.cs", "v1");
        Assert.False(reg.HasRead("src/b.cs"));
        Assert.False(reg.Matches("src/b.cs", "v1"));
        Assert.False(reg.Matches("src/a.cs", "v2"));
    }

    [Fact]
    public void CaseInsensitiveOnlyOnCaseInsensitivePlatforms()
    {
        var reg = new FileReadRegistry(Root);
        reg.RecordRead("src/a.cs", "v1");
        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        Assert.Equal(expected, reg.Matches("SRC/A.cs", "v1"));
    }

    [Fact]
    public void HostCreatedBoundaryRecognisesAbsoluteAndRelativeSpellings()
    {
        var key = OmniCore.Domain.ModelPolicyKey.For("p", "m");
        var effective = OmniCore.Domain.EffectiveModelPolicy.Resolve(key, null,
            new OmniCore.Domain.HarnessPolicy(OmniCore.Domain.ToolCallFormat.Native, OmniCore.Domain.ToolMode.Direct,
                16, OmniCore.Domain.GuidanceLevel.Full, 3, OmniCore.Domain.PlanControl.ModelDriven, 8));
        var a = OmniCore.Host.OmniCliRuntime.CreateBoundary(effective, Root);
        var b = OmniCore.Host.OmniCliRuntime.CreateBoundary(effective, Root);

        a.ReadRegistry().RecordRead("src/a.cs", "v1");

        Assert.True(a.ReadRegistry().Matches(Path.Combine(Root, "src", "a.cs"), "v1"));
        Assert.True(a.ReadRegistry().Matches("./src/a.cs", "v1"));
        Assert.NotSame(a.ReadRegistry(), b.ReadRegistry());
        Assert.False(b.ReadRegistry().HasRead("src/a.cs"));
    }

    [Fact]
    public async Task NoConfiguredModelSurfacesTheLocalizedKey()
    {
        var emptyConfig = Path.Combine(Path.GetTempPath(), "omni-noconfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyConfig);
        // Un provider sin modelos: el registro queda sin modelo por defecto.
        File.WriteAllText(Path.Combine(emptyConfig, "providers.yaml"), "providers:\n  p:\n    baseUrl: http://127.0.0.1:1\n");
        var previousConfig = Environment.GetEnvironmentVariable(OmniCore.Infrastructure.DefaultPlatformPaths.ConfigDirVariable);
        var previousModel = Environment.GetEnvironmentVariable("OMNI_MODEL");
        var lines = new List<string>();
        int code;
        try
        {
            Environment.SetEnvironmentVariable(OmniCore.Infrastructure.DefaultPlatformPaths.ConfigDirVariable, emptyConfig);
            Environment.SetEnvironmentVariable("OMNI_MODEL", null);
            code = await OmniCore.Host.OmniCliRuntime.Create(Root).AskAsync("hola", lines.Add, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OmniCore.Infrastructure.DefaultPlatformPaths.ConfigDirVariable, previousConfig);
            Environment.SetEnvironmentVariable("OMNI_MODEL", previousModel);
        }

        Assert.Equal(1, code);
        Assert.Contains(lines, l => l.Contains("models.noneConfigured"));
    }
}
