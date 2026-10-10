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
    public string? Profile { get; set; }
    public string? BillingMode { get; set; }
    public string? AuthRef { get; set; }

    /// <summary><c>attach</c> (servidor ya levantado) o <c>managed</c> (lo lanza OmniCore), ADR-0011 §4.</summary>
    public string? Host { get; set; }
    public ManagedHostYaml? Managed { get; set; }
    public decimal? InputPricePerMillionUsd { get; set; }
    public decimal? OutputPricePerMillionUsd { get; set; }

    /// <summary>Legacy auth syntax, parsed explicitly from YAML nodes to support scalar/map forms AOT-safely.</summary>
    public ProviderAuthYaml? Auth { get; set; }

    /// <summary>
    /// Identidad OAuth de Claude declarada por el usuario (ADR-0011 §3.3, ADR-0039 §2). Ausente =
    /// el provider no usa login por cuenta. Se parsea de nodos YAML igual que <see cref="Auth"/>:
    /// las formas escalar y lista de <c>scopes</c> no caben en un DTO generado.
    /// </summary>
    public ProviderOAuthYaml? OAuth { get; set; }
}

/// <summary>
/// Sección <c>oauth:</c> de un provider en providers.yaml. Los valores son los que el usuario
/// elige: el runtime no tiene una identidad de cliente propia ni defaults (ADR-0011 rev. 5).
/// </summary>
public sealed class ProviderOAuthYaml
{
    public string? ClientId { get; set; }

    /// <summary>Plantilla con <c>{version}</c>, como la manda el wire de Claude Code.</summary>
    public string? UserAgent { get; set; }

    public List<string>? Scopes { get; set; }

    public string? AuthorizeUrl { get; set; }

    public string? TokenUrl { get; set; }

    /// <summary>Opcional: si falta se deriva del host de <see cref="TokenUrl"/>.</summary>
    public string? ProfileUrl { get; set; }

    /// <summary>Clave bajo la que se guarda el credential cifrado. Default: "&lt;provider&gt;-oauth".</summary>
    public string? SecretRef { get; set; }
}

/// <summary>Lanzamiento del servidor local con <c>host: managed</c>. El puerto y la API key los genera el runtime.</summary>
public sealed class ManagedHostYaml
{
    public string? Executable { get; set; }
    public List<string>? Args { get; set; }
    public string? WorkingDirectory { get; set; }
    public int? ReadinessTimeoutSeconds { get; set; }
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
    public RoutingYaml? Routing { get; set; }
}

/// <summary>Routing por tipo de tarea con alias del usuario (M5, spec §21); solo en el scope User.</summary>
public sealed class RoutingYaml
{
    public bool? PreferLocal { get; set; }
    public List<string>? Meta { get; set; }
    public List<string>? Exploration { get; set; }
    public List<string>? Implementation { get; set; }
    public List<string>? Reasoning { get; set; }
    public List<string>? Architecture { get; set; }
    public EscalationYaml? Escalation { get; set; }
}

/// <summary>Escalación explícita (spec §73): <c>auto</c>, <c>ask</c> o <c>deny</c> y la cadena de alias.</summary>
public sealed class EscalationYaml
{
    public string? Mode { get; set; }
    public List<string>? Chain { get; set; }
}

public sealed class ModelFileYaml
{
    public ReasoningCapabilityYaml? Reasoning { get; set; }
    public string? Provider { get; set; }
    public long? Context { get; set; }
    public long? RecommendedUsableContext { get; set; }
    public long? MaxOutput { get; set; }
    public double? ParametersBillions { get; set; }
    public List<string>? Aliases { get; set; }
    public decimal? InputPricePerMillionUsd { get; set; }
    public decimal? OutputPricePerMillionUsd { get; set; }
}

/// <summary>Declared facts only; omission remains unknown, not unsupported.</summary>
public sealed class ReasoningCapabilityYaml
{
    public bool? Supported { get; set; }
    public List<string>? EffortLevels { get; set; }
    public string? ReplayPolicy { get; set; }
    public int? UltraCodeBudgetTokens { get; set; }
    public int? UltraCodeOutputReserveTokens { get; set; }
}

public sealed class WorkspaceSettingsYaml
{
    public SidebarSettingsYaml? Sidebar { get; set; }
    public Dictionary<string, WidgetSettingsYaml>? Widgets { get; set; }
    public string? DefaultModel { get; set; }
    public Dictionary<string, string>? PermissionRestrictions { get; set; }
    public WorkspaceGatesYaml? Gates { get; set; }
}

/// <summary>User-scope settings from <c>&lt;config&gt;/settings.yaml</c> (ADR-0039).</summary>
public sealed class UserSettingsYaml
{
    public SidebarSettingsYaml? Sidebar { get; set; }
    public Dictionary<string, WidgetSettingsYaml>? Widgets { get; set; }
    public BudgetSettingsYaml? Budget { get; set; }
    public string? DefaultModel { get; set; }
    public WorkspaceGatesYaml? Gates { get; set; }

    /// <summary>Claves fijadas en User: Project y Workspace no las cambian (ADR-0039 §5).</summary>
    public List<string>? Locked { get; set; }
}

public sealed class SidebarSettingsYaml
{
    public bool? Visible { get; set; }
    public string? Mode { get; set; }
    public int? StackedMinWidth { get; set; }
    public int? TabbedMinWidth { get; set; }
}
public sealed class WidgetSettingsYaml
{
    public string? Visible { get; set; }
    public bool? Expanded { get; set; }
    public int? Priority { get; set; }
}

/// <summary>USD spend caps configured by the user (ADR-0037 §7).</summary>
public sealed class BudgetSettingsYaml
{
    public decimal? Session { get; set; }
    public decimal? Daily { get; set; }
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
[YamlSerializable(typeof(ProviderOAuthYaml))]
[YamlSerializable(typeof(ManagedHostYaml))]
[YamlSerializable(typeof(ModelsFileYaml))]
[YamlSerializable(typeof(ModelFileYaml))]
[YamlSerializable(typeof(ReasoningCapabilityYaml))]
[YamlSerializable(typeof(RoutingYaml))]
[YamlSerializable(typeof(EscalationYaml))]
[YamlSerializable(typeof(WorkspaceSettingsYaml))]
[YamlSerializable(typeof(UserSettingsYaml))]
[YamlSerializable(typeof(SidebarSettingsYaml))]
[YamlSerializable(typeof(WidgetSettingsYaml))]
[YamlSerializable(typeof(BudgetSettingsYaml))]
[YamlSerializable(typeof(WorkspaceGatesYaml))]
[YamlSerializable(typeof(TrustFileYaml))]
[YamlSerializable(typeof(TrustEntryYaml))]
public sealed partial class OmniYamlStaticContext : StaticContext
{
}
