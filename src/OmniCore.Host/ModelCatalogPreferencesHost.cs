namespace OmniCore.Host;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OmniCore.Models;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

/// <summary>User configuration facade for registered model definitions; it never probes providers.</summary>
public interface IModelCatalogPreferencesHost
{
    ModelCatalogPreferencesSnapshot Read(CancellationToken cancellationToken);
    ModelCatalogPreferencesSnapshot Save(ModelCatalogDraft draft, string expectedRevision,
        CancellationToken cancellationToken);
    ModelCatalogPreferencesSnapshot Delete(string modelId, string expectedRevision,
        CancellationToken cancellationToken);
}

public sealed record ModelCatalogProvider(string Id, string Family);

public sealed record ModelCatalogModel(string Id, string ProviderId, long Context,
    long RecommendedUsableContext, long MaxOutput, double? ParametersBillions,
    IReadOnlyList<string> Aliases, bool CanDelete, IReadOnlyList<string> DeleteBlockers);

public sealed record ModelCatalogPreferencesSnapshot(bool Available, string Revision,
    IReadOnlyList<ModelCatalogProvider> Providers, IReadOnlyList<ModelCatalogModel> Models,
    IReadOnlyList<string> Diagnostics)
{
    public static ModelCatalogPreferencesSnapshot Unavailable(IReadOnlyList<string> diagnostics) =>
        new(false, "unavailable", [], [], diagnostics);
}

/// <summary>Only model fields already supported by models.yaml/ConfigLoader are exposed here.</summary>
public sealed record ModelCatalogDraft(string Id, string ProviderId, long Context,
    long RecommendedUsableContext, long MaxOutput, double? ParametersBillions,
    IReadOnlyList<string> Aliases, string? ExistingId = null)
{
    public override string ToString() => "ModelCatalogDraft [configuration]";
}

/// <summary>Safe localized error key; never includes YAML, provider endpoints or secret references.</summary>
public sealed class ModelCatalogConfigurationException(string code) : InvalidOperationException("modelCatalog.error." + code)
{
    public string MessageKey { get; } = "modelCatalog.error." + code;
}

/// <summary>
/// Reads and atomically updates User models.yaml while preserving untouched YAML nodes. Deletion checks
/// User routing/default and the current Project/Workspace defaults; no provider credentials or network operations are read.
/// </summary>
public sealed class ModelCatalogPreferencesHost : IModelCatalogPreferencesHost
{
    private const int MaximumFileBytes = 256 * 1024;
    private readonly string _directory;
    private readonly string? _workspaceRoot;
    private readonly string? _workspaceDataDirectory;
    private string ModelsFile => Path.Combine(_directory, "models.yaml");
    private string ProvidersFile => Path.Combine(_directory, "providers.yaml");
    private string SettingsFile => Path.Combine(_directory, "settings.yaml");
    private string? WorkspaceSettingsFile => _workspaceRoot is null ? null
        : Path.Combine(_workspaceRoot, ".omnicore", "settings.yaml");
    private string? WorkspaceDataSettingsFile => _workspaceDataDirectory is null ? null
        : Path.Combine(_workspaceDataDirectory, "settings.yaml");

    public ModelCatalogPreferencesHost(string? configDirectory = null, string? workspaceRoot = null,
        string? workspaceDataDirectory = null, string? dataDirectory = null)
    {
        var resolvedWorkspaceRoot = workspaceRoot is null ? null : Path.GetFullPath(workspaceRoot);
        var paths = configDirectory is null || (workspaceRoot is not null && workspaceDataDirectory is null && dataDirectory is null)
            ? OmniHost.CreatePlatformPaths() : null;
        _directory = Path.GetFullPath(configDirectory ?? paths!.ConfigDirectory);
        _workspaceRoot = resolvedWorkspaceRoot;
        _workspaceDataDirectory = resolvedWorkspaceRoot is null ? null : Path.GetFullPath(workspaceDataDirectory
            ?? Path.Combine(Path.GetFullPath(dataDirectory ?? paths!.DataDirectory), "workspaces",
                OmniCore.Domain.WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(
                    ProjectIdentity.ResolvePhysicalWorkspaceRoot(resolvedWorkspaceRoot))).ToString()));
    }

    public static IModelCatalogPreferencesHost Create(string? configDirectory = null, string? workspaceRoot = null,
        string? workspaceDataDirectory = null, string? dataDirectory = null) =>
        new ModelCatalogPreferencesHost(configDirectory, workspaceRoot, workspaceDataDirectory, dataDirectory);

    public ModelCatalogPreferencesSnapshot Read(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var files = ReadFiles();
            var loaded = Load(files.Providers, files.Models, files.Settings);
            var workspaceScopeAvailable = WorkspaceScopeAvailable();
            var rows = loaded.Registry.Models().OrderBy(model => model.Id, StringComparer.Ordinal)
                .Select(model =>
                {
                    var blockers = ReferencesTo(model.Id, model.Aliases, loaded.Registry, loaded.Models?.Routing,
                        loaded.Settings?.DefaultModel, ScopeDefaults(files));
                    return new ModelCatalogModel(model.Id, model.ProviderId, model.ContextWindow,
                        model.RecommendedUsableContext, model.MaxOutputTokens, model.ParameterCountBillions,
                        model.Aliases.ToArray(), workspaceScopeAvailable && blockers.Count == 0,
                        blockers);
                }).ToArray();
            var providers = loaded.Registry.Providers().OrderBy(provider => provider.Id, StringComparer.Ordinal)
                .Select(provider => new ModelCatalogProvider(provider.Id, provider.Family.ToString())).ToArray();
            var diagnostics = workspaceScopeAvailable ? Array.Empty<string>()
                : new[] { "modelCatalog.error.workspaceScopeUnavailable" };
            return new(true, Revision(files), providers, rows, diagnostics);
        }
        catch (ConfigValidationException exception)
        { return ModelCatalogPreferencesSnapshot.Unavailable(exception.Diagnostics.Select(_ => "modelCatalog.error.configurationInvalid").Distinct().ToArray()); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or YamlException or InvalidOperationException or ArgumentException)
        { return ModelCatalogPreferencesSnapshot.Unavailable(["modelCatalog.error.unavailable"]); }
    }

    public ModelCatalogPreferencesSnapshot Save(ModelCatalogDraft draft, string expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDraft(draft);
        try
        {
            var before = ReadFiles();
            RequireRevision(before, expectedRevision);
            using var lease = ProviderConfigurationLease.Acquire(_directory);
            using var settingsLeases = AcquireSettingsLeases();
            cancellationToken.ThrowIfCancellationRequested();
            var files = ReadFiles();
            RequireRevision(files, expectedRevision);
            var loaded = Load(files.Providers, files.Models, files.Settings);
            if (loaded.Registry.Provider(draft.ProviderId) is null) throw Error("unknownProvider");

            var root = ParseRoot(files.Models);
            var modelsKey = new YamlScalarNode("models");
            if (!root.Children.TryGetValue(modelsKey, out var modelsNode))
                root.Children.Add(modelsKey, modelsNode = new YamlMappingNode());
            if (modelsNode is not YamlMappingNode models) throw Error("configurationInvalid");

            var oldId = draft.ExistingId;
            if (oldId is null && loaded.Registry.Model(draft.Id) is not null) throw Error("modelAlreadyExists");
            if (oldId is not null && (!string.Equals(oldId, draft.Id, StringComparison.Ordinal)
                || loaded.Registry.Model(oldId) is null)) throw Error("modelNotFound");
            var idNode = new YamlScalarNode(draft.Id);
            if (oldId is not null && !models.Children.TryGetValue(idNode, out _)) throw Error("modelNotFound");

            YamlMappingNode entry;
            if (models.Children.TryGetValue(idNode, out var existingNode))
            {
                if (oldId is null || existingNode is not YamlMappingNode existingMap) throw Error("modelAlreadyExists");
                entry = existingMap;
            }
            else
            {
                if (oldId is not null) throw Error("modelNotFound");
                entry = new YamlMappingNode();
                models.Children.Add(idNode, entry);
            }

            Put(entry, "provider", draft.ProviderId);
            Put(entry, "context", draft.Context.ToString(CultureInfo.InvariantCulture));
            Put(entry, "recommendedUsableContext", draft.RecommendedUsableContext.ToString(CultureInfo.InvariantCulture));
            Put(entry, "maxOutput", draft.MaxOutput.ToString(CultureInfo.InvariantCulture));
            Put(entry, "parametersBillions", draft.ParametersBillions?.ToString("R", CultureInfo.InvariantCulture));
            PutList(entry, "aliases", draft.Aliases);
            var updated = Serialize(root);
            if (Encoding.UTF8.GetByteCount(updated) > MaximumFileBytes) throw Error("configurationInvalid");
            if (oldId is not null)
                EnsureRetainedAliases(loaded, oldId, draft.Aliases, ScopeDefaults(files));
            var next = Load(files.Providers, updated, files.Settings);
            EnsureReferencesResolve(next, ScopeDefaults(files));
            cancellationToken.ThrowIfCancellationRequested();
            Publish(updated, files, expectedRevision, cancellationToken);
            return Read(cancellationToken);
        }
        catch (ModelCatalogConfigurationException) { throw; }
        catch (ConfigValidationException) { throw Error("configurationInvalid"); }
        catch (AmbiguousModelAliasException) { throw Error("ambiguousAlias"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or YamlException or InvalidOperationException or ArgumentException)
        { throw Error("unavailable"); }
    }

    public ModelCatalogPreferencesSnapshot Delete(string modelId, string expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!WorkspaceScopeAvailable()) throw Error("workspaceScopeUnavailable");
            var before = ReadFiles();
            RequireRevision(before, expectedRevision);
            using var lease = ProviderConfigurationLease.Acquire(_directory);
            using var settingsLeases = AcquireSettingsLeases();
            cancellationToken.ThrowIfCancellationRequested();
            var files = ReadFiles();
            RequireRevision(files, expectedRevision);
            var loaded = Load(files.Providers, files.Models, files.Settings);
            var model = loaded.Registry.Model(modelId) ?? throw Error("modelNotFound");
            var blockers = ReferencesTo(model.Id, model.Aliases, loaded.Registry, loaded.Models?.Routing,
                loaded.Settings?.DefaultModel, ScopeDefaults(files));
            if (blockers.Count > 0) throw new ModelCatalogConfigurationException("referenced");

            var root = ParseRoot(files.Models);
            if (!root.Children.TryGetValue(new YamlScalarNode("models"), out var modelsNode)
                || modelsNode is not YamlMappingNode models
                || !models.Children.Remove(new YamlScalarNode(modelId))) throw Error("modelNotFound");
            var updated = Serialize(root);
            var next = Load(files.Providers, updated, files.Settings);
            EnsureReferencesResolve(next, ScopeDefaults(files));
            cancellationToken.ThrowIfCancellationRequested();
            Publish(updated, files, expectedRevision, cancellationToken);
            return Read(cancellationToken);
        }
        catch (ModelCatalogConfigurationException) { throw; }
        catch (ConfigValidationException) { throw Error("configurationInvalid"); }
        catch (AmbiguousModelAliasException) { throw Error("ambiguousAlias"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or YamlException or InvalidOperationException or ArgumentException)
        { throw Error("unavailable"); }
    }

    private (string? Providers, string? Models, string? Settings, string? ProjectSettings, string? WorkspaceSettings) ReadFiles() =>
        (ReadOptional(ProvidersFile), ReadOptional(ModelsFile), ReadOptional(SettingsFile),
            WorkspaceSettingsFile is { } projectPath ? ReadOptional(projectPath) : null,
            WorkspaceDataSettingsFile is { } workspacePath ? ReadOptional(workspacePath) : null);

    private static string? ReadOptional(string path)
    {
        ConfigurationWriteLease.EnsureNoLinks(path);
        return File.Exists(path) ? SidebarConfiguration.ReadFile(path) : null;
    }

    private bool WorkspaceScopeAvailable()
    {
        if (_workspaceRoot is null || _workspaceDataDirectory is null || !Directory.Exists(_workspaceRoot)) return false;
        try
        {
            ConfigurationWriteLease.EnsureNoLinks(_workspaceRoot);
            ConfigurationWriteLease.EnsureNoLinks(_workspaceDataDirectory);
            return true;
        }
        catch (InvalidDataException) { return false; }
    }

    private IDisposable AcquireSettingsLeases()
    {
        var paths = new[] { SettingsFile, WorkspaceSettingsFile, WorkspaceDataSettingsFile }
            .Where(path => path is not null && Directory.Exists(Path.GetDirectoryName(path)))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderBy(path => path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        var leases = new List<FileStream>(paths.Length);
        try
        {
            foreach (var path in paths) leases.Add(ConfigurationWriteLease.Acquire(path));
            return new SettingsLeases(leases);
        }
        catch
        {
            for (var index = leases.Count - 1; index >= 0; index--) leases[index].Dispose();
            throw;
        }
    }

    private sealed class SettingsLeases(List<FileStream> leases) : IDisposable
    {
        public void Dispose()
        {
            for (var index = leases.Count - 1; index >= 0; index--) leases[index].Dispose();
        }
    }

    private static LoadedUserConfiguration Load(string? providers, string? models, string? settings) =>
        new ConfigLoader().Load(providers, models, settings);

    private static string Revision((string? Providers, string? Models, string? Settings, string? ProjectSettings, string? WorkspaceSettings) files) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            Part(files.Providers) + Part(files.Models) + Part(files.Settings) + Part(files.ProjectSettings) + Part(files.WorkspaceSettings))));

    private static string Part(string? value) => value is null ? "-1:" : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

    private void RequireRevision((string? Providers, string? Models, string? Settings, string? ProjectSettings, string? WorkspaceSettings) files,
        string revision)
    {
        if (!string.Equals(Revision(files), revision, StringComparison.Ordinal)) throw Error("revisionConflict");
    }

    private static IReadOnlyList<string> ReferencesTo(string modelId, IReadOnlyList<string> aliases,
        ModelRegistry registry, RoutingYaml? routing, string? userDefault,
        IReadOnlyList<(string Scope, string? Model)> scopeDefaults)
    {
        var blockers = new List<string>();
        bool Refers(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            try { return string.Equals(registry.Resolve(name).Id, modelId, StringComparison.Ordinal); }
            catch (UnknownModelException) { return aliases.Contains(name, StringComparer.Ordinal); }
        }
        if (Refers(userDefault)) blockers.Add("modelCatalog.reference.userDefault");
        foreach (var (scope, value) in scopeDefaults)
            if (Refers(value)) blockers.Add("modelCatalog.reference." + scope + "Default");
        foreach (var (path, values) in new[]
        {
            ("routing.meta", routing?.Meta), ("routing.exploration", routing?.Exploration),
            ("routing.implementation", routing?.Implementation), ("routing.reasoning", routing?.Reasoning),
            ("routing.architecture", routing?.Architecture), ("routing.escalation.chain", routing?.Escalation?.Chain),
        })
            if (values?.Any(Refers) == true) blockers.Add("modelCatalog.reference." + path);
        return blockers.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string? ReadWorkspaceDefault(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;
        var root = ParseRoot(yaml);
        return root.Children.TryGetValue(new YamlScalarNode("defaultModel"), out var value)
            && value is YamlScalarNode scalar ? scalar.Value : null;
    }

    private static IReadOnlyList<(string Scope, string? Model)> ScopeDefaults(
        (string? Providers, string? Models, string? Settings, string? ProjectSettings, string? WorkspaceSettings) files) =>
        [("project", ReadWorkspaceDefault(files.ProjectSettings)), ("workspace", ReadWorkspaceDefault(files.WorkspaceSettings))];

    private static void EnsureReferencesResolve(LoadedUserConfiguration candidate,
        IReadOnlyList<(string Scope, string? Model)> scopeDefaults)
    {
        foreach (var value in new[] { candidate.Settings?.DefaultModel }
                     .Concat(scopeDefaults.Select(item => item.Model)).Where(value => !string.IsNullOrWhiteSpace(value)))
            try { _ = candidate.Registry.Resolve(value!); }
            catch (Exception exception) when (exception is UnknownModelException or AmbiguousModelAliasException)
            { throw Error("referenced"); }
    }

    private static void EnsureRetainedAliases(LoadedUserConfiguration current, string modelId,
        IReadOnlyList<string> nextAliases, IReadOnlyList<(string Scope, string? Model)> scopeDefaults)
    {
        var model = current.Registry.Model(modelId) ?? throw Error("modelNotFound");
        var names = new List<string?> { current.Settings?.DefaultModel };
        names.AddRange(scopeDefaults.Select(item => item.Model));
        var routing = current.Models?.Routing;
        names.AddRange(new[] { routing?.Meta, routing?.Exploration, routing?.Implementation, routing?.Reasoning,
            routing?.Architecture, routing?.Escalation?.Chain }.Where(values => values is not null).SelectMany(values => values!));
        foreach (var name in names.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            try
            {
                if (string.Equals(current.Registry.Resolve(name!).Id, modelId, StringComparison.Ordinal)
                    && !string.Equals(name, modelId, StringComparison.Ordinal)
                    && !nextAliases.Contains(name!, StringComparer.Ordinal))
                    throw Error("referenced");
            }
            catch (UnknownModelException) { }
        }
    }

    private static void ValidateDraft(ModelCatalogDraft draft)
    {
        static bool ValidId(string value) => value.Length is > 0 and <= 200
            && value == value.Trim() && value.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or '/' or ':');
        if (!ValidId(draft.Id)) throw Error("invalidId");
        if (!ValidId(draft.ProviderId)) throw Error("invalidProvider");
        if (draft.Context is < 1 or > int.MaxValue || draft.RecommendedUsableContext is < 1 or > int.MaxValue
            || draft.MaxOutput is < 1 or > int.MaxValue || draft.RecommendedUsableContext > draft.Context
            || draft.MaxOutput > draft.Context) throw Error("invalidLimits");
        if (draft.ParametersBillions is { } parameters && (!double.IsFinite(parameters) || parameters <= 0 || parameters > 100_000))
            throw Error("invalidParameters");
        if (draft.Aliases.Count > 128 || draft.Aliases.Any(alias => string.IsNullOrWhiteSpace(alias)
                || alias != alias.Trim() || alias.Length > 200 || alias.Any(char.IsControl))
            || draft.Aliases.Distinct(StringComparer.Ordinal).Count() != draft.Aliases.Count)
            throw Error("invalidAliases");
        if (draft.ExistingId is not null && !ValidId(draft.ExistingId)) throw Error("modelNotFound");
    }

    private void Publish(string updated, (string? Providers, string? Models, string? Settings, string? ProjectSettings, string? WorkspaceSettings) files,
        string expectedRevision, CancellationToken cancellationToken)
    {
        ConfigurationWriteLease.EnsureNoLinks(ModelsFile);
        Directory.CreateDirectory(_directory);
        var temporary = ModelsFile + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, updated, new UTF8Encoding(false));
            cancellationToken.ThrowIfCancellationRequested();
            RequireRevision(ReadFiles(), expectedRevision);
            ConfigurationWriteLease.EnsureNoLinks(ModelsFile);
            File.Move(temporary, ModelsFile, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static YamlMappingNode ParseRoot(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new YamlMappingNode();
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw Error("configurationInvalid");
        return root;
    }

    private static void Put(YamlMappingNode map, string key, string? value)
    {
        var yamlKey = new YamlScalarNode(key);
        if (value is null) map.Children.Remove(yamlKey);
        else map.Children[yamlKey] = new YamlScalarNode(value) { Style = ScalarStyle.Plain };
    }

    private static void PutList(YamlMappingNode map, string key, IReadOnlyList<string> values)
    {
        var yamlKey = new YamlScalarNode(key);
        if (values.Count == 0) map.Children.Remove(yamlKey);
        else map.Children[yamlKey] = new YamlSequenceNode(values.Select(value => (YamlNode)new YamlScalarNode(value)
            { Style = ScalarStyle.DoubleQuoted }).ToArray());
    }

    private static string Serialize(YamlMappingNode root)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(root)).Save(writer, false);
        return writer.ToString();
    }

    private static ModelCatalogConfigurationException Error(string code) => new(code);
}
