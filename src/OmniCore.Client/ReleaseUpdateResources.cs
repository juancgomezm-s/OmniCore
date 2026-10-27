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
        ["metadata_pending"] = ["Sólo se comprobaron metadatos. Verificación del archivo y del publicador pendiente; instalar no está disponible.", "Metadata only. Archive and publisher verification are pending; installation is unavailable.", "Métadonnées uniquement. Vérification de l’archive et de l’éditeur en attente ; installation indisponible.", "Solo metadati. Verifica dell’archivio e del publisher in sospeso; installazione non disponibile."],
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
        ["usage"] = ["Uso: omni update check [--channel stable|preview]", "Usage: omni update check [--channel stable|preview]", "Usage : omni update check [--channel stable|preview]", "Uso: omni update check [--channel stable|preview]"],
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
