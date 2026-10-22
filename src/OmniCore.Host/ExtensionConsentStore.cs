namespace OmniCore.Host;

using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;

/// <summary>Workspace-local, hash-bound consent for extension capabilities.</summary>
public sealed class ExtensionConsentStore
{
    private readonly string _file;

    public ExtensionConsentStore(IPlatformPaths paths, string workspaceRoot)
        : this(paths, ScopeLevel.Project, workspaceRoot) { }

    public ExtensionConsentStore(IPlatformPaths paths, ScopeLevel scope, string? workspaceRoot = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (scope == ScopeLevel.Project)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
            var id = WorkspaceId.Of(WorkspaceTrustStore.Canonical(workspaceRoot)).ToString();
            _file = Path.Combine(paths.DataDirectory, "workspaces", id, "extension-consent.tsv");
        }
        else if (scope == ScopeLevel.User) _file = Path.Combine(paths.DataDirectory, "extensions", "extension-consent.tsv");
        else throw new ArgumentOutOfRangeException(nameof(scope));
    }

    public IReadOnlySet<string> ConsentedCapabilities(DiscoveredExtension extension)
    {
        var entry = Read().FirstOrDefault(candidate => candidate.Id == extension.Manifest?.Id
            && candidate.Version == extension.Manifest?.Version
            && candidate.PackageHash == extension.ConsentHash);
        return entry is null ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(entry.Capabilities, StringComparer.Ordinal);
    }

    public void Grant(DiscoveredExtension extension, IReadOnlySet<string> capabilities)
    {
        if (extension.Manifest?.Id is not { Length: > 0 } id || extension.Manifest.Version is not { Length: > 0 } version
            || extension.Status is ExtensionDiscoveryStatus.InvalidManifest or ExtensionDiscoveryStatus.IncompatibleApi
            || extension.Trust == OmniCore.Domain.TrustLevel.Untrusted)
            throw new InvalidOperationException("Only a valid, compatible extension from a known source can receive consent.");
        ArgumentNullException.ThrowIfNull(capabilities);
        var requested = new HashSet<string>(extension.Manifest.Requests?.Capabilities ?? [], StringComparer.Ordinal);
        if (!capabilities.IsSubsetOf(requested))
            throw new InvalidOperationException("Consent cannot exceed the capabilities requested by the manifest.");

        var entries = Read().Where(candidate => candidate.Id != id).ToList();
        entries.Add(new Entry(id, version, extension.ConsentHash, capabilities.Order(StringComparer.Ordinal).ToArray(), Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temp = _file + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllLines(temp, entries.OrderBy(entry => entry.Id, StringComparer.Ordinal).Select(Encode), Encoding.UTF8);
        File.Move(temp, _file, overwrite: true);
    }

    public void Revoke(string extensionId)
    {
        var entries = Read().Where(entry => !string.Equals(entry.Id, extensionId, StringComparison.Ordinal)).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temp = _file + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllLines(temp, entries.Select(Encode), Encoding.UTF8);
        File.Move(temp, _file, overwrite: true);
    }

    private IReadOnlyList<Entry> Read()
    {
        if (!File.Exists(_file)) return [];
        var entries = new List<Entry>();
        foreach (var line in File.ReadLines(_file, Encoding.UTF8))
        {
            var fields = line.Split('\t');
            if (fields.Length is not (4 or 5)) throw new InvalidDataException("Extension consent store is malformed.");
            try
            {
                var id = Decode(fields[0]); var version = Decode(fields[1]); var hash = fields[2];
                var caps = Decode(fields[3]).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (id.Length == 0 || version.Length == 0 || hash.Length != 128 || hash.Any(character => !Uri.IsHexDigit(character)))
                    throw new FormatException("Invalid extension consent identity.");
                var nonce = fields.Length == 5 ? fields[4] : "";
                if (nonce.Length != 0 && !Guid.TryParseExact(nonce, "N", out _)) throw new FormatException("Invalid consent revision.");
                entries.Add(new Entry(id, version, hash, caps, nonce));
            }
            catch (FormatException exception) { throw new InvalidDataException("Extension consent store is malformed.", exception); }
        }
        return entries;
    }

    private static string Encode(Entry entry) => string.Join('\t', Encode(entry.Id), Encode(entry.Version),
        entry.PackageHash, Encode(string.Join('\n', entry.Capabilities)), entry.Nonce);
    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
    public string ConsentIdentity(DiscoveredExtension extension)
    {
        var entry = Read().SingleOrDefault(candidate => candidate.Id == extension.Manifest?.Id
            && candidate.Version == extension.Manifest?.Version && candidate.PackageHash == extension.ConsentHash);
        return entry is null ? "" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Encode(entry))));
    }
    private sealed record Entry(string Id, string Version, string PackageHash, IReadOnlyList<string> Capabilities, string Nonce);
}
