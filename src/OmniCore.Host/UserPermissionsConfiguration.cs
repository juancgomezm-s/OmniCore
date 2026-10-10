namespace OmniCore.Host;

using OmniCore.Abstractions;
using OmniCore.Domain;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

/// <summary>
/// Configuración de permisos del usuario (<c>&lt;config&gt;/permissions.yaml</c>, scope User; ADR-0037 §4–§5,
/// ADR-0039 §2): el perfil y las reglas por tool que forman la capa <c>UserPolicy</c>. Un repo nunca la aporta
/// (<c>permissions.yaml</c> en <c>.omnicore/</c> es un archivo prohibido).
/// </summary>
/// <param name="Profile">Perfil de permisos; <see cref="PermissionProfile.Autonomous"/> si no se declara.</param>
/// <param name="Rules">Decisión explícita del usuario por id de tool.</param>
public sealed record UserPermissions(PermissionProfile Profile,
    IReadOnlyDictionary<string, PermissionDecision> Rules)
{
    public static UserPermissions Default { get; } = new(PermissionProfile.Autonomous,
        new Dictionary<string, PermissionDecision>(StringComparer.Ordinal));
}

public static class UserPermissionsLoader
{
    private const string FileName = "permissions.yaml";
    private static readonly string[] Keys = ["profile", "rules"];

    /// <summary>Lee <c>permissions.yaml</c> del directorio de configuración del usuario; sin archivo, el default.</summary>
    public static UserPermissions Load(IPlatformPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var path = Path.Combine(paths.ConfigDirectory, FileName);
        return System.IO.File.Exists(path) ? Parse(System.IO.File.ReadAllText(path)) : UserPermissions.Default;
    }

    public static UserPermissions Parse(string yaml)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var profile = PermissionProfile.Autonomous;
        var rules = new Dictionary<string, PermissionDecision>(StringComparer.Ordinal);
        try
        {
            var stream = new YamlStream();
            stream.Load(new Parser(new StringReader(yaml)));
            if (stream.Documents.Count == 0) return UserPermissions.Default;
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
                ConfigLoader.Add(diagnostics, FileName, "$", "config.expectedMapping");
            else
            {
                foreach (var pair in root.Children)
                {
                    var key = (pair.Key as YamlScalarNode)?.Value ?? "?";
                    if (!Keys.Contains(key, StringComparer.Ordinal))
                        ConfigLoader.AddAtNode(diagnostics, FileName, key, "config.unknownKey", pair.Key);
                    else if (key == "profile") ReadProfile(pair.Value, diagnostics, ref profile);
                    else ReadRules(pair.Value, diagnostics, rules);
                }
            }
        }
        catch (YamlException ex)
        {
            ConfigLoader.Add(diagnostics, FileName, "$", "config.yamlSyntax",
                checked((int)ex.Start.Line), checked((int)ex.Start.Column));
        }

        if (diagnostics.Count != 0) throw new ConfigValidationException(diagnostics);
        return new UserPermissions(profile, rules);
    }

    private static void ReadProfile(YamlNode node, List<ConfigDiagnostic> diagnostics, ref PermissionProfile profile)
    {
        if (!ConfigLoader.IsYamlString(node))
        {
            ConfigLoader.AddAtNode(diagnostics, FileName, "profile", "config.wrongType", node);
            return;
        }

        switch (((YamlScalarNode)node).Value)
        {
            case "autonomous": profile = PermissionProfile.Autonomous; break;
            case "balanced": profile = PermissionProfile.Balanced; break;
            case "conservative": profile = PermissionProfile.Conservative; break;
            default: ConfigLoader.AddAtNode(diagnostics, FileName, "profile", "config.outOfRange", node); break;
        }
    }

    private static void ReadRules(YamlNode node, List<ConfigDiagnostic> diagnostics,
        Dictionary<string, PermissionDecision> rules)
    {
        if (node is not YamlMappingNode map)
        {
            ConfigLoader.AddAtNode(diagnostics, FileName, "rules", "config.wrongType", node);
            return;
        }

        foreach (var rule in map.Children)
        {
            var tool = (rule.Key as YamlScalarNode)?.Value;
            var path = "rules." + (tool ?? "?");
            if (string.IsNullOrWhiteSpace(tool) || tool.Length > 200 || tool.Any(char.IsWhiteSpace))
            {
                ConfigLoader.AddAtNode(diagnostics, FileName, path, "config.wrongType", rule.Key);
                continue;
            }

            if (!ConfigLoader.IsYamlString(rule.Value))
            {
                ConfigLoader.AddAtNode(diagnostics, FileName, path, "config.wrongType", rule.Value);
                continue;
            }

            switch (((YamlScalarNode)rule.Value).Value)
            {
                case "allow": rules[tool] = PermissionDecision.Allow; break;
                case "ask": rules[tool] = PermissionDecision.Ask; break;
                case "deny": rules[tool] = PermissionDecision.Deny; break;
                default: ConfigLoader.AddAtNode(diagnostics, FileName, path, "config.outOfRange", rule.Value); break;
            }
        }
    }
}
