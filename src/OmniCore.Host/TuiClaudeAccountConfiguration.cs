namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using OmniCore.Domain;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

public sealed record ClaudeAccountConfigurationDraft(string ProviderId, string BaseUrl, string ClientId,
    string UserAgent, string[] Scopes, string AuthorizeUrl, string TokenUrl, string ProfileUrl, string RolesUrl)
{
    public string AgentSystemPrompt { get; init; } = "";
}
public sealed record ClaudeAccountConfigurationSnapshot(string Revision, ClaudeAccountConfigurationDraft Draft);

/// <summary>Explicit configuration only. It never authenticates, reads tokens or sends a request.</summary>
public interface ITuiClaudeAccountConfigurationHost
{
    ClaudeAccountConfigurationSnapshot ReadConfiguration(string? providerId, CancellationToken token);
    void SaveConfiguration(string revision, string? existingProviderId, ClaudeAccountConfigurationDraft draft, CancellationToken token);
}

public sealed partial class TuiClaudeAccountHost
{
    public ClaudeAccountConfigurationSnapshot ReadConfiguration(string? providerId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var files = ConfigurationFiles();
        var loaded = new ConfigLoader().Load(files[0], files[1], files[2]);
        ProviderFileYaml provider;
        string id;
        if (providerId is not null)
        {
            id = providerId;
            provider = loaded.Providers?.Providers?.GetValueOrDefault(id)
                ?? throw new InvalidOperationException("claudeSetup.changed");
            if (loaded.Registry.Provider(id)?.Family != ProviderFamily.AnthropicMessages || provider.OAuth is null)
                throw new InvalidOperationException("claudeSetup.changed");
        }
        else
        {
            // The shipped example is a reviewable onboarding template, not a runtime identity default.
            using var stream = typeof(OmniHost).Assembly.GetManifestResourceStream("OmniCore.Host.examples.providers.yaml")
                ?? throw new InvalidOperationException("claudeSetup.unavailable");
            using var reader = new StreamReader(stream);
            provider = new ConfigLoader().Load(reader.ReadToEnd(), null).Providers!.Providers!["anthropic"];
            id = "anthropic";
            for (var suffix = 2; loaded.Registry.Provider(id) is not null; suffix++) id = "claude-" + suffix;
        }
        var oauth = provider.OAuth!;
        return new(ConfigurationRevision(files), new(id, provider.BaseUrl!, oauth.ClientId!, oauth.UserAgent!,
            oauth.Scopes!.ToArray(), oauth.AuthorizeUrl!, oauth.TokenUrl!, oauth.ProfileUrl ?? "", oauth.RolesUrl ?? "")
            { AgentSystemPrompt = provider.AgentSystemPrompt ?? "" });
    }

    public void SaveConfiguration(string revision, string? existingProviderId, ClaudeAccountConfigurationDraft draft, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!AgentSystemPromptConfiguration.IsValid(draft.AgentSystemPrompt))
            throw new ArgumentException("claudeSetup.system_invalid");
        using var lease = ProviderConfigurationLease.Acquire(_paths.ConfigDirectory);
        var files = ConfigurationFiles();
        if (revision != ConfigurationRevision(files)) throw new InvalidOperationException("claudeSetup.changed");
        if (!System.Text.RegularExpressions.Regex.IsMatch(draft.ProviderId, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$")
            || draft.Scopes.Length is 0 or > 64) throw new ArgumentException("claudeSetup.invalid");
        var loaded = new ConfigLoader().Load(files[0], files[1], files[2]);
        if (existingProviderId is null && loaded.Registry.Provider(draft.ProviderId) is not null)
            throw new InvalidOperationException("claudeSetup.duplicate");
        if (existingProviderId is not null && (draft.ProviderId != existingProviderId
            || loaded.Registry.Provider(existingProviderId)?.Family != ProviderFamily.AnthropicMessages
            || loaded.Providers?.Providers?[existingProviderId].OAuth is null))
            throw new InvalidOperationException("claudeSetup.changed");
        var yaml = new YamlStream();
        yaml.Load(new StringReader(files[0] ?? "providers: {}"));
        var root = (YamlMappingNode)yaml.Documents.Single().RootNode;
        var providers = (YamlMappingNode)root.Children[new YamlScalarNode("providers")];
        root.Style = MappingStyle.Block; providers.Style = MappingStyle.Block;
        var key = new YamlScalarNode(draft.ProviderId);
        var entry = existingProviderId is null ? new YamlMappingNode() : (YamlMappingNode)providers.Children[key];
        entry.Style = MappingStyle.Block;
        static void Put(YamlMappingNode map, string name, string value) =>
            map.Children[new YamlScalarNode(name)] = new YamlScalarNode(value) { Style = ScalarStyle.DoubleQuoted };
        Put(entry, "family", "AnthropicMessages"); Put(entry, "baseUrl", draft.BaseUrl);
        if (string.IsNullOrWhiteSpace(draft.AgentSystemPrompt)) entry.Children.Remove(new YamlScalarNode("agentSystemPrompt"));
        else Put(entry, "agentSystemPrompt", draft.AgentSystemPrompt);
        if (existingProviderId is null) Put(entry, "billingMode", "IncludedQuota");
        var oauth = entry.Children.TryGetValue(new YamlScalarNode("oauth"), out var existingOAuth)
            ? (YamlMappingNode)existingOAuth : new YamlMappingNode();
        oauth.Style = MappingStyle.Block;
        Put(oauth, "clientId", draft.ClientId); Put(oauth, "userAgent", draft.UserAgent);
        Put(oauth, "authorizeUrl", draft.AuthorizeUrl); Put(oauth, "tokenUrl", draft.TokenUrl);
        foreach (var (name, value) in new[] { ("profileUrl", draft.ProfileUrl), ("rolesUrl", draft.RolesUrl) })
        { if (value.Length == 0) oauth.Children.Remove(new YamlScalarNode(name)); else Put(oauth, name, value); }
        oauth.Children[new YamlScalarNode("scopes")] = new YamlSequenceNode(draft.Scopes.Select(scope =>
            (YamlNode)new YamlScalarNode(scope) { Style = ScalarStyle.DoubleQuoted }));
        entry.Children[new YamlScalarNode("oauth")] = oauth;
        providers.Children[key] = entry;
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        yaml.Save(writer, false);
        var updated = writer.ToString();
        _ = new ConfigLoader().Load(updated, files[1], files[2]);
        if (revision != ConfigurationRevision(ConfigurationFiles())) throw new InvalidOperationException("claudeSetup.changed");
        token.ThrowIfCancellationRequested();
        var target = Path.Combine(_paths.ConfigDirectory, "providers.yaml");
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, updated, new UTF8Encoding(false)); File.Move(temporary, target, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string?[] ConfigurationFiles() => new[] { "providers.yaml", "models.yaml", "settings.yaml" }.Select(name =>
    {
        var path = Path.Combine(_paths.ConfigDirectory, name);
        ConfigurationWriteLease.EnsureNoLinks(path);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }).ToArray();

    private static string ConfigurationRevision(string?[] files) => string.Join(":", files.Select(text => text is null ? "missing"
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))));
}
