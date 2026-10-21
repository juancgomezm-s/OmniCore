namespace OmniCore.Host;

using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Protocol;
using OmniCore.Sandbox;

/// <summary>One-shot JSON-RPC/stdio adapter; every child starts Strong and receives only its package read access.</summary>
public sealed class ExtensionRpcProcessInvoker : IExtensionRpcInvoker
{
    private const int MaximumRequestCharacters = 262_144;
    private const int MaximumProposalFiles = 64;
    private const long MaximumProposalBytes = 8 * 1024 * 1024;
    private readonly ISandboxProcessLauncher _launcher;
    private readonly IArtifactStore? _artifacts;

    public ExtensionRpcProcessInvoker(ISandboxProcessLauncher? launcher = null, IArtifactStore? artifacts = null)
    {
        _launcher = launcher ?? OmniHost.CreateProcessSandboxLauncher(OmniCore.Execution.SystemProcessRuntime.Instance());
        _artifacts = artifacts;
    }

    public async Task<string> InvokeAsync(DiscoveredExtension extension, string method, string parametersJson,
        ResourceClaims authorizedClaims, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(authorizedClaims);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (extension.Status != ExtensionDiscoveryStatus.Ready || extension.Manifest?.Runtime is not { } runtime)
            throw new InvalidOperationException("The extension is not eligible to run.");
        using var parameters = JsonDocument.Parse(parametersJson);
        if (parameters.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("JSON-RPC parameters must be an object.");

        var packageDirectory = Path.GetFullPath(extension.Directory);
        if (HasReparsePointToVolumeRoot(packageDirectory))
            throw new InvalidDataException("The extension package scope contains a reparse point.");
        var manifestFile = Path.Combine(packageDirectory, ExtensionManifestLoader.ManifestFileName);
        if (!File.Exists(manifestFile)
            || !string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(manifestFile))).ToLowerInvariant(),
                extension.ManifestSha256, StringComparison.Ordinal)
            || !string.Equals(ExtensionManifestLoader.ComputePackageSha256(packageDirectory), extension.PackageSha256,
                StringComparison.Ordinal))
            throw new InvalidDataException("The extension package changed after discovery or consent.");
        var executable = Path.GetFullPath(Path.IsPathRooted(runtime.Command!)
            ? runtime.Command! : Path.Combine(packageDirectory, runtime.Command!));
        var relative = Path.GetRelativePath(packageDirectory, executable);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !File.Exists(executable) || HasReparsePoint(packageDirectory, executable))
            throw new InvalidDataException("The extension runtime executable must exist inside its package directory.");

        var request = "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":"
            + "\"" + JsonObj.Escape(method) + "\",\"params\":" + parameters.RootElement.GetRawText() + "}";
        if (request.Length > MaximumRequestCharacters)
            throw new InvalidDataException("The extension request exceeds the size limit.");

        if (authorizedClaims.Network.Count > 0)
            throw new NotSupportedException("Strong isolation cannot enforce the extension's network allow-list; no process was started.");
        var processClaim = authorizedClaims.Process
            ?? throw new InvalidDataException("The authorized extension intent has no process claim.");
        if (!Path.GetFullPath(processClaim.Executable).Equals(executable,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !processClaim.Args.SequenceEqual(runtime.Args ?? [], StringComparer.Ordinal))
            throw new InvalidDataException("The authorized process claim no longer matches the extension runtime.");
        var runtimeDirectory = Path.GetDirectoryName(executable)!;
        var packagePaths = string.Equals(runtimeDirectory, packageDirectory,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? Array.Empty<SandboxAllowedPath>()
            : new[] { new SandboxAllowedPath(packageDirectory, SandboxPathAccess.Read) };
        var workspaceRoot = Path.GetFullPath(authorizedClaims.Process?.WorkingDirectory
            ?? throw new InvalidDataException("The authorized extension intent has no workspace scope."));
        var boundary = new ExtensionBoundary(extension.Manifest.Requests?.Permissions ?? new ExtensionPermissionManifest(),
            extension.Manifest.Id!, workspaceRoot,
            new ComponentSource(SourceKind.Extension,
                extension.Trust == TrustLevel.Project ? ScopeLevel.Project : ScopeLevel.User, extension.Trust,
                extension.Manifest.Id!, extension.Manifest.Version!));
        if (!boundary.AllowsClaims(authorizedClaims))
            throw new InvalidDataException("The authorized extension claims exceed the manifest or workspace scope.");
        // Extension write claims describe what a later explicit filesystem tool may propose;
        // they never grant the child write access to the real workspace. Stage only declared
        // read/write inputs, without Git metadata, ignored files, or secrets.
        using var stage = new ExtensionStage(workspaceRoot, extension.Manifest.Requests?.Permissions,
            _artifacts, extension);
        var launch = new SandboxLaunchSpec(SandboxStrength.Strong, executable, runtime.Args ?? [], stage.Root,
            new Dictionary<string, string>(StringComparer.Ordinal),
            packagePaths.Concat([new SandboxAllowedPath(stage.Root, SandboxPathAccess.Read | SandboxPathAccess.Write)]).ToArray(),
            new SandboxNetworkPolicy(SandboxNetworkMode.Deny, []), TimeSpan.FromSeconds(30), request);
        await using var process = await _launcher.StartAsync(launch, cancellationToken).ConfigureAwait(false);
        var output = await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (output.TimedOut) throw new TimeoutException("The extension process exceeded 30 seconds.");
        var stdout = string.Join("\n", output.Chunks.Where(chunk => chunk.Stream == SandboxOutputStream.StandardOutput)
            .Select(chunk => chunk.Text));
        var responseLine = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (output.ExitCode != 0 || responseLine is null)
            throw new InvalidDataException("The extension process did not return a successful JSON-RPC response.");
        using var response = JsonDocument.Parse(responseLine);
        var root = response.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out var version)
            || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0"
            || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
            || id.GetString() != "1" || (!root.TryGetProperty("result", out _) && !root.TryGetProperty("error", out _)))
            throw new InvalidDataException("The extension returned a mismatched JSON-RPC response.");
        if (root.TryGetProperty("error", out var error))
            throw new InvalidDataException("The extension returned a JSON-RPC error.");
        var proposal = stage.Capture(cancellationToken);
        if (proposal is null) return responseLine;
        using var outputJson = new MemoryStream();
        using (var writer = new Utf8JsonWriter(outputJson))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("id", "1");
            writer.WritePropertyName("result");
            using (var rpc = JsonDocument.Parse(responseLine))
                rpc.RootElement.GetProperty("result").WriteTo(writer);
            writer.WritePropertyName("proposal");
            JsonSerializer.Serialize(writer, proposal);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(outputJson.ToArray());
    }

    private sealed class ExtensionStage : IDisposable
    {
        private readonly string _workspaceRoot;
        private readonly ExtensionPermissionManifest? _permissions;
        private readonly IArtifactStore? _artifacts;
        private readonly DiscoveredExtension _extension;
        private readonly Dictionary<string, byte[]> _before = new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private readonly RedactionPolicy _redaction = new();
        private const long MaximumInputBytes = 8 * 1024 * 1024;
        public string Root { get; }

        public ExtensionStage(string workspaceRoot, ExtensionPermissionManifest? permissions,
            IArtifactStore? artifacts, DiscoveredExtension extension)
        {
            _workspaceRoot = Path.GetFullPath(workspaceRoot);
            _permissions = permissions;
            _artifacts = artifacts;
            _extension = extension;
            Root = Path.Combine(Path.GetTempPath(), "omnicore-extension-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            try
            {
                CopyInputs();
                InitializePrivateGit();
            }
            catch
            {
                try { Directory.Delete(Root, recursive: true); } catch { }
                throw;
            }
        }

        private void CopyInputs()
        {
            var patterns = (_permissions?.Paths?.Read ?? []).Concat(_permissions?.Paths?.Write ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var files = EnumerateFiles(_workspaceRoot).ToArray();
            long total = 0;
            foreach (var source in files)
            {
                var relative = Path.GetRelativePath(_workspaceRoot, source).Replace('\\', '/');
                if (relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)
                    || _redaction.IsSecretPath(relative) || IsIgnored(relative)
                    || !patterns.Any(pattern => IsMatch(pattern, relative))) continue;
                var bytes = File.ReadAllBytes(source);
                if (IsBinary(bytes)) throw new InvalidDataException("Binary extension inputs are not supported by the review pipeline.");
                var text = new UTF8Encoding(false, true).GetString(bytes);
                if (!string.Equals(_redaction.Redact(text), text, StringComparison.Ordinal))
                    throw new InvalidDataException("Extension input contains data that requires secret redaction.");
                total = checked(total + bytes.LongLength);
                if (total > MaximumInputBytes || _before.Count >= MaximumProposalFiles)
                    throw new InvalidDataException("Extension input snapshot exceeds its safe limit.");
                var target = Resolve(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
                _before.Add(relative, bytes);
            }
        }

        private void InitializePrivateGit()
        {
            // A fresh repository lets a writer extension use local Git without exposing the
            // original .git directory, alternates, worktree registration, or common directory.
            using var git = new System.Diagnostics.Process();
            git.StartInfo = new System.Diagnostics.ProcessStartInfo("git", ["init", "--quiet", Root])
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            if (!git.Start()) throw new InvalidOperationException("Could not create a private extension Git copy.");
            var error = git.StandardError.ReadToEnd();
            git.WaitForExit(TimeSpan.FromSeconds(10));
            if (!git.HasExited || git.ExitCode != 0)
            {
                try { git.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException("Could not initialize the private extension Git copy: " + error);
            }
        }

        public object? Capture(CancellationToken cancellationToken)
        {
            var changes = new List<object>();
            var changedPaths = new List<string>();
            long bytesTotal = 0;
            var afterPaths = EnumerateFiles(Root).Where(path => !Path.GetRelativePath(Root, path)
                .Replace('\\', '/').StartsWith(".git/", StringComparison.OrdinalIgnoreCase)).ToArray();
            var all = new HashSet<string>(_before.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var path in afterPaths)
                all.Add(Path.GetRelativePath(Root, path).Replace('\\', '/'));
            foreach (var relative in all.Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var staged = Resolve(relative);
                byte[]? post = File.Exists(staged) ? File.ReadAllBytes(staged) : null;
                var postHash = post is null ? null : Convert.ToHexString(SHA256.HashData(post)).ToLowerInvariant();
                var beforeBytes = _before.GetValueOrDefault(relative);
                var beforeHash = beforeBytes is null ? null : Convert.ToHexString(SHA256.HashData(beforeBytes)).ToLowerInvariant();
                if (beforeHash == postHash) continue;
                if (_redaction.IsSecretPath(relative) || !(_permissions?.Paths?.Write ?? [])
                    .Any(pattern => IsMatch(pattern, relative)))
                    throw new InvalidDataException("The extension changed a path outside its declared write scope.");
                byte[]? pre = beforeBytes;
                bytesTotal = checked(bytesTotal + (pre?.LongLength ?? 0) + (post?.LongLength ?? 0));
                if (changes.Count >= MaximumProposalFiles || bytesTotal > MaximumProposalBytes)
                    throw new InvalidDataException("Extension proposal exceeds its safe limit.");
                if ((pre is not null && IsBinary(pre)) || (post is not null && IsBinary(post)))
                    throw new InvalidDataException("Binary extension mutations are not supported by the review pipeline.");
                if ((pre is not null && _redaction.Redact(new UTF8Encoding(false, true).GetString(pre))
                        != new UTF8Encoding(false, true).GetString(pre))
                    || (post is not null && _redaction.Redact(new UTF8Encoding(false, true).GetString(post))
                        != new UTF8Encoding(false, true).GetString(post)))
                    throw new InvalidDataException("Extension proposal contains data that requires secret redaction.");
                changes.Add(new { path = relative, preimage = pre is null ? null : Convert.ToBase64String(pre),
                    preSha256 = pre is null ? null : Convert.ToHexString(SHA256.HashData(pre)).ToLowerInvariant(),
                    postimage = post is null ? null : Convert.ToBase64String(post), postSha256 = postHash });
                changedPaths.Add(relative);
            }
            if (changes.Count == 0) return null;
            if (_artifacts is null)
                throw new InvalidOperationException("Protected artifact storage is required for extension proposals.");
            var body = JsonSerializer.Serialize(new { version = 1, extensionId = _extension.Manifest?.Id,
                packageSha256 = _extension.PackageSha256, changes });
            if (Encoding.UTF8.GetByteCount(body) > MaximumProposalBytes * 2)
                throw new InvalidDataException("Extension proposal manifest exceeds its safe limit.");
            // The proposal is readable only through artifact.read after the tool result has
            // attached its hash to the session. Files that trigger secret redaction are rejected.
            var reference = _artifacts.PutText(body, "application/json", ArtifactKind.Patch, Sensitivity.Sensitive);
            if (reference.Redacted) throw new InvalidDataException("Extension proposal was redacted and cannot be applied faithfully.");
            return new { reference, summary = changes.Count + " proposed file change(s); workspace remains unchanged.", paths = changedPaths };
        }

        private string Resolve(string relative)
        {
            var path = Path.GetFullPath(relative.Replace('/', Path.DirectorySeparatorChar), Root);
            var rel = Path.GetRelativePath(Root, path);
            if (Path.IsPathRooted(rel) || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Extension path escaped its private copy.");
            return path;
        }

        private static IEnumerable<string> EnumerateFiles(string root)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Extension staging contains a symbolic link or reparse point.");
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!string.Equals(Path.GetFileName(entry), ".git", StringComparison.OrdinalIgnoreCase))
                            pending.Push(entry);
                    }
                    else yield return entry;
                }
            }
        }

        private static bool IsMatch(string pattern, string path) => ExtensionRpcProcessInvoker.IsMatch(
            pattern.Replace('\\', '/'), path);

        private bool IsIgnored(string relative)
        {
            if (!File.Exists(Path.Combine(_workspaceRoot, ".git"))
                && !Directory.Exists(Path.Combine(_workspaceRoot, ".git"))) return false;
            using var git = new System.Diagnostics.Process();
            git.StartInfo = new System.Diagnostics.ProcessStartInfo("git", ["check-ignore", "--no-index", "-q", "--", relative])
            { WorkingDirectory = _workspaceRoot, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true };
            try
            {
                if (!git.Start()) return true;
                git.WaitForExit(TimeSpan.FromSeconds(5));
                if (!git.HasExited) { git.Kill(entireProcessTree: true); return true; }
                return git.ExitCode == 0;
            }
            catch { return true; }
        }

        private static bool IsBinary(byte[] bytes) => bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static IReadOnlyList<SandboxAllowedPath> ResolveAuthorizedPaths(string workspaceRoot, ResourceClaims claims)
    {
        var root = Path.GetFullPath(workspaceRoot);
        if (HasReparsePointToVolumeRoot(root))
            throw new InvalidDataException("The workspace scope is a reparse point.");
        var files = EnumerateRegularFiles(root).ToArray();
        var result = new Dictionary<string, SandboxPathAccess>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        Add(claims.Reads, SandboxPathAccess.Read);
        Add(claims.Writes, SandboxPathAccess.Write);
        return result.Select(pair => new SandboxAllowedPath(pair.Key, pair.Value)).ToArray();

        void Add(IReadOnlyList<string> patterns, SandboxPathAccess access)
        {
            foreach (var pattern in patterns)
            {
                if (Path.IsPathRooted(pattern) || pattern.Replace('\\', '/').Split('/').Any(part => part is ".." or "."))
                    throw new InvalidDataException("The authorized extension path escaped its workspace scope.");
                var matches = files.Where(file => IsMatch(pattern.Replace('\\', '/'),
                    Path.GetRelativePath(root, file).Replace('\\', '/'))).ToArray();
                if (matches.Length == 0 || matches.Length > 128)
                    throw new InvalidDataException("The authorized extension path resolved to no files or exceeded the path limit.");
                foreach (var file in matches) result[file] = result.TryGetValue(file, out var current) ? current | access : access;
            }
        }
    }

    private static bool HasReparsePoint(string root, string file)
    {
        var canonicalRoot = Path.GetFullPath(root);
        var current = Path.GetFullPath(file);
        while (!string.Equals(current, canonicalRoot,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            current = Path.GetDirectoryName(current) ?? throw new InvalidDataException("The package path is invalid.");
        }
        return (File.GetAttributes(canonicalRoot) & FileAttributes.ReparsePoint) != 0;
    }

    private static bool HasReparsePointToVolumeRoot(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) return false;
            current = parent;
        }
    }

    private static IEnumerable<string> EnumerateRegularFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory).ToArray(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else yield return entry;
            }
        }
    }

    private static bool IsMatch(string pattern, string relativePath)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*\\*", ".*").Replace("\\*", "[^/]*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(relativePath, regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}
