using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OmniCore.Models;

namespace OmniCore.Host;

/// <summary>
/// Lock de refresco OAuth entre procesos. Combina dos primitivas porque ninguna basta:
///
/// <list type="bullet">
/// <item><c>Mutex</c> named serializa los procesos de la MISMA máquina con nombre compartido
/// (Windows lo resuelve en el kernel; fuera de Windows se emula con un archivo de bloqueo).
/// Es el mismo enfoque que ChatGptSubscriptionAuthProvider (ADR-0011 §3.4).</item>
/// <item>Un archivo de bloqueo en el directorio de datos da la serialización cross-platform y
/// deja un rastro inspeccionable (<c>omni doctor</c>) de qué proceso lo tiene.</item>
/// </list>
///
/// La espera es finita: si otro proceso no libera en <see cref="DefaultTimeout"/>, esto falla con
/// <see cref="ClaudeOAuthLockException"/> en vez de colgar un turno entero. Claude Code reintenta
/// cinco veces con backoff (authAlias.ts <c>pt6</c>); aquí se traduce a reintentos con jitter y
/// mismo límite.
/// </summary>
public sealed class ClaudeOAuthRefreshLock : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private const int MaxAttempts = 5;

    private readonly string _lockFilePath;
    private readonly string? _mutexName;
    private FileStream? _stream;
    private MutexHandle? _mutex;
    private int _disposed;

    private ClaudeOAuthRefreshLock(string lockFilePath, string? mutexName)
    {
        _lockFilePath = lockFilePath;
        _mutexName = mutexName;
    }

    /// <summary>Ruta del archivo de bloqueo, para diagnóstico.</summary>
    public string LockFilePath => _lockFilePath;

    /// <summary>
    /// Factoria para el coordinador de refresco de Models, que recibe el lock como funcion
    /// inyectada (asi se testea la concurrencia sin tocar disco). Devuelve el adapter que espera
    /// <see cref="ClaudeOAuthRefreshCoordinator"/>.
    /// </summary>
    public static Func<string, CancellationToken, Task<IAsyncDisposable>> ForDataDirectory(
        string dataDirectory, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return async (secretRef, ct) => await AcquireAsync(dataDirectory, secretRef, ct, timeout)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Adquiere el lock de un provider. Lanza <see cref="OperationCanceledException"/> si se
    /// cancela la llamada, y <see cref="ClaudeOAuthLockException"/> si otro proceso no libera.
    /// </summary>
    public static async Task<ClaudeOAuthRefreshLock> AcquireAsync(
        string dataDirectory,
        string secretRef,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);

        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, LockFileName(secretRef));

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var acquired = await TryAcquireAsync(path, secretRef, ct).ConfigureAwait(false);
            if (acquired is not null)
            {
                return acquired;
            }

            if (attempt >= MaxAttempts || DateTime.UtcNow >= deadline)
            {
                throw new ClaudeOAuthLockException(secretRef, path, attempt);
            }

            // Backoff con jitter: dos procesos que chocan contra el lock a la vez no deben
            // reintentar al unísono (authAlias.ts usa 1000 ms + Math.random()*1000).
            var delay = TimeSpan.FromMilliseconds(1000 + Random.Shared.Next(1000));
            var remaining = deadline - DateTime.UtcNow;
            if (remaining < delay)
            {
                delay = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
            }

            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private static string LockFileName(string secretRef) =>
        "claude-oauth-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secretRef)))[..16].ToLowerInvariant()
        + ".lock";

    private static async Task<ClaudeOAuthRefreshLock?> TryAcquireAsync(
        string path, string secretRef, CancellationToken ct)
    {
        // FileShare.Read: la exclusion entre escritores la pone el Mutex (Windows) o el propio
        // intento de abrir para escribir (fuera), y asi un tercero — omni doctor — puede leer el
        // PID del poseedor mientras el lock esta tomado. Con FileShare.None el diagnostico era
        // imposible justo cuando hacia falta.
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.Write,
            Share = FileShare.Read,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, options);
        }
        catch (IOException)
        {
            // Otro proceso tiene el archivo abierto exclusivamente.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            stream?.Dispose();
            return null;
        }

        MutexHandle? mutex = null;
        var transferred = false;
        try
        {
            // En Windows el Mutex named es el que serializa de verdad entre procesos; el archivo
            // aporta la misma garantía fuera y deja rastro inspeccionable.
            mutex = MutexHandle.TryAcquire(Path.GetFullPath(path));
            if (mutex is null)
            {
                return null;
            }

            var payload = Encoding.UTF8.GetBytes(
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "@" + DateTime.UtcNow.ToString("O"));
            stream.SetLength(0);
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            var lease = new ClaudeOAuthRefreshLock(path, mutex.Name)
            {
                _stream = stream,
                _mutex = mutex,
            };
            transferred = true;
            return lease;
        }
        catch
        {
            mutex?.Release();
            throw;
        }
        finally
        {
            // A rejected mutex must not leave its already-open file handle behind.
            if (!transferred) stream.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_stream is not null)
        {
            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // El otro proceso ya lo borró: soltar el lock igual.
            }

            _stream = null;
        }

        _mutex?.Release();
        _mutex = null;

        // Keep the stable lock path. Deleting after releasing can unlink a new holder's
        // file on Unix, allowing another writer to lock a different inode at the same path.
    }
}

/// <summary>
/// Named mutex owned and released by one dedicated thread. Async continuations only signal
/// that thread; mutex ownership must never migrate to an async disposer thread.
/// </summary>
internal sealed class MutexHandle
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private MutexHandle(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public static MutexHandle? TryAcquire(string secretRef)
    {
        var name = "OmniCore.ClaudeOAuth." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secretRef)));
        var handle = new MutexHandle(name);
        var acquired = new TaskCompletionSource<MutexHandle?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Thread(() =>
        {
            try
            {
                using var mutex = new Mutex(initiallyOwned: false, name: name, createdNew: out _);
                var owns = false;
                try
                {
                    try { owns = mutex.WaitOne(TimeSpan.Zero); }
                    catch (AbandonedMutexException) { owns = true; }
                    acquired.SetResult(owns ? handle : null);
                    if (owns) handle._release.Task.GetAwaiter().GetResult();
                }
                finally { if (owns) mutex.ReleaseMutex(); }
                handle._released.TrySetResult();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                acquired.TrySetResult(null);
                handle._released.TrySetResult();
            }
            catch (Exception ex)
            {
                acquired.TrySetException(ex);
                handle._released.TrySetException(ex);
            }
        }) { IsBackground = true, Name = "OmniCore OAuth mutex owner" };
        owner.Start();
        return acquired.Task.GetAwaiter().GetResult();
    }

    public void Release()
    {
        _release.TrySetResult();
        _released.Task.GetAwaiter().GetResult();
    }
}

/// <summary>No se pudo adquirir el lock de refresco tras reintentar.</summary>
public sealed class ClaudeOAuthLockException : ClaudeOAuthException
{
    public ClaudeOAuthLockException(string secretRef, string lockFilePath, int attempts)
        : base($"El refresco de OAuth de '{secretRef}' está ocupado por otro proceso "
            + $"({attempts} intentos). Bloqueo: {lockFilePath}")
    {
        Attempts = attempts;
        LockFilePath = lockFilePath;
    }

    public int Attempts { get; }

    public string LockFilePath { get; }
}
