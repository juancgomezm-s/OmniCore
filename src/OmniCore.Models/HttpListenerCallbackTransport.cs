using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OmniCore.Models;

/// <summary>
/// Transporte de callback por HTTP loopback. Equivalente al createServer().listen(0,'localhost')
/// de auth-code-listener.ts:16-75.
///
/// Reservado deliberadamente el puerto con un TcpListener antes de abrir HttpListener: así el
/// número de puerto se conoce antes de construir la URL de authorize, sin la carrera de abrir y
/// cerrar el socket a medias.
///
/// En Windows, un prefijo http://localhost:&lt;puerto&gt;/ requiere una ACL de namespace en
/// http.sys. Si falta, StartAsync falla con OAuthTransportException en vez de colgarse: mejor
/// un fallo tipado y accionable que un login que nunca responde.
/// </summary>
public sealed class HttpListenerCallbackTransport : IClaudeOAuthCallbackTransport
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<ClaudeOAuthCallbackRequest, HttpListenerContext> _contexts = new();
    private int _started;
    private int _disposed;

    public int Port { get; private set; }

    public async Task StartAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("El transporte ya está iniciado.");
        }

        ct.ThrowIfCancellationRequested();

        try
        {
            Port = ReserveFreeLoopbackPort();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
        }
        catch (OAuthTransportException)
        {
            throw;
        }
        catch (HttpListenerException ex)
        {
            // 5 =拒绝访问 (access denied): falta la ACL de namespace en Windows.
            throw new OAuthTransportException(
                ex.ErrorCode == 5 ? OAuthTransportFailure.AccessDenied : OAuthTransportFailure.ListenFailed,
                Port,
                ex);
        }
        catch (SocketException ex)
        {
            throw new OAuthTransportException(OAuthTransportFailure.ListenFailed, Port, ex);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static int ReserveFreeLoopbackPort()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        try
        {
            return ((IPEndPoint)tcp.LocalEndpoint).Port;
        }
        finally
        {
            tcp.Stop();
        }
    }

    public async ValueTask<ClaudeOAuthCallbackRequest> AcceptAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        HttpListenerContext ctx;
        try
        {
            ctx = await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 995 || ex.ErrorCode == 1236)
        {
            // El listener se cerró mientras esperábamos (cancelación del login).
            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException("El transporte de callback se cerró.", ex, ct);
        }
        catch (ObjectDisposedException)
        {
            ct.ThrowIfCancellationRequested();
            throw new OperationCanceledException("El transporte de callback se cerró.", ct);
        }

        var url = ctx.Request.Url;
        var query = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (url is not null)
        {
            var parsed = System.Web.HttpUtility.ParseQueryString(url.Query);
            foreach (var key in parsed.AllKeys)
            {
                if (key is not null)
                {
                    query[key] = parsed[key];
                }
            }
        }

        var request = new ClaudeOAuthCallbackRequest(url?.AbsolutePath ?? "/", query);
        _contexts[request] = ctx;
        return request;
    }

    public async ValueTask RespondAsync(
        ClaudeOAuthCallbackRequest request,
        ClaudeOAuthCallbackResponse response,
        CancellationToken ct)
    {
        if (!_contexts.TryRemove(request, out var ctx))
        {
            throw new InvalidOperationException("No hay contexto para esa petición.");
        }

        var res = ctx.Response;
        res.StatusCode = response.Status;

        if (response.Location is not null)
        {
            res.RedirectLocation = response.Location;
        }

        if (response.Body is not null)
        {
            var buffer = Encoding.UTF8.GetBytes(response.Body);
            res.ContentType = "text/plain; charset=utf-8";
            res.ContentLength64 = buffer.Length;
            await res.OutputStream.WriteAsync(buffer, ct).ConfigureAwait(false);
        }

        res.Close();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _contexts.Clear();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // Ya cerrado por el cierre del proceso.
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Causas de fallo al montar el transporte, tipadas (spec §71).</summary>
public enum OAuthTransportFailure
{
    /// <summary>El SO rechazó la escucha por permisos (Windows sin ACL de namespace en http.sys).</summary>
    AccessDenied,

    /// <summary>No se pudo reservar ni abrir el puerto.</summary>
    ListenFailed,
}

/// <summary>
/// Fallo del transporte de callback. Lleva el puerto intentado: ayuda a decidir si reintentar
/// con uno fijo o caer al modo manual. No lleva secretos (INV-016).
/// </summary>
public sealed class OAuthTransportException : ClaudeOAuthException
{
    public OAuthTransportException(OAuthTransportFailure failure, int port, Exception? inner = null)
        : base(Describe(failure, port), inner)
    {
        Failure = failure;
        Port = port;
    }

    public OAuthTransportFailure Failure { get; }

    public int Port { get; }

    private static string Describe(OAuthTransportFailure failure, int port) => failure switch
    {
        OAuthTransportFailure.AccessDenied =>
            $"Permiso denegado al escuchar en http://localhost:{port}/ para el callback OAuth. " +
            "En Windows requiere una ACL de namespace en http.sys; puede caer al modo manual.",
        _ => $"No se pudo abrir el puerto del callback OAuth ({port}).",
    };
}
