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

    internal static string Canonical(string path) => ProjectIdentity.ResolvePhysicalWorkspaceRoot(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

public sealed record WorkspaceConfigurationResult(bool Trusted, bool HasConfiguration,
    WorkspaceSettingsYaml? Settings, IReadOnlyList<ConfigDiagnostic> Diagnostics)
{
    public bool Ignored => HasConfiguration && !Trusted;
}

/// <summary>Loads only the repo's explicitly allowed, non-security settings after trust is established.</summary>
public static class WorkspaceConfigurationLoader
{
    private static readonly IDeserializer Deserializer = new StaticDeserializerBuilder(new OmniYamlStaticContext())
        .WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
    private static readonly string[] Allowed = ["defaultModel", "permissionRestrictions"];
    private static readonly string[] Forbidden = ["providers", "credentials", "permissions", "sandbox", "baseUrl",
        "auth", "authRef", "projectId", "network", "environmentAllowlist", "grants", "userPolicy"];

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
        try
        {
            var yaml = File.ReadAllText(settingsFile);
            var stream = new YamlStream();
            stream.Load(new Parser(new StringReader(yaml)));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
                ConfigLoader.Add(diagnostics, "settings.yaml", "$", "config.expectedMapping");
            else
            {
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
                    else if (key != "permissionRestrictions" && !ConfigLoader.IsYamlString(pair.Value))
                        ConfigLoader.AddAtNode(diagnostics, "settings.yaml", path, "config.wrongType", pair.Value);
                }
            }
            if (diagnostics.Count == 0) settings = Deserializer.Deserialize<WorkspaceSettingsYaml>(yaml);
        }
        catch (YamlException ex)
        {
            ConfigLoader.Add(diagnostics, "settings.yaml", "$", "config.yamlSyntax",
                checked((int)ex.Start.Line + 1), checked((int)ex.Start.Column + 1));
        }

        if (settings?.DefaultModel is not null && userHasModelAlias is not null
            && !userHasModelAlias(settings.DefaultModel))
            ConfigLoader.Add(diagnostics, "settings.yaml", "defaultModel", "config.unknownModelAlias");
        if (diagnostics.Count != 0) throw new ConfigValidationException(diagnostics);
        return new(true, hasConfig, settings, diagnostics);
    }
}

public enum ConfigScope { BuiltIn, User, Project, Workspace }
public sealed record ConfigScopeLayer<T>(ConfigScope Scope, IReadOnlyDictionary<string, T> Values,
    IReadOnlySet<string>? LockedKeys = null);
public sealed record ConfigScopeContribution(ConfigScope Scope, string Key);
public sealed record ScopedConfiguration<T>(IReadOnlyDictionary<string, T> Values,
    IReadOnlyList<ConfigScopeContribution> Contributions);

/// <summary>Per-key most-specific-wins resolver; an inherited lock blocks every more-specific layer.</summary>
public static class ConfigurationScopeResolver
{
    public static ScopedConfiguration<T> Resolve<T>(IEnumerable<ConfigScopeLayer<T>> layers)
    {
        var values = new Dictionary<string, T>(StringComparer.Ordinal);
        var contributions = new List<ConfigScopeContribution>();
        var locked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in layers.OrderBy(layer => layer.Scope))
        {
            foreach (var pair in layer.Values)
            {
                if (locked.Contains(pair.Key)) continue;
                values[pair.Key] = pair.Value;
                contributions.Add(new ConfigScopeContribution(layer.Scope, pair.Key));
            }
            if (layer.LockedKeys is not null) locked.UnionWith(layer.LockedKeys);
        }
        return new ScopedConfiguration<T>(values, contributions);
    }
}
