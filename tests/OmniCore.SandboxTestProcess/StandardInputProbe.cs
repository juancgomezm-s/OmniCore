using System.Runtime.InteropServices;

internal static class StandardInputProbe
{
    internal static int Run()
    {
        var handle = GetStdHandle(-10);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1) || GetFileType(handle) != 3)
        {
            Console.Write("INVALID_STDIN");
            return 28;
        }
        Console.Write("PIPE:" + Console.In.ReadToEnd());
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);
}
