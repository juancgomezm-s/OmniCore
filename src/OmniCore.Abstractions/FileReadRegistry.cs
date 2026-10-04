namespace OmniCore.Abstractions;

using System.Globalization;

using OmniCore.Domain;

/// <summary>
/// Registro de lecturas efectivas del modelo en un Run (ADR-0044 §5): pares (ruta →
/// token de versión) de las lecturas que tuvieron éxito y cuyo token fue VISIBLE al modelo
/// (filesystem.read expone el token real aunque el contenido se trunque o redacte, ADR-0018).
///
/// Un <c>filesystem.patch</c> solo se permite sobre una ruta que el modelo haya leído en el
/// MISMO Run, y exige el token exacto que esa lectura expuso (identidad de path/version):
///
///  - un read fallido o de otra ruta no habilita el parche;
///  - un token fabricado no coincide y se rechaza;
///  - un token de un Run anterior no está en el registro de este Run y se rechaza
///    (aislamiento entre Runs: cada <c>FileReadRegistry</c> es por-Run, nunca global).
///
/// La verificación del token contra el contenido vigente del archivo (STALE_WRITE) la sigue
/// haciendo <c>filesystem.patch</c> como defensa en profundidad, y el ToolRuntime + el
/// Permission Engine conservan su autoridad (INV-018): este registro solo RESTRINGE.
///
/// Está destinado a vivir dentro de la frontera de capacidad del modelo (per-Run); no es un
/// estado global entre sesiones.
///
/// SEMÁNTICA TRAS RESTART (ADR-0044 §5): el registro es en-memoria y por-Run. Tras un restart
/// del proceso, un Run reanudado conserva en su journal los eventos <c>toolcall.requested</c>
/// (filesystem.read + su route) y <c>toolcall.succeeded</c> que prueban QUE la lectura efectiva
/// ocurrió, pero NO el token de versión que se expuso al modelo: <c>toolcall.succeeded</c> solo
/// serializa el summary, no el token del marker. Reconstruir la identidad exacta path/version
/// tras restart exigiría perseguir el token en el journal (un cambio de contrato del evento).
/// Hasta que exista ese cambio, un restart vacía el registro y el runtime es CONSERVADOR: exige
/// releer el archivo en el nuevo proceso antes de permitir un patch, incluso si el journal del
/// mismo Run ya contenía la lectura. Esto es más estricto (nunca amplía) y no abre la brecha;
/// la verificación contra el contenido vigente (STALE_WRITE) sigue defendiendo en todo momento.
/// </summary>
public sealed class FileReadRegistry
{
    private readonly Dictionary<string, string> _readByPath;
    private readonly string? _workspaceRoot;

    /// <summary>
    /// Contabilidad de mutaciones de este Run (ADR-0044 §5, EPIC-021): presupuesto por Turn
    /// (archivos/lineas), totales por Run y validaciones post-edición pendientes. Viaja junto al
    /// registro de lecturas porque ambos son el estado per-Run de la política del modelo.
    /// </summary>
    public MutationLedger Ledger { get; }

    /// <summary>
    /// Registro vacío. Con <paramref name="workspaceRoot"/>, las rutas relativas se resuelven contra
    /// él y una ruta absoluta al mismo archivo cuenta como la misma lectura; sin raíz solo se
    /// unifican las grafías relativas (separadores, <c>./</c>, <c>..</c>). La comparación ignora
    /// mayúsculas solo en plataformas con sistema de archivos insensible (Windows, macOS).
    /// </summary>
    public FileReadRegistry(string? workspaceRoot = null)
    {
        _workspaceRoot = string.IsNullOrEmpty(workspaceRoot) ? null : workspaceRoot;
        _readByPath = new Dictionary<string, string>(
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        Ledger = new MutationLedger(this);
    }

    /// <summary>Forma canónica de la clave: nunca la grafía cruda del modelo.</summary>
    private string Canonical(string path)
    {
        if (_workspaceRoot is not null)
        {
            var full = Path.GetFullPath(path, _workspaceRoot);
            return Path.GetRelativePath(_workspaceRoot, full).Replace('\\', '/');
        }

        var parts = new List<string>();
        foreach (var seg in path.Replace('\\', '/').Split('/'))
        {
            if (seg.Length == 0 || seg == ".")
            {
                continue;
            }

            if (seg == ".." && parts.Count > 0 && parts[^1] != "..")
            {
                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(seg);
        }

        return (path.StartsWith('/') || path.StartsWith('\\') ? "/" : "") + string.Join('/', parts);
    }

    /// <summary>
    /// Registra una lectura EFECTIVA (éxito + token visible) de <c>path</c> con el token de
    /// versión <c>versionToken</c>. Solo lo invoca filesystem.read en su camino exitoso: un
    /// read fallido nunca llega aquí.
    /// </summary>
    public void RecordRead(string path, string versionToken)
    {
        _readByPath[Canonical(path)] = versionToken;
    }

    /// <summary>True si el modelo leyó efectivamente <c>path</c> en este Run.</summary>
    public bool HasRead(string path) => _readByPath.ContainsKey(Canonical(path));

    /// <summary>Token expuesto por la última lectura exitosa de <c>path</c> en este Run; null si no la hay.</summary>
    public string? VersionOf(string path) =>
        _readByPath.TryGetValue(Canonical(path), out var v) ? v : null;

    /// <summary>
    /// True solo si este Run contiene una lectura exitosa de EXACTAMENTE <c>path</c> cuyo token
    /// expuesto es <c>expectedVersion</c> (identidad de path/version). Un read de otra ruta,
    /// un read fallido, un token fabricado o un token de otro Run → false.
    /// </summary>
    public bool Matches(string path, string expectedVersion)
    {
        if (expectedVersion is null)
        {
            return false;
        }

        var exposed = VersionOf(path);
        if (exposed is null)
        {
            return false;
        }

        return exposed!.Equals(expectedVersion, StringComparison.Ordinal);
    }

    /// <summary>Número de rutas leídas efectivamente (diagnóstico / tests).</summary>
    public int Size() => _readByPath.Count;

    /// <summary>Forma canónica de una ruta como clave de contabilidad (misma normalización que las lecturas).</summary>
    internal string CanonicalKey(string path) => Canonical(path);
}

/// <summary>Validación post-edición exigida por la política y aún no verificada (ADR-0044 §5).</summary>
public sealed record PendingEditValidation(string Path, int Turn, ToolCallId? ToolCallId);

/// <summary>
/// Rechazo tipado del ledger de mutaciones (spec §71): código tipado + texto estable visible al
/// modelo. El texto conserva el prefijo "CODE: " histórico; el código viaja aparte para que
/// el runtime rasure por código, nunca parseando texto.
/// </summary>
public sealed record MutationRefusal(ToolErrorCode Code, string Message);

/// <summary>
/// Contabilidad de mutaciones del Run (ADR-0044 §5, EPIC-021): aplica los límites de la
/// política de mutación del modelo — <c>MaxFilesPerTurn</c>, <c>MaxChangedLinesPerTurn</c> y
/// <c>MaxRewriteRatio</c> — y registra las mutaciones publicadas y las validaciones
/// post-edición pendientes.
///
/// SEMÁNTICA POR TURN (ADR-0044 §5): los contadores de archivos y líneas se reinician al abrir
/// cada Turn (<see cref="BeginTurn"/>, invocada por el Engine); los totales por Run y las
/// validaciones pendientes NO se reinician nunca. Sin la señal de Turn la contabilidad acumula
/// monótonamente: la interpretación es conservadora (rechaza de más, nunca de menos).
///
/// Es un estado EN MEMORIA y POR RUN: vive dentro de la frontera de capacidad, nunca es un
/// estado global entre sesiones, y solo RESTRINGE (INV-018): el ToolRuntime y el Permission
/// Engine conservan su autoridad.
/// </summary>
public sealed class MutationLedger
{
    /// <summary>Código estable del rechazo por exceder un límite de mutación (spec §71).</summary>
    public const string LimitExceededCode = ToolErrorCode.LimitExceededCode;

    /// <summary>Código estable del rechazo por modo de mutación (spec §71).</summary>
    public const string MutationRefusedCode = ToolErrorCode.MutationRefusedCode;

    private readonly FileReadRegistry _registry;
    private readonly HashSet<string> _turnFiles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _runFiles = new(StringComparer.Ordinal);
    private readonly List<PendingEditValidation> _pendingValidations = new();
    private FileMutationPolicy? _policy;
    private int _turn;
    private int _turnChangedLines;
    private int _runChangedLines;
    private int _runMutations;

    internal MutationLedger(FileReadRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>Política de mutación efectiva de este Run; null si la frontera no la fijó.</summary>
    public FileMutationPolicy? MutationPolicy => _policy;

    /// <summary>Vincula la política de mutación efectiva del Run. La llama el
    /// <see cref="ModelCapabilityBoundary"/> al construirse; no debe re-vincularse a mitad de Run.</summary>
    public void Bind(FileMutationPolicy? policy) => _policy = policy;

    /// <summary>Número del Turn actual (0 antes del primero).</summary>
    public int TurnNumber => _turn;

    /// <summary>Rutas distintas mutadas en el Turn actual.</summary>
    public int FilesTouchedThisTurn => _turnFiles.Count;

    /// <summary>Líneas cambiadas (borradas + insertadas) acumuladas en el Turn actual.</summary>
    public int ChangedLinesThisTurn => _turnChangedLines;

    /// <summary>Rutas distintas mutadas en todo el Run.</summary>
    public int FilesTouchedThisRun => _runFiles.Count;

    /// <summary>Líneas cambiadas acumuladas en todo el Run.</summary>
    public int ChangedLinesThisRun => _runChangedLines;

    /// <summary>Mutaciones publicadas con éxito en todo el Run.</summary>
    public int MutationsThisRun => _runMutations;

    /// <summary>
    /// Señala el inicio de un Turn: reinicia la contabilidad POR TURN (archivos y líneas).
    /// Los totales por Run y las validaciones pendientes se conservan.
    /// </summary>
    public void BeginTurn()
    {
        _turn++;
        _turnFiles.Clear();
        _turnChangedLines = 0;
    }

    /// <summary>
    /// Evalúa una mutación PROPUESTA contra la política de este Run (ADR-0044 §5): el modo de
    /// mutación para la operación, el límite de archivos por Turn, el de líneas cambiadas por
    /// Turn y el ratio de reescritura de una sola operación. Devuelve el rechazo tipado
    /// (<see cref="MutationRefusal"/>, spec §71) o null si la permite.
    ///
    /// Es PURA: no toca estado. La mutación solo se contabiliza con <see cref="RecordMutation"/>
    /// una vez publicada con éxito; un rechazo no gasta presupuesto.
    /// </summary>
    /// <param name="operation">Operación exigida: PatchExisting, CreateFile o ReplaceFile.</param>
    /// <param name="path">Ruta relativa del destino (solo para el mensaje tipado).</param>
    /// <param name="targetExists">Si el archivo destino ya existe (reemplazo/parche vs creación).</param>
    /// <param name="deletedLines">Líneas del original que no sobreviven.</param>
    /// <param name="insertedLines">Líneas nuevas que no estaban en el original.</param>
    /// <param name="originalLines">Líneas del contenido original (0 en una creación).</param>
    public MutationRefusal? RefuseMutation(ModelToolCapability operation, string path, bool targetExists,
        int deletedLines, int insertedLines, int originalLines)
    {
        var policy = _policy;
        if (policy is null)
        {
            // Sin política activa (frontera sin fijar) esta capa no restringe: manda el techo.
            return null;
        }

        if (!FileMutationRules.ModeAllows(policy.Mode, operation))
        {
            return new MutationRefusal(ToolErrorCode.MutationRefused,
                MutationRefusedCode + ": el modo de mutación " + policy.Mode + " no permite "
                + OperationText(operation) + " (" + path + ").");
        }

        var key = _registry.CanonicalKey(path);

        // MaxFilesPerTurn: rutas distintas mutadas en el Turn actual. 0 = sin presupuesto
        // (coherente con FileMutationPolicy.CanMutateFiles).
        if (policy.MaxFilesPerTurn == 0)
        {
            return new MutationRefusal(ToolErrorCode.LimitExceeded,
                LimitExceededCode + ": la política del modelo no asigna presupuesto de archivos por Turn.");
        }

        if (!_turnFiles.Contains(key) && _turnFiles.Count >= policy.MaxFilesPerTurn)
        {
            return new MutationRefusal(ToolErrorCode.LimitExceeded,
                LimitExceededCode + ": la política del modelo permite como máximo "
                + policy.MaxFilesPerTurn + " archivo(s) por Turn y ya se mutaron " + _turnFiles.Count
                + " en este Turn. Continúa en el siguiente Turn.");
        }

        // MaxChangedLinesPerTurn: líneas borradas + insertadas acumuladas en el Turn.
        var delta = deletedLines + insertedLines;
        if (policy.MaxChangedLinesPerTurn == 0)
        {
            return new MutationRefusal(ToolErrorCode.LimitExceeded,
                LimitExceededCode + ": la política del modelo no asigna presupuesto de líneas por Turn.");
        }

        if (_turnChangedLines + delta > policy.MaxChangedLinesPerTurn)
        {
            return new MutationRefusal(ToolErrorCode.LimitExceeded,
                LimitExceededCode + ": la política del modelo permite como máximo "
                + policy.MaxChangedLinesPerTurn + " líneas cambiadas por Turn y esta mutación añade "
                + delta + " sobre las " + _turnChangedLines + " ya aplicadas. Continúa en el siguiente Turn.");
        }

        // MaxRewriteRatio: fracción del CONTENIDO ORIGINAL que una sola operación reescribe
        // (líneas originales que no sobreviven). No aplica a creaciones (no reescriben nada)
        // ni a archivos vacíos; 1.0 = reescritura completa permitida (FullAgent).
        if (targetExists && originalLines > 0 && policy.MaxRewriteRatio < 1.0)
        {
            var ratio = deletedLines / (double)originalLines;
            if (ratio > policy.MaxRewriteRatio)
            {
                return new MutationRefusal(ToolErrorCode.LimitExceeded,
                    LimitExceededCode + ": la política del modelo permite reescribir como máximo el "
                    + policy.MaxRewriteRatio.ToString("0.##", CultureInfo.InvariantCulture)
                    + " del archivo por operación y esta reescribe " + deletedLines + " de " + originalLines
                    + " líneas (" + ratio.ToString("0.##", CultureInfo.InvariantCulture)
                    + "). Usa filesystem.patch para cambios localizados.");
            }
        }

        return null;
    }

    /// <summary>
    /// Contabiliza una mutación PUBLICADA con éxito (solo lo invocan los tools de mutación tras
    /// el efecto durable) y registra la validación post-edición si la política la exige
    /// (<c>RequirePostEditValidation</c>, ADR-0044 §5).
    /// </summary>
    public void RecordMutation(string path, int deletedLines, int insertedLines,
        ToolCallId? toolCallId = null)
    {
        var key = _registry.CanonicalKey(path);
        _turnFiles.Add(key);
        _runFiles.Add(key);
        var delta = deletedLines + insertedLines;
        _turnChangedLines += delta;
        _runChangedLines += delta;
        _runMutations++;

        if (_policy?.RequirePostEditValidation == true)
        {
            _pendingValidations.Add(new PendingEditValidation(path, _turn, toolCallId));
        }
    }

    /// <summary>Validaciones post-edición exigidas por la política y aún no verificadas.</summary>
    public IReadOnlyList<PendingEditValidation> PendingValidations() => _pendingValidations;

    /// <summary>Consume only the captured pre-gate edits, not edits published during a gate.</summary>
    public void ConsumePendingValidations(IReadOnlyList<PendingEditValidation> covered)
    {
        foreach (var edit in covered) _pendingValidations.Remove(edit);
    }

    /// <summary>Retira y devuelve las validaciones pendientes (las consume el Coder / los gates).</summary>
    public IReadOnlyList<PendingEditValidation> TakePendingValidations()
    {
        var taken = _pendingValidations.ToArray();
        _pendingValidations.Clear();
        return taken;
    }

    private static string OperationText(ModelToolCapability operation) => operation switch
    {
        ModelToolCapability.PatchExisting => "parchear un archivo existente",
        ModelToolCapability.CreateFile => "crear archivos",
        ModelToolCapability.ReplaceFile => "reemplazar archivos existentes",
        _ => operation.ToString(),
    };
}
