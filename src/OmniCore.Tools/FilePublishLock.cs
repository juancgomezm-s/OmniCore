namespace OmniCore.Tools;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Cross-process lock for the final compare-and-publish section of filesystem mutations.
/// The lock is keyed by the normalized full path; non-cooperating processes are still able to
/// write between the final comparison and rename (see the tools' publish documentation).
/// </summary>
internal sealed class FilePublishLock : IDisposable
{
    private readonly Mutex _mutex;
    private bool _acquired;

    private FilePublishLock(Mutex mutex) => _mutex = mutex;

    public static FilePublishLock Acquire(string path, CancellationToken cancellationToken)
    {
        var canonicalPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            canonicalPath = canonicalPath.ToUpperInvariant();
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath)));
        var mutex = new Mutex(false, "OmniCore-FilePublish-" + hash);
        var lease = new FilePublishLock(mutex);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (mutex.WaitOne(TimeSpan.FromMilliseconds(50)))
                    {
                        lease._acquired = true;
                        cancellationToken.ThrowIfCancellationRequested();
                        return lease;
                    }
                }
                catch (AbandonedMutexException)
                {
                    // The abandoned mutex is acquired by this thread. Treat it as held and
                    // continue; the caller still performs the version check before publishing.
                    lease._acquired = true;
                    cancellationToken.ThrowIfCancellationRequested();
                    return lease;
                }
            }
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_acquired)
        {
            _acquired = false;
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
