namespace OmniCore.Host;

using System.Diagnostics;
using System.Globalization;
using OmniCore.Protocol;

public interface IReleaseUpdateDownloader
{
    Task<ReleaseUpdateDownloadResult> DownloadAsync(ReleaseUpdateAsset asset, string tag,
        string destinationDirectory, string expectedVersion, CancellationToken cancellationToken = default);
}

public interface IReleaseInstallationInspector
{
    ReleaseInstallation Inspect();
}

public interface IReleaseUpdateHelperLauncher
{
    int Launch(ReleaseUpdateHelperRequest request);
}

public sealed record ReleaseUpdateHelperRequest(string HelperPath, string Mode, int ParentPid,
    string StatePath, string? PackagePath, string? BackupPath, string NpmPath, string NodePath,
    string PackageName, string WorkingDirectory, TimeSpan Timeout, string OperationId = "",
    string? ExpectedSha256 = null, long? ExpectedSize = null, string NpmPrefix = "");

/// <summary>Starts the detached Node helper that installs after this process releases its files.</summary>
public sealed class NodeReleaseUpdateHelperLauncher : IReleaseUpdateHelperLauncher
{
    public int Launch(ReleaseUpdateHelperRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var start = new ProcessStartInfo
        {
            FileName = request.NodePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory,
        };
        start.ArgumentList.Add(request.HelperPath);
        start.ArgumentList.Add("--mode");
        start.ArgumentList.Add(request.Mode);
        start.ArgumentList.Add("--parent-pid");
        start.ArgumentList.Add(request.ParentPid.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--state");
        start.ArgumentList.Add(request.StatePath);
        if (request.PackagePath is not null)
        {
            start.ArgumentList.Add("--package-path");
            start.ArgumentList.Add(request.PackagePath);
        }
        if (request.BackupPath is not null)
        {
            start.ArgumentList.Add("--backup-path");
            start.ArgumentList.Add(request.BackupPath);
        }
        start.ArgumentList.Add("--npm");
        start.ArgumentList.Add(request.NpmPath);
        start.ArgumentList.Add("--node");
        start.ArgumentList.Add(request.NodePath);
        start.ArgumentList.Add("--package");
        start.ArgumentList.Add(request.PackageName);
        start.ArgumentList.Add("--timeout");
        start.ArgumentList.Add(((int)request.Timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--operation-id");
        start.ArgumentList.Add(request.OperationId);
        start.ArgumentList.Add("--npm-prefix");
        start.ArgumentList.Add(request.NpmPrefix);
        if (request.ExpectedSha256 is not null)
        {
            start.ArgumentList.Add("--sha256");
            start.ArgumentList.Add(request.ExpectedSha256);
        }
        if (request.ExpectedSize is { } size)
        {
            start.ArgumentList.Add("--size");
            start.ArgumentList.Add(size.ToString(CultureInfo.InvariantCulture));
        }
        using var process = Process.Start(start);
        return process?.Id ?? 0;
    }
}
