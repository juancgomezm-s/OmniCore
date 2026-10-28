namespace OmniCore.Host;

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OmniCore.Domain;

/// <summary>Workspace-confined import source opened by the trusted Host, not by a renderer path check.</summary>
internal sealed class WorkspaceConversationImportSource : IDisposable
{
    private readonly List<IDisposable> _leases = [];
    internal byte[] Bytes { get; private set; } = [];
    internal string CanonicalRoot { get; }
    private WorkspaceConversationImportSource(string root) => CanonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    internal static bool IsRelativePath(string? path) => path is { Length: > 0 and <= 4096 }
        && !Path.IsPathRooted(path) && !path.Contains(':') && !path.Contains('\\')
        && !path.Any(char.IsControl) && path.Split('/').All(part => part.Length > 0 && part is not ("." or "..")
            && !part.EndsWith('.') && !part.EndsWith(' '));

    internal static WorkspaceConversationImportSource Open(string root, string relativePath, Action<string>? beforeOpenForTests = null)
    {
        if (!Path.IsPathFullyQualified(root) || !IsRelativePath(relativePath)) throw Invalid("invalid_path");
        var result = new WorkspaceConversationImportSource(root);
        try
        {
            if (OperatingSystem.IsWindows()) result.ReadWindows(relativePath, beforeOpenForTests);
            else if (OperatingSystem.IsLinux()) result.ReadLinux(relativePath, beforeOpenForTests);
            else throw Invalid("confinement_unavailable");
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private void ReadWindows(string relativePath, Action<string>? beforeOpen)
    {
        // Every opened ancestor denies write/delete sharing for this short import.
        // OPEN_REPARSE_POINT checks the actual opened directory, not a prior stat.
        var root = OpenWindows(CanonicalRoot, directory: true);
        if (!SamePath(FinalWindowsPath(root), CanonicalRoot)) throw Invalid("workspace_changed");
        var parts = relativePath.Split('/');
        var current = CanonicalRoot;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            current = Path.Combine(current, parts[index]);
            beforeOpen?.Invoke(current);
            var directory = OpenWindows(current, directory: true);
            if (!Within(FinalWindowsPath(directory), CanonicalRoot)) throw Invalid("outside_workspace");
        }
        current = Path.Combine(current, parts[^1]);
        beforeOpen?.Invoke(current);
        var source = OpenWindows(current, directory: false);
        if (!Within(FinalWindowsPath(source), CanonicalRoot)) throw Invalid("outside_workspace");
        var before = WindowsInformation(source);
        using var stream = new FileStream(source, FileAccess.Read);
        Bytes = ReadBounded(stream, ((long)before.SizeHigh << 32) | before.SizeLow);
        var after = WindowsInformation(source);
        if (before.SizeHigh != after.SizeHigh || before.SizeLow != after.SizeLow
            || before.LastWrite != after.LastWrite || before.IndexHigh != after.IndexHigh || before.IndexLow != after.IndexLow
            || before.VolumeSerial != after.VolumeSerial || !SamePath(FinalWindowsPath(root), CanonicalRoot)
            || !Within(FinalWindowsPath(source), CanonicalRoot)) throw Invalid("source_changed");
        // Source is already snapshotted; ancestor handles stay leased until the
        // import barrier has persisted provenance and its conservative ceiling.
    }

    private SafeFileHandle OpenWindows(string path, bool directory)
    {
        const uint readAttributes = 0x80, genericRead = 0x80000000, shareRead = 1, existing = 3;
        const uint backupSemantics = 0x02000000, openReparse = 0x00200000;
        var handle = CreateFile(path, directory ? readAttributes : genericRead, shareRead, IntPtr.Zero,
            existing, backupSemantics | openReparse, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw Invalid("source_unavailable"); }
        _leases.Add(handle);
        var info = WindowsInformation(handle);
        if ((info.Attributes & 0x400) != 0 || GetFileType(handle) != 1
            || directory != ((info.Attributes & 0x10) != 0)) throw Invalid("reparse_or_nonregular");
        return handle;
    }

    private static WindowsFileInformation WindowsInformation(SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out var info) ? info : throw Invalid("identity_unavailable");

    private static string FinalWindowsPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        var count = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (count == 0 || count >= buffer.Capacity) throw Invalid("identity_unavailable");
        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    private void ReadLinux(string relativePath, Action<string>? beforeOpen)
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)) throw Invalid("confinement_unavailable");
        const int pathOnly = 0x200000, closeOnExec = 0x80000, directory = 0x10000, noFollow = 0x20000;
        var rootFd = UnixOpen(CanonicalRoot, pathOnly | closeOnExec | directory | noFollow);
        if (rootFd < 0) throw Invalid("source_unavailable");
        var root = new SafeFileHandle((IntPtr)rootFd, ownsHandle: true); _leases.Add(root);
        if (!SamePath(FinalLinuxPath(root), CanonicalRoot)) throw Invalid("workspace_changed");
        beforeOpen?.Invoke(Path.Combine(CanonicalRoot, relativePath));
        // openat2 resolves the entire path beneath this held root. Concurrent
        // rename/symlink races fail in the kernel (including EAGAIN); no loose fallback.
        var how = new OpenHow { Flags = closeOnExec | 0x800UL, Resolve = 0x08 | 0x04 | 0x02 };
        var fd = OpenAt2(437, rootFd, relativePath, ref how, (nuint)Marshal.SizeOf<OpenHow>());
        if (fd < 0) throw Invalid("source_unavailable");
        var source = new SafeFileHandle((IntPtr)fd, ownsHandle: true); _leases.Add(source);
        var before = UnixInformation((int)fd);
        if ((BinaryPrimitives.ReadUInt16LittleEndian(before.AsSpan(28)) & 0xf000) != 0x8000) throw Invalid("reparse_or_nonregular");
        using var stream = new FileStream(source, FileAccess.Read);
        Bytes = ReadBounded(stream, checked((long)BinaryPrimitives.ReadUInt64LittleEndian(before.AsSpan(40))));
        var after = UnixInformation((int)fd);
        if (!before.AsSpan(32, 16).SequenceEqual(after.AsSpan(32, 16))
            || !before.AsSpan(96, 32).SequenceEqual(after.AsSpan(96, 32))
            || !before.AsSpan(136, 8).SequenceEqual(after.AsSpan(136, 8))
            || !SamePath(FinalLinuxPath(root), CanonicalRoot)
            || !Within(FinalLinuxPath(source), CanonicalRoot)) throw Invalid("source_changed");
    }

    private static byte[] UnixInformation(int fd)
    {
        var data = new byte[256];
        if (UnixStatx(fd, "", 0x1000, 0x7ff, data) != 0 || (BinaryPrimitives.ReadUInt32LittleEndian(data) & 0x7ff) != 0x7ff)
            throw Invalid("identity_unavailable");
        return data;
    }
    private static string FinalLinuxPath(SafeFileHandle handle) => File.ResolveLinkTarget(
        "/proc/self/fd/" + handle.DangerousGetHandle().ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture), true)?.FullName
            ?? throw Invalid("identity_unavailable");

    private static byte[] ReadBounded(Stream stream, long size)
    {
        if (!stream.CanSeek || size is <= 0 or > ConversationTranscriptImport.MaximumSourceBytes) throw Invalid("document_size");
        var bytes = new byte[checked((int)size)]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw Invalid("source_changed");
        return bytes;
    }
    private static bool SamePath(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Within(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Length > 0 && relative != "." && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathFullyQualified(relative);
    }
    private static InvalidDataException Invalid(string reason) => new("conversation.import." + reason);
    public void Dispose() { for (var index = _leases.Count - 1; index >= 0; index--) _leases[index].Dispose(); _leases.Clear(); }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct WindowsFileInformation
    {
        internal uint Attributes;
        internal long Creation, LastAccess, LastWrite;
        internal uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow { internal ulong Flags, Mode, Resolve; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out WindowsFileInformation information);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, SetLastError = true)] private static extern int UnixOpen(string path, int flags);
    [DllImport("libc", EntryPoint = "syscall", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern long OpenAt2(long number, int root, string path, ref OpenHow how, nuint size);
    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern int UnixStatx(int fd, string path, int flags, uint mask, [Out] byte[] buffer);
}
