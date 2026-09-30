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
            new[] { "providers" }, new[] { "kind", "family", "baseUrl", "caCertificate", "profile", "authRef", "auth",
                "inputPricePerMillionUsd", "outputPricePerMillionUsd" });
        var modelNodes = ParseRoot(modelsYaml, "models.yaml", "models", diagnostics,
            new[] { "models", "routing" }, new[] { "provider", "context", "recommendedUsableContext", "maxOutput",
                "parametersBillions", "inputPricePerMillionUsd", "outputPricePerMillionUsd", "aliases" });
        ValidateRequiredAndRanges(providerNodes, modelNodes, diagnostics);
        if (diagnostics.Count != 0) throw new ConfigValidationException(diagnostics);

        var providerFile = CreateProvidersFile(providerNodes);
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
                var auth = values.AuthRef is not null
                    ? AuthConfig.ApiKey(values.AuthRef)
                    : values.Auth is { IsNone: false, ApiKey: not null } legacyAuth
                        ? AuthConfig.ApiKey(legacyAuth.ApiKey)
                        : AuthConfig.None();
                var family = values.Family switch
                {
                    "AnthropicMessages" => ProviderFamily.AnthropicMessages,
                    "OpenAIResponses" => ProviderFamily.OpenAIResponses,
                    _ => ProviderFamily.OpenAiChatCompatible,
                };
                registry.Add(new ProviderDescriptor(pair.Key, family, values.BaseUrl!,
                    auth,
                    true, true, true) { TrustedCertificatePath = values.CaCertificate, Profile = values.Profile });
            }
        }

        if (modelFile?.Models is not null)
        {
            foreach (var pair in modelFile.Models)
            {
                var m = pair.Value;
                var context = m.Context ?? 8192;
                registry.AddModel(new ModelDefinition(pair.Key, m.Provider!, context,
                    m.RecommendedUsableContext ?? context, m.MaxOutput ?? 2048, m.ParametersBillions, m.Aliases ?? []));
            }
        }

        if (providersYaml is null && modelsYaml is null)
            registry.AddModel(new ModelDefinition("local-worker", "local", 8192, 8192, 2048));

        ValidateRouting(modelFile?.Routing, registry, diagnostics);
        if (diagnostics.Count != 0) throw new ConfigValidationException(diagnostics);

        var notices = providerFile?.Providers?.Values.Any(provider => provider.Auth is not null) == true
            ? new[] { LocalizedText.Of("config.legacyAuthDeprecated") }
            : Array.Empty<LocalizedText>();
        return new LoadedUserConfiguration(registry, providerFile, modelFile, notices);
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
                var hasAuth = values.Children.TryGetValue(new YamlScalarNode("auth"), out var authNode);
                var hasAuthRef = values.Children.TryGetValue(new YamlScalarNode("authRef"), out var authRefNode);
                if (hasAuth && hasAuthRef)
                    AddAtNode(diagnostics, "providers.yaml", path + ".auth", "config.conflictingAuth", authNode!);
                if (hasAuthRef && string.IsNullOrWhiteSpace(Scalar(values, "authRef")))
                    AddAtNode(diagnostics, "providers.yaml", path + ".authRef", "config.missingRequired", authRefNode!);
                if (hasAuth) ValidateLegacyAuth(authNode!, path, diagnostics);
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

    private static ProvidersFileYaml? CreateProvidersFile(YamlMappingNode? providers)
    {
        if (providers is null) return null;
        var result = new ProvidersFileYaml { Providers = new Dictionary<string, ProviderFileYaml>(StringComparer.Ordinal) };
        foreach (var pair in providers.Children)
        {
            if (pair.Key is not YamlScalarNode { Value: { } id } || pair.Value is not YamlMappingNode values)
                continue;
            var provider = new ProviderFileYaml
            {
                Kind = Scalar(values, "kind"),
                Family = Scalar(values, "family"),
                BaseUrl = Scalar(values, "baseUrl"),
                CaCertificate = Scalar(values, "caCertificate"),
                Profile = Scalar(values, "profile"),
                AuthRef = Scalar(values, "authRef"),
                InputPricePerMillionUsd = Decimal(values, "inputPricePerMillionUsd"),
                OutputPricePerMillionUsd = Decimal(values, "outputPricePerMillionUsd"),
            };
            if (values.Children.TryGetValue(new YamlScalarNode("auth"), out var authNode))
            {
                provider.Auth = authNode is YamlScalarNode { Value: { } scalar }
                    ? new ProviderAuthYaml { IsNone = scalar.Equals("none", StringComparison.OrdinalIgnoreCase) }
                    : authNode is YamlMappingNode authMap
                        ? new ProviderAuthYaml { ApiKey = Scalar(authMap, "apiKey") }
                        : null;
            }
            result.Providers.Add(id, provider);
        }
        return result;
    }

    private static void ValidateLegacyAuth(YamlNode node, string providerPath,
        List<ConfigDiagnostic> diagnostics)
    {
        var path = providerPath + ".auth";
        if (node is YamlScalarNode scalar && IsYamlString(node)
            && scalar.Value!.Equals("none", StringComparison.OrdinalIgnoreCase)) return;
        if (node is not YamlMappingNode authMap)
        {
            AddAtNode(diagnostics, "providers.yaml", path, "config.wrongType", node);
            return;
        }
        CheckKeys(authMap, "providers.yaml", path, new[] { "apiKey" }, diagnostics);
        if (!authMap.Children.TryGetValue(new YamlScalarNode("apiKey"), out var apiKey))
            AddAtNode(diagnostics, "providers.yaml", path + ".apiKey", "config.missingRequired", authMap);
        else if (!IsYamlString(apiKey))
            AddAtNode(diagnostics, "providers.yaml", path + ".apiKey", "config.wrongType", apiKey);
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

    private static decimal? Decimal(YamlMappingNode values, string key) =>
        decimal.TryParse(Scalar(values, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : null;

    private static void ValidateKnownScalars(YamlMappingNode map, string path, string file,
        List<ConfigDiagnostic> diagnostics)
    {
        foreach (var pair in map.Children)
        {
            var key = (pair.Key as YamlScalarNode)?.Value ?? "?";
            if (key is "context" or "recommendedUsableContext" or "maxOutput" or "parametersBillions"
                or "inputPricePerMillionUsd" or "outputPricePerMillionUsd")
            {
                var scalar = pair.Value as YamlScalarNode;
                var raw = scalar?.Value;
                var valid = scalar is not null && scalar.Style == ScalarStyle.Plain && raw is not null
                    && (key == "parametersBillions"
                        ? double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                        : key is "inputPricePerMillionUsd" or "outputPricePerMillionUsd"
                            ? decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var price)
                                && price >= 0m && price <= 1_000_000m
                            : long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
                if (!valid) AddAtNode(diagnostics, file, path + "." + key,
                    key is "inputPricePerMillionUsd" or "outputPricePerMillionUsd"
                        ? "config.outOfRange" : "config.wrongType", pair.Value);
            }
            else if (key is "kind" or "family" or "baseUrl" or "caCertificate" or "profile" or "authRef" or "provider")
            {
                if (!IsYamlString(pair.Value)) AddAtNode(diagnostics, file, path + "." + key, "config.wrongType", pair.Value);
            }
            else if (key == "aliases")
            {
                if (pair.Value is not YamlSequenceNode aliasSequence)
                    AddAtNode(diagnostics, file, path + "." + key, "config.wrongType", pair.Value);
                else
                    foreach (var item in aliasSequence)
                        if (item is not YamlScalarNode aliasItem || !IsYamlString(aliasItem))
                            AddAtNode(diagnostics, file, path + "." + key, "config.wrongType", item);
            }
            else if (key == "auth")
            {
                // Validated as the supported legacy scalar/mapping union in ValidateLegacyAuth.
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

    /// <summary>Todo alias de <c>routing:</c> debe existir y el modo de escalación ser auto, ask o deny.</summary>
    private static void ValidateRouting(RoutingYaml? routing, ModelRegistry registry, List<ConfigDiagnostic> diagnostics)
    {
        if (routing is null) return;
        void Check(string path, List<string>? aliases)
        {
            if (aliases is null) return;
            for (var i = 0; i < aliases.Count; i++)
            {
                try { registry.Resolve(aliases[i]); }
                catch (UnknownModelException) { Add(diagnostics, "models.yaml", path + "[" + i + "]", "config.unknownModelAlias"); }
                catch (AmbiguousModelAliasException) { Add(diagnostics, "models.yaml", path + "[" + i + "]", "config.unknownModelAlias"); }
            }
        }
        Check("routing.meta", routing.Meta);
        Check("routing.exploration", routing.Exploration);
        Check("routing.implementation", routing.Implementation);
        Check("routing.reasoning", routing.Reasoning);
        Check("routing.architecture", routing.Architecture);
        Check("routing.escalation.chain", routing.Escalation?.Chain);
        if (routing.Escalation?.Mode is { } mode && mode is not ("auto" or "ask" or "deny"))
            Add(diagnostics, "models.yaml", "routing.escalation.mode", "config.outOfRange");
    }

    internal static void Add(List<ConfigDiagnostic> diagnostics, string file, string path, string key,
        int? line = null, int? column = null)
    {
        var args = new Dictionary<string, string> { ["path"] = path };
        if (line is not null) args["line"] = line.Value.ToString(CultureInfo.InvariantCulture);
        if (column is not null) args["column"] = column.Value.ToString(CultureInfo.InvariantCulture);
        diagnostics.Add(new ConfigDiagnostic(file, path, new LocalizedText(key, args), line, column));
    }
}

public sealed record ModelPricing(decimal? InputPricePerMillionUsd, decimal? OutputPricePerMillionUsd)
{
    public bool IsComplete => InputPricePerMillionUsd is not null && OutputPricePerMillionUsd is not null;

    public decimal? CostUsd(TokenUsage usage)
    {
        if (!IsComplete) return null;
        return usage.Input / 1_000_000m * InputPricePerMillionUsd!.Value
            + usage.Output / 1_000_000m * OutputPricePerMillionUsd!.Value;
    }
}

public sealed record LoadedUserConfiguration(ModelRegistry Registry, ProvidersFileYaml? Providers,
    ModelsFileYaml? Models, IReadOnlyList<LocalizedText>? DeprecationNotices = null)
{
    public string? ProviderKind(string providerId) => Providers?.Providers?.TryGetValue(providerId, out var provider) == true
        ? provider.Kind : null;

    public ModelPricing? Pricing(string modelId)
    {
        if (Models?.Models is not { } models || !models.TryGetValue(modelId, out var model)) return null;
        var provider = Providers?.Providers is { } providers
            && providers.TryGetValue(model.Provider ?? "", out var found) ? found : null;
        var input = model.InputPricePerMillionUsd ?? provider?.InputPricePerMillionUsd;
        var output = model.OutputPricePerMillionUsd ?? provider?.OutputPricePerMillionUsd;
        if (input is null && output is null) return null;
        return new ModelPricing(input, output);
    }
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
