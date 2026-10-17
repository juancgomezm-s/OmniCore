namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Infrastructure;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

internal sealed record SessionSecurityDefaultSnapshot(string Revision, ModelPolicyCategory Category,
    bool IsConfigured, IReadOnlyList<string> Diagnostics);

/// <summary>Explicit User-owned default for newly created native Sessions. Missing or invalid
/// configuration resolves to the conservative ObserveOnly ceiling.</summary>
internal sealed class SessionSecurityDefaultConfiguration(IPlatformPaths paths)
{
    private readonly string _file = Path.Combine(paths.ConfigDirectory, "session-security.yaml");

    internal SessionSecurityDefaultSnapshot Read()
    {
        try
        {
            if (!File.Exists(_file)) return new(Revision(""), ModelPolicyCategory.ObserveOnly, false, []);
            var text = SidebarConfiguration.ReadFile(_file);
            var root = Parse(text);
            var category = ReadCategory(root);
            return new(Revision(text), category, true, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or YamlException or ArgumentException or InvalidOperationException)
        {
            return new("unavailable", ModelPolicyCategory.ObserveOnly, false,
                ["session-security.yaml: " + exception.GetType().Name]);
        }
    }

    internal SessionSecurityDefaultSnapshot Set(string expectedRevision, ModelPolicyCategory category,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedRevision)) throw new ArgumentException("revision required", nameof(expectedRevision));
        if (!Enum.IsDefined(category) || category == ModelPolicyCategory.Custom)
            throw new ArgumentOutOfRangeException(nameof(category));
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(_file)!;
        Directory.CreateDirectory(directory);
        using var lease = ConfigurationWriteLease.Acquire(_file);
        cancellationToken.ThrowIfCancellationRequested();
        var original = File.Exists(_file) ? SidebarConfiguration.ReadFile(_file) : "";
        if (!string.Equals(Revision(original), expectedRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("sessionSecurity.error.stale");
        var root = original.Length == 0 ? new YamlMappingNode() : Parse(original);
        root.Children[new YamlScalarNode("schemaVersion")] = new YamlScalarNode("1") { Style = ScalarStyle.Plain };
        root.Children[new YamlScalarNode("defaultCategory")] = new YamlScalarNode(category.ToString()) { Style = ScalarStyle.Plain };
        var updated = Serialize(root);
        var temporary = _file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, updated, new UTF8Encoding(false));
            cancellationToken.ThrowIfCancellationRequested();
            var current = File.Exists(_file) ? SidebarConfiguration.ReadFile(_file) : "";
            if (!string.Equals(Revision(current), expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("sessionSecurity.error.stale");
            File.Move(temporary, _file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return Read();
    }

    private static YamlMappingNode Parse(string text)
    {
        var stream = new YamlStream();
        using var input = new StringReader(text);
        stream.Load(input);
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("session-security.yaml must contain one mapping.");
        if (!root.Children.TryGetValue(new YamlScalarNode("schemaVersion"), out var version)
            || (version as YamlScalarNode)?.Value != "1")
            throw new InvalidDataException("Unsupported session-security.yaml schema.");
        return root;
    }

    private static ModelPolicyCategory ReadCategory(YamlMappingNode root)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("defaultCategory"), out var node)
            || (node as YamlScalarNode)?.Value is not { } value
            || !Enum.TryParse<ModelPolicyCategory>(value, false, out var category)
            || !Enum.IsDefined(category) || category == ModelPolicyCategory.Custom)
            throw new InvalidDataException("Invalid session security default category.");
        return category;
    }

    private static string Serialize(YamlMappingNode root)
    {
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(root)).Save(output, false);
        return output.ToString();
    }

    private static string Revision(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
