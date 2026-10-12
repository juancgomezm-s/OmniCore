namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Infrastructure;
using OmniCore.Security;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

/// <summary>Read and explicit User/workspace security preference operations; it never creates grants.</summary>
public interface ISecurityPreferencesFacade
{
    SecurityPreferencesSnapshot Read();
    SecurityPreferencesSnapshot SaveUserPermissions(string expectedRevision, string profile,
        IReadOnlyDictionary<string, string> rules);
    SecurityPreferencesSnapshot SetWorkspaceTrust(bool expectedCurrent, bool trusted);
    SecurityPreferencesSnapshot RevokeGrant(string grantId);
}

public sealed class SecurityPreferencesHost(IPlatformPaths paths, string workspaceRoot) : ISecurityPreferencesFacade
{
    private readonly IPlatformPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly string _workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
    private string PermissionsFile => Path.Combine(_paths.ConfigDirectory, "permissions.yaml");

    public static SecurityPreferencesHost Create(string workspaceRoot, string? dataDirectoryOverride = null,
        string? configDirectoryOverride = null) => new(
        new DefaultPlatformPaths(dataDirectoryOverride, configDirectoryOverride), workspaceRoot);

    public SecurityPreferencesSnapshot Read()
    {
        var text = ReadPermissionsText();
        var permissions = UserPermissionsLoader.Parse(text);
        var workspaceId = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(
            ProjectIdentity.ResolvePhysicalWorkspaceRoot(_workspaceRoot)));
        var grants = new PermissionGrantCommandHandler(CreateGrantStore(), workspaceId)
            .Handle(new ListPermissionGrantsCommand(), CancellationToken.None).Grants;
        return new(Revision(text), permissions.Profile.ToString(),
            permissions.Rules.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new SecurityPermissionRule(pair.Key, pair.Value.ToString())).ToArray(),
            new WorkspaceTrustStore(_paths).IsTrusted(_workspaceRoot), grants, []);
    }

    public SecurityPreferencesSnapshot SaveUserPermissions(string expectedRevision, string profile,
        IReadOnlyDictionary<string, string> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!Enum.TryParse<PermissionProfile>(profile, ignoreCase: false, out var parsedProfile)
            || !Enum.IsDefined(parsedProfile)) throw new ArgumentException("securityPreferences.error.invalid_profile", nameof(profile));
        using var lease = ConfigurationWriteLease.Acquire(PermissionsFile);
        var original = ReadPermissionsText();
        if (!string.Equals(Revision(original), expectedRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("securityPreferences.error.stale");
        var root = ParseRoot(original);
        root.Children[new YamlScalarNode("profile")] = Plain(profile.ToLowerInvariant());
        var rulesNode = new YamlMappingNode();
        foreach (var pair in rules.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!Enum.TryParse<PermissionDecision>(pair.Value, ignoreCase: false, out var decision)
                || !Enum.IsDefined(decision)) throw new ArgumentException("securityPreferences.error.invalid_rule", nameof(rules));
            rulesNode.Children[new YamlScalarNode(pair.Key)] = Plain(decision.ToString().ToLowerInvariant());
        }
        if (rules.Count == 0) root.Children.Remove(new YamlScalarNode("rules"));
        else root.Children[new YamlScalarNode("rules")] = rulesNode;
        var updated = Serialize(root);
        _ = UserPermissionsLoader.Parse(updated);
        Publish(PermissionsFile, updated, () =>
        {
            if (!string.Equals(Revision(ReadPermissionsText()), expectedRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("securityPreferences.error.stale");
        });
        return Read();
    }

    public SecurityPreferencesSnapshot SetWorkspaceTrust(bool expectedCurrent, bool trusted)
    {
        new WorkspaceTrustStore(_paths).SetTrustedIfUnchanged(_workspaceRoot, expectedCurrent, trusted);
        return Read();
    }

    public SecurityPreferencesSnapshot RevokeGrant(string grantId)
    {
        var workspaceId = WorkspaceId.Of(ProjectIdentity.CanonicalWorkspacePath(
            ProjectIdentity.ResolvePhysicalWorkspaceRoot(_workspaceRoot)));
        var handler = new PermissionGrantCommandHandler(CreateGrantStore(), workspaceId);
        var result = handler.Handle(new RevokePermissionGrantCommand(grantId), CancellationToken.None);
        if (!result.Revoked) throw new InvalidOperationException("securityPreferences.error.grant_missing");
        return Read();
    }

    private FilePermissionGrantStore CreateGrantStore()
    {
        var canonical = ProjectIdentity.CanonicalWorkspacePath(ProjectIdentity.ResolvePhysicalWorkspaceRoot(_workspaceRoot));
        var workspaceId = WorkspaceId.Of(canonical).ToString();
        // Listing grants must remain read-only: WorkspaceDirectory() creates the directory.
        var workspaceData = Path.Combine(_paths.DataDirectory, "workspaces", workspaceId);
        return new(workspaceData, new FileAuditSink(_paths.DataDirectory));
    }

    private string ReadPermissionsText()
    {
        ConfigurationWriteLease.EnsureNoLinks(PermissionsFile);
        return File.Exists(PermissionsFile) ? File.ReadAllText(PermissionsFile) : "";
    }

    private static YamlMappingNode ParseRoot(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new YamlMappingNode();
        var stream = new YamlStream(); stream.Load(new StringReader(text));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new InvalidDataException("securityPreferences.error.invalid_configuration");
        return root;
    }

    private static YamlScalarNode Plain(string value) => new(value) { Style = ScalarStyle.Plain };
    private static string Serialize(YamlMappingNode root)
    {
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        new YamlStream(new YamlDocument(root)).Save(output, false); return output.ToString();
    }
    private static string Revision(string text) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static void Publish(string file, string content, Action recheck)
    {
        ConfigurationWriteLease.EnsureNoLinks(file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false)); recheck();
            ConfigurationWriteLease.EnsureNoLinks(file); File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed record SecurityPermissionRule(string ToolId, string Decision);
public sealed record SecurityPreferencesSnapshot(string Revision, string Profile,
    IReadOnlyList<SecurityPermissionRule> Rules, bool WorkspaceTrusted,
    IReadOnlyList<PermissionGrantSummary> Grants, IReadOnlyList<string> Diagnostics);
