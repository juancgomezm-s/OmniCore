using System.Diagnostics;
using OmniCore.Abstractions;

namespace OmniCore.Infrastructure;

/// <summary>
/// Abre una URL en el navegador por defecto del sistema (ADR-0038 §2). Implementación por
/// plataforma concentrada aquí: Models y Host solo ven <see cref="IBrowserLauncher"/>.
///
/// Proceso directo con argv de lista, sin shell intermedio (ADR-0015): en Windows se lanza
/// <c>rundll32 url.dll,FileProtocolHandler &lt;url&gt;</c> en vez de <c>cmd /c start</c>, porque
/// un texto de shell re-parsearía la URL — que contiene <c>&amp;</c> entre parámetros y es justo
/// el character que cmd interpreta.
/// </summary>
public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    private readonly bool _allowHttp;
    private readonly bool _allowLocalHtml;
    // OAuth callers retain HTTPS-only behavior; explicit web-link actions may use HTTP.
    public SystemBrowserLauncher(bool allowHttp = false, bool allowLocalHtml = false)
    { _allowHttp = allowHttp; _allowLocalHtml = allowLocalHtml; }

    public async Task<BrowserLaunchResult> LaunchAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        // OAuth stays HTTPS-only; explicit browser links may also use HTTP.
        var localReport = _allowLocalHtml && url.IsAbsoluteUri && url.IsFile && !url.IsUnc
            && url.Host.Length == 0 && url.Query.Length == 0 && !url.LocalPath.Any(char.IsControl)
            && string.Equals(Path.GetExtension(url.LocalPath), ".html", StringComparison.OrdinalIgnoreCase);
        if (!url.IsAbsoluteUri || url.OriginalString.Any(char.IsControl) || url.UserInfo.Length != 0
            || (url.Scheme != Uri.UriSchemeHttps && !(_allowHttp && url.Scheme == Uri.UriSchemeHttp) && !localReport))
        {
            return BrowserLaunchResult.Failed(BrowserLaunchFailure.UnsupportedUrl);
        }

        var (executable, arguments) = PlatformLauncher();
        if (executable is null)
        {
            return BrowserLaunchResult.Failed(BrowserLaunchFailure.NoLauncher);
        }

        try
        {
            var psi = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            psi.ArgumentList.Add(url.AbsoluteUri);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return BrowserLaunchResult.Failed(BrowserLaunchFailure.LauncherFailed);
            }

            // Algunos lanzadores (xdg-open) no terminan hasta que el navegador cierra la ventana,
            // así que no se espera a que salga: se le da un margen para que falle pronto si el
            // binario no está o rechaza la URL, y se sigue. El callback llega por su propio canal.
            var grace = TimeSpan.FromMilliseconds(750);
            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(grace, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Sigue vivo: el navegador abrió y el lanzador espera a que cierre. Para nosotros
                // ya es exito — el callback llega por su propio canal.
                return BrowserLaunchResult.Ok();
            }

            return process.ExitCode == 0
                ? BrowserLaunchResult.Ok()
                : BrowserLaunchResult.Failed(BrowserLaunchFailure.LauncherFailed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Binario inexistente o sin permisos: es un fallo accionable, no un motivo para
            // tumbar el login — el llamador cae al modo manual.
            _ = ex;
            return BrowserLaunchResult.Failed(BrowserLaunchFailure.NoLauncher);
        }
    }

    /// <summary>
    /// Lanzador por plataforma. Devuelve (ejecutable, argumentos previos a la URL).
    /// macOS es best-effort (ADR-0038 §1): <c>open</c> existe en todos los sistemas soportados.
    /// </summary>
    private static (string? Executable, string[] Arguments) PlatformLauncher()
    {
        if (OperatingSystem.IsWindows())
        {
            return ("rundll32.exe", ["url.dll,FileProtocolHandler"]);
        }

        if (OperatingSystem.IsLinux())
        {
            return Which.Find("xdg-open") is { } xdg ? (xdg, []) : (null, []);
        }

        if (OperatingSystem.IsMacOS())
        {
            return ("open", []);
        }

        return (null, []);
    }
}

/// <summary>Búsqueda mínima de un lanzador en PATH, sin tocar el cwd del workspace (ADR-0037 §6).</summary>
internal static class Which
{
    public static string? Find(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory.Trim(), executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
