namespace OmniCore.Host;

/// <summary>Coordinates cooperating atomic writers of a single Host-owned configuration file.</summary>
internal static class ConfigurationWriteLease
{
    internal static FileStream Acquire(string settingsFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFile);
        var file = Path.GetFullPath(settingsFile);
        CheckLinks(Path.GetDirectoryName(file)!);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        CheckLinks(Path.GetDirectoryName(file)!);
        var lease = new FileStream(file + ".write.lock", FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        try
        {
            CheckLinks(file);
            CheckLinks(file + ".write.lock");
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal static void EnsureNoLinks(string path) => CheckLinks(path);

    private static void CheckLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("ConfigurationLinkRejected");
    }
}
