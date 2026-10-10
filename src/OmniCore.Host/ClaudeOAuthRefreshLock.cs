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
        try
        {
            // En Windows el Mutex named es el que serializa de verdad entre procesos; el archivo
            // aporta la misma garantía fuera y deja rastro inspeccionable.
            mutex = MutexHandle.TryAcquire(secretRef);
            if (mutex is null)
            {
                return null;
            }

            var payload = Encoding.UTF8.GetBytes(
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "@" + DateTime.UtcNow.ToString("O"));
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            return new ClaudeOAuthRefreshLock(path, mutex.Name)
            {
                _stream = stream,
                _mutex = mutex,
            };
        }
        catch
        {
            mutex?.Release();
            stream.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
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

        try
        {
            if (File.Exists(_lockFilePath))
            {
                File.Delete(_lockFilePath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Mutex named solo donde existe (Windows). Fuera de Windows devuelve un handle sin mutex, y la
/// serialización la sostiene el archivo de bloqueo.
/// </summary>
internal sealed class MutexHandle
{
    private readonly Mutex? _mutex;

    private MutexHandle(Mutex? mutex, string name)
    {
        _mutex = mutex;
        Name = name;
    }

    public string Name { get; }

    public static MutexHandle? TryAcquire(string secretRef)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new MutexHandle(null, "n/a");
        }

        var name = "OmniCore.ClaudeOAuth." + new string(secretRef.Where(char.IsLetterOrDigit).ToArray());
        var mutex = new Mutex(initiallyOwned: false, name: name, createdNew: out _);
        try
        {
            if (mutex.WaitOne(TimeSpan.Zero))
            {
                return new MutexHandle(mutex, name);
            }

            mutex.Dispose();
            return null;
        }
        catch (AbandonedMutexException)
        {
            // El dueño anterior murió sin liberar: se asume el lock y se sigue.
            return new MutexHandle(mutex, name);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            mutex.Dispose();
            return null;
        }
    }

    public void Release()
    {
        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // No éramos dueños (lock abandonado y tomado por otro): no hay nada que liberar.
        }
        finally
        {
            _mutex.Dispose();
        }
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
