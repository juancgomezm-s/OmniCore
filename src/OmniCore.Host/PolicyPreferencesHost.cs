namespace OmniCore.Host;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Infrastructure;
using OmniCore.Models;
using OmniCore.Protocol;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

/// <summary>Editable, User-owned model routing and budget settings with optimistic revisions.</summary>
public interface IPolicyPreferencesFacade
{
    PolicyPreferencesSnapshot Read();
    PolicyPreferencesSnapshot SaveRouting(string expectedRevision, ModelRoutingPreferences draft);
    PolicyPreferencesSnapshot SaveUserSettings(string expectedRevision, string? defaultModel,
        decimal? sessionCapUsd, decimal? dailyCapUsd);
    PolicyPreferencesSnapshot SaveCommandAliases(string expectedRevision, IReadOnlyDictionary<string, string> aliases);
    PolicyPreferencesSnapshot SaveToolPreferences(string expectedRevision, IReadOnlyDictionary<string, string> preferences);
}

public sealed class PolicyPreferencesHost(IPlatformPaths paths, string workspaceRoot) : IPolicyPreferencesFacade
{
    private readonly IPlatformPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly string _workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
    private string ProvidersFile => Path.Combine(_paths.ConfigDirectory, "providers.yaml");
    private string ModelsFile => Path.Combine(_paths.ConfigDirectory, "models.yaml");
    private string SettingsFile => Path.Combine(_paths.ConfigDirectory, "settings.yaml");

    public static PolicyPreferencesHost Create(string workspaceRoot, string? dataDirectoryOverride = null,
        string? configDirectoryOverride = null) => new(
        new DefaultPlatformPaths(dataDirectoryOverride, configDirectoryOverride), workspaceRoot);

    public PolicyPreferencesSnapshot Read()
    {
        try
        {
            var files = ReadFiles();
            var loaded = new ConfigLoader().Load(files.Providers, files.Models, files.Settings);
            var trusted = new WorkspaceTrustStore(_paths).IsTrusted(_workspaceRoot);
            var scoped = ScopedSettingsLoader.Load(_paths, _workspaceRoot, trusted,
                alias =>
                {
                    try { _ = loaded.Registry.Resolve(alias); return true; }
                    catch (UnknownModelException) { return false; }
                    catch (AmbiguousModelAliasException) { return false; }
                });
            var defaultModel = scoped.Settings?.DefaultModel;
            var source = defaultModel is null ? null : scoped.Scopes?.SourceOf("defaultModel")?.ToString();
            var routing = loaded.Models?.Routing;
            var aliases = loaded.Registry.Models().SelectMany(model => new[] { model.Id }.Concat(model.Aliases))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return new(true, Revision(files), defaultModel, source, loaded.Settings?.DefaultModel,
                loaded.Settings?.Budget?.Session, loaded.Settings?.Budget?.Daily,
                new(routing?.Meta?.ToArray() ?? [], routing?.Exploration?.ToArray() ?? [],
                    routing?.Implementation?.ToArray() ?? [], routing?.Reasoning?.ToArray() ?? [],
                    routing?.Architecture?.ToArray() ?? [], routing?.PreferLocal,
                    routing?.Escalation?.Mode ?? "ask", routing?.Escalation?.Chain?.ToArray() ?? []),
                aliases, [], loaded.Settings?.CommandAliases ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                loaded.Settings?.ToolPreferences ?? new Dictionary<string, string>(StringComparer.Ordinal));
        }
        catch (ConfigValidationException exception)
        {
            return PolicyPreferencesSnapshot.Unavailable(exception.Diagnostics
                .Select(diagnostic => diagnostic.File + " · " + diagnostic.KeyPath + " · " + diagnostic.Message.Key).ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or YamlException
            or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return PolicyPreferencesSnapshot.Unavailable(["configuration.unavailable." + exception.GetType().Name]);
        }
    }

    public PolicyPreferencesSnapshot SaveRouting(string expectedRevision, ModelRoutingPreferences draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(expectedRevision)) throw new ArgumentException("revision required", nameof(expectedRevision));
        using var lease = ProviderConfigurationLease.Acquire(_paths.ConfigDirectory);
        var files = ReadFiles();
        if (!string.Equals(Revision(files), expectedRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("policyPreferences.error.stale");
        var root = ParseRoot(files.Models);
        var modelsKey = new YamlScalarNode("models");
        if (!root.Children.TryGetValue(modelsKey, out var modelsNode))
            root.Children.Add(modelsKey, modelsNode = new YamlMappingNode());
        if (modelsNode is not YamlMappingNode) throw new InvalidDataException("models.models must be a mapping");
        var routingKey = new YamlScalarNode("routing");
        if (!root.Children.TryGetValue(routingKey, out var routingNode))
            root.Children.Add(routingKey, routingNode = new YamlMappingNode());
        if (routingNode is not YamlMappingNode routing) throw new InvalidDataException("models.routing must be a mapping");
        SetList(routing, "meta", draft.Meta);
        SetList(routing, "exploration", draft.Exploration);
        SetList(routing, "implementation", draft.Implementation);
        SetList(routing, "reasoning", draft.Reasoning);
        SetList(routing, "architecture", draft.Architecture);
        var preferLocalKey = new YamlScalarNode("preferLocal");
        if (draft.PreferLocal is { } preferLocal)
            routing.Children[preferLocalKey] = new YamlScalarNode(preferLocal ? "true" : "false") { Style = ScalarStyle.Plain };
        else routing.Children.Remove(preferLocalKey);
        var escalationKey = new YamlScalarNode("escalation");
        if (!routing.Children.TryGetValue(escalationKey, out var escalationNode))
            routing.Children.Add(escalationKey, escalationNode = new YamlMappingNode());
        if (escalationNode is not YamlMappingNode escalation) throw new InvalidDataException("models.routing.escalation must be a mapping");
        escalation.Children[new YamlScalarNode("mode")] = Plain(draft.EscalationMode);
        SetList(escalation, "chain", draft.EscalationChain);
        var updated = Serialize(root);
        _ = new ConfigLoader().Load(files.Providers, updated, files.Settings);
        Publish(ModelsFile, updated, () =>
        {
            if (!string.Equals(Revision(ReadFiles()), expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("policyPreferences.error.stale");
        });
        return Read();
    }

    public PolicyPreferencesSnapshot SaveUserSettings(string expectedRevision, string? defaultModel,
        decimal? sessionCapUsd, decimal? dailyCapUsd)
    {
        if (string.IsNullOrWhiteSpace(expectedRevision)) throw new ArgumentException("revision required", nameof(expectedRevision));
        if (sessionCapUsd is < 0m || dailyCapUsd is < 0m) throw new ArgumentOutOfRangeException(nameof(sessionCapUsd));
        using var modelLease = ProviderConfigurationLease.Acquire(_paths.ConfigDirectory);
        using var settingsLease = ConfigurationWriteLease.Acquire(SettingsFile);
        var files = ReadFiles();
        if (!string.Equals(Revision(files), expectedRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("policyPreferences.error.stale");
        var loaded = new ConfigLoader().Load(files.Providers, files.Models, files.Settings);
        if (defaultModel is not null)
        {
            try { _ = loaded.Registry.Resolve(defaultModel); }
            catch (Exception exception) when (exception is UnknownModelException or AmbiguousModelAliasException)
            { throw new ArgumentException("policyPreferences.error.unknown_model", nameof(defaultModel)); }
        }
        var root = ParseRoot(files.Settings);
        SetScalar(root, "defaultModel", defaultModel);
        var budgetKey = new YamlScalarNode("budget");
        if (sessionCapUsd is not null || dailyCapUsd is not null)
        {
            if (!root.Children.TryGetValue(budgetKey, out var budgetNode)) root.Children.Add(budgetKey, budgetNode = new YamlMappingNode());
            if (budgetNode is not YamlMappingNode budget) throw new InvalidDataException("settings.budget must be a mapping");
            SetScalar(budget, "session", sessionCapUsd?.ToString(CultureInfo.InvariantCulture));
            SetScalar(budget, "daily", dailyCapUsd?.ToString(CultureInfo.InvariantCulture));
        }
        else if (root.Children.TryGetValue(budgetKey, out var existingBudget)
            && existingBudget is YamlMappingNode existingMap)
        {
            SetScalar(existingMap, "session", null);
            SetScalar(existingMap, "daily", null);
            if (existingMap.Children.Count == 0) root.Children.Remove(budgetKey);
        }
        var updated = Serialize(root);
        _ = new ConfigLoader().Load(files.Providers, files.Models, updated);
        Publish(SettingsFile, updated, () =>
        {
            if (!string.Equals(Revision(ReadFiles()), expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("policyPreferences.error.stale");
        });
        return Read();
    }

    public PolicyPreferencesSnapshot SaveCommandAliases(string expectedRevision,
        IReadOnlyDictionary<string, string> aliases)
    {
        _ = new UserCommandAliases(aliases);
        return SaveSettingsMap(expectedRevision, "commandAliases", aliases);
    }

    public PolicyPreferencesSnapshot SaveToolPreferences(string expectedRevision,
        IReadOnlyDictionary<string, string> preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        foreach (var pair in preferences)
            if (!IsToolId(pair.Key) || !IsToolId(pair.Value)
                || pair.Key.Equals(pair.Value, StringComparison.Ordinal))
                throw new ArgumentException("Tool preference ids must be distinct canonical ids.", nameof(preferences));
        return SaveSettingsMap(expectedRevision, "toolPreferences", preferences);
    }

    private PolicyPreferencesSnapshot SaveSettingsMap(string expectedRevision, string name,
        IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(expectedRevision)) throw new ArgumentException("revision required", nameof(expectedRevision));
        using var modelLease = ProviderConfigurationLease.Acquire(_paths.ConfigDirectory);
        using var settingsLease = ConfigurationWriteLease.Acquire(SettingsFile);
        var files = ReadFiles();
        if (!string.Equals(Revision(files), expectedRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("policyPreferences.error.stale");
        var root = ParseRoot(files.Settings);
        var key = new YamlScalarNode(name);
        if (values.Count == 0) root.Children.Remove(key);
        else
        {
            var map = new YamlMappingNode();
            foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                map.Children.Add(Plain(pair.Key), Plain(pair.Value));
            root.Children[key] = map;
        }
        var updated = Serialize(root);
        _ = new ConfigLoader().Load(files.Providers, files.Models, updated);
        Publish(SettingsFile, updated, () =>
        {
            if (!string.Equals(Revision(ReadFiles()), expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("policyPreferences.error.stale");
        });
        return Read();
    }

    private static bool IsToolId(string value) => value.Length is > 0 and <= 160
        && value[0] is >= 'a' and <= 'z'
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');

    private (string? Providers, string? Models, string? Settings) ReadFiles() =>
        (ReadOptional(ProvidersFile), ReadOptional(ModelsFile), ReadOptional(SettingsFile));

    private static string? ReadOptional(string path) => File.Exists(path) ? SidebarConfiguration.ReadFile(path) : null;

    private static string Revision((string? Providers, string? Models, string? Settings) files) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            Length(files.Providers) + "|" + Length(files.Models) + "|" + Length(files.Settings))));

    private static string Length(string? value) => value is null ? "-" : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

    private static YamlMappingNode ParseRoot(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new YamlMappingNode();
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("configuration.mapping_required");
        return root;
    }

    private static void SetList(YamlMappingNode map, string key, IReadOnlyList<string> values)
    {
        var nodeKey = new YamlScalarNode(key);
        if (values.Count == 0) { map.Children.Remove(nodeKey); return; }
        map.Children[nodeKey] = new YamlSequenceNode(values.Select(value => (YamlNode)Plain(value)).ToArray());
    }

    private static void SetScalar(YamlMappingNode map, string key, string? value)
    {
        var nodeKey = new YamlScalarNode(key);
        if (value is null) map.Children.Remove(nodeKey);
        else map.Children[nodeKey] = Plain(value);
    }

    private static YamlScalarNode Plain(string value) => new(value) { Style = ScalarStyle.Plain };

    private static string Serialize(YamlMappingNode root)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(root)).Save(output, false);
        return output.ToString();
    }

    private static void Publish(string file, string content, Action recheck)
    {
        ConfigurationWriteLease.EnsureNoLinks(file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            recheck();
            ConfigurationWriteLease.EnsureNoLinks(file);
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed record ModelRoutingPreferences(IReadOnlyList<string> Meta,
    IReadOnlyList<string> Exploration, IReadOnlyList<string> Implementation,
    IReadOnlyList<string> Reasoning, IReadOnlyList<string> Architecture,
    bool? PreferLocal, string EscalationMode, IReadOnlyList<string> EscalationChain);

public sealed record PolicyPreferencesSnapshot(bool Available, string Revision, string? EffectiveDefaultModel,
    string? DefaultModelSource, string? UserDefaultModel, decimal? SessionCapUsd, decimal? DailyCapUsd,
    ModelRoutingPreferences Routing, IReadOnlyList<string> ModelAliases, IReadOnlyList<string> Diagnostics,
    IReadOnlyDictionary<string, string> CommandAliases, IReadOnlyDictionary<string, string> ToolPreferences)
{
    public static PolicyPreferencesSnapshot Unavailable(IReadOnlyList<string> diagnostics) =>
        new(false, "unavailable", null, null, null, null, null,
            new([], [], [], [], [], null, "ask", []), [], diagnostics,
            new Dictionary<string, string>(), new Dictionary<string, string>());
}
