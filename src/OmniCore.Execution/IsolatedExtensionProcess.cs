using System.Text;
using OmniCore.Sandbox;

namespace OmniCore.Execution;

/// <summary>Mandatory OS-isolated duplex transport for executable extensions.
/// Host consumes this adapter, never platform sandbox internals. No permissive fallback.</summary>
public sealed class IsolatedExtensionProcess : IAsyncDisposable
{
    private readonly ISandboxDuplexProcessControl _control;
    private readonly string _root;
    private int _disposed;
    public StreamWriter StandardInput { get; }
    public Stream StandardOutput => _control.StandardOutput;
    public Stream StandardError => _control.StandardError;
    public string Protection => OperatingSystem.IsWindows() ? "windows-lpac-job" : "linux-bubblewrap";

    private IsolatedExtensionProcess(ISandboxDuplexProcessControl control, string root)
    {
        _control = control; _root = root;
        StandardInput = new StreamWriter(control.StandardInput, new UTF8Encoding(false), 4096, leaveOpen: true)
            { NewLine = "\n", AutoFlush = true };
    }

    public static async Task<IsolatedExtensionProcess> StartAsync(string executable, IReadOnlyList<string> arguments,
        string packageDirectory, string? storageRoot = null, CancellationToken token = default)
    {
        // Resolve only the user-configured runtime, not executable lookup relative to the package.
        var package = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory));
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var packagedExecutable = Path.IsPathRooted(executable)
            && Path.GetFullPath(executable).StartsWith(package + Path.DirectorySeparatorChar, pathComparison);
        executable = packagedExecutable ? Path.GetFullPath(executable)
            : new SystemExecutableResolver().Resolve(executable, package).ResolvedPath;
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("extension_strong_sandbox_unavailable");
        var storage = Path.GetFullPath(storageRoot ?? Path.Combine(Path.GetTempPath(), "OmniCore-extension-runtime"));
        if (storage.Equals(package, pathComparison) || storage.StartsWith(package + Path.DirectorySeparatorChar, pathComparison))
            throw new IOException("extension_runtime_storage_must_be_outside_package");
        var root = Path.Combine(storage, Guid.NewGuid().ToString("N"));
        var image = Path.Combine(root, "image"); var data = Path.Combine(root, "data");
        ISandboxProcessControl? control = null;
        try
        {
            Directory.CreateDirectory(data);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            CopyPackage(package, image, token);
            string Remap(string value) => Path.IsPathRooted(value)
                && Path.GetFullPath(value).StartsWith(package + Path.DirectorySeparatorChar, pathComparison)
                ? Path.Combine(image, Path.GetRelativePath(package, Path.GetFullPath(value))) : value;
            var stagedExecutable = Remap(executable);
            if (OperatingSystem.IsWindows() && stagedExecutable == executable)
            {
                // Grant the copied runtime directory, not ambient access to its original installation.
                var runtime = Path.Combine(image, ".runtime"); Directory.CreateDirectory(runtime);
                stagedExecutable = Path.Combine(runtime, Path.GetFileName(executable));
                File.Copy(executable, stagedExecutable);
            }
            // Copy external Linux runtimes too: installations commonly use a symlink into /opt.
            // Shared system libraries remain read-only, without exposing the installation's parent.
            if (OperatingSystem.IsLinux() && stagedExecutable == executable)
            {
                stagedExecutable = Path.Combine(image, ".runtime-" + Path.GetFileName(executable));
                File.Copy(executable, stagedExecutable);
                File.SetUnixFileMode(stagedExecutable, File.GetUnixFileMode(executable));
            }
            var environment = new Dictionary<string, string>
            {
                ["OMNICORE_EXTENSION_DATA_DIR"] = data,
                ["HOME"] = data,
                ["LANG"] = "C.UTF-8"
            };
            // Node realpath walks every ancestor (including the drive root) before reading the
            // entry. Avoid granting ambient ancestor ACLs. Package links are rejected above.
            var runtimeArguments = arguments.Select(Remap).ToArray();
            if (Path.GetFileNameWithoutExtension(executable).Equals("node", StringComparison.OrdinalIgnoreCase))
                runtimeArguments = new[] { "--preserve-symlinks", "--preserve-symlinks-main" }.Concat(runtimeArguments).ToArray();
            var launch = new SandboxLaunchSpec(SandboxStrength.Strong, stagedExecutable,
                runtimeArguments, image, environment,
                [new(image, SandboxPathAccess.Read), new(data, SandboxPathAccess.Read | SandboxPathAccess.Write)],
                new(SandboxNetworkMode.Deny, [])) { Duplex = true, LessPrivilegedAppContainer = true };
            ISandboxProcessLauncher launcher = OperatingSystem.IsWindows()
                ? new WindowsAppContainerProcessLauncher() : new LinuxBubblewrapProcessLauncher();
            control = await launcher.StartAsync(launch, token).ConfigureAwait(false);
            if (control is not ISandboxDuplexProcessControl duplex) throw new IOException("extension_duplex_sandbox_required");
            return new(duplex, root);
        }
        catch
        {
            if (control is not null) await control.DisposeAsync().ConfigureAwait(false);
            DeletePrivateRoot(root); throw;
        }
    }

    private static void CopyPackage(string source, string destination, CancellationToken token)
    {
        var entries = 0; long bytes = 0;
        void Copy(string from, string to)
        {
            token.ThrowIfCancellationRequested();
            if (++entries > 1024 || (File.GetAttributes(from) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("extension_runtime_package_link_or_limit");
            if (Directory.Exists(from))
            {
                Directory.CreateDirectory(to);
                foreach (var entry in Directory.EnumerateFileSystemEntries(from)) Copy(entry, Path.Combine(to, Path.GetFileName(entry)));
            }
            else
            {
                bytes += new FileInfo(from).Length;
                if (bytes > 67_108_864) throw new IOException("extension_runtime_package_limit");
                File.Copy(from, to);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(to, File.GetUnixFileMode(from));
            }
        }
        Copy(source, destination);
    }

    public void Kill() => _control.TerminateAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
    public async Task<int> WaitForExitAsync(CancellationToken token) =>
        (await _control.WaitForExitAsync(token).ConfigureAwait(false)).ExitCode;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await DisposeControlAndDeleteRootAsync(_control, StandardInput, _root).ConfigureAwait(false);
    }

    internal static async ValueTask DisposeControlAndDeleteRootAsync(
        ISandboxDuplexProcessControl control, StreamWriter standardInput, string root)
    {
        Exception? inputCloseFailure = null;
        try { standardInput.Dispose(); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) { inputCloseFailure = error; }

        // This is the process-tree completion barrier. If it fails, keep the private image so
        // the uncertain process has evidence and preserve that exact failure for the caller.
        await control.DisposeAsync().ConfigureAwait(false);
        DeletePrivateRoot(root);

        if (inputCloseFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inputCloseFailure).Throw();
    }

    internal static void DeletePrivateRoot(string root)
    {
        // A generated leaf only, never a caller's package, workspace or storage root.
        if (!Directory.Exists(root)) return;
        if (OperatingSystem.IsWindows())
        {
            // The sandbox control is disposed before this is called, which is the process-tree
            // completion barrier. Windows can still briefly retain an executable image section
            // after the process handle is signaled. Wait only for sharing/lock violations and
            // surface a persistent lock; never turn a live/orphaned worker into successful cleanup.
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            IOException? lastLock = null;
            while (true)
            {
                try { Directory.Delete(root, recursive: true); return; }
                catch (IOException error) when (IsTransientWindowsFileLock(error))
                {
                    lastLock = error;
                    if (deadline.Elapsed >= TimeSpan.FromSeconds(2))
                        throw new IOException("extension_runtime_cleanup_lock_timeout", lastLock);
                    Thread.Sleep(25);
                }
            }
        }

        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsTransientWindowsFileLock(IOException error) =>
        (error.HResult & 0xffff) is 32 or 33; // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION.
}
