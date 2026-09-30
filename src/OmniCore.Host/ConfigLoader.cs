namespace OmniCore.Host;

using System.Globalization;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Models;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>Valida y carga la configuración User YAML tipada (ADR-0039).</summary>
public sealed class ConfigLoader
{
    private static readonly IDeserializer Deserializer = new StaticDeserializerBuilder(new OmniYamlStaticContext())
        .WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

    public ModelRegistry BuildRegistry(string? providersYaml, string? modelsYaml) =>
        Load(providersYaml, modelsYaml).Registry;

    public LoadedUserConfiguration Load(string? providersYaml, string? modelsYaml)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var providerNodes = ParseRoot(providersYaml, "providers.yaml", "providers", diagnostics,
            new[] { "providers" }, new[] { "kind", "family", "baseUrl", "caCertificate", "authRef" });
        var modelNodes = ParseRoot(modelsYaml, "models.yaml", "models", diagnostics,
            new[] { "models" }, new[] { "provider", "context", "recommendedUsableContext", "maxOutput", "parametersBillions" });
        ValidateRequiredAndRanges(providerNodes, modelNodes, diagnostics);
        if (diagnostics.Count != 0) throw new ConfigValidationException(diagnostics);

        var providerFile = Deserialize<ProvidersFileYaml>(providersYaml, "providers.yaml", diagnostics);
        var modelFile = Deserialize<ModelsFileYaml>(modelsYaml, "models.yaml", diagnostics);
        if (diagnostics.Count != 0) throw new ConfigValidationException(diagnostics);
        var registry = new ModelRegistry();
        if (providerFile?.Providers is null)
            registry.Add(new ProviderDescriptor("local", ProviderFamily.OpenAiChatCompatible,
                "http://127.0.0.1:8080", AuthConfig.None(), true, true, true));
        if (providerFile?.Providers is not null)
        {
            foreach (var pair in providerFile.Providers)
            {
                var values = pair.Value;
                var authRef = values.AuthRef;
                var family = values.Family switch
                {
                    "AnthropicMessages" => ProviderFamily.AnthropicMessages,
                    "OpenAIResponses" => ProviderFamily.OpenAIResponses,
                    _ => ProviderFamily.OpenAiChatCompatible,
                };
                registry.Add(new ProviderDescriptor(pair.Key, family, values.BaseUrl!,
                    string.IsNullOrWhiteSpace(authRef) ? AuthConfig.None() : AuthConfig.ApiKey(authRef),
                    true, true, true) { TrustedCertificatePath = values.CaCertificate });
            }
        }

        if (modelFile?.Models is not null)
        {
            foreach (var pair in modelFile.Models)
            {
                var m = pair.Value;
                var context = m.Context ?? 8192;
                registry.AddModel(new ModelDefinition(pair.Key, m.Provider!, context,
                    m.RecommendedUsableContext ?? context, m.MaxOutput ?? 2048, m.ParametersBillions));
            }
        }

        if (providersYaml is null && modelsYaml is null)
            registry.AddModel(new ModelDefinition("local-worker", "local", 8192, 8192, 2048));

        return new LoadedUserConfiguration(registry, providerFile, modelFile);
    }

    private static YamlMappingNode? ParseRoot(string? yaml, string file, string rootKey,
        List<ConfigDiagnostic> diagnostics, string[] rootAllowed, string[] childAllowed)
    {
        if (yaml is null) return null;
        try
        {
            var stream = new YamlStream();
            stream.Load(new Parser(new StringReader(yaml)));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                Add(diagnostics, file, "$", "config.expectedMapping");
                return null;
            }
            CheckKeys(root, file, "$", rootAllowed, diagnostics);
            if (!root.Children.TryGetValue(new YamlScalarNode(rootKey), out var section))
            {
                Add(diagnostics, file, rootKey, "config.missingRequired");
                return null;
            }
            if (section is not YamlMappingNode sectionMap)
            {
                Add(diagnostics, file, rootKey, "config.wrongType");
                return null;
            }
            foreach (var pair in sectionMap.Children)
            {
                var id = (pair.Key as YamlScalarNode)?.Value;
                if (id is null || string.IsNullOrWhiteSpace(id))
                {
                    Add(diagnostics, file, rootKey, "config.wrongType");
                    continue;
                }
                if (pair.Value is not YamlMappingNode item)
                {
                    Add(diagnostics, file, rootKey + "." + id, "config.wrongType");
                    continue;
                }
                CheckKeys(item, file, rootKey + "." + id, childAllowed, diagnostics);
                ValidateKnownScalars(item, rootKey + "." + id, file, diagnostics);
            }
            return sectionMap;
        }
        catch (YamlException ex)
        {
            Add(diagnostics, file, "$", "config.yamlSyntax", checked((int)ex.Start.Line + 1),
                checked((int)ex.Start.Column + 1));
            return null;
        }
    }

    private static void ValidateRequiredAndRanges(YamlMappingNode? providers, YamlMappingNode? models,
        List<ConfigDiagnostic> diagnostics)
    {
        var providerIds = providers?.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value ?? "").ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal) { "local" };
        if (providers is not null)
        {
            foreach (var pair in providers.Children)
            {
                var id = (pair.Key as YamlScalarNode)?.Value ?? "?";
                if (pair.Value is not YamlMappingNode values) continue;
                var path = "providers." + id;
                if (!values.Children.TryGetValue(new YamlScalarNode("baseUrl"), out var baseNode))
                    AddAtNode(diagnostics, "providers.yaml", path + ".baseUrl", "config.missingRequired", values);
                else if (IsYamlString(baseNode) && baseNode is YamlScalarNode baseScalar && baseScalar.Value is not null
                    && (!Uri.TryCreate(baseScalar.Value, UriKind.Absolute, out var uri)
                        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
                    AddAtNode(diagnostics, "providers.yaml", path + ".baseUrl", "config.invalidUrl", baseNode);
                if (Scalar(values, "kind") is { } kind && kind is not ("openAiChatCompatible" or "openAiResponses"
                    or "anthropicMessages" or "llamaCpp" or "ikLlama"))
                    AddAtNode(diagnostics, "providers.yaml", path + ".kind", "config.outOfRange",
                        values.Children[new YamlScalarNode("kind")]);
                if (Scalar(values, "family") is { } family && family is not ("OpenAiChatCompatible" or "OpenAIResponses"
                    or "AnthropicMessages"))
                    AddAtNode(diagnostics, "providers.yaml", path + ".family", "config.outOfRange",
                        values.Children[new YamlScalarNode("family")]);
                if (values.Children.ContainsKey(new YamlScalarNode("authRef"))
                    && string.IsNullOrWhiteSpace(Scalar(values, "authRef")))
                    AddAtNode(diagnostics, "providers.yaml", path + ".authRef", "config.missingRequired",
                        values.Children[new YamlScalarNode("authRef")]);
            }
        }

        if (models is null) return;
        foreach (var pair in models.Children)
        {
            var id = (pair.Key as YamlScalarNode)?.Value ?? "?";
            if (pair.Value is not YamlMappingNode values) continue;
            var path = "models." + id;
            var provider = Scalar(values, "provider");
            if (string.IsNullOrWhiteSpace(provider))
                AddAtNode(diagnostics, "models.yaml", path + ".provider", "config.missingRequired", values);
            else if (!providerIds.Contains(provider))
                AddAtNode(diagnostics, "models.yaml", path + ".provider", "config.unknownProvider",
                    values.Children[new YamlScalarNode("provider")]);

            var context = ValidateInteger(values, "context", path, diagnostics);
            var usable = ValidateInteger(values, "recommendedUsableContext", path, diagnostics);
            var maxOutput = ValidateInteger(values, "maxOutput", path, diagnostics);
            if (usable is not null && context is not null && usable > context)
                AddAtNode(diagnostics, "models.yaml", path + ".recommendedUsableContext", "config.outOfRange",
                    values.Children[new YamlScalarNode("recommendedUsableContext")]);
            if (maxOutput is not null && context is not null && maxOutput > context)
                AddAtNode(diagnostics, "models.yaml", path + ".maxOutput", "config.outOfRange",
                    values.Children[new YamlScalarNode("maxOutput")]);
            if (Scalar(values, "parametersBillions") is { } parameters
                && double.TryParse(parameters, NumberStyles.Float, CultureInfo.InvariantCulture, out var count)
                && (!double.IsFinite(count) || count <= 0 || count > 100_000))
                AddAtNode(diagnostics, "models.yaml", path + ".parametersBillions", "config.outOfRange",
                    values.Children[new YamlScalarNode("parametersBillions")]);
        }
    }

    private static long? ValidateInteger(YamlMappingNode values, string key, string path,
        List<ConfigDiagnostic> diagnostics)
    {
        if (!values.Children.TryGetValue(new YamlScalarNode(key), out var node)) return null;
        if (node is not YamlScalarNode scalar || !long.TryParse(scalar.Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value)) return null;
        if (value < 1 || value > 2_147_483_647)
            AddAtNode(diagnostics, "models.yaml", path + "." + key, "config.outOfRange", node);
        return value;
    }

    private static string? Scalar(YamlMappingNode values, string key) =>
        values.Children.TryGetValue(new YamlScalarNode(key), out var node) ? (node as YamlScalarNode)?.Value : null;

    private static void ValidateKnownScalars(YamlMappingNode map, string path, string file,
        List<ConfigDiagnostic> diagnostics)
    {
        foreach (var pair in map.Children)
        {
            var key = (pair.Key as YamlScalarNode)?.Value ?? "?";
            if (key is "context" or "recommendedUsableContext" or "maxOutput" or "parametersBillions")
            {
                var scalar = pair.Value as YamlScalarNode;
                var raw = scalar?.Value;
                var valid = scalar is not null && scalar.Style == ScalarStyle.Plain && raw is not null
                    && (key == "parametersBillions"
                        ? double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                        : long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
                if (!valid) AddAtNode(diagnostics, file, path + "." + key, "config.wrongType", pair.Value);
            }
            else if (key is "kind" or "family" or "baseUrl" or "caCertificate" or "authRef" or "provider")
            {
                if (!IsYamlString(pair.Value)) AddAtNode(diagnostics, file, path + "." + key, "config.wrongType", pair.Value);
            }
            else if (pair.Value is not YamlScalarNode)
                AddAtNode(diagnostics, file, path + "." + key, "config.wrongType", pair.Value);
        }
    }

    internal static bool IsYamlString(YamlNode node)
    {
        if (node is not YamlScalarNode scalar || scalar.Value is null) return false;
        if (scalar.Style != ScalarStyle.Plain) return true;
        var text = scalar.Value.Trim();
        if (text.Length == 0 || text is "~" or "null" or "Null" or "NULL"
            or "true" or "True" or "TRUE" or "false" or "False" or "FALSE"
            or "yes" or "Yes" or "YES" or "no" or "No" or "NO" or "on" or "off") return false;
        return !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    private static void CheckKeys(YamlMappingNode map, string file, string path, string[] allowed,
        List<ConfigDiagnostic> diagnostics)
    {
        foreach (var pair in map.Children)
        {
            var key = (pair.Key as YamlScalarNode)?.Value ?? "?";
            if (!allowed.Contains(key, StringComparer.Ordinal))
                AddAtNode(diagnostics, file, path == "$" ? key : path + "." + key, "config.unknownKey", pair.Key);
        }
    }

    private static T? Deserialize<T>(string? yaml, string file, List<ConfigDiagnostic> diagnostics) where T : class
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;
        try { return Deserializer.Deserialize<T>(yaml); }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException or FormatException)
        {
            Add(diagnostics, file, "$", "config.deserializeFailed");
            return null;
        }
    }

    internal static void AddAtNode(List<ConfigDiagnostic> diagnostics, string file, string path, string key,
        YamlNode node) => Add(diagnostics, file, path, key, checked((int)node.Start.Line + 1),
            checked((int)node.Start.Column + 1));

    internal static void Add(List<ConfigDiagnostic> diagnostics, string file, string path, string key,
        int? line = null, int? column = null)
    {
        var args = new Dictionary<string, string> { ["path"] = path };
        if (line is not null) args["line"] = line.Value.ToString(CultureInfo.InvariantCulture);
        if (column is not null) args["column"] = column.Value.ToString(CultureInfo.InvariantCulture);
        diagnostics.Add(new ConfigDiagnostic(file, path, new LocalizedText(key, args), line, column));
    }
}

public sealed record LoadedUserConfiguration(ModelRegistry Registry, ProvidersFileYaml? Providers,
    ModelsFileYaml? Models)
{
    public string? ProviderKind(string providerId) => Providers?.Providers?.TryGetValue(providerId, out var provider) == true
        ? provider.Kind : null;
}

public sealed record ConfigDiagnostic(string File, string KeyPath, LocalizedText Message,
    int? Line = null, int? Column = null);

public sealed class ConfigValidationException : InvalidOperationException
{
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; }
    public ConfigValidationException(IReadOnlyList<ConfigDiagnostic> diagnostics)
        : base("La configuración YAML contiene errores: " + string.Join(", ", diagnostics.Select(d => d.File + ":" + d.KeyPath)))
        => Diagnostics = diagnostics;
}
