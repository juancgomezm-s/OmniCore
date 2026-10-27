using OmniCore.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OmniCore.Host;

public sealed class KeyBindingsFileYaml
{
    public int Version { get; set; }
    public List<KeyBindingEntryYaml>? Bindings { get; set; }
}

public sealed class KeyBindingEntryYaml
{
    public string? Keys { get; set; }
    public string? Action { get; set; }
    public string? Command { get; set; }
    public List<string>? Arguments { get; set; }
    public string? When { get; set; }
    public int Priority { get; set; }
    [YamlIgnore] public int Line { get; set; }
    [YamlIgnore] public int Column { get; set; }
}

public sealed record KeyBindingConfigurationIssue(string Code, int Line, int Column, string? Field = null);
public sealed record KeyBindingConfigurationSnapshot(string Path, IReadOnlyList<KeyBindingEntryYaml> Bindings,
    IReadOnlyList<KeyBindingConfigurationIssue> Issues);

/// <summary>Loads only the User keybindings file; no workspace/project lookup and no writes.</summary>
public static class KeyBindingConfigurationReader
{
    public const int MaximumCharacters = 65_536;
    private static readonly IDeserializer Reader = new StaticDeserializerBuilder(new OmniYamlStaticContext())
        .WithNamingConvention(CamelCaseNamingConvention.Instance).WithDuplicateKeyChecking().Build();

    public static KeyBindingConfigurationSnapshot LoadUser(string? dataDirectoryOverride, string? configDirectoryOverride,
        CancellationToken cancellationToken) => Load(new OmniCore.Infrastructure.DefaultPlatformPaths(dataDirectoryOverride,
            configDirectoryOverride), cancellationToken);

    public static KeyBindingConfigurationSnapshot Load(IPlatformPaths paths, CancellationToken cancellationToken)
    {
        var path = System.IO.Path.Combine(paths.ConfigDirectory, "keybindings.yaml");
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return new(path, [], []);
        try
        {
            using var reader = File.OpenText(path);
            var buffer = new char[MaximumCharacters + 1];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            cancellationToken.ThrowIfCancellationRequested();
            return count > MaximumCharacters ? new(path, [], [new("file_too_large", 1, 1)])
                : Parse(new string(buffer, 0, count), path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(path, [], [new("file_unreadable", 1, 1)]); }
    }

    public static KeyBindingConfigurationSnapshot Parse(string yaml, string path = "keybindings.yaml")
    {
        if (yaml.Length > MaximumCharacters) return new(path, [], [new("file_too_large", 1, 1)]);
        try
        {
            var file = Reader.Deserialize<KeyBindingsFileYaml>(yaml);
            if (file is null || file.Version != 1 || file.Bindings is null)
                return new(path, [], [new("invalid_document", 1, 1)]);
            if (file.Bindings.Count > 256) return new(path, [], [new("too_many_bindings", 1, 1)]);
            var stream = new YamlStream(); stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root
                || !root.Children.TryGetValue(new YamlScalarNode("bindings"), out var node) || node is not YamlSequenceNode items)
                return new(path, [], [new("invalid_document", 1, 1)]);
            var issues = new List<KeyBindingConfigurationIssue>();
            for (var index = 0; index < file.Bindings.Count; index++)
            {
                var binding = file.Bindings[index]; var item = items.Children[index];
                if (binding is null) { issues.Add(new("invalid_binding", (int)item.Start.Line, (int)item.Start.Column)); continue; }
                binding.Line = (int)item.Start.Line; binding.Column = (int)item.Start.Column;
                var action = !string.IsNullOrWhiteSpace(binding.Action); var command = !string.IsNullOrWhiteSpace(binding.Command);
                var remove = binding.Action?.StartsWith('-') == true;
                if (action == command || binding.Action?.Length > 160 || binding.Command?.Length > 160
                    || (!remove && string.IsNullOrWhiteSpace(binding.Keys)) || binding.Keys?.Length > 256
                    || binding.When?.Length > 512 || Math.Abs((long)binding.Priority) > 1000
                    || binding.Arguments?.Count > 16 || binding.Arguments?.Any(value => value is null || value.Length > 4096) == true
                    || action && binding.Arguments is not null
                    || remove && (!string.IsNullOrEmpty(binding.Keys) || binding.When is not (null or "true") || binding.Priority != 0))
                    issues.Add(new("invalid_binding", binding.Line, binding.Column));
            }
            return issues.Count == 0 ? new(path, file.Bindings.AsReadOnly(), []) : new(path, [], issues.AsReadOnly());
        }
        catch (YamlException error)
        { return new(path, [], [new("invalid_yaml", (int)error.Start.Line, (int)error.Start.Column)]); }
    }
}
