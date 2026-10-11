namespace OmniCore.Host;

using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Extensions;
using OmniCore.Protocol;
using OmniCore.Sandbox;

/// <summary>One-shot JSON-RPC/stdio adapter; every child starts Strong and receives only its package read access.</summary>
public sealed class ExtensionRpcProcessInvoker : IExtensionRpcInvoker
{
    private const int MaximumRequestCharacters = 262_144;
    private readonly ISandboxProcessLauncher _launcher;

    public ExtensionRpcProcessInvoker(ISandboxProcessLauncher? launcher = null) =>
        _launcher = launcher ?? OmniHost.CreateProcessSandboxLauncher(OmniCore.Execution.SystemProcessRuntime.Instance());

    public async Task<string> InvokeAsync(DiscoveredExtension extension, string method, string parametersJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (extension.Status != ExtensionDiscoveryStatus.Ready || extension.Manifest?.Runtime is not { } runtime)
            throw new InvalidOperationException("The extension is not eligible to run.");
        using var parameters = JsonDocument.Parse(parametersJson);
        if (parameters.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("JSON-RPC parameters must be an object.");

        var packageDirectory = Path.GetFullPath(extension.Directory);
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
            || !File.Exists(executable) || (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The extension runtime executable must exist inside its package directory.");

        var request = "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":"
            + "\"" + JsonObj.Escape(method) + "\",\"params\":" + parameters.RootElement.GetRawText() + "}";
        if (request.Length > MaximumRequestCharacters)
            throw new InvalidDataException("The extension request exceeds the size limit.");

        var runtimeDirectory = Path.GetDirectoryName(executable)!;
        var packagePaths = string.Equals(runtimeDirectory, packageDirectory,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? Array.Empty<SandboxAllowedPath>()
            : new[] { new SandboxAllowedPath(packageDirectory, SandboxPathAccess.Read) };
        var launch = new SandboxLaunchSpec(SandboxStrength.Strong, executable, runtime.Args ?? [], packageDirectory,
            new Dictionary<string, string>(StringComparer.Ordinal),
            packagePaths,
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
            || version.GetString() != "2.0" || !root.TryGetProperty("id", out var id)
            || id.GetString() != "1" || (!root.TryGetProperty("result", out _) && !root.TryGetProperty("error", out _)))
            throw new InvalidDataException("The extension returned a mismatched JSON-RPC response.");
        if (root.TryGetProperty("error", out var error))
            throw new InvalidOperationException("Extension JSON-RPC error: " + error.GetRawText());
        return responseLine;
    }
}
