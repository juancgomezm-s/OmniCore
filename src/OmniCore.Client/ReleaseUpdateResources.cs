namespace OmniCore.Client;

using OmniCore.Protocol;

/// <summary>Localized copy for the explicit, metadata-only release checker.</summary>
public static class ReleaseUpdateResources
{
    private static readonly IReadOnlyDictionary<string, string[]> Values = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["title"] = ["Actualizaciones", "Updates", "Mises à jour", "Aggiornamenti"],
        ["check"] = ["Comprobar ahora", "Check now", "Vérifier maintenant", "Controlla ora"],
        ["channel"] = ["Canal", "Channel", "Canal", "Canale"],
        ["stable"] = ["Estable", "Stable", "Stable", "Stabile"],
        ["preview"] = ["Vista previa", "Preview", "Aperçu", "Anteprima"],
        ["close"] = ["Volver", "Back", "Retour", "Indietro"],
        ["checking"] = ["Comprobando releases…", "Checking releases…", "Vérification des releases…", "Controllo delle release…"],
        ["ready"] = ["Sin consultar. La red sólo se usa al pulsar Comprobar ahora.", "Not checked. Network is used only after selecting Check now.", "Non vérifié. Le réseau est utilisé uniquement après Vérifier maintenant.", "Non verificato. La rete viene usata solo dopo Controlla ora."],
        ["open"] = ["Abrir actualizaciones", "Open updates", "Ouvrir les mises à jour", "Apri aggiornamenti"],
        ["installed"] = ["Instalada", "Installed", "Installée", "Installata"],
        ["available"] = ["Disponible", "Available", "Disponible", "Disponibile"],
        ["tag"] = ["Etiqueta", "Tag", "Tag", "Tag"],
        ["commit"] = ["Commit declarado", "Declared commit", "Commit déclaré", "Commit dichiarato"],
        ["asset"] = ["Paquete", "Package", "Paquet", "Pacchetto"],
        ["size"] = ["Tamaño", "Size", "Taille", "Dimensione"],
        ["runtime"] = ["Runtime", "Runtime", "Runtime", "Runtime"],
        ["platform"] = ["Plataforma", "Platform", "Plateforme", "Piattaforma"],
        ["protocol"] = ["Protocolo", "Protocol", "Protocole", "Protocollo"],
        ["configuration"] = ["Configuración", "Configuration", "Configuration", "Configurazione"],
        ["node"] = ["Node.js", "Node.js", "Node.js", "Node.js"],
        ["sha256"] = ["SHA-256 declarado", "Declared SHA-256", "SHA-256 déclaré", "SHA-256 dichiarato"],
        ["release_url"] = ["Release", "Release", "Release", "Release"],
        ["retry_after"] = ["Reintentar después de", "Retry after", "Réessayer après", "Riprova dopo"],
        ["requirements"] = ["Requisitos declarados", "Declared requirements", "Exigences déclarées", "Requisiti dichiarati"],
        ["compat_unknown"] = ["Compatibilidad desconocida; faltan datos del entorno o requisitos.", "Compatibility unknown; environment or requirement data is missing.", "Compatibilité inconnue ; données d’environnement ou exigences manquantes.", "Compatibilità sconosciuta; mancano dati dell’ambiente o requisiti."],
        ["compat_ok"] = ["Los requisitos declarados coinciden con el entorno observado.", "Declared requirements match the observed environment.", "Les exigences déclarées correspondent à l’environnement observé.", "I requisiti dichiarati corrispondono all’ambiente osservato."],
        ["compat_no"] = ["El paquete no coincide con los requisitos declarados.", "The package does not match declared requirements.", "Le paquet ne correspond pas aux exigences déclarées.", "Il pacchetto non corrisponde ai requisiti dichiarati."],
        ["metadata_pending"] = ["Sólo se comprobaron metadatos. La verificación del publicador sigue pendiente; usa «omni update apply» para descargar y verificar el archivo.", "Metadata only. Publisher verification is still pending; use \"omni update apply\" to download and verify the archive.", "Métadonnées uniquement. La vérification de l’éditeur reste en attente ; utilisez « omni update apply » pour télécharger et vérifier l’archive.", "Solo metadati. La verifica del publisher è ancora in sospeso; usa «omni update apply» per scaricare e verificare l’archivio."],
        ["notes"] = ["Notas de release (contenido remoto no confiable):", "Release notes (untrusted remote content):", "Notes de release (contenu distant non fiable) :", "Note di release (contenuto remoto non attendibile):"],
        ["status.no_release"] = ["No hay releases publicados en este canal.", "No published releases are available on this channel.", "Aucune release publiée sur ce canal.", "Nessuna release pubblicata su questo canale."],
        ["status.repository"] = ["El repositorio oficial no está disponible.", "The official repository is unavailable.", "Le dépôt officiel est indisponible.", "Il repository ufficiale non è disponibile."],
        ["status.rate"] = ["GitHub limitó las consultas. Inténtalo después de la hora indicada.", "GitHub rate limited the request. Try again after the indicated time.", "GitHub a limité la requête. Réessayez après l’heure indiquée.", "GitHub ha limitato la richiesta. Riprova dopo l’orario indicato."],
        ["status.offline"] = ["No se pudo conectar con GitHub.", "Could not connect to GitHub.", "Impossible de se connecter à GitHub.", "Impossibile connettersi a GitHub."],
        ["status.network"] = ["La respuesta de GitHub no se pudo validar.", "The GitHub response could not be validated.", "La réponse GitHub n’a pas pu être validée.", "La risposta GitHub non è stata convalidata."],
        ["status.incomplete"] = ["La lista de releases supera el límite de consulta; no se afirma que esté actualizada.", "The release list exceeded the query limit; current status is unknown.", "La liste des releases dépasse la limite ; l’état actuel est inconnu.", "L’elenco delle release supera il limite; lo stato attuale è sconosciuto."],
        ["status.manifest_missing"] = ["Esta release no contiene el manifiesto requerido.", "This release does not contain the required manifest.", "Cette release ne contient pas le manifeste requis.", "Questa release non contiene il manifest richiesto."],
        ["status.manifest_invalid"] = ["El manifiesto o los metadatos del paquete no coinciden o son inválidos.", "The manifest or package metadata is invalid or inconsistent.", "Le manifeste ou les métadonnées du paquet sont invalides ou incohérents.", "Il manifest o i metadati del pacchetto non sono validi o coerenti."],
        ["status.asset"] = ["No hay un paquete compatible con la plataforma o los requisitos observados.", "No package metadata is compatible with this platform or the observed requirements.", "Aucun paquet n’est compatible avec la plateforme ou les exigences observées.", "Nessun pacchetto è compatibile con la piattaforma o i requisiti osservati."],
        ["status.unknown"] = ["Hay una release candidata, pero no se puede confirmar la compatibilidad.", "A release candidate exists, but compatibility cannot be confirmed.", "Une release candidate existe, mais sa compatibilité ne peut être confirmée.", "Esiste una release candidata, ma la compatibilità non è confermabile."],
        ["status.current"] = ["No se encontró una versión publicada más reciente en este canal.", "No newer published version was found on this channel.", "Aucune version publiée plus récente n’a été trouvée sur ce canal.", "Non è stata trovata una versione pubblicata più recente in questo canale."],
        ["status.dev"] = ["Esta instalación de desarrollo no se compara como una versión publicada.", "This development build is not treated as a published version.", "Cette version de développement n’est pas comparée comme une version publiée.", "Questa build di sviluppo non è confrontata come versione pubblicata."],
        ["status.update"] = ["Hay una versión publicada más reciente.", "A newer published version is available.", "Une version publiée plus récente est disponible.", "È disponibile una versione pubblicata più recente."],
        ["diagnostic"] = ["Diagnóstico", "Diagnostic", "Diagnostic", "Diagnostica"],
        ["usage"] = ["Uso: omni update check|status [--channel stable|preview] · omni update apply [versión] [--channel stable|preview] [--yes] · omni update rollback [--yes]", "Usage: omni update check|status [--channel stable|preview] · omni update apply [version] [--channel stable|preview] [--yes] · omni update rollback [--yes]", "Usage : omni update check|status [--channel stable|preview] · omni update apply [version] [--channel stable|preview] [--yes] · omni update rollback [--yes]", "Uso: omni update check|status [--channel stable|preview] · omni update apply [versione] [--channel stable|preview] [--yes] · omni update rollback [--yes]"],
        ["apply.confirm"] = ["¿Aplicar {version}? Se instalará al cerrar esta sesión. [s/N] ", "Apply {version}? It will be installed after this session exits. [y/N] ", "Appliquer {version} ? L’installation aura lieu après la fermeture de cette session. [o/N] ", "Applicare {version}? Verrà installata alla chiusura di questa sessione. [s/N] "],
        ["apply.prepared"] = ["Actualización {version} preparada. Se aplicará al cerrar omni; consulta con: omni update status", "Update {version} prepared. It will be applied after omni exits; check with: omni update status", "Mise à jour {version} préparée. Elle sera appliquée après la fermeture d’omni ; vérifiez avec : omni update status", "Aggiornamento {version} preparato. Verrà applicato alla chiusura di omni; controlla con: omni update status"],
        ["apply.cancelled"] = ["Actualización cancelada.", "Update cancelled.", "Mise à jour annulée.", "Aggiornamento annullato."],
        ["rollback.confirm"] = ["¿Restaurar la versión anterior? Se aplicará al cerrar esta sesión. [s/N] ", "Restore the previous version? It will be applied after this session exits. [y/N] ", "Restaurer la version précédente ? Elle sera appliquée après la fermeture de cette session. [o/N] ", "Ripristinare la versione precedente? Verrà applicata alla chiusura di questa sessione. [s/N] "],
        ["rollback.prepared"] = ["Rollback a {version} preparado. Se aplicará al cerrar omni; consulta con: omni update status", "Rollback to {version} prepared. It will be applied after omni exits; check with: omni update status", "Retour à {version} préparé. Il sera appliqué après la fermeture d’omni ; vérifiez avec : omni update status", "Rollback a {version} preparato. Verrà applicato alla chiusura di omni; controlla con: omni update status"],
        ["status.none"] = ["Sin operaciones de actualización registradas.", "No update operations recorded.", "Aucune opération de mise à jour enregistrée.", "Nessuna operazione di aggiornamento registrata."],
        ["status.heading"] = ["Última operación de actualización:", "Last update operation:", "Dernière opération de mise à jour :", "Ultima operazione di aggiornamento:"],
        ["status.operation"] = ["Operación", "Operation", "Opération", "Operazione"],
        ["status.state"] = ["Estado", "State", "État", "Stato"],
        ["status.from"] = ["Desde", "From", "Depuis", "Da"],
        ["status.to"] = ["Hacia", "To", "Vers", "A"],
        ["status.updated"] = ["Actualizado", "Updated", "Mis à jour", "Aggiornato"],
        ["status.detail"] = ["Detalle", "Detail", "Détail", "Dettaglio"],
        ["error.confirmation"] = ["Confirmación requerida. Repite con --yes en modo no interactivo.", "Confirmation required. Repeat with --yes in non-interactive mode.", "Confirmation requise. Relancez avec --yes en mode non interactif.", "Conferma richiesta. Ripeti con --yes in modalità non interattiva."],
        ["error.development_checkout"] = ["Esta instalación es un checkout de desarrollo; usa scripts/install.ps1 (o install.sh) o git pull.", "This is a development checkout; use scripts/install.ps1 (or install.sh) or git pull.", "Cette installation est un checkout de développement ; utilisez scripts/install.ps1 (ou install.sh) ou git pull.", "Questa installazione è un checkout di sviluppo; usa scripts/install.ps1 (o install.sh) o git pull."],
        ["error.release_package_unsupported"] = ["La actualización de paquetes de release aún no está disponible.", "Updating release packages is not available yet.", "La mise à jour des paquets de release n’est pas encore disponible.", "L’aggiornamento dei pacchetti di release non è ancora disponibile."],
        ["error.install_kind_unknown"] = ["No se pudo reconocer el tipo de instalación.", "The installation kind could not be identified.", "Le type d’installation n’a pas pu être identifié.", "Impossibile identificare il tipo di installazione."],
        ["error.compatibility_unverified"] = ["La compatibilidad no está verificada; no se aplicará.", "Compatibility is not verified; it will not be applied.", "La compatibilité n’est pas vérifiée ; elle ne sera pas appliquée.", "La compatibilità non è verificata; non verrà applicato."],
        ["error.asset_unavailable"] = ["La release no ofrece un paquete verificable para esta plataforma.", "The release does not offer a verifiable package for this platform.", "La release n’offre pas de paquet vérifiable pour cette plateforme.", "La release non offre un pacchetto verificabile per questa piattaforma."],
        ["error.operation_in_progress"] = ["Hay otra operación de actualización en curso.", "Another update operation is in progress.", "Une autre opération de mise à jour est en cours.", "Un’altra operazione di aggiornamento è in corso."],
        ["error.operation_unknown"] = ["El resultado de una actualización anterior es incierto. No se repetirá automáticamente; revisa la instalación antes de continuar.", "The previous update outcome is uncertain. It will not be retried automatically; inspect the installation before continuing.", "Le résultat de la mise à jour précédente est incertain. Elle ne sera pas relancée automatiquement ; vérifiez l’installation avant de continuer.", "L’esito dell’aggiornamento precedente è incerto. Non verrà ripetuto automaticamente; controlla l’installazione prima di continuare."],
        ["error.prefix_mismatch"] = ["No se pudo verificar el prefijo npm asociado a esta instalación.", "The npm prefix associated with this installation could not be verified.", "Impossible de vérifier le préfixe npm associé à cette installation.", "Impossibile verificare il prefisso npm associato a questa installazione."],
        ["error.download_failed"] = ["La descarga no superó la verificación.", "The download did not pass verification.", "Le téléchargement n’a pas passé la vérification.", "Il download non ha superato la verifica."],
        ["error.backup_failed"] = ["No se pudo respaldar la instalación actual.", "The current installation could not be backed up.", "L’installation actuelle n’a pas pu être sauvegardée.", "Impossibile eseguire il backup dell’installazione corrente."],
        ["error.helper_start_failed"] = ["No se pudo iniciar el aplicador de la actualización.", "The update helper could not be started.", "L’assistant de mise à jour n’a pas pu démarrer.", "Impossibile avviare l’helper di aggiornamento."],
        ["error.helper_missing"] = ["Falta el helper de actualización en la instalación.", "The update helper is missing from this installation.", "L’assistant de mise à jour est absent de cette installation.", "L’helper di aggiornamento manca da questa installazione."],
        ["error.state_invalid"] = ["El estado de la actualización no se pudo leer.", "The update state could not be read.", "L’état de la mise à jour n’a pas pu être lu.", "Impossibile leggere lo stato dell’aggiornamento."],
        ["error.toolchain_unavailable"] = ["Falta npm o Node.js para aplicar la actualización.", "npm or Node.js is missing to apply the update.", "npm ou Node.js est absent pour appliquer la mise à jour.", "npm o Node.js mancano per applicare l’aggiornamento."],
        ["error.rollback_unavailable"] = ["No hay una versión anterior respaldada para restaurar.", "There is no backed-up previous version to restore.", "Aucune version précédente sauvegardée n’est disponible.", "Nessuna versione precedente salvata da ripristinare."],
        ["error.not_supported"] = ["Actualización no soportada en esta instalación.", "Update is not supported for this installation.", "La mise à jour n’est pas prise en charge pour cette installation.", "Aggiornamento non supportato per questa installazione."],
        ["development"] = ["desarrollo", "development", "développement", "sviluppo"],
        ["unknown"] = ["desconocida", "unknown", "inconnue", "sconosciuta"],
    };

    public static IReadOnlyCollection<string> Keys => Values.Keys.Select(key => "updates." + key).ToArray();
    public static bool TryTranslate(string locale, string key, out string text)
    {
        if (key.StartsWith("updates.", StringComparison.Ordinal) && Values.TryGetValue(key[8..], out var translations))
        { text = translations[locale switch { "es" => 0, "fr" => 2, "it" => 3, _ => 1 }]; return true; }
        text = ""; return false;
    }

    public static string StatusKey(ReleaseUpdateStatus status) => "updates." + (status switch
    {
        ReleaseUpdateStatus.NoPublishedRelease => "status.no_release",
        ReleaseUpdateStatus.RepositoryUnavailable => "status.repository",
        ReleaseUpdateStatus.RateLimited => "status.rate",
        ReleaseUpdateStatus.Offline => "status.offline",
        ReleaseUpdateStatus.NetworkError => "status.network",
        ReleaseUpdateStatus.CatalogIncomplete => "status.incomplete",
        ReleaseUpdateStatus.ManifestMissing => "status.manifest_missing",
        ReleaseUpdateStatus.ManifestInvalid => "status.manifest_invalid",
        ReleaseUpdateStatus.NoCompatibleAsset => "status.asset",
        ReleaseUpdateStatus.CompatibilityUnknown => "status.unknown",
        ReleaseUpdateStatus.UpToDate => "status.current",
        ReleaseUpdateStatus.DevelopmentBuild => "status.dev",
        ReleaseUpdateStatus.UpdateAvailable => "status.update",
        _ => "status.unknown",
    });
}
