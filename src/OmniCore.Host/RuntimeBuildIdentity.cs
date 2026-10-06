namespace OmniCore.Host;

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniCore.Domain;

/// <summary>Reports the host assembly's real build identity for qualification and Turn fingerprints.</summary>
internal static class RuntimeBuildIdentity
{
    internal const string FingerprintComponentName = "runtime.build";
    internal const string FingerprintComponentVersion = "1";

    /// <summary>
    /// Preserves the established qualification identity format: assembly version, slash, and
    /// module version id. Informational version is deliberately not parsed as a commit.
    /// </summary>
    internal static string ForAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var version = assembly.GetName().Version;
        return (version?.ToString() ?? "") + "/" + assembly.ManifestModule.ModuleVersionId.ToString("D");
    }

    /// <summary>Canonical, ordered JSON of metadata reported by the assembly itself.</summary>
    internal static string CanonicalJsonFor(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var name = assembly.GetName();
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "";

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("assemblyName", name.Name ?? "");
            writer.WriteString("version", name.Version?.ToString() ?? "");
            writer.WriteString("informationalVersion", informationalVersion);
            writer.WriteString("moduleVersionId", assembly.ManifestModule.ModuleVersionId.ToString("D"));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Creates a hash-only component; no build metadata is copied into a CAS artifact.</summary>
    internal static FingerprintComponent ComponentFor(Assembly assembly)
    {
        var canonicalJson = CanonicalJsonFor(assembly);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
        return new FingerprintComponent(FingerprintComponentName, FingerprintComponentVersion,
            ContentHash.Sha256(hash));
    }
}
