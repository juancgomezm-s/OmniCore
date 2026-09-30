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
    public decimal? InputPricePerMillionUsd { get; set; }
    public decimal? OutputPricePerMillionUsd { get; set; }

    /// <summary>Legacy auth syntax, parsed explicitly from YAML nodes to support scalar/map forms AOT-safely.</summary>
    public ProviderAuthYaml? Auth { get; set; }
}

/// <summary>Typed representation of legacy <c>auth: none</c> or <c>auth: { apiKey: ref }</c>.</summary>
public sealed class ProviderAuthYaml
{
    public bool IsNone { get; set; }
    public string? ApiKey { get; set; }
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
    public decimal? InputPricePerMillionUsd { get; set; }
    public decimal? OutputPricePerMillionUsd { get; set; }
}

public sealed class WorkspaceSettingsYaml
{
    public string? DefaultModel { get; set; }
    public Dictionary<string, string>? PermissionRestrictions { get; set; }
    public WorkspaceGatesYaml? Gates { get; set; }
}

/// <summary>Configured completion commands. Each sequence is literal argv: element zero is executable.</summary>
public sealed class WorkspaceGatesYaml
{
    public List<string>? Build { get; set; }
    public List<string>? Test { get; set; }
    public bool Acceptance { get; set; }
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
[YamlSerializable(typeof(ProviderAuthYaml))]
[YamlSerializable(typeof(ModelsFileYaml))]
[YamlSerializable(typeof(ModelFileYaml))]
[YamlSerializable(typeof(WorkspaceSettingsYaml))]
[YamlSerializable(typeof(WorkspaceGatesYaml))]
[YamlSerializable(typeof(TrustFileYaml))]
[YamlSerializable(typeof(TrustEntryYaml))]
public sealed partial class OmniYamlStaticContext : StaticContext
{
}
