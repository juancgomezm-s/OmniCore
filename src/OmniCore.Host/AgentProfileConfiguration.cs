namespace OmniCore.Host;

using System.Globalization;
using OmniCore.Abstractions;
using OmniCore.Domain;
using YamlDotNet.RepresentationModel;

/// <summary>
/// Loads explicit reusable profiles from trusted User configuration only. No workspace
/// file, model output, or missing field can become a permission ceiling.
/// </summary>
public static class AgentProfileConfiguration
{
    public static AgentProfileRegistry Load(string yaml, ScopeLevel sourceScope)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        if (sourceScope != ScopeLevel.User)
            throw new ArgumentException("Agent profile authority requires User-scope configuration.", nameof(sourceScope));
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count != 1) throw Invalid("Expected one document.");
        var root = Mapping(stream.Documents[0].RootNode, ["agentProfiles"]);
        var profiles = Mapping(Required(root, "agentProfiles"), null);
        var definitions = new List<AgentProfile>();
        foreach (var pair in profiles.Children)
        {
            var name = Scalar(pair.Key);
            var entry = Mapping(pair.Value, ["id", "revision", "permissions", "preferredTools"]);
            var id = ProfileId.Parse(Scalar(Required(entry, "id")));
            if (!long.TryParse(Scalar(Required(entry, "revision")), NumberStyles.None,
                CultureInfo.InvariantCulture, out var revision)) throw Invalid("Invalid profile revision.");
            var ceiling = Mapping(Required(entry, "permissions"),
                ["reads", "writes", "process", "network", "secrets", "allowShell"]);
            var process = Sequence(Required(ceiling, "process")).Select(node =>
            {
                var rule = Mapping(node, ["executablePattern", "argvPatterns", "decision"]);
                return new ProcessRule(Scalar(Required(rule, "executablePattern")),
                    Strings(Required(rule, "argvPatterns")), Decision(Required(rule, "decision")));
            }).ToArray();
            var network = Sequence(Required(ceiling, "network")).Select(node =>
            {
                var rule = Mapping(node, ["hostPattern", "decision"]);
                return new NetworkRule(Scalar(Required(rule, "hostPattern")), Decision(Required(rule, "decision")));
            }).ToArray();
            var shell = Scalar(Required(ceiling, "allowShell")) switch
            {
                "true" => true,
                "false" => false,
                _ => throw Invalid("allowShell must be true or false."),
            };
            definitions.Add(new AgentProfile(id, name, revision,
                PermissionScope.With(Strings(Required(ceiling, "reads")), Strings(Required(ceiling, "writes")),
                    process, network, Strings(Required(ceiling, "secrets")), shell),
                Strings(Required(entry, "preferredTools")).Select(value => new ToolId(value)).ToArray()));
        }
        return new AgentProfileRegistry(definitions);
    }

    private static PermissionDecision Decision(YamlNode node) => Scalar(node) switch
    {
        "Deny" => PermissionDecision.Deny,
        "Ask" => PermissionDecision.Ask,
        "Allow" => PermissionDecision.Allow,
        _ => throw Invalid("Permission decision must be Deny, Ask, or Allow."),
    };

    private static YamlMappingNode Mapping(YamlNode node, IReadOnlyList<string>? keys)
    {
        if (node is not YamlMappingNode mapping) throw Invalid("Expected a mapping.");
        foreach (var key in mapping.Children.Keys)
            if (keys is not null && !keys.Contains(Scalar(key), StringComparer.Ordinal))
                throw Invalid("Unknown profile configuration field.");
        return mapping;
    }

    private static YamlNode Required(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node
            : throw Invalid("Missing explicit profile field: " + key);

    private static string Scalar(YamlNode node) =>
        node is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value)
            ? scalar.Value : throw Invalid("Expected a nonempty scalar.");

    private static IEnumerable<YamlNode> Sequence(YamlNode node) =>
        node is YamlSequenceNode sequence ? sequence.Children : throw Invalid("Expected a sequence.");

    private static string[] Strings(YamlNode node) => Sequence(node).Select(Scalar).ToArray();
    private static InvalidDataException Invalid(string reason) => new(reason);
}
