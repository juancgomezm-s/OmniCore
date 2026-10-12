namespace OmniCore.Host;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OmniCore.Domain;
using OmniCore.Models;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

/// <summary>Configuration only: reading or saving never starts, stops, or probes a server.</summary>
public interface ITuiLocalEngineHost
{
    LocalEnginePreferencesSnapshot Read(CancellationToken cancellationToken);
    LocalEnginePreferencesSnapshot Save(LocalEngineConfigurationDraft draft, string expectedRevision,
        CancellationToken cancellationToken);
    LocalEnginePreferencesSnapshot Delete(string providerId, string expectedRevision,
        CancellationToken cancellationToken);
}

public sealed record LocalEnginePreferencesSnapshot(string Revision,
    IReadOnlyList<LocalEngineConfigurationRow> Engines, IReadOnlyList<string> Diagnostics);

public sealed record LocalEngineConfigurationRow(string ProviderId, string Mode, string BaseUrl,
    string? Executable, IReadOnlyList<string> Arguments, string? WorkingDirectory,
    int ReadinessTimeoutSeconds, bool CanEdit, string? UnavailableReasonKey, bool CanDelete = false);

/// <summary>Arguments are literal argv elements, never a shell command or a credential field.</summary>
public sealed record LocalEngineConfigurationDraft(string ProviderId, string Mode, string BaseUrl,
    string? Executable = null, IReadOnlyList<string>? Arguments = null, string? WorkingDirectory = null,
    int ReadinessTimeoutSeconds = 120)
{
    public override string ToString() => "LocalEngineConfigurationDraft [configuration]";
}

/// <summary>A presentation key, without configuration values or exception details.</summary>
public sealed class LocalEngineConfigurationException(string code) : InvalidOperationException("localEngines.error." + code)
{
    public string MessageKey { get; } = "localEngines.error." + code;
}

/// <summary>User-owned local provider editor. Unrelated providers, auth references and models are preserved.</summary>
public sealed class TuiLocalEngineHost : ITuiLocalEngineHost
{
    private readonly string _directory;
    private readonly ITuiLocalEngineSupervisor? _supervisor;
    private string ProvidersFile => Path.Combine(_directory, "providers.yaml");
    private string ModelsFile => Path.Combine(_directory, "models.yaml");

    public TuiLocalEngineHost(string? configDirectory = null, ITuiLocalEngineSupervisor? supervisor = null)
    {
        _directory = Path.GetFullPath(configDirectory ?? OmniHost.CreatePlatformPaths().ConfigDirectory);
        _supervisor = supervisor;
    }

    public LocalEnginePreferencesSnapshot Read(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var (providers, models) = ReadFiles();
            LoadedUserConfiguration loaded;
            try { loaded = Load(providers, models); }
            catch (ConfigValidationException)
            { return new(Revision(providers, models), [], [Key("configurationInvalid")]); }
            var rows = loaded.Registry.Providers().Where(provider => provider.LocalHost is not null)
                .OrderBy(provider => provider.Id, StringComparer.Ordinal)
                .Select(provider => Row(provider, loaded.Providers?.Providers?.GetValueOrDefault(provider.Id)))
                .ToArray();
            return new(Revision(providers, models), rows, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or YamlException)
        { return new("unavailable", [], [Key("unavailable")]); }
    }

    public LocalEnginePreferencesSnapshot Save(LocalEngineConfigurationDraft draft, string expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDraft(draft);
        try
        {
            var initial = ReadFiles();
            RequireRevision(initial, expectedRevision);
            Directory.CreateDirectory(_directory);
            // Check every ancestor before creating a lease or temporary file.
            _ = SidebarConfiguration.ReadFile(ProvidersFile);
            using var lease = ProviderConfigurationLease.Acquire(_directory);
            cancellationToken.ThrowIfCancellationRequested();
            var original = ReadFiles();
            RequireRevision(original, expectedRevision);
            var loaded = Load(original.Providers, original.Models);
            var existing = loaded.Registry.Provider(draft.ProviderId);
            if (existing is not null && existing.LocalHost is null) throw Error("notLocal");
            if (existing is not null && !Row(existing,
                    loaded.Providers?.Providers?.GetValueOrDefault(existing.Id)).CanEdit)
                throw Error("sensitive");

            // Preserve the implicit local provider if creating the first providers file,
            // so existing models.yaml references to it do not suddenly become dangling.
            var root = Parse(loaded.Providers?.Providers is null ?
                "providers:\n  local:\n    family: OpenAiChatCompatible\n    baseUrl: http://127.0.0.1:8080\n    auth: none\n    billingMode: Local\n" : original.Providers!);
            var providersKey = new YamlScalarNode("providers");
            if (!root.Children.TryGetValue(providersKey, out var providerNode))
                root.Children.Add(providersKey, providerNode = new YamlMappingNode());
            if (providerNode is not YamlMappingNode providers) throw Error("configurationInvalid");
            var id = new YamlScalarNode(draft.ProviderId);
            if (!providers.Children.TryGetValue(id, out var node))
            {
                node = new YamlMappingNode { { "family", "OpenAiChatCompatible" }, { "auth", "none" }, { "billingMode", "Local" } };
                providers.Children.Add(id, node);
            }
            if (node is not YamlMappingNode provider) throw Error("configurationInvalid");
            Put(provider, "host", draft.Mode);
            Put(provider, "baseUrl", draft.BaseUrl.Trim());
            if (draft.Mode == "managed")
            {
                var managed = new YamlMappingNode();
                Put(managed, "executable", draft.Executable!);
                managed.Add("args", new YamlSequenceNode((draft.Arguments ?? [])
                    .Select(argument => new YamlScalarNode(argument) { Style = ScalarStyle.DoubleQuoted })));
                if (!string.IsNullOrWhiteSpace(draft.WorkingDirectory)) Put(managed, "workingDirectory", draft.WorkingDirectory);
                managed.Add("readinessTimeoutSeconds", new YamlScalarNode(
                    draft.ReadinessTimeoutSeconds.ToString(CultureInfo.InvariantCulture)) { Style = ScalarStyle.Plain });
                provider.Children[new YamlScalarNode("managed")] = managed;
            }
            else provider.Children.Remove(new YamlScalarNode("managed"));

            var updated = Serialize(root);
            if (Encoding.UTF8.GetByteCount(updated) > 256 * 1024) throw Error("configurationInvalid");
            _ = Load(updated, original.Models);
            cancellationToken.ThrowIfCancellationRequested();
            var temporary = ProvidersFile + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, updated, new UTF8Encoding(false));
                cancellationToken.ThrowIfCancellationRequested();
                RequireRevision(ReadFiles(), expectedRevision);
                File.Move(temporary, ProvidersFile, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return Read(cancellationToken);
        }
        catch (LocalEngineConfigurationException) { throw; }
        catch (ConfigValidationException) { throw Error("configurationInvalid"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or YamlException) { throw Error("unavailable"); }
    }

    public LocalEnginePreferencesSnapshot Delete(string providerId, string expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var initial = ReadFiles();
            RequireRevision(initial, expectedRevision);
            using var lease = ProviderConfigurationLease.Acquire(_directory);
            cancellationToken.ThrowIfCancellationRequested();
            var original = ReadFiles();
            RequireRevision(original, expectedRevision);
            var loaded = Load(original.Providers, original.Models);
            var provider = loaded.Registry.Provider(providerId);
            var configured = loaded.Providers?.Providers?.GetValueOrDefault(providerId);
            if (provider?.LocalHost is null || configured?.Host is null) throw Error("notRemovable");
            if (loaded.Models?.Models?.Any(pair => string.Equals(pair.Value.Provider, providerId,
                    StringComparison.Ordinal)) == true)
                throw Error("modelsReferencingEngine");
            var runtime = _supervisor?.Read(cancellationToken)
                .FirstOrDefault(row => string.Equals(row.ProviderId, providerId, StringComparison.Ordinal));
            // Do not race a confirmed start/stop or remove config for a live owned process.
            if (configured.Host == "managed" && runtime is { ManagedOwned: true }
                && (runtime.Running || runtime.Transitioning))
                throw Error("managedRunning");

            var root = Parse(original.Providers ?? throw Error("notRemovable"));
            if (!root.Children.TryGetValue(new YamlScalarNode("providers"), out var providerNode)
                || providerNode is not YamlMappingNode providers
                || !providers.Children.Remove(new YamlScalarNode(providerId)))
                throw Error("notRemovable");
            var updated = Serialize(root);
            if (Encoding.UTF8.GetByteCount(updated) > 256 * 1024) throw Error("configurationInvalid");
            _ = Load(updated, original.Models);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_directory);
            var temporary = ProvidersFile + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, updated, new UTF8Encoding(false));
                cancellationToken.ThrowIfCancellationRequested();
                RequireRevision(ReadFiles(), expectedRevision);
                File.Move(temporary, ProvidersFile, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return Read(cancellationToken);
        }
        catch (LocalEngineConfigurationException) { throw; }
        catch (ConfigValidationException) { throw Error("configurationInvalid"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or YamlException) { throw Error("unavailable"); }
    }

    private (string? Providers, string? Models) ReadFiles()
    {
        static string? ReadFile(string path)
        {
            var text = SidebarConfiguration.ReadFile(path);
            return File.Exists(path) ? text : null;
        }
        return (ReadFile(ProvidersFile), ReadFile(ModelsFile));
    }

    private static LoadedUserConfiguration Load(string? providers, string? models) =>
        new ConfigLoader().Load(providers, models);

    private static LocalEngineConfigurationRow Row(ProviderDescriptor provider, ProviderFileYaml? configured)
    {
        var managed = configured?.Managed;
        var mode = provider.LocalHost!.Mode == LocalHostMode.Managed ? "managed" : "attach";
        var endpoint = configured?.BaseUrl ?? (mode == "managed" ? "auto" : provider.BaseUrl);
        var arguments = managed?.Args?.ToArray() ?? [];
        var sensitive = Sensitive(endpoint) || Sensitive(managed?.Executable) || Sensitive(managed?.WorkingDirectory)
            || arguments.Any(Sensitive) || !SafeEndpoint(endpoint, mode);
        return new(provider.Id, mode, sensitive ? "" : endpoint,
            sensitive ? null : managed?.Executable, sensitive ? [] : arguments,
            sensitive ? null : managed?.WorkingDirectory, managed?.ReadinessTimeoutSeconds
                ?? (int)LocalHostConfig.DefaultReadinessTimeout.TotalSeconds,
            !sensitive, sensitive ? Key("sensitive") : null, !sensitive && configured?.Host is not null);
    }

    private static void ValidateDraft(LocalEngineConfigurationDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.ProviderId) || draft.ProviderId.Length > 100
            || !draft.ProviderId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
            throw Error("invalidId");
        if (draft.Mode is not ("attach" or "managed")) throw Error("invalidMode");
        if (!SafeEndpoint(draft.BaseUrl, draft.Mode)) throw Error("invalidEndpoint");
        if (draft.Mode == "managed" && (string.IsNullOrWhiteSpace(draft.Executable)
            || draft.Executable.Length > 4096 || draft.Executable.Any(char.IsControl))) throw Error("invalidExecutable");
        var arguments = draft.Arguments ?? [];
        if (arguments.Count > 128 || arguments.Any(argument => argument is null || argument.Length > 8192
                || argument.Any(char.IsControl)) || arguments.Sum(argument => (long)argument.Length) > 32768
            || draft.WorkingDirectory is { } directory && (directory.Length > 4096 || directory.Any(char.IsControl)))
            throw Error("invalidArguments");
        if (draft.ReadinessTimeoutSeconds is < 1 or > 3600) throw Error("invalidTimeout");
        if (Sensitive(draft.BaseUrl) || Sensitive(draft.Executable) || Sensitive(draft.WorkingDirectory) || arguments.Any(Sensitive))
            throw Error("sensitive");
    }

    private static bool SafeEndpoint(string? value, string mode) =>
        mode == "managed" && value?.Trim() == "auto"
        || value is { Length: <= 4096 } && !value.Any(char.IsControl)
            && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            && (mode != "managed" || uri.IsLoopback);

    private static bool Sensitive(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var lower = value.ToLowerInvariant();
        // Detect credential flag names as whole segments, including split argv forms
        // (--access-token, value), without rejecting ordinary flags such as --tokenizer.
        var credentialFlag = lower.StartsWith('-') && Regex.IsMatch(lower,
            @"(?:^|-|_)(?:api[-_]?key|secret[-_]?key|auth(?:orization)?|bearer|password|passwd|secret|credentials?|token)(?:$|[-_=: ])",
            RegexOptions.CultureInvariant);
        return credentialFlag || lower.Contains("authorization", StringComparison.Ordinal)
            || !string.Equals(new PiiRedactor().Redact(value), value, StringComparison.Ordinal);
    }

    private static string Revision(string? providers, string? models) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{providers?.Length ?? -1}:{providers}{models?.Length ?? -1}:{models}")));
    private static void RequireRevision((string? Providers, string? Models) files, string revision)
    {
        if (!string.Equals(Revision(files.Providers, files.Models), revision, StringComparison.Ordinal)) throw Error("revisionConflict");
    }
    private static YamlMappingNode Parse(string text)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(text));
        if (yaml.Documents.Count == 0) return new YamlMappingNode();
        return yaml.Documents[0].RootNode as YamlMappingNode ?? throw Error("configurationInvalid");
    }
    private static void Put(YamlMappingNode map, string key, string value) =>
        map.Children[new YamlScalarNode(key)] = new YamlScalarNode(value) { Style = ScalarStyle.DoubleQuoted };
    private static string Serialize(YamlMappingNode root)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(root)).Save(output, false);
        return output.ToString();
    }
    private static string Key(string code) => "localEngines.error." + code;
    private static LocalEngineConfigurationException Error(string code) => new(code);
}
