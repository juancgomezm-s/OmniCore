namespace OmniCore.Infrastructure;

/// <summary>
/// Cross-process exclusion between CAS publication and mark-and-sweep. A completed PutText
/// refreshes the blob timestamp before releasing this lease; the GC's grace period then
/// protects the interval before the canonical event referencing the blob is committed.
/// </summary>
internal static class ArtifactStoreLease
{
    private const string LeaseFileName = ".artifact-gc.lease";

    public static FileStream Acquire(string dataDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, LeaseFileName);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                // Another process is publishing a blob or sweeping. Wait without allowing
                // cancellation to be swallowed, then retry the exclusive OS file lease.
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(25));
            }
        }
    }
}
