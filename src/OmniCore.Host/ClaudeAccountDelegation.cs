namespace OmniCore.Host;

using System.Globalization;
using System.Text.Json;
using OmniCore.Abstractions;
using OmniCore.Execution;

/// <summary>
/// Solicitud de delegación puntual al binario oficial de Claude Code en modo no interactivo
/// (<c>claude -p … --output-format json</c>; página oficial «Run Claude Code programmatically»,
/// consultada 2026-10-08). Es la integración soportada que devuelve respuestas y actividad reales
/// a OmniCore (ADR-0011 §10.7 reevaluación; ADR-0012 §2 como referencia de invocación).
/// <para>El prompt viaja como un argv tipado (nunca por shell, INV-015). Sin prompt no hay
/// delegación: el campo es obligatorio y no vacío.</para>
/// </summary>
public sealed record ClaudeDelegationRequest(
    string Prompt,
    string? WorkingDirectory = null,
    string? Model = null,
    int? MaxTurns = null,
    double? MaxBudgetUsd = null,
    string? JsonSchema = null)
{
    /// <summary>WorkingDirectory efectivo: el directorio actual si no se declara uno.</summary>
    public string EffectiveWorkingDirectory => string.IsNullOrWhiteSpace(WorkingDirectory)
        ? Environment.CurrentDirectory
        : WorkingDirectory;
}

/// <summary>Uso de tokens informado por el <c>result</c> JSON del CLI oficial (estimación del
/// cliente). Solo se copian los campos documentados; los ausentes quedan null, nunca inventados.</summary>
public sealed record ClaudeDelegationUsage(
    int? InputTokens,
    int? OutputTokens,
    int? CacheReadInputTokens,
    int? CacheCreationInputTokens);

/// <summary>Terminal de una delegación puntual. <see cref="Kind"/> distingue el desenlace del
/// proceso; los datos del <c>result</c> JSON documentado llegan en los campos tipados. La salida
/// del hijo que no sea el mensaje <c>result</c> (stderr, texto suelto) NUNCA se copia: solo se
/// transmiten los campos parseados del esquema documentado.</summary>
public sealed record ClaudeDelegationOutcome(
    ClaudeDelegationTerminalKind Kind,
    bool Success,
    string? Response,
    string? StructuredOutputJson,
    string? SessionId,
    int? NumTurns,
    double? TotalCostUsd,
    ClaudeDelegationUsage? Usage,
    IReadOnlyList<string> Errors,
    string? Subtype,
    int? ExitCode,
    string? Detail);

/// <summary>Desenlace terminal de la delegación puntual.</summary>
public enum ClaudeDelegationTerminalKind
{
    /// <summary>El CLI terminó y se obtuvo un mensaje <c>result</c> parseable.</summary>
    Completed,

    /// <summary>El CLI terminó pero no se obtuvo un mensaje <c>result</c> parseable.</summary>
    NoResultMessage,

    /// <summary>El tope de tiempo venció: el árbol del proceso se cortó.</summary>
    TimedOut,

    /// <summary>La cancelación del llamador cortó el árbol de procesos.</summary>
    Cancelled,
}

/// <summary>
/// Delegación puntual al CLI oficial de Claude Code (rev. de reevaluación OAuth, 2026-10-08):
/// el binario sin modificar corre su propio loop agentic con la sesión del usuario y OmniCore
/// recoge la respuesta real desde el stdout capturado con <c>--output-format json</c>.
/// <list type="bullet">
/// <item>Autenticación: el CLI usa su propia credencial (login de suscripción del usuario,
/// gestionada por Anthropic dentro del binario; ADR-0012). OmniCore no aporta, lee ni guarda
/// credenciales de Claude y no hace ninguna llamada HTTP.</item>
/// <item>Sin fallback a la vía de API pagada: el delta de entorno del hijo es vacío y la
/// allowlist de plataforma (ADR-0037) excluye <c>ANTHROPIC_API_KEY</c>/<c>CLAUDE_CODE_OAUTH_TOKEN</c>,
/// así que el modo <c>-p</c> (que usaría una API key del entorno si estuviera presente, según la
/// página oficial «Authentication») no puede heredarla de OmniCore. Este servicio no referencia
/// credential stores ni secretos.</item>
/// <item>Permisos: <c>--permission-mode dontAsk</c> deniega todo lo que pediría aprobación;
/// sin <c>--allowedTools</c> no se permite ninguna tool con prompt. Es un acote del bloque de
/// cuenta, no la lane delegada de ADR-0012 (M7): sin worktree, sin TaskPacket, sin scheduler.</item>
/// <item>Bounded: timeout propio (10 min por defecto), <c>stdout</c> capturado con el tope del
/// runtime, cancelación por árbol completo (<c>CancelTree</c>, ADR-0012 §5).</item>
/// <item>Refresh/expiry: no aplica en OmniCore — el CLI oficial refresca y expira sus propios
/// tokens dentro de su flujo (referencia: coordinación de refresh en el source de referencia);
/// OmniCore nunca ve tokens, así que no tiene lógica de refresh ni de expiración.</item>
/// </list>
/// </summary>
public sealed class ClaudeAccountDelegationService
{
    /// <summary>Tope por defecto de una delegación puntual (una tarea, no una sesión).</summary>
    internal static readonly TimeSpan DefaultDelegationTimeout = TimeSpan.FromMinutes(10);

    private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment =
        new Dictionary<string, string>();

    private readonly IProcessRuntime _runtime;
    private readonly ClaudeCodeRuntimeLocator _locator;
    private readonly TimeSpan _defaultTimeout;

    public ClaudeAccountDelegationService(IProcessRuntime? runtime = null,
        ClaudeCodeRuntimeLocator? locator = null, TimeSpan? defaultTimeout = null)
    {
        _runtime = runtime ?? SystemProcessRuntime.Instance();
        _locator = locator ?? new ClaudeCodeRuntimeLocator();
        _defaultTimeout = defaultTimeout ?? DefaultDelegationTimeout;
    }

    /// <summary>
    /// Ejecuta la delegación puntual y devuelve el desenlace tipado. Nunca lanza excepciones por
    /// fallos del CLI: se reportan como <see cref="ClaudeDelegationTerminalKind"/> + campos de
    /// error. Solo lanzan errores tipados del bloque: runtime ausente
    /// (<c>runtimeNotInstalled</c>), spawn imposible (<c>runtimeLaunchFailed</c>) o token de
    /// cancelación ya vencido (<see cref="OperationCanceledException"/>).
    /// </summary>
    public async System.Threading.Tasks.Task<ClaudeDelegationOutcome> RunAsync(
        ClaudeDelegationRequest request, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("El prompt de delegación es obligatorio.", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var executable = _locator.Locate()
            ?? throw new ClaudeAccountException("runtimeNotInstalled",
                "No se encontró el binario 'claude' en PATH ni en el directorio del instalador nativo.");

        var handle = Launch(executable, request, cancellationToken);
        var result = await System.Threading.Tasks.Task.Run(
            () => _runtime.Wait(handle, timeout ?? _defaultTimeout, cancellationToken),
            CancellationToken.None).ConfigureAwait(false);

        var cancelled = cancellationToken.IsCancellationRequested;
        if (result.TimedOut || cancelled)
        {
            _runtime.CancelTree(handle);
        }

        if (cancelled)
        {
            return new(ClaudeDelegationTerminalKind.Cancelled, false, null, null, null, null, null,
                null, [], null, null, "cancelled");
        }

        if (result.TimedOut)
        {
            return new(ClaudeDelegationTerminalKind.TimedOut, false, null, null, null, null, null,
                null, [], null, null, "timeout");
        }

        return ParseResultMessage(result.Stdout, result.ExitCode);
    }

    /// <summary>
    /// Lanza <c>claude -p &lt;prompt&gt; --output-format json …</c> con stdio capturado y delta
    /// de entorno vacío. Un shim npm <c>.cmd</c> (Windows) se delega a <c>cmd.exe /d /s /c</c>
    /// con argv tipado, igual que las sesiones interactivas.
    /// </summary>
    private ProcessHandle Launch(string executable, ClaudeDelegationRequest request,
        CancellationToken cancellationToken)
    {
        var args = BuildArguments(request);
        var launch = new ProcessLaunch(executable, args, request.EffectiveWorkingDirectory,
            EmptyEnvironment, captureOutput: true);
        try
        {
            return _runtime.Launch(ClaudeAccountService.WrapWindowsShim(launch), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ClaudeAccountException("runtimeLaunchFailed",
                "No se pudo lanzar el binario oficial de Claude Code (" + ex.GetType().Name + ").");
        }
    }

    internal static IReadOnlyList<string> BuildArguments(ClaudeDelegationRequest request)
    {
        // Bases documentadas: -p (modo no interactivo), --output-format json (resultado único
        // parseable), --permission-mode dontAsk + --permission-prompts none (deniega todo lo que
        // pediría aprobación; ADR-0012 §2, verificado con el CLI instalado). Sin --bare: ese
        // modo nunca lee el login por suscripción (página oficial «Start faster with bare mode»).
        var args = new List<string>
        {
            "-p", request.Prompt,
            "--output-format", "json",
            "--permission-mode", "dontAsk",
            "--permission-prompts", "none",
        };
        if (request.Model is not null)
        {
            args.Add("--model");
            args.Add(request.Model);
        }

        if (request.MaxTurns is int maxTurns)
        {
            args.Add("--max-turns");
            args.Add(maxTurns.ToString(CultureInfo.InvariantCulture));
        }

        if (request.MaxBudgetUsd is double budget)
        {
            args.Add("--max-budget-usd");
            args.Add(budget.ToString("R", CultureInfo.InvariantCulture));
        }

        if (request.JsonSchema is not null)
        {
            args.Add("--json-schema");
            args.Add(request.JsonSchema);
        }

        return args;
    }

    /// <summary>
    /// Parsea el stdout del CLI en el mensaje <c>result</c> único documentado (esquema
    /// <c>SDKResultMessage</c>: subtype success | error_during_execution | error_max_turns |
    /// error_max_budget_usd | error_max_structured_output_retries). Si el stdout no contiene el
    /// mensaje (fallo de arranque, flag rechazado), el resultado es
    /// <see cref="ClaudeDelegationTerminalKind.NoResultMessage"/> y la salida del hijo NUNCA se
    /// copia al outcome: solo exit code y detalle técnico.
    /// </summary>
    internal static ClaudeDelegationOutcome ParseResultMessage(string? stdout, int exitCode)
    {
        using var document = TryParseResult(stdout);
        if (document is null)
        {
            return new(ClaudeDelegationTerminalKind.NoResultMessage, false, null, null, null, null,
                null, null, [], null, exitCode, "noResultMessage exit " + exitCode);
        }

        var root = document.RootElement;
        var subtype = root.TryGetProperty("subtype", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;
        var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
        var success = subtype == "success" && !isError && exitCode == 0;

        var errors = new List<string>();
        if (root.TryGetProperty("errors", out var errorsValue) && errorsValue.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in errorsValue.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var error = item.GetString();
                    if (!string.IsNullOrEmpty(error)) errors.Add(error);
                }
            }
        }

        return new(
            ClaudeDelegationTerminalKind.Completed,
            success,
            success && root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null,
            root.TryGetProperty("structured_output", out var so) && so.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? so.GetRawText()
                : null,
            root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String
                ? sid.GetString()
                : null,
            root.TryGetProperty("num_turns", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var turns)
                ? turns
                : null,
            root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number && cost.TryGetDouble(out var usd)
                ? usd
                : null,
            ParseUsage(root),
            errors,
            subtype,
            exitCode,
            "exit " + exitCode);
    }

    private static JsonDocument? TryParseResult(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        var trimmed = stdout.Trim();
        if (TryParseDocument(trimmed, out var document) && IsResultMessage(document.RootElement))
        {
            return document;
        }

        // El CLI puede acompañar el JSON con avisos: se acepta cualquier línea que sea el
        // mensaje result completo (se busca de atrás hacia delante, el result es lo último).
        var lines = trimmed.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (TryParseDocument(line, out document) && IsResultMessage(document.RootElement))
            {
                return document;
            }
        }

        return null;
    }

    private static bool IsResultMessage(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String &&
        t.GetString() == "result";

    private static bool TryParseDocument(string text, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static ClaudeDelegationUsage? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new(
            PositiveOrNull(usage, "input_tokens"),
            PositiveOrNull(usage, "output_tokens"),
            PositiveOrNull(usage, "cache_read_input_tokens"),
            PositiveOrNull(usage, "cache_creation_input_tokens"));
    }

    private static int? PositiveOrNull(JsonElement usage, string name)
    {
        if (!usage.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetInt32(out var tokens) && tokens > 0 ? tokens : null;
    }
}
