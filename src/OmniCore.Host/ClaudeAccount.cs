namespace OmniCore.Host;

using System.Text;
using OmniCore.Abstractions;
using OmniCore.Domain;
using OmniCore.Execution;

/// <summary>
/// Planes de cuenta de Claude relevantes para la decisión de conexión (bloque de cuentas).
/// Solo los planes que la documentación oficial de Claude Code declara como tipos de cuenta
/// válidos pueden iniciar sesión; Free no lo es y no se le promete autenticación.
/// </summary>
public enum ClaudePlan
{
    Unknown,
    Free,
    Pro,
    Max,
    Team,
    Enterprise,
}

/// <summary>
/// Idoneidad de un plan para la integración autenticada con Claude Code. Separada de la
/// decisión de menú: el menú puede mostrar la fila de cuenta siempre; la idoneidad es un hecho
/// documentado por Anthropic, y el plan real del usuario solo lo puede medir su propia sesión
/// interactiva (<c>/status</c>) — este backend nunca lo infiere.
/// </summary>
public enum ClaudeAccountEntitlement
{
    /// <summary>La documentación oficial admite este plan para Claude Code.</summary>
    Eligible,

    /// <summary>La documentación oficial no ofrece este plan para Claude Code.</summary>
    NotEligible,

    /// <summary>Sin plan conocido: se requiere medición por la sesión del usuario.</summary>
    RequiresMeasurement,
}

/// <summary>Idoneidad documentada de un plan, con la fuente primaria exacta que la respalda.</summary>
public sealed record ClaudePlanEligibility(ClaudePlan Plan, ClaudeAccountEntitlement Entitlement, string Source);

/// <summary>
/// Matriz de idoneidad Free/Pro para Claude Code. Fuentes primarias oficiales (consultadas
/// hoy, 2026-10-08, sin autenticación): la documentación «Authentication» de Claude Code
/// (code.claude.com/docs/en/authentication) lista los tipos de cuenta con login — suscripción
/// Claude Pro o Max, Claude for Teams/Enterprise, Claude Console, providers de nube y el
/// gateway — y el token de <c>claude setup-token</c> «requires a Pro, Max, Team, or Enterprise
/// plan»; la página «Legal and compliance» (code.claude.com/docs/en/legal-and-compliance)
/// declara además que OAuth «is intended exclusively for purchasers of Claude Free, Pro, Max,
/// Team, and Enterprise subscription plans» y que a los desarrolladores de terceros no se les
/// permite ofrecer login de Claude.ai en sus propias aplicaciones ni enrutar requests con
/// credenciales de planes Free/Pro/Max. Complemento de política local: ADR-0011 §3.3 prohíbe
/// reutilizar OAuth de suscripción en terceros, y ADR-0012 fija la única vía legítima (el
/// binario oficial sin modificar). Referencia de comportamiento (no contractual, sin copia):
/// Icarus603/claude-code @ 7c918f78 (derivado de sourcemap, UNLICENSED; ver
/// <c>oauth-contract.md</c> del handoff).
/// </summary>
public static class ClaudeAccountEligibility
{
    /// <summary>Fuentes primarias oficiales citadas en cada fila de idoneidad.</summary>
    public const string PrimarySource =
        "code.claude.com/docs/en/authentication + code.claude.com/docs/en/legal-and-compliance (consultados 2026-10-08)";

    /// <summary>
    /// Fragmento de la fuente primaria que define qué planes pueden autenticarse por OAuth:
    /// el token de <c>claude setup-token</c> «requires a Pro, Max, Team, or Enterprise plan».
    /// </summary>
    public const string SetupTokenPlanRequirement =
        "claude setup-token requires a Pro, Max, Team, or Enterprise plan (code.claude.com/docs/en/authentication)";

    /// <summary>
    /// Fragmento de la fuente primaria de la restricción de terceros (página «Legal and
    /// compliance», consultada 2026-10-08): Anthropic no permite a desarrolladores de terceros
    /// ofrecer login de Claude.ai en sus propias aplicaciones, ni enrutar requests con
    /// credenciales de planes Free/Pro/Max en nombre de sus usuarios, ni recolectar,
    /// almacenar o intermediar credenciales o tokens de sesión de Claude.ai.
    /// </summary>
    public const string ThirdPartyProhibition =
        "Anthropic does not permit third-party developers to offer Claude.ai login into their own applications or to route requests through Free, Pro, or Max plan credentials (code.claude.com/docs/en/legal-and-compliance, 2026-10-08)";

    /// <summary>
    /// Idoneidad documentada por plan. Free → <see cref="ClaudeAccountEntitlement.NotEligible"/>
    /// (la cuenta Free no es un tipo de login de Claude Code); Pro/Max/Team/Enterprise →
    /// <see cref="ClaudeAccountEntitlement.Eligible"/>; desconocido → RequiresMeasurement
    /// (el plan real del usuario se verifica con su propia sesión <c>/status</c>).
    /// </summary>
    public static ClaudePlanEligibility ForPlan(ClaudePlan plan) => plan switch
    {
        ClaudePlan.Free => new(plan, ClaudeAccountEntitlement.NotEligible,
            PrimarySource + "; " + ThirdPartyProhibition +
            "; ADR-0011 §3.3: el plan Free no es un tipo de cuenta de Claude Code."),
        ClaudePlan.Pro or ClaudePlan.Max or ClaudePlan.Team or ClaudePlan.Enterprise =>
            new(plan, ClaudeAccountEntitlement.Eligible, PrimarySource + "; " + SetupTokenPlanRequirement),
        _ => new(plan, ClaudeAccountEntitlement.RequiresMeasurement,
            PrimarySource + "; el plan real del usuario solo lo muestra su sesión (/status)."),
    };
}

/// <summary>
/// Modo de la sesión interactiva del CLI oficial de Claude Code. Login, Logout y OpenSession
/// lanzan el mismo binario interactivo porque Anthropic solo soporta <c>/login</c> y
/// <c>/logout</c> dentro de su propia sesión; la diferencia es la intención que el menú
/// presenta. SetupToken lanza <c>claude setup-token</c>, cuyo token imprime solo en la
/// terminal del usuario (nunca se captura, nunca se guarda en OmniCore).
/// </summary>
public enum ClaudeAccountSessionMode
{
    /// <summary>Abrir la sesión interactiva para que el usuario complete el login de Anthropic.</summary>
    Login,

    /// <summary>Abrir la sesión interactiva para que el usuario ejecute <c>/logout</c> (revoca la credencial).</summary>
    Logout,

    /// <summary>Abrir la sesión interactiva sin una intención de cuenta concreta.</summary>
    OpenSession,

    /// <summary>Lanzar <c>claude setup-token</c> (token de larga vida; el usuario lo gestiona).</summary>
    SetupToken,
}

/// <summary>
/// Estado medible de la integración de cuenta de Claude Code, sin secretos y sin valores
/// inventados: presencia del binario y su versión. NUNCA informa estado de login, plan, cuota
/// ni ventanas de rate limit: medirlos honestamente exige la sesión interactiva del propio
/// usuario (<c>/status</c>) o un probe delegado (ADR-0012), y este backend no fabrica un
/// «Connected» solo porque el CLI esté instalado.
/// </summary>
public sealed record ClaudeAccountStatus(
    bool RuntimePresent,
    string? RuntimeVersion,
    ClaudePlanEligibility SubscriptionEligibility,
    string? Detail);

/// <summary>
/// Resultado de una sesión interactiva terminada. Nunca contiene salida del proceso hijo: los
/// modos interactivos se lanzan con stdio SIN capturar, así que ningún token puede llegar a
/// OmniCore (ni a logs, journal ni artifacts) a través de este registro.
/// </summary>
public sealed record ClaudeSessionOutcome(int? ExitCode, bool TimedOut, bool Cancelled, string? Detail);

/// <summary>Error tipado del bloque de cuenta Claude; el cliente muestra <see cref="UserMessage"/>.</summary>
public sealed class ClaudeAccountException : InvalidOperationException
{
    /// <summary>Código estable del error (p. ej. <c>runtimeNotInstalled</c>); el recurso del
    /// cliente es <c>claude.auth.&lt;kind&gt;</c>. El menú aporta los textos es/en.</summary>
    public string Kind { get; }

    public LocalizedText UserMessage { get; }

    public ClaudeAccountException(string kind, string message) : base(message)
    {
        Kind = kind;
        UserMessage = LocalizedText.Of("claude.auth." + kind);
    }
}

/// <summary>
/// Localizador del binario oficial <c>claude</c> (ADR-0012): entradas de PATH con
/// <c>claude</c>/<c>claude.exe</c>/<c>claude.cmd</c> y, como reserva, el directorio del
/// instalador nativo <c>&lt;profile&gt;/.local/bin</c>. Es de solo lectura y por sí mismo NO
/// integra nada: la integración real la hace <see cref="ClaudeAccountService"/> lanzando el
/// proceso; un locator sin lanzamiento no acredita ninguna sesión.
/// </summary>
public sealed class ClaudeCodeRuntimeLocator
{
    private readonly Func<string?> _pathProvider;
    private readonly Func<string?> _profileProvider;

    public ClaudeCodeRuntimeLocator(Func<string?>? pathProvider = null, Func<string?>? profileProvider = null)
    {
        _pathProvider = pathProvider ?? (() => Environment.GetEnvironmentVariable("PATH"));
        _profileProvider = profileProvider ?? (() => Environment.GetEnvironmentVariable(
            OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME"));
    }

    /// <summary>Devuelve la primera ubicación existente, o null si no hay binario instalado.</summary>
    public string? Locate()
    {
        var path = _pathProvider();
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (var candidate in Candidates(raw))
                {
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }

        var profile = _profileProvider();
        if (!string.IsNullOrWhiteSpace(profile))
        {
            var nativeDirectory = Path.Combine(profile, ".local", "bin");
            foreach (var candidate in Candidates(nativeDirectory))
            {
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(directory, "claude.exe");
            yield return Path.Combine(directory, "claude.cmd");
        }

        yield return Path.Combine(directory, "claude");
    }
}

/// <summary>
/// Backend del bloque de cuentas para Claude (ADR-0011 §3.3, ADR-0012): estado del runtime
/// oficial y lanzamiento de sesiones interactivas reales con las abstracciones de proceso
/// existentes (<c>IProcessRuntime</c>, ADR-0038 §2). No hay OAuth propio, no hay client IDs
/// inventados, no hay llamadas HTTP y no hay credenciales de Claude leídas, guardadas ni
/// mostradas: el login/logout lo completa el usuario en el binario oficial sin modificar.
/// <list type="bullet">
/// <item>Todas las sesiones se lanzan con stdio SIN capturar: el token de <c>setup-token</c>
/// solo aparece en la terminal del usuario; el resultado nunca lleva salida del hijo.</item>
/// <item>El entorno del hijo es el delta vacío sobre la allowlist de plataforma (ADR-0037):
/// <c>CLAUDE_CODE_OAUTH_TOKEN</c>/<c>ANTHROPIC_API_KEY</c> nunca se heredan de OmniCore.</item>
/// <item>La cancelación corta el árbol de procesos (graceful → kill, ADR-0012 §5).</item>
/// <item>Sin fallback a la vía de API pagada: este backend no toca la credencial
/// <c>anthropic</c> ni hace ninguna consulta de red.</item>
/// </list>
/// </summary>
public sealed class ClaudeAccountService
{
    /// <summary>Tope por defecto de una sesión interactiva (timeout del cliente: 60 min).</summary>
    internal static readonly TimeSpan DefaultInteractiveTimeout = TimeSpan.FromMinutes(60);

    /// <summary>Tope del probe <c>claude --version</c>: es un comando que termina solo.</summary>
    internal static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(30);

    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment =
        new Dictionary<string, string>();

    private readonly IProcessRuntime _runtime;
    private readonly ClaudeCodeRuntimeLocator _locator;
    private readonly TimeSpan _interactiveTimeout;
    private readonly TimeSpan _versionProbeTimeout;

    public ClaudeAccountService(IProcessRuntime? runtime = null, ClaudeCodeRuntimeLocator? locator = null,
        TimeSpan? interactiveTimeout = null, TimeSpan? versionProbeTimeout = null)
    {
        _runtime = runtime ?? SystemProcessRuntime.Instance();
        _locator = locator ?? new ClaudeCodeRuntimeLocator();
        _interactiveTimeout = interactiveTimeout ?? DefaultInteractiveTimeout;
        _versionProbeTimeout = versionProbeTimeout ?? VersionProbeTimeout;
    }

    /// <summary>
    /// Estado real: localiza el binario y, si existe, mide su versión con
    /// <c>claude --version</c> (capturado, acotado a 30 s). Sin binario → RuntimePresent=false,
    /// sin excepción y sin estados fingidos. La salida del probe solo se usa para extraer la
    /// primera secuencia <c>mayor.menor.parche</c>; nunca se copia texto del hijo al detalle.
    /// </summary>
    public async System.Threading.Tasks.Task<ClaudeAccountStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = _locator.Locate();
        if (executable is null)
        {
            return new(false, null, ClaudeAccountEligibility.ForPlan(ClaudePlan.Unknown), null);
        }

        var handle = Launch(new ProcessLaunch(executable, ["--version"], Environment.CurrentDirectory,
            EmptyEnvironment, captureOutput: true), cancellationToken);
        var result = await System.Threading.Tasks.Task.Run(
            () => _runtime.Wait(handle, _versionProbeTimeout, cancellationToken),
            CancellationToken.None).ConfigureAwait(false);
        if (result.TimedOut || cancellationToken.IsCancellationRequested)
        {
            _runtime.CancelTree(handle);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var version = !result.TimedOut && result.ExitCode == 0
            ? ParseVersion(result.Stdout) ?? ParseVersion(result.Stderr)
            : null;
        string? detail = null;
        if (version is null)
        {
            detail = result.TimedOut ? "versionProbeTimedOut" : "versionProbeExit " + result.ExitCode;
        }

        return new(true, version, ClaudeAccountEligibility.ForPlan(ClaudePlan.Unknown), detail);
    }

    /// <summary>
    /// Lanza la sesión interactiva oficial según el modo. El proceso comparte la consola del
    /// usuario (stdio no capturado) y OmniCore nunca escribe en su stdin: login/logout/setup
    /// son acciones del usuario dentro de la interfaz de Anthropic. Timeout por defecto de
    /// 60 min; al vencer o cancelar se corta el árbol (<c>CancelTree</c>) y el resultado lo
    /// informa como <c>TimedOut</c>/<c>Cancelled</c>. El resultado no contiene salida del hijo.
    /// </summary>
    public async System.Threading.Tasks.Task<ClaudeSessionOutcome> RunInteractiveAsync(
        ClaudeAccountSessionMode mode, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = _locator.Locate()
            ?? throw new ClaudeAccountException("runtimeNotInstalled",
                "No se encontró el binario 'claude' en PATH ni en el directorio del instalador nativo.");
        var args = mode switch
        {
            ClaudeAccountSessionMode.SetupToken => (IReadOnlyList<string>)["setup-token"],
            ClaudeAccountSessionMode.Login or ClaudeAccountSessionMode.Logout
                or ClaudeAccountSessionMode.OpenSession => (IReadOnlyList<string>)[],
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };

        var handle = Launch(new ProcessLaunch(executable, args, Environment.CurrentDirectory,
            EmptyEnvironment, captureOutput: false), cancellationToken);
        var outcome = await System.Threading.Tasks.Task.Run(
            () => _runtime.Wait(handle, timeout ?? _interactiveTimeout, cancellationToken),
            CancellationToken.None).ConfigureAwait(false);

        var cancelled = cancellationToken.IsCancellationRequested;
        if (outcome.TimedOut || cancelled) _runtime.CancelTree(handle); // Idempotente; explícito para fakes.
        return new(
            cancelled || outcome.TimedOut ? null : outcome.ExitCode,
            outcome.TimedOut && !cancelled,
            cancelled,
            cancelled ? "cancelled" : outcome.TimedOut ? "timeout" : "exit " + outcome.ExitCode);
    }

    /// <summary>
    /// Lanza el proceso vía <c>IProcessRuntime</c>. Un binario <c>.cmd</c> (instalación npm en
    /// Windows) no arranca como ejecutable directo: se delega a <c>cmd.exe /d /s /c</c> con
    /// argv tipado, manteniendo el mismo entorno restringido y sin capturar stdio. La
    /// integridad de la identidad es del binario oficial: OmniCore no cambia su invocación.
    /// </summary>
    private ProcessHandle Launch(ProcessLaunch launch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return _runtime.Launch(WrapWindowsShim(launch), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ClaudeAccountException("runtimeLaunchFailed",
                "No se pudo lanzar el binario oficial de Claude Code (" + ex.GetType().Name + ").");
        }
    }

    /// <summary>Envuelve un lanzamiento de shim npm <c>.cmd</c> en Windows vía
    /// <c>cmd.exe /d /s /c</c> con argv tipado (compartido con la delegación puntual). En el
    /// resto de plataformas devuelve el lanzamiento intacto.</summary>
    internal static ProcessLaunch WrapWindowsShim(ProcessLaunch launch)
    {
        return OperatingSystem.IsWindows() && launch.Executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? new ProcessLaunch("cmd.exe",
                ["/d", "/s", "/c", launch.Executable, .. launch.Args],
                launch.WorkingDirectory, launch.Environment, launch.CaptureOutput)
            : launch;
    }

    /// <summary>
    /// Extrae la primera versión <c>x.y.z</c> de la salida del probe con un escáner manual
    /// (sin regex dinámico): solo se devuelve la propia secuencia de versión, nunca el resto
    /// de la salida del hijo.
    /// </summary>
    internal static string? ParseVersion(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        var segment = new StringBuilder(16);
        for (var i = 0; i <= output.Length; i++)
        {
            var c = i < output.Length ? output[i] : ' ';
            if (char.IsAsciiDigit(c) || c == '.')
            {
                segment.Append(c);
                continue;
            }

            if (TryAcceptVersion(segment, out var version)) return version;
            segment.Clear();
        }

        return null;
    }

    private static bool TryAcceptVersion(StringBuilder segment, out string? version)
    {
        version = null;
        if (segment.Length == 0) return false;
        var text = segment.ToString();
        var parts = text.Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length is 0 or > 4) || parts.Any(p => !p.All(char.IsAsciiDigit)))
        {
            return false;
        }

        version = text;
        return true;
    }
}
