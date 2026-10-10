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

    public LoadedUserConfiguration Load(string? providersYaml, string? modelsYaml, string? settingsYaml = null)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        ParseSettings(settingsYaml, diagnostics);
        var providerNodes = ParseRoot(providersYaml, "providers.yaml", "providers", diagnostics,
            new[] { "providers" }, new[] { "kind", "family", "baseUrl", "caCertificate", "profile", "authRef", "auth", "oauth", "host", "managed",
                "billingMode", "inputPricePerMillionUsd", "outputPricePerMillionUsd" });
        var modelNodes = ParseRoot(modelsYaml, "models.yaml", "models", diagnostics,
            new[] { "models", "routing" }, new[] { "provider", "context", "recommendedUsableContext", "maxOutput",
                "parametersBillions", "inputPricePerMillionUsd", "outputPricePerMillionUsd", "aliases", "reasoning" });
        ValidateRequiredAndRanges(providerNodes, modelNodes, diagnostics);
        if (diagnostics.Count != 0) throw new ConfigValidationException(diagnostics);

        var settings = Deserialize<UserSettingsYaml>(settingsYaml, "settings.yaml", diagnostics);
        var providerFile = CreateProvidersFile(providerNodes);
        ValidateOAuthContents(providerFile, diagnostics);
        var modelFile = Deserialize<ModelsFileYaml>(modelsYaml, "models.yaml", diagnostics);
        if (diagnostics.Count != 0) throw new ConfigValidationException(diagnostics);
        var registry = new ModelRegistry();
        if (providerFile?.Providers is null)
            registry.Add(new ProviderDescriptor("local", ProviderFamily.OpenAiChatCompatible,
                "http://127.0.0.1:8080", AuthConfig.None(), true, true, true)
            { BillingMode = BillingMode.Local, LocalHost = new LocalHostConfig(LocalHostMode.Attach, Declared: false) });
        if (providerFile?.Providers is not null)
        {
            foreach (var pair in providerFile.Providers)
            {
                var values = pair.Value;
                // La seccion oauth: gana sobre authRef: un provider declarado por cuenta no puede
                // autentificar ademas con API key, y silenciar esa colision haria que el usuario
                // viera un camino de cuenta que en realidad gasta creditos de API.
                var auth = values.OAuth is not null
                    ? AuthConfig.OAuth(ClaudeOAuthConfiguration.SecretRefFor(pair.Key, values.OAuth))
                    : values.AuthRef is not null
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
                var billing = values.BillingMode switch
                {
                    "Local" => BillingMode.Local,
                    "IncludedQuota" => BillingMode.IncludedQuota,
                    "CreditBalance" => BillingMode.CreditBalance,
                    "MeteredCurrency" => BillingMode.MeteredCurrency,
                    _ => BillingMode.Unknown,
                };
                var localHost = LocalHostFor(values, family, billing);
                registry.Add(new ProviderDescriptor(pair.Key, family,
                    localHost is { Mode: LocalHostMode.Managed, FixedPort: 0 } ? LocalHostConfig.ManagedLogicalBaseUrl
                        : values.BaseUrl!,
                    auth,
                    true, true, true)
                {
                    LocalHost = localHost,
                    TrustedCertificatePath = values.CaCertificate,
                    Profile = values.Profile,
                    BillingMode = billing,
                });
            }
        }

        if (modelFile?.Models is not null)
        {
            foreach (var pair in modelFile.Models)
            {
                var m = pair.Value;
                var context = m.Context ?? 8192;
                registry.AddModel(new ModelDefinition(pair.Key, m.Provider!, context,
                    m.RecommendedUsableContext ?? context, m.MaxOutput ?? 2048, m.ParametersBillions, m.Aliases ?? [],
                    m.Reasoning is { } reasoning
                        ? new ReasoningCapability(reasoning.Supported, reasoning.EffortLevels,
                            reasoning.ReplayPolicy is null ? null : Enum.Parse<ReasoningReplayPolicy>(reasoning.ReplayPolicy),
                            reasoning.UltraCodeBudgetTokens, reasoning.UltraCodeOutputReserveTokens)
                        : null));
            }
        }

        if (providersYaml is null && modelsYaml is null)
            registry.AddModel(new ModelDefinition("local-worker", "local", 8192, 8192, 2048));

        ValidateRouting(modelFile?.Routing, registry, diagnostics);
        if (diagnostics.Count != 0) throw new ConfigValidationException(diagnostics);

        var notices = providerFile?.Providers?.Values.Any(provider => provider.Auth is not null) == true
            ? new[] { LocalizedText.Of("config.legacyAuthDeprecated") }
            : Array.Empty<LocalizedText>();
        return new LoadedUserConfiguration(registry, providerFile, modelFile, notices, settings);
    }

    internal static YamlMappingNode? ParseSettings(string? yaml, List<ConfigDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;
        try
        {
            var stream = new YamlStream();
            stream.Load(new Parser(new StringReader(yaml)));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                Add(diagnostics, "settings.yaml", "$", "config.expectedMapping");
                return null;
            }

            CheckKeys(root, "settings.yaml", "$", ["budget", "sidebar", "widgets", "locked", "defaultModel", "gates"],
                diagnostics);
            SidebarConfiguration.ValidateRoot(root, diagnostics);
            ValidateLocked(root, diagnostics);
            if (root.Children.TryGetValue(new YamlScalarNode("defaultModel"), out var defaultModel)
                && !IsYamlString(defaultModel))
                AddAtNode(diagnostics, "settings.yaml", "defaultModel", "config.wrongType", defaultModel);
            if (root.Children.TryGetValue(new YamlScalarNode("gates"), out var gates))
                WorkspaceConfigurationLoader.ValidateGates(gates, diagnostics);
            if (!root.Children.TryGetValue(new YamlScalarNode("budget"), out var budgetNode)) return root;
            if (budgetNode is not YamlMappingNode budget)
            {
                AddAtNode(diagnostics, "settings.yaml", "budget", "config.wrongType", budgetNode);
                return root;
            }

            CheckKeys(budget, "settings.yaml", "budget", ["session", "daily"], diagnostics);
            foreach (var key in new[] { "session", "daily" })
            {
                if (!budget.Children.TryGetValue(new YamlScalarNode(key), out var node)) continue;
                var scalar = node as YamlScalarNode;
                if (scalar is null || scalar.Style != ScalarStyle.Plain || scalar.Value is null
                    || !decimal.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    AddAtNode(diagnostics, "settings.yaml", "budget." + key, "config.wrongType", node);
                }
                else if (value < 0m)
                {
                    AddAtNode(diagnostics, "settings.yaml", "budget." + key, "config.outOfRange", node);
                }
            }
            return root;
        }
        catch (YamlException ex)
        {
            Add(diagnostics, "settings.yaml", "$", "config.yamlSyntax", checked((int)ex.Start.Line),
                checked((int)ex.Start.Column));
            return null;
        }
    }

    /// <summary><c>locked</c>: lista de claves que ningún scope más específico puede cambiar (ADR-0039 §5).</summary>
    private static void ValidateLocked(YamlMappingNode root, List<ConfigDiagnostic> diagnostics)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("locked"), out var node)) return;
        if (node is not YamlSequenceNode entries)
        {
            AddAtNode(diagnostics, "settings.yaml", "locked", "config.wrongType", node);
            return;
        }

        foreach (var entry in entries.Children)
        {
            if (!IsYamlString(entry))
                AddAtNode(diagnostics, "settings.yaml", "locked", "config.wrongType", entry);
            else if (!ScopedSettings.IsLockable(((YamlScalarNode)entry).Value!))
                AddAtNode(diagnostics, "settings.yaml", "locked." + ((YamlScalarNode)entry).Value, "config.unknownKey", entry);
        }
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
                if (item.Children.TryGetValue(new YamlScalarNode("oauth"), out var oauthCheck)
                    && oauthCheck is YamlMappingNode oauthMapCheck)
                {
                    CheckKeys(oauthMapCheck, file, rootKey + "." + id + ".oauth", OAuthKeys, diagnostics);
                }
            }
            return sectionMap;
        }
        catch (YamlException ex)
        {
            Add(diagnostics, file, "$", "config.yamlSyntax", checked((int)ex.Start.Line),
                checked((int)ex.Start.Column));
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
                var managedHost = Scalar(values, "host") == "managed";
                if (!values.Children.TryGetValue(new YamlScalarNode("baseUrl"), out var baseNode))
                {
                    if (!managedHost)
                        AddAtNode(diagnostics, "providers.yaml", path + ".baseUrl", "config.missingRequired", values);
                }
                else if (managedHost && Scalar(values, "baseUrl") == "auto")
                {
                    // Puerto efímero: el endpoint real se resuelve al arrancar el servidor.
                }
                else if (IsYamlString(baseNode) && baseNode is YamlScalarNode baseScalar && baseScalar.Value is not null
                    && (!Uri.TryCreate(baseScalar.Value, UriKind.Absolute, out var uri)
                        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
                    AddAtNode(diagnostics, "providers.yaml", path + ".baseUrl", "config.invalidUrl", baseNode);
                ValidateHost(values, path, diagnostics);
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
                if (values.Children.TryGetValue(new YamlScalarNode("billingMode"), out var billingModeNode))
                {
                    if (!IsYamlString(billingModeNode))
                        AddAtNode(diagnostics, "providers.yaml", path + ".billingMode", "config.wrongType", billingModeNode);
                    else if (Scalar(values, "billingMode") is not ("Unknown" or "Local" or "IncludedQuota"
                        or "CreditBalance" or "MeteredCurrency"))
                        AddAtNode(diagnostics, "providers.yaml", path + ".billingMode", "config.outOfRange", billingModeNode);
                }
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
                BillingMode = Scalar(values, "billingMode"),
                AuthRef = Scalar(values, "authRef"),
                Host = Scalar(values, "host"),
                Managed = values.Children.TryGetValue(new YamlScalarNode("managed"), out var managedNode)
                    && managedNode is YamlMappingNode managedMap ? CreateManaged(managedMap) : null,
                InputPricePerMillionUsd = Decimal(values, "inputPricePerMillionUsd"),
                OutputPricePerMillionUsd = Decimal(values, "outputPricePerMillionUsd"),
            };
            if (values.Children.TryGetValue(new YamlScalarNode("oauth"), out var oauthNode)
                && oauthNode is YamlMappingNode oauthMap)
            {
                provider.OAuth = CreateOAuth(oauthMap);
            }
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

    private static readonly string[] OAuthKeys =
        ["clientId", "userAgent", "scopes", "authorizeUrl", "tokenUrl", "profileUrl", "secretRef"];

    /// <summary>
    /// Seccion <c>oauth:</c> de un provider. Se lee de nodos YAML en vez de deserializar porque
    /// <c>scopes</c> admite forma de lista y de escalar separado por espacios, igual que el wire.
    /// La validacion de contenido (URLs absolutas, https, placeholder de version) vive en
    /// <see cref="ClaudeOAuthConfiguration"/>.
    /// </summary>
    private static ProviderOAuthYaml CreateOAuth(YamlMappingNode map) => new()
    {
        ClientId = Scalar(map, "clientId"),
        UserAgent = Scalar(map, "userAgent"),
        AuthorizeUrl = Scalar(map, "authorizeUrl"),
        TokenUrl = Scalar(map, "tokenUrl"),
        ProfileUrl = Scalar(map, "profileUrl"),
        SecretRef = Scalar(map, "secretRef"),
        Scopes = map.Children.TryGetValue(new YamlScalarNode("scopes"), out var scopesNode)
            ? scopesNode switch
            {
                YamlSequenceNode sequence => sequence.Children.OfType<YamlScalarNode>()
                    .Select(s => s.Value ?? "").Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim()).ToList(),
                YamlScalarNode { Value: { } scalar } => scalar
                    .Split(' ', '	')
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim()).ToList(),
                _ => null,
            }
            : null,
    };

    private static readonly string[] ManagedKeys = ["executable", "args", "workingDirectory", "readinessTimeoutSeconds"];

    /// <summary>
    /// <c>host</c> y <c>managed</c> de un provider (ADR-0011 §4). Solo los providers OpenAI-compatibles
    /// tienen servidor local; el endpoint managed es siempre loopback.
    /// </summary>
    private static void ValidateHost(YamlMappingNode values, string path, List<ConfigDiagnostic> diagnostics)
    {
        var hasManaged = values.Children.TryGetValue(new YamlScalarNode("managed"), out var managedNode);
        if (!values.Children.TryGetValue(new YamlScalarNode("host"), out var hostNode))
        {
            if (hasManaged) AddAtNode(diagnostics, "providers.yaml", path + ".managed", "config.unknownKey", managedNode!);
            return;
        }

        var host = Scalar(values, "host");
        if (!IsYamlString(hostNode) || host is not ("attach" or "managed"))
        {
            AddAtNode(diagnostics, "providers.yaml", path + ".host", IsYamlString(hostNode)
                ? "config.outOfRange" : "config.wrongType", hostNode);
            return;
        }
        if (Scalar(values, "family") is { } family && family != "OpenAiChatCompatible")
            AddAtNode(diagnostics, "providers.yaml", path + ".host", "config.outOfRange", hostNode);
        if (host == "attach")
        {
            if (hasManaged) AddAtNode(diagnostics, "providers.yaml", path + ".managed", "config.unknownKey", managedNode!);
            return;
        }

        if (!hasManaged)
        {
            AddAtNode(diagnostics, "providers.yaml", path + ".managed", "config.missingRequired", values);
            return;
        }
        if (managedNode is not YamlMappingNode managed)
        {
            AddAtNode(diagnostics, "providers.yaml", path + ".managed", "config.wrongType", managedNode!);
            return;
        }

        var managedPath = path + ".managed";
        CheckKeys(managed, "providers.yaml", managedPath, ManagedKeys, diagnostics);
        if (!managed.Children.TryGetValue(new YamlScalarNode("executable"), out var executable))
            AddAtNode(diagnostics, "providers.yaml", managedPath + ".executable", "config.missingRequired", managed);
        else if (!IsYamlString(executable) || string.IsNullOrWhiteSpace(Scalar(managed, "executable")))
            AddAtNode(diagnostics, "providers.yaml", managedPath + ".executable", "config.wrongType", executable);
        if (managed.Children.TryGetValue(new YamlScalarNode("args"), out var args))
        {
            if (args is not YamlSequenceNode argv)
                AddAtNode(diagnostics, "providers.yaml", managedPath + ".args", "config.expectedArgv", args);
            else
                foreach (var item in argv.Children)
                    if (!IsYamlString(item))
                        AddAtNode(diagnostics, "providers.yaml", managedPath + ".args", "config.expectedArgv", item);
        }
        if (managed.Children.TryGetValue(new YamlScalarNode("workingDirectory"), out var directory)
            && !IsYamlString(directory))
            AddAtNode(diagnostics, "providers.yaml", managedPath + ".workingDirectory", "config.wrongType", directory);
        if (managed.Children.TryGetValue(new YamlScalarNode("readinessTimeoutSeconds"), out var timeout)
            && !(timeout is YamlScalarNode { Style: ScalarStyle.Plain, Value: { } raw }
                && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                && seconds is >= 1 and <= 3600))
            AddAtNode(diagnostics, "providers.yaml", managedPath + ".readinessTimeoutSeconds", "config.outOfRange", timeout);

        // Con host: managed el endpoint es siempre loopback; un puerto explícito lo fija.
        if (Scalar(values, "baseUrl") is { } baseUrl && baseUrl != "auto"
            && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && !uri.IsLoopback)
            AddAtNode(diagnostics, "providers.yaml", path + ".baseUrl", "config.outOfRange",
                values.Children[new YamlScalarNode("baseUrl")]);
    }

    /// <summary>
    /// Servidor local de un provider: <c>host</c> explícito o, por defecto, <c>attach</c> para los
    /// providers OpenAI-compatibles con facturación <c>Local</c>. Los demás no tienen servidor que supervisar.
    /// </summary>
    private static LocalHostConfig? LocalHostFor(ProviderFileYaml values, ProviderFamily family, BillingMode billing)
    {
        if (values.Host == "managed" && values.Managed is { } managed)
        {
            var fixedPort = values.BaseUrl is { } url && url != "auto"
                && Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsDefaultPort ? uri.Port : 0;
            return new LocalHostConfig(LocalHostMode.Managed, managed.Executable, managed.Args ?? [],
                managed.WorkingDirectory ?? "", fixedPort,
                managed.ReadinessTimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
        }
        return values.Host == "attach"
            || values.Host is null && family == ProviderFamily.OpenAiChatCompatible && billing == BillingMode.Local
            ? new LocalHostConfig(LocalHostMode.Attach, Declared: values.Host is not null) : null;
    }

    private static ManagedHostYaml CreateManaged(YamlMappingNode map) => new()
    {
        Executable = Scalar(map, "executable"),
        Args = map.Children.TryGetValue(new YamlScalarNode("args"), out var args) && args is YamlSequenceNode sequence
            ? sequence.Children.OfType<YamlScalarNode>().Select(item => item.Value ?? "").ToList() : null,
        WorkingDirectory = Scalar(map, "workingDirectory"),
        ReadinessTimeoutSeconds = int.TryParse(Scalar(map, "readinessTimeoutSeconds"), NumberStyles.None,
            CultureInfo.InvariantCulture, out var seconds) ? seconds : null,
    };

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
            else if (key == "reasoning")
            {
                ValidateReasoning(pair.Value, path + ".reasoning", file, diagnostics);
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
            else if (key == "oauth")
            {
                ValidateOAuth(pair.Value, path + ".oauth", file, diagnostics);
            }
            else if (key == "managed")
            {
                // Mapping validated with the provider's host in ValidateHost.
            }
            else if (key == "billingMode")
            {
                // Type and exact enum-name validation is provider-specific in ValidateRequiredAndRanges.
            }
            else if (pair.Value is not YamlScalarNode)
                AddAtNode(diagnostics, file, path + "." + key, "config.wrongType", pair.Value);
        }
    }

    /// <summary>
    /// Contenido de cada seccion <c>oauth:</c> ya parseada: URLs absolutas https sin userinfo,
    /// placeholder de version y scopes sin repetir. Se hace aqui y no solo en
    /// <see cref="ClaudeOAuthConfiguration.TryBuild"/> para que un providers.yaml inválido
    /// falle al cargarse, en vez de descubrirse a mitad de un login del usuario.
    /// </summary>
    private static void ValidateOAuthContents(ProvidersFileYaml? providers, List<ConfigDiagnostic> diagnostics)
    {
        if (providers?.Providers is null)
        {
            return;
        }

        foreach (var pair in providers.Providers)
        {
            if (pair.Value.OAuth is not { } oauth)
            {
                continue;
            }

            var path = "providers." + pair.Key + ".oauth";
            RequireOAuthField(pair.Key, oauth.ClientId, path + ".clientId", diagnostics);
            var userAgent = RequireOAuthField(pair.Key, oauth.UserAgent, path + ".userAgent", diagnostics);
            RequireOAuthUrl(oauth.AuthorizeUrl, path + ".authorizeUrl", diagnostics);
            RequireOAuthUrl(oauth.TokenUrl, path + ".tokenUrl", diagnostics);
            if (oauth.ProfileUrl is { Length: > 0 })
            {
                RequireOAuthUrl(oauth.ProfileUrl, path + ".profileUrl", diagnostics);
            }

            RequireOAuthScopes(oauth.Scopes, path + ".scopes", diagnostics);

            // Sin {version} el user-agent quedaria clavado entre releases del producto.
            if (userAgent.Length > 0 && !userAgent.Contains("{version}", StringComparison.Ordinal))
            {
                Add(diagnostics, "providers.yaml", path + ".userAgent", "config.oauth.userAgentNeedsVersion");
            }
        }
    }

    private static string RequireOAuthField(string providerId, string? value, string path,
        List<ConfigDiagnostic> diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        Add(diagnostics, "providers.yaml", path, "config.missingRequired");
        return string.Empty;
    }

    private static void RequireOAuthUrl(string? value, string path, List<ConfigDiagnostic> diagnostics)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            Add(diagnostics, "providers.yaml", path, "config.missingRequired");
            return;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            Add(diagnostics, "providers.yaml", path, "config.invalidUrl");
            return;
        }

        // Por aqui viajan el authorization code y el refresh token: otro esquema o un userinfo
        // embebido los entregaria a un destino distinto del declarado (plan §6).
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            Add(diagnostics, "providers.yaml", path, "config.oauth.httpsRequired");
            return;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            Add(diagnostics, "providers.yaml", path, "config.oauth.noUserInfoInUrl");
        }
    }

    private static void RequireOAuthScopes(List<string>? scopes, string path,
        List<ConfigDiagnostic> diagnostics)
    {
        if (scopes is null || scopes.Count == 0)
        {
            Add(diagnostics, "providers.yaml", path, "config.emptyCollection");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in scopes)
        {
            if (string.IsNullOrWhiteSpace(scope))
            {
                Add(diagnostics, "providers.yaml", path, "config.emptyScalar");
                continue;
            }

            if (!seen.Add(scope.Trim()))
            {
                Add(diagnostics, "providers.yaml", path, "config.duplicateItem");
            }
        }
    }

    /// <summary>
    /// Forma de la seccion <c>oauth:</c>: mapeo con claves conocidas y valores del tipo esperado.
    /// Solo forma: el contenido (URLs https, placeholder de version, scopes repetidos) se valida
    /// en <see cref="ClaudeOAuthConfiguration"/> al construir la identidad.
    /// </summary>
    private static void ValidateOAuth(YamlNode node, string path, string file,
        List<ConfigDiagnostic> diagnostics)
    {
        if (node is not YamlMappingNode map)
        {
            AddAtNode(diagnostics, file, path, "config.wrongType", node);
            return;
        }

        CheckKeys(map, file, path, OAuthKeys, diagnostics);
        foreach (var pair in map.Children)
        {
            var key = (pair.Key as YamlScalarNode)?.Value ?? "?";
            if (key == "scopes")
            {
                if (pair.Value is YamlSequenceNode sequence)
                {
                    foreach (var item in sequence)
                    {
                        if (item is not YamlScalarNode scopeItem || !IsYamlString(scopeItem))
                        {
                            AddAtNode(diagnostics, file, path + ".scopes", "config.wrongType", item);
                        }
                    }
                }
                else if (!IsYamlString(pair.Value))
                {
                    AddAtNode(diagnostics, file, path + ".scopes", "config.wrongType", pair.Value);
                }
            }
            else if (!IsYamlString(pair.Value))
            {
                AddAtNode(diagnostics, file, path + "." + key, "config.wrongType", pair.Value);
            }
        }
    }

    private static void ValidateReasoning(YamlNode node, string path, string file,
        List<ConfigDiagnostic> diagnostics)
    {
        if (node is not YamlMappingNode map)
        {
            AddAtNode(diagnostics, file, path, "config.wrongType", node);
            return;
        }
        CheckKeys(map, file, path, ["supported", "effortLevels", "replayPolicy", "ultraCodeBudgetTokens",
            "ultraCodeOutputReserveTokens"], diagnostics);
        var supported = false;
        if (map.Children.TryGetValue(new YamlScalarNode("supported"), out var supportNode))
        {
            if (supportNode is not YamlScalarNode { Style: ScalarStyle.Plain, Value: "true" or "false" } support)
                AddAtNode(diagnostics, file, path + ".supported", "config.wrongType", supportNode);
            else supported = support.Value == "true";
        }
        if (map.Children.TryGetValue(new YamlScalarNode("replayPolicy"), out var policyNode))
        {
            if (!IsYamlString(policyNode))
                AddAtNode(diagnostics, file, path + ".replayPolicy", "config.wrongType", policyNode);
            else if (!Enum.GetNames<ReasoningReplayPolicy>().Contains(((YamlScalarNode)policyNode).Value, StringComparer.Ordinal))
                AddAtNode(diagnostics, file, path + ".replayPolicy", "config.outOfRange", policyNode);
        }
        int? budget = null;
        int? reserve = null;
        if (map.Children.TryGetValue(new YamlScalarNode("ultraCodeBudgetTokens"), out var budgetNode))
        {
            if (budgetNode is YamlScalarNode { Style: ScalarStyle.Plain, Value: { } rawBudget }
                && int.TryParse(rawBudget, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedBudget)
                && parsedBudget >= 1024)
                budget = parsedBudget;
            else AddAtNode(diagnostics, file, path + ".ultraCodeBudgetTokens", "config.outOfRange", budgetNode);
        }
        if (map.Children.TryGetValue(new YamlScalarNode("ultraCodeOutputReserveTokens"), out var reserveNode))
        {
            if (reserveNode is YamlScalarNode { Style: ScalarStyle.Plain, Value: { } rawReserve }
                && int.TryParse(rawReserve, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedReserve)
                && parsedReserve > 0)
                reserve = parsedReserve;
            else AddAtNode(diagnostics, file, path + ".ultraCodeOutputReserveTokens", "config.outOfRange", reserveNode);
        }
        if (!map.Children.TryGetValue(new YamlScalarNode("effortLevels"), out var levelsNode))
        {
            if (budget is not null || reserve is not null)
                AddAtNode(diagnostics, file, path + ".ultraCodeBudgetTokens", "config.outOfRange", map);
            return;
        }
        if (levelsNode is not YamlSequenceNode levels)
        {
            AddAtNode(diagnostics, file, path + ".effortLevels", "config.wrongType", levelsNode);
            return;
        }
        if (!supported)
            AddAtNode(diagnostics, file, path + ".effortLevels", "config.outOfRange", levelsNode);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var level in levels)
        {
            if (!IsYamlString(level))
                AddAtNode(diagnostics, file, path + ".effortLevels", "config.wrongType", level);
            else if (string.IsNullOrWhiteSpace(((YamlScalarNode)level).Value)
                || !unique.Add(((YamlScalarNode)level).Value!))
                AddAtNode(diagnostics, file, path + ".effortLevels", "config.outOfRange", level);
        }
        if ((budget is not null || reserve is not null)
            && (supported != true || !unique.Contains("budget") || budget is null || reserve is null))
            AddAtNode(diagnostics, file, path + ".ultraCodeBudgetTokens", "config.outOfRange", map);
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

    internal static void CheckKeys(YamlMappingNode map, string file, string path, string[] allowed,
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
        YamlNode node) => Add(diagnostics, file, path, key, checked((int)node.Start.Line),
            checked((int)node.Start.Column));

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
        => CostUsd(usage, TokenUsageFields.Input | TokenUsageFields.Output);

    /// <summary>Quotes aggregate prices only for consistent reported quantities; never an account debit.</summary>
    public decimal? CostUsd(TokenUsage usage, TokenUsageFields reportedFields)
    {
        if (!IsComplete || !reportedFields.HasFlag(TokenUsageFields.Input | TokenUsageFields.Output)
            || TokenUsageValidation.IsInvalid(usage, reportedFields)) return null;
        try
        {
            return usage.Input / 1_000_000m * InputPricePerMillionUsd!.Value
                + usage.Output / 1_000_000m * OutputPricePerMillionUsd!.Value;
        }
        catch (OverflowException)
        {
            // An unrepresentable estimate is unavailable, never free spending.
            return null;
        }
    }
}

public sealed record LoadedUserConfiguration(ModelRegistry Registry, ProvidersFileYaml? Providers,
    ModelsFileYaml? Models, IReadOnlyList<LocalizedText>? DeprecationNotices = null,
    UserSettingsYaml? Settings = null)
{
    public decimal SessionCapUsd => Settings?.Budget?.Session ?? 5m;
    public decimal DailyCapUsd => Settings?.Budget?.Daily ?? 20m;

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
