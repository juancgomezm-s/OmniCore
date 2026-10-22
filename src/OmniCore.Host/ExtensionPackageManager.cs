using System.IO.Compression;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Protocol;
using OmniCore.Infrastructure;

namespace OmniCore.Host;

/// <summary>User-owned lifecycle over the existing M8 root. Installation never executes code.</summary>
internal sealed class ExtensionPackageManager
{
    private readonly IPlatformPaths paths;
    public ExtensionPackageManager(IPlatformPaths paths)
    {
        this.paths = paths;
        if (Directory.Exists(StateRoot) && Directory.EnumerateFiles(StateRoot, "*.update.json").Any())
        { using var lease = AcquireLease(); RecoverUpdates(); }
    }
    internal Action? BeforeUpdatePublish { get; set; }
    private string Root => Path.Combine(paths.DataDirectory, "extensions");
    private string StateRoot => Path.Combine(paths.DataDirectory, "extension-state");
    private const long MaximumBytes = 64 * 1024 * 1024;

    public ExtensionPackageState Install(string source, CancellationToken token, string? replacingId = null,
        string? expectedManifest = null, string? expectedPackage = null)
    {
        using var lease = AcquireLease();
        var stage = Path.Combine(paths.DataDirectory, "extension-staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var count = 0; long bytes = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(source))
            {
                source = Path.GetFullPath(source); RejectLinks(source);
                if (stage.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidDataException("extension_source_contains_data_directory");
                var pending = new Stack<string>(); pending.Push(source);
                while (pending.TryPop(out var directory))
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        token.ThrowIfCancellationRequested(); RejectLinks(entry);
                        if (++count > 512) throw new InvalidDataException("extension_package_limit");
                        var relativeEntry = Path.GetRelativePath(source, entry).Replace('\\', '/');
                        if (!names.Add(relativeEntry)) throw new InvalidDataException("extension_path_collision");
                        if (Directory.Exists(entry)) { pending.Push(entry); continue; }
                        var destination = PackagePath(stage, relativeEntry);
                        var expectedLength = new FileInfo(entry).Length; bytes += expectedLength;
                        if (bytes > MaximumBytes) throw new InvalidDataException("extension_package_limit");
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        using var input = File.OpenRead(entry); using var output = new FileStream(destination, FileMode.CreateNew);
                        var buffer = new byte[32768]; long copied = 0; int read;
                        while ((read = input.Read(buffer)) > 0)
                        {
                            token.ThrowIfCancellationRequested(); copied += read;
                            if (copied > expectedLength) throw new InvalidDataException("extension_package_limit");
                            output.Write(buffer, 0, read);
                        }
                        ExtensionPackageFileMode.Copy(entry, destination);
                    }
            }
            else
            {
                using var zip = ZipFile.OpenRead(source);
                foreach (var entry in zip.Entries)
                {
                    if (++count > 512) throw new InvalidDataException("extension_package_limit");
                    token.ThrowIfCancellationRequested(); var destination = PackagePath(stage, entry.FullName);
                    if (!names.Add(Path.GetRelativePath(stage, destination))) throw new InvalidDataException("extension_path_collision");
                    if ((entry.ExternalAttributes >> 16 & 0xf000) == 0xa000) throw new InvalidDataException("extension_link_rejected");
                    if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
                    bytes += entry.Length;
                    if (bytes > MaximumBytes) throw new InvalidDataException("extension_package_limit");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var input = entry.Open(); using var output = new FileStream(destination, FileMode.CreateNew);
                    var buffer = new byte[32768]; long total = 0; int read;
                    while ((read = input.Read(buffer)) > 0)
                    {
                        token.ThrowIfCancellationRequested(); total += read;
                        if (total > entry.Length) throw new InvalidDataException("extension_archive_size_invalid");
                        output.Write(buffer, 0, read);
                    }
                    if (total != entry.Length) throw new InvalidDataException("extension_archive_size_invalid");
                    ExtensionPackageFileMode.FromArchive(destination, entry.ExternalAttributes);
                }
            }
            var file = Path.Combine(stage, ExtensionManifestLoader.ManifestFileName);
            if (!File.Exists(file) || new FileInfo(file).Length > 65536) throw new InvalidDataException("extension_manifest_missing_or_large");
            var manifest = new ExtensionManifestLoader().Load(File.ReadAllText(file));
            ValidatePackageFiles(stage, manifest); var existing = Discover();
            if (replacingId is not null && manifest.Id != replacingId) throw new InvalidOperationException("extension_update_identity_mismatch");
            if (replacingId is null && (existing.Any(item => item.Manifest?.Id == manifest.Id) || Directory.Exists(Path.Combine(Root, manifest.Id!))))
                throw new InvalidOperationException("extension_already_installed_uninstall_first_data_preserved");
            foreach (var dependency in manifest.Dependencies ?? [])
            {
                var pair = dependency.Split('@', 2);
                if (pair.Length != 2 || !existing.Any(item => item.Manifest?.Id == pair[0] && item.Manifest.Version == pair[1]))
                    throw new InvalidOperationException("extension_dependency_missing:" + dependency);
            }
            Directory.CreateDirectory(Root); RejectLinks(Root);
            var discovered = new ExtensionManifestLoader().Discover(Path.GetDirectoryName(stage)!, TrustLevel.Trusted, true)
                .Single(item => item.Directory == stage);
            var state = new ExtensionPackageState(manifest.Id!, manifest.Version!,
                discovered.Status == ExtensionDiscoveryStatus.IncompatibleApi || !SupportsPlatform(manifest) ? "incompatible" : "installed",
                discovered.ManifestSha256, discovered.PackageSha256, Path.GetFullPath(source), false, false, [],
                RuntimeKind: manifest.Runtime?.Kind, RequestedCapabilities: manifest.Requests?.Capabilities,
                ConfigurationKeys: manifest.Configuration);
            if (replacingId is not null)
            {
                var previous = Inspect(replacingId);
                if (previous.ManifestSha256 != expectedManifest || previous.PackageSha256 != expectedPackage
                    || previous.Diagnostic is not null) throw new InvalidOperationException("extension_consent_identity_changed");
                if (state.State == "incompatible" || discovered.Status is not (ExtensionDiscoveryStatus.ConsentRequired or ExtensionDiscoveryStatus.Ready))
                    throw new InvalidOperationException("extension_update_incompatible");
                if (existing.Any(item => item.Manifest?.Id != replacingId && item.Manifest?.Dependencies?.Any(dependency =>
                    dependency.StartsWith(replacingId + "@", StringComparison.Ordinal) && dependency != replacingId + "@" + manifest.Version) == true))
                    throw new InvalidOperationException("extension_update_breaks_dependents");
                PublishUpdate(stage, previous, state); return state;
            }
            Save(state); // publish disabled state first; crash cannot implicitly enable code.
            Directory.Move(stage, Path.Combine(Root, manifest.Id!)); return state;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    public ExtensionPackageState Rollback(string id, string expectedManifest, string expectedPackage, CancellationToken token)
    {
        ValidateId(id);
        var file = Path.Combine(StateRoot, id + ".rollback.json"); RejectLinks(file);
        var point = JsonSerializer.Deserialize(File.ReadAllText(file), ExtensionLifecycleJson.Default.ExtensionUpdateIntent)
            ?? throw new InvalidDataException("extension_rollback_missing");
        var archive = ArchivePath(point);
        var candidate = new ExtensionManifestLoader().Discover(Path.GetDirectoryName(archive)!, TrustLevel.Trusted, true)
            .Single(item => item.Directory == archive);
        if (point.Id != id || candidate.ManifestSha256 != point.Previous.ManifestSha256 || candidate.PackageSha256 != point.Previous.PackageSha256)
            throw new InvalidDataException("extension_rollback_hash_changed");
        return Install(archive, token, id, expectedManifest, expectedPackage);
    }

    private void PublishUpdate(string stage, ExtensionPackageState previous, ExtensionPackageState next)
    {
        var live = Path.Combine(Root, previous.Id); RejectLinks(live);
        var intent = new ExtensionUpdateIntent(previous.Id, previous.Id + "-" + Guid.NewGuid().ToString("N"), Path.GetFileName(stage), previous);
        var archive = ArchivePath(intent); Directory.CreateDirectory(Path.GetDirectoryName(archive)!); RejectLinks(Path.GetDirectoryName(archive)!);
        var intentFile = Path.Combine(StateRoot, previous.Id + ".update.json");
        WriteLifecycle(intentFile, intent);
        try
        {
            new ExtensionConsentStore(paths, ScopeLevel.User).Revoke(previous.Id);
            Save(Disabled(previous));
            Directory.Move(live, archive);
            BeforeUpdatePublish?.Invoke();
            Save(next); Directory.Move(stage, live);
            WriteLifecycle(Path.Combine(StateRoot, previous.Id + ".rollback.json"), intent);
            File.Delete(intentFile); // Publication complete; new hashes still require explicit consent.
        }
        catch { RecoverUpdates(); throw; }
    }

    private string ArchivePath(ExtensionUpdateIntent intent)
    {
        ValidateId(intent.Id);
        if (intent.Previous.Id != intent.Id || !intent.ArchiveName.StartsWith(intent.Id + "-", StringComparison.Ordinal)
            || !Guid.TryParseExact(intent.ArchiveName[(intent.Id.Length + 1)..], "N", out _)
            || !Guid.TryParseExact(intent.StageName, "N", out _)) throw new InvalidDataException("extension_update_intent_invalid");
        return Path.Combine(paths.DataDirectory, "extension-archive", intent.ArchiveName);
    }
    private void RecoverUpdates()
    {
        foreach (var file in Directory.EnumerateFiles(StateRoot, "*.update.json"))
        {
            RejectLinks(file);
            var intent = JsonSerializer.Deserialize(File.ReadAllText(file), ExtensionLifecycleJson.Default.ExtensionUpdateIntent)
                ?? throw new InvalidDataException("extension_update_intent_invalid");
            var archive = ArchivePath(intent);
            if (Path.GetFileName(file) != intent.Id + ".update.json") throw new InvalidDataException("extension_update_intent_invalid");
            var live = Path.Combine(Root, intent.Id);
            new ExtensionConsentStore(paths, ScopeLevel.User).Revoke(intent.Id);
            if (Directory.Exists(archive))
            {
                RejectLinks(archive);
                if (Directory.Exists(live))
                {
                    RejectLinks(live);
                    Directory.Move(live, Path.Combine(Path.GetDirectoryName(archive)!, intent.Id + "-" + Guid.NewGuid().ToString("N")));
                }
                Directory.Move(archive, live);
            }
            if (!Directory.Exists(live)) throw new IOException("extension_update_recovery_requires_archive");
            Save(Disabled(intent.Previous));
            var rollback = Path.Combine(StateRoot, intent.Id + ".rollback.json");
            if (File.Exists(rollback)) { RejectLinks(rollback); File.Delete(rollback); }
            File.Delete(file);
        }
    }
    private static ExtensionPackageState Disabled(ExtensionPackageState previous) => previous with
        { State = "disabled", Enabled = false, TrustedProcess = false, GrantedCapabilities = [], Diagnostic = null };
    private static void WriteLifecycle(string file, ExtensionUpdateIntent intent)
    {
        var temporary = file + "." + Guid.NewGuid().ToString("N");
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(intent, ExtensionLifecycleJson.Default.ExtensionUpdateIntent));
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(flushToDisk: true); }
        File.Move(temporary, file, true);
    }
    public IReadOnlyList<DiscoveredExtension> Discover() => new ExtensionManifestLoader().Discover(Root, TrustLevel.Trusted, true);
    public IReadOnlyList<ExtensionPackageState> List() => Discover().Select(Inspect).ToArray();
    public ExtensionPackageState Inspect(string id) => Inspect(Find(id));
    internal bool HasProcessGrant(ExtensionPackageState expected)
    {
        var current = Read(expected.Id);
        return current is { Enabled: true, TrustedProcess: true }
            && current.ManifestSha256 == expected.ManifestSha256 && current.PackageSha256 == expected.PackageSha256
            && current.GrantedCapabilities.All(CurrentConsent(current).Contains);
    }
    private IReadOnlySet<string> CurrentConsent(ExtensionPackageState state) => new ExtensionConsentStore(paths, ScopeLevel.User)
        .ConsentedCapabilities(new DiscoveredExtension(Path.Combine(Root, state.Id), new ExtensionManifest { Id = state.Id, Version = state.Version },
            ExtensionDiscoveryStatus.ConsentRequired, null, state.ManifestSha256, state.PackageSha256, TrustLevel.Trusted));
    internal IDisposable AcquireActivationLease(ExtensionPackageState expected)
    {
        var lease = AcquireLease();
        if (HasProcessGrant(expected)) return lease;
        lease.Dispose(); throw new UnauthorizedAccessException("extension_activation_revoked");
    }
    internal IDisposable AcquireConfigurationLease(ExtensionPackageState expected)
    {
        var lease = AcquireLease();
        try
        {
            var current = Inspect(expected.Id);
            if (!current.Enabled || current.Diagnostic is not null || current.ManifestSha256 != expected.ManifestSha256
                || current.PackageSha256 != expected.PackageSha256 || !current.GrantedCapabilities.Contains("configuration.write", StringComparer.Ordinal))
                throw new UnauthorizedAccessException("extension_capability_denied");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
    private ExtensionPackageState Inspect(DiscoveredExtension extension)
    {
        var id = extension.Manifest?.Id ?? Path.GetFileName(extension.Directory); var saved = Read(id);
        if (saved is not null && (saved.ManifestSha256 != extension.ManifestSha256 || saved.PackageSha256 != extension.PackageSha256))
            return saved with { State = "failed", Enabled = false, TrustedProcess = false, GrantedCapabilities = [], Diagnostic = "extension_package_changed" };
        if (saved is not null && extension.Manifest?.Dependencies?.Any(dependency => !DependencyReady(dependency)) == true)
            return saved with { State = "inactive", Enabled = false, Diagnostic = "extension_dependency_inactive" };
        if (saved is { Enabled: true } && !saved.GrantedCapabilities.All(CurrentConsent(saved).Contains))
            return saved with { State = "inactive", Enabled = false, Diagnostic = "extension_capabilities_revoked", GrantedCapabilities = [] };
        return saved ?? new(id, extension.Manifest?.Version ?? "", extension.Status.ToString(), extension.ManifestSha256,
            extension.PackageSha256, extension.Directory, true, false,
            new ExtensionConsentStore(paths, ScopeLevel.User).ConsentedCapabilities(extension).ToArray(),
            RuntimeKind: extension.Manifest?.Runtime?.Kind, RequestedCapabilities: extension.Manifest?.Requests?.Capabilities,
            ConfigurationKeys: extension.Manifest?.Configuration);
    }
    public ExtensionPackageState Change(string id, string action, bool trustProcess = false,
        string? expectedManifest = null, string? expectedPackage = null)
    {
        using var lease = AcquireLease(); var extension = Find(id); var state = Inspect(extension);
        if (expectedManifest is not null && (state.ManifestSha256 != expectedManifest || state.PackageSha256 != expectedPackage))
            throw new InvalidOperationException("extension_consent_identity_changed");
        if (action == "disable")
        {
            new ExtensionConsentStore(paths, ScopeLevel.User).Revoke(id);
            state = state with { State = "disabled", Enabled = false, TrustedProcess = false, GrantedCapabilities = [] };
        }
        else if (action == "enable")
        {
            if (extension.Manifest is not { } manifest || extension.Status == ExtensionDiscoveryStatus.IncompatibleApi
                || !SupportsPlatform(manifest) || state.Diagnostic is not (null or "extension_capabilities_revoked")) throw new InvalidOperationException("extension_incompatible_or_changed");
            if (manifest.ExecutionMode == "restricted" && manifest.Runtime is not null)
                throw new InvalidOperationException("extension_restricted_host_unavailable_no_downgrade");
            if (manifest.ManifestVersion == 2 && manifest.Runtime is not null && !trustProcess)
                throw new InvalidOperationException("extension_trusted_process_requires_explicit_user_consent");
            var granted = (manifest.Requests?.Capabilities ?? []).Order(StringComparer.Ordinal).ToArray();
            new ExtensionConsentStore(paths, ScopeLevel.User).Grant(extension, new HashSet<string>(granted, StringComparer.Ordinal));
            state = state with { Enabled = true, TrustedProcess = trustProcess, GrantedCapabilities = granted, State = "inactive", Diagnostic = null };
        }
        else throw new ArgumentException("extension_action_invalid", nameof(action));
        Save(state); return state;
    }
    public void Uninstall(string id, string? expectedManifest = null, string? expectedPackage = null)
    {
        using var lease = AcquireLease(); var extension = Find(id);
        var state = Inspect(extension);
        if (expectedManifest is not null && (state.ManifestSha256 != expectedManifest || state.PackageSha256 != expectedPackage))
            throw new InvalidOperationException("extension_consent_identity_changed");
        if (extension.Directory != Path.Combine(Root, id)) throw new InvalidOperationException("extension_not_managed_use_legacy_consent");
        if (Discover().Any(item => item.Manifest?.Dependencies?.Any(dep => dep.StartsWith(id + "@", StringComparison.Ordinal)) == true))
            throw new InvalidOperationException("extension_has_dependents");
        new ExtensionConsentStore(paths, ScopeLevel.User).Revoke(id);
        var archive = Path.Combine(paths.DataDirectory, "extension-archive", id + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!); RejectLinks(extension.Directory);
        Directory.Move(extension.Directory, archive); // recoverable; config/user data are not removed.
    }
    public bool Enabled(DiscoveredExtension extension)
    {
        if (extension.Manifest?.ManifestVersion != 2 && !File.Exists(Path.Combine(StateRoot, (extension.Manifest?.Id ?? "") + ".json"))) return true;
        var state = Inspect(extension);
        return state.Enabled && (extension.Manifest?.ManifestVersion != 2 || extension.Manifest.Runtime is null || state.TrustedProcess);
    }
    public DiscoveredExtension Find(string id)
    {
        ValidateId(id); var candidates = Discover().Where(item => item.Manifest?.Id == id).ToArray();
        return candidates.Length == 1 ? candidates[0] : throw new InvalidOperationException("extension_missing_or_ambiguous");
    }
    private bool DependencyReady(string dependency)
    {
        var pair = dependency.Split('@', 2);
        if (pair.Length != 2) return false;
        var extension = Discover().SingleOrDefault(item => item.Manifest?.Id == pair[0] && item.Manifest.Version == pair[1]);
        var state = Read(pair[0]);
        return extension is not null && state?.Enabled == true && state.ManifestSha256 == extension.ManifestSha256
            && state.PackageSha256 == extension.PackageSha256;
    }
    private ExtensionPackageState? Read(string id)
    {
        if (!ValidId(id)) return null;
        var file = Path.Combine(StateRoot, id + ".json"); if (!File.Exists(file)) return null;
        RejectLinks(file);
        return JsonSerializer.Deserialize(File.ReadAllText(file), ExtensionJson.Default.ExtensionPackageState)
            ?? throw new InvalidDataException("extension_state_invalid");
    }
    private void Save(ExtensionPackageState state)
    {
        ValidateId(state.Id); Directory.CreateDirectory(StateRoot); RejectLinks(StateRoot);
        var file = Path.Combine(StateRoot, state.Id + ".json"); var temporary = file + "." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, ExtensionJson.Default.ExtensionPackageState));
        File.Move(temporary, file, true);
    }
    private FileStream AcquireLease()
    {
        Directory.CreateDirectory(StateRoot); RejectLinks(StateRoot);
        return new FileStream(Path.Combine(StateRoot, "lifecycle.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    internal static void ValidateId(string id)
    {
        if (!ValidId(id)) throw new ArgumentException("extension_id_invalid");
    }
    private static bool ValidId(string id) => id.Length is >= 3 and <= 120 && (id.Contains('.') || id.Contains('-'))
        && !id.Contains("..", StringComparison.Ordinal) && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');
    internal static string PackagePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative.Contains('\\') || relative.StartsWith('/')
            || relative.Split('/').Any(part => part is "." or ".." || part.Any(char.IsControl)
                || part.EndsWith('.') || part != part.Trim() || part.IndexOfAny(['<', '>', '|', '?', '*', '"']) >= 0
                || System.Text.RegularExpressions.Regex.IsMatch(part.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
            throw new InvalidDataException("extension_path_invalid");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidDataException("extension_path_escape");
        return path;
    }
    internal static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("extension_link_rejected");
    }
    private static bool SupportsPlatform(ExtensionManifest manifest) => manifest.Platforms is not { Count: > 0 }
        || manifest.Platforms.Contains(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "osx", StringComparer.Ordinal);
    private static void ValidatePackageFiles(string root, ExtensionManifest manifest)
    {
        if (Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count(path => string.Equals(
            Path.GetFileName(path), ExtensionManifestLoader.ManifestFileName, StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidDataException("extension_nested_manifests_not_supported");
        var files = (manifest.Provides?.Tools?.Select(tool => tool.Schema!) ?? [])
            .Concat(manifest.Runtime is null ? [] : new[] { manifest.Runtime.Command! });
        foreach (var relative in files)
            if (!File.Exists(PackagePath(root, relative))) throw new InvalidDataException("extension_entry_missing:" + relative);
        foreach (var tool in manifest.Provides?.Tools ?? [])
        {
            var schemaPath = PackagePath(root, tool.Schema!);
            if (new FileInfo(schemaPath).Length > 131072) throw new InvalidDataException("extension_schema_limit");
            using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
            if (schema.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("extension_schema_invalid");
        }
        foreach (var relative in manifest.Provides?.Skills ?? [])
            if (!Directory.Exists(PackagePath(root, relative))) throw new InvalidDataException("extension_skill_missing:" + relative);
    }
}

internal sealed record ExtensionUpdateIntent(string Id, string ArchiveName, string StageName, ExtensionPackageState Previous);
[System.Text.Json.Serialization.JsonSerializable(typeof(ExtensionUpdateIntent))]
internal partial class ExtensionLifecycleJson : System.Text.Json.Serialization.JsonSerializerContext;
