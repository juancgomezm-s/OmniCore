namespace OmniCore.Host;

using System.Security.Cryptography;
using System.Text;
using OmniCore.Protocol;

/// <summary>Process leases for an installed package. A pending replacement is a launch barrier,
/// not permission to stop another client. No Session or credential is opened here.</summary>
public sealed class ReleaseUpdateInstances
{
    public const int RestartRequestedExitCode = 74;
    public const int LaunchBlockedExitCode = 75;
    private readonly ReleaseUpdateStateStore _updates;
    private readonly string _directory;
    private readonly string _gate;
    private readonly string _prefix;

    public ReleaseUpdateInstances(ReleaseUpdateStateStore updates, string npmPrefix)
    {
        _updates = updates;
        _prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(npmPrefix));
        var normalized = OperatingSystem.IsWindows() ? _prefix.ToUpperInvariant() : _prefix;
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        _directory = Path.Combine(_prefix, ".omnicore-updates", "instances", key);
        _gate = Path.Combine(_directory, "launch.lock");
    }

    public IDisposable AcquireGate()
    {
        Directory.CreateDirectory(_directory);
        RejectLinks(_directory);
        return new FileStream(_gate, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public IDisposable? TryAcquireGate()
    {
        try { return AcquireGate(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { return null; }
    }

    public bool HasOtherInstances(int? exceptPid = null)
    {
        var entries = Directory.Exists(_directory)
            ? Directory.EnumerateFiles(_directory, "*.lease").Take(257).ToArray() : [];
        if (entries.Length > 256) return true;
        foreach (var file in entries)
        {
            if (!File.Exists(file)) continue;
            try { if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return true; }
            catch (FileNotFoundException) { continue; }
            var pieces = Path.GetFileNameWithoutExtension(file).Split('-');
            if (pieces.Length != 2 || !int.TryParse(pieces[0], out var pid) || pid <= 0
                || !Guid.TryParseExact(pieces[1], "N", out _)) return true;
            if (pid == exceptPid) continue;
            try
            {
                // An exclusive open proves the owner no longer holds its kernel lease.
                using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                File.Delete(file);
            }
            catch (FileNotFoundException) { }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }
        return false;
    }

    // Shared by all data profiles using this physical npm prefix. The pointer survives
    // parent/helper exit; only a durable terminal result releases the launch barrier.
    public void PublishBarrier()
    {
        var path = Path.Combine(_directory, "operation.path");
        RejectLinks(_directory);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("update_instance_gate_unavailable");
        File.WriteAllText(path, _updates.StatePath);
    }

    public string? ReadBarrier()
    {
        var path = Path.Combine(_directory, "operation.path");
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 8192)
            return "update_operation_unknown";
        var statePath = File.ReadAllText(path);
        if (string.Equals(statePath, _updates.StatePath, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return null;
        if (!Path.IsPathFullyQualified(statePath) || Path.GetFileName(statePath) != Path.GetFileName(_updates.StatePath))
            return "update_operation_unknown";
        var store = new ReleaseUpdateStateStore(Path.GetDirectoryName(Path.GetDirectoryName(statePath))!);
        if (!string.Equals(store.StatePath, statePath, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !store.TryRead(out var state, out _) || state is null || !SamePrefix(state.NpmPrefix))
            return "update_operation_unknown";
        return state.Status is ReleaseUpdateOperationStatus.Pending or ReleaseUpdateOperationStatus.Running
            or ReleaseUpdateOperationStatus.Unknown ? "update_operation_in_progress" : null;
    }

    public IDisposable? TryEnter(out string? diagnostic)
    {
        diagnostic = null;
        try
        {
            using var gate = AcquireGate();
            if (ReadBarrier() is { } barrier) { diagnostic = barrier; return null; }
            if (!_updates.TryRead(out var update, out var error))
            { diagnostic = error; return null; }
            if (update is { Status: ReleaseUpdateOperationStatus.Pending or ReleaseUpdateOperationStatus.Running
                or ReleaseUpdateOperationStatus.Unknown } && SamePrefix(update.NpmPrefix))
            { diagnostic = "update_operation_in_progress"; return null; }
            var path = Path.Combine(_directory, Environment.ProcessId + "-" + Guid.NewGuid().ToString("N") + ".lease");
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            return new Lease(stream, path);
        }
        catch (UnauthorizedAccessException)
        { diagnostic = "update_instance_registry_read_only"; return null; }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        { diagnostic = "update_instance_gate_unavailable"; return null; }
    }

    public static IDisposable? EnterInstalledRuntime(string[] args, out string? diagnostic)
    {
        diagnostic = null;
        // Signed activation currently ships only for Windows x64.
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return null;
        // These commands do not start a Session and must remain usable for recovery.
        if (args.Length > 0 && args[0] is "update" or "--help" or "-h" or "help") return null;
        var installation = new ReleaseInstallationInspector().Inspect();
        if (installation.Kind != ReleaseInstallKind.NpmGlobal || installation.NpmPrefix is null) return null;
        var instances = new ReleaseUpdateInstances(ReleaseUpdateStateStore.CreateDefault(), installation.NpmPrefix);
        var lease = instances.TryEnter(out diagnostic);
        if (diagnostic == "update_instance_registry_read_only" && instances.CanRunReadOnly()) diagnostic = null;
        return lease;
    }

    internal bool CanRunReadOnly()
    {
        // Read-only installs remain usable. Windows holds the loaded CLI DLL open;
        // the existing helper's exclusive DLL preflight refuses replacement by an
        // elevated updater while this unregistered runtime is alive.
        try
        {
            return ReadBarrier() is null && _updates.TryRead(out var operation, out _)
                && operation is not { Status: ReleaseUpdateOperationStatus.Pending or ReleaseUpdateOperationStatus.Running
                    or ReleaseUpdateOperationStatus.Unknown };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { return false; }
    }

    private bool SamePrefix(string? prefix) => prefix is not null
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(prefix)), _prefix,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void RejectLinks(string directory)
    {
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Update instance path contains a link.");
    }

    private sealed class Lease(FileStream stream, string path) : IDisposable
    {
        public void Dispose()
        {
            stream.Dispose();
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
