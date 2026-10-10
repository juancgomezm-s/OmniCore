namespace OmniCore.Host;

using System.Globalization;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Infrastructure;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>Trust decisions are keyed by WorkspaceId and canonical path; data never enters the repo.</summary>
public sealed class WorkspaceTrustStore
{
    private readonly string _file;
    private static readonly ISerializer Serializer = new StaticSerializerBuilder(new OmniYamlStaticContext())
        .WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
    private static readonly IDeserializer Deserializer = new StaticDeserializerBuilder(new OmniYamlStaticContext())
        .WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

    public WorkspaceTrustStore(IPlatformPaths paths) => _file = Path.Combine(paths.DataDirectory, "trust.yaml");

    public bool IsTrusted(string workspaceRoot)
    {
        var canonical = Canonical(workspaceRoot);
        var id = WorkspaceId.Of(canonical).ToString();
        var entries = Read();
        return entries.Workspaces?.TryGetValue(id, out var entry) == true
            && entry.Trusted && string.Equals(entry.CanonicalPath, canonical, PathComparison);
    }

    public void SetTrusted(string workspaceRoot, bool trusted)
    {
        var canonical = Canonical(workspaceRoot);
        var id = WorkspaceId.Of(canonical).ToString();
        var data = Read();
        data.Workspaces ??= new Dictionary<string, TrustEntryYaml>(StringComparer.Ordinal);
        data.Workspaces[id] = new TrustEntryYaml { CanonicalPath = canonical, Trusted = trusted };
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temp = _file + ".tmp";
        File.WriteAllText(temp, Serializer.Serialize(data));
        File.Move(temp, _file, true);
    }

    private TrustFileYaml Read()
    {
        if (!File.Exists(_file)) return new TrustFileYaml { Workspaces = new Dictionary<string, TrustEntryYaml>() };
        try { return Deserializer.Deserialize<TrustFileYaml>(File.ReadAllText(_file)) ?? new TrustFileYaml(); }
        catch (YamlException ex) { throw new InvalidDataException("trust.yaml inválido.", ex); }
    }

    internal static string Canonical(string path) => ProjectIdentity.CanonicalWorkspacePath(
        ProjectIdentity.ResolvePhysicalWorkspaceRoot(path));
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

public sealed record WorkspaceConfigurationResult(bool Trusted, bool HasConfiguration,
    WorkspaceSettingsYaml? Settings, IReadOnlyList<ConfigDiagnostic> Diagnostics)
{
    public bool Ignored => HasConfiguration && !Trusted;

    /// <summary>Resolución por scope que produjo <see cref="Settings"/> (ADR-0039 §5); null si solo se leyó el repo.</summary>
    public ScopedConfiguration<ScopedSettingValue>? Scopes { get; init; }

    /// <summary>Raíz validada de <c>.omnicore/settings.yaml</c>; capa Project de la resolución.</summary>
    internal YamlMappingNode? ProjectRoot { get; init; }
}

/// <summary>Loads only the repo's explicitly allowed, non-security settings after trust is established.</summary>
public static class WorkspaceConfigurationLoader
{
    private static readonly IDeserializer Deserializer = new StaticDeserializerBuilder(new OmniYamlStaticContext())
        .WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
    private static readonly string[] Allowed = ["defaultModel", "permissionRestrictions", "gates", "sidebar", "widgets"];
    private static readonly string[] Forbidden = ["providers", "credentials", "permissions", "sandbox", "baseUrl",
        "auth", "authRef", "projectId", "network", "environmentAllowlist", "grants", "userPolicy", "locked"];

    public static WorkspaceConfigurationResult Load(string workspaceRoot, bool trusted,
        Func<string, bool>? userHasModelAlias = null)
    {
        var directory = Path.Combine(workspaceRoot, ".omnicore");
        if (!Directory.Exists(directory)) return new(trusted, false, null, Array.Empty<ConfigDiagnostic>());
        var settingsFile = Path.Combine(directory, "settings.yaml");
        var hasConfig = Directory.EnumerateFileSystemEntries(directory).Any();
        if (!trusted) return new(false, hasConfig, null, Array.Empty<ConfigDiagnostic>());

        var diagnostics = new List<ConfigDiagnostic>();
        foreach (var forbiddenFile in new[] { "providers.yaml", "credentials.yaml", "permissions.yaml", "sandbox.yaml" })
        {
            if (File.Exists(Path.Combine(directory, forbiddenFile)))
                ConfigLoader.Add(diagnostics, forbiddenFile, "$", "config.forbiddenRepoFile");
        }
        if (!File.Exists(settingsFile))
        {
            if (diagnostics.Count != 0) throw new ConfigValidationException(diagnostics);
            return new(true, hasConfig, null, diagnostics);
        }

        WorkspaceSettingsYaml? settings = null;
        YamlMappingNode? projectRoot = null;
        try
        {
            var yaml = File.ReadAllText(settingsFile);
            var stream = new YamlStream();
            stream.Load(new Parser(new StringReader(yaml)));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
                ConfigLoader.Add(diagnostics, "settings.yaml", "$", "config.expectedMapping");
            else
            {
                SidebarConfiguration.ValidateRoot(root, diagnostics);
                foreach (var pair in root.Children)
                {
                    var key = (pair.Key as YamlScalarNode)?.Value ?? "?";
                    var path = key;
                    if (Forbidden.Contains(key, StringComparer.Ordinal))
                        ConfigLoader.AddAtNode(diagnostics, "settings.yaml", path, "config.forbiddenRepoKey", pair.Key);
                    else if (!Allowed.Contains(key, StringComparer.Ordinal))
                        ConfigLoader.AddAtNode(diagnostics, "settings.yaml", path, "config.unknownKey", pair.Key);
                    else if (key == "permissionRestrictions")
                    {
                        if (pair.Value is not YamlMappingNode restrictions)
                            ConfigLoader.AddAtNode(diagnostics, "settings.yaml", path, "config.wrongType", pair.Value);
                        else foreach (var rule in restrictions.Children)
                        {
                            var ruleKey = (rule.Key as YamlScalarNode)?.Value ?? "?";
                            var value = (rule.Value as YamlScalarNode)?.Value;
                            if (value is not ("deny" or "ask"))
                                ConfigLoader.AddAtNode(diagnostics, "settings.yaml", path + "." + ruleKey,
                                    "config.permissionCanOnlyRestrict", rule.Value);
                        }
                    }
                    else if (key == "gates")
                    {
                        ValidateGates(pair.Value, diagnostics);
                    }
                    else if (key is not ("permissionRestrictions" or "gates" or "sidebar" or "widgets") && !ConfigLoader.IsYamlString(pair.Value))
                        ConfigLoader.AddAtNode(diagnostics, "settings.yaml", path, "config.wrongType", pair.Value);
                }
            }
            if (diagnostics.Count == 0)
            {
                settings = Deserializer.Deserialize<WorkspaceSettingsYaml>(yaml);
                projectRoot = stream.Documents[0].RootNode as YamlMappingNode;
            }
        }
        catch (YamlException ex)
        {
            ConfigLoader.Add(diagnostics, "settings.yaml", "$", "config.yamlSyntax",
                checked((int)ex.Start.Line), checked((int)ex.Start.Column));
        }

        if (settings?.DefaultModel is not null && userHasModelAlias is not null
            && !userHasModelAlias(settings.DefaultModel))
            ConfigLoader.Add(diagnostics, "settings.yaml", "defaultModel", "config.unknownModelAlias");
        if (diagnostics.Count != 0) throw new ConfigValidationException(diagnostics);
        return new(true, hasConfig, settings, diagnostics) { ProjectRoot = projectRoot };
    }

    internal static void ValidateGates(YamlNode node, List<ConfigDiagnostic> diagnostics)
    {
        if (node is not YamlMappingNode gates)
        {
            ConfigLoader.AddAtNode(diagnostics, "settings.yaml", "gates", "config.wrongType", node);
            return;
        }

        foreach (var entry in gates.Children)
        {
            var key = (entry.Key as YamlScalarNode)?.Value ?? "?";
            var path = "gates." + key;
            if (key is not ("build" or "test" or "acceptance"))
            {
                ConfigLoader.AddAtNode(diagnostics, "settings.yaml", path, "config.unknownKey", entry.Key);
                continue;
            }

            if (key == "acceptance")
            {
                if (entry.Value is not YamlScalarNode acceptance
                    || acceptance.Value is not ("true" or "false"))
                    ConfigLoader.AddAtNode(diagnostics, "settings.yaml", path, "config.wrongType", entry.Value);
                continue;
            }

            if (entry.Value is not YamlSequenceNode argv || argv.Children.Count == 0)
            {
                ConfigLoader.AddAtNode(diagnostics, "settings.yaml", path, "config.expectedArgv", entry.Value);
                continue;
            }

            var valid = true;
            foreach (var argument in argv.Children)
            {
                if (!ConfigLoader.IsYamlString(argument)) valid = false;
            }
            if (!valid || argv.Children[0] is not YamlScalarNode executable
                || string.IsNullOrWhiteSpace(executable.Value))
                ConfigLoader.AddAtNode(diagnostics, "settings.yaml", path, "config.expectedArgv", entry.Value);
        }
    }
}

public enum ConfigScope { BuiltIn, User, Project, Workspace }
public sealed record ConfigScopeLayer<T>(ConfigScope Scope, IReadOnlyDictionary<string, T> Values,
    IReadOnlySet<string>? LockedKeys = null);
public sealed record ConfigScopeContribution(ConfigScope Scope, string Key);

/// <summary>Contribución descartada porque un scope menos específico bloqueó la clave con <c>locked</c>.</summary>
public sealed record ConfigScopeBlocked(ConfigScope Scope, string Key, ConfigScope LockedBy, string Lock);

public sealed record ScopedConfiguration<T>(IReadOnlyDictionary<string, T> Values,
    IReadOnlyList<ConfigScopeContribution> Contributions, IReadOnlyList<ConfigScopeBlocked>? Blocked = null)
{
    /// <summary>Scope que aportó el valor efectivo de <paramref name="key"/>.</summary>
    public ConfigScope? SourceOf(string key) =>
        Contributions.LastOrDefault(contribution => contribution.Key == key)?.Scope;
}

/// <summary>
/// Per-key most-specific-wins resolver (ADR-0022 §4). A lock declared by a scope freezes the key, and
/// every key below it in the path (<c>sidebar</c> covers <c>sidebar.mode</c>), for all more-specific scopes.
/// </summary>
public static class ConfigurationScopeResolver
{
    public static ScopedConfiguration<T> Resolve<T>(IEnumerable<ConfigScopeLayer<T>> layers)
    {
        var values = new Dictionary<string, T>(StringComparer.Ordinal);
        var contributions = new List<ConfigScopeContribution>();
        var blocked = new List<ConfigScopeBlocked>();
        var locks = new List<(string Lock, ConfigScope Owner)>();
        foreach (var layer in layers.OrderBy(layer => layer.Scope))
        {
            foreach (var pair in layer.Values)
            {
                var owner = locks.FirstOrDefault(entry => Covers(entry.Lock, pair.Key));
                if (owner.Lock is not null)
                {
                    blocked.Add(new ConfigScopeBlocked(layer.Scope, pair.Key, owner.Owner, owner.Lock));
                    continue;
                }
                values[pair.Key] = pair.Value;
                contributions.Add(new ConfigScopeContribution(layer.Scope, pair.Key));
            }
            foreach (var key in layer.LockedKeys ?? new HashSet<string>())
                if (!locks.Any(entry => entry.Lock == key)) locks.Add((key, layer.Scope));
        }
        return new ScopedConfiguration<T>(values, contributions, blocked);
    }

    /// <summary>Una entrada <c>locked</c> cubre la clave exacta y las que cuelgan de ella.</summary>
    public static bool Covers(string lockEntry, string key) =>
        key == lockEntry || key.StartsWith(lockEntry + ".", StringComparison.Ordinal);
}

/// <summary>Hoja de configuración resuelta por scope: su ruta YAML y el nodo que la define.</summary>
public sealed record ScopedSettingValue(IReadOnlyList<string> Path, YamlNode Node);

/// <summary>
/// Configuración no sensible por scope (ADR-0022 §4, ADR-0039 §5). Cada hoja de <c>defaultModel</c>,
/// <c>gates</c>, <c>sidebar</c> y <c>widgets</c> se resuelve por separado: gana el scope más
/// específico, salvo que el scope User la haya fijado con <c>locked</c>. Los permisos no pasan por
/// aquí (se intersectan, ADR-0037) y el presupuesto solo existe en User.
/// </summary>
public static class ScopedSettings
{
    public static readonly IReadOnlyList<string> Sections = ["defaultModel", "gates", "sidebar", "widgets"];

    private static readonly IDeserializer Deserializer = new StaticDeserializerBuilder(new OmniYamlStaticContext())
        .WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();

    /// <summary>Capa con las hojas de las secciones resueltas de <paramref name="root"/>.</summary>
    public static ConfigScopeLayer<ScopedSettingValue> Layer(ConfigScope scope, YamlMappingNode? root,
        IReadOnlySet<string>? locked = null)
    {
        var values = new Dictionary<string, ScopedSettingValue>(StringComparer.Ordinal);
        if (root is not null)
        {
            foreach (var section in Sections)
                if (root.Children.TryGetValue(new YamlScalarNode(section), out var node))
                    Flatten([section], node, values);
        }
        return new ConfigScopeLayer<ScopedSettingValue>(scope, values, locked);
    }

    /// <summary>Entradas <c>locked</c> de una raíz de settings (solo el scope User puede declararlas).</summary>
    public static IReadOnlySet<string> Locks(YamlMappingNode? root)
    {
        var locks = new HashSet<string>(StringComparer.Ordinal);
        if (root is not null && root.Children.TryGetValue(new YamlScalarNode("locked"), out var node)
            && node is YamlSequenceNode entries)
        {
            foreach (var entry in entries.Children.OfType<YamlScalarNode>())
                if (entry.Value is { Length: > 0 } value) locks.Add(value);
        }
        return locks;
    }

    /// <summary>
    /// ¿Es <paramref name="entry"/> una clave bloqueable? Una sección (<c>sidebar</c>), una hoja
    /// (<c>sidebar.mode</c>, <c>gates.test</c>), un widget (<c>widgets.core.context</c>) o un campo
    /// de widget (<c>widgets.core.context.visible</c>).
    /// </summary>
    public static bool IsLockable(string entry)
    {
        var dot = entry.IndexOf('.', StringComparison.Ordinal);
        var section = dot < 0 ? entry : entry[..dot];
        var rest = dot < 0 ? null : entry[(dot + 1)..];
        return section switch
        {
            "defaultModel" => rest is null,
            "gates" => rest is null or "build" or "test" or "acceptance",
            "sidebar" => rest is null or "visible" or "mode" or "stackedMinWidth" or "tabbedMinWidth",
            "widgets" => rest is null || WidgetLock(rest),
            _ => false,
        };
    }

    private static bool WidgetLock(string rest)
    {
        var last = rest.LastIndexOf('.');
        return last > 0 && rest[(last + 1)..] is "visible" or "expanded" or "priority"
            ? SidebarConfiguration.ValidId(rest[..last])
            : SidebarConfiguration.ValidId(rest);
    }

    /// <summary>YAML efectivo reconstruido a partir de las hojas resueltas.</summary>
    public static YamlMappingNode Compose(ScopedConfiguration<ScopedSettingValue> resolved)
    {
        var root = new YamlMappingNode();
        foreach (var value in resolved.Values.Values)
        {
            var map = root;
            foreach (var segment in value.Path.Take(value.Path.Count - 1))
            {
                var key = new YamlScalarNode(segment);
                if (!map.Children.TryGetValue(key, out var child)) map.Add(key, child = new YamlMappingNode());
                map = (YamlMappingNode)child;
            }
            map.Children[new YamlScalarNode(value.Path[^1])] = value.Node;
        }
        return root;
    }

    /// <summary>Deserializa el YAML efectivo al modelo tipado (generador estático, ADR-0039 §1).</summary>
    public static T Typed<T>(ScopedConfiguration<ScopedSettingValue> resolved) where T : new()
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(Compose(resolved))).Save(writer, false);
        return Deserializer.Deserialize<T>(writer.ToString()) ?? new T();
    }

    private static void Flatten(IReadOnlyList<string> path, YamlNode node,
        Dictionary<string, ScopedSettingValue> values)
    {
        if (node is YamlMappingNode map)
        {
            foreach (var child in map.Children)
                if (child.Key is YamlScalarNode { Value: { } name }) Flatten([.. path, name], child.Value, values);
            return;
        }
        values[string.Join('.', path)] = new ScopedSettingValue(path, node);
    }
}

/// <summary>
/// Configuración efectiva de un workspace (ADR-0039 §5): User (<c>&lt;config&gt;/settings.yaml</c>),
/// Project (<c>.omnicore/settings.yaml</c>, solo si el workspace es confiable) y Workspace local
/// (<c>&lt;data&gt;/workspaces/&lt;id&gt;/settings.yaml</c>), resueltas por <see cref="ConfigurationScopeResolver"/>.
/// </summary>
public static class ScopedSettingsLoader
{
    internal const string WorkspaceFile = "workspace/settings.yaml";
    private static readonly string[] WorkspaceAllowed = ["defaultModel", "sidebar", "widgets"];

    public static WorkspaceConfigurationResult Load(IPlatformPaths paths, string workspaceRoot, bool trusted,
        Func<string, bool>? userHasModelAlias = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var project = WorkspaceConfigurationLoader.Load(workspaceRoot, trusted, userHasModelAlias);
        var user = UserRoot(paths);
        var local = LocalRoot(paths, workspaceRoot);
        var resolved = Resolve(user, project.ProjectRoot, local);
        var effective = ScopedSettings.Typed<WorkspaceSettingsYaml>(resolved);
        if (effective.DefaultModel is not null && userHasModelAlias is not null
            && !userHasModelAlias(effective.DefaultModel))
        {
            var rejected = new List<ConfigDiagnostic>();
            ConfigLoader.Add(rejected, "settings.yaml", "defaultModel", "config.unknownModelAlias");
            throw new ConfigValidationException(rejected);
        }
        effective.PermissionRestrictions = project.Settings?.PermissionRestrictions;
        var configured = resolved.Values.Count > 0 || effective.PermissionRestrictions is not null;
        return project with { Settings = configured ? effective : null, Scopes = resolved };
    }

    /// <summary>Resolución de las tres capas; las entradas <c>locked</c> solo se leen de User.</summary>
    public static ScopedConfiguration<ScopedSettingValue> Resolve(YamlMappingNode? user, YamlMappingNode? project,
        YamlMappingNode? workspace) => ConfigurationScopeResolver.Resolve(new[]
    {
        ScopedSettings.Layer(ConfigScope.User, user, ScopedSettings.Locks(user)),
        ScopedSettings.Layer(ConfigScope.Project, project),
        ScopedSettings.Layer(ConfigScope.Workspace, workspace),
    });

    private static YamlMappingNode? UserRoot(IPlatformPaths paths)
    {
        var text = SidebarConfiguration.ReadFile(Path.Combine(paths.ConfigDirectory, "settings.yaml"));
        var diagnostics = new List<ConfigDiagnostic>();
        var root = ConfigLoader.ParseSettings(text, diagnostics);
        if (diagnostics.Count != 0) throw new ConfigValidationException(diagnostics);
        return root;
    }

    private static YamlMappingNode? LocalRoot(IPlatformPaths paths, string workspaceRoot)
    {
        var text = SidebarConfiguration.ReadFile(Path.Combine(OmniHost.WorkspaceDataDirectory(paths, workspaceRoot),
            "settings.yaml"));
        if (string.IsNullOrWhiteSpace(text)) return null;
        var diagnostics = new List<ConfigDiagnostic>();
        YamlMappingNode? root = null;
        try
        {
            var stream = new YamlStream();
            stream.Load(new Parser(new StringReader(text)));
            root = stream.Documents.Count == 1 ? stream.Documents[0].RootNode as YamlMappingNode : null;
            if (root is null) ConfigLoader.Add(diagnostics, WorkspaceFile, "$", "config.expectedMapping");
            else
            {
                SidebarConfiguration.ValidateRoot(root, diagnostics);
                foreach (var pair in root.Children)
                {
                    var key = (pair.Key as YamlScalarNode)?.Value ?? "?";
                    if (!WorkspaceAllowed.Contains(key, StringComparer.Ordinal))
                        ConfigLoader.AddAtNode(diagnostics, WorkspaceFile, key, "config.unknownKey", pair.Key);
                    else if (key == "defaultModel" && !ConfigLoader.IsYamlString(pair.Value))
                        ConfigLoader.AddAtNode(diagnostics, WorkspaceFile, key, "config.wrongType", pair.Value);
                }
            }
        }
        catch (YamlException ex)
        {
            ConfigLoader.Add(diagnostics, WorkspaceFile, "$", "config.yamlSyntax",
                checked((int)ex.Start.Line), checked((int)ex.Start.Column));
        }
        if (diagnostics.Count != 0) throw new ConfigValidationException(diagnostics);
        return root;
    }
}
