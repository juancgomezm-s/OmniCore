namespace OmniCore.Client;

/// <summary>Localized presentation keys emitted by Host maintenance and journal command DTOs.</summary>
public static class OperationalHostResources
{
    private static readonly (string Key, string Es, string En, string Fr, string It)[] Rows =
    [
        ("doctor.label.permissionProfile.Autonomous", "Autónomo", "Autonomous", "Autonome", "Autonomo"),
        ("doctor.label.permissionProfile.Balanced", "Equilibrado", "Balanced", "Équilibré", "Bilanciato"),
        ("doctor.label.permissionProfile.Conservative", "Conservador", "Conservative", "Prudent", "Conservatore"),
        ("doctor.label.providerFamily.OpenAIResponses", "OpenAI Responses", "OpenAI Responses", "OpenAI Responses", "OpenAI Responses"),
        ("doctor.label.providerFamily.AnthropicMessages", "Anthropic Messages", "Anthropic Messages", "Anthropic Messages", "Anthropic Messages"),
        ("doctor.label.providerFamily.OpenAiChatCompatible", "Chat compatible con OpenAI", "OpenAI-compatible chat", "Chat compatible avec OpenAI", "Chat compatibile con OpenAI"),
        ("doctor.label.configScope.User", "Usuario", "User", "Utilisateur", "Utente"),
        ("doctor.label.configScope.Project", "Proyecto", "Project", "Projet", "Progetto"),
        ("doctor.label.configScope.Workspace", "Workspace", "Workspace", "Espace de travail", "Workspace"),
        ("doctor.label.configScope.BuiltIn", "Integrado", "Built-in", "Intégré", "Integrato"),
        ("doctor.config.locked", "    {key}: se ignora en {scope} (bloqueada «{lock}» en {owner})", "    {key}: ignored in {scope} (locked \"{lock}\" in {owner})", "    {key} : ignorée dans {scope} (verrouillée « {lock} » dans {owner})", "    {key}: ignorata in {scope} (bloccata «{lock}» in {owner})"),
        ("cli.maintenance.session_purge.usage", "Uso: omni session purge <id-de-sesión> [--journal <ruta>] [--data-dir <directorio-de-usuario>]", "Usage: omni session purge <session-id> [--journal <path>] [--data-dir <user-data-directory>]", "Usage : omni session purge <id-de-session> [--journal <chemin>] [--data-dir <répertoire-utilisateur>]", "Uso: omni session purge <id-sessione> [--journal <percorso>] [--data-dir <directory-dati-utente>]"),
        ("cli.maintenance.unknown_option", "{command}: opción o argumento desconocido '{value}'", "{command}: unknown option or argument '{value}'", "{command} : option ou argument inconnu '{value}'", "{command}: opzione o argomento sconosciuto '{value}'"),
        ("cli.maintenance.invalid_session", "omni session purge: id de sesión no válido: '{value}'", "omni session purge: invalid session ID: '{value}'", "omni session purge : identifiant de session invalide : '{value}'", "omni session purge: ID sessione non valido: '{value}'"),
        ("cli.maintenance.journal_missing", "{command}: no se encontró el journal: {path}", "{command}: journal was not found: {path}", "{command} : journal introuvable : {path}", "{command}: journal non trovato: {path}"),
        ("cli.maintenance.session_missing", "omni session purge: la sesión no existe en el journal: {session}", "omni session purge: session was not found in the journal: {session}", "omni session purge : session introuvable dans le journal : {session}", "omni session purge: sessione non trovata nel journal: {session}"),
        ("cli.maintenance.session_busy", "omni session purge: la sesión está en uso por otro cliente: {session}", "omni session purge: session is in use by another client: {session}", "omni session purge : session utilisée par un autre client : {session}", "omni session purge: sessione in uso da un altro client: {session}"),
        ("cli.maintenance.session_purged", "session purge: sesión {session} purgada, {events} evento(s) eliminados (auditoría conservada)", "session purge: session {session} purged, {events} event(s) deleted (audit retained)", "session purge : session {session} purgée, {events} événement(s) supprimé(s) (audit conservé)", "session purge: sessione {session} eliminata, {events} evento/i rimossi (audit conservato)"),
        ("cli.maintenance.artifacts_orphaned", "Los artifacts quedan huérfanos; ejecuta 'omni gc' para recogerlos.", "Artifacts are now unreferenced; run 'omni gc' to collect them.", "Les artifacts ne sont plus référencés ; lancez 'omni gc' pour les récupérer.", "Gli artifact non sono più referenziati; esegui 'omni gc' per raccoglierli."),
        ("cli.maintenance.gc.usage", "Uso: omni gc [--scope workspace|user] [--journal <ruta> (workspace)] [--artifacts <dir>] [--grace-hours N] [--dry-run]", "Usage: omni gc [--scope workspace|user] [--journal <path> (workspace)] [--artifacts <dir>] [--grace-hours N] [--dry-run]", "Usage : omni gc [--scope workspace|user] [--journal <chemin> (workspace)] [--artifacts <dir>] [--grace-hours N] [--dry-run]", "Uso: omni gc [--scope workspace|user] [--journal <percorso> (workspace)] [--artifacts <dir>] [--grace-hours N] [--dry-run]"),
        ("cli.maintenance.gc.invalid_scope", "omni gc: el scope debe ser workspace o user; User no acepta --journal de workspace.", "omni gc: scope must be workspace or user; User does not accept a workspace --journal.", "omni gc : le scope doit être workspace ou user ; User n’accepte pas --journal de workspace.", "omni gc: lo scope deve essere workspace o user; User non accetta --journal di workspace."),
        ("cli.maintenance.gc.summary", "gc: {scanned} blobs, {live} vivos por referencia, {grace} dentro del periodo de gracia ({hours} h), {deleted}{deleteLabel}{bytes}", "gc: {scanned} blobs, {live} referenced, {grace} within grace period ({hours} h), {deleted}{deleteLabel}{bytes}", "gc : {scanned} blobs, {live} référencés, {grace} dans le délai de grâce ({hours} h), {deleted}{deleteLabel}{bytes}", "gc: {scanned} blob, {live} referenziati, {grace} nel periodo di grazia ({hours} h), {deleted}{deleteLabel}{bytes}"),
        ("cli.maintenance.gc.delete.dry", " candidato(s) a borrar (dry-run)", " deletion candidate(s) (dry-run)", " candidat(s) à la suppression (dry-run)", " candidato/i alla rimozione (dry-run)"),
        ("cli.maintenance.gc.delete.live", " borrado(s)", " deleted", " supprimé(s)", " eliminato/i"),
        ("cli.maintenance.gc.bytes", ", {bytes} bytes", ", {bytes} bytes", ", {bytes} octets", ", {bytes} byte"),
        ("cli.maintenance.gc.error", "omni gc: error: {error}", "omni gc: error: {error}", "omni gc : erreur : {error}", "omni gc: errore: {error}"),
        ("cli.maintenance.scope_user", "Alcance: User", "Scope: User", "Portée : User", "Ambito: User"),
        ("cli.maintenance.gc.dry_hint", "Vuelve a ejecutar sin --dry-run para borrar de verdad.", "Run again without --dry-run to delete items.", "Relancez sans --dry-run pour supprimer réellement les éléments.", "Esegui di nuovo senza --dry-run per eliminare davvero."),
        ("cli.maintenance.audit_purge.date_invalid", "omni audit purge: --before espera una fecha ISO (p. ej. 2026-01-01): '{value}'", "omni audit purge: --before expects an ISO date (e.g. 2026-01-01): '{value}'", "omni audit purge : --before attend une date ISO (ex. 2026-01-01) : '{value}'", "omni audit purge: --before richiede una data ISO (es. 2026-01-01): '{value}'"),
        ("cli.maintenance.audit_purge.usage", "Uso: omni audit purge [--data-dir <dir>] [--before <fecha>] [--retention-days N] [--dry-run]", "Usage: omni audit purge [--data-dir <dir>] [--before <date>] [--retention-days N] [--dry-run]", "Usage : omni audit purge [--data-dir <dir>] [--before <date>] [--retention-days N] [--dry-run]", "Uso: omni audit purge [--data-dir <dir>] [--before <data>] [--retention-days N] [--dry-run]"),
        ("cli.maintenance.audit_purge.summary", "audit purge: {examined} registro(s) examinado(s), {removed} eliminado(s), {kept} conservado(s){unparseable}{dryRun}", "audit purge: {examined} record(s) examined, {removed} removed, {kept} retained{unparseable}{dryRun}", "audit purge : {examined} enregistrement(s) examiné(s), {removed} supprimé(s), {kept} conservé(s){unparseable}{dryRun}", "audit purge: {examined} record esaminati, {removed} rimossi, {kept} conservati{unparseable}{dryRun}"),
        ("cli.maintenance.audit_purge.unparseable", " ({count} sin fecha legible, conservados)", " ({count} with unreadable dates, retained)", " ({count} avec date illisible, conservés)", " ({count} con data illeggibile, conservati)"),
        ("cli.maintenance.audit_purge.dry", " — dry-run (sin cambios)", " — dry-run (no changes)", " — dry-run (aucune modification)", " — dry-run (nessuna modifica)"),
        ("cli.maintenance.audit_path", "Registro de auditoría: {path}", "Audit log: {path}", "Journal d’audit : {path}", "Registro audit: {path}"),
        ("cli.maintenance.audit_purge.dry_hint", "Vuelve a ejecutar sin --dry-run para aplicar la retención.", "Run again without --dry-run to apply retention.", "Relancez sans --dry-run pour appliquer la rétention.", "Esegui di nuovo senza --dry-run per applicare la conservazione."),
        ("cli.journal.unknown_option", "omni verify-journal: opción desconocida '{value}'", "omni verify-journal: unknown option '{value}'", "omni verify-journal : option inconnue '{value}'", "omni verify-journal: opzione sconosciuta '{value}'"),
        ("cli.journal.invalid_session", "omni verify-journal: --session espera un GUID válido: '{value}'", "omni verify-journal: --session expects a valid GUID: '{value}'", "omni verify-journal : --session attend un GUID valide : '{value}'", "omni verify-journal: --session richiede un GUID valido: '{value}'"),
        ("cli.journal.usage", "Uso: omni verify-journal [ruta] [--session <id>] [--artifacts <dir>] [--json] [--max-issues N]", "Usage: omni verify-journal [path] [--session <id>] [--artifacts <dir>] [--json] [--max-issues N]", "Usage : omni verify-journal [chemin] [--session <id>] [--artifacts <dir>] [--json] [--max-issues N]", "Uso: omni verify-journal [percorso] [--session <id>] [--artifacts <dir>] [--json] [--max-issues N]"),
        ("cli.journal.usage.path", "  ruta        journal SQLite (por defecto {path})", "  path        SQLite journal (default {path})", "  chemin      journal SQLite (par défaut {path})", "  percorso    journal SQLite (predefinito {path})"),
        ("cli.journal.usage.artifacts", "  --artifacts raíz de blobs (por defecto, directorio del journal)", "  --artifacts blob root (defaults to the journal directory)", "  --artifacts racine des blobs (par défaut, répertoire du journal)", "  --artifacts radice dei blob (predefinita nella directory del journal)"),
        ("cli.journal.summary.ok", "verify-journal: {file} — {sessions} sesión(es), {events} evento(s), {refs} referencia(s) de artifacts — OK", "verify-journal: {file} — {sessions} session(s), {events} event(s), {refs} artifact reference(s) — OK", "verify-journal : {file} — {sessions} session(s), {events} événement(s), {refs} référence(s) d’artifact — OK", "verify-journal: {file} — {sessions} sessioni, {events} eventi, {refs} riferimenti artifact — OK"),
        ("cli.journal.summary.issues", "verify-journal: {file} — {sessions} sesión(es), {events} evento(s), {refs} referencia(s) de artifacts — {issues} problema(s) (se listan {listed})", "verify-journal: {file} — {sessions} session(s), {events} event(s), {refs} artifact reference(s) — {issues} issue(s) (listing {listed})", "verify-journal : {file} — {sessions} session(s), {events} événement(s), {refs} référence(s) d’artifact — {issues} problème(s) (liste : {listed})", "verify-journal: {file} — {sessions} sessioni, {events} eventi, {refs} riferimenti artifact — {issues} problemi (elencati {listed})"),
        ("cli.journal.issue", "[{code}] {where} — {detail}", "[{code}] {where} — {detail}", "[{code}] {where} — {detail}", "[{code}] {where} — {detail}"),
        ("cli.journal.issue.sequence", "seq={value}", "seq={value}", "seq={value}", "seq={value}"),
        ("cli.journal.issue.session", "sesión={value}", "session={value}", "session={value}", "sessione={value}"),
        ("cli.journal.issue.event", "evento={value}", "event={value}", "événement={value}", "evento={value}"),
        ("cli.journal.issue.field", "campo={value}", "field={value}", "champ={value}", "campo={value}"),
        ("cli.journal.issues_truncated", "… y {count} problema(s) más (usa --max-issues para listarlos).", "… and {count} more issue(s) (use --max-issues to list them).", "… et {count} problème(s) supplémentaire(s) (utilisez --max-issues pour les afficher).", "… e altri {count} problemi (usa --max-issues per elencarli)."),
    ];

    private static readonly string[] Locales = ["es", "en", "fr", "it"];
    private static readonly Dictionary<string, string>[] Catalog = Enumerable.Range(0, Locales.Length)
        .Select(index => Rows.ToDictionary(row => row.Key, row => index switch
        { 0 => row.Es, 1 => row.En, 2 => row.Fr, _ => row.It }, StringComparer.Ordinal)).ToArray();
    public static IReadOnlyCollection<string> Keys => Catalog[0].Keys.ToArray();
    internal static bool ContainsKey(string key) => Catalog[0].ContainsKey(key);
    internal static bool TryTranslate(string locale, string key, out string text)
    {
        var index = Array.IndexOf(Locales, locale);
        if (index >= 0 && Catalog[index].TryGetValue(key, out text!)) return true;
        text = "";
        return false;
    }
}
