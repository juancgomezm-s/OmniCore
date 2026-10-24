namespace OmniCore.Host;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Extensions;
using OmniCore.Protocol;
using OmniCore.Sandbox;

/// <summary>One-shot JSON-RPC/stdio adapter; every child starts Strong and receives only its package read access.</summary>
public sealed class ExtensionRpcProcessInvoker : IStagedExtensionRpcInvoker
{
    private const int MaximumRequestCharacters = 262_144;
    private readonly ISandboxProcessLauncher _launcher;

    public ExtensionRpcProcessInvoker(ISandboxProcessLauncher? launcher = null) =>
        _launcher = launcher ?? OmniHost.CreateProcessSandboxLauncher(OmniCore.Execution.SystemProcessRuntime.Instance());

    public async Task<StagedExtensionRpcResult> InvokeStagedAsync(DiscoveredExtension extension, string method,
        string parametersJson, ResourceClaims authorizedClaims, string workspaceRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizedClaims);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (authorizedClaims.Process is not { } process)
            throw new InvalidDataException("The authorized extension intent has no process claim.");
        using var workspace = ExtensionWriteWorkspace.Create(workspaceRoot,
            Path.Combine(Path.GetTempPath(), "OmniCore", "extension-workspaces"), authorizedClaims);
        var stagedRoot = Path.GetFullPath(workspace.Root);
        var stagedClaims = new ResourceClaims(authorizedClaims.Reads, authorizedClaims.Writes,
            authorizedClaims.Network, process with { WorkingDirectory = stagedRoot }, authorizedClaims.Secrets,
            authorizedClaims.WorktreeDisposition);
        var response = await InvokeCoreAsync(extension, method, parametersJson, stagedClaims, stagedRoot,
                cancellationToken)
            .ConfigureAwait(false);
        return new StagedExtensionRpcResult(response, workspace.CaptureChanges());
    }

    public Task<string> InvokeAsync(DiscoveredExtension extension, string method, string parametersJson,
        ResourceClaims authorizedClaims, CancellationToken cancellationToken) =>
        InvokeCoreAsync(extension, method, parametersJson, authorizedClaims, null, cancellationToken);

    private async Task<string> InvokeCoreAsync(DiscoveredExtension extension, string method, string parametersJson,
        ResourceClaims authorizedClaims, string? isolatedWorkingDirectory, CancellationToken cancellationToken)
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
        var grantedPaths = ResolveAuthorizedPaths(workspaceRoot, authorizedClaims);
        // The extension writer gets an entire disposable private copy. This permits creation of
        // files that do not exist yet; the Host scans every output (including ignored files) and
        // rejects paths beyond the extension's declared write patterns before making a proposal.
        var allowedPaths = packagePaths.Concat(grantedPaths)
            .Concat(isolatedWorkingDirectory is null ? [] :
                [new SandboxAllowedPath(workspaceRoot, SandboxPathAccess.Read | SandboxPathAccess.Write)])
            .DistinctBy(path => Path.GetFullPath(path.Path), OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        var launch = new SandboxLaunchSpec(SandboxStrength.Strong, executable, runtime.Args ?? [],
            isolatedWorkingDirectory ?? packageDirectory,
            new Dictionary<string, string>(StringComparer.Ordinal),
            allowedPaths,
            new SandboxNetworkPolicy(SandboxNetworkMode.Deny, []), TimeSpan.FromSeconds(30), request);
        await using var process = await _launcher.StartAsync(launch, cancellationToken).ConfigureAwait(false);
        var output = await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (output.TimedOut) throw new TimeoutException("The extension process exceeded 30 seconds.");
        var stdout = string.Join("\n", output.Chunks.Where(chunk => chunk.Stream == SandboxOutputStream.StandardOutput)
            .Select(chunk => chunk.Text));
        var responseLine = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (output.ExitCode != 0 || responseLine is null)
            throw new InvalidDataException("The extension process did not return a successful JSON-RPC response (exit "
                + output.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ").");
        using var response = JsonDocument.Parse(responseLine);
        var root = response.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out var version)
            || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0"
            || !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
            || id.GetString() != "1" || (!root.TryGetProperty("result", out _) && !root.TryGetProperty("error", out _)))
            throw new InvalidDataException("The extension returned a mismatched JSON-RPC response.");
        if (root.TryGetProperty("error", out var error))
            throw new InvalidDataException("The extension returned a JSON-RPC error.");
        return responseLine;
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
                if (matches.Length > 128 || matches.Length == 0 && access == SandboxPathAccess.Read)
                    throw new InvalidDataException("The authorized extension path resolved to no readable files or exceeded the path limit.");
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
