namespace OmniCore.Client;

/// <summary>Localized, offline diagnostics and scoped maintenance copy.</summary>
public static class DiagnosticsPreferencesResources
{
    private static readonly (string Key, string Es, string En, string Fr, string It)[] Rows =
    [
        ("diagnosticsPreferences.title", "Diagnóstico y datos", "Diagnostics and data", "Diagnostic et données", "Diagnostica e dati"),
        ("diagnosticsPreferences.configuration_ok", "Sin errores de configuración detectados", "No configuration errors detected", "Aucune erreur de configuration détectée", "Nessun errore di configurazione rilevato"),
        ("diagnosticsPreferences.configuration_ok_detail", "Los archivos de configuración revisados son válidos.", "The reviewed configuration files are valid.", "Les fichiers de configuration examinés sont valides.", "I file di configurazione esaminati sono validi."),
        ("diagnosticsPreferences.configuration_issue", "Problema de configuración", "Configuration issue", "Problème de configuration", "Problema di configurazione"),
        ("diagnosticsPreferences.summary", "Modelos: {0} · proveedores: {1} · predeterminado: {2} · origen: {3}", "Models: {0} · providers: {1} · default: {2} · source: {3}", "Modèles : {0} · fournisseurs : {1} · défaut : {2} · source : {3}", "Modelli: {0} · provider: {1} · predefinito: {2} · origine: {3}"),
        ("diagnosticsPreferences.not_set", "sin definir", "not set", "non défini", "non impostato"),
        ("diagnosticsPreferences.default", "predeterminado", "default", "par défaut", "predefinito"),
        ("diagnosticsPreferences.path", "Ruta de configuración", "Configuration path", "Chemin de configuration", "Percorso configurazione"),
        ("diagnosticsPreferences.cli_only", "Sin fachada TUI; disponible por CLI", "No TUI facade; available through the CLI", "Pas de façade TUI ; disponible via la CLI", "Nessuna interfaccia TUI; disponibile da CLI"),
        ("diagnosticsPreferences.verify_journal", "Verificar registro", "Verify journal", "Vérifier le journal", "Verifica registro"),
        ("diagnosticsPreferences.journal_ok", "Journal íntegro · sesiones {0}, eventos {1}, problemas {2}", "Journal verified · sessions {0}, events {1}, issues {2}", "Journal vérifié · sessions {0}, événements {1}, problèmes {2}", "Journal verificato · sessioni {0}, eventi {1}, problemi {2}"),
        ("diagnosticsPreferences.journal_issues", "Journal con incidencias · sesiones {0}, eventos {1}, problemas {2}", "Journal has issues · sessions {0}, events {1}, issues {2}", "Le journal présente des problèmes · sessions {0}, événements {1}, problèmes {2}", "Il journal presenta problemi · sessioni {0}, eventi {1}, problemi {2}"),
        ("diagnosticsPreferences.preview_gc", "Revisar limpieza…", "Preview cleanup…", "Aperçu du nettoyage…", "Anteprima pulizia…"),
        ("diagnosticsPreferences.gc_preview", "Alcance: sólo datos de esta área de trabajo. Revisados {0}; referenciados {1}; protegidos por antigüedad {2}; potencialmente recuperables {3} bytes. Se volverá a comprobar antes de borrar.", "Scope: this workspace data only. Scanned {0}; referenced {1}; kept by age {2}; potentially reclaimable {3} bytes. References are checked again before deletion.", "Portée : données de cet espace uniquement. Examinés {0} ; référencés {1} ; conservés par ancienneté {2} ; récupérables {3} octets. Les références seront revérifiées avant suppression.", "Ambito: solo dati di quest’area di lavoro. Esaminati {0}; referenziati {1}; protetti dall’età {2}; recuperabili {3} byte. I riferimenti saranno ricontrollati prima della rimozione."),
        ("diagnosticsPreferences.gc_done", "Limpieza completada; bytes recuperables estimados: {0}.", "Cleanup completed; estimated reclaimable bytes: {0}.", "Nettoyage terminé ; octets récupérables estimés : {0}.", "Pulizia completata; byte recuperabili stimati: {0}."),
        ("diagnosticsPreferences.operation_failed", "La operación no se completó. No se muestran detalles internos.", "The operation did not complete. Internal details are hidden.", "L’opération n’a pas abouti. Les détails internes sont masqués.", "L’operazione non è riuscita. I dettagli interni sono nascosti."),
        ("diagnosticsPreferences.help", "La verificación es local y de sólo lectura. La limpieza sólo elimina objetos de esta área de trabajo sin referencias y fuera del periodo de gracia.", "Verification is local and read-only. Cleanup only removes unreferenced workspace blobs older than the grace period.", "La vérification est locale et en lecture seule. Le nettoyage supprime uniquement les objets de l’espace non référencés et au-delà du délai de grâce.", "La verifica è locale e di sola lettura. La pulizia rimuove solo oggetti dell’area di lavoro non referenziati e oltre il periodo di grazia."),
        ("diagnosticsPreferences.confirm_title", "Confirmar mantenimiento", "Confirm maintenance", "Confirmer la maintenance", "Conferma manutenzione"),
        ("diagnosticsPreferences.cancel", "Cancelar", "Cancel", "Annuler", "Annulla"),
        ("diagnosticsPreferences.confirm_gc", "Eliminar blobs identificados", "Delete identified blobs", "Supprimer les blobs identifiés", "Elimina blob identificati"),
        ("diagnosticsPreferences.back", "Volver", "Back", "Retour", "Indietro"),
        ("diagnosticsPreferences.file", "Archivo", "File", "Fichier", "File"),
        ("diagnosticsPreferences.key", "Clave", "Key", "Clé", "Chiave"),
        ("diagnosticsPreferences.line", "Línea", "Line", "Ligne", "Riga"),
        ("diagnosticsPreferences.issueCode", "Código", "Code", "Code", "Codice"),
        ("diagnosticsPreferences.sequence", "Secuencia", "Sequence", "Séquence", "Sequenza"),
        ("diagnosticsPreferences.eventType", "Tipo de evento", "Event type", "Type d’événement", "Tipo di evento"),
        ("diagnosticsPreferences.field", "Campo", "Field", "Champ", "Campo")
    ];
    private static readonly Dictionary<string, string>[] Catalog = Enumerable.Range(0, 4).Select(index =>
        Rows.ToDictionary(row => row.Key, row => index switch { 0 => row.Es, 1 => row.En, 2 => row.Fr, _ => row.It }, StringComparer.Ordinal)).ToArray();
    private static readonly string[] Locales = ["es", "en", "fr", "it"];
    public static IReadOnlyCollection<string> Keys => Catalog[0].Keys.ToArray();
    internal static bool ContainsKey(string key) => Catalog[0].ContainsKey(key);
    internal static bool TryTranslate(string locale, string key, out string text)
    {
        var index = Array.IndexOf(Locales, locale);
        if (index >= 0 && Catalog[index].TryGetValue(key, out text!)) return true;
        text = ""; return false;
    }
}
