using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Protocol;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OmniCore.Host;

/// <summary>Presentation-only YAML configuration. User -> trusted Project -> local Workspace.
/// Changes require a trusted human command and a matching revision; other settings are preserved.</summary>
public sealed class SidebarConfiguration
{
    private readonly IPlatformPaths _paths;
    private readonly string _workspace;
    private static readonly IDeserializer Deserializer = new StaticDeserializerBuilder(new OmniYamlStaticContext())
        .WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
    public SidebarConfiguration(IPlatformPaths paths, string workspace) { _paths = paths; _workspace = workspace; }
    private string UserFile => Path.Combine(_paths.ConfigDirectory, "settings.yaml");
    private string WorkspaceFile => Path.Combine(OmniHost.WorkspaceDataDirectory(_paths, _workspace), "settings.yaml");
    private string ProjectFile => Path.Combine(_workspace, ".omnicore", "settings.yaml");

    internal static void ValidateRoot(YamlMappingNode root, List<ConfigDiagnostic> diagnostics)
    {
        void Invalid(string path, YamlNode node, string code = "config.wrongType") => ConfigLoader.AddAtNode(diagnostics, "settings.yaml", path, code, node);
        void Validate(YamlNode node, string path, bool sidebar)
        {
            if (node is not YamlMappingNode map) { Invalid(path, node); return; }
            foreach (var field in map.Children)
            {
                var name = (field.Key as YamlScalarNode)?.Value ?? "?";
                var full = path + "." + name;
                var value = (field.Value as YamlScalarNode)?.Value;
                var plain = field.Value is YamlScalarNode { Style: ScalarStyle.Plain };
                var valid = sidebar ? name switch
                {
                    "visible" => plain && value is "true" or "false",
                    "mode" => value is "auto" or "stacked" or "tabbed" or "overlay",
                    "stackedMinWidth" or "tabbedMinWidth" => plain && int.TryParse(value, out var width) && width is >= 50 and <= 500,
                    _ => false,
                } : name switch
                {
                    "visible" => value is "true" or "false" or "auto",
                    "expanded" => plain && value is "true" or "false",
                    "priority" => plain && int.TryParse(value, out var priority) && priority is >= -1000 and <= 1000,
                    _ => false,
                };
                if (!valid) Invalid(full, field.Value, "config.outOfRange");
            }
        }
        if (root.Children.TryGetValue(new YamlScalarNode("sidebar"), out var sidebarNode))
        {
            Validate(sidebarNode, "sidebar", true);
            if (sidebarNode is YamlMappingNode map
                && map.Children.TryGetValue(new YamlScalarNode("stackedMinWidth"), out var stacked)
                && map.Children.TryGetValue(new YamlScalarNode("tabbedMinWidth"), out var tabbed)
                && int.TryParse((stacked as YamlScalarNode)?.Value, out var s) && int.TryParse((tabbed as YamlScalarNode)?.Value, out var t) && s <= t)
                Invalid("sidebar.stackedMinWidth", stacked, "config.outOfRange");
        }
        if (root.Children.TryGetValue(new YamlScalarNode("widgets"), out var widgets))
        {
            if (widgets is not YamlMappingNode map) { Invalid("widgets", widgets); return; }
            foreach (var widget in map.Children)
            {
                var id = (widget.Key as YamlScalarNode)?.Value ?? "";
                if (!ValidId(id)) Invalid("widgets", widget.Key);
                Validate(widget.Value, "widgets." + id, false);
            }
        }
    }

    private static bool ValidId(string id) => id.Length is > 0 and <= 100 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    private static string ReadFile(string path)
    {
        CheckLinks(path);
        if (!File.Exists(path)) return "";
        if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException("SettingsTooLarge");
        return File.ReadAllText(path);
    }
    private static void CheckLinks(string path)
    {
        for (var item = Path.GetFullPath(path); item is not null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("SettingsLinkRejected");
    }
    private static YamlMappingNode Root(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new YamlMappingNode();
        var yaml = new YamlStream(); yaml.Load(new StringReader(text));
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root) throw new InvalidDataException("SettingsMappingRequired");
        var diagnostics = new List<ConfigDiagnostic>(); ValidateRoot(root, diagnostics);
        if (diagnostics.Count > 0) throw new ConfigValidationException(diagnostics);
        return root;
    }
    private (string Scope, string Text)[] Layers()
    {
        var trusted = new WorkspaceTrustStore(_paths).IsTrusted(_workspace);
        return [("User", ReadFile(UserFile)), ("Project", trusted ? ReadFile(ProjectFile) : ""), ("Workspace", ReadFile(WorkspaceFile))];
    }
    private static string Revision((string Scope, string Text)[] layers) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join("\n", layers.Select(l => l.Scope + ":" + l.Text.Length + ":" + l.Text)))));

    public SidebarPreferencesSnapshot Read()
    {
        try
        {
            var layers = Layers(); var prefs = new SidebarPreferences();
            var widgets = new Dictionary<string, WidgetPreferences>(StringComparer.Ordinal);
            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (scope, text) in layers)
            {
                var root = Root(text);
                foreach (var section in new[] { "sidebar", "widgets" })
                    if (root.Children.TryGetValue(new YamlScalarNode(section), out var node) && node is YamlMappingNode map)
                        foreach (var entry in map.Children)
                        {
                            var name = (entry.Key as YamlScalarNode)!.Value!;
                            if (section == "sidebar") sources[section + "." + name] = scope;
                            else if (entry.Value is YamlMappingNode fields)
                                foreach (var key in fields.Children.Keys.OfType<YamlScalarNode>()) sources["widgets." + name + "." + key.Value] = scope;
                        }
                var settings = string.IsNullOrWhiteSpace(text) ? new UserSettingsYaml() : Deserializer.Deserialize<UserSettingsYaml>(text);
                if (settings.Sidebar is { } sidebar) prefs = prefs with
                {
                    Visible = sidebar.Visible ?? prefs.Visible, Mode = sidebar.Mode ?? prefs.Mode,
                    StackedMinWidth = sidebar.StackedMinWidth ?? prefs.StackedMinWidth,
                    TabbedMinWidth = sidebar.TabbedMinWidth ?? prefs.TabbedMinWidth,
                };
                foreach (var (id, setting) in settings.Widgets ?? [])
                {
                    var current = widgets.GetValueOrDefault(id) ?? new WidgetPreferences();
                    widgets[id] = current with { Visible = setting.Visible ?? current.Visible,
                        Expanded = setting.Expanded ?? current.Expanded, Priority = setting.Priority ?? current.Priority };
                }
            }
            if (prefs.StackedMinWidth <= prefs.TabbedMinWidth) throw new InvalidDataException("InvalidBreakpoints");
            return new(Revision(layers), prefs with { Widgets = widgets }, [], sources);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or YamlException or ConfigValidationException)
        {
            var diagnostics = exception is ConfigValidationException config
                ? config.Diagnostics.Select(d => $"{d.File}:{d.Line}:{d.Column} {d.KeyPath}").ToArray()
                : exception is YamlException yaml ? [$"settings.yaml:{yaml.Start.Line + 1}:{yaml.Start.Column + 1} Syntax"]
                : ["Sidebar settings unavailable · " + exception.GetType().Name];
            return new("unavailable", new(), diagnostics);
        }
    }

    public void Set(string scope, string key, string value, string revision)
    {
        if (scope is not ("User" or "Workspace")) throw new ArgumentException("Only User/Workspace presentation settings can be edited");
        var current = Read();
        if (current.Diagnostics.Count > 0) throw new InvalidOperationException("Fix invalid settings before saving");
        if (current.Revision != revision) throw new InvalidOperationException("Settings changed; reload before saving");
        string[] parts;
        if (key.StartsWith("sidebar.", StringComparison.Ordinal)) parts = key.Split('.');
        else if (key.StartsWith("widgets.", StringComparison.Ordinal))
        {
            var end = key.LastIndexOf('.');
            parts = ["widgets", key[8..end], key[(end + 1)..]];
        }
        else throw new ArgumentException("Presentation key required");
        if (parts.Length is not (2 or 3) || parts.Length == 3 && !ValidId(parts[1])) throw new ArgumentException("Invalid widget key");
        if (parts.Length == 3 && parts[1] == "core.session" && parts[2] == "visible" && value is not ("true" or "reset"))
            throw new ArgumentException("Session widget keeps its fixed slot");
        var file = scope == "User" ? UserFile : WorkspaceFile;
        CheckLinks(file); Directory.CreateDirectory(Path.GetDirectoryName(file)!); CheckLinks(file);
        // A lease prevents competing OmniCore writers; revision prevents overwriting intervening edits.
        var lockFile = file + ".sidebar.lock";
        using var lease = new FileStream(lockFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        if (Read().Revision != revision) throw new InvalidOperationException("Settings changed; reload before saving");
        var original = ReadFile(file); var root = Root(original); var map = root;
        foreach (var part in parts[..^1])
        {
            var name = new YamlScalarNode(part);
            if (!map.Children.TryGetValue(name, out var child)) map.Add(name, child = new YamlMappingNode());
            map = child as YamlMappingNode ?? throw new InvalidOperationException("Invalid mapping");
        }
        if (value == "reset") map.Children.Remove(new YamlScalarNode(parts[^1]));
        else map.Children[new YamlScalarNode(parts[^1])] = new YamlScalarNode(value) { Style = ScalarStyle.Plain };
        var diagnostics = new List<ConfigDiagnostic>(); ValidateRoot(root, diagnostics);
        if (diagnostics.Count > 0) throw new ConfigValidationException(diagnostics);
        var stream = new YamlStream(new YamlDocument(root)); using var output = new StringWriter(CultureInfo.InvariantCulture); stream.Save(output, false);
        var content = output.ToString();
        // Also validate effective cross-scope breakpoint ordering before publishing.
        var layers = Layers(); layers[scope == "User" ? 0 : 2] = (scope, content);
        var stacked = 120; var tabbed = 90;
        foreach (var layer in layers)
        {
            var settings = string.IsNullOrWhiteSpace(layer.Text) ? new UserSettingsYaml() : Deserializer.Deserialize<UserSettingsYaml>(layer.Text);
            stacked = settings.Sidebar?.StackedMinWidth ?? stacked; tabbed = settings.Sidebar?.TabbedMinWidth ?? tabbed;
        }
        if (stacked <= tabbed) throw new ArgumentException("Stacked breakpoint must exceed tabbed breakpoint");
        var temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            if (ReadFile(file) != original) throw new InvalidOperationException("Settings changed; reload before saving");
            CheckLinks(file); File.Move(temp, file, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
