namespace OmniCore.Client;

public static class UndoResources
{
    private static readonly IReadOnlyDictionary<string, string[]> Values = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["undo.title"] = ["Deshacer un cambio", "Undo a change", "Rétablir un changement", "Ripristina una modifica"],
        ["undo.recover"] = ["Clasificar recuperación", "Classify recovery", "Classer la récupération", "Classifica recupero"],
        ["undo.recovery_required"] = ["Hay un intento interrumpido. Confirmar sólo clasifica sus archivos y registra el resultado; conserva las ediciones actuales y no reintenta escrituras.", "An attempt was interrupted. Confirmation only classifies its files and records the result; it preserves current edits and never retries writes.", "Une tentative a été interrompue. La confirmation classe les fichiers et enregistre le résultat ; elle préserve les modifications actuelles sans réécriture.", "Un tentativo è stato interrotto. La conferma classifica i file e registra l’esito; conserva le modifiche attuali e non ripete scritture."],
        ["undo.recovery_unknown"] = ["Recuperación clasificada como incierta; se conservaron los archivos actuales. Revisa los conflictos manualmente.", "Recovery classified as uncertain; current files were preserved. Review conflicts manually.", "Récupération classée incertaine ; fichiers actuels préservés. Vérifiez les conflits manuellement.", "Recupero classificato come incerto; file attuali conservati. Verifica manualmente i conflitti."],
        ["undo.recovery_done"] = ["Recuperación registrada sin reintentar escrituras.", "Recovery recorded without retrying writes.", "Récupération enregistrée sans réécriture.", "Recupero registrato senza ripetere scritture."],
        ["undo.diff.unavailable"] = ["Vista textual no disponible por estado, formato o límite de tamaño; revisa las versiones antes de confirmar.", "Text preview unavailable due to state, format or size limit; review the versions before confirming.", "Aperçu texte indisponible selon l’état, le format ou la taille ; vérifiez les versions avant de confirmer.", "Anteprima testuale non disponibile per stato, formato o dimensione; verifica le versioni prima di confermare."],
        ["client.action.workspace.undo"] = ["Deshacer un cambio", "Undo a change", "Rétablir un changement", "Ripristina una modifica"],
        ["undo.help"] = ["Revisa un efecto registrado. Sólo se restauran sus archivos; los cambios ajenos bloquean la operación.", "Review a recorded effect. Only its files are restored; unrelated edits block the operation.", "Vérifiez un effet enregistré. Seuls ses fichiers sont restaurés ; les modifications tierces bloquent l’opération.", "Esamina un effetto registrato. Vengono ripristinati solo i suoi file; le modifiche altrui bloccano l’operazione."],
        ["undo.apply"] = ["Confirmar deshacer", "Confirm undo", "Confirmer le rétablissement", "Conferma ripristino"],
        ["undo.back"] = ["Cancelar", "Cancel", "Annuler", "Annulla"],
        ["undo.empty"] = ["No hay efectos reversibles en esta sesión.", "No reversible effects in this session.", "Aucun effet réversible dans cette session.", "Nessun effetto reversibile in questa sessione."],
        ["undo.confirm_required"] = ["Hace falta confirmar el preview exacto. Usa /undo para revisarlo.", "Confirm the exact preview first. Use /undo to review it.", "Confirmez d’abord l’aperçu exact. Utilisez /undo pour le vérifier.", "Conferma prima l’anteprima esatta. Usa /undo per esaminarla."],
        ["undo.unavailable"] = ["Deshacer no está disponible para esta sesión o efecto.", "Undo is unavailable for this session or effect.", "Le rétablissement est indisponible pour cette session ou cet effet.", "Ripristino non disponibile per questa sessione o effetto."],
        ["undo.busy"] = ["Espera a que termine la actividad antes de deshacer.", "Wait for activity to finish before undoing.", "Attendez la fin de l’activité avant de rétablir.", "Attendi la fine dell’attività prima di ripristinare."],
        ["undo.stale"] = ["El preview cambió o hay ediciones ajenas. Revisa de nuevo; no se sobrescribieron.", "The preview changed or other edits exist. Review again; they were not overwritten.", "L’aperçu a changé ou des modifications tierces existent. Vérifiez à nouveau ; elles n’ont pas été écrasées.", "L’anteprima è cambiata o ci sono altre modifiche. Esamina di nuovo; non sono state sovrascritte."],
        ["undo.denied"] = ["Los permisos actuales no permiten restaurar este efecto.", "Current permissions do not allow restoring this effect.", "Les autorisations actuelles ne permettent pas de restaurer cet effet.", "Le autorizzazioni attuali non consentono di ripristinare questo effetto."],
        ["undo.unknown"] = ["El resultado requiere revisión. Consulta el estado; no se repetirá automáticamente.", "The result needs review. Inspect the state; it will not be retried automatically.", "Le résultat nécessite une vérification. Consultez l’état ; aucun nouvel essai automatique.", "Il risultato richiede verifica. Controlla lo stato; nessun nuovo tentativo automatico."],
        ["undo.done"] = ["Efecto restaurado. El historial original se conserva.", "Effect restored. Original history is preserved.", "Effet restauré. L’historique original est conservé.", "Effetto ripristinato. La cronologia originale viene conservata."],
        ["undo.state.Ready"] = ["Listo", "Ready", "Prêt", "Pronto"],
        ["undo.state.AlreadyRestored"] = ["Ya restaurado", "Already restored", "Déjà restauré", "Già ripristinato"],
        ["undo.state.Conflict"] = ["Conflicto: conservar edición actual", "Conflict: preserve current edit", "Conflit : conserver la modification actuelle", "Conflitto: conserva la modifica attuale"],
        ["undo.state.Unsupported"] = ["Sin evidencia suficiente", "Insufficient evidence", "Preuve insuffisante", "Prove insufficienti"],
        ["undo.operation.restore"] = ["Restaurar versión anterior", "Restore previous version", "Restaurer la version précédente", "Ripristina la versione precedente"],
        ["undo.operation.remove"] = ["Retirar archivo creado por el efecto", "Remove file created by the effect", "Retirer le fichier créé par l’effet", "Rimuovi file creato dall’effetto"],
        ["undo.operation.recreate"] = ["Recuperar archivo eliminado por el efecto", "Recreate file removed by the effect", "Récupérer le fichier supprimé par l’effet", "Ricrea file eliminato dall’effetto"],
    };
    public static IReadOnlyCollection<string> Keys => Values.Keys.ToArray();
    internal static bool ContainsKey(string key) => Values.ContainsKey(key);
    internal static bool TryTranslate(string locale, string key, out string text)
    {
        if (Values.TryGetValue(key, out var variants))
        { text = variants[locale switch { "en" => 1, "fr" => 2, "it" => 3, _ => 0 }]; return true; }
        text = ""; return false;
    }
}
