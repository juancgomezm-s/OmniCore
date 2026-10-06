namespace OmniCore.Host;

using System.Globalization;
using YamlDotNet.RepresentationModel;

/// <summary>User-confirmed catalogue registration. Existing provider credentials and TLS settings stay unchanged.</summary>
internal static class ModelCatalogRegistration
{
    internal static void RegisterChatGpt(string directory, string modelId, int context, int output)
        => RegisterChatGpt(directory, new[] { new AvailableChatGptModel(modelId, modelId, context, output) }, false);

    internal static string RegisterChatGpt(string directory, IReadOnlyList<AvailableChatGptModel> catalog, bool discovery = true)
    {
        foreach (var model in catalog)
        {
            if (string.IsNullOrWhiteSpace(model.Id) || model.Id.Length > 200 || model.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or '/' or ':')))
                throw new ArgumentException("Invalid model ID.");
            if (model.ContextWindow is <= 0 || model.MaxOutputTokens is <= 0 || model.MaxOutputTokens > model.ContextWindow)
                throw new ArgumentException("Invalid token limits.");
        }
        Directory.CreateDirectory(directory);
        var providerPath = Path.Combine(directory, "providers.yaml");
        var modelPath = Path.Combine(directory, "models.yaml");
        var oldProviders = File.Exists(providerPath) ? File.ReadAllText(providerPath) : null;
        var oldModels = File.Exists(modelPath) ? File.ReadAllText(modelPath) : null;
        var loaded = new ConfigLoader().Load(oldProviders, oldModels);
        var providers = Parse(oldProviders ?? "providers:\n  local: { baseUrl: http://127.0.0.1:8080, auth: none }\n");
        var models = Parse(oldModels ?? (oldProviders is null ? "models:\n  local-worker: { provider: local, context: 8192, maxOutput: 2048 }\n" : "models: {}\n"));
        var providerMap = (YamlMappingNode)providers.Children[new YamlScalarNode("providers")];
        var providerId = loaded.Providers?.Providers?.FirstOrDefault(p => p.Value.Family == "OpenAIResponses" && p.Value.Profile == "codex").Key;
        if (providerId is null)
        {
            providerId = "chatgpt";
            if (providerMap.Children.ContainsKey(new YamlScalarNode(providerId))) throw new ArgumentException("Provider ID chatgpt already exists with another profile.");
            providerMap.Add(providerId, new YamlMappingNode
            {
                { "family", "OpenAIResponses" }, { "profile", "codex" },
                { "baseUrl", "https://chatgpt.com/backend-api" }, { "auth", "none" }
            });
        }
        foreach (var model in catalog)
        {
            var existing = loaded.Registry.Model(model.Id);
            if (existing is not null)
            {
                if (!discovery || existing.ProviderId != providerId) throw new ArgumentException("Model already registered with another provider.");
                continue; // Preserve the user's limits, aliases, routing and qualification data.
            }
            var context = model.ContextWindow ?? 8192;
            var output = Math.Min(context, model.MaxOutputTokens ?? 2048);
            ((YamlMappingNode)models.Children[new YamlScalarNode("models")]).Add(model.Id, new YamlMappingNode
            {
                { "provider", providerId }, { "context", new YamlScalarNode(context.ToString(CultureInfo.InvariantCulture)) },
                { "maxOutput", new YamlScalarNode(output.ToString(CultureInfo.InvariantCulture)) }
            });
        }
        if (catalog.All(m => loaded.Registry.Model(m.Id) is not null) && loaded.Registry.Provider(providerId) is not null) return providerId;
        var newProviders = Save(providers);
        var newModels = Save(models);
        _ = new ConfigLoader().Load(newProviders, newModels); // Validate both before touching either file.
        var suffix = ".backup-" + Guid.NewGuid().ToString("N");
        if (oldProviders is not null) File.Copy(providerPath, providerPath + suffix);
        if (oldModels is not null) File.Copy(modelPath, modelPath + suffix);
        // Fail closed if configuration changed while the form was being saved.
        if (Read(providerPath) != oldProviders || Read(modelPath) != oldModels) throw new IOException("Configuration changed concurrently.");
        try
        {
            WriteAtomic(providerPath, newProviders);
            WriteAtomic(modelPath, newModels);
        }
        catch
        {
            if (Read(providerPath) == newProviders) Restore(providerPath, oldProviders);
            if (Read(modelPath) == newModels) Restore(modelPath, oldModels);
            throw;
        }
        return providerId;
    }

    private static string? Read(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
    private static void Restore(string path, string? text) { if (text is null) File.Delete(path); else WriteAtomic(path, text); }
    private static void WriteAtomic(string path, string text)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, text); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static YamlMappingNode Parse(string text)
    {
        var stream = new YamlStream(); stream.Load(new StringReader(text));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }
    private static string Save(YamlMappingNode root)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(root)).Save(writer, false);
        return writer.ToString();
    }
}
