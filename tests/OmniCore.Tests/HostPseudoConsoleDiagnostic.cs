namespace OmniCore.Tests;

using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

internal static class HostPseudoConsoleDiagnostic
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const nuint PseudoConsoleAttribute = 0x00020016;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;

    internal static async Task<(int ExitCode, string Output)> RunAsync(
        string executable, string workingDirectory, CancellationToken cancellationToken, bool systemEcho = false)
    {
        IntPtr inputRead = IntPtr.Zero, inputWrite = IntPtr.Zero;
        IntPtr outputRead = IntPtr.Zero, outputWrite = IntPtr.Zero;
        IntPtr pseudoConsole = IntPtr.Zero, attributeList = IntPtr.Zero, startupPointer = IntPtr.Zero;
        IntPtr process = IntPtr.Zero, thread = IntPtr.Zero;
        Task<string>? outputTask = null;
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0)) ThrowLastError("CreatePipe(input)");
            if (!CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0)) ThrowLastError("CreatePipe(output)");
            var createHr = CreatePseudoConsole(new Coord(80, 24), inputRead, outputWrite, 0, out pseudoConsole);
            if (createHr < 0) Marshal.ThrowExceptionForHR(createHr);

            nuint attributeSize = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeSize);
            if (attributeSize == 0) ThrowLastError("InitializeProcThreadAttributeList(size)");
            attributeList = Marshal.AllocHGlobal(checked((int)attributeSize));
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeSize))
                ThrowLastError("InitializeProcThreadAttributeList");
            if (!UpdateProcThreadAttribute(attributeList, 0, PseudoConsoleAttribute, pseudoConsole,
                (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                ThrowLastError("UpdateProcThreadAttribute(PSEUDOCONSOLE)");

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo { Cb = (uint)Marshal.SizeOf<StartupInfoEx>(), Flags = 0x00000100 },
                AttributeList = attributeList
            };
            startupPointer = Marshal.AllocHGlobal(Marshal.SizeOf<StartupInfoEx>());
            Marshal.StructureToPtr(startup, startupPointer, false);
            var commandLine = new StringBuilder(systemEcho
                ? $"\"{executable}\" /d /c echo READY"
                : $"\"{executable}\" terminal-echo");
            if (!CreateProcess(executable, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                ExtendedStartupInfoPresent,
                IntPtr.Zero, workingDirectory, startupPointer, out var info))
                ThrowLastError("CreateProcess(host diagnostic)");
            process = info.Process;
            thread = info.Thread;
            Close(ref inputRead);
            Close(ref outputWrite);

            using var outputStream = new FileStream(new SafeFileHandle(outputRead, ownsHandle: false), FileAccess.Read, 4096, isAsync: false);
            using var input = new FileStream(new SafeFileHandle(inputWrite, ownsHandle: false), FileAccess.Write, 4096, isAsync: false);
            using var output = new StreamReader(outputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            if (systemEcho)
            {
                // Drain from process start while waiting: the diagnostic must distinguish an
                // exited child whose output escaped the HPCON from a child blocked on startup.
                outputTask = ReadAllAsync(output, cancellationToken);
                var nativeWait = WaitForSingleObject(process, 5_000);
                if (nativeWait == WaitTimeout) throw new TimeoutException("The native cmd ConPTY probe did not exit.");
                if (nativeWait != WaitObject0) ThrowLastError("WaitForSingleObject(cmd)");
                if (!GetExitCodeProcess(process, out var nativeExitCode)) ThrowLastError("GetExitCodeProcess(cmd)");
                ClosePseudoConsole(pseudoConsole);
                pseudoConsole = IntPtr.Zero;
                return (unchecked((int)nativeExitCode), await outputTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            }
            var ready = await output.ReadLineAsync(cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            var readyText = ready is null ? null : Regex.Replace(ready, @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))", "").Trim();
            if (readyText is null || !readyText.Equals("READY", StringComparison.Ordinal))
                throw new InvalidDataException($"Expected READY from host ConPTY child, got '{ready ?? "<eof>"}'.");
            outputTask = ReadAllAsync(output, cancellationToken);
            var resizeHr = ResizePseudoConsole(pseudoConsole, new Coord(100, 32));
            if (resizeHr < 0) Marshal.ThrowExceptionForHR(resizeHr);
            var bytes = Encoding.UTF8.GetBytes("terminal-test\r\n");
            await input.WriteAsync(bytes, cancellationToken);
            await input.FlushAsync(cancellationToken);

            var wait = WaitForSingleObject(process, 10_000);
            if (wait == WaitTimeout) throw new TimeoutException("The non-AppContainer ConPTY helper did not exit after duplex input.");
            if (wait != WaitObject0) ThrowLastError("WaitForSingleObject(process)");
            if (!GetExitCodeProcess(process, out var exitCode)) ThrowLastError("GetExitCodeProcess");
            ClosePseudoConsole(pseudoConsole);
            pseudoConsole = IntPtr.Zero;
            var result = await outputTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return (unchecked((int)exitCode), result);
        }
        finally
        {
            if (process != IntPtr.Zero)
            {
                _ = TerminateProcess(process, 0xE0000001);
                _ = WaitForSingleObject(process, 5_000);
            }
            if (pseudoConsole != IntPtr.Zero) ClosePseudoConsole(pseudoConsole);
            if (outputTask is not null)
            {
                try { _ = await outputTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException) { }
            }
            Close(ref thread);
            Close(ref process);
            Close(ref inputRead);
            Close(ref inputWrite);
            Close(ref outputRead);
            Close(ref outputWrite);
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (startupPointer != IntPtr.Zero) Marshal.FreeHGlobal(startupPointer);
        }
    }

    private static async Task<string> ReadAllAsync(StreamReader stream, CancellationToken cancellationToken)
    {
        return await stream.ReadToEndAsync(cancellationToken);
    }

    private static void ThrowLastError(string operation) =>
        throw new Win32Exception(Marshal.GetLastPInvokeError(), operation);

    private static void Close(ref IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        _ = CloseHandle(handle);
        handle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord(short x, short y)
    {
        internal short X = x;
        internal short Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal uint Cb;
        internal IntPtr Reserved;
        internal IntPtr Desktop;
        internal IntPtr Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal ushort ShowWindow;
        internal ushort Reserved2Length;
        internal IntPtr Reserved2;
        internal IntPtr StdInput;
        internal IntPtr StdOutput;
        internal IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr securityAttributes, uint size);

    [DllImport("kernel32.dll", EntryPoint = "CreatePseudoConsole", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);

    [DllImport("kernel32.dll", EntryPoint = "ResizePseudoConsole", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);

    [DllImport("kernel32.dll", EntryPoint = "ClosePseudoConsole")]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value,
        nuint size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment,
        string currentDirectory, IntPtr startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
