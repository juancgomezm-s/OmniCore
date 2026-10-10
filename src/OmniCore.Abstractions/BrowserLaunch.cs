namespace OmniCore.Abstractions;

/// <summary>
/// Abre la URL de autorización en el navegador del usuario (ADR-0038 §2). Es la única vía por la
/// que el login OAuth llega a un navegador: el resto del flujo no depende de la plataforma.
///
/// La implementación vive en Infrastructure, no en Models ni en Host: elegir entre
/// <c>cmd /c start</c>, <c>xdg-open</c> o <c>open</c> es conocimiento de sistema operativo, y las
/// abstracciones de plataforma se concentran ahí (ADR-0038 §2). Los tests usan un launcher falso:
/// ningún test abre navegadores (CLAUDE.md: priorizar tests deterministas).
/// </summary>
public interface IBrowserLauncher
{
    /// <summary>
    /// Intenta abrir <paramref name="url"/>. Devuelve un resultado tipado: si no hay navegador,
    /// el login sigue por el camino manual en vez de quedarse esperando un callback que nunca
    /// va a llegar.
    /// </summary>
    Task<BrowserLaunchResult> LaunchAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>Resultado de intentar abrir el navegador.</summary>
public readonly struct BrowserLaunchResult
{
    private BrowserLaunchResult(bool launched, BrowserLaunchFailure? failure)
    {
        Launched = launched;
        Failure = failure;
    }

    public bool Launched { get; }

    public BrowserLaunchFailure? Failure { get; }

    public static BrowserLaunchResult Ok() => new(true, null);

    public static BrowserLaunchResult Failed(BrowserLaunchFailure failure) => new(false, failure);
}

/// <summary>Causas por las que no se pudo abrir el navegador, tipadas (spec §71).</summary>
public enum BrowserLaunchFailure
{
    /// <summary>No se encontró ningún lanzador de navegador en el sistema.</summary>
    NoLauncher,

    /// <summary>El lanzador existió pero devolvió error.</summary>
    LauncherFailed,

    /// <summary>La URL no es apta para abrirla (esquema distinto de https, por ejemplo).</summary>
    UnsupportedUrl,
}
