using System.Runtime.InteropServices;
using System.Text;

internal static class StandardInputProbe
{
    internal static int Run()
    {
        var handle = GetStdHandle(-10);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1) || GetFileType(handle) != 3)
        {
            using var invalidWriter = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), leaveOpen: true);
            invalidWriter.Write("INVALID_STDIN");
            invalidWriter.Flush();
            return 28;
        }
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true), leaveOpen: true);
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), leaveOpen: true);
        output.Write("PIPE:" + input.ReadToEnd());
        output.Flush();
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);
}
