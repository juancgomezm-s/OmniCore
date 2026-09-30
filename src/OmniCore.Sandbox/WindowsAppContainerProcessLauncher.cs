namespace OmniCore.Sandbox;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Runtime.Versioning;

/// <summary>
/// Starts Strong processes in a per-workspace Windows AppContainer and controls their process
/// tree with a kill-on-close Job Object. Weak and None are intentionally not handled here.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAppContainerProcessLauncher : ISandboxProcessLauncher
{
    private static readonly ConcurrentDictionary<string, Semaphore> ProfileLeases = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> QuarantinedProfiles = new(StringComparer.Ordinal);

    public ValueTask<ISandboxProcessControl> StartAsync(
        SandboxLaunchSpec launch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(launch.Executable) || launch.Arguments is null
            || launch.Environment is null || launch.AllowedPaths is null || launch.Network is null
            || launch.Network.AllowedHosts is null || !Enum.IsDefined(launch.Network.Mode)
            || launch.Arguments.Any(argument => argument is null))
            throw new ArgumentException("The sandbox launch specification is invalid.", nameof(launch));
        foreach (var variable in launch.Environment)
        {
            if (string.IsNullOrWhiteSpace(variable.Key) || variable.Key.Contains('=')
                || variable.Key.Any(char.IsControl) || variable.Value is null)
                throw new ArgumentException("The process environment contains an invalid variable.", nameof(launch));
            if (variable.Key.Equals("SystemRoot", StringComparison.OrdinalIgnoreCase)
                || variable.Key.Equals("SystemDrive", StringComparison.OrdinalIgnoreCase)
                || variable.Key.Equals("WINDIR", StringComparison.OrdinalIgnoreCase)
                || variable.Key.Equals("TEMP", StringComparison.OrdinalIgnoreCase)
                || variable.Key.Equals("TMP", StringComparison.OrdinalIgnoreCase)
                || variable.Key.Equals("LOCALAPPDATA", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The process environment cannot override protected AppContainer variables.", nameof(launch));
        }
        if (launch.RequestedStrength != SandboxStrength.Strong)
            throw new NotSupportedException("This launcher is only for Strong sandbox requests.");
        if (launch.Timeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(launch), "The process timeout must be positive.");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("AppContainer process isolation is only available on Windows.");
        if (launch.Network.Mode == SandboxNetworkMode.AllowList)
            throw new NotSupportedException("AppContainer cannot enforce a host allow-list; Strong networking is unavailable for this policy.");
        if (launch.Network.Mode is not (SandboxNetworkMode.Deny or SandboxNetworkMode.AllowAll))
            throw new ArgumentException("The network policy is invalid.", nameof(launch));

        var executable = ResolveExistingPhysicalPath(launch.Executable);
        var workingDirectory = ResolveExistingPhysicalPath(launch.WorkingDirectory ?? Path.GetDirectoryName(executable)!);
        if (!File.Exists(executable) || !Directory.Exists(workingDirectory))
            throw new FileNotFoundException("The executable or working directory does not exist.", executable);

        var workspacePath = launch.AllowedPaths
            .Where(path => (path.Access & SandboxPathAccess.Write) != 0 && Directory.Exists(path.Path))
            .Select(path => ResolveExistingPhysicalPath(path.Path))
            .OrderBy(path => path.Length)
            .FirstOrDefault() ?? workingDirectory;
        var profileName = GetProfileName(workspacePath);
        if (QuarantinedProfiles.TryGetValue(profileName, out var quarantineReason))
            throw new IOException("The AppContainer profile is quarantined after an ACL cleanup failure: " + quarantineReason);
        var profileLease = ProfileLeases.GetOrAdd(profileName,
            static name => new Semaphore(1, 1, "Local\\" + name));
        while (!profileLease.WaitOne(50))
            cancellationToken.ThrowIfCancellationRequested();

        AppContainerAclLease? aclLease = null;
        var tempDirectory = Path.Combine(Path.GetTempPath(), "OmniCore-AppContainer", Guid.NewGuid().ToString("N"));
        SafeFileHandle? stdoutRead = null;
        SafeFileHandle? stderrRead = null;
        SafeFileHandle? job = null;
        SafeProcessHandle? process = null;
        try
        {
            var sid = AppContainerNative.GetOrCreateProfileSid(profileName);
            aclLease = new AppContainerAclLease(sid);
            Directory.CreateDirectory(tempDirectory);
            aclLease.GrantDirectory(tempDirectory, FileSystemRights.Modify);
            aclLease.GrantDirectory(Path.GetDirectoryName(executable)!, FileSystemRights.ReadAndExecute);
            foreach (var allowed in launch.AllowedPaths)
            {
                if (allowed.Access == SandboxPathAccess.None
                    || (allowed.Access & ~(SandboxPathAccess.Read | SandboxPathAccess.Write)) != 0)
                    throw new ArgumentException("An allowed path has invalid access flags.", nameof(launch));
                var path = ResolveExistingPhysicalPath(allowed.Path);
                var canRead = (allowed.Access & SandboxPathAccess.Read) != 0;
                var canWrite = (allowed.Access & SandboxPathAccess.Write) != 0;
                var rights = canRead && canWrite
                    ? FileSystemRights.ReadAndExecute | FileSystemRights.Modify
                    : canRead ? FileSystemRights.ReadAndExecute
                    : Directory.Exists(path)
                        ? FileSystemRights.Traverse | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories
                            | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes
                        : FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes
                            | FileSystemRights.WriteExtendedAttributes;
                if (Directory.Exists(path))
                    aclLease.GrantDirectory(path, rights);
                else if (File.Exists(path))
                    aclLease.GrantFile(path, rights);
                else
                    throw new DirectoryNotFoundException($"An allowed sandbox path does not exist: {path}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var capabilities = launch.Network.Mode == SandboxNetworkMode.AllowAll
                ? AppContainerNative.CreateInternetClientCapability()
                : Array.Empty<AppContainerNative.SidAndAttributes>();
            using var securityCapabilities = AppContainerNative.CreateSecurityCapabilities(sid, capabilities);
            var commandLine = BuildCommandLine(executable, launch.Arguments);
            AppContainerNative.CreateSuspendedProcess(
                executable, commandLine, workingDirectory, launch.Environment, tempDirectory, securityCapabilities,
                out stdoutRead, out stderrRead, out job, out process);
            return ValueTask.FromResult<ISandboxProcessControl>(
                new AppContainerProcessControl(process!, job!, stdoutRead!, stderrRead!, aclLease!, tempDirectory,
                    launch.Timeout, profileLease, profileName,
                    reason => QuarantinedProfiles[profileName] = reason));
        }
        catch (Exception launchFailure)
        {
            process?.Dispose();
            job?.Dispose();
            stdoutRead?.Dispose();
            stderrRead?.Dispose();
            Exception? aclCleanupFailure = null;
            try { aclLease?.Dispose(); }
            catch (Exception exception) { aclCleanupFailure = exception; }
            TryDeleteDirectory(tempDirectory);
            if (aclCleanupFailure is null)
                profileLease.Release();
            else
            {
                QuarantinedProfiles[profileName] = aclCleanupFailure.Message;
                throw new AggregateException("AppContainer launch failed and ACL grants could not be revoked; profile quarantined.",
                    launchFailure, aclCleanupFailure);
            }
            throw;
        }
    }

    private static string ResolveExistingPhysicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new ArgumentException("Path must be rooted.", nameof(path));
        var current = root;
        var remainder = fullPath[root.Length..];
        var segments = remainder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            FileSystemInfo info = index == segments.Length - 1 && File.Exists(current)
                ? new FileInfo(current) : new DirectoryInfo(current);
            if (!info.Exists)
                throw new FileNotFoundException("A sandbox path does not exist.", current);
            if (info.LinkTarget is not null)
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException("Unable to resolve a sandbox reparse point: " + current);
        }
        return Path.GetFullPath(current);
    }

    /// <summary>Stable profile identity: SHA-256 of the normalized, case-folded workspace path.</summary>
    public static string GetProfileName(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var canonical = Path.GetFullPath(workspacePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        // Keep the profile name within the documented AppContainer profile-name limit.
        return "OmniCore-" + hash[..40];
    }

    private static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var result = new StringBuilder(QuoteArgument(executable));
        foreach (var argument in arguments)
        {
            result.Append(' ').Append(QuoteArgument(argument));
        }
        return result.ToString();
    }

    private static string QuoteArgument(string value)
    {
        if (value.Length != 0 && !value.Any(char.IsWhiteSpace) && !value.Contains('"'))
            return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }
            if (character == '"')
                result.Append('\\', slashes * 2 + 1).Append('"');
            else
                result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Reports whether the Windows AppContainer APIs are present on this OS.</summary>
public sealed class WindowsAppContainerCapabilitiesProbe : ISandboxCapabilitiesProbe
{
    public SandboxCapabilities Probe()
    {
        if (!OperatingSystem.IsWindows())
            return Unavailable("AppContainer is only available on Windows.");
        if (!OperatingSystem.IsWindowsVersionAtLeast(8, 1))
            return Unavailable("This Windows version does not provide AppContainer process APIs.");
        try
        {
            // Creating (or deriving an existing) profile, creating a low-box token and deriving
            // its environment tests the operations needed before the first process is resumed.
            // Profiles are retained for reuse; no path ACL is granted by the probe.
            var sid = AppContainerNative.GetOrCreateProfileSid("OmniCore-capability-probe");
            AppContainerNative.ProbeAppContainerLaunchSupport(sid);
            return new SandboxCapabilities(true, true, true, null);
        }
        catch (Exception exception) when (exception is Win32Exception or EntryPointNotFoundException or DllNotFoundException or PlatformNotSupportedException or System.Security.SecurityException)
        {
            return Unavailable("AppContainer profile creation is unavailable: " + exception.Message);
        }
    }

    private static SandboxCapabilities Unavailable(string reason) => new(false, true, true, reason);
}

[SupportedOSPlatform("windows")]
internal sealed class AppContainerAclLease : IDisposable
{
    private readonly SecurityIdentifier _sid;
    private readonly List<(string Path, bool IsDirectory, FileSystemAccessRule Rule)> _added = new();
    private bool _disposed;

    public AppContainerAclLease(SecurityIdentifier sid) => _sid = sid;

    public void GrantDirectory(string path, FileSystemRights rights) => Grant(path, true, rights);
    public void GrantFile(string path, FileSystemRights rights) => Grant(path, false, rights);

    private void Grant(string path, bool isDirectory, FileSystemRights rights)
    {
        if (rights == 0)
            return;
        var inheritance = isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        var rule = new FileSystemAccessRule(_sid, rights, inheritance, PropagationFlags.None, AccessControlType.Allow);
        if (isDirectory)
        {
            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl(AccessControlSections.Access);
            var existingRules = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Where(existing => existing.IdentityReference.Equals(_sid)
                    && existing.AccessControlType == AccessControlType.Allow).ToArray();
            var exists = existingRules.Any(existing => existing.FileSystemRights == rights
                && existing.InheritanceFlags == inheritance && existing.PropagationFlags == PropagationFlags.None);
            if (existingRules.Length > 0 && !exists)
                throw new System.Security.SecurityException("An unexpected AppContainer ACL grant already exists on " + path);
            if (!exists)
            {
                security.AddAccessRule(rule);
                info.SetAccessControl(security);
                _added.Add((path, true, rule));
            }
        }
        else
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl(AccessControlSections.Access);
            var existingRules = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Where(existing => existing.IdentityReference.Equals(_sid)
                    && existing.AccessControlType == AccessControlType.Allow).ToArray();
            var exists = existingRules.Any(existing => existing.FileSystemRights == rights
                && existing.InheritanceFlags == InheritanceFlags.None
                && existing.PropagationFlags == PropagationFlags.None);
            if (existingRules.Length > 0 && !exists)
                throw new System.Security.SecurityException("An unexpected AppContainer ACL grant already exists on " + path);
            if (!exists)
            {
                security.AddAccessRule(rule);
                info.SetAccessControl(security);
                _added.Add((path, false, rule));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Cleanup removes only the exact ACEs introduced by this lease, preserving unrelated ACL
        // changes made concurrently. The stable AppContainer profile is intentionally retained;
        // the per-launch temp directory is removed by the process control after ACL restoration.
        var failures = new List<Exception>();
        for (var index = _added.Count - 1; index >= 0; index--)
        {
            var entry = _added[index];
            try
            {
                if (entry.IsDirectory)
                {
                    var info = new DirectoryInfo(entry.Path);
                    var security = info.GetAccessControl(AccessControlSections.Access);
                    security.RemoveAccessRuleSpecific(entry.Rule);
                    info.SetAccessControl(security);
                }
                else
                {
                    var info = new FileInfo(entry.Path);
                    var security = info.GetAccessControl(AccessControlSections.Access);
                    security.RemoveAccessRuleSpecific(entry.Rule);
                    info.SetAccessControl(security);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                failures.Add(exception);
            }
        }
        if (failures.Count > 0)
            throw new IOException("Failed to revoke one or more AppContainer ACL grants.", new AggregateException(failures));
    }
}

[SupportedOSPlatform("windows")]
internal sealed class AppContainerProcessControl : ISandboxProcessControl
{
    private readonly SafeProcessHandle _process;
    private readonly SafeFileHandle _job;
    private readonly SafeFileHandle _stdout;
    private readonly SafeFileHandle _stderr;
    private readonly AppContainerAclLease _aclLease;
    private readonly string _tempDirectory;
    private readonly TimeSpan? _timeout;
    private readonly Semaphore _profileLease;
    private readonly string _profileName;
    private readonly Action<string> _quarantineProfile;
    private readonly Task<string> _stdoutTask;
    private readonly Task<string> _stderrTask;
    private bool _finished;
    private bool _disposed;

    public AppContainerProcessControl(SafeProcessHandle process, SafeFileHandle job,
        SafeFileHandle stdout, SafeFileHandle stderr, AppContainerAclLease aclLease, string tempDirectory,
        TimeSpan? timeout, Semaphore profileLease, string profileName, Action<string> quarantineProfile)
    {
        _process = process; _job = job; _stdout = stdout; _stderr = stderr;
        _aclLease = aclLease; _tempDirectory = tempDirectory; _timeout = timeout; _profileLease = profileLease;
        _profileName = profileName; _quarantineProfile = quarantineProfile;
        _stdoutTask = ReadOutputAsync(_stdout);
        _stderrTask = ReadOutputAsync(_stderr);
    }

    public async ValueTask<SandboxProcessOutput> WaitForExitAsync(CancellationToken cancellationToken)
    {
        var timedOut = false;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Task.Run(() =>
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_timeout is { } timeout && stopwatch.Elapsed >= timeout)
                {
                    timedOut = true;
                    RequestGracefulTermination();
                    if (AppContainerNative.WaitForSingleObject(_process, 250) == AppContainerNative.WaitTimeout
                        && !AppContainerNative.TerminateJobObject(_job, 1))
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to terminate the timed-out process tree.");
                    var terminated = AppContainerNative.WaitForSingleObject(_process, AppContainerNative.Infinite);
                    if (terminated != AppContainerNative.WaitObject0)
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Waiting for the timed-out process failed.");
                    break;
                }

                var waitMilliseconds = _timeout is { } limit
                    ? (uint)Math.Clamp((limit - stopwatch.Elapsed).TotalMilliseconds, 1, 50)
                    : 50u;
                var result = AppContainerNative.WaitForSingleObject(_process, waitMilliseconds);
                if (result == AppContainerNative.WaitObject0) break;
                if (result != AppContainerNative.WaitTimeout)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Waiting for the sandbox process failed.");
            }
        }, CancellationToken.None).ConfigureAwait(false);
        if (!AppContainerNative.TerminateJobObject(_job, timedOut ? 1u : 0u)
            && Marshal.GetLastPInvokeError() != AppContainerNative.ErrorProcessNotFound)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to stop remaining descendants after the root process exited.");
        await Task.WhenAll(_stdoutTask, _stderrTask).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!AppContainerNative.GetExitCodeProcess(_process, out var exitCode))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to read the process exit code.");
        _finished = true;
        return new SandboxProcessOutput(unchecked((int)exitCode), new[]
        {
            new SandboxOutputChunk(SandboxOutputStream.StandardOutput, _stdoutTask.Result),
            new SandboxOutputChunk(SandboxOutputStream.StandardError, _stderrTask.Result)
        }.Where(chunk => chunk.Text.Length != 0).ToArray(), timedOut);
    }

    public ValueTask TerminateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_finished && !_job.IsInvalid)
        {
            RequestGracefulTermination();
            if (AppContainerNative.WaitForSingleObject(_process, 250) == AppContainerNative.WaitTimeout
                && !AppContainerNative.TerminateJobObject(_job, 1)
                && Marshal.GetLastPInvokeError() != AppContainerNative.ErrorProcessNotFound)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to terminate the sandbox process tree.");
            AppContainerNative.WaitForSingleObject(_process, AppContainerNative.Infinite);
            AppContainerNative.TerminateJobObject(_job, 0);
            _finished = true;
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _disposed = true;
        if (!_finished && !_job.IsInvalid)
        {
            AppContainerNative.TerminateJobObject(_job, 1);
            AppContainerNative.WaitForSingleObject(_process, AppContainerNative.Infinite);
        }
        try { await Task.WhenAll(_stdoutTask, _stderrTask).ConfigureAwait(false); }
        catch (IOException) { }
        _stdout.Dispose(); _stderr.Dispose(); _process.Dispose(); _job.Dispose();
        Exception? aclCleanupFailure = null;
        try { _aclLease.Dispose(); }
        catch (Exception exception)
        {
            aclCleanupFailure = exception;
            _quarantineProfile(exception.Message);
        }
        try { Directory.Delete(_tempDirectory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (aclCleanupFailure is null)
            _profileLease.Release();
        else
            throw new IOException($"ACL cleanup failed for AppContainer profile '{_profileName}'; the profile is quarantined.", aclCleanupFailure);
    }

    private void RequestGracefulTermination()
    {
        var processId = AppContainerNative.GetProcessId(_process);
        if (processId != 0)
            AppContainerNative.GenerateConsoleCtrlEvent(AppContainerNative.CtrlBreakEvent, processId);
    }

    private static async Task<string> ReadOutputAsync(SafeFileHandle handle)
    {
        await using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[8192];
        var output = new StringBuilder(8192);
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0) break;
            var keep = Math.Min(read, Math.Max(0, 1_048_576 - output.Length));
            if (keep > 0) output.Append(buffer, 0, keep);
        }
        return output.ToString();
    }
}

[SupportedOSPlatform("windows")]
internal static partial class AppContainerNative
{
    internal const uint WaitObject0 = 0;
    internal const uint WaitTimeout = 258;
    internal const uint Infinite = 0xFFFFFFFF;
    internal const uint CtrlBreakEvent = 1;
    internal const int ErrorProcessNotFound = 128;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 1;
    private const uint ProcThreadAttributeSecurityCapabilities = 0x00020009;
    private const uint ProcThreadAttributeHandleList = 0x00020002;
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint ResumeFailed = 0xFFFFFFFF;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityCapabilities { public IntPtr AppContainerSid; public IntPtr Capabilities; public uint CapabilityCount; public uint Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo { public uint Cb; public IntPtr Reserved; public IntPtr Desktop; public IntPtr Title; public uint X; public uint Y; public uint XSize; public uint YSize; public uint XCountChars; public uint YCountChars; public uint FillAttribute; public uint Flags; public short ShowWindow; public short Reserved2; public IntPtr Reserved2Pointer; public IntPtr StdInput; public IntPtr StdOutput; public IntPtr StdError; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process; public IntPtr Thread; public uint ProcessId; public uint ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; public int InheritHandle; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation { public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize; public UIntPtr MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass; public uint SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperationCount; public ulong WriteOperationCount; public ulong OtherOperationCount; public ulong ReadTransferCount; public ulong WriteTransferCount; public ulong OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation { public BasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit; public UIntPtr JobMemoryLimit; public UIntPtr PeakProcessMemoryUsed; public UIntPtr PeakJobMemoryUsed; }

    internal static SecurityCapabilitiesBuffer CreateSecurityCapabilities(SecurityIdentifier sid, SidAndAttributes[] capabilities)
    {
        var sidBuffer = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBuffer, 0);
        var sidPointer = Marshal.AllocHGlobal(sidBuffer.Length);
        Marshal.Copy(sidBuffer, 0, sidPointer, sidBuffer.Length);
        var capSize = Marshal.SizeOf<SidAndAttributes>();
        var capPointer = capabilities.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(capSize * capabilities.Length);
        for (var i = 0; i < capabilities.Length; i++)
            Marshal.StructureToPtr(capabilities[i], IntPtr.Add(capPointer, i * capSize), false);
        var value = new SecurityCapabilities { AppContainerSid = sidPointer, Capabilities = capPointer, CapabilityCount = (uint)capabilities.Length };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
        Marshal.StructureToPtr(value, pointer, false);
        return new SecurityCapabilitiesBuffer(pointer, sidPointer, capPointer, capabilities.Length, capSize);
    }

    internal sealed class SecurityCapabilitiesBuffer : IDisposable
    {
        private readonly IntPtr _sid; private readonly IntPtr _caps; private readonly int _count; private readonly int _size;
        public IntPtr Pointer { get; }
        public SecurityCapabilitiesBuffer(IntPtr pointer, IntPtr sid, IntPtr caps, int count, int size) { Pointer = pointer; _sid = sid; _caps = caps; _count = count; _size = size; }
        public void Dispose() { if (_caps != IntPtr.Zero) { for (var i = 0; i < _count; i++) { var item = Marshal.PtrToStructure<SidAndAttributes>(IntPtr.Add(_caps, i * _size)); if (item.Sid != IntPtr.Zero) Marshal.FreeHGlobal(item.Sid); } Marshal.FreeHGlobal(_caps); } Marshal.FreeHGlobal(_sid); Marshal.FreeHGlobal(Pointer); }
    }

    internal static SecurityIdentifier GetOrCreateProfileSid(string name)
    {
        var result = CreateAppContainerProfile(name, name, "OmniCore process isolation profile", IntPtr.Zero, 0, out var profileSid);
        if (result >= 0 && profileSid != IntPtr.Zero)
        {
            var created = new SecurityIdentifier(profileSid);
            FreeSid(profileSid);
            return created;
        }
        // ERROR_ALREADY_EXISTS is expected if another process created this deterministic profile.
        if (result == unchecked((int)0x800700B7))
            return DeriveProfileSid(name);
        throw new Win32Exception(result, "Unable to create the AppContainer profile.");
    }

    internal static SecurityIdentifier DeriveProfileSid(string name) => TryDeriveSid(name)
        ?? throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to derive the AppContainer profile SID.");

    private static SecurityIdentifier? TryDeriveSid(string name)
    {
        var result = DeriveAppContainerSidFromAppContainerName(name, out var sid);
        if (result < 0 || sid == IntPtr.Zero) return null;
        try { return new SecurityIdentifier(sid); }
        finally { FreeSid(sid); }
    }

    internal static void ProbeAppContainerLaunchSupport(SecurityIdentifier sid)
    {
        using var securityCapabilities = CreateSecurityCapabilities(sid, Array.Empty<SidAndAttributes>());
        var environment = BuildAppContainerEnvironmentBlock(securityCapabilities.Pointer,
            new Dictionary<string, string>(), Path.Combine(Path.GetTempPath(), "OmniCore-Probe"),
            Environment.CurrentDirectory);
        Marshal.FreeHGlobal(environment);
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Job Objects are unavailable.");
        CloseHandle(job);
    }

    internal static SidAndAttributes[] CreateInternetClientCapability()
    {
        var capability = new SecurityIdentifier("S-1-15-3-1");
        var bytes = new byte[capability.BinaryLength];
        capability.GetBinaryForm(bytes, 0);
        var sid = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, sid, bytes.Length);
        return new[] { new SidAndAttributes { Sid = sid, Attributes = 0x4 } };
    }

    private static IntPtr BuildAppContainerEnvironmentBlock(IntPtr securityCapabilities,
        IReadOnlyDictionary<string, string> requested, string tempDirectory, string workingDirectory)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenDuplicate | TokenQuery, out var processToken))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to open the current process token.");
        using (processToken)
        {
            if (!CreateAppContainerToken(processToken, securityCapabilities, out var appContainerToken))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to create an AppContainer token for its environment.");
            using (appContainerToken)
            {
                if (!CreateEnvironmentBlock(out var sourceBlock, appContainerToken, false))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to create the AppContainer environment block.");
                try
                {
                    var values = ReadEnvironmentBlock(sourceBlock);
                    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "PATH", "SystemRoot", "WINDIR", "SystemDrive", "TEMP", "TMP", "LOCALAPPDATA",
                        "APPDATA", "USERPROFILE", "PUBLIC", "ALLUSERSPROFILE", "ProgramData",
                        "ProgramFiles", "ProgramFiles(x86)", "CommonProgramFiles", "CommonProgramFiles(x86)",
                        "COMPUTERNAME", "OS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER",
                        "PROCESSOR_LEVEL", "PROCESSOR_REVISION", "USERNAME", "HOMEDRIVE", "HOMEPATH",
                        "ComSpec", "PATHEXT", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_X86"
                    };
                    foreach (var name in values.Keys.ToArray())
                    {
                        if (name.StartsWith('=') && name.Equals("=" + Path.GetPathRoot(workingDirectory)!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!allowed.Contains(name)) values.Remove(name);
                    }

                    var drive = Path.GetPathRoot(workingDirectory);
                    if (drive is { Length: 3 } && drive[1] == ':')
                        values["=" + drive[..2]] = workingDirectory;
                    foreach (var pair in requested) values[pair.Key] = pair.Value;
                    values["TEMP"] = tempDirectory;
                    values["TMP"] = tempDirectory;
                    var block = string.Join('\0', values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
                    var characters = block.ToCharArray();
                    var pointer = Marshal.AllocHGlobal(checked(characters.Length * sizeof(char)));
                    Marshal.Copy(characters, 0, pointer, characters.Length);
                    return pointer;
                }
                finally { DestroyEnvironmentBlock(sourceBlock); }
            }
        }
    }

    private static Dictionary<string, string> ReadEnvironmentBlock(IntPtr block)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        var totalChars = 0;
        while (totalChars < 1_000_000)
        {
            var item = new StringBuilder();
            while (true)
            {
                var character = (char)Marshal.ReadInt16(block, offset * sizeof(char));
                offset++;
                totalChars++;
                if (character == '\0') break;
                item.Append(character);
            }
            if (item.Length == 0) break;
            var text = item.ToString();
            var separator = text.IndexOf('=', text[0] == '=' ? 1 : 0);
            if (separator > 0) values[text[..separator]] = text[(separator + 1)..];
        }
        return values;
    }

    internal static bool CreateSuspendedProcess(string application, string commandLine, string workingDirectory,
        IReadOnlyDictionary<string, string> requestedEnvironment, string tempDirectory,
        SecurityCapabilitiesBuffer securityCapabilities, out SafeFileHandle? stdoutRead,
        out SafeFileHandle? stderrRead, out SafeFileHandle? job, out SafeProcessHandle? process)
    {
        stdoutRead = stderrRead = null; job = null; process = null;
        var startupSize = (nuint)0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref startupSize);
        var attributes = Marshal.AllocHGlobal(checked((int)startupSize));
        IntPtr stdoutWrite = IntPtr.Zero, stderrWrite = IntPtr.Zero, threadHandle = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero, jobHandle = IntPtr.Zero;
        var processCreated = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref startupSize))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeSecurityCapabilities,
                securityCapabilities.Pointer, (nuint)Marshal.SizeOf<SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to set AppContainer security capabilities.");
            var pipeAttributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };
            if (!CreatePipe(out var stdoutReadRaw, out stdoutWrite, ref pipeAttributes, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to create stdout pipe.");
            stdoutRead = new SafeFileHandle(stdoutReadRaw, true);
            if (!SetHandleInformation(stdoutReadRaw, HandleFlagInherit, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to protect stdout pipe.");
            if (!CreatePipe(out var stderrReadRaw, out stderrWrite, ref pipeAttributes, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to create stderr pipe.");
            stderrRead = new SafeFileHandle(stderrReadRaw, true);
            if (!SetHandleInformation(stderrReadRaw, HandleFlagInherit, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to protect stderr pipe.");
            var handleList = Marshal.AllocHGlobal(IntPtr.Size * 2);
            try
            {
                Marshal.WriteIntPtr(handleList, 0, stdoutWrite);
                Marshal.WriteIntPtr(handleList, IntPtr.Size, stderrWrite);
                if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList, handleList,
                    (nuint)(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to restrict inherited handles.");
                var startup = new StartupInfoEx
                {
                    StartupInfo = new StartupInfo { Cb = (uint)Marshal.SizeOf<StartupInfoEx>(), Flags = StartfUseStdHandles,
                        StdInput = IntPtr.Zero, StdOutput = stdoutWrite, StdError = stderrWrite },
                    AttributeList = attributes
                };
                var startupPointer = Marshal.AllocHGlobal(Marshal.SizeOf<StartupInfoEx>());
                var environmentPointer = BuildAppContainerEnvironmentBlock(securityCapabilities.Pointer,
                    requestedEnvironment, tempDirectory, workingDirectory);
                var mutableCommand = Marshal.StringToHGlobalUni(commandLine);
                try
                {
                    Marshal.StructureToPtr(startup, startupPointer, false);
                    if (!CreateProcess(application, mutableCommand, IntPtr.Zero, IntPtr.Zero, true,
                        CreateSuspended | CreateNewProcessGroup | CreateUnicodeEnvironment | ExtendedStartupInfoPresent, environmentPointer,
                        workingDirectory, startupPointer, out var info))
                    {
                        var error = Marshal.GetLastPInvokeError();
                        throw new Win32Exception(error, $"CreateProcessW failed for AppContainer (Win32 error {error}).");
                    }
                    processHandle = info.Process; threadHandle = info.Thread; processCreated = true;
                }
                finally { Marshal.FreeHGlobal(startupPointer); Marshal.FreeHGlobal(environmentPointer); Marshal.FreeHGlobal(mutableCommand); }
            }
            finally { Marshal.FreeHGlobal(handleList); }

            jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (jobHandle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to create process Job Object.");
            var limits = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };
            var limitsPointer = Marshal.AllocHGlobal(Marshal.SizeOf<ExtendedLimitInformation>());
            try
            {
                Marshal.StructureToPtr(limits, limitsPointer, false);
                if (!SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformation, limitsPointer, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to configure kill-on-close Job Object.");
            }
            finally { Marshal.FreeHGlobal(limitsPointer); }
            if (!AssignProcessToJobObject(jobHandle, processHandle))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to assign AppContainer process to its Job Object.");
            CloseHandle(stdoutWrite); stdoutWrite = IntPtr.Zero;
            CloseHandle(stderrWrite); stderrWrite = IntPtr.Zero;
            process = new SafeProcessHandle(processHandle, true); processHandle = IntPtr.Zero;
            job = new SafeFileHandle(jobHandle, true); jobHandle = IntPtr.Zero;
            if (ResumeThread(threadHandle) == ResumeFailed)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to resume AppContainer process.");
            CloseHandle(threadHandle); threadHandle = IntPtr.Zero;
            return true;
        }
        catch
        {
            if (jobHandle != IntPtr.Zero) TerminateJobObjectRaw(jobHandle, 1);
            if (processCreated && processHandle != IntPtr.Zero) TerminateProcess(processHandle, 1);
            if (processHandle != IntPtr.Zero)
            {
                WaitForSingleObjectRaw(processHandle, 5000);
                CloseHandle(processHandle);
            }
            if (jobHandle != IntPtr.Zero) CloseHandle(jobHandle);
            if (threadHandle != IntPtr.Zero) CloseHandle(threadHandle);
            process?.Dispose(); job?.Dispose(); stdoutRead?.Dispose(); stderrRead?.Dispose();
            throw;
        }
        finally
        {
            if (stdoutWrite != IntPtr.Zero) CloseHandle(stdoutWrite);
            if (stderrWrite != IntPtr.Zero) CloseHandle(stderrWrite);
            DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    private static partial IntPtr GetCurrentProcess();
    [LibraryImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenProcessToken(IntPtr process, uint access, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token);
    [LibraryImport("kernelbase.dll", EntryPoint = "CreateAppContainerToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CreateAppContainerToken(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token, IntPtr securityCapabilities, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle appContainerToken);
    [LibraryImport("userenv.dll", EntryPoint = "CreateEnvironmentBlock", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CreateEnvironmentBlock(out IntPtr environment, Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);
    [LibraryImport("userenv.dll", EntryPoint = "DestroyEnvironmentBlock", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyEnvironmentBlock(IntPtr environment);
    [LibraryImport("userenv.dll", EntryPoint = "CreateAppContainerProfile", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateAppContainerProfile(string name, string displayName, string description, IntPtr capabilities, uint capabilityCount, out IntPtr sid);
    [LibraryImport("userenv.dll", EntryPoint = "DeriveAppContainerSidFromAppContainerName", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [LibraryImport("advapi32.dll", EntryPoint = "FreeSid")]
    private static partial IntPtr FreeSid(IntPtr sid);
    [LibraryImport("kernel32.dll", EntryPoint = "InitializeProcThreadAttributeList", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [LibraryImport("kernel32.dll", EntryPoint = "UpdateProcThreadAttribute", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool UpdateProcThreadAttribute(IntPtr list, uint flags, uint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [LibraryImport("kernel32.dll", EntryPoint = "DeleteProcThreadAttributeList")]
    private static partial void DeleteProcThreadAttributeList(IntPtr list);
    [LibraryImport("kernel32.dll", EntryPoint = "CreatePipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, ref SecurityAttributes attributes, uint size);
    [LibraryImport("kernel32.dll", EntryPoint = "SetHandleInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CreateProcess(string application, IntPtr commandLine, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, IntPtr startupInfo, out ProcessInformation processInformation);
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateJobObject(IntPtr attributes, string? name);
    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetInformationJobObject(IntPtr job, uint informationClass, IntPtr information, uint length);
    [LibraryImport("kernel32.dll", EntryPoint = "AssignProcessToJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [LibraryImport("kernel32.dll", EntryPoint = "ResumeThread", SetLastError = true)]
    private static partial uint ResumeThread(IntPtr thread);
    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(IntPtr handle);
    [LibraryImport("kernel32.dll", EntryPoint = "TerminateJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [LibraryImport("kernel32.dll", EntryPoint = "TerminateJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool TerminateJobObjectRaw(IntPtr job, uint exitCode);
    [LibraryImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool TerminateProcess(IntPtr process, uint exitCode);
    [LibraryImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)]
    private static partial uint WaitForSingleObjectRaw(IntPtr handle, uint milliseconds);
    [LibraryImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)]
    internal static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [LibraryImport("kernel32.dll", EntryPoint = "GenerateConsoleCtrlEvent", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GenerateConsoleCtrlEvent(uint eventType, uint processGroupId);
    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessId", SetLastError = true)]
    internal static partial uint GetProcessId(SafeProcessHandle process);
    [LibraryImport("kernel32.dll", EntryPoint = "GetExitCodeProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
