namespace OmniCore.Infrastructure;

/// <summary>Portable package mode handling. Never retain setuid/setgid or inherited write grants.</summary>
public static class ExtensionPackageFileMode
{
    public static void Copy(string source, string destination)
    {
        if (!OperatingSystem.IsWindows()) Set(destination, File.GetUnixFileMode(source));
    }
    public static void FromArchive(string destination, int externalAttributes)
    {
        if (!OperatingSystem.IsWindows()) Set(destination, (UnixFileMode)(externalAttributes >> 16));
    }
    private static void Set(string destination, UnixFileMode original)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | (original & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)));
    }
}
