using OmniCore.Protocol;

namespace OmniCore.Host;

/// <summary>Headless turns announce the same principal identity as the TUI, off the drawing loop.</summary>
internal sealed class PrincipalMailboxPresence : IDisposable
{
    private readonly object _gate = new();
    private readonly CompanionCoordinator _hub;
    private readonly CompanionInstance _instance;
    private readonly Timer? _timer;
    private bool _disposed;
    internal PrincipalMailboxPresence(CompanionCoordinator hub, CompanionInstance instance, bool headless)
    {
        _hub = hub; _instance = instance; Touch();
        if (headless) _timer = new(_ => Touch(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }
    private void Touch()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { _hub.Heartbeat(_instance); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { }
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true; _timer?.Dispose();
            if (_timer is not null)
                try { _hub.Disconnect(_instance.Id); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { }
        }
    }
}
