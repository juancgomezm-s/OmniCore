namespace OmniCore.Domain;

/// <summary>
/// Código tipado de error de una tool (spec §71). El spec enumera las categorías "como mínimo",
/// así que el conjunto es ABIERTO: no es un enum cerrado sino un valor de texto validado
/// (SCREAMING_SNAKE_CASE, empieza por letra, 1..64 caracteres) con constantes conocidas.
/// Dominio de ida y vuelta: <see cref="ToolResult.ErrorCode"/>, <see cref="ToolCallFailed.ErrorCode"/>
/// y <see cref="ToolCallRejected.ErrorCode"/> lo persisten en el journal; el runtime razona por
/// código, nunca parseando el texto visible al modelo (que viaja aparte, en Cause/Reason/Summary,
/// y es estable). Los códigos conocidos cubren las categorías de spec §71 que hoy tienen productor
/// más los cinco códigos históricos del M3.
/// </summary>
public sealed record ToolErrorCode
{
    /// <summary>Spec §71 ToolFailure: fallo genérico de la tool. Código por defecto de todo
    /// resultado de error sin código específico.</summary>
    public const string ToolFailureCode = "TOOL_FAILURE";

    /// <summary>Argumentos inválidos: esquema violado, JSON ilegible, ruta fuera del workspace,
    /// campo ausente, parche ambiguo, regex inválida (spec §71 InvalidArguments).</summary>
    public const string InvalidArgumentsCode = "INVALID_ARGUMENTS";

    /// <summary>Permiso denegado: Permission Engine o contexto de ejecución (rutas de secretos,
    /// artifact no referenciado por la sesión, consentimiento de sandbox no otorgado).</summary>
    public const string PermissionDeniedCode = "PERMISSION_DENIED";

    /// <summary>Fallo de proceso: no se pudo iniciar, esperar o el proceso terminó con salida
    /// distinta de cero (spec §71 ProcessFailure).</summary>
    public const string ProcessFailureCode = "PROCESS_FAILURE";

    /// <summary>Cancelación: la ejecución se canceló (el efecto puede ser parcial).</summary>
    public const string CancellationCode = "CANCELLATION";

    /// <summary>La frontera de capacidad del modelo (ADR-0044 §5) rehusó la llamada.</summary>
    public const string CapabilityRefusedCode = "CAPABILITY_REFUSED";

    /// <summary>La tool terminó sin declarar su efecto: reconciliación conservadora (ADR-0004 §2).</summary>
    public const string UnknownEffectCode = "UNKNOWN_EFFECT";

    /// <summary>La tool no existe en el catálogo o la política no la clasifica.</summary>
    public const string UnknownToolCode = "UNKNOWN_TOOL";

    /// <summary>El artifact requerido no existe en el almacén.</summary>
    public const string ArtifactMissingCode = "ARTIFACT_MISSING";

    /// <summary>El artifact existe pero no se puede leer (corrupto o ilegible).</summary>
    public const string ArtifactCorruptedCode = "ARTIFACT_CORRUPTED";

    /// <summary>La versión esperada del archivo no coincide: escritura sobre estado ya cambiado.</summary>
    public const string StaleWriteCode = "STALE_WRITE";

    /// <summary>La mutación exige una lectura previa registrada en el Run (ADR-0044 §5).</summary>
    public const string PriorReadRequiredCode = "PRIOR_READ_REQUIRED";

    /// <summary>El ledger de mutaciones rehusó la operación por límite de turnos/Run.</summary>
    public const string LimitExceededCode = "LIMIT_EXCEEDED";

    /// <summary>El ledger de mutaciones rehusó la operación porque el modo del Run no muta.</summary>
    public const string MutationRefusedCode = "MUTATION_REFUSED";

    /// <summary>Código por defecto: fallo genérico de tool (spec §71 ToolFailure).</summary>
    public static readonly ToolErrorCode ToolFailure = new(ToolFailureCode);

    /// <summary>Constantes tipadas, una por código conocido.</summary>
    public static readonly ToolErrorCode InvalidArguments = new(InvalidArgumentsCode);

    public static readonly ToolErrorCode PermissionDenied = new(PermissionDeniedCode);

    public static readonly ToolErrorCode ProcessFailure = new(ProcessFailureCode);

    public static readonly ToolErrorCode Cancellation = new(CancellationCode);

    public static readonly ToolErrorCode CapabilityRefused = new(CapabilityRefusedCode);

    public static readonly ToolErrorCode UnknownEffect = new(UnknownEffectCode);

    public static readonly ToolErrorCode UnknownTool = new(UnknownToolCode);

    public static readonly ToolErrorCode ArtifactMissing = new(ArtifactMissingCode);

    public static readonly ToolErrorCode ArtifactCorrupted = new(ArtifactCorruptedCode);

    public static readonly ToolErrorCode StaleWrite = new(StaleWriteCode);

    public static readonly ToolErrorCode PriorReadRequired = new(PriorReadRequiredCode);

    public static readonly ToolErrorCode LimitExceeded = new(LimitExceededCode);

    public static readonly ToolErrorCode MutationRefused = new(MutationRefusedCode);

    /// <summary>Valor validado (SCREAMING_SNAKE_CASE). Igualdad por valor de record.</summary>
    public string Value { get; }

    /// <summary>
    /// Valida y crea. Lanza <see cref="ArgumentException"/> si el valor no es un código válido:
    /// solo letras, dígitos y guion bajo, en mayúsculas, empezando por letra, 1..64 caracteres.
    /// Es un error de programación (los productores usan las constantes), no un fallo de dominio.
    /// </summary>
    public ToolErrorCode(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            throw new ArgumentException("código de error de tool vacío o mayor de 64 caracteres", nameof(value));
        }

        if (!char.IsAsciiLetter(value[0]))
        {
            throw new ArgumentException("el código de error de tool debe empezar por letra: '" + value + "'",
                nameof(value));
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                throw new ArgumentException(
                    "código de error de tool con carácter no permitido '" + c + "' en '" + value +
                    "' (solo A-Z, 0-9 y _)", nameof(value));
            }

            if (char.IsAsciiLetter(c) && (c < 'A' || c > 'Z'))
            {
                throw new ArgumentException(
                    "el código de error de tool debe ir en mayúsculas: '" + value + "'", nameof(value));
            }
        }

        Value = value;
    }

    /// <summary>Fábrica por valor conocido; alias de lectura del constructor validado.</summary>
    public static ToolErrorCode Of(string value) => new(value);

    public override string ToString() => Value;
}
