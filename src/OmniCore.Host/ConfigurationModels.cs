namespace OmniCore.Host;

using YamlDotNet.Serialization;

/// <summary>YAML DTOs are intentionally data-only; generated accessors keep loading AOT-safe.</summary>
public sealed class ProvidersFileYaml
{
    public Dictionary<string, ProviderFileYaml>? Providers { get; set; }
}

public sealed class ProviderFileYaml
{
    public string? Kind { get; set; }
    public string? Family { get; set; }
    public string? BaseUrl { get; set; }
    public string? CaCertificate { get; set; }
    public string? AuthRef { get; set; }
}

public sealed class ModelsFileYaml
{
    public Dictionary<string, ModelFileYaml>? Models { get; set; }
}

public sealed class ModelFileYaml
{
    public string? Provider { get; set; }
    public long? Context { get; set; }
    public long? RecommendedUsableContext { get; set; }
    public long? MaxOutput { get; set; }
    public double? ParametersBillions { get; set; }
}

public sealed class WorkspaceSettingsYaml
{
    public string? DefaultModel { get; set; }
    public Dictionary<string, string>? PermissionRestrictions { get; set; }
}

public sealed class TrustFileYaml
{
    public Dictionary<string, TrustEntryYaml>? Workspaces { get; set; }
}

public sealed class TrustEntryYaml
{
    public string? CanonicalPath { get; set; }
    public bool Trusted { get; set; }
}

[YamlStaticContext]
[YamlSerializable(typeof(ProvidersFileYaml))]
[YamlSerializable(typeof(ProviderFileYaml))]
[YamlSerializable(typeof(ModelsFileYaml))]
[YamlSerializable(typeof(ModelFileYaml))]
[YamlSerializable(typeof(WorkspaceSettingsYaml))]
[YamlSerializable(typeof(TrustFileYaml))]
[YamlSerializable(typeof(TrustEntryYaml))]
public sealed partial class OmniYamlStaticContext : StaticContext
{
}
