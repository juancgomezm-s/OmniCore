namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using OmniCore.Abstractions;
using OmniCore.Protocol;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

/// <summary>Reads and atomically updates only the User-owned <c>ui.locale</c> setting.</summary>
public sealed class UserLocaleConfiguration(IPlatformPaths paths)
{
    private readonly string _file = Path.Combine(
        (paths ?? throw new ArgumentNullException(nameof(paths))).ConfigDirectory, "settings.yaml");

    /// <summary>Reads the current user's configured UI locale using Host-owned platform paths.</summary>
    public static UiLocalePreferencesSnapshot ReadCurrentUser() =>
        new UserLocaleConfiguration(OmniHost.CreatePlatformPaths()).Read();

    public UiLocalePreferencesSnapshot Read()
    {
        try
        {
            var text = SidebarConfiguration.ReadFile(_file);
            var diagnostics = new List<ConfigDiagnostic>();
            var root = ConfigLoader.ParseSettings(text, diagnostics);
            var locale = "es";
            var isConfigured = false;
            if (root?.Children.TryGetValue(new YamlScalarNode("ui"), out var uiNode) == true
                && uiNode is YamlMappingNode ui
                && ui.Children.TryGetValue(new YamlScalarNode("locale"), out var localeNode)
                && (localeNode as YamlScalarNode)?.Value is { } configured
                && UiLocaleCodes.TryNormalize(configured, out var normalized))
            {
                locale = normalized;
                isConfigured = true;
            }
            if (diagnostics.Count != 0)
            {
                locale = "es";
                isConfigured = false;
            }
            return new(Revision(text), locale, diagnostics.Select(FormatDiagnostic).ToArray(), isConfigured);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or YamlException
            or InvalidDataException)
        {
            return new("unavailable", "es", ["settings.yaml: " + exception.GetType().Name], false);
        }
    }

    public void Set(UiLocaleUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Scope, "User", StringComparison.Ordinal))
            throw WithLocaleError(new ArgumentException("UI locale can only be saved in User settings.", nameof(request)),
                "ui.locale.error.scope");
        if (!UiLocaleCodes.TryNormalize(request.Locale, out var locale))
            throw WithLocaleError(new ArgumentException("The UI locale is not supported.", nameof(request)),
                "ui.locale.error.unsupported");
        if (string.IsNullOrWhiteSpace(request.Revision))
            throw WithLocaleError(new ArgumentException("A settings revision is required.", nameof(request)),
                "ui.locale.error.revision_required");
        cancellationToken.ThrowIfCancellationRequested();

        var current = Read();
        if (current.Diagnostics.Count != 0)
            throw WithLocaleError(new InvalidOperationException("Fix invalid User settings before saving the UI locale."),
                "ui.locale.error.invalid_settings");
        if (!string.Equals(current.Revision, request.Revision, StringComparison.Ordinal))
            throw WithLocaleError(new InvalidOperationException("User settings changed; reload before saving."),
                "ui.locale.error.stale");

        var directory = Path.GetDirectoryName(_file)!;
        Directory.CreateDirectory(directory);
        _ = SidebarConfiguration.ReadFile(_file); // Re-check the path after creating the directory.
        // Share the settings lease used by the existing settings writer, so a locale save cannot
        // race a sidebar edit and discard its unrelated keys.
        using var lease = ConfigurationWriteLease.Acquire(_file);
        cancellationToken.ThrowIfCancellationRequested();

        var original = SidebarConfiguration.ReadFile(_file);
        if (!string.Equals(Revision(original), request.Revision, StringComparison.Ordinal))
            throw WithLocaleError(new InvalidOperationException("User settings changed; reload before saving."),
                "ui.locale.error.stale");
        var diagnostics = new List<ConfigDiagnostic>();
        var root = ConfigLoader.ParseSettings(original, diagnostics) ?? new YamlMappingNode();
        if (diagnostics.Count != 0)
            throw new ConfigValidationException(diagnostics);
        var uiKey = new YamlScalarNode("ui");
        if (!root.Children.TryGetValue(uiKey, out var uiNode)) root.Children.Add(uiKey, uiNode = new YamlMappingNode());
        if (uiNode is not YamlMappingNode ui)
            throw new InvalidOperationException("The User ui setting must be a mapping.");
        ui.Children[new YamlScalarNode("locale")] = new YamlScalarNode(locale) { Style = ScalarStyle.Plain };

        var updated = Serialize(root);
        var validation = new List<ConfigDiagnostic>();
        _ = ConfigLoader.ParseSettings(updated, validation);
        if (validation.Count != 0) throw new ConfigValidationException(validation);
        cancellationToken.ThrowIfCancellationRequested();

        var temporary = _file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, updated, new UTF8Encoding(false));
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(SidebarConfiguration.ReadFile(_file), original, StringComparison.Ordinal))
                throw WithLocaleError(new InvalidOperationException("User settings changed; reload before saving."),
                    "ui.locale.error.stale");
            _ = SidebarConfiguration.ReadFile(_file); // Re-check reparse points immediately before publish.
            File.Move(temporary, _file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Serialize(YamlMappingNode root)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(root)).Save(output, false);
        return output.ToString();
    }

    private static string Revision(string content) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static T WithLocaleError<T>(T exception, string key) where T : Exception
    {
        exception.Data["UiLocaleErrorKey"] = key;
        return exception;
    }

    private static string FormatDiagnostic(ConfigDiagnostic diagnostic) =>
        $"{diagnostic.File}:{diagnostic.Line}:{diagnostic.Column} {diagnostic.KeyPath} ({diagnostic.Message.Key})";
}
