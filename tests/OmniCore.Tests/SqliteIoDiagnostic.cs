using System.Runtime.InteropServices;
using System.Text;

namespace OmniCore.Tests;

internal static class SqliteIoDiagnostic
{
    private static SetEnd? _original, _hook;
    private static IntPtr _vfs, _setPointer, _originalPointer;
    private static string _lastFailure = "no failed native truncate observed";
    internal static string LastFailure => Volatile.Read(ref _lastFailure);

    [StructLayout(LayoutKind.Sequential)]
    private struct Vfs
    {
        public int Version, FileSize, MaxPath;
        public IntPtr Next, Name, Data;
        public IntPtr Open, Delete, Access, FullPath, DlOpen, DlError, DlSym, DlClose;
        public IntPtr Randomness, Sleep, CurrentTime, GetLastError, CurrentTime64;
        public IntPtr SetSystemCall, GetSystemCall, NextSystemCall;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetSystemCall(IntPtr vfs, [MarshalAs(UnmanagedType.LPStr)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetSystemCall(IntPtr vfs, [MarshalAs(UnmanagedType.LPStr)] string name, IntPtr function);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    private delegate int SetEnd(IntPtr file);
    [DllImport("e_sqlite3", EntryPoint = "sqlite3_vfs_find", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FindVfs(IntPtr name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(IntPtr file, StringBuilder result, uint capacity, uint flags);
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);

    internal static void EnableOsTrace()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pointer = FindVfs(IntPtr.Zero);
        var vfs = Marshal.PtrToStructure<Vfs>(pointer);
        if (vfs.Version < 3) throw new InvalidOperationException("VFS syscall tracing is unavailable.");
        var get = Marshal.GetDelegateForFunctionPointer<GetSystemCall>(vfs.GetSystemCall);
        var set = Marshal.GetDelegateForFunctionPointer<SetSystemCall>(vfs.SetSystemCall);
        _vfs = pointer; _setPointer = vfs.SetSystemCall;
        _originalPointer = get(pointer, "SetEndOfFile");
        _original = Marshal.GetDelegateForFunctionPointer<SetEnd>(_originalPointer);
        _hook = file =>
        {
            var result = _original(file);
            var error = Marshal.GetLastPInvokeError();
            if (result == 0)
            {
                var path = new StringBuilder(32768);
                var length = GetFinalPathNameByHandleW(file, path, (uint)path.Capacity, 8);
                var detail = $"SetEndOfFile failed: Win32={error}; path={(length > 0 ? path.ToString() : "unavailable")}";
                Volatile.Write(ref _lastFailure, detail);
                Console.Error.WriteLine(detail);
            }
            SetLastError((uint)error);
            return result;
        };
        var status = set(pointer, "SetEndOfFile", Marshal.GetFunctionPointerForDelegate(_hook));
        if (status != SQLitePCL.raw.SQLITE_OK)
            throw new InvalidOperationException("Cannot trace SQLite truncation: " + status);
    }

    internal static void DisableOsTrace()
    {
        if (!OperatingSystem.IsWindows()) return;
        var set = Marshal.GetDelegateForFunctionPointer<SetSystemCall>(_setPointer);
        var status = set(_vfs, "SetEndOfFile", _originalPointer);
        if (status != SQLitePCL.raw.SQLITE_OK) throw new InvalidOperationException("Cannot restore SQLite syscall: " + status);
    }
}
