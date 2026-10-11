namespace OmniCore.Host;

/// <summary>Coordinates cooperating writers of the User provider/model YAML files.</summary>
internal static class ProviderConfigurationLease
{
    /// <summary>
    /// Acquires the shared configuration lease used while reading and publishing
    /// <c>providers.yaml</c> and/or <c>models.yaml</c>.
    /// </summary>
    internal static FileStream Acquire(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        var directory = Path.GetFullPath(configDirectory);
        CheckLinks(directory);
        Directory.CreateDirectory(directory);
        CheckLinks(directory);
        var path = Path.Combine(directory, "providers.yaml.write.lock");
        var lease = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
            FileOptions.DeleteOnClose);
        try
        {
            CheckLinks(path);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static void CheckLinks(string path)
    {
        for (var item = Path.GetFullPath(path); item is not null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item))
                && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("ProviderConfigurationLinkRejected");
    }
}
