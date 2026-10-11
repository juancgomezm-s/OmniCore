namespace OmniCore.Extensions;

using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

/// <summary>Strict, reflection-free mapping from YAML's syntax tree to the closed manifest DTOs.</summary>
internal static class ManifestYamlAst
{
    public static YamlMappingNode ReadRoot(string yaml, string description)
    {
        var stream = new YamlStream();
        stream.Load(new Parser(new StringReader(yaml)));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException($"The {description} must be a YAML mapping.");
        return root;
    }

    public static void CheckKeys(YamlMappingNode map, string description, params string[] allowed)
    {
        foreach (var pair in map.Children)
        {
            var key = (pair.Key as YamlScalarNode)?.Value;
            if (key is null || !allowed.Contains(key, StringComparer.Ordinal))
                throw new InvalidDataException($"Unknown {description} field: {key ?? "<non-scalar>"}");
        }
    }

    public static YamlNode? Optional(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

    public static YamlMappingNode? OptionalMap(YamlMappingNode map, string key, string description,
        params string[] allowed)
    {
        var node = Optional(map, key);
        if (node is null) return null;
        if (node is not YamlMappingNode child) throw WrongType(description);
        CheckKeys(child, description, allowed);
        return child;
    }

    public static string? OptionalString(YamlMappingNode map, string key, string description) =>
        Optional(map, key) is { } node ? String(node, description) : null;

    public static string String(YamlNode node, string description)
    {
        if (node is not YamlScalarNode { Value: { } value }) throw WrongType(description);
        return value;
    }

    public static int Int(YamlNode node, string description)
    {
        var value = String(node, description);
        if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
            throw WrongType(description);
        return parsed;
    }

    public static bool Bool(YamlNode node, string description)
    {
        var value = String(node, description);
        if (bool.TryParse(value, out var parsed)) return parsed;
        throw WrongType(description);
    }

    public static List<string>? OptionalStringList(YamlMappingNode map, string key, string description)
    {
        var node = Optional(map, key);
        if (node is null) return null;
        if (node is not YamlSequenceNode sequence) throw WrongType(description);
        return sequence.Select(item => String(item, description)).ToList();
    }

    public static InvalidDataException WrongType(string description) =>
        new($"The {description} has the wrong YAML type.");
}
