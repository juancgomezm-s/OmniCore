namespace OmniCore.Abstractions;

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
    private readonly Dictionary<string, string> _readByPath = new();

    /// <summary>
    /// Registra una lectura EFECTIVA (éxito + token visible) de <c>path</c> con el token de
    /// versión <c>versionToken</c>. Solo lo invoca filesystem.read en su camino exitoso: un
    /// read fallido nunca llega aquí.
    /// </summary>
    public void RecordRead(string path, string versionToken)
    {
        _readByPath[path] = versionToken;
    }

    /// <summary>True si el modelo leyó efectivamente <c>path</c> en este Run.</summary>
    public bool HasRead(string path) => _readByPath.ContainsKey(path);

    /// <summary>Token expuesto por la última lectura exitosa de <c>path</c> en este Run; null si no la hay.</summary>
    public string? VersionOf(string path) =>
        _readByPath.TryGetValue(path, out var v) ? v : null;

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
}