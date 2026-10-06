using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Domain;
using OmniCore.Host;
using OmniCore.Models;

namespace OmniCore.Tests;

public sealed class RuntimeBuildIdentityTests
{
    [Fact]
    public void Host_identity_matches_the_established_qualification_format()
    {
        var hostAssembly = typeof(OmniServer).Assembly;
        var version = hostAssembly.GetName().Version?.ToString() ?? "";
        var expected = version + "/" + hostAssembly.ManifestModule.ModuleVersionId.ToString("D");

        Assert.Equal(expected, RuntimeBuildIdentity.ForAssembly(hostAssembly));

        var model = new ModelDefinition("fixture-model", "fixture-provider", 8192, 7000, 1024);
        var qualificationKey = ModelQualificationHost.QualificationKeyFor(model, provider: null);
        Assert.Equal(expected, qualificationKey.RuntimeBuild);
    }

    [Fact]
    public void Runtime_build_component_hashes_canonical_real_assembly_metadata_deterministically()
    {
        var hostAssembly = typeof(OmniServer).Assembly;
        var canonicalJson = RuntimeBuildIdentity.CanonicalJsonFor(hostAssembly);
        var component = RuntimeBuildIdentity.ComponentFor(hostAssembly);
        var repeated = RuntimeBuildIdentity.ComponentFor(hostAssembly);
        using var parsed = JsonDocument.Parse(canonicalJson);

        Assert.Equal("runtime.build", component.Name);
        Assert.Equal("1", component.Version);
        Assert.Equal(repeated, component);
        Assert.Null(component.Content);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))),
            component.Hash.Value);
        Assert.Equal(hostAssembly.GetName().Name ?? "", parsed.RootElement.GetProperty("assemblyName").GetString());
        Assert.Equal(hostAssembly.GetName().Version?.ToString() ?? "",
            parsed.RootElement.GetProperty("version").GetString());
        Assert.Equal(hostAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "",
            parsed.RootElement.GetProperty("informationalVersion").GetString());
        Assert.Equal(hostAssembly.ManifestModule.ModuleVersionId.ToString("D"),
            parsed.RootElement.GetProperty("moduleVersionId").GetString());
        Assert.Equal(canonicalJson, RuntimeBuildIdentity.CanonicalJsonFor(hostAssembly));
    }
}
